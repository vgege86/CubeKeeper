using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace CubeKeeper.Infrastructure.Persistence;

/// <summary>Usuario del panel (no confundir con los jugadores).</summary>
public sealed class PanelUser : IdentityUser
{
}

public static class PanelRoles
{
    public const string Admin = "Admin";
}

public sealed class CubeKeeperDbContext(DbContextOptions<CubeKeeperDbContext> options)
    : IdentityDbContext<PanelUser>(options)
{
    public DbSet<Player> Players => Set<Player>();

    public DbSet<PlayerSession> PlayerSessions => Set<PlayerSession>();

    public DbSet<AccessRequest> AccessRequests => Set<AccessRequest>();

    public DbSet<PlayerRulesEntity> PlayerRules => Set<PlayerRulesEntity>();

    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<Player>(e =>
        {
            e.HasKey(p => p.Xuid);
            e.Property(p => p.Xuid).HasMaxLength(32);
            e.Property(p => p.Name).HasMaxLength(64);
            // SQLite no ordena ni compara DateTimeOffset: se guarda como entero (ticks UTC).
            e.Property(p => p.FirstSeen).HasConversion<DateTimeOffsetToBinaryConverter>();
            e.Property(p => p.LastSeen).HasConversion<DateTimeOffsetToBinaryConverter>();
            e.Property(p => p.BlockedAt).HasConversion<DateTimeOffsetToBinaryConverter>();
            e.HasIndex(p => p.Name);
            e.HasMany(p => p.Sessions).WithOne(s => s.Player).HasForeignKey(s => s.Xuid);
        });

        builder.Entity<PlayerSession>(e =>
        {
            e.Property(s => s.Xuid).HasMaxLength(32);
            e.Property(s => s.Name).HasMaxLength(64);
            e.Property(s => s.EndReason).HasConversion<string>().HasMaxLength(20);
            e.Property(s => s.ConnectedAt).HasConversion<DateTimeOffsetToBinaryConverter>();
            e.Property(s => s.DisconnectedAt).HasConversion<DateTimeOffsetToBinaryConverter>();
            e.Ignore(s => s.Duration);
            e.HasIndex(s => s.ConnectedAt);
            e.HasIndex(s => s.DisconnectedAt);
        });

        builder.Entity<AccessRequest>(e =>
        {
            e.Property(r => r.Xuid).HasMaxLength(32);
            e.Property(r => r.Name).HasMaxLength(64);
            e.Property(r => r.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(r => r.RequestedAt).HasConversion<DateTimeOffsetToBinaryConverter>();
            e.Property(r => r.DecidedAt).HasConversion<DateTimeOffsetToBinaryConverter>();
            e.HasIndex(r => new { r.Xuid, r.Status });
        });

        builder.Entity<PlayerRulesEntity>(e =>
        {
            e.ToTable("PlayerRules");
            e.HasKey(r => r.Xuid);
            e.Property(r => r.Xuid).HasMaxLength(32);
            e.Property(r => r.ScheduleJson).HasMaxLength(4000);
        });

        builder.Entity<AuditEntry>(e =>
        {
            e.Property(a => a.At).HasConversion<DateTimeOffsetToBinaryConverter>();
            e.Property(a => a.Actor).HasMaxLength(64);
            e.Property(a => a.Action).HasMaxLength(64);
            e.Property(a => a.Target).HasMaxLength(200);
            e.Property(a => a.Details).HasMaxLength(2000);
            e.HasIndex(a => a.At);
        });
    }
}
