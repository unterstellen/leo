using Leo.Core.Config;
using Leo.Core.Profiles;
using Leo.Core.SplitTunnel;
using Leo.Ipc;
using Leo.Service;
using Leo.Service.Networking;
using Leo.Service.Profiles;
using Microsoft.Extensions.Logging.Abstractions;

namespace Leo.Tests;

public sealed class ConfigMigrationTests
{
    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "leo-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void V1_Config_Becomes_Default_Profile_Without_Losing_Anything()
    {
        var dir = TempDir();
        var path = Path.Combine(dir, "config.json");
        File.WriteAllText(path, """
            {
              "schemaVersion": 1,
              "adapters": [ { "id": "{W}", "mac": "AA", "nickname": "Home Wi-Fi", "pinnedSsids": [ "Zixel" ] } ],
              "priorityOrder": [ "{W}", "{E}" ],
              "splitTunnel": {
                "enabled": true,
                "appRules": [ { "id": "a1", "displayName": "curl", "kind": 0, "pattern": "C:\\curl.exe", "targetAdapterId": "{E}" } ],
                "destinationRules": [ { "id": "d1", "kind": 0, "value": "example.com", "targetAdapterId": "{E}" } ],
                "networkDns": [ { "adapterId": "{E}", "servers": [ "1.1.1.1" ] } ]
              }
            }
            """);

        var store = new ConfigStore(path);

        store.Read(c =>
        {
            Assert.Equal(ConfigMigration.CurrentSchema, c.SchemaVersion);
            var def = Assert.Single(c.Profiles);
            Assert.True(def.IsDefault);
            Assert.Equal(Profile.DefaultId, c.ProfileState.ActiveProfileId);
            Assert.False(c.ProfileState.Manual);
            // The live settings are untouched: they are the active (Default) profile's.
            Assert.Equal(["{W}", "{E}"], c.PriorityOrder);
            Assert.Equal("a1", Assert.Single(c.SplitTunnel.AppRules).Id);
            Assert.Equal("d1", Assert.Single(c.SplitTunnel.DestinationRules).Id);
            Assert.Equal("1.1.1.1", Assert.Single(Assert.Single(c.SplitTunnel.NetworkDns).Servers));
            Assert.Equal(["Zixel"], c.Adapters[0].PinnedSsids);
            Assert.Equal("Home Wi-Fi", c.Adapters[0].Nickname);
            return 0;
        });
        Assert.True(File.Exists(path + ".v1.bak"), "the original file is kept");
        Assert.Contains("\"schemaVersion\": 1", File.ReadAllText(path + ".v1.bak"));
        Assert.Contains("\"profiles\"", File.ReadAllText(path));

        // Loading again changes nothing further.
        var again = new ConfigStore(path);
        Assert.Equal(1, again.Read(c => c.Profiles.Count));
    }

    [Fact]
    public void New_Config_Has_Default_Profile()
    {
        var store = new ConfigStore(Path.Combine(TempDir(), "config.json"));
        Assert.Equal(Profile.DefaultId, store.Read(c => Assert.Single(c.Profiles).Id));
    }

    [Fact]
    public void Repair_Moves_Default_Last_And_Fixes_A_Missing_Active_Profile()
    {
        var c = new LeoConfig
        {
            SchemaVersion = 2,
            Profiles = [new Profile { Id = Profile.DefaultId, Name = "Default", Triggers = [new ProfileTrigger { Value = "x" }] }, new Profile { Id = "p", Name = "Phone" }, new Profile { Id = "p", Name = "Dup" }],
            ProfileState = new ProfileState { ActiveProfileId = "gone", Manual = true },
        };
        Assert.True(ConfigMigration.Upgrade(c));
        Assert.Equal(["p", Profile.DefaultId], c.Profiles.Select(p => p.Id));
        Assert.Empty(c.Profiles[^1].Triggers);
        Assert.Equal(Profile.DefaultId, c.ProfileState.ActiveProfileId);
        Assert.False(c.ProfileState.Manual);
        Assert.False(ConfigMigration.Upgrade(c));
    }
}

public sealed class ProfileSelectorTests
{
    private static Profile P(string id, params ProfileTrigger[] triggers) => new() { Id = id, Name = id, Triggers = [.. triggers] };
    private static ProfileTrigger Connected(string ssid) => new() { Kind = ProfileTriggerKind.WifiConnected, Value = ssid };
    private static ProfileTrigger Visible(string ssid) => new() { Kind = ProfileTriggerKind.WifiVisible, Value = ssid };
    private static ProfileTrigger Adapter(string id, string? mac = null) => new() { Kind = ProfileTriggerKind.AdapterConnected, Value = id, Mac = mac, Label = "Phone" };

