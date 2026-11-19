using System.Globalization;
using System.Text.RegularExpressions;

namespace CubeKeeper.Core.Versions;

/// <summary>Versión de BDS del tipo 1.26.10.4, comparable numéricamente parte a parte.</summary>
public sealed partial record BdsVersion : IComparable<BdsVersion>
{
    private readonly int[] _parts;

    private BdsVersion(int[] parts, string text)
    {
        _parts = parts;
        Text = text;
    }

    public string Text { get; }

    [GeneratedRegex(@"^\d+(\.\d+){1,4}$", RegexOptions.CultureInvariant)]
    private static partial Regex VersionRegex();

    [GeneratedRegex(@"bedrock-server-(?<v>\d+(?:\.\d+){1,4})\.zip", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex UrlRegex();

    public static bool TryParse(string? text, out BdsVersion? version)
    {
        version = null;
        if (text is null || !VersionRegex().IsMatch(text.Trim()))
        {
            return false;
        }
        var t = text.Trim();
        version = new BdsVersion(t.Split('.').Select(p => int.Parse(p, CultureInfo.InvariantCulture)).ToArray(), t);
        return true;
    }

    public static BdsVersion Parse(string text) =>
        TryParse(text, out var v) ? v! : throw new FormatException($"Versión de BDS no válida: '{text}'.");

    /// <summary>Extrae la versión del nombre del zip: .../bedrock-server-1.26.10.4.zip.</summary>
    public static BdsVersion? FromDownloadUrl(string url)
    {
        var m = UrlRegex().Match(url ?? string.Empty);
        return m.Success && TryParse(m.Groups["v"].Value, out var v) ? v : null;
    }

    public int CompareTo(BdsVersion? other)
    {
        if (other is null)
        {
            return 1;
        }
        var n = Math.Max(_parts.Length, other._parts.Length);
        for (var i = 0; i < n; i++)
        {
            var a = i < _parts.Length ? _parts[i] : 0;
            var b = i < other._parts.Length ? other._parts[i] : 0;
            if (a != b)
            {
                return a.CompareTo(b);
            }
        }
        return 0;
    }

    public bool Equals(BdsVersion? other) => other is not null && CompareTo(other) == 0;

    public override int GetHashCode()
    {
        var trimmed = _parts.Reverse().SkipWhile(p => p == 0).Reverse();
        var hc = new HashCode();
        foreach (var p in trimmed)
        {
            hc.Add(p);
        }
        return hc.ToHashCode();
    }

    public static bool operator <(BdsVersion a, BdsVersion b) => a.CompareTo(b) < 0;
    public static bool operator >(BdsVersion a, BdsVersion b) => a.CompareTo(b) > 0;
    public static bool operator <=(BdsVersion a, BdsVersion b) => a.CompareTo(b) <= 0;
    public static bool operator >=(BdsVersion a, BdsVersion b) => a.CompareTo(b) >= 0;

    public override string ToString() => Text;
}
