using Leo.Core;
using Leo.Ipc;
using Leo.Service.Networking;
using StreamJsonRpc;

namespace Leo.Service;

/// <summary>Per-connection implementation of the control contract.</summary>
public sealed class LeoControlTarget : ILeoControl, IDisposable
{
    private readonly EngineStatusProvider _status;
    private readonly NetworkManager? _networks;
    private readonly ClientIdentity _client;
    private readonly ILogger _log;

    public LeoControlTarget(EngineStatusProvider status, NetworkManager? networks, ClientIdentity client, ILogger log)
    {
        _status = status;
        _networks = networks;
        _client = client;
        _log = log;
        _status.Changed += OnStatusChanged;
        if (_networks is not null) _networks.Changed += OnNetworksChanged;
    }

    public event EventHandler<EngineStatus>? StatusChanged;

    public event EventHandler<NetworkSnapshot>? NetworksChanged;

    public Task<HelloResponse> HelloAsync(HelloRequest request, CancellationToken cancellationToken = default)
    {
        var compatible = request.ProtocolVersion == LeoInfo.ProtocolVersion;
        _log.LogInformation("Hello from {Client} v{Version} (protocol {Protocol}, compatible={Compatible})",
            request.ClientName, request.ClientVersion, request.ProtocolVersion, compatible);
        return Task.FromResult(new HelloResponse(LeoInfo.ProtocolVersion, LeoInfo.Version, compatible, _client.Access));
    }

    public Task<EngineStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(_status.Current);

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

    private NetworkManager Networks =>
        _networks ?? throw new LocalRpcException("Network manager is not available.") { ErrorCode = LeoErrorCodes.InvalidOperation };

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
            await action();
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

    private void OnStatusChanged(object? sender, EngineStatus e) => StatusChanged?.Invoke(this, e);

    private void OnNetworksChanged(object? sender, NetworkSnapshot e) => NetworksChanged?.Invoke(this, e);

    public void Dispose()
    {
        _status.Changed -= OnStatusChanged;
        if (_networks is not null) _networks.Changed -= OnNetworksChanged;
    }
}
