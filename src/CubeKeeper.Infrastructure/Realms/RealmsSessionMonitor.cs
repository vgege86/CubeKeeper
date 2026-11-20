using CubeKeeper.Core.Notifications;
using CubeKeeper.Infrastructure.Notifications;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CubeKeeper.Infrastructure.Realms;

/// <summary>
/// Renueva la sesión de Realms una vez al día. Si falla tres veces seguidas (una por hora, para no avisar por un corte
/// de red), avisa con ntfy una sola vez hasta que vuelva a funcionar.
/// </summary>
public sealed class RealmsSessionMonitor(RealmsService realms, TimeProvider time, ILogger<RealmsSessionMonitor> logger, Notifier? notifier = null)
    : BackgroundService
{
    public static readonly TimeSpan FirstCheck = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan Interval = TimeSpan.FromDays(1);
    public static readonly TimeSpan RetryInterval = TimeSpan.FromHours(1);
    public const int FailuresBeforeNotifying = 3;

    private int _failures;
    private bool _notified;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(FirstCheck, time, stoppingToken).ConfigureAwait(false);
            while (!stoppingToken.IsCancellationRequested)
            {
                var ok = await CheckOnceAsync(stoppingToken).ConfigureAwait(false);
                await Task.Delay(ok ? Interval : RetryInterval, time, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Una comprobación. Devuelve false si ha fallado (para reintentar antes).</summary>
    internal async Task<bool> CheckOnceAsync(CancellationToken ct)
    {
        if (!realms.IsConnected)
        {
            _failures = 0;
            _notified = false;
            return true;
        }
        try
        {
            await realms.CheckSessionAsync(ct).ConfigureAwait(false);
            _failures = 0;
            _notified = false;
            return true;
        }
        catch (RealmsAuthException ex)
        {
            _failures++;
            logger.LogWarning("No se pudo renovar la sesión de Realms ({Failures}): {Message}", _failures, ex.Message);
            if (_failures >= FailuresBeforeNotifying && !_notified && notifier is not null)
            {
                var result = await notifier.NotifyAsync(new Notification(
                    NotificationKind.Maintenance,
                    "La cuenta de Realms ha dejado de funcionar",
                    $"{ex.Message} Entra en Realms y vuelve a conectarla cuando quieras descargar un mundo.",
                    "realms",
                    NotificationPriority.Default,
                    "/realms"), ct).ConfigureAwait(false);
                _notified = result.Sent || !notifier.Configured;
            }
            return false;
        }
    }
}
