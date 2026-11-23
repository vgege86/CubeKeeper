using CubeKeeper.Core.Abstractions;
using CubeKeeper.Core.Logs;
using CubeKeeper.Core.Players;
using CubeKeeper.Core.Server;
using CubeKeeper.Infrastructure.Scheduling;
using CubeKeeper.Infrastructure.State;
using Microsoft.Extensions.Logging.Abstractions;

namespace CubeKeeper.Infrastructure.Tests;

public class RestartSchedulerTests
{
    /// <summary>Reloj manual: las esperas terminan al momento y adelantan el reloj.</summary>
    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;

        public override DateTimeOffset GetUtcNow() => Now;

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            if (dueTime > TimeSpan.Zero && dueTime != Timeout.InfiniteTimeSpan)
            {
                Now += dueTime;
            }
            ThreadPool.QueueUserWorkItem(_ => callback(state));
            return new NoTimer();
        }

        private sealed class NoTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class Players(params string[] names) : IPlayerPresence
    {
        public IReadOnlyList<OnlinePlayer> Online { get; } = names.Select((n, i) => new OnlinePlayer(n, $"25354000000000{i}", DateTimeOffset.UtcNow)).ToList();
        public event Action? Changed;
        public Task ResyncAsync(CancellationToken ct = default)
        {
            _ = Changed;
            return Task.CompletedTask;
        }
    }

    private static readonly DateTimeOffset Morning = new(2026, 11, 21, 5, 0, 0, TimeSpan.Zero);

    private static RestartScheduler Create(SupervisorFixture f, ManualClock clock, IPlayerPresence presence) =>
        new(f.Supervisor, presence, f.Console, f.State, clock, NullLogger<RestartScheduler>.Instance);

    [Fact]
    public async Task Restarts_at_the_configured_time_after_warning_in_chat()
    {
        await using var f = new SupervisorFixture();
        await f.Supervisor.StartAsync();
        await f.WaitForStatusAsync(ServerStatus.Running);
        var startedAt = f.Supervisor.Snapshot.StartedAt;
        var clock = new ManualClock(Morning);
        var restart = Create(f, clock, new Players());
        Assert.Null(restart.SaveSettings(true, "05:30", "5,1", waitForEmpty: false));

        await restart.RunIfDueAsync(default);
        Assert.Equal(new DateTimeOffset(2026, 11, 21, 5, 30, 0, TimeSpan.Zero), restart.NextRunAt);
        Assert.Null(restart.LastRun);

        clock.Now = new DateTimeOffset(2026, 11, 21, 5, 25, 1, TimeSpan.Zero);
        await restart.RunIfDueAsync(default);
        await f.WaitForStatusAsync(ServerStatus.Running);

        Assert.True(restart.LastRun!.Restarted, restart.LastRun.Message);
        Assert.NotEqual(startedAt, f.Supervisor.Snapshot.StartedAt);
        var lines = f.Console.Snapshot().Select(l => l.Text).ToList();
        Assert.Contains(lines, l => l.Contains("se reiniciará en 5 minutos"));
        Assert.Contains(lines, l => l.Contains("se reiniciará en 1 minuto para"));
        Assert.Equal(new DateTimeOffset(2026, 11, 22, 5, 30, 0, TimeSpan.Zero), restart.NextRunAt);
    }

    /// <summary>Hay jugadores hasta que el reloj llega a <paramref name="until"/>.</summary>
    private sealed class PlayersUntil(ManualClock clock, DateTimeOffset until, params string[] names) : IPlayerPresence
    {
        private readonly IReadOnlyList<OnlinePlayer> _players =
            names.Select((n, i) => new OnlinePlayer(n, $"25354000000000{i}", DateTimeOffset.UtcNow)).ToList();
        public IReadOnlyList<OnlinePlayer> Online => clock.Now < until ? _players : [];
        public event Action? Changed;
        public Task ResyncAsync(CancellationToken ct = default)
        {
            _ = Changed;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Does_nothing_when_the_server_is_stopped()
    {
        await using var f = new SupervisorFixture();
        var clock = new ManualClock(Morning);
        var restart = Create(f, clock, new Players("Alucard"));
        restart.SaveSettings(true, "05:30", "", waitForEmpty: true);
        await restart.RunIfDueAsync(default);

        clock.Now = Morning.AddMinutes(31);
        await restart.RunIfDueAsync(default);
        Assert.False(restart.LastRun!.Restarted);
        Assert.Contains("no estaba en marcha", restart.LastRun.Message);
    }

    [Fact]
    public async Task Waits_until_nobody_is_left_reminding_every_ten_minutes()
    {
        await using var f = new SupervisorFixture();
        await f.Supervisor.StartAsync();
        await f.WaitForStatusAsync(ServerStatus.Running);
        var clock = new ManualClock(Morning);
        // Alucard sigue jugando hasta las 06:05: 35 minutos después de la hora del reinicio.
        var restart = Create(f, clock, new PlayersUntil(clock, Morning.AddMinutes(65), "Alucard"));
        Assert.Null(restart.SaveSettings(true, "05:30", "30,15,5,1", waitForEmpty: true));

        // A las 05:00 ya toca el primer aviso (30 minutos antes): se hace todo seguido con el reloj manual.
        await restart.RunIfDueAsync(default);
        await f.WaitForStatusAsync(ServerStatus.Running);

        Assert.True(restart.LastRun!.Restarted, restart.LastRun.Message);
        Assert.Contains("tras esperar a que salieran todos", restart.LastRun.Message);
        Assert.True(clock.Now >= Morning.AddMinutes(65), clock.Now.ToString("O"));
        Assert.Null(restart.WaitingSince);
        var lines = f.Console.Snapshot().Select(l => l.Text).ToList();
        foreach (var m in new[] { "30 minutos", "15 minutos", "5 minutos", "1 minuto." })
        {
            Assert.Contains(lines, l => l.Contains($"Reinicio de mantenimiento en {m}"));
        }
        // A las 05:30, 05:40, 05:50 y 06:00: un recordatorio cada 10 minutos mientras quede alguien.
        Assert.Equal(4, f.Console.Snapshot().Count(l =>
            l.Kind == ConsoleLineKind.Command && l.Text.Contains("pendiente: el servidor se reiniciará en cuanto salgáis todos")));
    }

    [Fact]
    public async Task Restarts_on_time_with_people_inside_when_not_waiting()
    {
        await using var f = new SupervisorFixture();
        await f.Supervisor.StartAsync();
        await f.WaitForStatusAsync(ServerStatus.Running);
        var clock = new ManualClock(Morning);
        var restart = Create(f, clock, new Players("Alucard"));
        restart.SaveSettings(true, "05:30", "1", waitForEmpty: false);
        await restart.RunIfDueAsync(default);

        clock.Now = Morning.AddMinutes(29);
        await restart.RunIfDueAsync(default);
        await f.WaitForStatusAsync(ServerStatus.Running);

        Assert.True(restart.LastRun!.Restarted);
        Assert.Equal("Reiniciado.", restart.LastRun.Message);
        Assert.True(clock.Now < Morning.AddMinutes(31));
    }

    [Fact]
    public async Task Gives_up_after_waiting_too_long()
    {
        await using var f = new SupervisorFixture();
        await f.Supervisor.StartAsync();
        await f.WaitForStatusAsync(ServerStatus.Running);
        var clock = new ManualClock(Morning);
        var restart = Create(f, clock, new Players("Alucard"));
        restart.SaveSettings(true, "05:30", "", waitForEmpty: true);
        await restart.RunIfDueAsync(default);

        clock.Now = Morning.AddMinutes(30);
        await restart.RunIfDueAsync(default);

        Assert.False(restart.LastRun!.Restarted);
        Assert.Contains("seguía habiendo gente jugando (Alucard)", restart.LastRun.Message);
    }

    [Theory]
    [InlineData("5:30x", "1", "hora")]
    [InlineData("05:30", "cinco", "avisos")]
    public async Task Invalid_settings_are_rejected(string time, string warnings, string expected)
    {
        await using var f = new SupervisorFixture();
        var restart = Create(f, new ManualClock(Morning), new Players());
        Assert.Contains(expected, restart.SaveSettings(true, time, warnings, true));
        Assert.False(restart.Settings.Enabled);
    }
}
