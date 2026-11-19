using System.Globalization;

namespace CubeKeeper.Core.Properties;

public enum PropertyKind
{
    Text,
    Boolean,
    Integer,
    Choice,
}

/// <summary>Descripción de un ajuste conocido de server.properties para mostrarlo y validarlo en el editor.</summary>
public sealed record PropertyDefinition(
    string Key,
    string Label,
    string Category,
    PropertyKind Kind,
    string? Help = null,
    int? Min = null,
    int? Max = null,
    IReadOnlyList<string>? Choices = null,
    bool AllowEmpty = false,
    int? MaxLength = null,
    string? Warning = null)
{
    /// <summary>Devuelve un mensaje de error en español, o null si el valor es válido.</summary>
    public string? Validate(string? value)
    {
        value ??= string.Empty;
        if (value.Contains('\n') || value.Contains('\r'))
        {
            return "No puede contener saltos de línea.";
        }
        if (value.Length == 0)
        {
            return AllowEmpty ? null : "No puede estar vacío.";
        }
        if (MaxLength is int max && value.Length > max)
        {
            return $"Como mucho {max} caracteres.";
        }

        switch (Kind)
        {
            case PropertyKind.Boolean:
                return value is "true" or "false" ? null : "Debe ser true o false.";
            case PropertyKind.Integer:
                if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                {
                    return "Debe ser un número entero.";
                }
                if (Min is int lo && n < lo)
                {
                    return $"Debe ser como mínimo {lo}.";
                }
                if (Max is int hi && n > hi)
                {
                    return $"Debe ser como máximo {hi}.";
                }
                return null;
            case PropertyKind.Choice:
                return Choices is null || Choices.Contains(value, StringComparer.Ordinal)
                    ? null
                    : $"Valores posibles: {string.Join(", ", Choices)}.";
            default:
                return null;
        }
    }
}

/// <summary>
/// Ajustes conocidos de BDS 1.26. Los que no estén aquí se editan como texto libre.
/// BDS solo lee server.properties al arrancar: cualquier cambio necesita reiniciar el servidor.
/// </summary>
public static class PropertyDefinitions
{
    public const string General = "General";
    public const string Gameplay = "Juego";
    public const string Players = "Jugadores";
    public const string Network = "Red";
    public const string World = "Mundo";
    public const string Performance = "Rendimiento";
    public const string Other = "Otros";

    public static readonly IReadOnlyList<string> CategoryOrder = [General, Gameplay, Players, World, Network, Performance, Other];

