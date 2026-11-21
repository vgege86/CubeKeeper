using Microsoft.Extensions.Logging;

namespace CubeKeeper.Infrastructure.Diagnostics;

public sealed record LogRecord(DateTimeOffset At, LogLevel Level, string Category, string Message, string? Exception);

/// <summary>
/// Guarda en memoria los últimos mensajes del registro de CubeKeeper (los mismos que salen en los registros de la
/// app en TrueNAS) para incluirlos en el diagnóstico descargable.
/// </summary>
public sealed class RecentLogs(int capacity = 1500) : ILoggerProvider
{
    private readonly Queue<LogRecord> _records = new();
    private readonly Lock _lock = new();

    public IReadOnlyList<LogRecord> Snapshot()
    {
        lock (_lock)
        {
            return [.. _records];
        }
    }

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

    public void Dispose()
    {
    }

    private void Add(LogRecord record)
    {
        lock (_lock)
        {
            _records.Enqueue(record);
            while (_records.Count > capacity)
            {
                _records.Dequeue();
            }
        }
    }

    private sealed class Logger(RecentLogs owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }
            ArgumentNullException.ThrowIfNull(formatter);
            owner.Add(new LogRecord(DateTimeOffset.UtcNow, logLevel, category, formatter(state, exception), exception?.ToString()));
        }
    }
}
