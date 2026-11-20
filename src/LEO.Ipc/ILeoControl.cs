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
