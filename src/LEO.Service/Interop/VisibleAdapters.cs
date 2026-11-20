using System.Management;

namespace Leo.Service.Interop;

/// <summary>
/// The adapters Windows itself shows (MSFT_NetAdapter without hidden ones) — the same set as Get-NetAdapter.
/// Needed because hidden virtual ports (e.g. extra WiFiCx "WiFi 3/4" instances) have no class-registry entry
/// carrying NCF_HIDDEN. Cached briefly; refreshed early when an unknown adapter shows up.
/// </summary>
internal sealed class VisibleAdapters
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(60);
    private readonly Lock _gate = new();
    private HashSet<string>? _ids;
    private DateTime _fetchedAt;

    /// <summary>Returns null if WMI is unavailable (callers then fall back to registry flags only).</summary>
    public HashSet<string>? Get(IEnumerable<string> currentIds)
    {
        lock (_gate)
        {
            var stale = _ids is null || DateTime.UtcNow - _fetchedAt > Ttl;
            var unknown = !stale && currentIds.Any(id => !_ids!.Contains(id) && !_ignoredUnknown.Contains(id));
            if (stale || unknown)
            {
                _ids = Query() ?? _ids;
                _fetchedAt = DateTime.UtcNow;
                // Ids still unknown after a fresh query are genuinely hidden; don't re-query for them.
                _ignoredUnknown = _ids is null ? [] : currentIds.Where(id => !_ids.Contains(id)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            }
            return _ids;
        }
    }

    private HashSet<string> _ignoredUnknown = new(StringComparer.OrdinalIgnoreCase);

    private static HashSet<string>? Query()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\StandardCimv2",
                "SELECT InterfaceGuid FROM MSFT_NetAdapter");
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var obj in searcher.Get())
            {
                using (obj)
                {
                    if (obj["InterfaceGuid"] is string guid) set.Add(guid);
                }
            }
            return set;
        }
        catch
        {
            return null;
        }
    }
}
