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

    public LeoControlTarget(EngineStatusProvider status, NetworkManager? networks, ClientIdentity client, ILogger log,
        WifiManager? wifi = null, NotificationHub? hub = null)
    {
        _status = status;
        _networks = networks;
        _wifi = wifi;
        _hub = hub;
        _client = client;
        _log = log;
        _status.Changed += OnStatusChanged;
        if (_networks is not null) _networks.Changed += OnNetworksChanged;
        if (_hub is not null) _hub.Raised += OnNotification;
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
    }
}
