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
    ILogger<MaintenanceScheduler> logger,
    IVersionSource? versions = null,
    State.AppStateStore? state = null,
    Notifications.Notifier? notifier = null) : BackgroundService
{
    /// <summary>Cada cuánto se mira si Mojang ha publicado una versión nueva (para avisar).</summary>
    public static readonly TimeSpan VersionCheckInterval = TimeSpan.FromHours(6);

    private DateTimeOffset _nextVersionCheck = DateTimeOffset.MinValue;

    // Para avisar una vez cuando el segundo destino falla, no en cada copia.
    private bool _secondaryNotified;
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
            _nextVersionCheck = time.GetUtcNow().AddMinutes(1);
            do
            {
                await RunBackupIfDueAsync(stoppingToken).ConfigureAwait(false);
                await CheckNewVersionIfDueAsync(stoppingToken).ConfigureAwait(false);
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
            await CheckSecondaryAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _activitySinceBackup = true;
            logger.LogError(ex, "Fallo en la copia programada");
            if (notifier is not null)
            {
                await notifier.NotifyAsync(new Core.Notifications.Notification(
                    Core.Notifications.NotificationKind.BackupFailed,
                    "Falló la copia del mundo",
                    $"No se pudo hacer la copia programada: {ex.Message}",
                    "backup",
                    Core.Notifications.NotificationPriority.High,
                    "/copias"), ct).ConfigureAwait(false);
            }
        }
    }

    private async Task CheckSecondaryAsync(CancellationToken ct)
    {
        if (backups.SecondaryProblem is not { } problem)
        {
            _secondaryNotified = false;
            return;
        }
        if (_secondaryNotified || notifier is null)
        {
            return;
        }
        var result = await notifier.NotifyAsync(new Core.Notifications.Notification(
            Core.Notifications.NotificationKind.Maintenance,
            "Las copias no llegan al segundo destino",
            $"{problem} Las copias se siguen guardando en /data/backups.",
            "secondary",
            Core.Notifications.NotificationPriority.High,
            "/copias"), ct).ConfigureAwait(false);
        _secondaryNotified = result.Sent || !notifier.Configured;
    }

    /// <summary>Avisa (una vez por versión) si Mojang ha publicado una más nueva que la instalada.</summary>
    private async Task CheckNewVersionIfDueAsync(CancellationToken ct)
    {
        if (versions is null || state is null || notifier is null || time.GetUtcNow() < _nextVersionCheck)
        {
            return;
        }
        _nextVersionCheck = time.GetUtcNow() + VersionCheckInterval;
        if (!notifier.Configured || !notifier.Enabled.HasFlag(Core.Notifications.NotificationKind.NewVersion) ||
            !Core.Versions.BdsVersion.TryParse(installer.InstalledVersion, out var installed) || installed is null)
        {
            return;
        }
        try
        {
            var newest = await versions.GetNewestPublishedAsync(_options.Bds.Channel, ct).ConfigureAwait(false);
            if (newest is null || newest.Version <= installed || newest.Version.Text == state.Current.LastNotifiedVersion)
            {
                return;
            }
            var how = updates.AutoUpdateActive
                ? $"Se instalará sola a las {_options.Updates.Time}."
                : $"Tienes la versión fijada: para pasar a ella, entra en el Panel y pulsa «Instalar {newest.Version}».";
            var result = await notifier.NotifyAsync(new Core.Notifications.Notification(
                Core.Notifications.NotificationKind.NewVersion,
                $"BDS {newest.Version} disponible",
                $"Mojang ha publicado la versión {newest.Version} (tienes la {installed}). {how}",
                newest.Version.Text,
                Core.Notifications.NotificationPriority.Low,
                "/"), ct).ConfigureAwait(false);
            if (result.Sent)
            {
                state.Update(s => s with { LastNotifiedVersion = newest.Version.Text });
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "No se pudo comprobar si hay versión nueva");
        }
    }
}
