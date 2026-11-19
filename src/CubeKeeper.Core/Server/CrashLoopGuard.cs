namespace CubeKeeper.Core.Server;

/// <summary>
/// Decide si se puede reiniciar automáticamente tras una caída: como máximo <c>maxRestarts</c>
/// reinicios dentro de una ventana deslizante. Evita bucles de arranque y caída.
/// </summary>
public sealed class CrashLoopGuard
{
    private readonly int _maxRestarts;
    private readonly TimeSpan _window;
    private readonly Queue<DateTimeOffset> _restarts = new();
    private readonly Lock _lock = new();

    public CrashLoopGuard(int maxRestarts, TimeSpan window)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxRestarts);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(window, TimeSpan.Zero);
        _maxRestarts = maxRestarts;
        _window = window;
    }

    /// <summary>Registra una caída en <paramref name="now"/> y devuelve true si se permite reiniciar.</summary>
    public bool TryRegisterRestart(DateTimeOffset now)
    {
        lock (_lock)
        {
            while (_restarts.Count > 0 && now - _restarts.Peek() > _window)
            {
                _restarts.Dequeue();
            }
            if (_restarts.Count >= _maxRestarts)
            {
                return false;
            }
            _restarts.Enqueue(now);
            return true;
        }
    }

    /// <summary>Olvida las caídas anteriores (por ejemplo, tras un arranque manual).</summary>
    public void Reset()
    {
        lock (_lock)
        {
            _restarts.Clear();
        }
    }
}
