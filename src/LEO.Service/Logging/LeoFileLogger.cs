using System.Collections.Concurrent;
using System.Text;

namespace Leo.Service.Logging;

/// <summary>
/// Minimal rolling daily file logger: {dir}\{prefix}-yyyyMMdd.log, keeps 7 days (LEO.md §8).
/// Falls back silently if the directory can't be written (e.g. non-admin console run).
/// </summary>
public sealed class LeoFileLoggerProvider : ILoggerProvider
{
    private const int RetentionDays = 7;
    private readonly string _directory;
    private readonly string _prefix;
    private readonly Lock _writeGate = new();
    private readonly ConcurrentDictionary<string, LeoFileLogger> _loggers = new();
    private bool _disabled;

    public LeoFileLoggerProvider(string directory, string prefix)
    {
        _directory = directory;
        _prefix = prefix;
        try
        {
            Directory.CreateDirectory(directory);
            PruneOldFiles();
        }
        catch
        {
            _disabled = true;
        }
    }

    public ILogger CreateLogger(string categoryName) =>
        _loggers.GetOrAdd(categoryName, c => new LeoFileLogger(this, c));

    internal void Write(string category, LogLevel level, string message, Exception? exception)
    {
        if (_disabled) return;
        var sb = new StringBuilder()
            .Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz")).Append(' ')
            .Append(level.ToString().ToUpperInvariant()[..4]).Append(' ')
            .Append(category).Append(": ").Append(message);
        if (exception is not null) sb.AppendLine().Append(exception);
        sb.AppendLine();

        lock (_writeGate)
        {
            try
            {
                File.AppendAllText(Path.Combine(_directory, $"{_prefix}-{DateTime.Now:yyyyMMdd}.log"), sb.ToString());
            }
            catch
            {
                _disabled = true;
            }
        }
    }

    private void PruneOldFiles()
    {
        var cutoff = DateTime.Now.AddDays(-RetentionDays);
        foreach (var file in Directory.EnumerateFiles(_directory, $"{_prefix}-*.log"))
        {
            if (File.GetLastWriteTime(file) < cutoff) File.Delete(file);
        }
    }

    public void Dispose() { }

    private sealed class LeoFileLogger(LeoFileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            provider.Write(category, logLevel, formatter(state, exception), exception);
        }
    }
}

public static class LeoFileLoggerExtensions
{
    public static ILoggingBuilder AddLeoFileLogger(this ILoggingBuilder builder, string directory, string prefix)
    {
        builder.AddProvider(new LeoFileLoggerProvider(directory, prefix));
        return builder;
    }
}
