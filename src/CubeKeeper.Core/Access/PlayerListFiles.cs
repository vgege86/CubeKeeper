using System.Text.Json;
using System.Text.Json.Nodes;

namespace CubeKeeper.Core.Access;

public enum PermissionLevel
{
    Visitor,
    Member,
    Operator,
}

public static class PermissionLevelText
{
    public static string ToBds(this PermissionLevel p) => p.ToString().ToLowerInvariant();

    public static PermissionLevel? FromBds(string? s) => s?.Trim().ToLowerInvariant() switch
    {
        "visitor" => PermissionLevel.Visitor,
        "member" => PermissionLevel.Member,
        "operator" => PermissionLevel.Operator,
        _ => null,
    };

    public static string ToSpanish(this PermissionLevel p) => p switch
    {
        PermissionLevel.Visitor => "Visitante",
        PermissionLevel.Member => "Miembro",
        PermissionLevel.Operator => "Operador",
        _ => p.ToString(),
    };
}

public sealed record AllowListEntry(string Name, string? Xuid, bool IgnoresPlayerLimit = false);

/// <summary>
/// <c>allowlist.json</c> de BDS: <c>[{"ignoresPlayerLimit":false,"name":"Steve","xuid":"2535…"}]</c>.
/// Se identifica por XUID (los nombres con sufijo como «Alucard#9583» dan problemas). Las entradas antiguas solo
/// con nombre se conservan.
/// </summary>
public sealed class AllowListDocument
{
    private readonly List<AllowListEntry> _entries;

    private AllowListDocument(List<AllowListEntry> entries)
    {
        _entries = entries;
    }

    public IReadOnlyList<AllowListEntry> Entries => _entries;

    public static AllowListDocument Empty() => new([]);

    public static AllowListDocument Parse(string json)
    {
        var entries = new List<AllowListEntry>();
        if (string.IsNullOrWhiteSpace(json))
        {
            return new AllowListDocument(entries);
        }
        if (JsonNode.Parse(json) is not JsonArray array)
        {
            throw new JsonException("allowlist.json debe ser una lista.");
        }
        foreach (var node in array.OfType<JsonObject>())
        {
            var name = node["name"]?.GetValue<string>() ?? string.Empty;
            var xuid = node["xuid"]?.GetValue<string>();
            var ignores = node["ignoresPlayerLimit"]?.GetValue<bool>() ?? false;
            if (name.Length == 0 && string.IsNullOrEmpty(xuid))
            {
                continue;
            }
            entries.Add(new AllowListEntry(name, string.IsNullOrWhiteSpace(xuid) ? null : xuid, ignores));
        }
        return new AllowListDocument(entries);
    }

    public bool Contains(string xuid) => _entries.Any(e => e.Xuid == xuid);

    /// <summary>Añade o actualiza (por XUID) un jugador. Devuelve true si cambia algo.</summary>
    public bool Add(string name, string xuid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(xuid);
        var index = _entries.FindIndex(e => e.Xuid == xuid);
        if (index >= 0)
        {
            if (_entries[index].Name == name)
            {
                return false;
            }
            _entries[index] = _entries[index] with { Name = name };
            return true;
        }
        // Si había una entrada antigua solo con ese nombre, se sustituye por la de XUID.
        _entries.RemoveAll(e => e.Xuid is null && string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
        _entries.Add(new AllowListEntry(name, xuid));
        return true;
    }

    public bool Remove(string xuid) => _entries.RemoveAll(e => e.Xuid == xuid) > 0;

    public string ToJson()
    {
        var array = new JsonArray();
        foreach (var e in _entries)
        {
            var obj = new JsonObject { ["ignoresPlayerLimit"] = e.IgnoresPlayerLimit, ["name"] = e.Name };
            if (e.Xuid is not null)
            {
                obj["xuid"] = e.Xuid;
            }
            array.Add(obj);
        }
        return array.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";
    }
}

/// <summary><c>permissions.json</c> de BDS: <c>[{"permission":"operator","xuid":"2535…"}]</c>.</summary>
public sealed class PermissionsDocument
{
    private readonly Dictionary<string, PermissionLevel> _byXuid;

    private PermissionsDocument(Dictionary<string, PermissionLevel> byXuid)
    {
        _byXuid = byXuid;
    }

    public IReadOnlyDictionary<string, PermissionLevel> ByXuid => _byXuid;

    public static PermissionsDocument Empty() => new(new Dictionary<string, PermissionLevel>(StringComparer.Ordinal));

    public static PermissionsDocument Parse(string json)
    {
        var map = new Dictionary<string, PermissionLevel>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(json))
        {
            return new PermissionsDocument(map);
        }
        if (JsonNode.Parse(json) is not JsonArray array)
        {
            throw new JsonException("permissions.json debe ser una lista.");
        }
        foreach (var node in array.OfType<JsonObject>())
        {
            var xuid = node["xuid"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(xuid) && PermissionLevelText.FromBds(node["permission"]?.GetValue<string>()) is { } level)
            {
                map[xuid] = level;
            }
        }
        return new PermissionsDocument(map);
    }

    public PermissionLevel? Get(string xuid) => _byXuid.TryGetValue(xuid, out var p) ? p : null;

    /// <summary>Fija el permiso; null lo quita (el jugador recibe el permiso por defecto del servidor).</summary>
    public bool Set(string xuid, PermissionLevel? level)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(xuid);
        if (level is null)
        {
            return _byXuid.Remove(xuid);
        }
        if (_byXuid.TryGetValue(xuid, out var current) && current == level)
        {
            return false;
        }
        _byXuid[xuid] = level.Value;
        return true;
    }

    public string ToJson()
    {
        var array = new JsonArray();
        foreach (var (xuid, level) in _byXuid.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            array.Add(new JsonObject { ["permission"] = level.ToBds(), ["xuid"] = xuid });
        }
        return array.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";
    }
}
