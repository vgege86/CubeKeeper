using CubeKeeper.Infrastructure.Persistence;

namespace CubeKeeper.Web.Formatting;

public static class Format
{
    /// <summary>"2 h 05 min", "12 min", "menos de 1 min".</summary>
    public static string Duration(TimeSpan t)
    {
        if (t < TimeSpan.FromMinutes(1))
        {
            return "menos de 1 min";
        }
        var hours = (int)t.TotalHours;
        return hours > 0 ? $"{hours} h {t.Minutes:00} min" : $"{t.Minutes} min";
    }

    public static string Size(long bytes) => bytes switch
    {
        < 1024 * 1024 => $"{Math.Max(1, bytes / 1024)} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024:0.0} MB",
        _ => $"{bytes / 1024.0 / 1024 / 1024:0.00} GB",
    };

    public static string When(DateTimeOffset at, TimeProvider time)
    {
        var local = TimeZoneInfo.ConvertTime(at, time.LocalTimeZone);
        var today = time.GetLocalNow().Date;
        return local.Date == today ? $"hoy {local:HH:mm}"
            : local.Date == today.AddDays(-1) ? $"ayer {local:HH:mm}"
            : local.ToString("dd/MM/yyyy HH:mm");
    }

    public static string Reason(SessionEndReason? reason) => reason switch
    {
        null => "conectado",
        SessionEndReason.Disconnected => "salió",
        SessionEndReason.ServerStopped => "servidor parado",
        SessionEndReason.NotInList => "no estaba en list",
        SessionEndReason.Reconnected => "volvió a entrar",
        SessionEndReason.Interrupted => "interrumpida",
        _ => reason.ToString()!,
    };
}
