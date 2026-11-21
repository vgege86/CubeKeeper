namespace CubeKeeper.Core.Notifications;

/// <summary>Tipos de aviso. Se pueden activar y desactivar por separado.</summary>
[Flags]
public enum NotificationKind
{
    None = 0,
    /// <summary>Alguien ha pedido acceso en modo inscripción.</summary>
    AccessRequest = 1,
    /// <summary>El servidor se ha caído (o ya no se reintenta).</summary>
    ServerDown = 2,
    /// <summary>Ha fallado una copia programada.</summary>
    BackupFailed = 4,
    /// <summary>Resultado de una actualización (fallos siempre; éxitos de las programadas).</summary>
    Update = 8,
    /// <summary>Mojang ha publicado una versión nueva de BDS.</summary>
    NewVersion = 16,
    /// <summary>Un jugador entra o sale.</summary>
    PlayerJoin = 32,
    /// <summary>Falla una tarea de mantenimiento: el mapa nocturno, el segundo destino de copias o la cuenta de Realms.</summary>
    Maintenance = 64,

    /// <summary>Lo que elegiste al planificar la fase 3C, más los fallos de mantenimiento.</summary>
    Defaults = AccessRequest | ServerDown | BackupFailed | Update | NewVersion | Maintenance,
}

public static class NotificationKindText
{
    public static readonly IReadOnlyList<(NotificationKind Kind, string Label, string Help)> All =
    [
        (NotificationKind.AccessRequest, "Solicitudes de acceso", "Alguien ha intentado entrar durante el modo inscripción."),
        (NotificationKind.ServerDown, "Caídas del servidor", "Se ha cerrado inesperadamente, o se ha caído varias veces y ya no se reintenta."),
        (NotificationKind.BackupFailed, "Copias fallidas", "No se ha podido hacer una copia programada del mundo."),
        (NotificationKind.Update, "Actualizaciones", "Una actualización ha fallado (y se ha vuelto atrás), o la programada ha ido bien."),
        (NotificationKind.NewVersion, "Versión nueva de BDS", "Mojang ha publicado una versión más nueva que la instalada (también con la versión fijada)."),
        (NotificationKind.Maintenance, "Fallos de mantenimiento", "Queda poco espacio en disco, falla el mapa nocturno o la comprobación de copias, el segundo destino no está montado o la cuenta de Realms deja de funcionar."),
        (NotificationKind.PlayerJoin, "Entradas y salidas", "Cada vez que un jugador entra o sale."),
    ];
}

/// <summary>Un aviso para enviar.</summary>
/// <param name="Key">Para no repetir el mismo aviso: dos con el mismo tipo y clave en 10 minutos se envían una sola vez.</param>
public sealed record Notification(NotificationKind Kind, string Title, string Message, string Key, NotificationPriority Priority = NotificationPriority.Default, string? Path = null);

public enum NotificationPriority
{
    Low = 2,
    Default = 3,
    High = 4,
    Urgent = 5,
}
