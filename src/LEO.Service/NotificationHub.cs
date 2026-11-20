using Leo.Ipc;

namespace Leo.Service;

/// <summary>
/// Collects user-facing events from the service's subsystems; every connected UI forwards them as Windows
/// notifications. Rate-limited per (kind, adapter) so a flapping network can't spam the user.
/// </summary>
public sealed class NotificationHub(ILogger<NotificationHub> log)
{
    private static readonly TimeSpan MinGap = TimeSpan.FromSeconds(20);
    private readonly Dictionary<(NotificationKind, string?), DateTimeOffset> _lastSent = [];
    private readonly Lock _gate = new();

    public event EventHandler<LeoNotification>? Raised;

    public void Publish(NotificationKind kind, string title, string message, string? adapterId = null)
    {
        var now = DateTimeOffset.Now;
        lock (_gate)
        {
            if (_lastSent.TryGetValue((kind, adapterId), out var last) && now - last < MinGap) return;
            _lastSent[(kind, adapterId)] = now;
        }
        log.LogInformation("Notify [{Kind}] {Title}: {Message}", kind, title, message);
        Raised?.Invoke(this, new LeoNotification(kind, title, message, adapterId, now));
    }
}
