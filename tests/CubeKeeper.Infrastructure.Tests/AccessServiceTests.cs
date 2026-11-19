using CubeKeeper.Core.Access;
using CubeKeeper.Core.Server;
using CubeKeeper.Infrastructure.Access;
using CubeKeeper.Infrastructure.Persistence;
using CubeKeeper.Infrastructure.Players;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace CubeKeeper.Infrastructure.Tests;

public class AccessServiceTests
{
    private sealed class DbFactory(string path) : IDbContextFactory<CubeKeeperDbContext>
    {
        public CubeKeeperDbContext CreateDbContext() =>
            new(new DbContextOptionsBuilder<CubeKeeperDbContext>().UseSqlite($"Data Source={path}").Options);
    }

    private static async Task<(PlayerTrackingService Tracking, AccessService Access)> StartAsync(SupervisorFixture f)
    {
        var factory = new DbFactory(f.Paths.Database);
        await using (var db = factory.CreateDbContext())
        {
            await db.Database.MigrateAsync();
        }
        var tracking = new PlayerTrackingService(f.Supervisor, f.Console, factory, TimeProvider.System, NullLogger<PlayerTrackingService>.Instance);
        var access = new AccessService(f.Supervisor, tracking, f.Paths, f.State, factory, TimeProvider.System, NullLogger<AccessService>.Instance);
        await tracking.StartAsync(CancellationToken.None);
        await access.StartAsync(CancellationToken.None);
        await f.Supervisor.StartAsync();
        await f.WaitForStatusAsync(ServerStatus.Running);
        return (tracking, access);
    }

    private static async Task StopAsync(PlayerTrackingService tracking, AccessService access)
    {
        await access.StopAsync(CancellationToken.None);
        await tracking.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Enrollment_records_request_kicks_and_approval_lets_player_in()
    {
        await using var f = new SupervisorFixture();
        var (tracking, access) = await StartAsync(f);
        try
        {
            await access.SetModeAsync(AccessMode.ApprovedOnly);
            Assert.Contains("allow-list=true", File.ReadAllText(f.Paths.ServerProperties));

            // Sin inscripción, el servidor (falso) rechaza en silencio: nadie entra ni deja rastro.
            await f.Supervisor.SendCommandAsync("join Nuevo 2535400000000009");
            await Task.Delay(300);
            Assert.Empty((await access.GetOverviewAsync()).Pending);

            await access.StartEnrollmentAsync(5);
            Assert.True(access.EnrollmentActive);
            await f.Supervisor.SendCommandAsync("join Nuevo 2535400000000009");
            await f.WaitForLineAsync(l => l.Contains("Kicked Nuevo from the game"));

            var request = Assert.Single((await WaitForAsync(access, o => o.Pending.Count == 1)).Pending);
            Assert.Equal("2535400000000009", request.Xuid);

            await access.ApproveAsync(request.Xuid, request.Name, PermissionLevel.Member);
            await access.StopEnrollmentAsync();

            Assert.Contains("2535400000000009", File.ReadAllText(access.AllowListPath));
            Assert.Contains("\"permission\": \"member\"", File.ReadAllText(access.PermissionsPath));
            Assert.Contains(f.Console.Snapshot(), l => l.Text == "allowlist reload");

            var kicksBefore = f.Console.Snapshot().Count(l => l.Text.Contains("Kicked Nuevo"));
            await f.Supervisor.SendCommandAsync("join Nuevo 2535400000000009");
            await WaitAsync(() => tracking.Online.Any(p => p.Xuid == "2535400000000009"));
            await Task.Delay(300);
            Assert.Equal(kicksBefore, f.Console.Snapshot().Count(l => l.Text.Contains("Kicked Nuevo")));
            Assert.Empty((await access.GetOverviewAsync()).Pending);
        }
        finally
        {
            await StopAsync(tracking, access);
        }
    }

    [Fact]
    public async Task Blocked_player_is_kicked_now_and_on_every_join()
    {
        await using var f = new SupervisorFixture();
        var (tracking, access) = await StartAsync(f);
        try
        {
            await f.Supervisor.SendCommandAsync("join Gamberro 2535400000000066");
            await WaitAsync(() => tracking.Online.Count == 1);

            await access.BlockAsync("2535400000000066", "Gamberro");
            await WaitAsync(() => tracking.Online.Count == 0);

            await f.Supervisor.SendCommandAsync("join Gamberro 2535400000000066");
            await f.WaitForLineAsync(l => l.Contains("Kicked Gamberro") && l.Contains("bloqueado"));

            var overview = await access.GetOverviewAsync();
            Assert.Equal("2535400000000066", Assert.Single(overview.Blocked).Xuid);

            await access.UnblockAsync("2535400000000066");
            Assert.Empty((await access.GetOverviewAsync()).Blocked);
        }
        finally
        {
            await StopAsync(tracking, access);
        }
    }

    [Fact]
    public async Task Switching_to_approved_only_kicks_unapproved_players_online()
    {
        await using var f = new SupervisorFixture();
        var (tracking, access) = await StartAsync(f);
        try
        {
            await access.ApproveAsync("2535400000000001", "Alucard#9583", PermissionLevel.Operator);
            await f.Supervisor.SendCommandAsync("join Alucard#9583 2535400000000001");
            await f.Supervisor.SendCommandAsync("join Visita 2535400000000005");
            await WaitAsync(() => tracking.Online.Count == 2);

            await access.SetModeAsync(AccessMode.ApprovedOnly);
            await WaitAsync(() => tracking.Online.Count == 1);

            Assert.Equal("Alucard#9583", Assert.Single(tracking.Online).Name);
            Assert.Contains(f.Console.Snapshot(), l => l.Text == "allowlist on");
        }
        finally
        {
            await StopAsync(tracking, access);
        }
    }

    [Fact]
    public async Task Enrollment_requires_approved_only_mode_and_sane_duration()
    {
        await using var f = new SupervisorFixture();
        var (tracking, access) = await StartAsync(f);
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => access.StartEnrollmentAsync(5));
            await access.SetModeAsync(AccessMode.ApprovedOnly);
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => access.StartEnrollmentAsync(0));
            await Assert.ThrowsAsync<ArgumentException>(() => access.ApproveAsync("abc", "x", PermissionLevel.Member));
        }
        finally
        {
            await StopAsync(tracking, access);
        }
    }

    private static async Task<AccessOverview> WaitForAsync(AccessService access, Func<AccessOverview, bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            var o = await access.GetOverviewAsync();
            if (condition(o))
            {
                return o;
            }
            Assert.True(DateTime.UtcNow < deadline, "La condición no se cumplió a tiempo.");
            await Task.Delay(50);
        }
    }

    private static async Task WaitAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "La condición no se cumplió a tiempo.");
            await Task.Delay(50);
        }
    }
}
