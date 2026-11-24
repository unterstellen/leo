using Leo.Core.Config;

namespace Leo.Core.Setup;

/// <summary>An adapter as Windows shows it right now, for the uninstall cleanup (LEO.md §24.5).</summary>
public sealed record CleanupAdapter(string Id, int InterfaceIndex, string Name, bool AutomaticMetric, IReadOnlyList<string> DnsServers);

/// <summary>
/// What a full uninstall puts back and deletes (LEO.md §24.5). Pure, so the rules are unit-tested; the service's
/// <c>cleanup</c> verb carries it out. Nothing LEO didn't create is touched.
/// </summary>
public sealed record CleanupPlan(
    IReadOnlyList<CleanupAdapter> RestoreAutomaticMetric,
    IReadOnlyList<CleanupAdapter> RemoveEngineDns,
    IReadOnlyList<string> DualLinkOff,
    bool RemoveFirewallGroup,
    bool RemoveEngineAdapter,
    IReadOnlyList<string> DeleteFolders,
    IReadOnlyList<string> DeleteIfEmpty)
{
    /// <summary>The engine's DNS address on its virtual adapter (fake range, LEO.md §18).</summary>
    public const string EngineDns = "198.18.0.2";

    /// <summary>The engine's virtual adapter (mihomo's embedded Wintun, LEO.md §17).</summary>
    public const string EngineAdapterName = "LEO";

    public static CleanupPlan Nothing { get; } = new([], [], [], false, false, [], []);

    /// <param name="config">LEO's config, or null when it's missing or unreadable (then only the generic steps run).</param>
    /// <param name="adapters">Adapters Windows has now.</param>
    /// <param name="engineAdapterPresent">The "LEO" adapter is still there (an engine crash left it).</param>
    /// <param name="machineData">%ProgramData%\UNTERSTELLEN\LEO.</param>
    /// <param name="userProfiles">Every user profile folder (C:\Users\…), from the profile list in the registry.</param>
    /// <param name="upgrading">An upgrade removes the old version: nothing is put back and nothing is deleted.</param>
    public static CleanupPlan Build(LeoConfig? config, IReadOnlyList<CleanupAdapter> adapters, bool engineAdapterPresent,
        string machineData, IReadOnlyList<string> userProfiles, bool upgrading)
    {
        if (upgrading) return Nothing;

        var managed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (config is not null)
        {
            foreach (var id in config.PriorityOrder) managed.Add(id);
            foreach (var p in config.Profiles) foreach (var id in p.Settings?.PriorityOrder ?? []) managed.Add(id);
        }

        var restore = adapters
            .Where(a => !a.AutomaticMetric && managed.Contains(a.Id) && !IsEngineAdapter(a))
            .ToList();
        var dns = adapters
            .Where(a => !IsEngineAdapter(a) && a.DnsServers.Any(s => s.Trim() == EngineDns))
            .ToList();
        var dualLink = config?.Adapters.Where(a => a.DualLink == true).Select(a => a.Id).ToList() ?? [];

        var folders = new List<string> { machineData };
        foreach (var profile in userProfiles.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            folders.Add(Path.Combine(profile, "AppData", "Local", LeoInfo.Publisher, LeoInfo.ProductName));
        }

        var ifEmpty = new List<string> { Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(machineData))! };
        ifEmpty.AddRange(folders.Skip(1).Select(f => Path.GetDirectoryName(f)!));

        return new CleanupPlan(restore, dns, dualLink, RemoveFirewallGroup: true, engineAdapterPresent, folders, ifEmpty);
    }

    private static bool IsEngineAdapter(CleanupAdapter a) => a.Name.Equals(EngineAdapterName, StringComparison.OrdinalIgnoreCase);
}
