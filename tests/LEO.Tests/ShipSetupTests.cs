using Leo.Core;
using Leo.Core.Config;
using Leo.Core.Profiles;
using Leo.Core.Setup;
using Leo.Service.Setup;

namespace Leo.Tests;

/// <summary>M8 install/uninstall rules (LEO.md §24.2, §24.5): cleanup plan, login start, dev-install removal.</summary>
public sealed class ShipSetupTests
{
    private const string Wifi = "{11111111-1111-1111-1111-111111111111}";
    private const string Eth = "{22222222-2222-2222-2222-222222222222}";
    private const string Tether = "{33333333-3333-3333-3333-333333333333}";
    private const string Other = "{44444444-4444-4444-4444-444444444444}";
    private const string MachineData = @"C:\ProgramData\UNTERSTELLEN\LEO";

    private static LeoConfig Config()
    {
        var c = new LeoConfig { PriorityOrder = [Wifi, Eth] };
        c.Profiles.Add(new Profile { Id = "phone", Name = "Phone", Settings = new ProfileSettings { PriorityOrder = [Tether, Wifi] } });
        c.Adapters.Add(new AdapterSettings { Id = Wifi, DualLink = true });
        c.Adapters.Add(new AdapterSettings { Id = Eth, DualLink = false });
        c.Adapters.Add(new AdapterSettings { Id = Tether });
        return c;
    }

    private static readonly IReadOnlyList<CleanupAdapter> Adapters =
    [
        new(Wifi, 5, "WiFi", AutomaticMetric: false, ["192.168.1.1"]),
        new(Eth, 6, "Ethernet", AutomaticMetric: true, ["198.18.0.2", "1.1.1.1"]),
        new(Tether, 7, "Ethernet 2", AutomaticMetric: false, []),
        new(Other, 8, "Someone's adapter", AutomaticMetric: false, []), // manual metric LEO never managed
        new("{55555555-5555-5555-5555-555555555555}", 9, "LEO", AutomaticMetric: false, ["198.18.0.2"]), // engine adapter
    ];

    private static readonly string[] Profiles = [@"C:\Users\alice", @"C:\Users\bob", @"C:\Users\ALICE"];

    [Fact]
    public void Full_Uninstall_Restores_Only_What_LEO_Managed()
    {
        var plan = CleanupPlan.Build(Config(), Adapters, engineAdapterPresent: true, MachineData, Profiles, upgrading: false);

        // Metrics: managed (main list or any profile's list) and still manual; never another adapter, never the engine's.
        Assert.Equal([5, 7], plan.RestoreAutomaticMetric.Select(a => a.InterfaceIndex));
        // DNS: only adapters still pointing at the engine resolver; the engine adapter goes away with the device.
        Assert.Equal(["Ethernet"], plan.RemoveEngineDns.Select(a => a.Name));
        // Dual link: only where LEO turned it on.
        Assert.Equal([Wifi], plan.DualLinkOff);
        Assert.True(plan.RemoveFirewallGroup);
        Assert.True(plan.RemoveEngineAdapter);
    }

    [Fact]
    public void Full_Uninstall_Deletes_All_Settings_For_Every_User()
    {
        var plan = CleanupPlan.Build(Config(), Adapters, false, MachineData, Profiles, upgrading: false);
        Assert.Equal(
            [MachineData, @"C:\Users\alice\AppData\Local\UNTERSTELLEN\LEO", @"C:\Users\bob\AppData\Local\UNTERSTELLEN\LEO"],
            plan.DeleteFolders);
        Assert.Contains(@"C:\ProgramData\UNTERSTELLEN", plan.DeleteIfEmpty);
        Assert.Contains(@"C:\Users\bob\AppData\Local\UNTERSTELLEN", plan.DeleteIfEmpty);
        Assert.False(plan.RemoveEngineAdapter);
    }

