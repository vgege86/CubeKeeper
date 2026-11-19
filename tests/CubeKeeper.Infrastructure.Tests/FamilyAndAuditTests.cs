using CubeKeeper.Core.Family;
using CubeKeeper.Core.Server;
using CubeKeeper.Infrastructure.Access;
using CubeKeeper.Infrastructure.Audit;
using CubeKeeper.Infrastructure.Family;
using CubeKeeper.Infrastructure.Persistence;
using CubeKeeper.Infrastructure.Players;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace CubeKeeper.Infrastructure.Tests;

public class FamilyAndAuditTests
{
    private sealed class DbFactory(string path) : IDbContextFactory<CubeKeeperDbContext>
    {
        public CubeKeeperDbContext CreateDbContext() =>
            new(new DbContextOptionsBuilder<CubeKeeperDbContext>().UseSqlite($"Data Source={path}").Options);
    }

    private sealed record Stack(PlayerTrackingService Tracking, AccessService Access, FamilyService Family, AuditLog Audit);

    private static async Task<Stack> StartAsync(SupervisorFixture f)
    {
        var factory = new DbFactory(f.Paths.Database);
        await using (var db = factory.CreateDbContext())
        {
            await db.Database.MigrateAsync();
        }
        var tracking = new PlayerTrackingService(f.Supervisor, f.Console, factory, TimeProvider.System, NullLogger<PlayerTrackingService>.Instance);
        var access = new AccessService(f.Supervisor, tracking, f.Paths, f.State, factory, TimeProvider.System, NullLogger<AccessService>.Instance);
        var audit = new AuditLog(factory, TimeProvider.System, NullLogger<AuditLog>.Instance);
        var family = new FamilyService(f.Supervisor, tracking, access, new PlayerStatsService(factory, TimeProvider.System), audit, factory, TimeProvider.System, NullLogger<FamilyService>.Instance);
        await tracking.StartAsync(CancellationToken.None);
        await access.StartAsync(CancellationToken.None);
        await family.StartAsync(CancellationToken.None);
        await f.Supervisor.StartAsync();
        await f.WaitForStatusAsync(ServerStatus.Running);
        return new Stack(tracking, access, family, audit);
    }

    private static async Task StopAsync(Stack s)
    {
        await s.Family.StopAsync(CancellationToken.None);
        await s.Access.StopAsync(CancellationToken.None);
        await s.Tracking.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Player_without_time_left_is_kicked_on_join_and_it_is_audited()
    {
        await using var f = new SupervisorFixture();
        var s = await StartAsync(f);
        try
        {
            await s.Family.SaveRulesAsync("2535400000000003", new PlayerRules(true, 0, []));

            await f.Supervisor.SendCommandAsync("join Peque 2535400000000003");
            await f.WaitForLineAsync(l => l.Contains("Kicked Peque") && l.Contains("tiempo de juego"), 15000);

            await WaitAsync(async () => (await s.Audit.GetRecentAsync(10, AuditLog.SystemActor, "family.")).Count == 1);
            var entry = (await s.Audit.GetRecentAsync(10)).First(e => e.Action == "family.kick");
            Assert.Equal("Peque", entry.Target);
        }
        finally
        {
            await StopAsync(s);
        }
    }

    [Fact]
    public async Task Player_near_the_end_of_the_window_gets_a_private_warning()
    {
        var now = DateTime.Now;
        if (now.TimeOfDay > new TimeSpan(23, 50, 0) || now.TimeOfDay < new TimeSpan(0, 10, 0))
        {
            return; // La franja de prueba cruzaría la medianoche.
        }
        await using var f = new SupervisorFixture();
        var s = await StartAsync(f);
        try
        {
            var from = TimeOnly.FromDateTime(now.AddMinutes(-5));
            var to = TimeOnly.FromDateTime(now.AddMinutes(3));
            await f.Supervisor.SendCommandAsync("join Peque 2535400000000003");
            await WaitAsync(() => Task.FromResult(s.Tracking.Online.Count == 1));

            await s.Family.SaveRulesAsync("2535400000000003", new PlayerRules(true, null, [new ScheduleRule(WeekDays.All, from, to)]));

            await f.WaitForLineAsync(l => l.StartsWith("tell \"Peque\"", StringComparison.Ordinal) && l.Contains("5 minutos") == false && l.Contains("minuto"), 15000);
            Assert.Single(s.Tracking.Online); // Avisado, no expulsado.

            var status = await s.Family.GetStatusAsync("2535400000000003");
            Assert.True(status.Allowed);
            Assert.True(status.Remaining <= TimeSpan.FromMinutes(3));
        }
        finally
        {
            await StopAsync(s);
        }
    }

    [Fact]
    public async Task Extra_time_today_is_saved_and_rules_round_trip()
    {
        await using var f = new SupervisorFixture();
        var s = await StartAsync(f);
        try
        {
            var rules = new PlayerRules(true, 60, [new ScheduleRule(WeekDays.Weekend, new TimeOnly(10, 0), new TimeOnly(21, 0))]);
            await s.Family.SaveRulesAsync("1", rules);
            var updated = await s.Family.SetExtraTodayAsync("1", 30);

            var loaded = await s.Family.GetRulesAsync("1");
            Assert.Equal(60, loaded.DailyMinutes);
            Assert.Single(loaded.Schedule);
            Assert.Equal(30, loaded.ExtraFor(DateOnly.FromDateTime(DateTime.Now)));
            Assert.Equal(updated.ExtraDate, loaded.ExtraDate);

            await Assert.ThrowsAsync<ArgumentException>(() => s.Family.SaveRulesAsync("1", rules with { DailyMinutes = 5000 }));
            await Assert.ThrowsAsync<ArgumentException>(() => s.Family.SaveRulesAsync("1", rules with { Schedule = [new ScheduleRule(WeekDays.None, new TimeOnly(1, 0), new TimeOnly(2, 0))] }));
        }
        finally
        {
            await StopAsync(s);
        }
    }

    [Fact]
    public async Task Audit_log_filters_by_actor_and_category()
    {
        await using var f = new SupervisorFixture();
        var factory = new DbFactory(f.Paths.Database);
        await using (var db = factory.CreateDbContext())
        {
            await db.Database.MigrateAsync();
        }
        var audit = new AuditLog(factory, TimeProvider.System, NullLogger<AuditLog>.Instance);
        await audit.RecordAsync("admin", "server.start");
        await audit.RecordAsync("admin", "access.approve", "Peque", "Member");
        await audit.RecordAsync("papa", "server.stop");
        await audit.RecordAsync(new string('x', 100), "game.say", null, new string('y', 5000));

        Assert.Equal(4, (await audit.GetRecentAsync()).Count);
        Assert.Equal(2, (await audit.GetRecentAsync(actor: "admin")).Count);
        Assert.Equal(2, (await audit.GetRecentAsync(actionPrefix: "server.")).Count);
        Assert.Equal("server.stop", (await audit.GetRecentAsync())[1].Action);
        Assert.Contains("papa", await audit.GetActorsAsync());
        Assert.Equal(2000, (await audit.GetRecentAsync(actionPrefix: "game.")).Single().Details!.Length);
    }

    private static async Task WaitAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "La condición no se cumplió a tiempo.");
            await Task.Delay(100);
        }
    }
}
