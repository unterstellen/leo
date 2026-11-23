using System.Text.Json;
using Leo.Core.Config;

namespace Leo.Core.Profiles;

/// <summary>
/// Pure config operations for profiles (LEO.md §19): capture the live settings into a profile, put a profile's
/// settings live, switch. Called inside <see cref="ConfigStore.Update"/>, so a switch is one atomic save.
/// </summary>
public static class ProfileOps
{
    private static readonly JsonSerializerOptions CloneJson = new();

    public static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, CloneJson), CloneJson)!;

    public static Profile? Active(LeoConfig c) => c.Profiles.FirstOrDefault(p => p.Id == c.ProfileState.ActiveProfileId);

    /// <summary>A copy of the live (active profile's) settings.</summary>
    public static ProfileSettings Capture(LeoConfig c) => new()
    {
        PriorityOrder = [.. c.PriorityOrder],
        AppRules = Clone(c.SplitTunnel.AppRules),
        DestinationRules = Clone(c.SplitTunnel.DestinationRules),
        NetworkDns = Clone(c.SplitTunnel.NetworkDns),
        WifiPins = c.Adapters.Where(a => a.PinnedSsids.Count > 0)
            .Select(a => new ProfilePins { AdapterId = a.Id, Mac = a.Mac, Ssids = [.. a.PinnedSsids] }).ToList(),
    };

    /// <summary>
    /// Makes <paramref name="s"/> the live settings. Pins are set exactly as the profile says (adapters it doesn't
    /// mention get no pins); Wi-Fi profiles themselves are never touched, so nothing is forgotten or restored.
    /// </summary>
    public static void ApplyLive(LeoConfig c, ProfileSettings s)
    {
        c.PriorityOrder = [.. s.PriorityOrder];
        c.SplitTunnel.AppRules = Clone(s.AppRules);
        c.SplitTunnel.DestinationRules = Clone(s.DestinationRules);
        c.SplitTunnel.NetworkDns = Clone(s.NetworkDns);

        var unused = s.WifiPins.Where(p => p.Ssids.Count > 0).ToList();
        foreach (var a in c.Adapters)
        {
            var pin = unused.FirstOrDefault(p => p.AdapterId.Equals(a.Id, StringComparison.OrdinalIgnoreCase))
                      ?? unused.FirstOrDefault(p => p.Mac is not null && a.Mac is not null && p.Mac.Equals(a.Mac, StringComparison.OrdinalIgnoreCase));
            if (pin is not null) unused.Remove(pin);
            a.PinnedSsids = pin is null ? [] : [.. pin.Ssids];
        }
        foreach (var pin in unused)
            c.Adapters.Add(new AdapterSettings { Id = pin.AdapterId, Mac = pin.Mac, PinnedSsids = [.. pin.Ssids] });
    }

    /// <summary>Stores the live settings in the active profile and puts <paramref name="targetId"/> live. False if already active / unknown.</summary>
    public static bool Switch(LeoConfig c, string targetId, bool manual)
    {
        var target = c.Profiles.FirstOrDefault(p => p.Id == targetId);
        if (target is null) return false;
        if (c.ProfileState.ActiveProfileId == targetId)
        {
            if (c.ProfileState.Manual == manual) return false;
            c.ProfileState.Manual = manual;
            return true;
        }
        if (Active(c) is { } outgoing) outgoing.Settings = Capture(c);
        ApplyLive(c, target.Settings);
        c.ProfileState.ActiveProfileId = target.Id;
        c.ProfileState.Manual = manual;
        return true;
    }

    /// <summary>The settings of any profile: live for the active one, stored for the rest.</summary>
    public static ProfileSettings SettingsOf(LeoConfig c, Profile p) =>
        p.Id == c.ProfileState.ActiveProfileId ? Capture(c) : p.Settings;

    /// <summary>The user forgot these networks in Windows: no profile may keep an adapter on them (LEO.md §5.5).</summary>
    public static bool RemoveForgottenSsids(LeoConfig c, IReadOnlySet<string> forgotten)
    {
        var changed = false;
        foreach (var a in c.Adapters) changed |= a.PinnedSsids.RemoveAll(forgotten.Contains) > 0;
        foreach (var p in c.Profiles)
        {
            foreach (var pin in p.Settings.WifiPins) changed |= pin.Ssids.RemoveAll(forgotten.Contains) > 0;
            changed |= p.Settings.WifiPins.RemoveAll(pin => pin.Ssids.Count == 0) > 0;
        }
        return changed;
    }

    /// <summary>"3 app rules · 1 destination · 1 Wi-Fi pin".</summary>
    public static string Summary(ProfileSettings s)
    {
        var parts = new List<string>
        {
            Count(s.AppRules.Count, "app rule"),
            Count(s.DestinationRules.Count, "destination rule"),
        };
        if (s.NetworkDns.Count > 0) parts.Add($"custom DNS on {Count(s.NetworkDns.Count, "network")}");
        var pins = s.WifiPins.Sum(p => p.Ssids.Count);
        if (pins > 0) parts.Add(Count(pins, "Wi-Fi pin"));
        return string.Join(" · ", parts);
    }

    private static string Count(int n, string what) => n == 1 ? $"1 {what}" : $"{n} {what}s";
}
