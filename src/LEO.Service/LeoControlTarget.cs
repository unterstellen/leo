using Leo.Core;
using Leo.Core.Networking;
using Leo.Ipc;
using Leo.Service.Networking;
using Leo.Service.Wifi;
using StreamJsonRpc;

namespace Leo.Service;

/// <summary>Per-connection implementation of the control contract.</summary>
public sealed class LeoControlTarget : ILeoControl, IDisposable
{
    private readonly EngineStatusProvider _status;
    private readonly NetworkManager? _networks;
    private readonly WifiManager? _wifi;
    private readonly NotificationHub? _hub;
    private readonly ClientIdentity _client;
    private readonly ILogger _log;

    private readonly Leo.Service.SplitTunnel.SplitTunnelManager? _split;

    public LeoControlTarget(EngineStatusProvider status, NetworkManager? networks, ClientIdentity client, ILogger log,
        WifiManager? wifi = null, NotificationHub? hub = null, Leo.Service.SplitTunnel.SplitTunnelManager? splitTunnel = null,
        Leo.Service.Profiles.ProfileManager? profiles = null, Leo.Service.Stats.StatsCollector? stats = null)
    {
        _profiles = profiles;
        _stats = stats;
        _status = status;
        _networks = networks;
        _wifi = wifi;
        _hub = hub;
        _split = splitTunnel;
        _client = client;
        _log = log;
        _status.Changed += OnStatusChanged;
        if (_networks is not null) _networks.Changed += OnNetworksChanged;
        if (_hub is not null) _hub.Raised += OnNotification;
        if (_split is not null) _split.Changed += OnSplitTunnelChanged;
        if (_profiles is not null) _profiles.Changed += OnProfilesChanged;
    }

    // ── Profiles + stats (M5) ───────────────────────────────────────────────

    private readonly Leo.Service.Profiles.ProfileManager? _profiles;
    private readonly Leo.Service.Stats.StatsCollector? _stats;

    public event EventHandler<ProfilesInfo>? ProfilesChanged;

    private void OnProfilesChanged(object? sender, ProfilesInfo e) => ProfilesChanged?.Invoke(this, e);

    private Leo.Service.Profiles.ProfileManager Profiles =>
        _profiles ?? throw new LocalRpcException("Profiles are not available.") { ErrorCode = LeoErrorCodes.InvalidOperation };

    private Leo.Service.Stats.StatsCollector Stats =>
        _stats ?? throw new LocalRpcException("Statistics are not available.") { ErrorCode = LeoErrorCodes.InvalidOperation };

    public Task<ProfilesInfo> GetProfilesAsync(CancellationToken cancellationToken = default) => Task.FromResult(Profiles.Info);

    public async Task<string> SaveProfileAsync(ProfileEdit edit, CancellationToken cancellationToken = default)
    {
        string id = "";
        await Mutate(nameof(SaveProfileAsync), async () => id = await Profiles.SaveAsync(edit, cancellationToken));
        return id;
    }

    public Task DeleteProfileAsync(string profileId, CancellationToken cancellationToken = default) =>
        Mutate(nameof(DeleteProfileAsync), () => Profiles.DeleteAsync(profileId, cancellationToken));

    public Task MoveProfileAsync(string profileId, bool up, CancellationToken cancellationToken = default) =>
        Mutate(nameof(MoveProfileAsync), () => Profiles.MoveAsync(profileId, up, cancellationToken));

    public Task ActivateProfileAsync(string? profileId, CancellationToken cancellationToken = default) =>
        Mutate(nameof(ActivateProfileAsync), () => Profiles.ActivateAsync(profileId, cancellationToken));

    public Task SetProfileAutoSwitchAsync(bool enabled, CancellationToken cancellationToken = default) =>
        Mutate(nameof(SetProfileAutoSwitchAsync), () => Profiles.SetAutoSwitchAsync(enabled, cancellationToken));

    public Task<StatsSnapshot> GetStatsAsync(CancellationToken cancellationToken = default) => Task.FromResult(Stats.GetSnapshot());

    /// <summary>Connections name other users' apps and destinations, so only administrators may see them (LEO.md §19).</summary>
    public async Task<ConnectionsSnapshot> GetConnectionsAsync(CancellationToken cancellationToken = default)
    {
        if (_client.Access != ClientAccess.Full)
            throw new LocalRpcException("Only administrators can see live connections.") { ErrorCode = LeoErrorCodes.AccessDenied };
        return await Stats.GetConnectionsAsync(cancellationToken);
    }

