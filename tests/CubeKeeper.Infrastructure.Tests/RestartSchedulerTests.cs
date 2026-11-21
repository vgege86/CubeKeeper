using CubeKeeper.Core.Abstractions;
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
        Assert.Null(restart.SaveSettings(true, "05:30", "5,1", skipIfPlayers: true));

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

    [Fact]
    public async Task Skips_when_someone_is_playing_or_the_server_is_stopped()
    {
        await using var f = new SupervisorFixture();
        var clock = new ManualClock(Morning);
        var restart = Create(f, clock, new Players("Alucard"));
        restart.SaveSettings(true, "05:30", "", skipIfPlayers: true);
        await restart.RunIfDueAsync(default);

        clock.Now = Morning.AddMinutes(31);
        await restart.RunIfDueAsync(default);
        Assert.False(restart.LastRun!.Restarted);
        Assert.Contains("no estaba en marcha", restart.LastRun.Message);

        await f.Supervisor.StartAsync();
        await f.WaitForStatusAsync(ServerStatus.Running);
        clock.Now = Morning.AddDays(1).AddMinutes(31);
        await restart.RunIfDueAsync(default);
        Assert.False(restart.LastRun!.Restarted);
        Assert.Contains("Alucard", restart.LastRun.Message);
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
