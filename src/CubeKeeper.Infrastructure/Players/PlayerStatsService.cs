using CubeKeeper.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CubeKeeper.Infrastructure.Players;

public sealed record PlayerSummary(
    string Xuid,
    string Name,
    DateTimeOffset FirstSeen,
    DateTimeOffset LastSeen,
    int Sessions,
    TimeSpan TotalPlayed,
    TimeSpan PlayedToday);

public sealed record SessionRow(
    long Id,
    string Xuid,
    string Name,
    DateTimeOffset ConnectedAt,
    DateTimeOffset? DisconnectedAt,
    SessionEndReason? EndReason,
    TimeSpan Duration);

/// <summary>Consultas del historial de jugadores para la interfaz.</summary>
public sealed class PlayerStatsService(IDbContextFactory<CubeKeeperDbContext> dbFactory, TimeProvider time)
{
    public async Task<IReadOnlyList<PlayerSummary>> GetPlayersAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var players = await db.Players.AsNoTracking().Include(p => p.Sessions).ToListAsync(ct);
        var now = time.GetUtcNow();
        var todayStart = new DateTimeOffset(time.GetLocalNow().Date, time.GetLocalNow().Offset);

        return players
            .Select(p => new PlayerSummary(
                p.Xuid,
                p.Name,
                p.FirstSeen,
                p.LastSeen,
                p.Sessions.Count,
                Sum(p.Sessions, DateTimeOffset.MinValue, now),
                Sum(p.Sessions, todayStart, now)))
            .OrderByDescending(p => p.LastSeen)
            .ToList();
    }

    public async Task<IReadOnlyList<SessionRow>> GetRecentSessionsAsync(int take = 50, string? xuid = null, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var query = db.PlayerSessions.AsNoTracking();
        if (xuid is not null)
        {
            query = query.Where(s => s.Xuid == xuid);
        }
        // ConnectedAt se guarda como entero: se puede ordenar en SQLite.
        var rows = await query.OrderByDescending(s => s.ConnectedAt).Take(take).ToListAsync(ct);
        var now = time.GetUtcNow();
        return rows
            .Select(s => new SessionRow(s.Id, s.Xuid, s.Name, s.ConnectedAt, s.DisconnectedAt, s.EndReason,
                (s.DisconnectedAt ?? now) - s.ConnectedAt))
            .ToList();
    }

    /// <summary>Tiempo jugado dentro de [desde, ahora], contando las sesiones abiertas hasta ahora.</summary>
    internal static TimeSpan Sum(IEnumerable<PlayerSession> sessions, DateTimeOffset from, DateTimeOffset now)
    {
        var total = TimeSpan.Zero;
        foreach (var s in sessions)
        {
            var start = s.ConnectedAt > from ? s.ConnectedAt : from;
            var end = s.DisconnectedAt ?? now;
            if (end > start)
            {
                total += end - start;
            }
        }
        return total;
    }
}
