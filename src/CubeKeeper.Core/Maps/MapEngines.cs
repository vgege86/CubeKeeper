using System.Text;

namespace CubeKeeper.Core.Maps;

/// <summary>Un programa externo que dibuja el mapa del mundo.</summary>
/// <param name="ExecutableNames">Nombres de ejecutable que se buscan dentro de lo que se instala.</param>
/// <param name="DefaultCommand">
/// Argumentos por defecto. Se pueden cambiar desde el panel si una versión usa otros. {world} = carpeta del mundo,
/// {output} = carpeta de salida, {threads} = hilos.
/// </param>
public sealed record MapEngine(string Id, string Name, IReadOnlyList<string> ExecutableNames, string DefaultCommand, string Help);

public static class MapEngines
{
    public static readonly MapEngine Unmined = new(
        "unmined",
        "uNmINeD",
        ["unmined-cli"],
        "web render --world \"{world}\" --output \"{output}\"",
        "Descarga la versión CLI para Linux x64 desde unmined.net (gratuita para uso personal) y súbela aquí, o pega el enlace de descarga.");

    public static readonly MapEngine BedrockViz = new(
        "bedrock-viz",
        "bedrock-viz",
        ["bedrock-viz", "bedrock_viz"],
        "--db \"{world}\" --out \"{output}\" --html-most",
        "Alternativa de código abierto. Sube el ejecutable de Linux (o un .tar.gz/.zip que lo contenga), o pega su enlace de descarga.");

    public static readonly IReadOnlyList<MapEngine> All = [Unmined, BedrockViz];

    public static MapEngine? Find(string? id) => All.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.Ordinal));
}

/// <summary>
/// Convierte una línea de argumentos con marcadores en una lista de argumentos, sin pasar por una shell: las comillas
/// agrupan, y los valores sustituidos nunca se vuelven a partir (una ruta con espacios sigue siendo un solo argumento).
/// </summary>
public static class CommandTemplate
{
    public static IReadOnlyList<string> Build(string template, IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(values);
        if (template.Contains('\n') || template.Contains('\r'))
        {
            throw new ArgumentException("El comando debe ir en una sola línea.", nameof(template));
        }

        var args = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        var hasToken = false;
        for (var i = 0; i < template.Length; i++)
        {
            var c = template[i];
            if (c == '"')
            {
                inQuotes = !inQuotes;
                hasToken = true;
            }
            else if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (hasToken)
                {
                    args.Add(current.ToString());
                    current.Clear();
                    hasToken = false;
                }
            }
            else if (c == '{')
            {
                var end = template.IndexOf('}', i);
                if (end < 0)
                {
                    throw new ArgumentException("Falta una llave de cierre en el comando.", nameof(template));
                }
                var key = template[(i + 1)..end];
                if (!values.TryGetValue(key, out var value))
                {
                    throw new ArgumentException($"Marcador desconocido: {{{key}}}. Se admiten: {string.Join(", ", values.Keys.Select(k => "{" + k + "}"))}.", nameof(template));
                }
                current.Append(value);
                hasToken = true;
                i = end;
            }
            else
            {
                current.Append(c);
                hasToken = true;
            }
        }
        if (inQuotes)
        {
            throw new ArgumentException("Faltan unas comillas de cierre en el comando.", nameof(template));
        }
        if (hasToken)
        {
            args.Add(current.ToString());
        }
        return args;
    }
}
