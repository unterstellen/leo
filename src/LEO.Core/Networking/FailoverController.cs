namespace Leo.Core.Networking;

/// <summary>
/// Decides which adapters are temporarily demoted because they have no internet (LEO.md §5.7).
/// <para>
/// Demote: health is failing (NoInternet / Captive) continuously for <see cref="FailoverDelay"/>.<br/>
/// Restore: health is usable (Up / Degraded) continuously for <see cref="FailbackDelay"/> — hysteresis,
/// so a flapping network doesn't bounce traffic back and forth.<br/>
/// Disconnected adapters aren't demoted: Windows already skips them (no default route); when they
/// reconnect they must prove themselves before being trusted again only if they were demoted before.
/// </para>
/// Pure and clock-injected so it's unit-testable. Not thread-safe: call from one place (NetworkManager).
/// </summary>
public sealed class FailoverController(TimeProvider clock)
{
    private sealed class Track
    {
        public HealthState State;
        public DateTimeOffset Since;
        public bool Demoted;
    }

    private readonly Dictionary<string, Track> _tracks = new(StringComparer.OrdinalIgnoreCase);

    public TimeSpan FailoverDelay { get; set; } = TimeSpan.FromSeconds(6);
    public TimeSpan FailbackDelay { get; set; } = TimeSpan.FromSeconds(30);
    public bool Enabled { get; set; } = true;

    /// <summary>Feeds the latest health of every adapter. Returns true if the demoted set changed.</summary>
    public bool Update(IReadOnlyDictionary<string, HealthState> health)
    {
        var now = clock.GetUtcNow();
        var changed = false;

        foreach (var id in _tracks.Keys.Where(k => !health.ContainsKey(k)).ToList())
        {
            changed |= _tracks[id].Demoted;
            _tracks.Remove(id); // adapter vanished
        }

        foreach (var (id, state) in health)
        {
            if (!_tracks.TryGetValue(id, out var t))
            {
                _tracks[id] = t = new Track { State = state, Since = now };
            }
            else if (Category(t.State) != Category(state))
            {
                t.Since = now; // the clock restarts whenever the category changes
            }
            t.State = state;

            var before = t.Demoted;
            if (!Enabled)
            {
                t.Demoted = false;
            }
            else if (state.IsFailing() && now - t.Since >= FailoverDelay)
            {
                t.Demoted = true;
            }
            else if (state.IsUsable() && t.Demoted && now - t.Since >= FailbackDelay)
            {
                t.Demoted = false;
            }
            changed |= before != t.Demoted;
        }

        return changed;
    }

    public bool IsDemoted(string id) => _tracks.TryGetValue(id, out var t) && t.Demoted;

    public IReadOnlySet<string> Demoted =>
        _tracks.Where(kv => kv.Value.Demoted).Select(kv => kv.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Seconds until a demoted adapter is restored (if it stays healthy), for the UI.</summary>
    public int? SecondsUntilFailback(string id)
    {
        if (!_tracks.TryGetValue(id, out var t) || !t.Demoted || !t.State.IsUsable()) return null;
        var left = FailbackDelay - (clock.GetUtcNow() - t.Since);
        return Math.Max(0, (int)Math.Ceiling(left.TotalSeconds));
    }

    // Usable / failing / neither (unknown, disconnected) — transitions within a category keep the clock.
    private static int Category(HealthState s) => s.IsUsable() ? 1 : s.IsFailing() ? 2 : 0;
}
