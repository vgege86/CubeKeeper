using CubeKeeper.Core.Scheduling;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CubeKeeper.Infrastructure.Maps;

/// <summary>Dibuja el mapa cada noche a la hora configurada (si hay motor instalado).</summary>
public sealed class MapScheduler(MapService maps, TimeProvider time, ILogger<MapScheduler> logger, Notifications.Notifier? notifier = null) : BackgroundService
{
    public DateTimeOffset? NextRunAt { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1), time);
        try
        {
            do
            {
                var settings = maps.Settings;
                if (!settings.Nightly || DailySchedule.ParseTime(settings.Time) is not { } at)
                {
                    NextRunAt = null;
                    continue;
                }
                NextRunAt ??= DailySchedule.Next(time.GetUtcNow(), at, time.LocalTimeZone);
                if (time.GetUtcNow() < NextRunAt)
                {
                    continue;
                }
                NextRunAt = DailySchedule.Next(time.GetUtcNow(), at, time.LocalTimeZone);
                if (maps.GetStatus(maps.ActiveEngine).Executable is null || maps.IsRendering)
                {
                    continue;
                }
                try
                {
                    var result = await maps.RenderAsync(stoppingToken).ConfigureAwait(false);
                    logger.LogInformation("Mapa nocturno: {Message}", result.Message);
                    if (!result.Succeeded)
                    {
                        await NotifyFailureAsync(result.Message, stoppingToken).ConfigureAwait(false);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Fallo en el mapa nocturno");
                    await NotifyFailureAsync(ex.Message, stoppingToken).ConfigureAwait(false);
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
        }
    }

    private Task NotifyFailureAsync(string detail, CancellationToken ct) =>
        notifier?.NotifyAsync(new Core.Notifications.Notification(
            Core.Notifications.NotificationKind.Maintenance,
            "Falló el mapa nocturno",
            $"No se pudo generar el mapa: {detail}",
            "map",
            Core.Notifications.NotificationPriority.Default,
            "/mapa"), ct) ?? Task.CompletedTask;

    /// <summary>Recalcula la próxima ejecución (tras cambiar la hora en el panel).</summary>
    public void Reschedule() => NextRunAt = null;
}