    public Task ClearStatsAsync(CancellationToken cancellationToken = default) =>
        Mutate(nameof(ClearStatsAsync), () => { Stats.Clear(); return Task.CompletedTask; });

    // ── VPN coexistence (M6) ────────────────────────────────────────────────

    public Task SetVpnSettingsAsync(string vpnKey, bool skipUnruled, CancellationToken cancellationToken = default) =>
        Mutate(nameof(SetVpnSettingsAsync), () => Split.SetVpnSettingsAsync(vpnKey, skipUnruled, cancellationToken));

    public Task<IReadOnlyList<VpnClientInfo>> GetVpnClientPresetsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<VpnClientInfo>>(Leo.Core.SplitTunnel.VpnClients.Installed()
            .Select(i => new VpnClientInfo(i.Preset.Name, i.ExePath)).ToList());

    public event EventHandler<SplitTunnelInfo>? SplitTunnelChanged;

    // ── Bonding (M7) ──

    public async Task<string> SaveBondAsync(Leo.Core.SplitTunnel.Bond bond, CancellationToken cancellationToken = default)
    {
        var id = "";
        await Mutate(nameof(SaveBondAsync), async () => id = await Split.SaveBondAsync(bond, cancellationToken));
        return id;
    }

    public Task DeleteBondAsync(string bondId, CancellationToken cancellationToken = default) =>
        Mutate(nameof(DeleteBondAsync), () => Split.DeleteBondAsync(bondId, cancellationToken));

    public Task MoveBondAsync(string bondId, bool up, CancellationToken cancellationToken = default) =>
        Mutate(nameof(MoveBondAsync), () => Split.MoveBondAsync(bondId, up, cancellationToken));

    public Task SetUnruledBondAsync(string? bondId, CancellationToken cancellationToken = default) =>
        Mutate(nameof(SetUnruledBondAsync), () => Split.SetUnruledBondAsync(bondId, cancellationToken));

    public Task<BondsLiveInfo> GetBondsLiveAsync(CancellationToken cancellationToken = default) => Split.GetBondsLiveAsync(cancellationToken);

    private void OnSplitTunnelChanged(object? sender, SplitTunnelInfo e) => SplitTunnelChanged?.Invoke(this, e);

    private Leo.Service.SplitTunnel.SplitTunnelManager Split =>
        _split ?? throw new LocalRpcException("Split tunneling is not available.") { ErrorCode = LeoErrorCodes.InvalidOperation };

    public Task<SplitTunnelInfo> GetSplitTunnelAsync(CancellationToken cancellationToken = default) => Task.FromResult(Split.Info);

    public Task SetSplitTunnelEnabledAsync(bool enabled, CancellationToken cancellationToken = default) =>
        Mutate(nameof(SetSplitTunnelEnabledAsync), () => Split.SetEnabledAsync(enabled, cancellationToken));

    public Task SaveAppRuleAsync(Leo.Core.SplitTunnel.AppRule rule, CancellationToken cancellationToken = default) =>
        Mutate(nameof(SaveAppRuleAsync), () => Split.SaveRuleAsync(rule, cancellationToken));

    public Task DeleteAppRuleAsync(string ruleId, CancellationToken cancellationToken = default) =>
        Mutate(nameof(DeleteAppRuleAsync), () => Split.DeleteRuleAsync(ruleId, cancellationToken));

    public Task SaveDestinationRuleAsync(Leo.Core.SplitTunnel.DestinationRule rule, CancellationToken cancellationToken = default) =>
        Mutate(nameof(SaveDestinationRuleAsync), () => Split.SaveDestinationRuleAsync(rule, cancellationToken));

    public Task DeleteDestinationRuleAsync(string ruleId, CancellationToken cancellationToken = default) =>
        Mutate(nameof(DeleteDestinationRuleAsync), () => Split.DeleteDestinationRuleAsync(ruleId, cancellationToken));

    public Task MoveDestinationRuleAsync(string ruleId, bool up, CancellationToken cancellationToken = default) =>
        Mutate(nameof(MoveDestinationRuleAsync), () => Split.MoveDestinationRuleAsync(ruleId, up, cancellationToken));

