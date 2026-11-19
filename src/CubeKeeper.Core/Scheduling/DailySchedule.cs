using System.Globalization;

namespace CubeKeeper.Core.Scheduling;

public static class DailySchedule
{
    /// <summary>Interpreta "05:00" o "5:30". Devuelve null si no es una hora válida.</summary>
    public static TimeOnly? ParseTime(string? text) =>
        TimeOnly.TryParseExact(text?.Trim(), ["H:mm", "HH:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : null;

    /// <summary>Siguiente momento (hora local de <paramref name="zone"/>) en que toca <paramref name="time"/>, estrictamente después de <paramref name="now"/>.</summary>
    public static DateTimeOffset Next(DateTimeOffset now, TimeOnly time, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(now, zone);
        var candidate = local.Date + time.ToTimeSpan();
        if (candidate <= local.DateTime)
        {
            candidate = candidate.AddDays(1);
        }
        // Si la hora no existe (cambio de hora en primavera), se adelanta una hora.
        while (zone.IsInvalidTime(candidate))
        {
            candidate = candidate.AddHours(1);
        }
        return new DateTimeOffset(candidate, zone.GetUtcOffset(candidate));
    }

    /// <summary>"5,1" → [5, 1]: minutos de aviso antes de reiniciar, de mayor a menor, sin repetidos ni valores no válidos.</summary>
    public static IReadOnlyList<int> ParseWarnings(string? text) =>
        (text ?? string.Empty)
            .Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var m) ? m : 0)
            .Where(m => m is > 0 and <= 60)
            .Distinct()
            .OrderDescending()
            .ToList();
}
