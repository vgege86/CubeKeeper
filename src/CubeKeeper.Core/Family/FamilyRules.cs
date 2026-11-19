using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CubeKeeper.Core.Family;

[Flags]
public enum WeekDays
{
    None = 0,
    Monday = 1,
    Tuesday = 2,
    Wednesday = 4,
    Thursday = 8,
    Friday = 16,
    Saturday = 32,
    Sunday = 64,
    Weekdays = Monday | Tuesday | Wednesday | Thursday | Friday,
    Weekend = Saturday | Sunday,
    All = Weekdays | Weekend,
}

public static class WeekDaysExtensions
{
    public static WeekDays From(DayOfWeek d) => d switch
    {
        DayOfWeek.Monday => WeekDays.Monday,
        DayOfWeek.Tuesday => WeekDays.Tuesday,
        DayOfWeek.Wednesday => WeekDays.Wednesday,
        DayOfWeek.Thursday => WeekDays.Thursday,
        DayOfWeek.Friday => WeekDays.Friday,
        DayOfWeek.Saturday => WeekDays.Saturday,
        _ => WeekDays.Sunday,
    };

    /// <summary>Lunes a domingo, con su inicial en castellano.</summary>
    public static readonly IReadOnlyList<(WeekDays Day, string Short, string Long)> Ordered =
    [
        (WeekDays.Monday, "L", "lunes"),
        (WeekDays.Tuesday, "M", "martes"),
        (WeekDays.Wednesday, "X", "miércoles"),
        (WeekDays.Thursday, "J", "jueves"),
        (WeekDays.Friday, "V", "viernes"),
        (WeekDays.Saturday, "S", "sábado"),
        (WeekDays.Sunday, "D", "domingo"),
    ];

    /// <summary>"L-V", "S-D", "L, X, V"…</summary>
    public static string ToSpanish(this WeekDays days) => days switch
    {
        WeekDays.All => "todos los días",
        WeekDays.Weekdays => "de lunes a viernes",
        WeekDays.Weekend => "fines de semana",
        _ => string.Join(", ", Ordered.Where(o => days.HasFlag(o.Day)).Select(o => o.Long)),
    };
}

/// <summary>Franja en la que se puede jugar: ciertos días, de <see cref="From"/> a <see cref="To"/> (hora local).</summary>
public sealed record ScheduleRule(WeekDays Days, TimeOnly From, TimeOnly To)
{
    public bool IsValid => Days != WeekDays.None && To > From;

    public override string ToString() => $"{Days.ToSpanish()} de {From:HH\\:mm} a {To:HH\\:mm}";
}

/// <summary>Normas de un jugador. Sin franjas = a cualquier hora; sin límite diario = sin límite.</summary>
public sealed record PlayerRules(
    bool Enabled,
    int? DailyMinutes,
    IReadOnlyList<ScheduleRule> Schedule,
    int ExtraMinutes = 0,
    DateOnly? ExtraDate = null)
{
    public static PlayerRules None { get; } = new(false, null, []);

    /// <summary>Minutos extra que valen hoy (se conceden para un día concreto).</summary>
    public int ExtraFor(DateOnly today) => ExtraDate == today ? ExtraMinutes : 0;

    private static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };

    public static string SerializeSchedule(IReadOnlyList<ScheduleRule> rules) =>
        JsonSerializer.Serialize(rules.Select(r => new RuleDto(r.Days, r.From.ToString("HH:mm", CultureInfo.InvariantCulture), r.To.ToString("HH:mm", CultureInfo.InvariantCulture))), Json);

    public static IReadOnlyList<ScheduleRule> DeserializeSchedule(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }
        try
        {
            return (JsonSerializer.Deserialize<List<RuleDto>>(json, Json) ?? [])
                .Select(d => new ScheduleRule(d.Days,
                    TimeOnly.ParseExact(d.From, "HH:mm", CultureInfo.InvariantCulture),
                    TimeOnly.ParseExact(d.To, "HH:mm", CultureInfo.InvariantCulture)))
                .Where(r => r.IsValid)
                .ToList();
        }
        catch (Exception ex) when (ex is JsonException or FormatException)
        {
            return [];
        }
    }

    private sealed record RuleDto(WeekDays Days, string From, string To);
}

public enum BlockReason
{
    None,
    /// <summary>Fuera de las franjas permitidas.</summary>
    OutsideSchedule,
    /// <summary>Ha gastado el tiempo de hoy.</summary>
    DailyLimit,
}

/// <summary>
/// Situación de un jugador ahora mismo. <see cref="Remaining"/> es cuánto le queda antes de que se le expulse
/// (lo que antes llegue: fin de la franja o fin del tiempo diario), o null si no hay tope.
/// </summary>
public sealed record FamilyStatus(bool Allowed, BlockReason Reason, TimeSpan? Remaining, TimeSpan PlayedToday, TimeSpan? DailyAllowance, DateTime? NextAllowedLocal);

