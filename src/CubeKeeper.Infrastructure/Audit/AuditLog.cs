using CubeKeeper.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CubeKeeper.Infrastructure.Audit;

/// <summary>Registro de auditoría: quién hizo qué y cuándo. Las acciones automáticas van con el actor «CubeKeeper».</summary>
public sealed class AuditLog(IDbContextFactory<CubeKeeperDbContext> dbFactory, TimeProvider time, ILogger<AuditLog> logger)
{
    public const string SystemActor = "CubeKeeper";

    public event Action? Changed;

    /// <summary>Guarda una entrada. Nunca lanza: un fallo de auditoría no debe romper la acción.</summary>
    public async Task RecordAsync(string actor, string action, string? target = null, string? details = null, CancellationToken ct = default)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            db.AuditEntries.Add(new AuditEntry
            {
                At = time.GetUtcNow(),
                Actor = Truncate(actor, 64),
                Action = Truncate(action, 64),
                Target = target is null ? null : Truncate(target, 200),
                Details = details is null ? null : Truncate(details, 2000),
            });
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            logger.LogInformation("Auditoría: {Actor} {Action} {Target}", actor, action, target);
            Changed?.Invoke();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "No se pudo guardar la entrada de auditoría {Action}", action);
        }
    }

    public async Task<IReadOnlyList<AuditEntry>> GetRecentAsync(int take = 200, string? actor = null, string? actionPrefix = null, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var q = db.AuditEntries.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(actor))
        {
            q = q.Where(a => a.Actor == actor);
        }
        if (!string.IsNullOrWhiteSpace(actionPrefix))
        {
            q = q.Where(a => a.Action.StartsWith(actionPrefix));
        }
        return await q.OrderByDescending(a => a.At).Take(take).ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<string>> GetActorsAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.AuditEntries.Select(a => a.Actor).Distinct().OrderBy(a => a).ToListAsync(ct).ConfigureAwait(false);
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
