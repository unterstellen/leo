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
    NotificationHub notifications, ILogger<SplitTunnelManager> log) : BackgroundService
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

    public async Task SetEnabledAsync(bool enabled, CancellationToken ct)
    {
        config.Update(c =>
        {
            if (c.SplitTunnel.Enabled == enabled) return false;
            c.SplitTunnel.Enabled = enabled;
            return true;
        });
        if (enabled) { _gaveUp = false; _crashes.Clear(); }
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
        var target = networks.Snapshot.Adapters.FirstOrDefault(a => a.Id.Equals(rule.TargetAdapterId, StringComparison.OrdinalIgnoreCase));
        if (target is not null && !target.IsPrimaryCandidate) throw new ArgumentException($"{target.DisplayName} can't carry app traffic.");
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
        _gaveUp = false; // a user change deserves a fresh attempt
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
        var target = networks.Snapshot.Adapters.FirstOrDefault(a => a.Id.Equals(rule.TargetAdapterId, StringComparison.OrdinalIgnoreCase));
        if (target is not null && !target.IsPrimaryCandidate) throw new ArgumentException($"{target.DisplayName} can't carry this traffic.");
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
        _gaveUp = false;
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

        var (apps, dests, enabled) = config.Read(c => (c.SplitTunnel.AppRules.Select(Clone).ToList(),
            c.SplitTunnel.DestinationRules.Select(Clone).ToList(), c.SplitTunnel.Enabled));
        var query = new RouteQuery(host, addresses, port, DestinationProtocol.Tcp, appPath);
        var decision = DestinationMatcher.Decide(dests, apps, query);
        var snap = networks.Snapshot;
        var primaryName = snap.Adapters.FirstOrDefault(a => a.IsPrimary)?.DisplayName ?? "your Primary network";

        string ruleText, network, why;
        if (decision.Destination is { } d)
        {
            var app = d.AppRuleId is null ? null : apps.FirstOrDefault(a => a.Id == d.AppRuleId);
            ruleText = "Rule: " + DestinationMatcher.Describe(d) + (app is null ? "" : $" from {app.DisplayName}");
            network = RouteNow(d.Enabled, d.TargetAdapterId, d.OnTargetDown, snap);
            why = d.Kind == DestinationKind.IpRange && !IPAddress.TryParse(host, out _)
                ? $"{host} resolves to {string.Join(", ", addresses.Where(a => DestinationMatcher.TryParseRange(d.Value, out var n, out var p) && DestinationMatcher.InRange(a, n, p)))}, which is in {d.Value}."
                : app is not null ? "This app + destination combo beats every other rule."
                : "Destination rules beat app rules; the most specific destination rule wins.";
        }
        else if (decision.App is { } a)
        {
            ruleText = $"App rule: {a.DisplayName}";
            network = RouteNow(a.Enabled, a.TargetAdapterId, a.OnTargetDown, snap);
            why = "No destination rule matches, so the app's rule decides.";
        }
        else
        {
            ruleText = "No rule";
            network = primaryName;
            why = appPath is null ? "No destination rule matches, so it uses your Primary network." : "Neither a destination nor an app rule matches, so it uses your Primary network.";
        }
        if (!enabled) { network = primaryName; why = "Split tunneling is switched off, so everything uses your Primary network. " + why; }
        else if (_state != SplitTunnelEngineState.Running && (decision.Destination is not null || decision.App is not null))
            why += " (The routing engine isn't running right now.)";
        if (guessed) why += $" Port {port} assumed.";
        if (lookupNote is not null) why += " " + lookupNote;

        return new RouteTestResult(host, port, guessed, addresses.Select(x => x.ToString()).ToList(),
            decision.Destination?.Id, decision.App?.Id, ruleText, network, why.Trim());
    }

    /// <summary>"Ethernet", "WiFi (fallback: Ethernet is unavailable)", "Blocked (Ethernet is unavailable)".</summary>
    private static string RouteNow(bool ruleEnabled, string targetId, OnTargetDown mode, NetworkSnapshot snap)
    {
        var target = snap.Adapters.FirstOrDefault(a => a.Id.Equals(targetId, StringComparison.OrdinalIgnoreCase));
        var usable = target is not null && EnginePlan.IsUsable(ToEngine(target));
        var targetName = target?.DisplayName ?? "its network";
        var primary = snap.Adapters.FirstOrDefault(a => a.IsPrimary);
        return !ruleEnabled ? "Off"
            : usable ? targetName
            : mode == OnTargetDown.Block ? $"Blocked ({targetName} is unavailable)"
            : $"{primary?.DisplayName ?? "Primary"} (fallback: {targetName} is unavailable)";
    }

    private static EngineAdapter ToEngine(AdapterInfo a) =>
        new(a.Id, a.Name, a.IsPrimaryCandidate, a.Status == AdapterStatus.Up, a.IsDemoted, a.Health, a.DnsServers);

    // ── Lifecycle ────────────────────────────────────────────────────────────

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        networks.Changed += OnNetworksChanged;
        children.ChildrenChanged += OnSomethingChanged;
        engine.UnexpectedExit += OnEngineCrashed;
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
            _dests = dests;
            _dns = dns;

            var fingerprint = enabled + "|" + string.Join("|", rules.Select(r => $"{r.Id}:{r.Enabled}:{r.IncludeChildren}:{r.Kind}:{r.Pattern}"));
            if (fingerprint != _rulesFingerprint)
            {
                _rulesFingerprint = fingerprint;
                children.SetRules(enabled ? rules : []);
            }

            var wanted = enabled && (rules.Any(r => r.Enabled) || DestinationMatcher.Ordered(dests, rules).Count > 0);
            if (!wanted)
            {
                if (engine.IsRunning)
                {
                    // Tell the UI right away; stopping waits for Windows to remove the engine's adapter (seconds).
                    SetState(SplitTunnelEngineState.Off, enabled ? null : "Split tunneling is switched off.", rules);
                    await engine.StopAsync(ct);
                }
                _structureKey = null;
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
                SetState(SplitTunnelEngineState.Error, "The routing engine kept stopping, so split tunneling is paused. Switch it off and on to retry.", rules);
                return;
            }

            var plan = BuildPlan(rules, dests, dns);
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
                    _structureKey = plan.StructureKey;
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
            }
            else
            {
                await engine.UpdateProvidersAsync(plan, ct);
                await engine.ApplySelectionsAsync(plan, ct);
            }

            SetState(SplitTunnelEngineState.Running, null, rules);
        }
        finally
        {
            _syncGate.Release();
        }
    }

    private EnginePlan BuildPlan(IReadOnlyList<AppRule> rules, IReadOnlyList<DestinationRule> dests, IReadOnlyList<NetworkDnsSetting> dns)
    {
        var snap = networks.Snapshot;
        var adapters = snap.Adapters.Select(ToEngine).ToList();
        return EnginePlan.Build(adapters, snap.PrimaryAdapterId, rules, children.Children, dests, dns);
    }

    private List<DestinationRule> _dests = [];
    private List<NetworkDnsSetting> _dns = [];

    private void SetState(SplitTunnelEngineState state, string? message, IReadOnlyList<AppRule> rules)
    {
        if (state != _state) log.LogInformation("Split tunneling: {Old} -> {New}{Message}", _state, state, message is null ? "" : $" ({message})");
        _state = state;
        _message = message;

        var snap = networks.Snapshot;
        var primary = snap.Adapters.FirstOrDefault(a => a.IsPrimary);
        var childCounts = children.Children.GroupBy(c => c.ParentRuleId).ToDictionary(g => g.Key, g => g.Count());
        var infos = rules.Select(r =>
        {
            var target = snap.Adapters.FirstOrDefault(a => a.Id.Equals(r.TargetAdapterId, StringComparison.OrdinalIgnoreCase));
            var usable = target is not null && EnginePlan.IsUsable(new EngineAdapter(target.Id, target.Name, target.IsPrimaryCandidate,
                target.Status == AdapterStatus.Up, target.IsDemoted, target.Health));
            var targetName = target?.DisplayName ?? "its network";
            var route = !r.Enabled ? "Off"
                : state != SplitTunnelEngineState.Running ? "Not active"
                : usable ? targetName
                : r.OnTargetDown == OnTargetDown.Block ? $"Blocked ({targetName} is unavailable)"
                : $"{primary?.DisplayName ?? "Primary"} (fallback: {targetName} is unavailable)";
            return new AppRuleInfo(r.Id, r.DisplayName, r.Kind, r.Pattern, r.TargetAdapterId, r.OnTargetDown,
                r.IncludeChildren, r.Enabled, route, usable, childCounts.GetValueOrDefault(r.Id));
        }).ToList();

        var ordered = DestinationMatcher.Ordered(_dests, rules).ToList();
        var destInfos = _dests.Select(d =>
        {
            var target = snap.Adapters.FirstOrDefault(a => a.Id.Equals(d.TargetAdapterId, StringComparison.OrdinalIgnoreCase));
            var usable = target is not null && EnginePlan.IsUsable(ToEngine(target));
            var app = d.AppRuleId is null ? null : rules.FirstOrDefault(a => a.Id == d.AppRuleId);
            var rank = ordered.FindIndex(o => o.Id == d.Id) + 1;
            var route = !d.Enabled ? "Off"
                : d.AppRuleId is not null && app is null ? "Not active (its app rule was removed)"
                : app is { Enabled: false } ? $"Not active ({app.DisplayName}'s rule is off)"
                : state != SplitTunnelEngineState.Running ? "Not active"
                : RouteNow(true, d.TargetAdapterId, d.OnTargetDown, snap);
            return new DestinationRuleInfo(d.Id, d.DisplayName, d.Kind, d.Value, d.Ports, d.Protocol, d.AppRuleId, app?.DisplayName,
                d.TargetAdapterId, d.OnTargetDown, d.Enabled, DestinationMatcher.Describe(d), route, usable, rank);
        }).ToList();
        var dnsInfos = snap.Adapters.Where(a => a.IsPrimaryCandidate).OrderBy(a => a.PriorityIndex ?? int.MaxValue).Select(a =>
        {
            var custom = _dns.FirstOrDefault(d => d.AdapterId.Equals(a.Id, StringComparison.OrdinalIgnoreCase))?.Servers ?? [];
            return new NetworkDnsInfo(a.Id, a.DisplayName, a.Status == AdapterStatus.Up, custom, a.DnsServers,
                EnginePlan.EffectiveDns(ToEngine(a), _dns));
        }).ToList();

        var info = new SplitTunnelInfo(config.Read(c => c.SplitTunnel.Enabled), state, message, infos, destInfos, dnsInfos);
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
        AppRuleId = r.AppRuleId, TargetAdapterId = r.TargetAdapterId, OnTargetDown = r.OnTargetDown, Enabled = r.Enabled,
    };

    private static AppRule Clone(AppRule r) => new()
    {
        Id = r.Id, DisplayName = r.DisplayName, Kind = r.Kind, Pattern = r.Pattern, TargetAdapterId = r.TargetAdapterId,
        OnTargetDown = r.OnTargetDown, IncludeChildren = r.IncludeChildren, Enabled = r.Enabled,
    };
}
