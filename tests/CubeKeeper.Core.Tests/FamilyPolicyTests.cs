using CubeKeeper.Core.Family;

namespace CubeKeeper.Core.Tests;

public class FamilyPolicyTests
{
    // Miércoles 18/11/2026.
    private static readonly DateTime Wed18 = new(2026, 11, 18, 18, 0, 0);

    private static PlayerRules Rules(int? daily = null, params ScheduleRule[] schedule) => new(true, daily, schedule);

    private static ScheduleRule Weekdays(int fromH, int fromM, int toH, int toM) =>
        new(WeekDays.Weekdays, new TimeOnly(fromH, fromM), new TimeOnly(toH, toM));

    [Fact]
    public void Disabled_rules_always_allow()
    {
        var s = FamilyPolicy.Evaluate(PlayerRules.None, Wed18, TimeSpan.FromHours(10));
        Assert.True(s.Allowed);
        Assert.Null(s.Remaining);
    }

    [Fact]
    public void Daily_limit_counts_down_and_blocks()
    {
        var ok = FamilyPolicy.Evaluate(Rules(daily: 60), Wed18, TimeSpan.FromMinutes(45));
        Assert.True(ok.Allowed);
        Assert.Equal(TimeSpan.FromMinutes(15), ok.Remaining);

        var over = FamilyPolicy.Evaluate(Rules(daily: 60), Wed18, TimeSpan.FromMinutes(60));
        Assert.False(over.Allowed);
        Assert.Equal(BlockReason.DailyLimit, over.Reason);
        Assert.Equal(new DateTime(2026, 11, 19), over.NextAllowedLocal);
        Assert.Contains("mañana", FamilyPolicy.KickMessage(over, Wed18));
        Assert.Contains("1 h", FamilyPolicy.KickMessage(over, Wed18));
    }

    [Fact]
    public void Inside_window_remaining_is_the_earliest_limit()
    {
        var r = Rules(daily: 120, Weekdays(17, 0, 18, 30));
        // Quedan 30 min de franja y 100 min de tiempo diario: manda la franja.
        Assert.Equal(TimeSpan.FromMinutes(30), FamilyPolicy.Evaluate(r, Wed18, TimeSpan.FromMinutes(20)).Remaining);
        // Quedan 30 min de franja y 10 min de tiempo diario: manda el límite.
        Assert.Equal(TimeSpan.FromMinutes(10), FamilyPolicy.Evaluate(r, Wed18, TimeSpan.FromMinutes(110)).Remaining);
    }

    [Fact]
    public void Outside_window_blocks_and_says_when_next()
    {
        var r = Rules(null, Weekdays(17, 0, 17, 30), new ScheduleRule(WeekDays.Weekend, new TimeOnly(10, 0), new TimeOnly(21, 0)));
        var s = FamilyPolicy.Evaluate(r, Wed18, TimeSpan.Zero);
        Assert.False(s.Allowed);
        Assert.Equal(BlockReason.OutsideSchedule, s.Reason);
        Assert.Equal(new DateTime(2026, 11, 19, 17, 0, 0), s.NextAllowedLocal);
        Assert.Equal("Ahora no es horario de juego. Puedes volver mañana a las 17:00.", FamilyPolicy.KickMessage(s, Wed18));

        // Viernes por la noche: lo siguiente es el sábado a las 10.
        var friday = new DateTime(2026, 11, 20, 22, 0, 0);
        var f = FamilyPolicy.Evaluate(r, friday, TimeSpan.Zero);
        Assert.Equal(new DateTime(2026, 11, 21, 10, 0, 0), f.NextAllowedLocal);
        Assert.Contains("mañana a las 10:00", FamilyPolicy.KickMessage(f, friday));
    }

    [Fact]
    public void Window_end_is_exclusive_and_overlapping_windows_merge()
    {
        var r = Rules(null, Weekdays(16, 0, 18, 0), Weekdays(17, 30, 19, 0));
        Assert.Equal(TimeSpan.FromHours(1), FamilyPolicy.Evaluate(r, Wed18, TimeSpan.Zero).Remaining);
        Assert.False(FamilyPolicy.Evaluate(Rules(null, Weekdays(16, 0, 18, 0)), Wed18, TimeSpan.Zero).Allowed);
    }

    [Fact]
    public void Extra_minutes_only_count_on_their_day()
    {
        var today = DateOnly.FromDateTime(Wed18);
        var r = Rules(daily: 60) with { ExtraMinutes = 30, ExtraDate = today };
        var s = FamilyPolicy.Evaluate(r, Wed18, TimeSpan.FromMinutes(70));
        Assert.True(s.Allowed);
        Assert.Equal(TimeSpan.FromMinutes(20), s.Remaining);
        Assert.False(FamilyPolicy.Evaluate(r, Wed18.AddDays(1), TimeSpan.FromMinutes(70)).Allowed);
    }

    [Fact]
    public void Schedule_serialization_round_trips_and_ignores_garbage()
    {
        var rules = new[] { Weekdays(17, 0, 20, 30), new ScheduleRule(WeekDays.Weekend, new TimeOnly(10, 0), new TimeOnly(21, 0)) };
        var json = PlayerRules.SerializeSchedule(rules);
        Assert.Equal(rules, PlayerRules.DeserializeSchedule(json));
        Assert.Empty(PlayerRules.DeserializeSchedule("no es json"));
        Assert.Empty(PlayerRules.DeserializeSchedule("""[{"Days":"Monday","From":"20:00","To":"10:00"}]"""));
    }

    [Theory]
    [InlineData(4.5, "Te quedan 5 minutos de juego. ¡Ve guardando!")]
    [InlineData(0.5, "Te queda 1 minuto de juego. ¡Ve guardando!")]
    public void Warning_messages(double minutes, string expected) =>
        Assert.Equal(expected, FamilyPolicy.WarningMessage(TimeSpan.FromMinutes(minutes)));

    [Fact]
    public void Days_in_spanish()
    {
        Assert.Equal("de lunes a viernes", WeekDays.Weekdays.ToSpanish());
        Assert.Equal("lunes, miércoles", (WeekDays.Monday | WeekDays.Wednesday).ToSpanish());
    }
}