    private static NetworkConditions Cond(string[]? connected = null, string[]? visible = null, params ConnectedAdapter[] adapters) =>
        new((connected ?? []).ToHashSet(), (visible ?? []).ToHashSet(), adapters);

    private static readonly List<Profile> Profiles =
    [
        P("phone", Adapter("{TETHER}", "02-00-00-00-00-01")),
        P("home", Connected("Zixel")),
        P("office", Visible("OfficeNet")),
        P(Profile.DefaultId),
    ];

    [Fact]
    public void Default_When_Nothing_Matches() =>
        Assert.Equal(Profile.DefaultId, ProfileSelector.Choose(Profiles, Cond()).ProfileId);

    [Fact]
    public void Higher_Profile_Wins_When_Several_Match()
    {
        var both = Cond(["Zixel"], null, new ConnectedAdapter("{TETHER}", null, "Ethernet"));
        var choice = ProfileSelector.Choose(Profiles, both);
        Assert.Equal("phone", choice.ProfileId);
        Assert.Equal("Ethernet is connected", choice.Reason);
        Assert.Equal("home", ProfileSelector.Choose(Profiles, Cond(["Zixel"])).ProfileId);
    }

    [Fact]
    public void Adapter_Trigger_Falls_Back_To_Mac_After_A_Replug()
    {
        var replugged = Cond(null, null, new ConnectedAdapter("{NEW-GUID}", "02-00-00-00-00-01", "Ethernet 2"));
        Assert.Equal("phone", ProfileSelector.Choose(Profiles, replugged).ProfileId);
    }

    [Fact]
    public void Visible_Trigger_Also_Matches_When_Connected()
    {
        Assert.Equal("office", ProfileSelector.Choose(Profiles, Cond(null, ["OfficeNet"])).ProfileId);
        Assert.Equal("office", ProfileSelector.Choose(Profiles, Cond(["OfficeNet"])).ProfileId);
    }

    [Fact]
    public void Ssids_Are_Case_Sensitive()
    {
        Assert.Equal(Profile.DefaultId, ProfileSelector.Choose(Profiles, Cond(["zixel"])).ProfileId);
    }
}

public sealed class ProfileSwitcherTests
{
    private readonly ManualClock _time = new();
    private readonly ProfileSwitcher _switcher;
    private readonly ProfileState _state = new();

    private static readonly List<Profile> Profiles =
    [
        new() { Id = "phone", Name = "Phone", Triggers = [new() { Kind = ProfileTriggerKind.AdapterConnected, Value = "{T}" }] },
        new() { Id = "home", Name = "Home", Triggers = [new() { Kind = ProfileTriggerKind.WifiConnected, Value = "Zixel" }] },
        new() { Id = Profile.DefaultId, Name = "Default" },
    ];

    private static readonly NetworkConditions Nothing = NetworkConditions.None;
    private static readonly NetworkConditions Tether = new(new HashSet<string>(), new HashSet<string>(), [new ConnectedAdapter("{T}", null, "Ethernet")]);
    private static readonly NetworkConditions Home = new(new HashSet<string> { "Zixel" }, new HashSet<string>(), []);

    public ProfileSwitcherTests() => _switcher = new ProfileSwitcher(_time);

    /// <summary>Evaluates every second for <paramref name="seconds"/>; applies switches like the manager does.</summary>
    private List<string> Run(NetworkConditions now, int seconds)
    {
        var switches = new List<string>();
        for (var i = 0; i <= seconds; i++)
        {
            var d = _switcher.Evaluate(Profiles, now, _state);
            if (d.SwitchTo is { } to)
            {
                switches.Add(to);
                _state.ActiveProfileId = to;
                _state.Manual = false;
            }
            if (i < seconds) _time.Advance(1);
        }
        return switches;
    }

    [Fact]
    public void Switches_In_After_Five_Seconds()
    {
        Assert.Empty(Run(Tether, 4));
        Assert.Equal(["phone"], Run(Tether, 1));
    }