    public static readonly IReadOnlyList<PropertyDefinition> All =
    [
        new("server-name", "Nombre del servidor", General, PropertyKind.Text, "Nombre que se ve en la lista de servidores.", MaxLength: 100),
        new("gamemode", "Modo de juego", Gameplay, PropertyKind.Choice, "Modo para los jugadores nuevos.", Choices: ["survival", "creative", "adventure"]),
        new("force-gamemode", "Forzar modo de juego", Gameplay, PropertyKind.Boolean, "Aplica el modo de juego también a quien ya tenía otro guardado."),
        new("difficulty", "Dificultad", Gameplay, PropertyKind.Choice, Choices: ["peaceful", "easy", "normal", "hard"]),
        new("allow-cheats", "Permitir trucos", Gameplay, PropertyKind.Boolean, "Permite comandos como /give o /tp a los operadores dentro del juego."),
        new("max-players", "Jugadores máximos", Players, PropertyKind.Integer, Min: 1, Max: 100),
        new("online-mode", "Modo en línea", Players, PropertyKind.Boolean, "Exige cuenta Microsoft. Necesario para identificar a los jugadores por XUID.",
            Warning: "Con false los XUID no son fiables y cualquiera puede suplantar un nombre."),
        new("allow-list", "Lista de permitidos", Players, PropertyKind.Boolean, "Solo pueden entrar los de allowlist.json.",
            Warning: "La lista de permitidos la gestionará CubeKeeper en la fase 2; de momento se recomienda false."),
        new("default-player-permission-level", "Permiso por defecto", Players, PropertyKind.Choice,
            "Permiso de quien entra por primera vez: visitante (solo mirar), miembro (jugar) u operador.", Choices: ["visitor", "member", "operator"]),
        new("player-idle-timeout", "Expulsar por inactividad (minutos)", Players, PropertyKind.Integer, "0 = nunca.", Min: 0, Max: 1440),
        new("chat-restriction", "Restricción del chat", Players, PropertyKind.Choice, Choices: ["None", "Dropped", "Disabled"]),
        new("level-name", "Nombre del mundo", World, PropertyKind.Text, "Carpeta dentro de worlds/. Si no existe, se crea un mundo nuevo.", MaxLength: 100),
        new("level-seed", "Semilla", World, PropertyKind.Text, "Solo se usa al crear un mundo nuevo. Vacía = aleatoria.", AllowEmpty: true, MaxLength: 100),
        new("view-distance", "Distancia de visión (chunks)", World, PropertyKind.Integer, Min: 5, Max: 96),
        new("tick-distance", "Distancia de simulación (chunks)", World, PropertyKind.Integer, Min: 4, Max: 12),
        new("texturepack-required", "Exigir paquetes de texturas", World, PropertyKind.Boolean),
        new("transport", "Transporte", Network, PropertyKind.Choice, "nethernet es el único que funciona en 1.26.x.", Choices: ["nethernet", "raknet"],
            Warning: "Con raknet el servidor arranca pero nadie puede conectar."),
        new("server-port", "Puerto TCP (negociación)", Network, PropertyKind.Integer, Min: 1, Max: 65535),
        new("server-portv6", "Puerto IPv6", Network, PropertyKind.Integer, Min: 1, Max: 65535),
        new("server-udp-ports", "Puertos UDP del juego", Network, PropertyKind.Text, "Por ejemplo 19140. No puede coincidir con el puerto TCP.", MaxLength: 100),
        new("enable-lan-visibility", "Visible en «Partidas LAN»", Network, PropertyKind.Boolean),
        new("server-ip", "IP de escucha", Network, PropertyKind.Text, "Vacío = todas las interfaces.", AllowEmpty: true, MaxLength: 64,
            Warning: "Con la IP del NAS solo escucha ahí y la comprobación de salud local falla. Déjalo vacío."),
        new("max-threads", "Hilos máximos", Performance, PropertyKind.Integer, "0 = todos los disponibles.", Min: 0, Max: 64),
        new("compression-threshold", "Umbral de compresión (bytes)", Performance, PropertyKind.Integer, Min: 0, Max: 65535),
        new("compression-algorithm", "Algoritmo de compresión", Performance, PropertyKind.Choice, Choices: ["zlib", "snappy"]),
        new("content-log-file-enabled", "Registro de errores de contenido", Other, PropertyKind.Boolean),
    ];

    private static readonly Dictionary<string, PropertyDefinition> ByKey = All.ToDictionary(d => d.Key, StringComparer.Ordinal);

    public static PropertyDefinition? Find(string key) => ByKey.GetValueOrDefault(key);

    /// <summary>Definición para cualquier clave: la conocida o una de texto libre en "Otros".</summary>
    public static PropertyDefinition For(string key) =>
        Find(key) ?? new PropertyDefinition(key, key, Other, PropertyKind.Text, AllowEmpty: true, MaxLength: 1000);

    /// <summary>Comprobaciones que dependen de varias claves a la vez.</summary>
    public static IEnumerable<(string Key, string Error)> ValidateTogether(IReadOnlyDictionary<string, string> values)
    {
        if (values.TryGetValue("server-port", out var tcp) && values.TryGetValue("server-udp-ports", out var udp) &&
            udp.Split([',', ':', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Contains(tcp.Trim()))
        {
            yield return ("server-udp-ports", "No puede coincidir con el puerto TCP: chocan.");
        }
    }
}