public static class FamilyPolicy
{
    public static FamilyStatus Evaluate(PlayerRules rules, DateTime localNow, TimeSpan playedToday)
    {
        ArgumentNullException.ThrowIfNull(rules);
        if (!rules.Enabled)
        {
            return new FamilyStatus(true, BlockReason.None, null, playedToday, null, null);
        }

        var today = DateOnly.FromDateTime(localNow);
        TimeSpan? allowance = rules.DailyMinutes is int m ? TimeSpan.FromMinutes(m + rules.ExtraFor(today)) : null;
        TimeSpan? leftToday = allowance is { } a ? a - playedToday : null;

        // Franja actual.
        TimeSpan? leftInWindow = null;
        var inWindow = rules.Schedule.Count == 0;
        if (!inWindow)
        {
            var now = TimeOnly.FromDateTime(localNow);
            var day = WeekDaysExtensions.From(localNow.DayOfWeek);
            var current = rules.Schedule.Where(r => r.Days.HasFlag(day) && now >= r.From && now < r.To).ToList();
            if (current.Count > 0)
            {
                inWindow = true;
                leftInWindow = current.Max(r => r.To.ToTimeSpan()) - now.ToTimeSpan();
            }
        }

        if (!inWindow)
        {
            return new FamilyStatus(false, BlockReason.OutsideSchedule, null, playedToday, allowance, NextWindowStart(rules, localNow));
        }
        if (leftToday is { } left && left <= TimeSpan.Zero)
        {
            return new FamilyStatus(false, BlockReason.DailyLimit, null, playedToday, allowance, NextDayStart(rules, localNow));
        }

        TimeSpan? remaining = (leftToday, leftInWindow) switch
        {
            (null, null) => null,
            ({ } x, null) => x,
            (null, { } y) => y,
            ({ } x, { } y) => x < y ? x : y,
        };
        return new FamilyStatus(true, BlockReason.None, remaining, playedToday, allowance, null);
    }

    /// <summary>Mensaje para el jugador al expulsarle.</summary>
    public static string KickMessage(FamilyStatus status, DateTime localNow) => status.Reason switch
    {
        BlockReason.DailyLimit => $"Se acabó el tiempo de juego de hoy ({FormatDuration(status.DailyAllowance ?? TimeSpan.Zero)}).{Next(status, localNow)}",
        BlockReason.OutsideSchedule => $"Ahora no es horario de juego.{Next(status, localNow)}",
        _ => "Expulsado.",
    };

    public static string WarningMessage(TimeSpan remaining)
    {
        var minutes = Math.Max(1, (int)Math.Ceiling(remaining.TotalMinutes));
        return $"Te queda{(minutes == 1 ? "" : "n")} {minutes} minuto{(minutes == 1 ? "" : "s")} de juego. ¡Ve guardando!";
    }

    public static string FormatDuration(TimeSpan t)
    {
        var h = (int)t.TotalHours;
        return h > 0 ? (t.Minutes > 0 ? $"{h} h {t.Minutes} min" : $"{h} h") : $"{t.Minutes} min";
    }

    private static string Next(FamilyStatus s, DateTime localNow)
    {
        if (s.NextAllowedLocal is not { } next)
        {
            return string.Empty;
        }
        var when = next.Date == localNow.Date ? "hoy"
            : next.Date == localNow.Date.AddDays(1) ? "mañana"
            : "el " + WeekDaysExtensions.Ordered.First(o => o.Day == WeekDaysExtensions.From(next.DayOfWeek)).Long;
        return $" Puedes volver {when} a las {next:HH:mm}.";
    }

    /// <summary>Inicio de la próxima franja (hasta una semana vista).</summary>
    internal static DateTime? NextWindowStart(PlayerRules rules, DateTime localNow)
    {
        if (rules.Schedule.Count == 0)
        {
            return null;
        }
        for (var d = 0; d <= 7; d++)
        {
            var date = localNow.Date.AddDays(d);
            var day = WeekDaysExtensions.From(date.DayOfWeek);
            var starts = rules.Schedule.Where(r => r.Days.HasFlag(day)).Select(r => date + r.From.ToTimeSpan()).Where(t => t > localNow).Order().ToList();
            if (starts.Count > 0)
            {
                return starts[0];
            }
        }
        return null;
    }

    /// <summary>Para el límite diario: el primer momento permitido de mañana.</summary>
    private static DateTime? NextDayStart(PlayerRules rules, DateTime localNow)
    {
        var tomorrow = localNow.Date.AddDays(1);
        return rules.Schedule.Count == 0 ? tomorrow : NextWindowStart(rules, tomorrow.AddTicks(-1));
    }
}
