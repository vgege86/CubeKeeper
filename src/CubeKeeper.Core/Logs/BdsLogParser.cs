using System.Globalization;
using System.Text.RegularExpressions;

namespace CubeKeeper.Core.Logs;

/// <summary>
/// Convierte líneas de salida de bedrock_server en eventos tipados. Sin estado: cada línea se analiza por separado.
/// </summary>
public static partial class BdsLogParser
{
    // El nombre puede contener espacios, comas raras no, y sufijos tipo "Alucard#9583".
    // Se toma todo hasta ", xuid: " (con o sin coma antes de "xuid").
    [GeneratedRegex(@"^Player connected:\s*(?<name>.+?),?\s+xuid:\s*(?<xuid>\d*)", RegexOptions.CultureInvariant)]
    private static partial Regex ConnectedRegex();

    [GeneratedRegex(@"^Player disconnected:\s*(?<name>.+?),?\s+xuid:\s*(?<xuid>\d*)", RegexOptions.CultureInvariant)]
    private static partial Regex DisconnectedRegex();

    [GeneratedRegex(@"^Player Spawned:\s*(?<name>.+?),?\s+xuid:\s*(?<xuid>\d*)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex SpawnedRegex();

    [GeneratedRegex(@"^Version:?\s+(?<v>\d+(?:\.\d+){1,4})\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex VersionRegex();

    [GeneratedRegex(@"^There are (?<on>\d+)/(?<max>\d+) players online:?", RegexOptions.CultureInvariant)]
    private static partial Regex ListRegex();

    public static BdsLogEvent? Parse(string rawLine) => Parse(BdsLogLine.Parse(rawLine));

    public static BdsLogEvent? Parse(BdsLogLine line)
    {
        ArgumentNullException.ThrowIfNull(line);
        var msg = line.Message.Trim();
        if (msg.Length == 0)
        {
            return null;
        }

        if (msg.Equals("Server started.", StringComparison.Ordinal))
        {
            return new ServerStarted();
        }

        if (msg.StartsWith("Stopping server", StringComparison.Ordinal))
        {
            return new ServerStopping();
        }

        Match m;
        if ((m = ConnectedRegex().Match(msg)).Success)
        {
            return new PlayerConnected(m.Groups["name"].Value.Trim(), m.Groups["xuid"].Value);
        }

        if ((m = DisconnectedRegex().Match(msg)).Success)
        {
            return new PlayerDisconnected(m.Groups["name"].Value.Trim(), m.Groups["xuid"].Value);
        }

        if ((m = SpawnedRegex().Match(msg)).Success)
        {
            return new PlayerSpawned(m.Groups["name"].Value.Trim(), m.Groups["xuid"].Value);
        }

        if ((m = VersionRegex().Match(msg)).Success)
        {
            return new ServerVersionReported(m.Groups["v"].Value);
        }

        if ((m = ListRegex().Match(msg)).Success)
        {
            return new PlayerCountReported(
                int.Parse(m.Groups["on"].Value, CultureInfo.InvariantCulture),
                int.Parse(m.Groups["max"].Value, CultureInfo.InvariantCulture));
        }

        return null;
    }
}
