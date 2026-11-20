namespace Leo.Ipc;

/// <summary>
/// JSON-RPC contract exposed by LEO.Service over \.\pipe\LEO.Control.
/// Grows milestone by milestone (adapters, rules, profiles…). Keep it additive.
/// </summary>
public interface ILeoControl
{
    /// <summary>Handshake: the UI sends its protocol version, the service returns its info.</summary>
    Task<HelloResponse> HelloAsync(HelloRequest request, CancellationToken cancellationToken = default);

    /// <summary>Current engine status snapshot.</summary>
    Task<EngineStatus> GetStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>Raised by the service whenever the status snapshot changes (also a periodic heartbeat).</summary>
    event EventHandler<EngineStatus>? StatusChanged;

    // ── Networks (M1) ─────────────────────────────────────────────────────────

    /// <summary>All user-visible adapters, their networks, priority and the current Primary.</summary>
    Task<NetworkSnapshot> GetNetworksAsync(CancellationToken cancellationToken = default);

    /// <summary>Make an adapter the preferred Primary (moves it to the top of the priority list). Full access.</summary>
    Task SetPrimaryAsync(string adapterId, CancellationToken cancellationToken = default);

    /// <summary>Replace the failover priority order (adapter ids, most preferred first). Full access.</summary>
    Task SetPriorityOrderAsync(IReadOnlyList<string> adapterIds, CancellationToken cancellationToken = default);

    /// <summary>Set or clear (null/empty) an adapter nickname. Full access.</summary>
    Task SetAdapterNicknameAsync(string adapterId, string? nickname, CancellationToken cancellationToken = default);

    /// <summary>Set or clear (null/empty) a network nickname, by <see cref="NetworkInfo.Key"/>. Full access.</summary>
    Task SetNetworkNicknameAsync(string networkKey, string? nickname, CancellationToken cancellationToken = default);

    /// <summary>Raised whenever adapters, networks, priority, health or nicknames change.</summary>
    event EventHandler<NetworkSnapshot>? NetworksChanged;

    // ── Health & failover (M2) ────────────────────────────────────────────────

    /// <summary>Turn automatic failover on/off (probes keep running either way). Full access.</summary>
    Task SetAutoFailoverAsync(bool enabled, CancellationToken cancellationToken = default);

    /// <summary>Internet-check settings: interval, failover/failback delays, slow threshold, probe targets.</summary>
    Task<Leo.Core.Config.HealthSettings> GetHealthSettingsAsync(CancellationToken cancellationToken = default);

    /// <summary>Replace the internet-check settings (validated by the service; AutoFailover is kept). Full access.</summary>
    Task SetHealthSettingsAsync(Leo.Core.Config.HealthSettings settings, CancellationToken cancellationToken = default);

    /// <summary>Failover, failback, new adapters… for the tray app to show as Windows notifications.</summary>
    event EventHandler<LeoNotification>? NotificationRaised;

    // ── Wi-Fi (M2) ────────────────────────────────────────────────────────────

    /// <summary>Networks visible to a Wi-Fi adapter, plus its pinned ones. <paramref name="rescan"/> triggers a fresh scan.</summary>
    Task<IReadOnlyList<WifiNetworkInfo>> GetWifiNetworksAsync(string adapterId, bool rescan, CancellationToken cancellationToken = default);

    /// <summary>SSIDs this adapter should stay on, most preferred first (empty = no pinning). Full access.</summary>
    Task SetWifiPinsAsync(string adapterId, IReadOnlyList<string> ssids, CancellationToken cancellationToken = default);

    /// <summary>
    /// Connect a Wi-Fi adapter to an SSID now. Uses its saved profile, copies one from another adapter, or —
    /// with <paramref name="password"/> — creates one (stored by Windows, never by LEO). Fails with
    /// <see cref="LeoErrorCodes.PasswordRequired"/> when a password is needed. Full access.
    /// </summary>
    Task ConnectWifiAsync(string adapterId, string ssid, string? password, CancellationToken cancellationToken = default);

    /// <summary>Disconnect a Wi-Fi adapter (pins are kept; clear them first to stop LEO reconnecting). Full access.</summary>
    Task DisconnectWifiAsync(string adapterId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Dual link on a Wi-Fi primary that has a secondary station (DBS cards): its secondary ("WiFi 2") joins the
    /// same network on the other band, giving a second independent link. Remembered and re-applied. Full access.
    /// </summary>
    Task SetDualLinkAsync(string adapterId, bool enabled, CancellationToken cancellationToken = default);
}

public sealed record HelloRequest(int ProtocolVersion, string ClientName, string ClientVersion);

public sealed record HelloResponse(
    int ProtocolVersion,
    string ServiceVersion,
    bool Compatible,
    ClientAccess Access);

/// <summary>What the connected client is allowed to do (LEO.md §4.2).</summary>
public enum ClientAccess
{
    ReadOnly = 0,
    Full = 1,
}

public enum EngineState
{
    Starting = 0,
    Running = 1,
    Stopping = 2,
    Disabled = 3,
}

public sealed record EngineStatus(
    EngineState State,
    string ServiceVersion,
    DateTimeOffset StartedAt,
    bool IsElevated,
    string RunningAs,
    int ConnectedClients);
