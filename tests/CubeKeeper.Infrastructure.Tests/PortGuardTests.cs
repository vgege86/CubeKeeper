using CubeKeeper.Core.Resources;
using CubeKeeper.Core.Server;
using CubeKeeper.Infrastructure.Process;

namespace CubeKeeper.Infrastructure.Tests;

/// <summary>BDS no abre el puerto TCP si quedan conexiones recientes: se espera a que se liberen y se vigila.</summary>
public class PortGuardTests
{
    private sealed class ScriptedProbe(Func<int, IReadOnlyList<int>> next) : ITcpPortProbe
    {
        private int _calls;
        public IReadOnlyList<int>? States(int port) => next(Interlocked.Increment(ref _calls));
    }

    [Fact]
    public async Task Waits_for_recent_connections_to_clear_before_starting()
    {
        // Las dos primeras consultas ven un TIME_WAIT; luego el puerto está libre y después escucha.
        var probe = new ScriptedProbe(n => n <= 2 ? [TcpSockets.TimeWait] : [TcpSockets.Listen]);
        await using var f = new SupervisorFixture(o =>
        {
            o.PortCheckWithLaunchCommand = true;
            o.PortCheckSeconds = 10;
        }, ports: probe);

        await f.Supervisor.StartAsync();
        await f.WaitForStatusAsync(ServerStatus.Running);

        var lines = f.Console.Snapshot().Select(l => l.Text).ToList();
        Assert.Contains(lines, l => l.Contains("Esperando a que se libere el puerto TCP", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("libre", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Restarts_once_when_the_port_is_never_opened_and_then_reports_it()
    {
        // Nunca escucha: se reinicia una vez y, a la segunda, se avisa sin volver a reiniciar.
        var probe = new ScriptedProbe(_ => []);
        await using var f = new SupervisorFixture(o =>
        {
            o.PortCheckWithLaunchCommand = true;
            o.PortCheckSeconds = 3;
        }, ports: probe);

        await f.Supervisor.StartAsync();
        await f.WaitForLineAsync(l => l.Contains("Se reinicia para que lo abra", StringComparison.Ordinal), 20000);
        await f.WaitForLineAsync(l => l.Contains("sigue sin abrir el puerto", StringComparison.Ordinal), 30000);

        Assert.Equal(ServerStatus.Running, f.Supervisor.Snapshot.Status);
        Assert.Contains("sigue sin abrir el puerto", f.Supervisor.Snapshot.LastError, StringComparison.Ordinal);
    }
}
