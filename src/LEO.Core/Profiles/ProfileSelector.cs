namespace Leo.Core.Profiles;

/// <summary>An adapter that is present and connected right now.</summary>
public sealed record ConnectedAdapter(string Id, string? Mac, string Name);

/// <summary>What profile triggers look at (LEO.md §6.6).</summary>
public sealed record NetworkConditions(
    IReadOnlySet<string> ConnectedSsids,
    IReadOnlySet<string> VisibleSsids,
    IReadOnlyList<ConnectedAdapter> ConnectedAdapters)
{
    public static NetworkConditions None { get; } = new(new HashSet<string>(), new HashSet<string>(), []);
}

/// <summary>The profile the triggers point at, and why.</summary>
public sealed record ProfileChoice(string ProfileId, string ProfileName, string Reason);

/// <summary>
/// Picks the profile for the current networks (LEO.md §19): the first profile in the user's list with a matching
/// trigger (any trigger of a profile is enough); Default when none matches. Pure, so tests and UI agree.
/// </summary>
public static class ProfileSelector
{
    public static ProfileChoice Choose(IReadOnlyList<Profile> profiles, NetworkConditions now)
    {
        foreach (var p in profiles.Where(p => !p.IsDefault))
        {
            foreach (var t in p.Triggers)
            {
                if (Explain(t, now) is { } why) return new ProfileChoice(p.Id, p.Name, why);
            }
        }
        var def = profiles.FirstOrDefault(p => p.IsDefault);
        return new ProfileChoice(Profile.DefaultId, def?.Name ?? "Default", "No other profile's trigger matches right now");
    }

    public static bool AnyTriggerMatches(Profile p, NetworkConditions now) => p.Triggers.Any(t => Explain(t, now) is not null);

    /// <summary>"Wi-Fi “Zixel” is connected", or null when the trigger doesn't match.</summary>
    public static string? Explain(ProfileTrigger t, NetworkConditions now)
    {
        switch (t.Kind)
        {
            case ProfileTriggerKind.WifiConnected:
                return now.ConnectedSsids.Contains(t.Value) ? $"Wi-Fi “{t.Value}” is connected" : null;
            case ProfileTriggerKind.WifiVisible:
                return now.VisibleSsids.Contains(t.Value) || now.ConnectedSsids.Contains(t.Value) ? $"Wi-Fi “{t.Value}” is in range" : null;
            case ProfileTriggerKind.AdapterConnected:
                var hit = now.ConnectedAdapters.FirstOrDefault(a => a.Id.Equals(t.Value, StringComparison.OrdinalIgnoreCase))
                          ?? now.ConnectedAdapters.FirstOrDefault(a => t.Mac is not null && a.Mac is not null && a.Mac.Equals(t.Mac, StringComparison.OrdinalIgnoreCase));
                return hit is null ? null : $"{hit.Name} is connected";
            default:
                return null;
        }
    }

    /// <summary>"When Wi-Fi “Zixel” is connected" — for the profile card.</summary>
    public static string Describe(ProfileTrigger t) => t.Kind switch
    {
        ProfileTriggerKind.WifiConnected => $"Wi-Fi “{t.Value}” is connected",
        ProfileTriggerKind.WifiVisible => $"Wi-Fi “{t.Value}” is in range",
        ProfileTriggerKind.AdapterConnected => $"{t.Label ?? "an adapter"} is plugged in and connected",
        _ => t.Value,
    };
}

