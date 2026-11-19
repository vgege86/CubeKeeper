using CubeKeeper.Core.Server;
using CubeKeeper.Infrastructure.Persistence;
using CubeKeeper.Infrastructure.Players;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace CubeKeeper.Infrastructure.Tests;

public class PlayerTrackingServiceTests
{
    private sealed class DbFactory(string path) : IDbContextFactory<CubeKeeperDbContext>
    {
        public CubeKeeperDbContext CreateDbContext() =>
            new(new DbContextOptionsBuilder<CubeKeeperDbContext>().UseSqlite($"Data Source={path}").Options);
    }

    [Fact]
    public async Task Sessions_are_recorded_and_closed_when_server_stops()
    {
        await using var f = new SupervisorFixture();
        var factory = new DbFactory(f.Paths.Database);
        await using (var db = factory.CreateDbContext())
        {
            await db.Database.MigrateAsync();
        }

        var tracking = new PlayerTrackingService(f.Supervisor, f.Console, factory, TimeProvider.System, NullLogger<PlayerTrackingService>.Instance);
        await tracking.StartAsync(CancellationToken.None);
        try
        {
            await f.Supervisor.StartAsync();
            await f.WaitForStatusAsync(ServerStatus.Running);

            await f.Supervisor.SendCommandAsync("join Alucard#9583 2535400000000001");
            await f.Supervisor.SendCommandAsync("join Peque 2535400000000002");
            await WaitAsync(() => tracking.Online.Count == 2);

            await f.Supervisor.SendCommandAsync("leave Peque 2535400000000002");
            await WaitAsync(() => tracking.Online.Count == 1);

            await f.Supervisor.StopAsync();
            await WaitAsync(() => tracking.Online.Count == 0);

            var stats = new PlayerStatsService(factory, TimeProvider.System);
            await WaitAsync(async () => (await stats.GetRecentSessionsAsync()).All(s => s.DisconnectedAt is not null));

            var players = await stats.GetPlayersAsync();
            Assert.Equal(2, players.Count);
            Assert.Contains(players, p => p.Name == "Alucard#9583" && p.Xuid == "2535400000000001" && p.Sessions == 1);

            var sessions = await stats.GetRecentSessionsAsync();
            Assert.Contains(sessions, s => s.Name == "Peque" && s.EndReason == SessionEndReason.Disconnected);
            Assert.Contains(sessions, s => s.Name == "Alucard#9583" && s.EndReason == SessionEndReason.ServerStopped);
        }
        finally
        {
            await tracking.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Sessions_left_open_by_a_crash_are_closed_at_startup()
    {
        await using var f = new SupervisorFixture();
        var factory = new DbFactory(f.Paths.Database);
        var t0 = DateTimeOffset.UtcNow.AddHours(-2);
        await using (var db = factory.CreateDbContext())
        {
            await db.Database.MigrateAsync();
            db.Players.Add(new Player { Xuid = "9", Name = "Steve", FirstSeen = t0, LastSeen = t0.AddMinutes(40) });
            db.PlayerSessions.Add(new PlayerSession { Xuid = "9", Name = "Steve", ConnectedAt = t0 });
            await db.SaveChangesAsync();
        }

        var tracking = new PlayerTrackingService(f.Supervisor, f.Console, factory, TimeProvider.System, NullLogger<PlayerTrackingService>.Instance);
        await tracking.StartAsync(CancellationToken.None);
        await tracking.StopAsync(CancellationToken.None);

        var session = Assert.Single(await new PlayerStatsService(factory, TimeProvider.System).GetRecentSessionsAsync());
        Assert.Equal(SessionEndReason.Interrupted, session.EndReason);
        Assert.Equal(TimeSpan.FromMinutes(40), session.Duration);
    }

    [Fact]
    public void Played_time_is_clipped_to_the_period()
    {
        var day = new DateTimeOffset(2026, 11, 19, 0, 0, 0, TimeSpan.Zero);
        var sessions = new[]
        {
            new PlayerSession { Xuid = "1", Name = "a", ConnectedAt = day.AddHours(-1), DisconnectedAt = day.AddHours(1) },
            new PlayerSession { Xuid = "1", Name = "a", ConnectedAt = day.AddHours(10) },
        };
        Assert.Equal(TimeSpan.FromHours(3), PlayerStatsService.Sum(sessions, day, day.AddHours(12)));
        Assert.Equal(TimeSpan.FromHours(4), PlayerStatsService.Sum(sessions, DateTimeOffset.MinValue, day.AddHours(12)));
    }

    private static Task WaitAsync(Func<bool> condition) => WaitAsync(() => Task.FromResult(condition()));

    private static async Task WaitAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "La condición no se cumplió a tiempo.");
            await Task.Delay(50);
        }
    }
}
