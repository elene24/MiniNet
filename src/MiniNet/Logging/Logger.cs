namespace MiniNet.Logging;

public enum LogLevel
{
    Debug,
    Information,
    Warning,
    Error,
}

public interface ILogger
{
    void Log(LogLevel level, string message, Exception? exception = null);
}

public static class LoggerExtensions
{
    public static void Info(this ILogger logger, string message) => logger.Log(LogLevel.Information, message);

    public static void Warn(this ILogger logger, string message) => logger.Log(LogLevel.Warning, message);

    public static void Error(this ILogger logger, string message, Exception? exception = null) => logger.Log(LogLevel.Error, message, exception);
}

/// <summary>Writes one line per entry: "2026-11-21T18:03:12.123Z INFO  GET /doctors -> 200 in 1.4 ms".</summary>
public sealed class ConsoleLogger(LogLevel minimumLevel = LogLevel.Information) : ILogger
{
    public void Log(LogLevel level, string message, Exception? exception = null)
    {
        if (level < minimumLevel) return;
        var label = level switch
        {
            LogLevel.Debug => "DEBUG",
            LogLevel.Information => "INFO ",
            LogLevel.Warning => "WARN ",
            _ => "ERROR",
        };
        var line = $"{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ss.fffZ} {label} {message}";
        if (exception is not null) line += Environment.NewLine + exception;
        Console.WriteLine(line);
    }
}

/// <summary>Keeps entries in memory. Used by tests to check what was logged.</summary>
public sealed class InMemoryLogger : ILogger
{
    private readonly List<(LogLevel Level, string Message, Exception? Exception)> _entries = [];

    public IReadOnlyList<(LogLevel Level, string Message, Exception? Exception)> Entries
    {
        get { lock (_entries) return _entries.ToList(); }
    }

    public void Log(LogLevel level, string message, Exception? exception = null)
    {
        lock (_entries) _entries.Add((level, message, exception));
    }
}
