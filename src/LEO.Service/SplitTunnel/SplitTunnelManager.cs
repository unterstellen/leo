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
            var (enabled, rules) = config.Read(c => (c.SplitTunnel.Enabled, c.SplitTunnel.AppRules.Select(Clone).ToList()));

            var fingerprint = enabled + "|" + string.Join("|", rules.Select(r => $"{r.Id}:{r.Enabled}:{r.IncludeChildren}:{r.Kind}:{r.Pattern}"));
            if (fingerprint != _rulesFingerprint)
            {
                _rulesFingerprint = fingerprint;
                children.SetRules(enabled ? rules : []);
            }

            var wanted = enabled && rules.Any(r => r.Enabled);
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

            var plan = BuildPlan(rules);
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

    private EnginePlan BuildPlan(IReadOnlyList<AppRule> rules)
    {
        var snap = networks.Snapshot;
        var adapters = snap.Adapters.Select(a => new EngineAdapter(a.Id, a.Name, a.IsPrimaryCandidate,
            a.Status == AdapterStatus.Up, a.IsDemoted, a.Health)).ToList();
        return EnginePlan.Build(adapters, snap.PrimaryAdapterId, rules, children.Children);
    }

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

        var info = new SplitTunnelInfo(config.Read(c => c.SplitTunnel.Enabled), state, message, infos);
        if (!SameInfo(info, _info))
        {
            Volatile.Write(ref _info, info);
            Changed?.Invoke(this, info);
        }
    }

    private static bool SameInfo(SplitTunnelInfo a, SplitTunnelInfo b) =>
        a.Enabled == b.Enabled && a.State == b.State && a.Message == b.Message && a.Rules.SequenceEqual(b.Rules);

    private static AppRule Clone(AppRule r) => new()
    {
        Id = r.Id, DisplayName = r.DisplayName, Kind = r.Kind, Pattern = r.Pattern, TargetAdapterId = r.TargetAdapterId,
        OnTargetDown = r.OnTargetDown, IncludeChildren = r.IncludeChildren, Enabled = r.Enabled,
    };
}
