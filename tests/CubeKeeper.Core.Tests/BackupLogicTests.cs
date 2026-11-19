using CubeKeeper.Core.Backups;
using CubeKeeper.Core.Scheduling;

namespace CubeKeeper.Core.Tests;

public class BackupLogicTests
{
    private static readonly TimeZoneInfo Madrid = TimeZoneInfo.FindSystemTimeZoneById("Europe/Madrid");

    [Fact]
    public void Backup_name_round_trips()
    {
        var at = new DateTimeOffset(2026, 11, 19, 17, 30, 5, TimeSpan.FromHours(1));
        var name = BackupName.Format("Bedrock level", at, BackupKind.PreUpdate);

        Assert.Equal("Bedrock level__20261119-163005Z__preupdate.mcworld", name);
        Assert.True(BackupName.TryParse(name, 123, out var b));
        Assert.Equal("Bedrock level", b!.World);
        Assert.Equal(at, b.CreatedAt);
        Assert.Equal(BackupKind.PreUpdate, b.Kind);
        Assert.Equal(123, b.Size);
    }

    [Theory]
    [InlineData("otra-cosa.zip")]
    [InlineData("../x__20261119-163005Z__manual.mcworld")]
    [InlineData("mundo__fecha__manual.mcworld")]
    [InlineData("mundo__20261119-163005Z__inventado.mcworld")]
    [InlineData("__20261119-163005Z__manual.mcworld")]
    public void Rejects_invalid_names(string name) => Assert.False(BackupName.TryParse(name, 0, out _));

    [Fact]
    public void World_names_are_sanitized()
    {
        Assert.Equal("Mi_mundo_ raro", BackupName.Sanitize("Mi/mundo__ raro"));
        var name = BackupName.Format("a__b", DateTimeOffset.UnixEpoch, BackupKind.Manual);
        Assert.True(BackupName.TryParse(name, 0, out var b));
        Assert.Equal("a_b", b!.World);
    }

    private static BackupFile B(DateTimeOffset at, BackupKind kind = BackupKind.Scheduled, string world = "w") =>
        new(BackupName.Format(world, at, kind), world, at, kind, 1);

    [Fact]
    public void Retention_keeps_one_per_hour_day_and_week()
    {
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        // Una copia cada 30 minutos durante 60 días.
        var all = Enumerable.Range(0, 60 * 48).Select(i => B(start.AddMinutes(30 * i))).ToList();

        var delete = new RetentionPolicy(KeepHourly: 24, KeepDaily: 14, KeepWeekly: 8).SelectToDelete(all, Madrid).ToHashSet();
        var kept = all.Where(b => !delete.Contains(b)).OrderByDescending(b => b.CreatedAt).ToList();

        Assert.Equal(all.Max(b => b.CreatedAt), kept[0].CreatedAt); // la más reciente siempre
        Assert.InRange(kept.Count, 24, 24 + 14 + 8);
        // De las últimas 24 horas, una por hora.
        var last24 = kept.Where(b => b.CreatedAt > all.Max(x => x.CreatedAt).AddHours(-24)).ToList();
        Assert.Equal(24, last24.Count);
        // La más antigua conservada tiene ~8 semanas.
        Assert.True(kept[^1].CreatedAt < all.Max(b => b.CreatedAt).AddDays(-40));
    }

    [Fact]
    public void Retention_counts_manual_and_safety_separately_per_world()
    {
        var t = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var manual = Enumerable.Range(0, 5).Select(i => B(t.AddDays(i), BackupKind.Manual)).ToList();
        var safety = Enumerable.Range(0, 4).Select(i => B(t.AddDays(i), i % 2 == 0 ? BackupKind.PreUpdate : BackupKind.PreRestore)).ToList();
        var other = Enumerable.Range(0, 5).Select(i => B(t.AddDays(i), BackupKind.Manual, "otro")).ToList();

        var delete = new RetentionPolicy(KeepManual: 3, KeepSafety: 2).SelectToDelete([.. manual, .. safety, .. other], Madrid);

        Assert.Equal(2 + 2 + 2, delete.Count);
        Assert.DoesNotContain(manual[^1], delete);
        Assert.Contains(manual[0], delete);
        Assert.Contains(safety[0], delete);
        Assert.DoesNotContain(safety[^1], delete);
    }

    [Fact]
    public void Parses_save_query_file_list()
    {
        var files = SaveQueryParser.ParseFileList("Bedrock level/db/000005.ldb:4096, Bedrock level/db/CURRENT:16, Bedrock level/level.dat:2345")!;
        Assert.Equal(3, files.Count);
        Assert.Equal(new SaveFileEntry("Bedrock level/db/000005.ldb", 4096), files[0]);
        Assert.Equal(2345, files[2].Length);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Data saved. Files are now ready to be copied.")]
    [InlineData("w/level.dat:abc")]
    [InlineData("../fuera/level.dat:10")]
    [InlineData("/etc/passwd:10")]
    public void Rejects_bad_file_lists(string line) => Assert.Null(SaveQueryParser.ParseFileList(line));

    [Fact]
    public void Recognizes_ready_and_not_ready()
    {
        Assert.True(SaveQueryParser.IsReady("Data saved. Files are now ready to be copied."));
        Assert.True(SaveQueryParser.IsNotReady("A previous save has not been completed."));
        Assert.False(SaveQueryParser.IsReady("Saving..."));
    }

    [Theory]
    [InlineData("05:00", 5, 0)]
    [InlineData("5:30", 5, 30)]
    [InlineData("23:59", 23, 59)]
    public void Parses_times(string text, int h, int m) => Assert.Equal(new TimeOnly(h, m), DailySchedule.ParseTime(text));

    [Theory]
    [InlineData("25:00")]
    [InlineData("cinco")]
    [InlineData(null)]
    public void Rejects_bad_times(string? text) => Assert.Null(DailySchedule.ParseTime(text));

    [Fact]
    public void Next_occurrence_is_today_or_tomorrow_in_local_time()
    {
        // 19/11 03:00 en Madrid (UTC+1) → hoy a las 05:00.
        var now = new DateTimeOffset(2026, 11, 19, 2, 0, 0, TimeSpan.Zero);
        Assert.Equal(new DateTimeOffset(2026, 11, 19, 5, 0, 0, TimeSpan.FromHours(1)), DailySchedule.Next(now, new TimeOnly(5, 0), Madrid));
        // 19/11 06:00 en Madrid → mañana.
        var later = new DateTimeOffset(2026, 11, 19, 5, 0, 0, TimeSpan.Zero);
        Assert.Equal(new DateTimeOffset(2026, 11, 20, 5, 0, 0, TimeSpan.FromHours(1)), DailySchedule.Next(later, new TimeOnly(5, 0), Madrid));
    }

    [Fact]
    public void Next_occurrence_skips_nonexistent_spring_forward_hour()
    {
        // 29/03/2026: en Madrid se pasa de 02:00 a 03:00.
        var now = new DateTimeOffset(2026, 3, 28, 23, 0, 0, TimeSpan.Zero);
        var next = DailySchedule.Next(now, new TimeOnly(2, 30), Madrid);
        Assert.Equal(new DateTimeOffset(2026, 3, 29, 3, 30, 0, TimeSpan.FromHours(2)), next);
    }

    [Theory]
    [InlineData("5,1", new[] { 5, 1 })]
    [InlineData("1, 5, 5", new[] { 5, 1 })]
    [InlineData("10;x;0;-3", new[] { 10 })]
    [InlineData("", new int[0])]
    public void Parses_warning_minutes(string text, int[] expected) => Assert.Equal(expected, DailySchedule.ParseWarnings(text));
}