    [Fact]
    public void A_Short_Blip_Does_Not_Switch()
    {
        Assert.Empty(Run(Tether, 3));
        Assert.Empty(Run(Nothing, 10));
        Assert.Equal(Profile.DefaultId, _state.ActiveProfileId);
    }

    [Fact]
    public void Leaving_Waits_Twenty_Seconds_And_A_Replug_Keeps_The_Profile()
    {
        Run(Tether, 5);
        Assert.Equal("phone", _state.ActiveProfileId);
        _time.Advance(60); // past the minimum dwell

        Assert.Empty(Run(Nothing, 15));           // unplugged for 15 s…
        Assert.Empty(Run(Tether, 5));             // …plugged back in: stays
        Assert.Equal("phone", _state.ActiveProfileId);

        Assert.Empty(Run(Nothing, 19));
        Assert.Equal([Profile.DefaultId], Run(Nothing, 1));
    }

    [Fact]
    public void Leaving_For_Another_Matching_Profile_Also_Uses_The_Longer_Delay()
    {
        Run(Tether, 5);
        _time.Advance(60);
        Assert.Empty(Run(Home, 19));
        Assert.Equal(["home"], Run(Home, 1));
    }

    [Fact]
    public void Minimum_Time_Between_Automatic_Switches()
    {
        Run(Tether, 5); // switched at t=5
        // A higher-priority… none here; go Home at once: 20 s leave delay, but dwell is 30 s after the switch.
        var switches = Run(Home, 24);
        Assert.Empty(switches);
        Assert.Equal(["home"], Run(Home, 6)); // 30 s after the last switch
    }

    [Fact]
    public void Manual_Choice_Holds_Until_The_Situation_Changes()
    {
        Run(Home, 5);
        Assert.Equal("home", _state.ActiveProfileId);

        // User picks Default by hand while Home still matches.
        _switcher.NoteManual(ProfileSelector.Choose(Profiles, Home));
        _state.ActiveProfileId = Profile.DefaultId;
        _state.Manual = true;
        Assert.Empty(Run(Home, 120));
        Assert.Equal(Profile.DefaultId, _state.ActiveProfileId);

        // Plugging the tether in is a new situation: automatic switching takes over again.
        _time.Advance(60);
        Assert.Equal(["phone"], Run(Tether, 5));
        Assert.False(_state.Manual);
    }

    [Fact]
    public void Manual_Choice_Survives_A_Restart()
    {
        _state.ActiveProfileId = "phone";
        _state.Manual = true; // restored from config; nothing about the tether now
        Assert.Empty(Run(Home, 60)); // the first evaluation's situation is the baseline
        Assert.Equal("phone", _state.ActiveProfileId);
    }

    [Fact]
    public void Automatic_Switching_Off_Never_Switches()
    {
        _state.AutoSwitch = false;
        Assert.Empty(Run(Tether, 60));
    }

    [Fact]
    public void Flapping_Pauses_Automatic_Switching()
    {
        var timing = new ProfileSwitchTiming { MinDwell = TimeSpan.Zero, SwitchOut = TimeSpan.FromSeconds(5) };
        var switcher = new ProfileSwitcher(_time, timing);
        var state = new ProfileState();
        var switches = 0;
        var paused = false;
        for (var round = 0; round < 6 && !paused; round++)
        {
            var cond = round % 2 == 0 ? Tether : Nothing;
            for (var i = 0; i < 7; i++)
            {
                var d = switcher.Evaluate(Profiles, cond, state);
                if (d.JustPaused) { paused = true; break; }
                if (d.SwitchTo is { } to) { switches++; state.ActiveProfileId = to; }
                _time.Advance(1);
            }
        }
        Assert.True(paused);
        Assert.Equal(4, switches);
        Assert.True(switcher.Paused);
        switcher.Resume();
        Assert.False(switcher.Paused);
    }
}

public sealed class ProfileOpsTests
{
    private static LeoConfig Live()
    {
        var c = new LeoConfig
        {
            PriorityOrder = ["{W}", "{E}"],
            Adapters =
            [
                new AdapterSettings { Id = "{W}", Mac = "W-MAC", Nickname = "WiFi", PinnedSsids = ["Zixel"], DualLink = false },
                new AdapterSettings { Id = "{E}", Mac = "E-MAC", Nickname = "Phone" },
            ],
        };
        c.SplitTunnel.AppRules.Add(new AppRule { Id = "a1", Pattern = @"C:\x.exe", TargetAdapterId = "{E}" });
        c.SplitTunnel.DestinationRules.Add(new DestinationRule { Id = "d1", Value = "example.com", TargetAdapterId = "{E}" });
        ConfigMigration.Upgrade(c);
        return c;
    }

