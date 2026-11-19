using CubeKeeper.Core.Abstractions;
using CubeKeeper.Core.Logs;

namespace CubeKeeper.Infrastructure.Process;

/// <summary>
/// Envía un comando a BDS y espera su respuesta en la consola. BDS no numera las respuestas, así que los comandos
/// que esperan respuesta se ejecutan de uno en uno.
/// </summary>
public sealed class ConsoleCommandRunner(IServerController server, IConsoleLog console)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// Envía <paramref name="command"/> y va pasando a <paramref name="tryComplete"/> los mensajes de salida que llegan
    /// (sin el prefijo de fecha y nivel) hasta que devuelve un resultado distinto de null.
    /// </summary>
    /// <exception cref="TimeoutException">Si no se completa a tiempo.</exception>
    public async Task<T> RunAsync<T>(string command, Func<IReadOnlyList<string>, T?> tryComplete, TimeSpan timeout, CancellationToken ct = default)
        where T : class
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        var lines = new List<string>();
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnLine(ConsoleLine line)
        {
            if (line.Kind != ConsoleLineKind.Output)
            {
                return;
            }
            lock (lines)
            {
                lines.Add(BdsLogLine.Parse(line.Text).Message);
                try
                {
                    if (tryComplete(lines) is { } result)
                    {
                        done.TrySetResult(result);
                    }
                }
                catch (Exception ex)
                {
                    done.TrySetException(ex);
                }
            }
        }

        console.LineAdded += OnLine;
        try
        {
            await server.SendCommandAsync(command, ct).ConfigureAwait(false);
            try
            {
                return await done.Task.WaitAsync(timeout, ct).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                throw new TimeoutException($"El servidor no respondió a «{command}» en {timeout.TotalSeconds:0} s.");
            }
        }
        finally
        {
            console.LineAdded -= OnLine;
            _gate.Release();
        }
    }
}
