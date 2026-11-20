using Leo.Core;
using Leo.Ipc;

namespace Leo.Service;

/// <summary>Per-connection implementation of the control contract.</summary>
public sealed class LeoControlTarget : ILeoControl, IDisposable
{
    private readonly EngineStatusProvider _status;
    private readonly ClientIdentity _client;
    private readonly ILogger _log;

    public LeoControlTarget(EngineStatusProvider status, ClientIdentity client, ILogger log)
    {
        _status = status;
        _client = client;
        _log = log;
        _status.Changed += OnStatusChanged;
    }

    public event EventHandler<EngineStatus>? StatusChanged;

    public Task<HelloResponse> HelloAsync(HelloRequest request, CancellationToken cancellationToken = default)
    {
        var compatible = request.ProtocolVersion == LeoInfo.ProtocolVersion;
        _log.LogInformation("Hello from {Client} v{Version} (protocol {Protocol}, compatible={Compatible})",
            request.ClientName, request.ClientVersion, request.ProtocolVersion, compatible);
        return Task.FromResult(new HelloResponse(LeoInfo.ProtocolVersion, LeoInfo.Version, compatible, _client.Access));
    }

    public Task<EngineStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(_status.Current);

    private void OnStatusChanged(object? sender, EngineStatus e) => StatusChanged?.Invoke(this, e);

    public void Dispose() => _status.Changed -= OnStatusChanged;
}
