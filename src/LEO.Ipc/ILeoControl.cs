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

    // ── Split tunneling: apps (M3) ────────────────────────────────────────────

    /// <summary>Engine state and app rules with their current route.</summary>
    Task<SplitTunnelInfo> GetSplitTunnelAsync(CancellationToken cancellationToken = default);

    /// <summary>Master switch for split tunneling (rules are kept). Full access.</summary>
    Task SetSplitTunnelEnabledAsync(bool enabled, CancellationToken cancellationToken = default);

    /// <summary>Adds a rule, or replaces the one with the same Id. Validated by the service. Full access.</summary>
    Task SaveAppRuleAsync(Leo.Core.SplitTunnel.AppRule rule, CancellationToken cancellationToken = default);

    /// <summary>Deletes a rule. Full access.</summary>
    Task DeleteAppRuleAsync(string ruleId, CancellationToken cancellationToken = default);

    // ── Split tunneling: destinations + DNS (M4) ──────────────────────────────

    /// <summary>Adds a destination rule, or replaces the one with the same Id. Validated/normalised by the service. Full access.</summary>
    Task SaveDestinationRuleAsync(Leo.Core.SplitTunnel.DestinationRule rule, CancellationToken cancellationToken = default);

    /// <summary>Deletes a destination rule. Full access.</summary>
    Task DeleteDestinationRuleAsync(string ruleId, CancellationToken cancellationToken = default);

    /// <summary>Moves a destination rule one place up/down in the user's order (the tie-breaker). Full access.</summary>
    Task MoveDestinationRuleAsync(string ruleId, bool up, CancellationToken cancellationToken = default);

    /// <summary>Custom DNS servers for a network; empty = the network's own DNS. Full access.</summary>
    Task SetNetworkDnsAsync(string adapterId, IReadOnlyList<string> servers, CancellationToken cancellationToken = default);

    /// <summary>
    /// Which rule and network a destination ("youtube.com", "https://x.com/a", "1.2.3.4:443") would use, optionally
    /// for a program path. Read-only; may do one DNS lookup to check IP rules.
    /// </summary>
    Task<RouteTestResult> TestRouteAsync(string target, string? appPath, CancellationToken cancellationToken = default);

    /// <summary>Raised when the engine state, rules or their routes change.</summary>
    event EventHandler<SplitTunnelInfo>? SplitTunnelChanged;

    /// <summary>
    /// Dual link on a Wi-Fi primary that has a secondary station (DBS cards): its secondary ("WiFi 2") joins the
    /// same network on the other band, giving a second independent link. Remembered and re-applied. Full access.
    /// </summary>
    Task SetDualLinkAsync(string adapterId, bool enabled, CancellationToken cancellationToken = default);

    // ── Profiles (M5) ─────────────────────────────────────────────────────────

    /// <summary>All profiles, the active one and why.</summary>
    Task<ProfilesInfo> GetProfilesAsync(CancellationToken cancellationToken = default);

    /// <summary>Creates (Id null) or renames/re-triggers a profile; returns its id. Full access.</summary>
    Task<string> SaveProfileAsync(ProfileEdit edit, CancellationToken cancellationToken = default);

    /// <summary>Deletes a profile (not Default). Deleting the active one switches to Default first. Full access.</summary>
    Task DeleteProfileAsync(string profileId, CancellationToken cancellationToken = default);

    /// <summary>Moves a profile up/down: when several profiles' triggers match, the higher one wins. Full access.</summary>
    Task MoveProfileAsync(string profileId, bool up, CancellationToken cancellationToken = default);

    /// <summary>Switches to a profile by hand; null = back to automatic (the triggers decide now). Full access.</summary>
    Task ActivateProfileAsync(string? profileId, CancellationToken cancellationToken = default);

    /// <summary>Automatic switching on/off. Full access.</summary>
    Task SetProfileAutoSwitchAsync(bool enabled, CancellationToken cancellationToken = default);

    /// <summary>Raised when profiles, the active profile or its reason change.</summary>
    event EventHandler<ProfilesInfo>? ProfilesChanged;

    // ── Stats (M5) ────────────────────────────────────────────────────────────

    /// <summary>Per-adapter throughput/totals/latency and per-app usage per network. Poll while a stats view is open.</summary>
    Task<StatsSnapshot> GetStatsAsync(CancellationToken cancellationToken = default);

    /// <summary>Live connections through the routing engine (app → destination → network → rule). Never stored. Full access.</summary>
    Task<ConnectionsSnapshot> GetConnectionsAsync(CancellationToken cancellationToken = default);

    /// <summary>Deletes all saved statistics (stats.db). Full access.</summary>
    Task ClearStatsAsync(CancellationToken cancellationToken = default);

    // ── VPN coexistence (M6) ──────────────────────────────────────────────────

    /// <summary>"Let unruled traffic skip this VPN" for the VPN with this key (machine-wide). Full access.</summary>
    Task SetVpnSettingsAsync(string vpnKey, bool skipUnruled, CancellationToken cancellationToken = default);

    /// <summary>Installed VPN clients LEO knows, for Add app → VPN clients.</summary>
    Task<IReadOnlyList<VpnClientInfo>> GetVpnClientPresetsAsync(CancellationToken cancellationToken = default);
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