/// <summary>Delays that stop profiles from flapping (LEO.md §19).</summary>
public sealed record ProfileSwitchTiming
{
    /// <summary>A profile's trigger started matching: switch once it has matched this long.</summary>
    public TimeSpan SwitchIn { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>The active profile's triggers stopped matching: wait this long (Wi-Fi blips, re-plugs).</summary>
    public TimeSpan SwitchOut { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>At least this long between two automatic switches.</summary>
    public TimeSpan MinDwell { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>This many automatic switches within <see cref="FlapWindow"/> pause automatic switching.</summary>
    public int FlapLimit { get; init; } = 4;
    public TimeSpan FlapWindow { get; init; } = TimeSpan.FromMinutes(10);
}

/// <summary>Result of one evaluation.</summary>
public sealed record SwitchDecision(
    ProfileChoice Choice,
    string? SwitchTo,          // switch to this profile now (automatic)
    string? PendingProfileId,  // a switch is waiting for its delay
    TimeSpan PendingIn,
    bool Paused,               // automatic switching paused because profiles kept flapping
    bool JustPaused);

/// <summary>
/// Automatic switching with hysteresis (LEO.md §19). A new choice must hold for <see cref="ProfileSwitchTiming.SwitchIn"/>
/// (or <see cref="ProfileSwitchTiming.SwitchOut"/> when the active profile merely stopped matching), with a minimum
/// time between switches and a flap guard. A manual pick holds until the automatic choice changes from what it was
/// when the user picked. Not thread-safe; time is injected for tests.
/// </summary>
public sealed class ProfileSwitcher(TimeProvider time, ProfileSwitchTiming? timing = null)
{
    private readonly ProfileSwitchTiming _t = timing ?? new ProfileSwitchTiming();
    private readonly List<DateTimeOffset> _autoSwitches = [];
    private string? _pendingId;
    private DateTimeOffset _pendingSince;
    private string? _manualBaseline;
    private DateTimeOffset _lastSwitch = DateTimeOffset.MinValue;

    public bool Paused { get; private set; }

    /// <summary>The user picked a profile: automatic switching waits until the situation differs from <paramref name="choiceNow"/>.</summary>
    public void NoteManual(ProfileChoice choiceNow)
    {
        _manualBaseline = choiceNow.ProfileId;
        _pendingId = null;
        _lastSwitch = time.GetUtcNow();
        Resume();
    }

    /// <summary>"Back to automatic" or automatic switching turned on again: forget the manual hold and the flap pause.</summary>
    public void Resume()
    {
        Paused = false;
        _autoSwitches.Clear();
    }

    public void ClearManual() => _manualBaseline = null;

    public SwitchDecision Evaluate(IReadOnlyList<Profile> profiles, NetworkConditions now, ProfileState state)
    {
        var choice = ProfileSelector.Choose(profiles, now);
        var clock = time.GetUtcNow();
        if (!state.AutoSwitch || Paused) return Idle(choice);

        ProfileChoice candidate;
        if (state.Manual)
        {
            _manualBaseline ??= choice.ProfileId; // after a restart: the situation at start is the baseline
            if (choice.ProfileId == _manualBaseline || choice.ProfileId == state.ActiveProfileId)
            {
                if (choice.ProfileId != _manualBaseline) _manualBaseline = choice.ProfileId;
                return Idle(choice);
            }
            candidate = choice;
        }
        else
        {
            _manualBaseline = null;
            if (choice.ProfileId == state.ActiveProfileId) return Idle(choice);
            candidate = choice;
        }

        if (_pendingId != candidate.ProfileId)
        {
            _pendingId = candidate.ProfileId;
            _pendingSince = clock;
        }

        var active = profiles.FirstOrDefault(p => p.Id == state.ActiveProfileId);
        var leaving = active is { IsDefault: false } && !ProfileSelector.AnyTriggerMatches(active, now);
        var due = _pendingSince + (leaving ? _t.SwitchOut : _t.SwitchIn);
        if (_lastSwitch != DateTimeOffset.MinValue && _lastSwitch + _t.MinDwell > due) due = _lastSwitch + _t.MinDwell;

        if (clock < due) return new SwitchDecision(choice, null, candidate.ProfileId, due - clock, false, false);

        _autoSwitches.RemoveAll(s => clock - s > _t.FlapWindow);
        if (_autoSwitches.Count >= _t.FlapLimit)
        {
            Paused = true;
            _pendingId = null;
            return new SwitchDecision(choice, null, null, TimeSpan.Zero, true, true);
        }
        _autoSwitches.Add(clock);
        _lastSwitch = clock;
        _pendingId = null;
        _manualBaseline = null;
        return new SwitchDecision(choice, candidate.ProfileId, null, TimeSpan.Zero, false, false);
    }

    private SwitchDecision Idle(ProfileChoice choice)
    {
        _pendingId = null;
        return new SwitchDecision(choice, null, null, TimeSpan.Zero, Paused, false);
    }
}