    public Task SetNetworkDnsAsync(string adapterId, IReadOnlyList<string> servers, CancellationToken cancellationToken = default) =>
        Mutate(nameof(SetNetworkDnsAsync), () => Split.SetNetworkDnsAsync(adapterId, servers, cancellationToken));

    public async Task<RouteTestResult> TestRouteAsync(string target, string? appPath, CancellationToken cancellationToken = default)
    {
        try
        {
            return await Split.TestRouteAsync(target, appPath, cancellationToken);
        }
        catch (ArgumentException ex)
        {
            throw new LocalRpcException(ex.Message) { ErrorCode = LeoErrorCodes.InvalidOperation };
        }
    }

    public event EventHandler<EngineStatus>? StatusChanged;

    public event EventHandler<NetworkSnapshot>? NetworksChanged;

    public event EventHandler<LeoNotification>? NotificationRaised;

    public Task<HelloResponse> HelloAsync(HelloRequest request, CancellationToken cancellationToken = default)
    {
        var compatible = request.ProtocolVersion == LeoInfo.ProtocolVersion;
        _log.LogInformation("Hello from {Client} v{Version} (protocol {Protocol}, compatible={Compatible})",
            request.ClientName, request.ClientVersion, request.ProtocolVersion, compatible);
        return Task.FromResult(new HelloResponse(LeoInfo.ProtocolVersion, LeoInfo.Version, compatible, _client.Access));
    }

    public Task<EngineStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(_status.Current);

    // ── Networks ────────────────────────────────────────────────────────────

    public Task<NetworkSnapshot> GetNetworksAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(Networks.Snapshot);

    public Task SetPrimaryAsync(string adapterId, CancellationToken cancellationToken = default) =>
        Mutate(nameof(SetPrimaryAsync), () => Networks.SetPrimaryAsync(adapterId, cancellationToken));

    public Task SetPriorityOrderAsync(IReadOnlyList<string> adapterIds, CancellationToken cancellationToken = default) =>
        Mutate(nameof(SetPriorityOrderAsync), () => Networks.SetPriorityOrderAsync(adapterIds, cancellationToken));

    public Task SetAdapterNicknameAsync(string adapterId, string? nickname, CancellationToken cancellationToken = default) =>
        Mutate(nameof(SetAdapterNicknameAsync), () => Networks.SetAdapterNicknameAsync(adapterId, nickname, cancellationToken));

    public Task SetNetworkNicknameAsync(string networkKey, string? nickname, CancellationToken cancellationToken = default) =>
        Mutate(nameof(SetNetworkNicknameAsync), () => Networks.SetNetworkNicknameAsync(networkKey, nickname, cancellationToken));

    public Task SetAutoFailoverAsync(bool enabled, CancellationToken cancellationToken = default) =>
        Mutate(nameof(SetAutoFailoverAsync), () => Networks.SetAutoFailoverAsync(enabled, cancellationToken));

    public Task<Leo.Core.Config.HealthSettings> GetHealthSettingsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(Networks.GetHealthSettings());

    public Task SetHealthSettingsAsync(Leo.Core.Config.HealthSettings settings, CancellationToken cancellationToken = default) =>
        Mutate(nameof(SetHealthSettingsAsync), () => Networks.SetHealthSettingsAsync(settings, cancellationToken));

    // ── Wi-Fi ───────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<WifiNetworkInfo>> GetWifiNetworksAsync(string adapterId, bool rescan, CancellationToken cancellationToken = default)
    {
        RequireWifiAdapter(adapterId);
        try
        {
            return await Wifi.GetNetworksAsync(adapterId, rescan, cancellationToken);
        }
        catch (Exception ex) when (Unwrap(ex) is InvalidOperationException or ArgumentException)
        {
            throw new LocalRpcException(Unwrap(ex).Message) { ErrorCode = LeoErrorCodes.InvalidOperation };
        }
    }

    public Task SetWifiPinsAsync(string adapterId, IReadOnlyList<string> ssids, CancellationToken cancellationToken = default) =>
        Mutate(nameof(SetWifiPinsAsync), async () =>
        {
            var adapter = RequireWifiAdapter(adapterId);
            await Wifi.SetPinsAsync(adapter.Id, ssids, adapter.Mac, cancellationToken);
            Networks.RequestRescan();
        });

