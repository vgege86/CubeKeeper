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

/// <summary>Tiempo jugado por día de un jugador en un periodo (para las gráficas).</summary>
/// <param name="Rank">Orden del jugador por primera vez visto (0 = el primero): fija su color en cualquier periodo.</param>
public sealed record PlayerDaily(string Xuid, string Name, int Rank, IReadOnlyList<TimeSpan> Days)
{
    public TimeSpan Total => Days.Aggregate(TimeSpan.Zero, (a, d) => a + d);

    public int DaysPlayed => Days.Count(d => d > TimeSpan.Zero);
}

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

    /// <summary>Tiempo jugado hoy (hora local) por un jugador, contando la sesión abierta.</summary>
    public async Task<TimeSpan> PlayedTodayAsync(string xuid, CancellationToken ct = default)
    {
        var start = LocalDayStart(time.GetLocalNow().DateTime);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var sessions = await RelevantSessions(db, xuid, start).ToListAsync(ct);
        return Sum(sessions, start, time.GetUtcNow());
    }

    /// <summary>Tiempo jugado por día en los últimos <paramref name="days"/> días (hoy incluido), del más reciente al más antiguo.</summary>
    public async Task<IReadOnlyList<(DateOnly Day, TimeSpan Played)>> GetDailyAsync(string xuid, int days, CancellationToken ct = default)
    {
        var today = time.GetLocalNow().DateTime.Date;
        var from = LocalDayStart(today.AddDays(-(days - 1)));
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var sessions = await RelevantSessions(db, xuid, from).ToListAsync(ct);
        var now = time.GetUtcNow();
        var result = new List<(DateOnly, TimeSpan)>();
        for (var i = 0; i < days; i++)
        {
            var day = today.AddDays(-i);
            var start = LocalDayStart(day);
            var end = LocalDayStart(day.AddDays(1));
            var clippedNow = now < end ? now : end;
            var played = TimeSpan.Zero;
            foreach (var s in sessions)
            {
                var a = s.ConnectedAt > start ? s.ConnectedAt : start;
                var b = (s.DisconnectedAt ?? now) < clippedNow ? (s.DisconnectedAt ?? now) : clippedNow;
                if (b > a)
                {
                    played += b - a;
                }
            }
            result.Add((DateOnly.FromDateTime(day), played));
        }
        return result;
    }

    /// <summary>
    /// Tiempo jugado por día en los últimos <paramref name="days"/> días (hoy incluido) de todos los jugadores que han
    /// jugado en el periodo. <see cref="DailyDates"/> da las fechas, del más antiguo al más reciente.
    /// </summary>
    public async Task<IReadOnlyList<PlayerDaily>> GetDailyAllAsync(int days, CancellationToken ct = default)
    {
        var today = time.GetLocalNow().DateTime.Date;
        var from = LocalDayStart(today.AddDays(-(days - 1)));
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var players = await db.Players.AsNoTracking().ToListAsync(ct);
        var rank = players.OrderBy(p => p.FirstSeen).Select((p, i) => (p.Xuid, i)).ToDictionary(x => x.Xuid, x => x.i);
        var sessions = await db.PlayerSessions.AsNoTracking()
            .Where(s => s.DisconnectedAt == null || s.DisconnectedAt >= from)
            .ToListAsync(ct);
        var now = time.GetUtcNow();
        var result = new List<PlayerDaily>();
        foreach (var player in players)
        {
            var mine = sessions.Where(s => s.Xuid == player.Xuid).ToList();
            var perDay = new List<TimeSpan>(days);
            for (var i = days - 1; i >= 0; i--)
            {
                var day = today.AddDays(-i);
                var start = LocalDayStart(day);
                var end = LocalDayStart(day.AddDays(1));
                var clippedNow = now < end ? now : end;
                var played = TimeSpan.Zero;
                foreach (var s in mine)
                {
                    var a = s.ConnectedAt > start ? s.ConnectedAt : start;
                    var b = (s.DisconnectedAt ?? now) < clippedNow ? (s.DisconnectedAt ?? now) : clippedNow;
                    if (b > a)
                    {
                        played += b - a;
                    }
                }
                perDay.Add(played);
            }
            var daily = new PlayerDaily(player.Xuid, player.Name, rank[player.Xuid], perDay);
            if (daily.Total > TimeSpan.Zero)
            {
                result.Add(daily);
            }
        }
        return result.OrderBy(p => p.Rank).ToList();
    }

    /// <summary>Las fechas de <see cref="GetDailyAllAsync"/>, del más antiguo al más reciente.</summary>
    public IReadOnlyList<DateOnly> DailyDates(int days)
    {
        var today = DateOnly.FromDateTime(time.GetLocalNow().DateTime);
        return Enumerable.Range(0, days).Select(i => today.AddDays(i - (days - 1))).ToList();
    }

    private DateTimeOffset LocalDayStart(DateTime localDate)
    {
        var d = localDate.Date;
        return new DateTimeOffset(d, time.LocalTimeZone.GetUtcOffset(d));
    }

    /// <summary>Sesiones que pueden solaparse con [desde, ahora]: abiertas o que acabaron después de <paramref name="from"/>.</summary>
    private static IQueryable<PlayerSession> RelevantSessions(CubeKeeperDbContext db, string xuid, DateTimeOffset from)
    {
        // DisconnectedAt se guarda como entero ordenable: la comparación se hace en SQLite.
        return db.PlayerSessions.AsNoTracking().Where(s => s.Xuid == xuid && (s.DisconnectedAt == null || s.DisconnectedAt >= from));
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
