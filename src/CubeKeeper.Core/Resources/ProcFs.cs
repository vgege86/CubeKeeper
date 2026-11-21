using System.Globalization;

namespace CubeKeeper.Core.Resources;

/// <summary>Lectura de /proc y de los cgroups (v1 y v2) de Linux, sin dependencias, para poder probarla.</summary>
public static class ProcFs
{
    /// <summary>Memoria residente (VmRSS) de /proc/&lt;pid&gt;/status, en bytes.</summary>
    public static long? ParseRssBytes(string status)
    {
        ArgumentNullException.ThrowIfNull(status);
        foreach (var line in status.Split('\n'))
        {
            if (line.StartsWith("VmRSS:", StringComparison.Ordinal))
            {
                var parts = line["VmRSS:".Length..].Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length > 0 && long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var kb))
                {
                    return kb * 1024;
                }
            }
        }
        return null;
    }

    /// <summary>utime + stime (en ticks) de /proc/&lt;pid&gt;/stat. El nombre va entre paréntesis y puede tener espacios.</summary>
    public static long? ParseCpuTicks(string stat)
    {
        ArgumentNullException.ThrowIfNull(stat);
        var close = stat.LastIndexOf(')');
        if (close < 0)
        {
            return null;
        }
        // Tras «) » vienen los campos desde el 3 (state); utime y stime son el 14 y el 15.
        var fields = stat[(close + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 13 ||
            !long.TryParse(fields[11], NumberStyles.None, CultureInfo.InvariantCulture, out var utime) ||
            !long.TryParse(fields[12], NumberStyles.None, CultureInfo.InvariantCulture, out var stime))
        {
            return null;
        }
        return utime + stime;
    }

    /// <summary>Un número de un archivo de cgroup («max» o un valor enorme = sin límite → null).</summary>
    public static long? ParseLimit(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var t = text.Trim();
        if (t == "max" || !long.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
        {
            return null;
        }
        // cgroup v1 pone un número cercano a long.MaxValue cuando no hay límite.
        return value >= long.MaxValue / 2 ? null : value;
    }

    /// <summary>Un campo de memory.stat (por ejemplo inactive_file en v2, total_inactive_file en v1).</summary>
    public static long? ParseStat(string memoryStat, string key)
    {
        ArgumentNullException.ThrowIfNull(memoryStat);
        foreach (var line in memoryStat.Split('\n'))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && parts[0] == key && long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var v))
            {
                return v;
            }
        }
        return null;
    }

    /// <summary>CPU disponibles según cpu.max de cgroup v2 («400000 100000» → 4); null si no hay límite.</summary>
    public static double? ParseCpuMax(string cpuMax)
    {
        ArgumentNullException.ThrowIfNull(cpuMax);
        var parts = cpuMax.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || parts[0] == "max" ||
            !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var quota) ||
            !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var period) || period <= 0)
        {
            return null;
        }
        return quota / period;
    }
}
