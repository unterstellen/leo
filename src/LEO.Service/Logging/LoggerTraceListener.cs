using System.Diagnostics;

namespace Leo.Service.Logging;

/// <summary>Forwards StreamJsonRpc TraceSource output (protocol errors etc.) into the service log.</summary>
internal sealed class LoggerTraceListener(ILogger log) : TraceListener
{
    public override void Write(string? message) { }

    public override void WriteLine(string? message)
    {
        if (!string.IsNullOrWhiteSpace(message)) log.LogWarning("JSON-RPC: {Message}", message);
    }

    public override void TraceEvent(TraceEventCache? eventCache, string source, TraceEventType eventType, int id, string? message)
    {
        if (eventType <= TraceEventType.Warning) log.LogWarning("JSON-RPC {Type}: {Message}", eventType, message);
    }

    public override void TraceEvent(TraceEventCache? eventCache, string source, TraceEventType eventType, int id, string? format, params object?[]? args) =>
        TraceEvent(eventCache, source, eventType, id, args is { Length: > 0 } && format is not null ? string.Format(format, args) : format);
}
