namespace CubeKeeper.Core.Worlds;

/// <summary>Reglas para los nombres de mundo (carpetas dentro de worlds/).</summary>
public static class WorldNames
{
    public const int MaxLength = 64;
    private static readonly char[] Invalid = ['/', '\\', ':', '*', '?', '"', '<', '>', '|'];

    /// <summary>Mensaje de error en castellano, o null si el nombre vale.</summary>
    public static string? Validate(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "Escribe un nombre.";
        }
        if (name != name.Trim())
        {
            return "No puede empezar ni acabar con espacios.";
        }
        if (name.Length > MaxLength)
        {
            return $"Como mucho {MaxLength} caracteres.";
        }
        if (name.StartsWith('.') || name is "." or "..")
        {
            return "No puede empezar por un punto.";
        }
        if (name.IndexOfAny(Invalid) >= 0 || name.Any(char.IsControl))
        {
            return "No puede contener / \\ : * ? \" < > |";
        }
        return null;
    }

    /// <summary>Convierte un texto cualquiera (nombre de archivo, levelname.txt) en un nombre válido.</summary>
    public static string FromText(string? text)
    {
        var chars = (text ?? string.Empty).Select(c => Invalid.Contains(c) || char.IsControl(c) ? ' ' : c).ToArray();
        var name = string.Join(' ', new string(chars).Split(' ', StringSplitOptions.RemoveEmptyEntries)).TrimStart('.');
        if (name.Length > MaxLength)
        {
            name = name[..MaxLength].Trim();
        }
        return name.Length == 0 ? "Mundo importado" : name;
    }
}