    [Fact]
    public void Upgrade_Touches_Nothing()
    {
        var plan = CleanupPlan.Build(Config(), Adapters, true, MachineData, Profiles, upgrading: true);
        Assert.Empty(plan.DeleteFolders);
        Assert.Empty(plan.DeleteIfEmpty);
        Assert.Empty(plan.RestoreAutomaticMetric);
        Assert.Empty(plan.RemoveEngineDns);
        Assert.Empty(plan.DualLinkOff);
        Assert.False(plan.RemoveFirewallGroup);
        Assert.False(plan.RemoveEngineAdapter);
    }

    [Fact]
    public void Missing_Config_Still_Cleans_Generic_Things_And_Data()
    {
        var plan = CleanupPlan.Build(null, Adapters, false, MachineData, [], upgrading: false);
        Assert.Empty(plan.RestoreAutomaticMetric); // without config LEO can't know which metrics were its own
        Assert.Equal(["Ethernet"], plan.RemoveEngineDns.Select(a => a.Name));
        Assert.True(plan.RemoveFirewallGroup);
        Assert.Equal([MachineData], plan.DeleteFolders);
    }

    // ── Start at login (§24.2, user's decision Q4) ───────────────────────────

    [Fact]
    public void Machine_Entry_Makes_The_Switch_Per_User()
    {
        var install = LeoPaths.InstallDir;
        Assert.True(LoginStartRule.IsInstalledCopy(Path.Combine(install, "LEO.exe"), install));
        Assert.False(LoginStartRule.IsInstalledCopy(Path.Combine(install, "Service", "LEO.exe"), install));
        Assert.False(LoginStartRule.IsInstalledCopy(@"C:\src\leo\bin\Debug\LEO.exe", install));

        var machine = LoginStartRule.UsesMachineEntry(installedCopy: true, machineEntryExists: true);
        Assert.True(machine);
        Assert.False(LoginStartRule.UsesMachineEntry(true, false)); // START_AT_LOGIN=0: per-user HKCU as before
        Assert.False(LoginStartRule.UsesMachineEntry(false, true)); // a dev build ignores the machine entry

        Assert.True(LoginStartRule.IsOn(machine, userChoice: null, userEntryPointsHere: false));
        Assert.False(LoginStartRule.IsOn(machine, userChoice: false, userEntryPointsHere: true));
        Assert.True(LoginStartRule.IsOn(false, userChoice: false, userEntryPointsHere: true)); // HKCU decides

        Assert.True(LoginStartRule.ExitAtTrayStart(machine, userChoice: false, trayStart: true));
        Assert.False(LoginStartRule.ExitAtTrayStart(machine, userChoice: false, trayStart: false)); // Start menu still opens
        Assert.False(LoginStartRule.ExitAtTrayStart(machine, userChoice: null, trayStart: true));
        Assert.False(LoginStartRule.ExitAtTrayStart(false, userChoice: false, trayStart: true));

        Assert.True(LoginStartRule.RemoveUserEntry(machine, userEntryExists: true)); // the old dev HKCU value
        Assert.False(LoginStartRule.RemoveUserEntry(false, userEntryExists: true));
    }

    // ── remove-dev-install (§24.2) ───────────────────────────────────────────

    [Theory]
    [InlineData(@"""C:\Program Files\UNTERSTELLEN\LEO\Service\LEO.Service.exe""", true)]  // dev-service.ps1 deploy
    [InlineData(@"""C:\src\leo\bin\Debug\LEO.Service.exe""", true)]                       // registered from a build folder
    [InlineData(@"C:\src\leo\bin\LEO.Service.exe --flag", true)]
    [InlineData(@"""C:\Program Files\UNTERSTELLEN\LEO\LEO.Service.exe""", false)]        // the MSI's own
    public void Dev_Service_Is_Recognised(string imagePath, bool remove) =>
        Assert.Equal(remove, DevInstallRemover.ShouldRemove(imagePath, @"C:\Program Files\UNTERSTELLEN\LEO"));
}
