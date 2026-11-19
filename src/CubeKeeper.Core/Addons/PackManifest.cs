using System.Text.Json;
using System.Text.Json.Nodes;

namespace CubeKeeper.Core.Addons;

public enum PackType
{
    /// <summary>Paquete de comportamiento (módulos "data" o "script"): va en world_behavior_packs.json.</summary>
    Behavior,
    /// <summary>Paquete de recursos (módulo "resources"): va en world_resource_packs.json.</summary>
    Resource,
}

/// <summary>Lo que interesa del manifest.json de un paquete.</summary>
public sealed record PackManifest(Guid Uuid, int[] Version, string Name, string? Description, PackType Type, bool UsesScripts)
{
    public string VersionText => string.Join('.', Version);

    private static readonly JsonDocumentOptions Options = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    /// <summary>
    /// Interpreta un manifest.json (admite comentarios y comas finales, que algunos paquetes traen).
    /// <paramref name="langLookup"/> resuelve nombres traducidos como «pack.name» a partir de texts/*.lang.
    /// Devuelve null si no es un manifiesto de paquete válido (por ejemplo, el de una plantilla de mundo).
    /// </summary>
    public static PackManifest? Parse(string json, Func<string, string?>? langLookup = null)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json, documentOptions: Options);
        }
        catch (JsonException)
        {
            return null;
        }
        var header = root?["header"];
        if (header is null || !Guid.TryParse(header["uuid"]?.GetValue<string>(), out var uuid))
        {
            return null;
        }

        var version = ParseVersion(header["version"]);
        if (version is null)
        {
            return null;
        }

        var types = (root!["modules"] as JsonArray ?? [])
            .Select(m => m?["type"]?.GetValue<string>()?.ToLowerInvariant())
            .Where(t => t is not null)
            .ToList();
        PackType type;
        if (types.Contains("resources"))
        {
            type = PackType.Resource;
        }
        else if (types.Any(t => t is "data" or "script" or "javascript" or "client_data"))
        {
            type = PackType.Behavior;
        }
        else
        {
            return null; // skin_pack, world_template…: no se activan en el mundo.
        }

        var name = Resolve(header["name"]?.GetValue<string>(), langLookup) ?? uuid.ToString();
        var description = Resolve(header["description"]?.GetValue<string>(), langLookup);
        var scripts = types.Any(t => t is "script" or "javascript");
        return new PackManifest(uuid, version, name, description, type, scripts);
    }

    private static int[]? ParseVersion(JsonNode? node)
    {
        switch (node)
        {
            case JsonArray arr when arr.Count is >= 1 and <= 4:
                try
                {
                    return arr.Select(n => n!.GetValue<int>()).ToArray();
                }
                catch (Exception ex) when (ex is InvalidOperationException or FormatException or NullReferenceException)
                {
                    return null;
                }
            case JsonValue v when v.TryGetValue<string>(out var s):
                // Formato nuevo: "1.2.3" (o con sufijo "-beta").
                var parts = s.Split('-')[0].Split('.');
                return parts.All(p => int.TryParse(p, out _)) && parts.Length is >= 1 and <= 4 ? parts.Select(int.Parse).ToArray() : null;
            default:
                return null;
        }
    }

    private static string? Resolve(string? text, Func<string, string?>? langLookup)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }
        // "pack.name", "pack.description": son claves de traducción.
        if (langLookup is not null && !text.Contains(' ') && text.Contains('.') && langLookup(text) is { } translated)
        {
            return translated;
        }
        return text;
    }

    /// <summary>Busca una clave en un archivo .lang (líneas «clave=valor», con comentarios «##»).</summary>
    public static string? LookupLang(string langText, string key)
    {
        foreach (var raw in langText.Split('\n'))
        {
            var line = raw.Trim();
            var hash = line.IndexOf("\t#", StringComparison.Ordinal);
            if (hash >= 0)
            {
                line = line[..hash].Trim();
            }
            var eq = line.IndexOf('=');
            if (eq > 0 && line[..eq].Trim() == key)
            {
                return line[(eq + 1)..].Trim();
            }
        }
        return null;
    }
}

/// <summary>
/// world_behavior_packs.json / world_resource_packs.json: <c>[{"pack_id":"…","version":[1,0,0]}]</c>.
/// </summary>
public sealed class WorldPacksDocument
{
    private readonly List<(Guid Id, int[] Version)> _packs;

    private WorldPacksDocument(List<(Guid, int[])> packs)
    {
        _packs = packs;
    }

    public IReadOnlyList<(Guid Id, int[] Version)> Packs => _packs;

    public static WorldPacksDocument Parse(string? json)
    {
        var list = new List<(Guid, int[])>();
        if (string.IsNullOrWhiteSpace(json))
        {
            return new WorldPacksDocument(list);
        }
        var root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        foreach (var node in (root as JsonArray ?? []).OfType<JsonObject>())
        {
            if (Guid.TryParse(node["pack_id"]?.GetValue<string>(), out var id))
            {
                var version = node["version"] is JsonArray v ? v.Select(n => n?.GetValue<int>() ?? 0).ToArray() : [0, 0, 0];
                list.Add((id, version));
            }
        }
        return new WorldPacksDocument(list);
    }

    public bool Contains(Guid id) => _packs.Any(p => p.Id == id);

    /// <summary>Activa (o actualiza la versión de) un paquete. Devuelve true si cambia algo.</summary>
    public bool Enable(Guid id, int[] version)
    {
        var i = _packs.FindIndex(p => p.Id == id);
        if (i >= 0)
        {
            if (_packs[i].Version.SequenceEqual(version))
            {
                return false;
            }
            _packs[i] = (id, version);
            return true;
        }
        _packs.Add((id, version));
        return true;
    }

    public bool Disable(Guid id) => _packs.RemoveAll(p => p.Id == id) > 0;

    public string ToJson()
    {
        var arr = new JsonArray();
        foreach (var (id, version) in _packs)
        {
            arr.Add(new JsonObject { ["pack_id"] = id.ToString(), ["version"] = new JsonArray(version.Select(v => (JsonNode)v).ToArray()) });
        }
        return arr.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";
    }
}
