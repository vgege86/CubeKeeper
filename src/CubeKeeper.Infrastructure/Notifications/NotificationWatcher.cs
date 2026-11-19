using CubeKeeper.Core.Abstractions;
using CubeKeeper.Core.Logs;
using CubeKeeper.Core.Notifications;
using CubeKeeper.Core.Server;
using Microsoft.Extensions.Hosting;

namespace CubeKeeper.Infrastructure.Notifications;

/// <summary>Convierte en avisos lo que pasa en el servidor: caídas y entradas y salidas de jugadores.</summary>
public sealed class NotificationWatcher(IServerController server, Notifier notifier) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        notifier.EnableNewDefaultKinds();
        server.StateChanged += OnState;
        server.LogEvent += OnLogEvent;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        server.StateChanged -= OnState;
        server.LogEvent -= OnLogEvent;
        return Task.CompletedTask;
    }

    private void OnState(ServerSnapshot snap)
    {
        Notification? n = snap.Status switch
        {
            ServerStatus.Crashed => new Notification(NotificationKind.ServerDown, "El servidor se ha caído",
                $"Se ha cerrado inesperadamente ({snap.LastError}). CubeKeeper lo está volviendo a arrancar.", "crashed", NotificationPriority.High, "/consola"),
            ServerStatus.Failed => new Notification(NotificationKind.ServerDown, "El servidor está parado por fallos",
                snap.LastError ?? "Se ha caído varias veces seguidas y ya no se reintenta. Revisa la consola.", "failed", NotificationPriority.Urgent, "/consola"),
            _ => null,
        };
        if (n is not null)
        {
            _ = notifier.NotifyAsync(n);
        }
    }

    private void OnLogEvent(BdsLogEvent evt)
    {
        Notification? n = evt switch
        {
            PlayerConnected c => new Notification(NotificationKind.PlayerJoin, $"{c.Name} ha entrado", $"{c.Name} se ha conectado al servidor.", $"in:{c.Xuid}", NotificationPriority.Low, "/jugadores"),
            PlayerDisconnected d => new Notification(NotificationKind.PlayerJoin, $"{d.Name} ha salido", $"{d.Name} se ha desconectado.", $"out:{d.Xuid}", NotificationPriority.Low, "/jugadores"),
            _ => null,
        };
        if (n is not null)
        {
            _ = notifier.NotifyAsync(n);
        }
    }
}