    public Task ConnectWifiAsync(string adapterId, string ssid, string? password, CancellationToken cancellationToken = default) =>
        Mutate(nameof(ConnectWifiAsync), async () =>
        {
            var adapter = RequireWifiAdapter(adapterId);
            try
            {
                await Wifi.ConnectAsync(adapter.Id, ssid, password, cancellationToken);
            }
            catch (PasswordRequiredException ex)
            {
                throw new LocalRpcException(ex.Message) { ErrorCode = LeoErrorCodes.PasswordRequired };
            }
            Networks.RequestRescan();
        });

    public Task DisconnectWifiAsync(string adapterId, CancellationToken cancellationToken = default) =>
        Mutate(nameof(DisconnectWifiAsync), async () =>
        {
            var adapter = RequireWifiAdapter(adapterId);
            await Wifi.DisconnectAsync(adapter.Id, cancellationToken);
            Networks.RequestRescan();
        });

    public Task SetDualLinkAsync(string adapterId, bool enabled, CancellationToken cancellationToken = default) =>
        Mutate(nameof(SetDualLinkAsync), async () =>
        {
            var adapter = RequireWifiAdapter(adapterId);
            if (!adapter.DualLinkSupported)
                throw new InvalidOperationException($"{adapter.DisplayName} has no second station, so it can't run a dual link.");
            await Wifi.SetDualLinkAsync(adapter.Id, enabled, adapter.Mac, cancellationToken);
            Networks.RequestRescan();
        });

    // ── Plumbing ────────────────────────────────────────────────────────────

    private NetworkManager Networks =>
        _networks ?? throw new LocalRpcException("Network manager is not available.") { ErrorCode = LeoErrorCodes.InvalidOperation };

    private WifiManager Wifi =>
        _wifi ?? throw new LocalRpcException("Wi-Fi manager is not available.") { ErrorCode = LeoErrorCodes.InvalidOperation };

    private AdapterInfo RequireWifiAdapter(string adapterId)
    {
        AdapterInfo adapter;
        try { adapter = Networks.Find(adapterId); }
        catch (KeyNotFoundException ex) { throw new LocalRpcException(ex.Message) { ErrorCode = LeoErrorCodes.NotFound }; }
        if (adapter.Kind != AdapterKind.WiFi)
            throw new LocalRpcException($"{adapter.DisplayName} isn't a Wi-Fi adapter.") { ErrorCode = LeoErrorCodes.InvalidOperation };
        return adapter;
    }

    /// <summary>Changes need Full access (LEO.md §4.2); service-side errors map to JSON-RPC error codes.</summary>
    private async Task Mutate(string operation, Func<Task> action)
    {
        if (_client.Access != ClientAccess.Full)
        {
            _log.LogWarning("Denied {Operation} for read-only client {User} (pid {Pid})", operation, _client.UserName, _client.ProcessId);
            throw new LocalRpcException("Only administrators can change LEO settings.") { ErrorCode = LeoErrorCodes.AccessDenied };
        }

        try
        {
            try
            {
                await action();
            }
            catch (AggregateException agg) when (agg.InnerExceptions.Count == 1)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(agg.InnerException!).Throw();
            }
        }
        catch (LocalRpcException)
        {
            throw;
        }
        catch (KeyNotFoundException ex)
        {
            throw new LocalRpcException(ex.Message) { ErrorCode = LeoErrorCodes.NotFound };
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            throw new LocalRpcException(ex.Message) { ErrorCode = LeoErrorCodes.InvalidOperation };
        }
    }

    private static Exception Unwrap(Exception ex) =>
        ex is AggregateException { InnerExceptions.Count: 1 } agg ? Unwrap(agg.InnerException!) : ex;

    private void OnStatusChanged(object? sender, EngineStatus e) => StatusChanged?.Invoke(this, e);

    private void OnNetworksChanged(object? sender, NetworkSnapshot e) => NetworksChanged?.Invoke(this, e);

    private void OnNotification(object? sender, LeoNotification e) => NotificationRaised?.Invoke(this, e);

    public void Dispose()
    {
        _status.Changed -= OnStatusChanged;
        if (_networks is not null) _networks.Changed -= OnNetworksChanged;
        if (_hub is not null) _hub.Raised -= OnNotification;
        if (_split is not null) _split.Changed -= OnSplitTunnelChanged;
        if (_profiles is not null) _profiles.Changed -= OnProfilesChanged;
    }
}
