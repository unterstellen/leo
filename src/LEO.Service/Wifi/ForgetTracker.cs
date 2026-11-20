namespace Leo.Service.Wifi;

/// <summary>Saved Wi-Fi profiles on one interface: profile name → SSID.</summary>
public sealed record ProfileSnapshot(bool IsSecondary, IReadOnlyDictionary<string, string> Profiles);

/// <summary>
/// Detects networks the user "forgot" in Windows (LEO.md §5.5): a profile that disappeared from a primary
/// interface (the ones Windows' Wi-Fi menus manage) that is still present. Only the user's forget counts:
/// removals LEO made itself (<see cref="Suppress"/>), unplugged adapters and hidden secondary interfaces don't.
/// LEO never forgets a network on its own — it only follows the user's forget to every other adapter.
/// </summary>
public sealed class ForgetTracker
{
    private Dictionary<Guid, ProfileSnapshot>? _previous;
    private readonly HashSet<(Guid, string)> _suppressed = [];

    /// <summary>Marks a profile removal as LEO's own, so it isn't mistaken for a user forget.</summary>
    public void Suppress(Guid iface, string profileName) => _suppressed.Add((iface, profileName));

    /// <summary>Feeds the current state; returns the SSIDs the user forgot since the last call (first call: none).</summary>
    public IReadOnlySet<string> Update(IReadOnlyDictionary<Guid, ProfileSnapshot> current)
    {
        var forgotten = new HashSet<string>(StringComparer.Ordinal);
        if (_previous is not null)
        {
            foreach (var (iface, before) in _previous)
            {
                if (before.IsSecondary || !current.TryGetValue(iface, out var now)) continue; // hidden or unplugged
                foreach (var (profile, ssid) in before.Profiles)
                {
                    if (now.Profiles.ContainsKey(profile)) continue;
                    if (_suppressed.Remove((iface, profile))) continue; // LEO removed it
                    forgotten.Add(ssid);
                }
            }
        }
        _previous = current.ToDictionary(kv => kv.Key, kv => kv.Value);
        // A suppression is only needed until LEO's own delete shows up; drop any whose profile is already gone.
        _suppressed.RemoveWhere(s => !(current.TryGetValue(s.Item1, out var snap) && snap.Profiles.ContainsKey(s.Item2)));
        return forgotten;
    }
}
