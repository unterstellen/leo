using Leo.Core.Config;
using Leo.Core.Networking;
using Leo.Core.Profiles;
using Leo.Ipc;
using Leo.Service.Networking;

namespace Leo.Service.Profiles;

/// <summary>
/// Profiles and automatic switching (LEO.md §6.6, §19). Evaluates triggers whenever the networks change, switches
/// with hysteresis (<see cref="ProfileSwitcher"/>), and applies a profile by putting its settings live in config in
/// one atomic save; the network, split-tunneling and Wi-Fi managers then pick the change up as usual.
/// Switching only rewrites pins in config: Wi-Fi profiles are never created or deleted here.
/// </summary>
public sealed class ProfileManager(
    ConfigStore config, NetworkManager networks, NotificationHub notifications, ILogger<ProfileManager> log,
    Leo.Service.Wifi.WifiManager? wifi = null, Leo.Service.SplitTunnel.SplitTunnelManager? splitTunnel = null,
    TimeProvider? time = null) : BackgroundService
{
    private static readonly TimeSpan VisibleRefresh = TimeSpan.FromSeconds(15);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _signal = new(0, int.MaxValue);
    private ProfileSwitcher? _switcherField;
    private IReadOnlySet<string> _visible = new HashSet<string>();
    private DateTimeOffset _visibleAt = DateTimeOffset.MinValue;
    private SwitchDecision? _last;
    private string? _activeReason;
    private ProfilesInfo _info = ProfilesInfo.Empty;

    private ProfileSwitcher Switcher => _switcherField ??= new ProfileSwitcher(_time);

    public event EventHandler<ProfilesInfo>? Changed;

    public ProfilesInfo Info => Volatile.Read(ref _info);

    // ── API ─────────────────────────────────────────────────────────────────

    public async Task<string> SaveAsync(ProfileEdit edit, CancellationToken ct)
    {
        var name = (edit.Name ?? "").Trim();
        if (name.Length is 0 or > 32) throw new ArgumentException("Give the profile a name (up to 32 characters).");
        var triggers = (edit.Triggers ?? []).Select(CleanTrigger).ToList();
        if (triggers.Count > 8) throw new ArgumentException("Use up to 8 triggers per profile.");
        if (triggers.GroupBy(t => (t.Kind, t.Value.ToUpperInvariant())).Any(g => g.Count() > 1))
            throw new ArgumentException("That trigger is listed twice.");

        await _gate.WaitAsync(ct);
        string id;
        try
        {
            id = edit.Id ?? Guid.NewGuid().ToString("N");
            config.Update(c =>
            {
                if (c.Profiles.Any(p => p.Id != id && p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                    throw new ArgumentException($"There's already a profile called “{name}”.");
                var existing = c.Profiles.FirstOrDefault(p => p.Id == id);
                if (edit.Id is not null && existing is null) throw new KeyNotFoundException("That profile doesn't exist anymore.");
                if (existing is null)
                {
                    if (c.Profiles.Count >= 20) throw new ArgumentException("LEO keeps up to 20 profiles.");
                    var settings = ProfileOps.Capture(c);
                    if (edit.StartEmpty)
                        settings = new ProfileSettings { PriorityOrder = settings.PriorityOrder }; // no rules, DNS or pins
                    c.Profiles.Insert(c.Profiles.Count - 1, new Profile { Id = id, Name = name, Triggers = triggers, Settings = settings });
                }
                else
                {
                    existing.Name = name;
                    existing.Triggers = existing.IsDefault ? [] : triggers;
                }
                return true;
            });
        }
        finally { _gate.Release(); }

        // Names and SSIDs are the user's own labels; the log keeps ids and trigger kinds only.
        log.LogInformation("Profile {Id} saved ({Triggers})", id, string.Join(", ", triggers.Select(t => t.Kind)));
        _signal.Release();
        Publish();
        return id;
    }

    public async Task DeleteAsync(string profileId, CancellationToken ct)
    {
        if (profileId == Profile.DefaultId) throw new ArgumentException("The Default profile can't be deleted.");
        await _gate.WaitAsync(ct);
        try
        {
            if (config.Read(c => c.Profiles.All(p => p.Id != profileId))) throw new KeyNotFoundException("That profile doesn't exist anymore.");
            if (config.Read(c => c.ProfileState.ActiveProfileId) == profileId)
            {
                Apply(Profile.DefaultId, manual: false, "the profile that was on was deleted");
            }
            config.Update(c => c.Profiles.RemoveAll(p => p.Id == profileId) > 0);
        }
        finally { _gate.Release(); }
        log.LogInformation("Profile {Id} deleted", profileId);
        _signal.Release();
        Publish();
    }

    public async Task MoveAsync(string profileId, bool up, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var found = false;
            config.Update(c =>
            {
                var i = c.Profiles.FindIndex(p => p.Id == profileId);
                found = i >= 0;
                var j = up ? i - 1 : i + 1;
                if (i < 0 || j < 0 || j >= c.Profiles.Count || c.Profiles[i].IsDefault || c.Profiles[j].IsDefault) return false;
                (c.Profiles[i], c.Profiles[j]) = (c.Profiles[j], c.Profiles[i]);
                return true;
            });
            if (!found) throw new KeyNotFoundException("That profile doesn't exist anymore.");
        }
        finally { _gate.Release(); }
        _signal.Release();
        Publish();
    }

    /// <summary>Manual switch; null = back to automatic (switches to what the triggers say right away).</summary>
    public async Task ActivateAsync(string? profileId, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var choice = ProfileSelector.Choose(config.Read(c => ProfileOps.Clone(c.Profiles)), CurrentConditions());
            if (profileId is null)
            {
                Switcher.ClearManual();
                Switcher.Resume();
                config.Update(c =>
                {
                    if (c.ProfileState.AutoSwitch) return false;
                    c.ProfileState.AutoSwitch = true;
                    return true;
                });
                Apply(choice.ProfileId, manual: false, choice.Reason);
                log.LogInformation("Profiles: back to automatic");
            }
            else
            {
                if (config.Read(c => c.Profiles.All(p => p.Id != profileId))) throw new KeyNotFoundException("That profile doesn't exist anymore.");
                // Picking what the triggers would pick anyway is simply automatic.
                var manual = profileId != choice.ProfileId;
                if (manual) Switcher.NoteManual(choice); else { Switcher.ClearManual(); Switcher.Resume(); }
                Apply(profileId, manual, manual ? null : choice.Reason);
                log.LogInformation("Profile {Id} chosen by the user", profileId);
            }
        }
        finally { _gate.Release(); }
        _signal.Release();
        Publish();
    }

    public async Task SetAutoSwitchAsync(bool enabled, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            config.Update(c =>
            {
                if (c.ProfileState.AutoSwitch == enabled) return false;
                c.ProfileState.AutoSwitch = enabled;
                c.ProfileState.Manual = !enabled; // off: whatever is on now stays, by the user's choice; on: triggers decide
                return true;
            });
            if (enabled) Switcher.Resume();
        }
        finally { _gate.Release(); }
        log.LogInformation("Automatic profile switching {State}", enabled ? "on" : "off");
        _signal.Release();
        Publish();
    }

    // ── Loop ─────────────────────────────────────────────────────────────────

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        networks.Changed += OnNetworksChanged;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await EvaluateAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    log.LogError(ex, "Profile evaluation failed");
                }

                // Tick every second while a switch is pending (countdown), otherwise wait for a change.
                var wait = _last?.PendingProfileId is not null ? TimeSpan.FromSeconds(1) : TimeSpan.FromSeconds(5);
                try
                {
                    if (await _signal.WaitAsync(wait, stoppingToken))
                        while (_signal.CurrentCount > 0) _signal.Wait(0);
                }
                catch (OperationCanceledException) { break; }
            }
        }
        finally
        {
            networks.Changed -= OnNetworksChanged;
        }
    }

    private void OnNetworksChanged(object? sender, NetworkSnapshot e) => _signal.Release();

    private async Task EvaluateAsync(CancellationToken ct)
    {
        var profiles = config.Read(c => ProfileOps.Clone(c.Profiles));
        if (wifi is not null && profiles.Any(p => p.Triggers.Any(t => t.Kind == ProfileTriggerKind.WifiVisible))
            && _time.GetUtcNow() - _visibleAt > VisibleRefresh)
        {
            _visibleAt = _time.GetUtcNow();
            try { _visible = await wifi.GetVisibleSsidsAsync(ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { _visible = new HashSet<string>(); }
        }

        if (networks.Snapshot.TakenAt == DateTimeOffset.MinValue) { Publish(); return; } // first scan not done yet

        await _gate.WaitAsync(ct);
        try
        {
            profiles = config.Read(c => ProfileOps.Clone(c.Profiles));
            var state = config.Read(c => ProfileOps.Clone(c.ProfileState));
            var decision = Switcher.Evaluate(profiles, CurrentConditions(), state);
            _last = decision;
            if (decision.JustPaused)
            {
                log.LogWarning("Profiles kept switching back and forth; automatic switching paused");
                notifications.Publish(NotificationKind.ProfileSwitched, "Automatic profile switching paused",
                    "Profiles kept switching back and forth, so LEO stays on the current profile for now. Choose a profile, or use “Back to automatic” on the Profiles page.");
            }
            if (decision.SwitchTo is { } target)
            {
                var name = profiles.First(p => p.Id == target).Name;
                if (Apply(target, manual: false, decision.Choice.Reason))
                {
                    notifications.Publish(NotificationKind.ProfileSwitched, $"Profile: {name}",
                        $"LEO switched to {name}: {decision.Choice.Reason}.");
                }
            }
        }
        finally { _gate.Release(); }
        Publish();
    }

    /// <summary>Puts a profile live (one atomic config save) and nudges the managers. Call under <see cref="_gate"/>.</summary>
    private bool Apply(string profileId, bool manual, string? reason)
    {
        string? from = null;
        var switched = false;
        config.Update(c =>
        {
            from = c.ProfileState.ActiveProfileId;
            var wasManual = c.ProfileState.Manual;
            var changed = ProfileOps.Switch(c, profileId, manual);
            switched = changed && from != profileId;
            return changed || wasManual != manual;
        });
        _activeReason = reason;
        if (!switched) return false;

        log.LogInformation("Profile {From} -> {To} ({How})", from, profileId, manual ? "by the user" : "automatic");
        networks.RequestRescan();   // priority list
        splitTunnel?.RequestSync(); // app/destination rules, DNS
        wifi?.Wake();               // pins
        return true;
    }

    private NetworkConditions CurrentConditions()
    {
        var snap = networks.Snapshot;
        var connected = snap.Adapters.Where(a => a.Status == AdapterStatus.Up).ToList();
        var ssids = connected.Where(a => a.Kind == AdapterKind.WiFi && a.Network?.Key.StartsWith("wifi:", StringComparison.Ordinal) == true)
            .Select(a => a.Network!.Key["wifi:".Length..]).ToHashSet(StringComparer.Ordinal);
        return new NetworkConditions(ssids, _visible, connected.Select(a => new ConnectedAdapter(a.Id, a.Mac, a.DisplayName)).ToList());
    }

    private static ProfileTrigger CleanTrigger(ProfileTrigger t)
    {
        var value = (t.Value ?? "").Trim();
        if (value.Length == 0) throw new ArgumentException(t.Kind == ProfileTriggerKind.AdapterConnected ? "Choose an adapter." : "Enter a Wi-Fi name.");
        if (t.Kind is ProfileTriggerKind.WifiConnected or ProfileTriggerKind.WifiVisible && System.Text.Encoding.UTF8.GetByteCount(value) > 32)
            throw new ArgumentException("A Wi-Fi name is at most 32 bytes.");
        if (!Enum.IsDefined(t.Kind)) throw new ArgumentException("Unknown trigger.");
        return new ProfileTrigger { Kind = t.Kind, Value = value, Mac = string.IsNullOrWhiteSpace(t.Mac) ? null : t.Mac.Trim(), Label = t.Label?.Trim() };
    }

    // ── Snapshot for the UI ──────────────────────────────────────────────────

    private void Publish()
    {
        var snap = networks.Snapshot;
        var cond = CurrentConditions();
        var decision = _last;
        var info = config.Read(c =>
        {
            var names = c.Adapters.ToDictionary(a => a.Id, a => a.Nickname, StringComparer.OrdinalIgnoreCase);
            string AdapterName(string id) =>
                snap.Adapters.FirstOrDefault(a => a.Id.Equals(id, StringComparison.OrdinalIgnoreCase))?.DisplayName
                ?? names.GetValueOrDefault(id) ?? "Adapter not plugged in";

            var list = c.Profiles.Select(p =>
            {
                var s = ProfileOps.SettingsOf(c, p);
                return new ProfileInfo(p.Id, p.Name, p.IsDefault, p.Id == c.ProfileState.ActiveProfileId,
                    ProfileOps.Clone(p.Triggers), p.Triggers.Select(ProfileSelector.Describe).ToList(),
                    ProfileSelector.AnyTriggerMatches(p, cond), ProfileOps.Summary(s),
                    s.PriorityOrder.Select(AdapterName).ToList());
            }).ToList();

            var state = c.ProfileState;
            var active = list.FirstOrDefault(p => p.IsActive) ?? list[^1];
            string reason;
            if (Switcher.Paused) reason = "Automatic switching is paused because profiles kept switching back and forth. Choose a profile, or go back to automatic.";
            else if (!state.AutoSwitch) reason = "Chosen by you. Automatic switching is off.";
            else if (state.Manual) reason = "Chosen by you. LEO switches automatically again when your networks change.";
            else if (decision is not null && decision.Choice.ProfileId == active.Id) reason = "Automatic: " + decision.Choice.Reason + ".";
            else reason = _activeReason is { } r ? "Automatic: " + r + "." : "On since LEO started.";

            string? pending = null;
            if (decision?.PendingProfileId is { } pid && list.FirstOrDefault(p => p.Id == pid) is { } next)
                pending = $"Switching to {next.Name} in {Math.Max(1, (int)Math.Ceiling(decision.PendingIn.TotalSeconds))} s ({decision.Choice.Reason}).";

            return new ProfilesInfo(list, active.Id, active.Name, state.Manual, state.AutoSwitch, Switcher.Paused, reason, pending);
        });

        if (System.Text.Json.JsonSerializer.Serialize(info) == System.Text.Json.JsonSerializer.Serialize(Info)) return;
        Volatile.Write(ref _info, info);
        Changed?.Invoke(this, info);
    }
}