    [Fact]
    public void Switch_Stores_The_Outgoing_Profile_And_Applies_The_Target()
    {
        var c = Live();
        c.Profiles.Insert(0, new Profile
        {
            Id = "travel", Name = "Travel",
            Settings = new ProfileSettings
            {
                PriorityOrder = ["{E}", "{W}"],
                WifiPins = [new ProfilePins { AdapterId = "{OLD-GUID}", Mac = "W-MAC", Ssids = ["Hotel"] }],
            },
        });

        Assert.True(ProfileOps.Switch(c, "travel", manual: true));

        Assert.Equal("travel", c.ProfileState.ActiveProfileId);
        Assert.True(c.ProfileState.Manual);
        Assert.Equal(["{E}", "{W}"], c.PriorityOrder);
        Assert.Empty(c.SplitTunnel.AppRules);
        Assert.Empty(c.SplitTunnel.DestinationRules);
        Assert.Equal(["Hotel"], c.Adapters.Single(a => a.Id == "{W}").PinnedSsids); // matched by MAC
        Assert.Empty(c.Adapters.Single(a => a.Id == "{E}").PinnedSsids);
        // Machine-wide settings stay.
        Assert.Equal("WiFi", c.Adapters.Single(a => a.Id == "{W}").Nickname);
        Assert.False(c.Adapters.Single(a => a.Id == "{W}").DualLink);
        Assert.Equal(2, c.Adapters.Count);

        // Default kept what was live.
        var def = c.Profiles.Single(p => p.IsDefault).Settings;
        Assert.Equal(["{W}", "{E}"], def.PriorityOrder);
        Assert.Equal("a1", Assert.Single(def.AppRules).Id);
        Assert.Equal(["Zixel"], Assert.Single(def.WifiPins).Ssids);

        // And switching back restores it exactly.
        Assert.True(ProfileOps.Switch(c, Profile.DefaultId, manual: false));
        Assert.Equal(["{W}", "{E}"], c.PriorityOrder);
        Assert.Equal("a1", Assert.Single(c.SplitTunnel.AppRules).Id);
        Assert.Equal("d1", Assert.Single(c.SplitTunnel.DestinationRules).Id);
        Assert.Equal(["Zixel"], c.Adapters.Single(a => a.Id == "{W}").PinnedSsids);
        Assert.Equal(["Hotel"], Assert.Single(c.Profiles.Single(p => p.Id == "travel").Settings.WifiPins).Ssids);
    }

    [Fact]
    public void Edits_While_Active_Belong_To_That_Profile()
    {
        var c = Live();
        c.Profiles.Insert(0, new Profile { Id = "p", Name = "P", Settings = ProfileOps.Capture(c) });
        ProfileOps.Switch(c, "p", manual: true);
        c.SplitTunnel.AppRules.Add(new AppRule { Id = "a2", Pattern = @"C:\y.exe", TargetAdapterId = "{W}" }); // edited on the Apps page
        ProfileOps.Switch(c, Profile.DefaultId, manual: false);
        Assert.Equal(["a1"], c.SplitTunnel.AppRules.Select(r => r.Id));
        ProfileOps.Switch(c, "p", manual: true);
        Assert.Equal(["a1", "a2"], c.SplitTunnel.AppRules.Select(r => r.Id));
    }

    [Fact]
    public void Profiles_Do_Not_Share_Rule_Objects()
    {
        var c = Live();
        var copy = ProfileOps.Capture(c);
        copy.AppRules[0].TargetAdapterId = "{W}";
        Assert.Equal("{E}", c.SplitTunnel.AppRules[0].TargetAdapterId);
    }

    [Fact]
    public void Forgotten_Networks_Leave_Every_Profile()
    {
        var c = Live();
        c.Profiles.Insert(0, new Profile
        {
            Id = "p", Name = "P",
            Settings = new ProfileSettings { WifiPins = [new ProfilePins { AdapterId = "{W}", Ssids = ["Zixel", "Other"] }, new ProfilePins { AdapterId = "{X}", Ssids = ["Zixel"] }] },
        });
        Assert.True(ProfileOps.RemoveForgottenSsids(c, new HashSet<string> { "Zixel" }));
        Assert.Empty(c.Adapters.Single(a => a.Id == "{W}").PinnedSsids);
        Assert.Equal(["Other"], Assert.Single(c.Profiles[0].Settings.WifiPins).Ssids);
        Assert.False(ProfileOps.RemoveForgottenSsids(c, new HashSet<string> { "Zixel" }));
    }

