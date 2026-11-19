using System.Globalization;
using System.Text.RegularExpressions;

namespace CubeKeeper.Core.Logs;

/// <summary>
/// Línea de salida de BDS separada en marca de tiempo, nivel y mensaje.
/// Formato habitual: <c>[2026-11-19 10:00:00:123 INFO] Mensaje</c>.
/// Las líneas sin prefijo (por ejemplo, la lista de nombres tras "list") se conservan tal cual.
/// </summary>
public sealed partial record BdsLogLine(DateTimeOffset? Timestamp, string? Level, string Message, string Raw)
{
    [GeneratedRegex(@"^\[(?<ts>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2})(?::(?<ms>\d{1,3}))? (?<lvl>[A-Z]+)\]\s?(?<msg>.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex PrefixRegex();

    public static BdsLogLine Parse(string raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        var line = raw.TrimEnd('\r', '\n');
        var m = PrefixRegex().Match(line);
        if (!m.Success)
        {
            return new BdsLogLine(null, null, line, line);
        }

        DateTimeOffset? ts = null;
        if (DateTime.TryParseExact(m.Groups["ts"].Value, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal, out var dt))
        {
            if (m.Groups["ms"].Success && int.TryParse(m.Groups["ms"].Value, CultureInfo.InvariantCulture, out var ms))
            {
                dt = dt.AddMilliseconds(ms);
            }
            ts = new DateTimeOffset(dt);
        }

        return new BdsLogLine(ts, m.Groups["lvl"].Value, m.Groups["msg"].Value, line);
    }
}
