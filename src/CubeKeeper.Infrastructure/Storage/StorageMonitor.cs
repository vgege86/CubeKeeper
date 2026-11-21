using CubeKeeper.Core.Notifications;
using CubeKeeper.Infrastructure.Notifications;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CubeKeeper.Infrastructure.Storage;

/// <summary>Mira el espacio libre cada hora y avisa una vez por disco hasta que vuelva a haber sitio.</summary>
public sealed class StorageMonitor(StorageService storage, TimeProvider time, ILogger<StorageMonitor> logger, Notifier? notifier = null) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    private readonly HashSet<string> _notified = new(StringComparer.Ordinal);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(2), time, stoppingToken).ConfigureAwait(false);
            while (!stoppingToken.IsCancellationRequested)
            {
                await CheckOnceAsync(stoppingToken).ConfigureAwait(false);
                await Task.Delay(Interval, time, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    internal async Task CheckOnceAsync(CancellationToken ct)
    {
        foreach (var disk in storage.MeasureDisks())
        {
            if (!disk.Low)
            {
                _notified.Remove(disk.Path);
                continue;
            }
            logger.LogWarning("Queda poco espacio en {Path}: {Free} libres de {Total}", disk.Path, disk.Free, disk.Total);
            if (_notified.Contains(disk.Path) || notifier is null)
            {
                continue;
            }
            var result = await notifier.NotifyAsync(new Notification(
                NotificationKind.Maintenance,
                "Queda poco espacio en disco",
                $"{disk.Label}: quedan {FormatSize(disk.Free)} libres ({disk.FreePercent:0}%). Borra copias antiguas o amplía el dataset; si se llena, las copias y el mundo pueden fallar.",
                "disk:" + disk.Path,
                NotificationPriority.High,
                "/copias"), ct).ConfigureAwait(false);
            if (result.Sent || !notifier.Configured)
            {
                _notified.Add(disk.Path);
            }
        }
    }

    internal static string FormatSize(long bytes) => bytes switch
    {
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024:0} MB",
        _ => $"{bytes / 1024.0 / 1024 / 1024:0.0} GB",
    };
}
