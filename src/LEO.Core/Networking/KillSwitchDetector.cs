namespace Leo.Core.Networking;

/// <summary>A VPN adapter in one detector tick: connected, and whether the probe bound to it reached the internet.</summary>
public sealed record KillSwitchVpn(string Id, bool Connected, bool ProbeUp);

/// <summary>A physical adapter's latest probe: OK, or failed with an error class.</summary>
public sealed record KillSwitchPhysical(string Id, bool Ok, ProbeError Error);

public enum KillSwitchVerdict
{
    None = 0,

    /// <summary>The VPN's firewall blocks traffic outside the VPN: rules to physical networks can't connect.</summary>
    BlocksBypass = 1,

    /// <summary>The VPN's firewall blocks apps from sending into LEO's adapter: LEO stands aside until the VPN disconnects.</summary>
    BlocksLeo = 2,
}

public sealed record KillSwitchResult(KillSwitchVerdict Verdict, string? VpnId)
{
    public static KillSwitchResult None { get; } = new(KillSwitchVerdict.None, null);
}

/// <summary>
/// Decides whether a connected VPN's kill switch blocks traffic outside it (LEO.md §20.4). Pure, time injected.
/// <para>
/// BlocksBypass: a VPN is connected with its probe Up and every physical probe fails — all with "access denied" (a WFP
/// block), or all physicals that were fine turned to failures within <see cref="ConnectWindow"/> of the VPN connecting.
/// A failure that starts long after the VPN connected and isn't "denied" is a real outage, not the VPN.<br/>
/// BlocksLeo: the tunnel self-check (a connect through LEO's own adapter) fails while the VPN probe is Up. Latched until
/// that VPN disconnects, because standing aside stops the engine (no more self-checks).<br/>
/// A verdict needs <see cref="Confirmations"/> consecutive ticks; a disconnected VPN clears it at once.
/// </para>
/// </summary>
public sealed class KillSwitchDetector(TimeProvider clock)
{
    public TimeSpan ConnectWindow { get; set; } = TimeSpan.FromSeconds(10);
    public int Confirmations { get; set; } = 2;

    private readonly Dictionary<string, DateTimeOffset> _vpnSince = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _failingSince = new(StringComparer.OrdinalIgnoreCase);
    private KillSwitchResult _candidate = KillSwitchResult.None;
    private int _streak;

    public KillSwitchResult Current { get; private set; } = KillSwitchResult.None;

    /// <param name="selfCheckOk">Tunnel self-check: true/false while the engine runs; null when it wasn't run.</param>
    public KillSwitchResult Update(IReadOnlyList<KillSwitchVpn> vpns, IReadOnlyList<KillSwitchPhysical> physicals, bool? selfCheckOk)
    {
        var now = clock.GetUtcNow();

        foreach (var v in vpns)
        {
            if (v.Connected) _vpnSince.TryAdd(v.Id, now);
            else _vpnSince.Remove(v.Id);
        }
        foreach (var id in _vpnSince.Keys.Where(k => vpns.All(v => !v.Id.Equals(k, StringComparison.OrdinalIgnoreCase))).ToList())
            _vpnSince.Remove(id);
        foreach (var p in physicals)
        {
            if (p.Ok) _failingSince.Remove(p.Id);
            else _failingSince.TryAdd(p.Id, now);
        }

        // A latched stand-aside holds while its VPN stays connected; a disconnected VPN clears any verdict at once.
        if (Current.VpnId is { } current && !_vpnSince.ContainsKey(current))
        {
            Reset();
            return Current;
        }
        if (Current.Verdict == KillSwitchVerdict.BlocksLeo) return Current;

        var candidate = Evaluate(vpns, physicals, selfCheckOk);
        if (candidate == Current) { _candidate = candidate; _streak = 0; return Current; }
        if (candidate == _candidate) _streak++;
        else { _candidate = candidate; _streak = 1; }
        if (_streak >= Confirmations)
        {
            Current = candidate;
            _streak = 0;
        }
        return Current;
    }

    private KillSwitchResult Evaluate(IReadOnlyList<KillSwitchVpn> vpns, IReadOnlyList<KillSwitchPhysical> physicals, bool? selfCheckOk)
    {
        var vpn = vpns.Where(v => v.Connected && v.ProbeUp).OrderBy(v => _vpnSince.GetValueOrDefault(v.Id)).FirstOrDefault();
        if (vpn is null) return KillSwitchResult.None;
        if (selfCheckOk == false) return new KillSwitchResult(KillSwitchVerdict.BlocksLeo, vpn.Id);
        if (physicals.Count == 0 || physicals.Any(p => p.Ok)) return KillSwitchResult.None;

        if (physicals.All(p => p.Error == ProbeError.Denied)) return new KillSwitchResult(KillSwitchVerdict.BlocksBypass, vpn.Id);
        var since = _vpnSince[vpn.Id];
        var allNearConnect = physicals.All(p => _failingSince.TryGetValue(p.Id, out var f) && f >= since - TimeSpan.FromSeconds(2) && f - since <= ConnectWindow);
        return allNearConnect ? new KillSwitchResult(KillSwitchVerdict.BlocksBypass, vpn.Id) : KillSwitchResult.None;
    }

    private void Reset()
    {
        Current = KillSwitchResult.None;
        _candidate = KillSwitchResult.None;
        _streak = 0;
    }
}
