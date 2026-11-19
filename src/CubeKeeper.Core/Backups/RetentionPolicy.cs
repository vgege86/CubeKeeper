using System.Globalization;

namespace CubeKeeper.Core.Backups;

/// <summary>Cuántas copias se guardan de cada tipo.</summary>
public sealed record RetentionPolicy(int KeepHourly = 24, int KeepDaily = 14, int KeepWeekly = 8, int KeepManual = 20, int KeepSafety = 5)
{
    /// <summary>
    /// Devuelve las copias que sobran. Para las programadas se usa un esquema abuelo-padre-hijo:
    /// la más reciente de cada una de las últimas <see cref="KeepHourly"/> horas, de los últimos <see cref="KeepDaily"/> días
    /// y de las últimas <see cref="KeepWeekly"/> semanas (en la zona horaria indicada). Las manuales y las de seguridad
    /// (antes de actualizar o restaurar) se cuentan aparte. Cada mundo se trata por separado.
    /// </summary>
    public IReadOnlyList<BackupFile> SelectToDelete(IEnumerable<BackupFile> backups, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(backups);
        ArgumentNullException.ThrowIfNull(zone);
        var delete = new List<BackupFile>();

        foreach (var world in backups.GroupBy(b => b.World, StringComparer.Ordinal))
        {
            var newestFirst = world.OrderByDescending(b => b.CreatedAt).ToList();

            var scheduled = newestFirst.Where(b => b.Kind == BackupKind.Scheduled).ToList();
            var keep = new HashSet<BackupFile>();
            KeepNewestPerBucket(scheduled, KeepHourly, b => Local(b, zone).ToString("yyyyMMddHH", CultureInfo.InvariantCulture), keep);
            KeepNewestPerBucket(scheduled, KeepDaily, b => Local(b, zone).ToString("yyyyMMdd", CultureInfo.InvariantCulture), keep);
            KeepNewestPerBucket(scheduled, KeepWeekly, b =>
            {
                var d = Local(b, zone);
                return $"{ISOWeek.GetYear(d)}-{ISOWeek.GetWeekOfYear(d):00}";
            }, keep);
            delete.AddRange(scheduled.Where(b => !keep.Contains(b)));

            delete.AddRange(newestFirst.Where(b => b.Kind == BackupKind.Manual).Skip(Math.Max(0, KeepManual)));
            delete.AddRange(newestFirst.Where(b => b.Kind is BackupKind.PreUpdate or BackupKind.PreRestore or BackupKind.PreChange).Skip(Math.Max(0, KeepSafety)));
        }
        return delete;
    }

    private static DateTime Local(BackupFile b, TimeZoneInfo zone) => TimeZoneInfo.ConvertTime(b.CreatedAt, zone).DateTime;

    private static void KeepNewestPerBucket(List<BackupFile> newestFirst, int buckets, Func<BackupFile, string> bucketOf, HashSet<BackupFile> keep)
    {
        if (buckets <= 0)
        {
            return;
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var b in newestFirst)
        {
            if (seen.Add(bucketOf(b)))
            {
                keep.Add(b);
                if (seen.Count == buckets)
                {
                    return;
                }
            }
        }
    }
}
