using System.Text;

namespace CubeKeeper.Core.Properties;

/// <summary>
/// Representa un <c>server.properties</c> conservando comentarios, líneas en blanco, orden y estilo de salto de línea.
/// Solo se reescribe la línea de la clave que cambia; las claves nuevas se añaden al final.
/// </summary>
public sealed class ServerPropertiesDocument
{
    private readonly List<Line> _lines;
    private readonly string _newLine;
    private readonly bool _endsWithNewLine;

    private ServerPropertiesDocument(List<Line> lines, string newLine, bool endsWithNewLine)
    {
        _lines = lines;
        _newLine = newLine;
        _endsWithNewLine = endsWithNewLine;
    }

    public static ServerPropertiesDocument Empty() => new([], "\n", true);

    public static ServerPropertiesDocument Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var newLine = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var endsWithNewLine = text.Length == 0 || text.EndsWith('\n');
        var rawLines = text.Split('\n');
        var count = endsWithNewLine && rawLines.Length > 0 ? rawLines.Length - 1 : rawLines.Length;

        var lines = new List<Line>(count);
        for (var i = 0; i < count; i++)
        {
            lines.Add(ParseLine(rawLines[i].TrimEnd('\r')));
        }

        return new ServerPropertiesDocument(lines, newLine, endsWithNewLine);
    }

    public static async Task<ServerPropertiesDocument> LoadAsync(string path, CancellationToken ct = default)
    {
        if (!File.Exists(path))
        {
            return Empty();
        }
        var text = await File.ReadAllTextAsync(path, Encoding.UTF8, ct).ConfigureAwait(false);
        return Parse(text);
    }

    /// <summary>Claves en el orden en que aparecen.</summary>
    public IEnumerable<string> Keys => _lines.Where(l => l.Key is not null).Select(l => l.Key!).Distinct(StringComparer.Ordinal);

    public bool ContainsKey(string key) => FindLast(key) is not null;

    /// <summary>Valor de la clave, o null si no existe. Si está repetida, gana la última (como hace BDS).</summary>
    public string? Get(string key) => FindLast(key)?.Value;

    public int? GetInt(string key) =>
        int.TryParse(Get(key), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null;

    public bool? GetBool(string key) => Get(key)?.Trim().ToLowerInvariant() switch
    {
        "true" => true,
        "false" => false,
        _ => null,
    };

    /// <summary>Cambia (o añade al final) el valor de una clave. Devuelve true si el documento cambia.</summary>
    public bool Set(string key, string value)
    {
        ValidateKey(key);
        ArgumentNullException.ThrowIfNull(value);
        if (value.Contains('\n') || value.Contains('\r'))
        {
            throw new ArgumentException("El valor no puede contener saltos de línea.", nameof(value));
        }

        var existing = FindLast(key);
        if (existing is not null)
        {
            if (existing.Value == value)
            {
                return false;
            }
            existing.Value = value;
            existing.Raw = null; // se regenera al serializar
            return true;
        }

        _lines.Add(new Line { Key = key, Value = value });
        return true;
    }

    /// <summary>
    /// Añade al final las claves de <paramref name="template"/> que no existan aquí, junto con los comentarios
    /// que las acompañan en la plantilla. Útil al actualizar BDS, que puede traer ajustes nuevos.
    /// Devuelve las claves añadidas.
    /// </summary>
    public IReadOnlyList<string> AddMissingFrom(ServerPropertiesDocument template)
    {
        ArgumentNullException.ThrowIfNull(template);
        var added = new List<string>();
        for (var i = 0; i < template._lines.Count; i++)
        {
            var line = template._lines[i];
            if (line.Key is null || ContainsKey(line.Key) || added.Contains(line.Key))
            {
                continue;
            }

            _lines.Add(new Line { Key = line.Key, Value = line.Value });
            // En el server.properties de BDS, los comentarios van DESPUÉS de la clave.
            for (var j = i + 1; j < template._lines.Count && template._lines[j].IsComment; j++)
            {
                _lines.Add(new Line { Raw = template._lines[j].Raw });
            }
            added.Add(line.Key);
        }
        return added;
    }

    public override string ToString()
    {
        var sb = new StringBuilder();
        for (var i = 0; i < _lines.Count; i++)
        {
            var l = _lines[i];
            sb.Append(l.Raw ?? $"{l.Key}={l.Value}");
            if (i < _lines.Count - 1 || _endsWithNewLine)
            {
                sb.Append(_newLine);
            }
        }
        return sb.ToString();
    }

    /// <summary>Guarda de forma atómica (archivo temporal + renombrado).</summary>
    public async Task SaveAsync(string path, CancellationToken ct = default)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(dir);
        var tmp = Path.Combine(dir, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        await File.WriteAllTextAsync(tmp, ToString(), new UTF8Encoding(false), ct).ConfigureAwait(false);
        File.Move(tmp, path, overwrite: true);
    }

    private Line? FindLast(string key)
    {
        for (var i = _lines.Count - 1; i >= 0; i--)
        {
            if (string.Equals(_lines[i].Key, key, StringComparison.Ordinal))
            {
                return _lines[i];
            }
        }
        return null;
    }

    private static void ValidateKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.IndexOfAny(['=', '\n', '\r', '#']) >= 0 || key.Trim() != key)
        {
            throw new ArgumentException($"Clave no válida: '{key}'.", nameof(key));
        }
    }

    private static Line ParseLine(string raw)
    {
        var trimmed = raw.TrimStart();
        if (trimmed.Length == 0 || trimmed[0] == '#' || trimmed[0] == '!')
        {
            return new Line { Raw = raw, IsComment = trimmed.Length > 0 };
        }

        var eq = raw.IndexOf('=');
        if (eq < 0)
        {
            // Línea sin '=': se conserva tal cual, no es una clave.
            return new Line { Raw = raw };
        }

        return new Line
        {
            Raw = raw,
            Key = raw[..eq].Trim(),
            Value = raw[(eq + 1)..],
        };
    }

    private sealed class Line
    {
        public string? Raw { get; set; }
        public string? Key { get; init; }
        public string? Value { get; set; }
        public bool IsComment { get; init; }
    }
}
