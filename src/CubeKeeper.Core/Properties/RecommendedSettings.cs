namespace CubeKeeper.Core.Properties;

/// <summary>Valor recomendado para una clave de server.properties y el motivo.</summary>
/// <param name="OnlyIfPresent">Si es true, solo se corrige cuando la clave ya existe (no se añade).</param>
public sealed record RecommendedSetting(string Key, string Value, string Reason, bool OnlyIfPresent = false);

/// <summary>Una clave cuyo valor actual no coincide con el recomendado.</summary>
public sealed record SettingDeviation(RecommendedSetting Setting, string? CurrentValue);

/// <summary>
/// Configuración probada en el NAS con BDS 1.26.x (lecciones del contenedor itzg).
/// <see cref="Required"/> se revisa siempre y se avisa en el panel si no coincide;
/// <see cref="InitialDefaults"/> solo se aplica al crear un server.properties nuevo.
/// </summary>
public static class RecommendedSettings
{
    public static readonly IReadOnlyList<RecommendedSetting> Required =
    [
        new("transport", "nethernet",
            "NetherNet es el único transporte soportado en 1.26.x; con raknet arranca pero nadie puede conectar."),
        new("server-port", "19132", "Puerto TCP de la negociación de NetherNet (y el que vigila /health)."),
        new("server-udp-ports", "19140",
            "Puerto UDP del juego. Si coincide con server-port, chocan."),
        new("enable-lan-visibility", "true", "Para que aparezca en «Partidas LAN» (UDP 7551)."),
        new("online-mode", "true", "Cuenta Microsoft obligatoria: los XUID son fiables (la lista de permitidos depende de ello)."),
        new("server-ip", "",
            "Con la IP del NAS el servidor solo escucha en esa IP y la conexión local falla; vacío escucha en todas.",
            OnlyIfPresent: true),
    ];

    public static readonly IReadOnlyList<RecommendedSetting> InitialDefaults =
    [
        new("level-name", "Bedrock level", "Nombre del mundo."),
        new("gamemode", "survival", "Modo de juego."),
        new("difficulty", "normal", "Dificultad."),
    ];

    public static IReadOnlyList<SettingDeviation> Check(ServerPropertiesDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        var result = new List<SettingDeviation>();
        foreach (var s in Required)
        {
            var current = doc.Get(s.Key);
            if (s.OnlyIfPresent && current is null)
            {
                continue;
            }
            if (!string.Equals(current?.Trim(), s.Value, StringComparison.OrdinalIgnoreCase))
            {
                result.Add(new SettingDeviation(s, current));
            }
        }
        return result;
    }

    /// <summary>Aplica los valores recomendados. Devuelve las claves cambiadas.</summary>
    public static IReadOnlyList<string> Apply(ServerPropertiesDocument doc, bool includeInitialDefaults = false)
    {
        ArgumentNullException.ThrowIfNull(doc);
        var changed = new List<string>();
        var settings = includeInitialDefaults ? Required.Concat(InitialDefaults) : Required;
        foreach (var s in settings)
        {
            if (s.OnlyIfPresent && !doc.ContainsKey(s.Key))
            {
                continue;
            }
            if (!string.Equals(doc.Get(s.Key)?.Trim(), s.Value, StringComparison.OrdinalIgnoreCase) && doc.Set(s.Key, s.Value))
            {
                changed.Add(s.Key);
            }
        }
        return changed;
    }
}
