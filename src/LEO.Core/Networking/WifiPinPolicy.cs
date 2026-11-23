namespace Leo.Core.Networking;

/// <summary>How Windows made the current Wi-Fi connection (WLAN_CONNECTION_MODE).</summary>
public enum WifiConnectionMode
{
    /// <summary>A saved profile was used explicitly: the user picked it (Windows' Wi-Fi menu, LEO's Connect) or LEO's pin reconnect.</summary>
    Profile = 0,
    TemporaryProfile = 1,
    DiscoverySecure = 2,
    DiscoveryUnsecure = 3,

    /// <summary>Windows connected on its own (auto-connect, roaming between saved networks).</summary>
    Auto = 4,
    Unknown = 99,
}

public enum PinAction
{
    /// <summary>On a pinned network, or nothing to decide: leave it.</summary>
    Nothing,

    /// <summary>Not connected, or Windows moved it by itself: reconnect to a pinned network (M2 behaviour).</summary>
    Enforce,

    /// <summary>The user picked another network on the Primary Wi-Fi: it becomes the first pinned network.</summary>
    AdoptAsPin,

    /// <summary>The user picked another network on a non-Primary Wi-Fi: leave it there; the saved pins stay as they are.</summary>
    Hold,
}

/// <summary>
/// "Keep connected" never overrides a choice the user made (decided 2026-11-23, LEO.md §19.1). A connection the user
/// made explicitly (mode <see cref="WifiConnectionMode.Profile"/> etc., not LEO's own reconnect) is respected; only
/// disconnects and Windows' own automatic moves are corrected.
/// </summary>
public static class WifiPinPolicy
{
    /// <param name="connectedSsid">Current SSID, or null when not connected.</param>
    /// <param name="leoReconnectedTo">SSID LEO's own automatic pin reconnect last connected this adapter to, if any.</param>
    public static PinAction Decide(string? connectedSsid, WifiConnectionMode mode, IReadOnlyList<string> pins,
        bool isPrimary, string? leoReconnectedTo)
    {
        if (pins.Count == 0) return PinAction.Nothing;
        if (connectedSsid is null) return PinAction.Enforce;
        if (pins.Contains(connectedSsid)) return PinAction.Nothing;
        if (mode is WifiConnectionMode.Auto or WifiConnectionMode.Unknown) return PinAction.Enforce;
        if (leoReconnectedTo == connectedSsid) return PinAction.Enforce; // LEO's own doing, not the user's
        return isPrimary ? PinAction.AdoptAsPin : PinAction.Hold;
    }

    /// <summary>The new pin list after adopting: the chosen network first, the old pins after it as backups.</summary>
    public static List<string> Adopt(IReadOnlyList<string> pins, string ssid) =>
        [ssid, .. pins.Where(p => p != ssid)];
}
