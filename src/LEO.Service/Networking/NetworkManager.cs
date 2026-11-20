using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Leo.Core.Config;
using Leo.Core.Networking;
using Leo.Ipc;
using Leo.Service.Health;
using Leo.Service.Interop;
using WinRtNetworkInformation = Windows.Networking.Connectivity.NetworkInformation;

namespace Leo.Service.Networking;

/// <summary>
/// Owns the live network picture (LEO.md §5.1, §5.7, §6.1–6.3): scans adapters, probes each one's real
/// internet through itself, applies the user's priority (with failover demotion) as interface metrics, keeps
/// nicknames, publishes <see cref="NetworkSnapshot"/>s and user-facing notifications.
/// On stop, every metric LEO pinned is handed back to Windows (automatic) — LEO.md §5.9.
/// </summary>
public sealed class NetworkManager(
    NetworkInventory inventory, ConfigStore config, NotificationHub notifications, ILogger<NetworkManager> log,
    Leo.Service.Wifi.WifiManager? wifi = null)
    : BackgroundService
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan PeriodicRefresh = TimeSpan.FromSeconds(15);

    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly SemaphoreSlim _changeSignal = new(0, int.MaxValue);
    private readonly HashSet<int> _pinnedInterfaces = [];
    private readonly FailoverController _failover = new(TimeProvider.System);
    private readonly Dictionary<string, HealthReport> _health = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<ScannedAdapter> _lastScan = [];
    private NetworkSnapshot _snapshot = NetworkSnapshot.Empty;
    private string _snapshotFingerprint = "";
    private bool _userChangedPriority;

    public event EventHandler<NetworkSnapshot>? Changed;

    public NetworkSnapshot Snapshot => Volatile.Read(ref _snapshot);

    /// <summary>Internet probes on/off (tests turn them off so a test run never sends traffic).</summary>
    public bool ProbesEnabled { get; init; } = true;

    /// <summary>Ask for a full rescan soon (e.g. after LEO connected a Wi-Fi adapter).</summary>
    public void RequestRescan() => _changeSignal.Release();

    // ── API (called from LeoControlTarget) ───────────────────────────────────

    public async Task SetPrimaryAsync(string adapterId, CancellationToken ct)
    {
        var adapter = Find(adapterId);
        if (!adapter.IsPrimaryCandidate)
            throw new InvalidOperationException($"{adapter.DisplayName} can't be the Primary network.");

        var current = MergedOrder();
        config.Update(c =>
        {
            c.PriorityOrder = PriorityPlanner.MoveToFront(current, adapter.Id);
            return true;
        });
        log.LogInformation("Primary preference set to {Adapter} ({Id})", adapter.DisplayName, adapter.Id);
        _userChangedPriority = true;
        await RefreshAsync(rescan: true, ct);
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
        _userChangedPriority = true;
        await RefreshAsync(rescan: true, ct);
    }

    public async Task SetAdapterNicknameAsync(string adapterId, string? nickname, CancellationToken ct)
    {
        var adapter = Find(adapterId);
        var value = Normalize(nickname);
        config.Update(c =>
        {
            var entry = c.Adapters.FirstOrDefault(a => a.Id.Equals(adapter.Id, StringComparison.OrdinalIgnoreCase));
            if (entry is null) c.Adapters.Add(entry = new AdapterSettings { Id = adapter.Id });
            entry.Mac = adapter.Mac;
            entry.Nickname = value;
            return true;
        });
        await RefreshAsync(rescan: false, ct);
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
        await RefreshAsync(rescan: false, ct);
    }

    public async Task SetAutoFailoverAsync(bool enabled, CancellationToken ct)
    {
        config.Update(c =>
        {
            if (c.Health.AutoFailover == enabled) return false;
            c.Health.AutoFailover = enabled;
            return true;
        });
        log.LogInformation("Automatic failover {State}", enabled ? "enabled" : "disabled");
        await RefreshAsync(rescan: false, ct);
    }

    public AdapterInfo Find(string adapterId) =>
        Snapshot.Adapters.FirstOrDefault(a => a.Id.Equals(adapterId, StringComparison.OrdinalIgnoreCase))
        ?? throw new KeyNotFoundException($"Adapter {adapterId} not found.");

    // ── Lifecycle ────────────────────────────────────────────────────────────

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        log.LogInformation("Network manager starting (config: {Path})", config.Path);

        NetworkChange.NetworkAddressChanged += OnWindowsNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged += OnWindowsNetworkChanged;
        try { WinRtNetworkInformation.NetworkStatusChanged += OnWinRtStatusChanged; }
        catch (Exception ex) { log.LogWarning(ex, "NetworkStatusChanged unavailable; relying on polling"); }

        var probes = ProbesEnabled ? Task.Run(() => ProbeLoopAsync(stoppingToken), CancellationToken.None) : Task.CompletedTask;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await RefreshAsync(rescan: true, stoppingToken);
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
            try { await probes; } catch { }
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

    // ── Health probes ────────────────────────────────────────────────────────

    private async Task ProbeLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var settings = config.Read(c => c.Health);
            try
            {
                await ProbeAllAsync(settings, ct);
                await RefreshAsync(rescan: false, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Health probe round failed");
            }

            try { await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(settings.ProbeIntervalSeconds, 2, 300)), ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task ProbeAllAsync(HealthSettings settings, CancellationToken ct)
    {
        var targets = settings.ProbeTargets.Count > 0 ? settings.ProbeTargets : new HealthSettings().ProbeTargets;
        var scan = Volatile.Read(ref _lastScan);

        var jobs = new List<Task>();
        foreach (var a in scan.Where(a => a.Kind.IsPrimaryCandidate()))
        {
            var local = a.IPv4.Select(IPAddress.Parse).FirstOrDefault(ip => !ip.ToString().StartsWith("169.254.", StringComparison.Ordinal));
            HealthReport? immediate = null;
            if (a.Status != AdapterStatus.Up || local is null || a.InterfaceIndex < 0)
            {
                immediate = new HealthReport(HealthState.Disconnected, null, DateTimeOffset.Now);
            }
            else if (a.Gateways.Count == 0)
            {
                immediate = new HealthReport(HealthState.NoInternet, null, DateTimeOffset.Now, "No gateway on this network");
            }

            if (immediate is not null)
            {
                lock (_health) _health[a.Id] = immediate;
                continue;
            }

            var target = new ProbeTargetAdapter(a.Id, a.InterfaceIndex, local!,
                a.DnsServers.Select(s => IPAddress.TryParse(s, out var ip) ? ip : null).OfType<IPAddress>().ToList());
            jobs.Add(Task.Run(async () =>
            {
                var report = await InterfaceProbe.RunAsync(target, targets, settings.SlowLatencyMs, ct);
                HealthReport? previous;
                lock (_health)
                {
                    _health.TryGetValue(a.Id, out previous);
                    _health[a.Id] = report;
                }
                if (previous?.State != report.State)
                {
                    log.LogInformation("Health {Adapter}: {Old} -> {New} {Latency}{Detail}", a.Name,
                        previous?.State.ToString() ?? "-", report.State,
                        report.LatencyMs is { } ms ? $"({ms} ms) " : "", report.Detail);
                }
            }, ct));
        }
        await Task.WhenAll(jobs);
    }

    private HealthReport HealthOf(ScannedAdapter a, HealthSettings settings)
    {
        if (a.Status != AdapterStatus.Up) return new HealthReport(HealthState.Disconnected, null, DateTimeOffset.Now);
        lock (_health)
        {
            // Results older than 3 rounds are stale (e.g. adapter just reconnected) → Unknown until re-probed.
            if (_health.TryGetValue(a.Id, out var r) &&
                DateTimeOffset.Now - r.CheckedAt < TimeSpan.FromSeconds(Math.Max(15, settings.ProbeIntervalSeconds * 3)))
            {
                return r;
            }
        }
        return new HealthReport(HealthState.Unknown, null, DateTimeOffset.Now);
    }

    // ── Refresh ──────────────────────────────────────────────────────────────

    private async Task RefreshAsync(bool rescan, CancellationToken ct)
    {
        await _refreshGate.WaitAsync(ct);
        try
        {
            var scanned = rescan || _lastScan.Count == 0 ? inventory.Scan() : _lastScan;
            Volatile.Write(ref _lastScan, scanned);

            var (userOrder, manage, adapterSettings, networkSettings, health) = config.Read(c => (
                c.PriorityOrder.ToList(),
                c.ManageMetrics,
                c.Adapters.Select(a => (a.Id, a.Mac, a.Nickname, Pins: a.PinnedSsids.ToList())).ToList(),
                c.Networks.ToDictionary(n => n.Key, n => n.Nickname, StringComparer.Ordinal),
                c.Health));

            // Failover: feed health of every candidate, get the demoted set.
            var healthById = scanned.Where(a => a.Kind.IsPrimaryCandidate())
                .ToDictionary(a => a.Id, a => HealthOf(a, health), StringComparer.OrdinalIgnoreCase);
            _failover.Enabled = health.AutoFailover;
            _failover.FailoverDelay = TimeSpan.FromSeconds(Math.Max(1, health.FailoverDelaySeconds));
            _failover.FailbackDelay = TimeSpan.FromSeconds(Math.Max(5, health.FailbackDelaySeconds));
            _failover.Update(healthById.ToDictionary(kv => kv.Key, kv => kv.Value.State, StringComparer.OrdinalIgnoreCase));
            var demoted = _failover.Demoted;

            var plan = PriorityPlanner.Plan(
                scanned.Select(a => new PlannerAdapter(a.Id, a.Kind, a.Status == AdapterStatus.Up, a.Gateways.Count > 0,
                    a.Metric ?? int.MaxValue)).ToList(),
                userOrder, demoted);

            var metrics = scanned.ToDictionary(a => a.Id, a => (a.Metric, a.AutomaticMetric), StringComparer.OrdinalIgnoreCase);
            if (manage) ApplyMetrics(scanned, plan, metrics);

            var rank = plan.Ranking.Select((id, i) => (id, i)).ToDictionary(x => x.id, x => x.i, StringComparer.OrdinalIgnoreCase);
            var adapters = scanned
                .OrderBy(a => rank.GetValueOrDefault(a.Id, int.MaxValue))
                .ThenBy(a => a.Kind.DefaultRank())
                .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
                .Select(a =>
                {
                    var settings = adapterSettings.FirstOrDefault(s => s.Id.Equals(a.Id, StringComparison.OrdinalIgnoreCase));
                    var nick = settings.Nickname
                        ?? (a.Mac is null ? null : adapterSettings.FirstOrDefault(s => a.Mac.Equals(s.Mac, StringComparison.OrdinalIgnoreCase)).Nickname);
                    var network = a.Network is null ? null : new NetworkInfo(a.Network.Key, a.Network.Name,
                        networkSettings.GetValueOrDefault(a.Network.Key), a.Network.Connectivity, a.Network.IsMetered);
                    int? priority = rank.TryGetValue(a.Id, out var p) ? p : null;
                    var (metric, automatic) = metrics[a.Id];
                    var h = healthById.GetValueOrDefault(a.Id);
                    var dual = wifi?.DualLink.GetValueOrDefault(a.Id);
                    return new AdapterInfo(a.Id, a.Name, a.Description, a.Kind, nick, a.Mac, a.Status, a.LinkSpeedBps,
                        a.IPv4, a.IPv6, a.Gateways, a.DnsServers, network, metric, automatic,
                        priority,
                        IsPrimary: a.Id.Equals(plan.PrimaryId, StringComparison.OrdinalIgnoreCase),
                        IsPrimaryCandidate: a.Kind.IsPrimaryCandidate(),
                        Health: h?.State ?? HealthState.Unknown,
                        LatencyMs: h?.LatencyMs,
                        HealthDetail: h?.Detail,
                        IsDemoted: demoted.Contains(a.Id),
                        FailbackInSeconds: _failover.SecondsUntilFailback(a.Id),
                        PinnedSsids: settings.Pins ?? [],
                        DualLinkSupported: dual?.Supported ?? false,
                        DualLinkEnabled: dual?.Enabled ?? false,
                        FollowsAdapterId: dual?.PrimaryId);
                })
                .ToList();

            // Failover is "active" when the Primary isn't the user's top routable choice.
            var topChoice = plan.Ranking.FirstOrDefault(id =>
                adapters.First(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) is { Status: AdapterStatus.Up, Gateways.Count: > 0 });
            var failoverActive = plan.PrimaryId is not null && topChoice is not null &&
                                 !plan.PrimaryId.Equals(topChoice, StringComparison.OrdinalIgnoreCase);

            var snapshot = new NetworkSnapshot(adapters, plan.PrimaryId, manage, DateTimeOffset.Now, health.AutoFailover, failoverActive);
            var previous = Snapshot;
            var fingerprint = Fingerprint(snapshot);
            Volatile.Write(ref _snapshot, snapshot);

            if (fingerprint != _snapshotFingerprint)
            {
                var first = _snapshotFingerprint.Length == 0;
                _snapshotFingerprint = fingerprint;
                if (!first) Announce(previous, snapshot);
                if (first || previous.PrimaryAdapterId != snapshot.PrimaryAdapterId || previous.Adapters.Count != adapters.Count)
                {
                    log.LogInformation("{What}: {Count} adapter(s), primary = {Primary}{Failover}",
                        first ? "Networks discovered" : "Networks changed", adapters.Count,
                        adapters.FirstOrDefault(a => a.IsPrimary)?.DisplayName ?? "none",
                        failoverActive ? " (failover)" : "");
                }
                Changed?.Invoke(this, snapshot);
            }
            _userChangedPriority = false;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    /// <summary>Turns snapshot differences into user notifications (LEO.md §6.10).</summary>
    private void Announce(NetworkSnapshot before, NetworkSnapshot after)
    {
        static string Label(AdapterInfo a) => a.Network is { } n ? $"{a.DisplayName} ({n.Nickname ?? n.Name})" : a.DisplayName;

        var oldPrimary = before.Adapters.FirstOrDefault(a => a.IsPrimary);
        var newPrimary = after.Adapters.FirstOrDefault(a => a.IsPrimary);

        if (oldPrimary?.Id != newPrimary?.Id && !_userChangedPriority)
        {
            var oldNow = oldPrimary is null ? null : after.Adapters.FirstOrDefault(a => a.Id == oldPrimary.Id);
            if (newPrimary is null)
            {
                if (oldPrimary is not null)
                    notifications.Publish(NotificationKind.NoInternetAnywhere, "No network available",
                        $"{Label(oldPrimary)} dropped and no other network can take over.");
            }
            else if (oldNow is { IsDemoted: true })
            {
                var why = oldNow.Health == HealthState.Captive ? "needs a sign-in" : "has no internet";
                notifications.Publish(NotificationKind.Failover, $"Switched to {newPrimary.DisplayName}",
                    $"{Label(oldNow)} {why}. LEO moved your traffic to {Label(newPrimary)}.", newPrimary.Id);
            }
            else if (before.Adapters.FirstOrDefault(a => a.Id == newPrimary.Id) is { IsDemoted: true })
            {
                notifications.Publish(NotificationKind.Failback, $"Back on {newPrimary.DisplayName}",
                    $"{Label(newPrimary)} has had a good connection for a while, so LEO switched back to it.", newPrimary.Id);
            }
            else if (oldPrimary is not null)
            {
                notifications.Publish(NotificationKind.PrimaryChanged, $"Now using {newPrimary.DisplayName}",
                    oldNow is null || oldNow.Status != AdapterStatus.Up
                        ? $"{oldPrimary.DisplayName} disconnected; traffic now goes through {Label(newPrimary)}."
                        : $"Traffic now goes through {Label(newPrimary)}.", newPrimary.Id);
            }
        }

        foreach (var added in after.Adapters.Where(a => a.IsPrimaryCandidate && before.Adapters.All(b => b.Id != a.Id)))
        {
            notifications.Publish(NotificationKind.NewAdapter, "New network adapter",
                $"{added.Name} ({added.Description}) was added. Open LEO to name it or set its priority.", added.Id);
        }

        foreach (var a in after.Adapters.Where(a => a.Health == HealthState.Captive))
        {
            if (before.Adapters.FirstOrDefault(b => b.Id == a.Id)?.Health != HealthState.Captive)
            {
                notifications.Publish(NotificationKind.CaptivePortal, "Sign-in needed",
                    $"{Label(a)} is asking you to sign in (hotel/café portal). Open a browser to continue.", a.Id);
            }
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
        var sb = new System.Text.StringBuilder()
            .Append(s.PrimaryAdapterId).Append('|').Append(s.ManageMetrics).Append('|').Append(s.AutoFailover).Append('|').Append(s.FailoverActive);
        foreach (var a in s.Adapters)
        {
            sb.Append('\n').AppendJoin('|', a.Id, a.Name, a.Nickname, a.Kind, a.Status, a.LinkSpeedBps, a.Metric,
                    a.AutomaticMetric, a.PriorityIndex, a.IsPrimary, a.Mac, a.Health, a.LatencyMs, a.IsDemoted, a.FailbackInSeconds,
                    a.Network?.Key, a.Network?.Name, a.Network?.Nickname, a.Network?.Connectivity, a.Network?.IsMetered)
              .Append('|').AppendJoin(',', a.IPv4).Append('|').AppendJoin(',', a.IPv6)
              .Append('|').AppendJoin(',', a.Gateways).Append('|').AppendJoin(',', a.DnsServers)
              .Append('|').AppendJoin(',', a.PinnedSsids)
              .Append('|').Append(a.DualLinkSupported).Append(a.DualLinkEnabled).Append(a.FollowsAdapterId);
        }
        return sb.ToString();
    }

    private static string? Normalize(string? nickname)
    {
        var trimmed = nickname?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed.Length > 48 ? trimmed[..48] : trimmed;
    }
}
