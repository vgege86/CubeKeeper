using System.Globalization;

namespace CubeKeeper.Core.Backups;

/// <summary>Archivo que BDS indica en la respuesta a <c>save query</c>: hay que copiar solo los primeros <see cref="Length"/> bytes.</summary>
public sealed record SaveFileEntry(string RelativePath, long Length);

/// <summary>
/// Respuesta de <c>save query</c> tras <c>save hold</c>:
/// <c>Data saved. Files are now ready to be copied.</c> y, en la línea siguiente,
/// <c>Bedrock level/db/000005.ldb:1234, Bedrock level/level.dat:2345, …</c> (rutas relativas a worlds/).
/// </summary>
public static class SaveQueryParser
{
    public const string ReadyText = "Files are now ready to be copied";
    public const string NotReadyText = "A previous save has not been completed";

    public static bool IsReady(string message) => message.Contains(ReadyText, StringComparison.OrdinalIgnoreCase);

    public static bool IsNotReady(string message) => message.Contains(NotReadyText, StringComparison.OrdinalIgnoreCase);

    /// <summary>Interpreta la lista de archivos. Devuelve null si la línea no tiene ese formato.</summary>
    public static IReadOnlyList<SaveFileEntry>? ParseFileList(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return null;
        }
        var result = new List<SaveFileEntry>();
        foreach (var part in message.Split(", ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var colon = part.LastIndexOf(':');
            if (colon <= 0 ||
                !long.TryParse(part[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var length))
            {
                return null;
            }
            var path = part[..colon].Replace('\\', '/');
            if (path.StartsWith('/') || path.Split('/').Any(s => s is ".." or ""))
            {
                return null; // Nunca se copia nada fuera de worlds/.
            }
            result.Add(new SaveFileEntry(path, length));
        }
        return result.Count == 0 ? null : result;
    }
}
