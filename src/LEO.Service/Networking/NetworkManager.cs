using System.Net.NetworkInformation;
using System.Net.Sockets;
using Leo.Core.Config;
using Leo.Core.Networking;
using Leo.Ipc;
using Leo.Service.Interop;
using WinRtNetworkInformation = Windows.Networking.Connectivity.NetworkInformation;

namespace Leo.Service.Networking;

/// <summary>
/// Owns the live network picture (LEO.md §5.1, §6.1–6.3): scans adapters, applies the user's priority as
/// interface metrics, keeps nicknames, and publishes <see cref="NetworkSnapshot"/>s to connected UIs.
/// On stop, every metric LEO pinned is handed back to Windows (automatic) — LEO.md §5.9.
/// </summary>
public sealed class NetworkManager(NetworkInventory inventory, ConfigStore config, ILogger<NetworkManager> log)
    : BackgroundService
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan PeriodicRefresh = TimeSpan.FromSeconds(15);

    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly SemaphoreSlim _changeSignal = new(0, int.MaxValue);
    private readonly HashSet<int> _pinnedInterfaces = [];
    private NetworkSnapshot _snapshot = NetworkSnapshot.Empty;
    private string _snapshotFingerprint = "";

    public event EventHandler<NetworkSnapshot>? Changed;

    public NetworkSnapshot Snapshot => Volatile.Read(ref _snapshot);

    // ── API (called from LeoControlTarget) ───────────────────────────────────

    public async Task SetPrimaryAsync(string adapterId, CancellationToken ct)
    {
        var adapter = Snapshot.Adapters.FirstOrDefault(a => a.Id.Equals(adapterId, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException($"Adapter {adapterId} not found.");
        if (!adapter.IsPrimaryCandidate)
            throw new InvalidOperationException($"{adapter.DisplayName} can't be the Primary network.");

        var current = MergedOrder();
        config.Update(c =>
        {
            c.PriorityOrder = PriorityPlanner.MoveToFront(current, adapter.Id);
            return true;
        });
        log.LogInformation("Primary preference set to {Adapter} ({Id})", adapter.DisplayName, adapter.Id);
        await RefreshAsync(ct);
    }

    public async Task SetPriorityOrderAsync(IReadOnlyList<string> adapterIds, CancellationToken ct)
    {
        // Keep remembered-but-absent adapters (e.g. an unplugged phone) after the ones the UI sent.
        var order = adapterIds.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        order.AddRange(MergedOrder().Where(id => !order.Contains(id, StringComparer.OrdinalIgnoreCase)));
        config.Update(c =>
        {
            c.PriorityOrder = order;
            return true;
        });
        await RefreshAsync(ct);
    }

    public async Task SetAdapterNicknameAsync(string adapterId, string? nickname, CancellationToken ct)
    {
        var adapter = Snapshot.Adapters.FirstOrDefault(a => a.Id.Equals(adapterId, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException($"Adapter {adapterId} not found.");
        var value = Normalize(nickname);
        config.Update(c =>
        {
            var entry = c.Adapters.FirstOrDefault(a => a.Id.Equals(adapter.Id, StringComparison.OrdinalIgnoreCase));
            if (entry is null) c.Adapters.Add(entry = new AdapterSettings { Id = adapter.Id });
            entry.Mac = adapter.Mac;
            entry.Nickname = value;
            return true;
        });
        await RefreshAsync(ct);
    }

    public async Task SetNetworkNicknameAsync(string networkKey, string? nickname, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(networkKey)) throw new ArgumentException("Network key required.", nameof(networkKey));
        var value = Normalize(nickname);
        config.Update(c =>
        {
            var entry = c.Networks.FirstOrDefault(n => n.Key.Equals(networkKey, StringComparison.Ordinal));
            if (entry is null) c.Networks.Add(entry = new NetworkSettings { Key = networkKey });
            entry.Nickname = value;
            c.Networks.RemoveAll(n => n.Nickname is null);
            return true;
        });
        await RefreshAsync(ct);
    }

    // ── Lifecycle ────────────────────────────────────────────────────────────

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        log.LogInformation("Network manager starting (config: {Path})", config.Path);

        NetworkChange.NetworkAddressChanged += OnWindowsNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged += OnWindowsNetworkChanged;
        try { WinRtNetworkInformation.NetworkStatusChanged += OnWinRtStatusChanged; }
        catch (Exception ex) { log.LogWarning(ex, "NetworkStatusChanged unavailable; relying on polling"); }

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await RefreshAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    log.LogError(ex, "Network refresh failed");
                }

                // Sleep until Windows reports a change (debounced) or the periodic refresh is due.
                if (await _changeSignal.WaitAsync(PeriodicRefresh, stoppingToken))
                {
                    await Task.Delay(Debounce, stoppingToken);
                    while (_changeSignal.CurrentCount > 0) _changeSignal.Wait(0); // coalesce bursts
                }
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            NetworkChange.NetworkAddressChanged -= OnWindowsNetworkChanged;
            NetworkChange.NetworkAvailabilityChanged -= OnWindowsNetworkChanged;
            try { WinRtNetworkInformation.NetworkStatusChanged -= OnWinRtStatusChanged; } catch { }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        RestoreAllMetrics();
    }

    /// <summary>Gives every metric LEO pinned back to Windows (LEO.md §5.9).</summary>
    private void RestoreAllMetrics()
    {
        int[] pinned;
        lock (_pinnedInterfaces) pinned = [.. _pinnedInterfaces];
        foreach (var index in pinned)
        {
            IpHelper.RestoreAutomaticMetric(index, AddressFamily.InterNetwork);
            IpHelper.RestoreAutomaticMetric(index, AddressFamily.InterNetworkV6);
        }
        if (pinned.Length > 0) log.LogInformation("Restored automatic metrics on {Count} interface(s)", pinned.Length);
    }

    private void OnWindowsNetworkChanged(object? sender, EventArgs e) => _changeSignal.Release();

    private void OnWinRtStatusChanged(object sender) => _changeSignal.Release();

    // ── Refresh ──────────────────────────────────────────────────────────────

    private async Task RefreshAsync(CancellationToken ct)
    {
        await _refreshGate.WaitAsync(ct);
        try
        {
            var scanned = inventory.Scan();
            var (userOrder, manage, adapterSettings, networkSettings) = config.Read(c => (
                c.PriorityOrder.ToList(),
                c.ManageMetrics,
                c.Adapters.Select(a => (a.Id, a.Mac, a.Nickname)).ToList(),
                c.Networks.ToDictionary(n => n.Key, n => n.Nickname, StringComparer.Ordinal)));

            var plan = PriorityPlanner.Plan(
                scanned.Select(a => new PlannerAdapter(a.Id, a.Kind, a.Status == AdapterStatus.Up, a.Gateways.Count > 0,
                    a.Metric ?? int.MaxValue)).ToList(),
                userOrder);

            var metrics = scanned.ToDictionary(a => a.Id, a => (a.Metric, a.AutomaticMetric), StringComparer.OrdinalIgnoreCase);
            if (manage) ApplyMetrics(scanned, plan, metrics);

            var position = plan.Order.Select((id, i) => (id, i)).ToDictionary(x => x.id, x => x.i, StringComparer.OrdinalIgnoreCase);
            var adapters = scanned
                .OrderBy(a => position.GetValueOrDefault(a.Id, int.MaxValue))
                .ThenBy(a => a.Kind.DefaultRank())
                .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
                .Select(a =>
                {
                    var nick = adapterSettings.FirstOrDefault(s => s.Id.Equals(a.Id, StringComparison.OrdinalIgnoreCase)).Nickname
                        ?? (a.Mac is null ? null : adapterSettings.FirstOrDefault(s => a.Mac.Equals(s.Mac, StringComparison.OrdinalIgnoreCase)).Nickname);
                    var network = a.Network is null ? null : new NetworkInfo(a.Network.Key, a.Network.Name,
                        networkSettings.GetValueOrDefault(a.Network.Key), a.Network.Connectivity, a.Network.IsMetered);
                    int? priority = position.TryGetValue(a.Id, out var p) ? p : null;
                    var (metric, automatic) = metrics[a.Id];
                    return new AdapterInfo(a.Id, a.Name, a.Description, a.Kind, nick, a.Mac, a.Status, a.LinkSpeedBps,
                        a.IPv4, a.IPv6, a.Gateways, a.DnsServers, network, metric, automatic,
                        priority,
                        IsPrimary: a.Id.Equals(plan.PrimaryId, StringComparison.OrdinalIgnoreCase),
                        IsPrimaryCandidate: a.Kind.IsPrimaryCandidate());
                })
                .ToList();

            var snapshot = new NetworkSnapshot(adapters, plan.PrimaryId, manage, DateTimeOffset.Now);
            var fingerprint = Fingerprint(snapshot);
            Volatile.Write(ref _snapshot, snapshot);
            if (fingerprint != _snapshotFingerprint)
            {
                var first = _snapshotFingerprint.Length == 0;
                _snapshotFingerprint = fingerprint;
                var primary = adapters.FirstOrDefault(a => a.IsPrimary);
                log.LogInformation("{What}: {Count} adapter(s), primary = {Primary}",
                    first ? "Networks discovered" : "Networks changed", adapters.Count, primary?.DisplayName ?? "none");
                Changed?.Invoke(this, snapshot);
            }
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private void ApplyMetrics(IReadOnlyList<ScannedAdapter> scanned, PriorityPlan plan,
        Dictionary<string, (int? Metric, bool Automatic)> metrics)
    {
        foreach (var a in scanned)
        {
            if (a.InterfaceIndex < 0 || !plan.Metrics.TryGetValue(a.Id, out var target)) continue;
            if (metrics[a.Id] is { Metric: var m, Automatic: false } && m == target) continue;

            var rc4 = IpHelper.SetMetric(a.InterfaceIndex, AddressFamily.InterNetwork, target);
            IpHelper.SetMetric(a.InterfaceIndex, AddressFamily.InterNetworkV6, target); // IPv6 may be unbound
            if (rc4 == 0)
            {
                lock (_pinnedInterfaces) _pinnedInterfaces.Add(a.InterfaceIndex);
                metrics[a.Id] = (target, false);
                log.LogInformation("Metric {Adapter} -> {Metric}", a.Name, target);
            }
            else if (rc4 != 1168) // ERROR_NOT_FOUND: no IPv4 interface (adapter unbound) — not worth a warning
            {
                log.LogWarning("Setting metric on {Adapter} failed: Win32 error {Error}", a.Name, rc4);
            }
        }
    }

    /// <summary>Config order + any currently-planned adapters not yet in it.</summary>
    private List<string> MergedOrder()
    {
        var order = config.Read(c => c.PriorityOrder.ToList());
        foreach (var a in Snapshot.Adapters.Where(a => a.IsPrimaryCandidate && a.PriorityIndex is not null).OrderBy(a => a.PriorityIndex))
        {
            if (!order.Contains(a.Id, StringComparer.OrdinalIgnoreCase)) order.Add(a.Id);
        }
        return order;
    }

    /// <summary>Compares snapshots ignoring the timestamp, so the UI only hears about real changes.</summary>
    private static string Fingerprint(NetworkSnapshot s)
    {
        var sb = new System.Text.StringBuilder().Append(s.PrimaryAdapterId).Append('|').Append(s.ManageMetrics);
        foreach (var a in s.Adapters)
        {
            sb.Append('\n').AppendJoin('|', a.Id, a.Name, a.Nickname, a.Kind, a.Status, a.LinkSpeedBps, a.Metric,
                    a.AutomaticMetric, a.PriorityIndex, a.IsPrimary, a.Mac,
                    a.Network?.Key, a.Network?.Name, a.Network?.Nickname, a.Network?.Connectivity, a.Network?.IsMetered)
              .Append('|').AppendJoin(',', a.IPv4).Append('|').AppendJoin(',', a.IPv6)
              .Append('|').AppendJoin(',', a.Gateways).Append('|').AppendJoin(',', a.DnsServers);
        }
        return sb.ToString();
    }

    private static string? Normalize(string? nickname)
    {
        var trimmed = nickname?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed.Length > 48 ? trimmed[..48] : trimmed;
    }
}