    [Fact]
    public void Switching_To_The_Active_Profile_Only_Changes_The_Manual_Flag()
    {
        var c = Live();
        Assert.False(ProfileOps.Switch(c, Profile.DefaultId, manual: false));
        Assert.True(ProfileOps.Switch(c, Profile.DefaultId, manual: true));
        Assert.False(ProfileOps.Switch(c, "missing", manual: true));
    }
}

public sealed class ProfileManagerTests
{
    private static (ProfileManager Manager, ConfigStore Config) Create()
    {
        var dir = Path.Combine(Path.GetTempPath(), "leo-tests-" + Guid.NewGuid().ToString("N"));
        var config = new ConfigStore(Path.Combine(dir, "config.json"));
        config.Update(c =>
        {
            c.PriorityOrder = ["{W}"];
            c.SplitTunnel.AppRules.Add(new AppRule { Id = "a1", Pattern = @"C:\x.exe", TargetAdapterId = "{E}" });
            return true;
        });
        var hub = new NotificationHub(NullLogger<NotificationHub>.Instance);
        var networks = new NetworkManager(new NetworkInventory(NullLogger<NetworkInventory>.Instance), config, hub,
            NullLogger<NetworkManager>.Instance) { ProbesEnabled = false };
        return (new ProfileManager(config, networks, hub, NullLogger<ProfileManager>.Instance), config);
    }

    [Fact]
    public async Task Create_Activate_Delete_Roundtrip()
    {
        var (m, config) = Create();

        var id = await m.SaveAsync(new ProfileEdit(null, "Gaming", [new ProfileTrigger { Kind = ProfileTriggerKind.WifiConnected, Value = "Zixel" }], StartEmpty: true), default);
        Assert.Equal(["Gaming", "Default"], config.Read(c => c.Profiles.Select(p => p.Name).ToList()));

        await m.ActivateAsync(id, default);
        Assert.Equal(id, config.Read(c => c.ProfileState.ActiveProfileId));
        Assert.True(config.Read(c => c.ProfileState.Manual));
        Assert.Empty(config.Read(c => c.SplitTunnel.AppRules)); // started empty
        Assert.Equal(["{W}"], config.Read(c => c.PriorityOrder)); // but kept the priority list
        Assert.Equal("Gaming", m.Info.ActiveProfileName);
        Assert.Contains("Chosen by you", m.Info.Reason);

        await m.DeleteAsync(id, default);
        Assert.Equal(Profile.DefaultId, config.Read(c => c.ProfileState.ActiveProfileId));
        Assert.Equal("a1", config.Read(c => Assert.Single(c.SplitTunnel.AppRules).Id)); // Default's rules are back
    }

    [Fact]
    public async Task Validation()
    {
        var (m, _) = Create();
        await Assert.ThrowsAsync<ArgumentException>(() => m.SaveAsync(new ProfileEdit(null, " ", []), default));
        await Assert.ThrowsAsync<ArgumentException>(() => m.SaveAsync(new ProfileEdit(null, "default", []), default));
        await Assert.ThrowsAsync<ArgumentException>(() => m.SaveAsync(new ProfileEdit(null, "X",
            [new ProfileTrigger { Kind = ProfileTriggerKind.WifiConnected, Value = new string('a', 33) }]), default));
        await Assert.ThrowsAsync<ArgumentException>(() => m.DeleteAsync(Profile.DefaultId, default));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => m.ActivateAsync("nope", default));
    }

    [Fact]
    public async Task Default_Stays_Last_When_Moving()
    {
        var (m, config) = Create();
        var a = await m.SaveAsync(new ProfileEdit(null, "A", []), default);
        var b = await m.SaveAsync(new ProfileEdit(null, "B", []), default);
        await m.MoveAsync(b, up: true, default);
        Assert.Equal([b, a, Profile.DefaultId], config.Read(c => c.Profiles.Select(p => p.Id).ToList()));
        await m.MoveAsync(a, up: false, default); // would pass Default: ignored
        Assert.Equal([b, a, Profile.DefaultId], config.Read(c => c.Profiles.Select(p => p.Id).ToList()));
    }
}
