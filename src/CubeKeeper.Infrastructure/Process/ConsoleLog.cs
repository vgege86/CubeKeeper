using CubeKeeper.Core.Abstractions;

namespace CubeKeeper.Infrastructure.Process;

/// <summary>Búfer circular en memoria con las últimas líneas de consola.</summary>
public sealed class ConsoleLog : IConsoleLog
{
    private readonly int _capacity;
    private readonly Queue<ConsoleLine> _lines;
    private readonly Lock _lock = new();
    private readonly TimeProvider _time;
    private long _sequence;

    public ConsoleLog(TimeProvider time, int capacity = 2000)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _time = time;
        _capacity = capacity;
        _lines = new Queue<ConsoleLine>(capacity);
    }

    public event Action<ConsoleLine>? LineAdded;

    public IReadOnlyList<ConsoleLine> Snapshot()
    {
        lock (_lock)
        {
            return [.. _lines];
        }
    }

    public ConsoleLine Append(ConsoleLineKind kind, string text)
    {
        ConsoleLine line;
        lock (_lock)
        {
            line = new ConsoleLine(++_sequence, _time.GetLocalNow(), kind, text);
            if (_lines.Count == _capacity)
            {
                _lines.Dequeue();
            }
            _lines.Enqueue(line);
        }

        var handlers = LineAdded;
        if (handlers is not null)
        {
            foreach (var h in handlers.GetInvocationList().Cast<Action<ConsoleLine>>())
            {
                try
                {
                    h(line);
                }
                catch
                {
                    // Un suscriptor roto (por ejemplo, un circuito de Blazor cerrado) no debe afectar al resto.
                }
            }
        }
        return line;
    }
}
