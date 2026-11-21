using CubeKeeper.Infrastructure.Persistence;
using CubeKeeper.Infrastructure.Players;
using Microsoft.EntityFrameworkCore;

namespace CubeKeeper.Infrastructure.Tests;

public sealed class PlayerStatsDailyTests : IDisposable
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), "ck-stats-" + Guid.NewGuid().ToString("N") + ".db");

    private sealed class DbFactory(string path) : IDbContextFactory<CubeKeeperDbContext>
    {
        public CubeKeeperDbContext CreateDbContext() =>
            new(new DbContextOptionsBuilder<CubeKeeperDbContext>().UseSqlite($"Data Source={path}").Options);
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    [Fact]
    public async Task Splits_playtime_by_day_for_everyone_who_played_in_the_period()
    {
        var factory = new DbFactory(_db);
        var now = new DateTimeOffset(2026, 11, 21, 20, 0, 0, TimeSpan.Zero);
        await using (var db = factory.CreateDbContext())
        {
            await db.Database.MigrateAsync();
            db.Players.AddRange(
                new Player { Xuid = "1", Name = "Papa", FirstSeen = now.AddDays(-30), LastSeen = now },
                new Player { Xuid = "2", Name = "Lucía", FirstSeen = now.AddDays(-20), LastSeen = now },
                new Player { Xuid = "3", Name = "Antiguo", FirstSeen = now.AddDays(-10), LastSeen = now.AddDays(-10) });
            db.PlayerSessions.AddRange(
                // Ayer de 23:30 a 00:30: media hora en cada día.
                new PlayerSession { Xuid = "2", Name = "Lucía", ConnectedAt = now.Date.AddDays(-1).AddHours(23).AddMinutes(30), DisconnectedAt = now.Date.AddMinutes(30) },
                // Hoy, sesión abierta desde las 19:00.
                new PlayerSession { Xuid = "1", Name = "Papa", ConnectedAt = now.AddHours(-1) },
                // Fuera del periodo.
                new PlayerSession { Xuid = "3", Name = "Antiguo", ConnectedAt = now.AddDays(-10), DisconnectedAt = now.AddDays(-10).AddHours(2) });
            await db.SaveChangesAsync();
        }
        var stats = new PlayerStatsService(factory, new Clock(now));

        var daily = await stats.GetDailyAllAsync(7);

        Assert.Equal(["Papa", "Lucía"], daily.Select(d => d.Name));
        Assert.Equal([0, 1], daily.Select(d => d.Rank)); // el color sigue al jugador por antigüedad
        var lucia = daily[1];
        Assert.Equal(7, lucia.Days.Count);
        Assert.Equal(TimeSpan.FromMinutes(30), lucia.Days[5]); // ayer
        Assert.Equal(TimeSpan.FromMinutes(30), lucia.Days[6]); // hoy
        Assert.Equal(2, lucia.DaysPlayed);
        Assert.Equal(TimeSpan.FromHours(1), daily[0].Days[6]);
        Assert.Equal(new DateOnly(2026, 11, 15), stats.DailyDates(7)[0]);
        Assert.Equal(new DateOnly(2026, 11, 21), stats.DailyDates(7)[6]);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (File.Exists(_db))
        {
            File.Delete(_db);
        }
    }
}
