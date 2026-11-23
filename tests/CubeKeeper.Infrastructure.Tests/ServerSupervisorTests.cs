using System.Net.Sockets;
using CubeKeeper.Core.Logs;
using CubeKeeper.Core.Server;
using CubeKeeper.Infrastructure.Health;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace CubeKeeper.Infrastructure.Tests;

public class ServerSupervisorTests
{
    [Fact]
    public async Task Start_reaches_running_and_reports_version()
    {
        await using var f = new SupervisorFixture();
        await f.Supervisor.StartAsync();
        await f.WaitForStatusAsync(ServerStatus.Running);

        Assert.Equal("1.26.0.2", f.Supervisor.Snapshot.RunningVersion);
        Assert.NotNull(f.Supervisor.Snapshot.ProcessId);
        Assert.True(f.State.Current.DesiredRunning);
    }

    [Fact]
    public async Task Commands_reach_stdin_and_player_events_are_raised()
    {
        await using var f = new SupervisorFixture();
        var events = new List<BdsLogEvent>();
        f.Supervisor.LogEvent += e => { lock (events) { events.Add(e); } };

        await f.Supervisor.StartAsync();
        await f.WaitForStatusAsync(ServerStatus.Running);
        await f.Supervisor.SendCommandAsync("join Alucard#9583 2535400000000001");
        await f.Supervisor.SendCommandAsync("list");
        // La línea llega a la consola un instante antes de que se emita el evento: esperar al evento.
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            lock (events)
            {
                if (events.Any(e => e is PlayerCountReported))
                {
                    break;
                }
            }
            Assert.True(DateTime.UtcNow < deadline, "No llegó el evento de la respuesta a list.");
            await Task.Delay(20);
        }

        lock (events)
        {
            var c = Assert.Single(events.OfType<PlayerConnected>());
            Assert.Equal("Alucard#9583", c.Name);
            Assert.Equal("2535400000000001", c.Xuid);
            Assert.Contains(events, e => e is PlayerCountReported { Online: 1, Max: 10 });
        }
    }

    [Fact]
    public async Task Stop_sends_stop_and_waits_for_clean_exit()
    {
        await using var f = new SupervisorFixture();
        await f.Supervisor.StartAsync();
        await f.WaitForStatusAsync(ServerStatus.Running);

        await f.Supervisor.StopAsync();

        Assert.Equal(ServerStatus.Stopped, f.Supervisor.Snapshot.Status);
        Assert.False(f.State.Current.DesiredRunning);
        Assert.Contains(f.Console.Snapshot(), l => l.Text == "Quit correctly");
        Assert.Contains(f.Console.Snapshot(), l => l.Text.Contains("código 0"));
    }

    [Fact]
    public async Task Stop_kills_server_that_does_not_respond()
    {
        await using var f = new SupervisorFixture(o => o.StopTimeoutSeconds = 1);
        await f.Supervisor.StartAsync();
        await f.WaitForStatusAsync(ServerStatus.Running);
        await f.Supervisor.SendCommandAsync("hang");

        await f.Supervisor.StopAsync();

        Assert.Equal(ServerStatus.Stopped, f.Supervisor.Snapshot.Status);
        Assert.Contains(f.Console.Snapshot(), l => l.Text.Contains("se fuerza la parada"));
    }

    [Fact]
    public async Task Crash_triggers_automatic_restart()
    {
        await using var f = new SupervisorFixture();
        await f.Supervisor.StartAsync();
        await f.WaitForStatusAsync(ServerStatus.Running);
        var firstPid = f.Supervisor.Snapshot.ProcessId;

        await f.Supervisor.SendCommandAsync("crash");
        await f.WaitForLineAsync(l => l.Contains("cerrado inesperadamente"));
        await f.WaitForStatusAsync(ServerStatus.Running);

        Assert.NotEqual(firstPid, f.Supervisor.Snapshot.ProcessId);
    }

    [Fact]
    public async Task Crash_loop_gives_up_after_max_restarts()
    {
        await using var f = new SupervisorFixture(
            o => o.MaxRestarts = 2,
            new Dictionary<string, string> { ["FAKE_BDS_CRASH_ON_START"] = "1" });

        await f.Supervisor.StartAsync();
        await f.WaitForStatusAsync(ServerStatus.Failed, 30000);

        var crashes = f.Console.Snapshot().Count(l => l.Text.Contains("cerrado inesperadamente"));
        Assert.Equal(3, crashes); // primer arranque + 2 reintentos
        Assert.NotNull(f.Supervisor.Snapshot.LastError);
    }

    [Fact]
    public async Task Send_command_when_stopped_fails()
    {
        await using var f = new SupervisorFixture();
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Supervisor.SendCommandAsync("list"));
    }

    [Fact]
    public async Task Commands_with_newlines_are_rejected()
    {
        await using var f = new SupervisorFixture();
        await f.Supervisor.StartAsync();
        await f.WaitForStatusAsync(ServerStatus.Running);
        await Assert.ThrowsAsync<ArgumentException>(() => f.Supervisor.SendCommandAsync("say hola\nstop"));
    }

    [Fact]
    public async Task Shutdown_stops_server_but_keeps_desired_state()
    {
        await using var f = new SupervisorFixture();
        await f.Supervisor.StartAsync();
        await f.WaitForStatusAsync(ServerStatus.Running);

        await f.Supervisor.ShutdownAsync(CancellationToken.None);

        Assert.Equal(ServerStatus.Stopped, f.Supervisor.Snapshot.Status);
        Assert.True(f.State.Current.DesiredRunning);
    }

    [Fact]
    public async Task Health_probe_checks_tcp_port()
    {
        await using var f = new SupervisorFixture();
        var probe = new BdsHealthProbe(f.Supervisor, f.Paths);

        var stopped = await probe.CheckHealthAsync(new HealthCheckContext());
        Assert.Equal(HealthStatus.Healthy, stopped.Status);

        await f.Supervisor.StartAsync();
        await f.WaitForStatusAsync(ServerStatus.Running);
        var running = await probe.CheckHealthAsync(new HealthCheckContext());
        Assert.Equal(HealthStatus.Healthy, running.Status);
        Assert.Equal(f.Port, running.Data["port"]);
    }

    [Fact]
    public async Task Health_probe_is_unhealthy_when_port_is_closed_but_server_should_run()
    {
        await using var f = new SupervisorFixture();
        await f.Supervisor.StartAsync();
        await f.WaitForStatusAsync(ServerStatus.Running);
        // Apuntamos el probe a otro puerto cerrado.
        File.WriteAllText(f.Paths.ServerProperties, "server-port=1\n");

        var result = await new BdsHealthProbe(f.Supervisor, f.Paths).CheckHealthAsync(new HealthCheckContext());

        // En marcha pero sin puerto: aviso (Degraded), sin marcar el contenedor como roto.
        Assert.Equal(HealthStatus.Degraded, result.Status);
    }
}

public class HealthProbeServerIpTests
{
    [Fact]
    public async Task Probe_uses_server_ip_when_set()
    {
        await using var f = new SupervisorFixture();
        await f.Supervisor.StartAsync();
        await f.WaitForStatusAsync(ServerStatus.Running);
        File.WriteAllText(f.Paths.ServerProperties, $"server-port={f.Port}\nserver-ip=127.0.0.1\n");

        var result = await new BdsHealthProbe(f.Supervisor, f.Paths).CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Equal("127.0.0.1", result.Data["host"]);
    }
}
