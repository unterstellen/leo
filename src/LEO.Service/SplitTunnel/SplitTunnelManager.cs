using System.Net;
using System.Net.Sockets;
using Leo.Core.Config;
using Leo.Core.Networking;
using Leo.Core.SplitTunnel;
using Leo.Ipc;
using Leo.Service.Networking;

namespace Leo.Service.SplitTunnel;

/// <summary>
/// Split tunneling by app (LEO.md §6.4, §17). Runs the engine only while it's switched on and at least one rule is
/// enabled; otherwise LEO stays in metrics-only mode and never touches routing. Applies the cheapest change:
/// live route switches (health/Primary), rule-list refreshes (rule edits, child programs), full reload (adapters).
/// A crashing engine is restarted with backoff, then given up on — always leaving normal networking.
/// </summary>
public sealed class SplitTunnelManager(
    ConfigStore config, NetworkManager networks, MihomoEngine engine, ChildProcessTracker children,
    NotificationHub notifications, ILogger<SplitTunnelManager> log, VpnMonitor? vpn = null) : BackgroundService
{
    private static readonly TimeSpan[] Backoff = [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(60)];
    private static readonly TimeSpan CrashWindow = TimeSpan.FromMinutes(10);

    private readonly SemaphoreSlim _signal = new(0, int.MaxValue);
    private readonly SemaphoreSlim _syncGate = new(1, 1);
    private readonly List<DateTimeOffset> _crashes = [];
    private DateTimeOffset _retryAt = DateTimeOffset.MinValue;
    private bool _gaveUp;
    private string? _structureKey;
    private string _rulesFingerprint = "";
    private SplitTunnelEngineState _state = SplitTunnelEngineState.Off;
    private string? _message;
    private SplitTunnelInfo _info = SplitTunnelInfo.Empty;

    public event EventHandler<SplitTunnelInfo>? Changed;

    public SplitTunnelInfo Info => Volatile.Read(ref _info);

    // ── API ─────────────────────────────────────────────────────────────────

    /// <summary>Re-read rules from config (a profile switch replaced them).</summary>
    public void RequestSync()
    {
        _gaveUp = _routesPaused = false;
        _signal.Release();
    }

    /// <summary>Running engine, or null. For stats and the connections view.</summary>
    public MihomoEngine? RunningEngine => engine.IsRunning && _state == SplitTunnelEngineState.Running ? engine : null;

    public async Task SetEnabledAsync(bool enabled, CancellationToken ct)
    {
        config.Update(c =>
        {
            if (c.SplitTunnel.Enabled == enabled) return false;
            c.SplitTunnel.Enabled = enabled;
            return true;
        });
        if (enabled) { _gaveUp = _routesPaused = false; _crashes.Clear(); }
        log.LogInformation("Split tunneling switched {State}", enabled ? "on" : "off");
        await SyncAsync(ct);
    }

    public async Task SaveRuleAsync(AppRule rule, CancellationToken ct)
    {
        rule.Pattern = rule.Pattern.Trim();
        if (rule.Pattern.Length == 0) throw new ArgumentException("Choose an app, folder or name pattern.");
        if (rule.Kind is AppMatchKind.ExactPath or AppMatchKind.Folder && !Path.IsPathFullyQualified(rule.Pattern))
            throw new ArgumentException("Use a full path, e.g. C:\\Program Files\\App\\app.exe.");
        if (string.IsNullOrWhiteSpace(rule.TargetAdapterId)) throw new ArgumentException("Choose the network for this app.");
        if (BondTargets.IsBond(rule.TargetAdapterId))
        {
            CheckBondExists(rule.TargetAdapterId);
            if (RuleTargets.CheckBond(rule) is { } bondProblem) throw new ArgumentException(bondProblem);
            rule.TargetVpnKey = null;
        }
        var target = networks.Snapshot.Adapters.FirstOrDefault(a => a.Id.Equals(rule.TargetAdapterId, StringComparison.OrdinalIgnoreCase));
        if (target is not null && RuleTargets.Check(rule, target.Kind, target.DisplayName) is { } problem) throw new ArgumentException(problem);
        if (target is not null) rule.TargetVpnKey = target.Vpn?.Key;
        if (string.IsNullOrWhiteSpace(rule.Id)) rule.Id = Guid.NewGuid().ToString("N");
        if (string.IsNullOrWhiteSpace(rule.DisplayName))
            rule.DisplayName = rule.Kind == AppMatchKind.ExactPath ? Path.GetFileNameWithoutExtension(rule.Pattern) : rule.Pattern;

        config.Update(c =>
        {
            var i = c.SplitTunnel.AppRules.FindIndex(r => r.Id == rule.Id);
            if (i >= 0) c.SplitTunnel.AppRules[i] = rule; else c.SplitTunnel.AppRules.Add(rule);
            return true;
        });
        log.LogInformation("App rule saved: {Name} ({Kind}) -> {Target}", rule.DisplayName, rule.Kind, target?.DisplayName ?? rule.TargetAdapterId);
        _gaveUp = _routesPaused = false; // a user change deserves a fresh attempt
        await SyncAsync(ct);
    }

    public async Task DeleteRuleAsync(string ruleId, CancellationToken ct)
    {
        var removed = false;
        config.Update(c => removed = c.SplitTunnel.AppRules.RemoveAll(r => r.Id == ruleId) > 0);
        if (!removed) throw new KeyNotFoundException("That rule doesn't exist anymore.");
        log.LogInformation("App rule deleted: {Id}", ruleId);
        await SyncAsync(ct);
    }

    // ── Destinations + DNS (M4) ──────────────────────────────────────────────

    public async Task SaveDestinationRuleAsync(DestinationRule rule, CancellationToken ct)
    {
        if (DestinationMatcher.Normalize(rule) is { } problem) throw new ArgumentException(problem);
        if (string.IsNullOrWhiteSpace(rule.TargetAdapterId)) throw new ArgumentException("Choose the network for this destination.");
        if (BondTargets.IsBond(rule.TargetAdapterId))
        {
            CheckBondExists(rule.TargetAdapterId);
            rule.TargetVpnKey = null;
        }
        var target = networks.Snapshot.Adapters.FirstOrDefault(a => a.Id.Equals(rule.TargetAdapterId, StringComparison.OrdinalIgnoreCase));
        if (target is not null && RuleTargets.Check(null, target.Kind, target.DisplayName) is { } targetProblem) throw new ArgumentException(targetProblem);
        if (target is not null) rule.TargetVpnKey = target.Vpn?.Key;
        if (string.IsNullOrWhiteSpace(rule.AppRuleId)) rule.AppRuleId = null;
        if (rule.AppRuleId is not null && !config.Read(c => c.SplitTunnel.AppRules.Any(a => a.Id == rule.AppRuleId)))
            throw new ArgumentException("That app doesn't have a rule anymore. Choose another app or \"Any app\".");
        if (string.IsNullOrWhiteSpace(rule.Id)) rule.Id = Guid.NewGuid().ToString("N");

        config.Update(c =>
        {
            var list = c.SplitTunnel.DestinationRules;
            var i = list.FindIndex(r => r.Id == rule.Id);
            if (i >= 0) list[i] = rule; else list.Add(rule);
            return true;
        });
        // Destinations can be personal: log the rule's id and kind, not the domain/address.
        log.LogInformation("Destination rule saved: {Id} ({Kind}) -> {Target}", rule.Id, rule.Kind, target?.DisplayName ?? rule.TargetAdapterId);
        _gaveUp = _routesPaused = false;
        await SyncAsync(ct);
    }

    public async Task DeleteDestinationRuleAsync(string ruleId, CancellationToken ct)
    {
        var removed = false;
        config.Update(c => removed = c.SplitTunnel.DestinationRules.RemoveAll(r => r.Id == ruleId) > 0);
        if (!removed) throw new KeyNotFoundException("That rule doesn't exist anymore.");
        log.LogInformation("Destination rule deleted: {Id}", ruleId);
        await SyncAsync(ct);
    }

    public async Task MoveDestinationRuleAsync(string ruleId, bool up, CancellationToken ct)
    {
        var found = false;
        config.Update(c =>
        {
            var list = c.SplitTunnel.DestinationRules;
            var i = list.FindIndex(r => r.Id == ruleId);
            found = i >= 0;
            var j = up ? i - 1 : i + 1;
            if (i < 0 || j < 0 || j >= list.Count) return false;
            (list[i], list[j]) = (list[j], list[i]);
            return true;
        });
        if (!found) throw new KeyNotFoundException("That rule doesn't exist anymore.");
        await SyncAsync(ct);
    }

    public async Task SetNetworkDnsAsync(string adapterId, IReadOnlyList<string> servers, CancellationToken ct)
    {
        var list = servers.Select(s => s.Trim()).Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (list.Count > 4) throw new ArgumentException("Use up to 4 DNS servers.");
        foreach (var s in list)
            if (EnginePlan.ValidateDnsServer(s) is { } problem) throw new ArgumentException(problem);
        var adapter = networks.Snapshot.Adapters.FirstOrDefault(a => a.Id.Equals(adapterId, StringComparison.OrdinalIgnoreCase))
                      ?? throw new KeyNotFoundException("That network isn't there anymore.");
        config.Update(c =>
        {
            c.SplitTunnel.NetworkDns.RemoveAll(d => d.AdapterId.Equals(adapterId, StringComparison.OrdinalIgnoreCase));
            if (list.Count > 0) c.SplitTunnel.NetworkDns.Add(new NetworkDnsSetting { AdapterId = adapter.Id, Servers = list });
            return true;
        });
        log.LogInformation("DNS for {Adapter}: {Servers}", adapter.DisplayName, list.Count == 0 ? "automatic" : string.Join(", ", list));
        await SyncAsync(ct);
    }

    public async Task<RouteTestResult> TestRouteAsync(string target, string? appPath, CancellationToken ct)
    {
        if (!DestinationMatcher.TryParseTarget(target, out var host, out var port, out var guessed))
            throw new ArgumentException("Enter an address like youtube.com, https://example.com/page or 203.0.113.7:443.");
        appPath = string.IsNullOrWhiteSpace(appPath) ? null : appPath.Trim();

        var addresses = new List<IPAddress>();
        string? lookupNote = null;
        if (IPAddress.TryParse(host, out var literal)) addresses.Add(literal);
        else
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(3));
                addresses.AddRange((await Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork, cts.Token)).Take(8));
            }
            catch (Exception ex) when (ex is SocketException || ex is OperationCanceledException && !ct.IsCancellationRequested)
            {
                lookupNote = $"{host} couldn't be looked up, so IP-range rules weren't checked.";
            }
        }

        var (apps, dests, enabled, bonds) = config.Read(c => (c.SplitTunnel.AppRules.Select(Clone).ToList(),
            c.SplitTunnel.DestinationRules.Select(Clone).ToList(), c.SplitTunnel.Enabled, c.SplitTunnel.Bonds.Select(b => b.Clone()).ToList()));
        var snap = networks.Snapshot;
        ResolveVpnTargets(apps, dests, snap);
        var query = new RouteQuery(host, addresses, port, DestinationProtocol.Tcp, appPath);
        var decision = DestinationMatcher.Decide(dests, apps, query);
        var primaryName = snap.Adapters.FirstOrDefault(a => a.IsPrimary)?.DisplayName ?? "your Primary network";
        var unruledVia = _state == SplitTunnelEngineState.Running ? Info.UnruledVia : null;

        string ruleText, network, why;
        if (decision.Destination is { } d)
        {
            var app = d.AppRuleId is null ? null : apps.FirstOrDefault(a => a.Id == d.AppRuleId);
            ruleText = "Rule: " + DestinationMatcher.Describe(d) + (app is null ? "" : $" from {app.DisplayName}");
            network = Route(d.Enabled, d.TargetAdapterId, d.OnTargetDown, snap, bonds);
            why = d.Kind == DestinationKind.IpRange && !IPAddress.TryParse(host, out _)
                ? $"{host} resolves to {string.Join(", ", addresses.Where(a => DestinationMatcher.TryParseRange(d.Value, out var n, out var p) && DestinationMatcher.InRange(a, n, p)))}, which is in {d.Value}."
                : app is not null ? "This app + destination combo beats every other rule."
                : "Destination rules beat app rules; the most specific destination rule wins.";
        }
        else if (decision.App is { } a)
        {
            ruleText = $"App rule: {a.DisplayName}";
            network = Route(a.Enabled, a.TargetAdapterId, a.OnTargetDown, snap, bonds);
            why = "No destination rule matches, so the app's rule decides.";
        }
        else
        {
            ruleText = "No rule";
            var unruledBond = _state == SplitTunnelEngineState.Running ? Info.UnruledBondNow : null;
            network = unruledVia is not null ? $"{unruledVia} (follows the VPN)"
                : unruledBond is not null ? $"bond '{unruledBond}' (everything else uses it)" : primaryName;
            var where = unruledVia is not null ? $"{unruledVia}, because that VPN takes all unruled traffic"
                : unruledBond is not null ? $"bond '{unruledBond}', because everything else uses it" : "your Primary network";
            why = appPath is null ? $"No destination rule matches, so it uses {where}." : $"Neither a destination nor an app rule matches, so it uses {where}.";
        }
        if (!enabled) { network = primaryName; why = "Split tunneling is switched off, so everything uses your Primary network. " + why; }
        else if (_state != SplitTunnelEngineState.Running && (decision.Destination is not null || decision.App is not null))
            why += " (The routing engine isn't running right now.)";
        if (guessed) why += $" Port {port} assumed.";
        if (lookupNote is not null) why += " " + lookupNote;

        return new RouteTestResult(host, port, guessed, addresses.Select(x => x.ToString()).ToList(),
            decision.Destination?.Id, decision.App?.Id, ruleText, network, why.Trim());
    }

    // ── VPNs (M6) ───────────────────────────────────────────────────────────

    /// <summary>"Let unruled traffic skip this VPN" (machine-wide, by VPN key).</summary>
    public async Task SetVpnSettingsAsync(string vpnKey, bool skipUnruled, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(vpnKey)) throw new ArgumentException("Choose a VPN.");
        config.Update(c =>
        {
            var list = c.SplitTunnel.Vpns;
            var entry = list.FirstOrDefault(v => v.Key.Equals(vpnKey, StringComparison.OrdinalIgnoreCase));
            if ((entry?.SkipUnruled ?? false) == skipUnruled) return false;
            if (entry is null) list.Add(entry = new VpnSettings { Key = vpnKey });
            entry.SkipUnruled = skipUnruled;
            list.RemoveAll(v => !v.SkipUnruled);
            return true;
        });
        log.LogInformation("VPN option: unruled traffic {Mode} a VPN", skipUnruled ? "skips" : "follows");
        await networks.RefreshAsync(ct);
        await SyncAsync(ct);
    }

    // ── Bonds (M7, LEO.md §22) ──────────────────────────────────────────────

    public async Task<string> SaveBondAsync(Bond bond, CancellationToken ct)
    {
        if (bond is null) throw new ArgumentException("Nothing to save.");
        if (string.IsNullOrWhiteSpace(bond.Id)) bond.Id = Guid.NewGuid().ToString("N");
        var others = config.Read(c => c.SplitTunnel.Bonds.Select(b => b.Clone()).ToList());
        if (BondRules.Validate(bond, BondAdapters(networks.Snapshot), others) is { } problem) throw new ArgumentException(problem);
        config.Update(c =>
        {
            var list = c.SplitTunnel.Bonds;
            var i = list.FindIndex(b => b.Id == bond.Id);
            if (i >= 0) list[i] = bond; else list.Add(bond);
            return true;
        });
        // Bond names are the user's labels; the log keeps ids, mode and counts.
        log.LogInformation("Bond saved: {Id} ({Mode}, {Count} networks)", bond.Id, bond.Mode, bond.Members.Count);
        _gaveUp = _routesPaused = false;
        await SyncAsync(ct);
        return bond.Id;
    }

    public async Task DeleteBondAsync(string bondId, CancellationToken ct)
    {
        var removed = false;
        config.Update(c =>
        {
            removed = c.SplitTunnel.Bonds.RemoveAll(b => b.Id == bondId) > 0;
            if (removed && c.SplitTunnel.UnruledBondId == bondId) c.SplitTunnel.UnruledBondId = null;
            return removed;
        });
        if (!removed) throw new KeyNotFoundException("That bond doesn't exist anymore.");
        log.LogInformation("Bond deleted: {Id}", bondId);
        await SyncAsync(ct);
    }

    public async Task MoveBondAsync(string bondId, bool up, CancellationToken ct)
    {
        var found = false;
        config.Update(c =>
        {
            var list = c.SplitTunnel.Bonds;
            var i = list.FindIndex(b => b.Id == bondId);
            found = i >= 0;
            var j = up ? i - 1 : i + 1;
            if (i < 0 || j < 0 || j >= list.Count) return false;
            (list[i], list[j]) = (list[j], list[i]);
            return true;
        });
        if (!found) throw new KeyNotFoundException("That bond doesn't exist anymore.");
        await SyncAsync(ct);
    }

    /// <summary>"Everything else uses": a bond, or null = the Primary (machine-wide).</summary>
    public async Task SetUnruledBondAsync(string? bondId, CancellationToken ct)
    {
        bondId = string.IsNullOrWhiteSpace(bondId) ? null : bondId;
        if (bondId is not null && !config.Read(c => c.SplitTunnel.Bonds.Any(b => b.Id == bondId)))
            throw new KeyNotFoundException("That bond doesn't exist anymore.");
        config.Update(c =>
        {
            if (c.SplitTunnel.UnruledBondId == bondId) return false;
            c.SplitTunnel.UnruledBondId = bondId;
            return true;
        });
        log.LogInformation("Everything else uses {Target}", bondId is null ? "the Primary" : $"bond {bondId}");
        _gaveUp = _routesPaused = false;
        await SyncAsync(ct);
    }

    /// <summary>
    /// Per-member numbers of each bond in the engine: open connections and their bytes so far, and the engine's own
    /// alive flag. Computed on demand from the engine, in memory only; nothing about destinations leaves this method.
    /// </summary>
    public async Task<BondsLiveInfo> GetBondsLiveAsync(CancellationToken ct)
    {
        var running = RunningEngine;
        var plan = _lastPlan;
        if (running is null || plan is null) return new BondsLiveInfo(false, []);
        IReadOnlyList<Leo.Core.Stats.EngineConnection> conns;
        IReadOnlyDictionary<string, bool> alive;
        try
        {
            conns = await running.GetConnectionsAsync(ct);
            alive = await running.GetAliveAsync(plan.Bonds.SelectMany(b => b.Members), plan.BondCheck.Url, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return new BondsLiveInfo(true, []);
        }
        var bonds = ResolveBonds(config.Read(c => c.SplitTunnel.Bonds.Select(b => b.Clone()).ToList()), networks.Snapshot);
        var list = new List<BondLive>();
        foreach (var pb in plan.Bonds)
        {
            var b = bonds.FirstOrDefault(x => x.Id == pb.BondId);
            if (b is null) continue;
            var group = EnginePlan.BondGroupName(b.Id);
            var mine = conns.Where(c => c.Chains.Contains(group)).ToList();
            list.Add(new BondLive(b.Id, b.Members.Select(m =>
            {
                var outbound = EnginePlan.OutboundName(m.AdapterId);
                var onIt = mine.Where(c => c.Chains.Count > 0 && c.Chains[0] == outbound).ToList();
                return new BondLiveMember(m.AdapterId, onIt.Count, onIt.Sum(c => c.Download), onIt.Sum(c => c.Upload),
                    alive.TryGetValue(outbound, out var a) ? a : null);
            }).ToList()));
        }
        return new BondsLiveInfo(true, list);
    }

    private void CheckBondExists(string target)
    {
        var id = BondTargets.BondId(target);
        if (!config.Read(c => c.SplitTunnel.Bonds.Any(b => b.Id == id)))
            throw new ArgumentException("That bond doesn't exist anymore. Choose another network.");
    }

    /// <summary>Physical networks (and VPNs, so validation can name them) as bond logic sees them.</summary>
    private static List<BondAdapter> BondAdapters(NetworkSnapshot snap) =>
        snap.Adapters.Where(a => a.IsPrimaryCandidate || a.Kind == AdapterKind.Vpn).Select(a => new BondAdapter(a.Id, a.Mac, a.DisplayName, a.Kind,
            a.Status == AdapterStatus.Up, a.IsPrimaryCandidate && EnginePlan.IsUsable(ToEngine(a)), a.Network?.IsMetered ?? false,
            a.Network?.Key)).ToList();

    /// <summary>Bond members point at today's adapter (a re-plugged tether comes back with a new id, same MAC).</summary>
    private static List<Bond> ResolveBonds(IEnumerable<Bond> bonds, NetworkSnapshot snap)
    {
        var adapters = BondAdapters(snap);
        return bonds.Select(b =>
        {
            var copy = b.Clone();
            foreach (var m in copy.Members)
                if (BondRules.Resolve(m, adapters) is { } a) m.AdapterId = a.Id;
            return copy;
        }).ToList();
    }

    /// <summary>"Bond 'Fast': WiFi + Ethernet", "Bond 'Fast': WiFi (Ethernet unavailable)", "WiFi (fallback: no network in bond 'Fast' is available)".</summary>
    private static string BondRoute(Bond? b, OnTargetDown mode, NetworkSnapshot snap)
    {
        if (b is null) return "Not active (its bond was removed)";
        var members = BondRules.Members(b, BondAdapters(snap));
        var usable = members.Where(x => x.Adapter is { Usable: true }).Select(x => x.Adapter!.Name).ToList();
        var down = members.Where(x => x.Adapter is not { Usable: true }).Select(x => x.Adapter?.Name ?? "a network that isn't plugged in").ToList();
        var primary = snap.Adapters.FirstOrDefault(a => a.IsPrimary)?.DisplayName ?? "Primary";
        if (usable.Count == 0)
            return mode == OnTargetDown.Block ? $"Blocked (no network in bond '{b.Name}' is available)"
                : $"{primary} (fallback: no network in bond '{b.Name}' is available)";
        var head = b.Mode == BondMode.Fastest && usable.Count > 1
            ? $"Bond '{b.Name}': the fastest of {string.Join(" and ", usable)}"
            : $"Bond '{b.Name}': {string.Join(" + ", usable)}";
        return down.Count == 0 ? head : $"{head} ({string.Join(", ", down)} unavailable)";
    }

    /// <summary><see cref="RouteNow"/> for adapters, <see cref="BondRoute"/> for bonds.</summary>
    private static string Route(bool ruleEnabled, string targetId, OnTargetDown mode, NetworkSnapshot snap, IReadOnlyList<Bond> bonds) =>
        !BondTargets.IsBond(targetId) ? RouteNow(ruleEnabled, targetId, mode, snap)
        : !ruleEnabled ? "Off"
        : BondRoute(ResolveBonds(bonds.Where(b => b.Id == BondTargets.BondId(targetId)), snap).FirstOrDefault(), mode, snap);

    private static bool BondUsable(string targetId, NetworkSnapshot snap, IReadOnlyList<Bond> bonds) =>
        bonds.FirstOrDefault(b => b.Id == BondTargets.BondId(targetId)) is { } b && BondRules.Usable(b, BondAdapters(snap)).Count > 0;

    /// <summary>
    /// After each apply: when a bond member's usability (LEO's own health) changes, ask the engine to re-check that bond at
    /// once. Only when a member is really gone (disconnected, or failing long enough that failover demoted it) close the
    /// bond's connections on it so apps reconnect over the others (LEO.md §22.3). A single failed probe on a busy link is
    /// not a loss: found live, closing on it killed healthy downloads (§23).
    /// </summary>
    private async Task WatchBondMembersAsync(EnginePlan plan, bool fresh, CancellationToken ct)
    {
        var snap = networks.Snapshot;
        var adapters = BondAdapters(snap);
        var healthyIds = snap.Adapters.Where(a => a.IsPrimaryCandidate && a.Status == AdapterStatus.Up && !a.IsDemoted)
            .Select(a => a.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var usableNow = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var healthyNow = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var pb in plan.Bonds)
            if (_bonds.FirstOrDefault(b => b.Id == pb.BondId) is { } b)
            {
                usableNow[pb.BondId] = BondRules.Usable(b, adapters).Select(a => a.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
                healthyNow[pb.BondId] = b.Members.Select(m => m.AdapterId).Where(healthyIds.Contains).ToHashSet(StringComparer.OrdinalIgnoreCase);
            }
        var usableBefore = _bondUsable;
        var healthyBefore = _bondHealthy;
        _bondUsable = usableNow;
        _bondHealthy = healthyNow;
        if (fresh) return;
        foreach (var (id, healthy) in healthyNow)
        {
            var usableChanged = usableBefore.TryGetValue(id, out var wasUsable) && !wasUsable.SetEquals(usableNow[id]);
            var lost = healthyBefore.TryGetValue(id, out var wasHealthy) ? wasHealthy.Except(healthy, StringComparer.OrdinalIgnoreCase).ToList() : [];
            if (!usableChanged && lost.Count == 0) continue;
            var group = EnginePlan.BondGroupName(id);
            var set = usableNow[id];
            try
            {
                if (usableChanged) await engine.CheckGroupAsync(group, plan.BondCheck.Url, ct);
                if (lost.Count == 0) continue;
                var lostOutbounds = lost.Select(l => EnginePlan.OutboundName(l)).ToHashSet(StringComparer.Ordinal);
                var conns = await engine.GetConnectionsAsync(ct);
                var closing = conns.Where(c => c.Chains.Count > 0 && lostOutbounds.Contains(c.Chains[0]) && c.Chains.Contains(group)).ToList();
                foreach (var c in closing) await engine.CloseConnectionAsync(c.Id, ct);
                log.LogInformation("Bond {Id}: {Usable} of {Total} usable; closed {Count} connection(s) on the lost network(s)",
                    id, set.Count, _bonds.FirstOrDefault(b => b.Id == id)?.Members.Count ?? 0, closing.Count);
                if (_bonds.FirstOrDefault(b => b.Id == id) is { } bond)
                    foreach (var l in lost)
                    {
                        var name = adapters.FirstOrDefault(a => a.Id.Equals(l, StringComparison.OrdinalIgnoreCase))?.Name ?? "A network";
                        var rest = adapters.Where(a => set.Contains(a.Id)).Select(a => a.Name).ToList();
                        notifications.Publish(NotificationKind.BondMemberLost, $"Bond '{bond.Name}'",
                            rest.Count > 0 ? $"{name} stopped working. The bond keeps going on {string.Join(" and ", rest)}."
                                : $"{name} stopped working, and no other network in the bond is available.", l);
                    }
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                log.LogInformation("Bond {Id} check failed: {Message}", id, ex.Message);
            }
        }
    }

    private List<Bond> _bonds = [];
    private string? _unruledBondSetting;
    private EnginePlan? _lastPlan;
    private Dictionary<string, HashSet<string>> _bondUsable = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, HashSet<string>> _bondHealthy = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Rules that target a VPN find it again when it came back as a new adapter (same key; LEO.md §20.2).</summary>
    private static void ResolveVpnTargets(IEnumerable<AppRule> apps, IEnumerable<DestinationRule> dests, NetworkSnapshot snap)
    {
        var present = snap.Adapters.Select(a => a.Id).ToList();
        var vpns = snap.Adapters.Where(a => a.Vpn is not null).Select(a => new VpnCandidate(a.Id, a.Vpn!.Key, a.Vpn.Connected)).ToList();
        foreach (var r in apps) r.TargetAdapterId = VpnIdentity.Resolve(r.TargetAdapterId, r.TargetVpnKey, present, vpns);
        foreach (var d in dests) d.TargetAdapterId = VpnIdentity.Resolve(d.TargetAdapterId, d.TargetVpnKey, present, vpns);
    }

    /// <summary>
    /// "Ethernet", "WiFi (fallback: Ethernet is unavailable)", "Blocked (Ethernet is unavailable)";
    /// VPNs: "Tailscale (this VPN reports no internet access)", "Blocked (Tailscale is not connected)".
    /// </summary>
    private static string RouteNow(bool ruleEnabled, string targetId, OnTargetDown mode, NetworkSnapshot snap)
    {
        var target = snap.Adapters.FirstOrDefault(a => a.Id.Equals(targetId, StringComparison.OrdinalIgnoreCase));
        var usable = target is not null && EnginePlan.IsUsable(ToEngine(target));
        var targetName = target?.DisplayName ?? "its network";
        var down = target?.Kind == AdapterKind.Vpn ? "is not connected" : "is unavailable";
        var primary = snap.Adapters.FirstOrDefault(a => a.IsPrimary);
        return !ruleEnabled ? "Off"
            : usable ? targetName + (target!.Vpn is { Internet: false } ? " (this VPN reports no internet access)" : "")
            : mode == OnTargetDown.Block ? $"Blocked ({targetName} {down})"
            : $"{primary?.DisplayName ?? "Primary"} (fallback: {targetName} {down})";
    }

    private static EngineAdapter ToEngine(AdapterInfo a) => a.Kind == AdapterKind.Vpn
        ? new(a.Id, a.Name, false, a.Vpn?.Connected ?? false, false, a.Health, a.DnsServers, IsVpn: true,
            TakesDefault: a.Vpn?.TakesDefault4 ?? false, SkipUnruled: a.Vpn?.SkipUnruled ?? false, RouteMetric: a.Vpn?.RouteMetric ?? int.MaxValue)
        : new(a.Id, a.Name, a.IsPrimaryCandidate, a.Status == AdapterStatus.Up, a.IsDemoted, a.Health, a.DnsServers);

    // ── Lifecycle ────────────────────────────────────────────────────────────

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        networks.Changed += OnNetworksChanged;
        children.ChildrenChanged += OnSomethingChanged;
        engine.UnexpectedExit += OnEngineCrashed;
        if (vpn is not null) vpn.Changed += OnVpnChanged;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await SyncAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    log.LogError(ex, "Split tunneling sync failed");
                }

                try
                {
                    if (await _signal.WaitAsync(TimeSpan.FromSeconds(10), stoppingToken))
                    {
                        await Task.Delay(300, stoppingToken);
                        while (_signal.CurrentCount > 0) _signal.Wait(0);
                    }
                }
                catch (OperationCanceledException) { break; }
            }
        }
        finally
        {
            networks.Changed -= OnNetworksChanged;
            children.ChildrenChanged -= OnSomethingChanged;
            engine.UnexpectedExit -= OnEngineCrashed;
            if (vpn is not null) vpn.Changed -= OnVpnChanged;
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        await engine.StopAsync(CancellationToken.None); // never leave the tunnel behind
        children.Dispose();
    }

    private void OnSomethingChanged(object? sender, EventArgs e) => _signal.Release();

    private void OnNetworksChanged(object? sender, NetworkSnapshot e) => _signal.Release();

    private void OnVpnChanged(object? sender, VpnPicture e) => _signal.Release();

    private void OnEngineCrashed(object? sender, int code)
    {
        var now = DateTimeOffset.Now;
        lock (_crashes)
        {
            _crashes.RemoveAll(t => now - t > CrashWindow);
            _crashes.Add(now);
            if (_crashes.Count >= Backoff.Length)
            {
                _gaveUp = true;
                notifications.Publish(NotificationKind.NoInternetAnywhere, "Split tunneling stopped",
                    "LEO's routing engine stopped repeatedly, so LEO switched split tunneling off for now. Your normal networking is unaffected. Turn it back on from the Apps page.");
            }
            else
            {
                _retryAt = now + Backoff[_crashes.Count - 1];
            }
        }
        _structureKey = null;
        _signal.Release();
    }

    // ── Sync ────────────────────────────────────────────────────────────────

    private async Task SyncAsync(CancellationToken ct)
    {
        await _syncGate.WaitAsync(ct);
        try
        {
            var (enabled, rules, dests, dns) = config.Read(c => (c.SplitTunnel.Enabled, c.SplitTunnel.AppRules.Select(Clone).ToList(),
                c.SplitTunnel.DestinationRules.Select(Clone).ToList(),
                c.SplitTunnel.NetworkDns.Select(d => new NetworkDnsSetting { AdapterId = d.AdapterId, Servers = [.. d.Servers] }).ToList()));
            ResolveVpnTargets(rules, dests, networks.Snapshot);
            _dests = dests;
            ReconcileFailClosed(enabled, rules);
            _dns = dns;
            var (bonds, unruledBond) = config.Read(c => (c.SplitTunnel.Bonds.Select(b => b.Clone()).ToList(), c.SplitTunnel.UnruledBondId));
            _bonds = ResolveBonds(bonds, networks.Snapshot);
            _unruledBondSetting = unruledBond is not null && _bonds.Any(b => b.Id == unruledBond) ? unruledBond : null;
            // Rules pointing at a deleted bond aren't active at all (not "fall back"/"block"): shown "Not active".
            bool BondKnown(string target) => !BondTargets.IsBond(target) || _bonds.Any(b => b.Id == BondTargets.BondId(target));
            var planRules = rules.Where(r => BondKnown(r.TargetAdapterId)).ToList();
            var planDests = dests.Where(d => BondKnown(d.TargetAdapterId)).ToList();

            var fingerprint = enabled + "|" + string.Join("|", rules.Select(r => $"{r.Id}:{r.Enabled}:{r.IncludeChildren}:{r.Kind}:{r.Pattern}"));
            if (fingerprint != _rulesFingerprint)
            {
                _rulesFingerprint = fingerprint;
                children.SetRules(enabled ? rules : []);
            }

            var wanted = enabled && (planRules.Any(r => r.Enabled) || DestinationMatcher.Ordered(planDests, planRules).Count > 0
                                     || _unruledBondSetting is not null);
            if (!wanted)
            {
                if (engine.IsRunning)
                {
                    // Tell the UI right away; stopping waits for Windows to remove the engine's adapter (seconds).
                    SetState(SplitTunnelEngineState.Off, enabled ? null : "Split tunneling is switched off.", rules);
                    await engine.StopAsync(ct);
                }
                _structureKey = null;
                _unruledVpnId = null;
                _lastPlan = null;
                SetState(SplitTunnelEngineState.Off, enabled ? null : "Split tunneling is switched off.", rules);
                return;
            }
            if (!engine.IsInstalled)
            {
                SetState(SplitTunnelEngineState.NotInstalled, "The routing engine isn't installed, so app rules can't be applied.", rules);
                return;
            }
            if (_gaveUp)
            {
                if (engine.IsRunning) await engine.StopAsync(ct);
                SetState(SplitTunnelEngineState.Error, _routesPaused
                    ? "Another program keeps changing routes, so split tunneling is paused. Switch it off and on to retry."
                    : "The routing engine kept stopping, so split tunneling is paused. Switch it off and on to retry.", rules);
                return;
            }

            // A VPN whose firewall only allows its own tunnel blocks every app sending into LEO's adapter: stand aside
            // (normal Windows routing = everything through the VPN) until that VPN disconnects (LEO.md §20.4).
            var picture = vpn?.Picture ?? VpnPicture.Empty;
            if (picture.Firewall.Verdict == KillSwitchVerdict.BlocksLeo)
            {
                var name = VpnName(picture.Firewall.VpnId, picture.FirewallOwner);
                var message = $"Split tunneling paused: {name} only allows its own tunnel. It resumes when {name} disconnects.";
                if (!_stoodAside)
                {
                    _stoodAside = true;
                    log.LogWarning("Standing aside: VPN {Id} blocks LEO's adapter", picture.Firewall.VpnId);
                    notifications.Publish(NotificationKind.SplitTunnelStoodAside, "Split tunneling paused", message, picture.Firewall.VpnId);
                }
                if (engine.IsRunning)
                {
                    SetState(SplitTunnelEngineState.StoodAside, message, rules);
                    await engine.StopAsync(ct);
                }
                _structureKey = null;
                _unruledVpnId = null;
                SetState(SplitTunnelEngineState.StoodAside, message, rules);
                return;
            }
            if (_stoodAside)
            {
                _stoodAside = false;
                log.LogInformation("The VPN that blocked LEO disconnected; resuming split tunneling");
                notifications.Publish(NotificationKind.SplitTunnelResumed, "Split tunneling resumed",
                    "The VPN that only allowed its own tunnel disconnected, so LEO's rules apply again.");
            }

            // Another program removed LEO's routes: restart once; if it happens again soon, pause (LEO.md §20.3).
            if (engine.IsRunning && !ReferenceEquals(picture, _lastPicture))
            {
                _lastPicture = picture;
                if (picture.EngineRoutesPresent == false && DateTimeOffset.Now - _engineSince > TimeSpan.FromSeconds(15)) _routesMissing++;
                else if (picture.EngineRoutesPresent == true) _routesMissing = 0;
                if (_routesMissing >= 2)
                {
                    _routesMissing = 0;
                    var now = DateTimeOffset.Now;
                    _tampers.RemoveAll(t => now - t > CrashWindow);
                    _tampers.Add(now);
                    await engine.StopAsync(ct);
                    _structureKey = null;
                    _unruledVpnId = null;
                    if (_tampers.Count >= 2)
                    {
                        _gaveUp = _routesPaused = true;
                        log.LogWarning("LEO's routes were removed again; pausing split tunneling");
                        notifications.Publish(NotificationKind.RoutesTampered, "Split tunneling paused",
                            "Another program keeps changing routes, so LEO paused split tunneling. Your normal networking is unaffected. Turn it back on from the Apps page.");
                        SetState(SplitTunnelEngineState.Error, "Another program keeps changing routes, so split tunneling is paused. Switch it off and on to retry.", rules);
                        return;
                    }
                    log.LogWarning("LEO's routes were removed by another program; restarting the engine");
                }
            }

            var plan = BuildPlan(planRules, planDests, dns, picture);
            var fresh = false;
            if (!engine.IsRunning)
            {
                if (DateTimeOffset.Now < _retryAt)
                {
                    SetState(SplitTunnelEngineState.Error, "The routing engine stopped; LEO restarts it in a moment.", rules);
                    return;
                }
                SetState(SplitTunnelEngineState.Starting, null, rules);
                try
                {
                    await engine.StartAsync(plan, ct);
                    fresh = true;
                    _structureKey = plan.StructureKey;
                    _engineSince = DateTimeOffset.Now;
                    _unruledVpnId = null;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    log.LogError(ex, "Engine start failed");
                    await engine.StopAsync(ct);
                    OnEngineCrashed(this, -1);
                    SetState(SplitTunnelEngineState.Error, $"The routing engine couldn't start: {ex.Message}", rules);
                    return;
                }
            }
            else if (plan.StructureKey != _structureKey)
            {
                await engine.ReloadAsync(plan, ct);
                _structureKey = plan.StructureKey;
                _engineSince = DateTimeOffset.Now;
            }
            else
            {
                await engine.UpdateProvidersAsync(plan, ct);
                await engine.ApplySelectionsAsync(plan, ct);
            }

            if (!string.Equals(plan.UnruledVpnId, _unruledVpnId, StringComparison.OrdinalIgnoreCase))
            {
                var previous = _unruledVpnId;
                _unruledVpnId = plan.UnruledVpnId;
                log.LogInformation("Unruled traffic now follows {Target}", plan.UnruledVpnId is null ? "the Primary" : $"VPN {plan.UnruledVpnId}");
                if (plan.UnruledVpnId is not null && previous is null) await CloseUnruledPhysicalAsync(ct);
            }

            _lastPlan = plan;
            await WatchBondMembersAsync(plan, fresh, ct);
            SetState(SplitTunnelEngineState.Running, null, rules);
        }
        finally
        {
            _syncGate.Release();
        }
    }

    /// <summary>
    /// A VPN started taking unruled traffic: close the engine's open unruled connections that run over a physical network,
    /// so long-lived ones reconnect through the VPN (LEO.md §20.3). Ruled connections are left alone. Counts only in the log.
    /// </summary>
    private async Task CloseUnruledPhysicalAsync(CancellationToken ct)
    {
        try
        {
            var conns = await engine.GetConnectionsAsync(ct);
            var closing = conns.Where(c => c.Rule.Equals("Match", StringComparison.OrdinalIgnoreCase)
                                           && c.Chains.Count > 0 && c.Chains[0].StartsWith("net-", StringComparison.Ordinal)).ToList();
            foreach (var c in closing) await engine.CloseConnectionAsync(c.Id, ct);
            if (closing.Count > 0) log.LogInformation("Closed {Count} unruled connection(s) so they reconnect through the VPN", closing.Count);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
            log.LogInformation("Closing unruled connections failed: {Message}", ex.Message);
        }
    }

    private string VpnName(string? vpnId, string? owner) =>
        owner ?? networks.Snapshot.Adapters.FirstOrDefault(a => a.Id.Equals(vpnId, StringComparison.OrdinalIgnoreCase))?.DisplayName ?? "Your VPN";

    // ── Fail-closed firewall rules (LEO.md §20.7 Q1, §21) ──────────────────────

    private string? _firewallKey;
    private DateTimeOffset _firewallCheckedAt = DateTimeOffset.MinValue;

    /// <summary>
    /// Keeps LEO's own firewall group equal to the "VPN, else block" app rules: each such app is blocked from public
    /// addresses on the physical adapters, so it can't go out directly even while the engine isn't running. Only rules in
    /// LEO's group are ever changed. Re-checked when the wanted set changes and every 5 minutes (someone may delete them).
    /// </summary>
    private void ReconcileFailClosed(bool enabled, IReadOnlyList<AppRule> rules)
    {
        if (!OperatingSystem.IsWindows() || !ManageFirewall) return;
        var snap = networks.Snapshot;
        var wanted = FailClosedRules.Plan(enabled, rules, id => snap.Adapters.Any(a => a.Id.Equals(id, StringComparison.OrdinalIgnoreCase) && a.Kind == AdapterKind.Vpn));
        var aliases = snap.Adapters.Where(a => a.IsPrimaryCandidate).Select(a => a.Name).Order(StringComparer.OrdinalIgnoreCase).ToList();
        var key = string.Join("|", wanted.Select(w => w.Name + "=" + w.ProgramPath)) + "#" + string.Join(",", aliases);
        if (key == _firewallKey && DateTimeOffset.Now - _firewallCheckedAt < TimeSpan.FromMinutes(5)) return;
        try
        {
            var remote = FailClosedRules.PublicRemoteAddresses();
            var (added, removed) = Leo.Service.Interop.FirewallRules.Reconcile(
                wanted.Select(w => new Leo.Service.Interop.LeoFirewallRule(w.Name, w.ProgramPath, aliases, remote)).ToList());
            if (added + removed > 0) log.LogInformation("Fail-closed firewall rules: {Count} wanted, {Added} added, {Removed} removed", wanted.Count, added, removed);
            _firewallKey = key;
            _firewallCheckedAt = DateTimeOffset.Now;
        }
        catch (Exception ex)
        {
            log.LogWarning("Updating LEO's firewall rules failed: {Message}", ex.Message);
            _firewallCheckedAt = DateTimeOffset.Now; // retry in 5 minutes, not every sync
            _firewallKey = key;
        }
    }

    /// <summary>Tests turn this off so they never touch the real firewall.</summary>
    public bool ManageFirewall { get; init; } = true;

    private bool _stoodAside;
    private bool _routesPaused;
    private string? _unruledVpnId;
    private VpnPicture? _lastPicture;
    private int _routesMissing;
    private DateTimeOffset _engineSince = DateTimeOffset.MaxValue;
    private readonly List<DateTimeOffset> _tampers = [];

    private EnginePlan BuildPlan(IReadOnlyList<AppRule> rules, IReadOnlyList<DestinationRule> dests, IReadOnlyList<NetworkDnsSetting> dns,
        VpnPicture picture)
    {
        var snap = networks.Snapshot;
        var adapters = snap.Adapters.Where(a => a.IsPrimaryCandidate || a.Kind == AdapterKind.Vpn).Select(ToEngine).ToList();
        var check = BondHealthCheck.From(config.Read(c => c.Health.ProbeTargets.Select(t => (t.Url, t.Expect)).ToList()));
        return EnginePlan.Build(adapters, snap.PrimaryAdapterId, rules, children.Children, dests, dns, picture.Nrpt,
            _bonds, _unruledBondSetting, check);
    }

    private List<DestinationRule> _dests = [];
    private List<NetworkDnsSetting> _dns = [];

    private void SetState(SplitTunnelEngineState state, string? message, IReadOnlyList<AppRule> rules)
    {
        if (state != _state) log.LogInformation("Split tunneling: {Old} -> {New}{Message}", _state, state, message is null ? "" : $" ({message})");
        _state = state;
        _message = message;

        var snap = networks.Snapshot;
        var childCounts = children.Children.GroupBy(c => c.ParentRuleId).ToDictionary(g => g.Key, g => g.Count());
        var infos = rules.Select(r =>
        {
            var target = snap.Adapters.FirstOrDefault(a => a.Id.Equals(r.TargetAdapterId, StringComparison.OrdinalIgnoreCase));
            var isBond = BondTargets.IsBond(r.TargetAdapterId);
            var usable = isBond ? BondUsable(r.TargetAdapterId, snap, _bonds) : target is not null && EnginePlan.IsUsable(ToEngine(target));
            var route = !r.Enabled ? "Off"
                : isBond && !_bonds.Any(b => b.Id == BondTargets.BondId(r.TargetAdapterId)) ? "Not active (its bond was removed)"
                : state != SplitTunnelEngineState.Running ? "Not active"
                : Route(true, r.TargetAdapterId, r.OnTargetDown, snap, _bonds);
            return new AppRuleInfo(r.Id, r.DisplayName, r.Kind, r.Pattern, r.TargetAdapterId, r.OnTargetDown,
                r.IncludeChildren, r.Enabled, route, usable, childCounts.GetValueOrDefault(r.Id));
        }).ToList();

        var ordered = DestinationMatcher.Ordered(_dests, rules).ToList();
        var destInfos = _dests.Select(d =>
        {
            var target = snap.Adapters.FirstOrDefault(a => a.Id.Equals(d.TargetAdapterId, StringComparison.OrdinalIgnoreCase));
            var isBond = BondTargets.IsBond(d.TargetAdapterId);
            var usable = isBond ? BondUsable(d.TargetAdapterId, snap, _bonds) : target is not null && EnginePlan.IsUsable(ToEngine(target));
            var app = d.AppRuleId is null ? null : rules.FirstOrDefault(a => a.Id == d.AppRuleId);
            var rank = ordered.FindIndex(o => o.Id == d.Id) + 1;
            var route = !d.Enabled ? "Off"
                : isBond && !_bonds.Any(b => b.Id == BondTargets.BondId(d.TargetAdapterId)) ? "Not active (its bond was removed)"
                : d.AppRuleId is not null && app is null ? "Not active (its app rule was removed)"
                : app is { Enabled: false } ? $"Not active ({app.DisplayName}'s rule is off)"
                : state != SplitTunnelEngineState.Running ? "Not active"
                : Route(true, d.TargetAdapterId, d.OnTargetDown, snap, _bonds);
            return new DestinationRuleInfo(d.Id, d.DisplayName, d.Kind, d.Value, d.Ports, d.Protocol, d.AppRuleId, app?.DisplayName,
                d.TargetAdapterId, d.OnTargetDown, d.Enabled, DestinationMatcher.Describe(d), route, usable, rank);
        }).ToList();
        var dnsInfos = snap.Adapters.Where(a => a.IsPrimaryCandidate).OrderBy(a => a.PriorityIndex ?? int.MaxValue).Select(a =>
        {
            var custom = _dns.FirstOrDefault(d => d.AdapterId.Equals(a.Id, StringComparison.OrdinalIgnoreCase))?.Servers ?? [];
            return new NetworkDnsInfo(a.Id, a.DisplayName, a.Status == AdapterStatus.Up, custom, a.DnsServers,
                EnginePlan.EffectiveDns(ToEngine(a), _dns));
        }).ToList();

        // M6: which VPN unruled traffic follows, and a banner when a VPN's firewall interferes.
        var unruledVia = state == SplitTunnelEngineState.Running && _unruledVpnId is not null
            ? snap.Adapters.FirstOrDefault(a => a.Id.Equals(_unruledVpnId, StringComparison.OrdinalIgnoreCase))?.DisplayName : null;
        var picture = vpn?.Picture ?? VpnPicture.Empty;
        var banner = state == SplitTunnelEngineState.StoodAside ? message
            : picture.Firewall.Verdict == KillSwitchVerdict.BlocksBypass
                ? $"{VpnName(picture.Firewall.VpnId, picture.FirewallOwner)} blocks traffic outside it. Rules that bypass it can't connect while it's on."
                : null;

        // M7: bonds with their members and route.
        var bondAdapters = BondAdapters(snap);
        var inEngine = state == SplitTunnelEngineState.Running ? _lastPlan?.Bonds.Select(b => b.BondId).ToHashSet(StringComparer.OrdinalIgnoreCase) : null;
        var bondInfos = _bonds.Select(b =>
        {
            var members = BondRules.Members(b, bondAdapters).Select(x => new BondMemberInfo(x.Member.AdapterId,
                x.Adapter?.Name ?? "Not plugged in", x.Member.Weight, x.Adapter is not null, x.Adapter?.Connected ?? false,
                x.Adapter?.Usable ?? false, x.Adapter?.Metered ?? false)).ToList();
            var target = BondTargets.For(b.Id);
            var ruleCount = rules.Count(r => r.Enabled && r.TargetAdapterId == target) + _dests.Count(d => d.Enabled && d.TargetAdapterId == target);
            var hints = new List<string>();
            if (BondRules.SameRouterHint(b, bondAdapters) is { } same) hints.Add(same);
            if (BondRules.Members(b, bondAdapters).Any(x => x.Adapter is { Connected: true }
                    && snap.Adapters.FirstOrDefault(a => a.Id == x.Adapter.Id)?.IPv6.Any(ip => !ip.StartsWith("fe80", StringComparison.OrdinalIgnoreCase)) == true))
                hints.Add("IPv6 isn't bonded: it follows Windows' routes (your Primary network).");
            return new BondInfo(b.Id, b.Name, b.Mode, b.OnAllDown, members, members.Count(m => m.Usable), ruleCount,
                inEngine?.Contains(b.Id) == true, BondRoute(b, b.OnAllDown, snap), hints);
        }).ToList();
        var unruledBondNow = state == SplitTunnelEngineState.Running && _lastPlan?.UnruledBondId is { } ub
            ? _bonds.FirstOrDefault(b => b.Id == ub)?.Name : null;

        var info = new SplitTunnelInfo(config.Read(c => c.SplitTunnel.Enabled), state, message, infos, destInfos, dnsInfos, unruledVia, banner,
            bondInfos, _unruledBondSetting, unruledBondNow);
        if (!SameInfo(info, _info))
        {
            Volatile.Write(ref _info, info);
            Changed?.Invoke(this, info);
        }
    }

    private static bool SameInfo(SplitTunnelInfo a, SplitTunnelInfo b) =>
        System.Text.Json.JsonSerializer.Serialize(a) == System.Text.Json.JsonSerializer.Serialize(b);

    private static DestinationRule Clone(DestinationRule r) => new()
    {
        Id = r.Id, DisplayName = r.DisplayName, Kind = r.Kind, Value = r.Value, Ports = r.Ports, Protocol = r.Protocol,
        AppRuleId = r.AppRuleId, TargetAdapterId = r.TargetAdapterId, TargetVpnKey = r.TargetVpnKey, OnTargetDown = r.OnTargetDown, Enabled = r.Enabled,
    };

    private static AppRule Clone(AppRule r) => new()
    {
        Id = r.Id, DisplayName = r.DisplayName, Kind = r.Kind, Pattern = r.Pattern, TargetAdapterId = r.TargetAdapterId, TargetVpnKey = r.TargetVpnKey,
        OnTargetDown = r.OnTargetDown, IncludeChildren = r.IncludeChildren, Enabled = r.Enabled,
    };
}
