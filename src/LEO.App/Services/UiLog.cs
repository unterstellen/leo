using Leo.Core;

namespace Leo.App.Services;

/// <summary>Tiny append-only log for the UI: %LocalAppData%\UNTERSTELLEN\LEO\logs\ui-yyyyMMdd.log.</summary>
public static class UiLog
{
    private static readonly Lock Gate = new();

    public static void Write(string message, Exception? ex = null)
    {
        try
        {
            Directory.CreateDirectory(LeoPaths.UserLogs);
            var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} {message}{(ex is null ? "" : Environment.NewLine + ex)}{Environment.NewLine}";
            lock (Gate) File.AppendAllText(Path.Combine(LeoPaths.UserLogs, $"ui-{DateTime.Now:yyyyMMdd}.log"), line);
        }
        catch
        {
            // logging must never break the UI
        }
    }
}
