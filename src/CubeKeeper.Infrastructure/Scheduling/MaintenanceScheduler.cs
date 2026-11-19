using CubeKeeper.Core.Abstractions;
using CubeKeeper.Core.Backups;
using CubeKeeper.Core.Scheduling;
using CubeKeeper.Core.Server;
using CubeKeeper.Infrastructure.Backups;
using CubeKeeper.Infrastructure.Configuration;
using CubeKeeper.Infrastructure.Updates;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CubeKeeper.Infrastructure.Scheduling;

/// <summary>Copias periódicas y comprobación diaria de actualizaciones.</summary>
public sealed class MaintenanceScheduler(
    BackupService backups,
    UpdateService updates,
    IServerController server,
    IServerInstaller installer,
    IPlayerPresence presence,
    IOptions<CubeKeeperOptions> options,
    TimeProvider time,
    ILogger<MaintenanceScheduler> logger) : BackgroundService
{
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(30);
    private readonly CubeKeeperOptions _options = options.Value;

    // Al arrancar se considera que hay actividad: la primera copia programada se hace siempre.
    private volatile bool _activitySinceBackup = true;

    public DateTimeOffset? NextBackupAt { get; private set; }

    public DateTimeOffset? NextUpdateCheckAt { get; private set; }

    public bool BackupsEnabled => _options.Backups.IntervalMinutes > 0;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        presence.Changed += OnPresenceChanged;
        var updateTime = DailySchedule.ParseTime(_options.Updates.Time);
        if (updateTime is null)
        {
            logger.LogError("Hora de actualización no válida: '{Time}'. Usa HH:mm, por ejemplo 05:00", _options.Updates.Time);
        }
        NextUpdateCheckAt = updateTime is { } t && updates.AutoUpdateActive ? DailySchedule.Next(time.GetUtcNow(), t, time.LocalTimeZone) : null;

        using var timer = new PeriodicTimer(Tick, time);
        try
        {
            do
            {
                await RunBackupIfDueAsync(stoppingToken).ConfigureAwait(false);
                if (NextUpdateCheckAt is { } due && time.GetUtcNow() >= due)
                {
                    NextUpdateCheckAt = DailySchedule.Next(time.GetUtcNow(), updateTime!.Value, time.LocalTimeZone);
                    try
                    {
                        await updates.RunScheduledAsync(stoppingToken).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        logger.LogError(ex, "Fallo en la actualización programada");
                    }
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            presence.Changed -= OnPresenceChanged;
        }
    }

    private void OnPresenceChanged()
    {
        if (presence.Online.Count > 0)
        {
            _activitySinceBackup = true;
        }
    }

    private async Task RunBackupIfDueAsync(CancellationToken ct)
    {
        if (!BackupsEnabled)
        {
            NextBackupAt = null;
            return;
        }
        var interval = TimeSpan.FromMinutes(_options.Backups.IntervalMinutes);
        var last = backups.List().Where(b => b.File.Kind == BackupKind.Scheduled).Select(b => (DateTimeOffset?)b.File.CreatedAt).FirstOrDefault();
        var due = last is { } l ? l + interval : time.GetUtcNow();
        NextBackupAt = due;

        if (time.GetUtcNow() < due || !installer.IsInstalled || backups.IsBusy || updates.IsBusy ||
            server.Snapshot.Status is not (ServerStatus.Running or ServerStatus.Stopped))
        {
            return;
        }
        if (presence.Online.Count > 0)
        {
            _activitySinceBackup = true;
        }
        if (_options.Backups.SkipIfIdle && !_activitySinceBackup)
        {
            return;
        }
        var world = await backups.GetWorldNameAsync(ct).ConfigureAwait(false);
        if (!Directory.Exists(backups.WorldDirectory(world)))
        {
            return;
        }

        try
        {
            _activitySinceBackup = presence.Online.Count > 0;
            await backups.CreateAsync(BackupKind.Scheduled, ct).ConfigureAwait(false);
            NextBackupAt = time.GetUtcNow() + interval;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _activitySinceBackup = true;
            logger.LogError(ex, "Fallo en la copia programada");
        }
    }
}
