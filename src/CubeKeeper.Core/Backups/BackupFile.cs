using System.Globalization;
using System.Text;

namespace CubeKeeper.Core.Backups;

public enum BackupKind
{
    /// <summary>Copia periódica automática.</summary>
    Scheduled,
    /// <summary>Hecha a mano desde el panel.</summary>
    Manual,
    /// <summary>Automática antes de actualizar el servidor.</summary>
    PreUpdate,
    /// <summary>Automática antes de restaurar otra copia.</summary>
    PreRestore,
}

/// <summary>Una copia del mundo en disco (.mcworld).</summary>
public sealed record BackupFile(string FileName, string World, DateTimeOffset CreatedAt, BackupKind Kind, long Size);

/// <summary>
/// Nombre de archivo de las copias: <c>&lt;mundo&gt;__&lt;AAAAMMDD-HHMMSS&gt;Z__&lt;tipo&gt;.mcworld</c> (hora UTC).
/// El propio nombre guarda los metadatos: no hace falta base de datos y se puede copiar o borrar a mano.
/// </summary>
public static class BackupName
{
    public const string Extension = ".mcworld";
    private const string Separator = "__";
    private const string TimeFormat = "yyyyMMdd-HHmmss'Z'";

    public static string Format(string world, DateTimeOffset createdAt, BackupKind kind) =>
        $"{Sanitize(world)}{Separator}{createdAt.UtcDateTime.ToString(TimeFormat, CultureInfo.InvariantCulture)}{Separator}{kind.ToString().ToLowerInvariant()}{Extension}";

    public static bool TryParse(string fileName, long size, out BackupFile? backup)
    {
        backup = null;
        if (string.IsNullOrEmpty(fileName) || !fileName.EndsWith(Extension, StringComparison.OrdinalIgnoreCase) ||
            fileName.Contains('/') || fileName.Contains('\\'))
        {
            return false;
        }
        var stem = fileName[..^Extension.Length];
        var kindSep = stem.LastIndexOf(Separator, StringComparison.Ordinal);
        if (kindSep <= 0)
        {
            return false;
        }
        var timeSep = stem.LastIndexOf(Separator, kindSep - 1, StringComparison.Ordinal);
        if (timeSep <= 0)
        {
            return false;
        }

        var world = stem[..timeSep];
        var time = stem[(timeSep + Separator.Length)..kindSep];
        var kindText = stem[(kindSep + Separator.Length)..];
        if (!DateTime.TryParseExact(time, TimeFormat, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var utc) ||
            !Enum.TryParse<BackupKind>(kindText, ignoreCase: true, out var kind) ||
            !Enum.IsDefined(kind))
        {
            return false;
        }

        backup = new BackupFile(fileName, world, new DateTimeOffset(utc, TimeSpan.Zero), kind, size);
        return true;
    }

    /// <summary>Quita del nombre del mundo lo que no vale en un nombre de archivo (y el separador).</summary>
    public static string Sanitize(string world)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat(['/', '\\', ':', '*', '?', '"', '<', '>', '|']).ToHashSet();
        var sb = new StringBuilder(world.Length);
        foreach (var c in world.Trim())
        {
            sb.Append(invalid.Contains(c) || char.IsControl(c) ? '_' : c);
        }
        var result = sb.ToString();
        while (result.Contains(Separator, StringComparison.Ordinal))
        {
            result = result.Replace(Separator, "_", StringComparison.Ordinal);
        }
        return result.Length == 0 ? "mundo" : result;
    }
}
