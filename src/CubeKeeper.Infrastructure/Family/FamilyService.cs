using System.Collections.Concurrent;
using CubeKeeper.Core.Abstractions;
using CubeKeeper.Core.Access;
using CubeKeeper.Core.Family;
using CubeKeeper.Core.Logs;
using CubeKeeper.Core.Server;
using CubeKeeper.Infrastructure.Access;
using CubeKeeper.Infrastructure.Audit;
using CubeKeeper.Infrastructure.Persistence;
using CubeKeeper.Infrastructure.Players;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CubeKeeper.Infrastructure.Family;

/// <summary>
/// Horarios y límite diario por jugador. Cada 20 s revisa a los conectados que tienen normas: avisa por mensaje privado
/// a los 5 y a 1 minuto del final y, al llegar, le expulsa con la explicación. También comprueba a cada uno al entrar.
/// </summary>
public sealed class FamilyService(
    IServerController server,
    IPlayerPresence presence,
    AccessService access,
    PlayerStatsService stats,
    AuditLog audit,
    IDbContextFactory<CubeKeeperDbContext> dbFactory,
    TimeProvider time,
    ILogger<FamilyService> logger) : BackgroundService
{
    public static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(20);
    private static readonly int[] WarningMinutes = [5, 1];

    private readonly ConcurrentDictionary<string, HashSet<int>> _warned = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _checkGate = new(1, 1);

    public event Action? Changed;

    public async Task<PlayerRules> GetRulesAsync(string xuid, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var e = await db.PlayerRules.AsNoTracking().FirstOrDefaultAsync(r => r.Xuid == xuid, ct).ConfigureAwait(false);
        return e is null ? PlayerRules.None : ToRules(e);
    }

    public async Task<IReadOnlyDictionary<string, PlayerRules>> GetAllRulesAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return (await db.PlayerRules.AsNoTracking().ToListAsync(ct).ConfigureAwait(false))
            .ToDictionary(e => e.Xuid, ToRules, StringComparer.Ordinal);
    }

    public async Task SaveRulesAsync(string xuid, PlayerRules rules, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(rules);
        if (rules.DailyMinutes is < 0 or > 24 * 60)
        {
            throw new ArgumentException("El límite diario debe estar entre 0 y 24 horas.");
        }
        if (rules.Schedule.Any(r => !r.IsValid))
        {
            throw new ArgumentException("Cada franja necesita al menos un día y una hora de fin posterior a la de inicio.");
        }
        await using (var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false))
        {
            var e = await db.PlayerRules.FirstOrDefaultAsync(r => r.Xuid == xuid, ct).ConfigureAwait(false);
            if (e is null)
            {
                e = new PlayerRulesEntity { Xuid = xuid };
                db.PlayerRules.Add(e);
            }
            e.Enabled = rules.Enabled;
            e.DailyMinutes = rules.DailyMinutes;
            e.ScheduleJson = PlayerRules.SerializeSchedule(rules.Schedule);
            e.ExtraMinutes = rules.ExtraMinutes;
            e.ExtraDate = rules.ExtraDate;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        _warned.TryRemove(xuid, out _);
        Changed?.Invoke();
        await CheckOnlineAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Suma minutos extra solo para hoy (o los quita con 0).</summary>
    public async Task<PlayerRules> SetExtraTodayAsync(string xuid, int minutes, CancellationToken ct = default)
    {
        if (minutes is < 0 or > 600)
        {
            throw new ArgumentException("Entre 0 y 600 minutos extra.");
        }
        var rules = await GetRulesAsync(xuid, ct).ConfigureAwait(false);
        var updated = rules with { ExtraMinutes = minutes, ExtraDate = minutes == 0 ? null : Today };
        await SaveRulesAsync(xuid, updated, ct).ConfigureAwait(false);
        return updated;
    }

    public async Task<FamilyStatus> GetStatusAsync(string xuid, CancellationToken ct = default)
    {
        var rules = await GetRulesAsync(xuid, ct).ConfigureAwait(false);
        var played = await stats.PlayedTodayAsync(xuid, ct).ConfigureAwait(false);
        return FamilyPolicy.Evaluate(rules, time.GetLocalNow().DateTime, played);
    }

    private DateOnly Today => DateOnly.FromDateTime(time.GetLocalNow().DateTime);

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        server.LogEvent += OnLogEvent;
        return base.StartAsync(cancellationToken);
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        server.LogEvent -= OnLogEvent;
        return base.StopAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(CheckInterval, time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                await CheckOnlineAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void OnLogEvent(BdsLogEvent evt)
    {
        if (evt is PlayerSpawned or PlayerConnected)
        {
            // Un momento después de entrar, para que el jugador ya esté en la lista de conectados.
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), time).ConfigureAwait(false);
                    await CheckOnlineAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error revisando normas al entrar");
                }
            });
        }
    }

    /// <summary>Revisa a todos los conectados que tienen normas activas.</summary>
    public async Task CheckOnlineAsync(CancellationToken ct)
    {
        if (server.Snapshot.Status != ServerStatus.Running)
        {
            return;
        }
        if (!await _checkGate.WaitAsync(0, ct).ConfigureAwait(false))
        {
            return; // Ya hay una revisión en marcha.
        }
        try
        {
            var online = presence.Online.Where(p => p.Xuid is not null).ToList();
            foreach (var key in _warned.Keys.Where(k => online.All(o => o.Xuid != k)).ToList())
            {
                _warned.TryRemove(key, out _);
            }
            if (online.Count == 0)
            {
                return;
            }
            var all = await GetAllRulesAsync(ct).ConfigureAwait(false);
            foreach (var player in online)
            {
                if (!all.TryGetValue(player.Xuid!, out var rules) || !rules.Enabled)
                {
                    continue;
                }
                var played = await stats.PlayedTodayAsync(player.Xuid!, ct).ConfigureAwait(false);
                var now = time.GetLocalNow().DateTime;
                var status = FamilyPolicy.Evaluate(rules, now, played);
                if (!status.Allowed)
                {
                    var message = FamilyPolicy.KickMessage(status, now);
                    await access.KickAsync(player.Name, message, ct).ConfigureAwait(false);
                    _warned.TryRemove(player.Xuid!, out _);
                    await audit.RecordAsync(AuditLog.SystemActor, "family.kick", player.Name, message, ct).ConfigureAwait(false);
                    logger.LogInformation("Expulsado {Name} por normas de juego: {Message}", player.Name, message);
                    continue;
                }
                await WarnIfNeededAsync(player.Name, player.Xuid!, status, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Error aplicando las normas de juego");
        }
        finally
        {
            _checkGate.Release();
        }
    }

    private async Task WarnIfNeededAsync(string name, string xuid, FamilyStatus status, CancellationToken ct)
    {
        if (status.Remaining is not { } remaining)
        {
            return;
        }
        var warned = _warned.GetOrAdd(xuid, _ => []);
        if (remaining > TimeSpan.FromMinutes(WarningMinutes[0] + 1))
        {
            warned.Clear(); // Por ejemplo, tras darle tiempo extra.
            return;
        }
        foreach (var m in WarningMinutes)
        {
            if (remaining <= TimeSpan.FromMinutes(m) && warned.Add(m))
            {
                try
                {
                    await server.SendCommandAsync($"tell {AccessPolicy.QuoteName(name)} {AccessPolicy.SafeText(FamilyPolicy.WarningMessage(remaining))}", ct).ConfigureAwait(false);
                }
                catch (InvalidOperationException)
                {
                }
                // Si ya quedaba menos de 1 minuto, no mandar también el de 5.
                foreach (var bigger in WarningMinutes.Where(x => x > m))
                {
                    warned.Add(bigger);
                }
                break;
            }
        }
    }

    private static PlayerRules ToRules(PlayerRulesEntity e) =>
        new(e.Enabled, e.DailyMinutes, PlayerRules.DeserializeSchedule(e.ScheduleJson), e.ExtraMinutes, e.ExtraDate);
}
