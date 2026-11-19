using System.Globalization;
using System.Text.RegularExpressions;

namespace CubeKeeper.Core.GameRules;

public sealed record GameRuleDefinition(string Name, string Label, string Help, bool IsNumber = false, int Min = 0, int Max = int.MaxValue);

/// <summary>Reglas del juego (<c>gamerule</c>) que se muestran como interruptores en el panel.</summary>
public static partial class GameRuleCatalog
{
    public static readonly IReadOnlyList<GameRuleDefinition> All =
    [
        new("keepInventory", "Conservar el inventario", "Al morir no se pierden los objetos ni la experiencia."),
        new("doDaylightCycle", "Ciclo de día y noche", "Si se desactiva, la hora se queda fija."),
        new("doWeatherCycle", "Cambios de tiempo", "Si se desactiva, el tiempo (lluvia, tormenta) se queda fijo."),
        new("doMobSpawning", "Aparición de criaturas", "Las criaturas aparecen solas por el mundo."),
        new("mobGriefing", "Destrozos de criaturas", "Los creepers rompen bloques, los endermans se los llevan…"),
        new("doFireTick", "Propagación del fuego", "El fuego se extiende y se apaga solo."),
        new("tntExplodes", "La dinamita explota", "Si se desactiva, la dinamita no explota."),
        new("pvp", "Combate entre jugadores", "Los jugadores pueden hacerse daño entre sí."),
        new("naturalRegeneration", "Regeneración natural", "La salud se recupera sola con la barra de hambre llena."),
        new("doImmediateRespawn", "Reaparecer al instante", "Sin pantalla de muerte: se reaparece directamente."),
        new("showCoordinates", "Mostrar coordenadas", "Muestra la posición en pantalla a todos los jugadores."),
        new("showDaysPlayed", "Mostrar días jugados", "Muestra cuántos días lleva el mundo."),
        new("doInsomnia", "Fantasmas", "Aparecen fantasmas si alguien lleva varios días sin dormir."),
        new("fallDamage", "Daño por caída", "Caer desde altura hace daño."),
        new("fireDamage", "Daño por fuego", "El fuego y la lava hacen daño."),
        new("drowningDamage", "Daño por ahogamiento", "Quedarse sin aire bajo el agua hace daño."),
        new("freezeDamage", "Daño por congelación", "La nieve en polvo congela y hace daño."),
        new("doEntityDrops", "Objetos de entidades", "Las vagonetas, barcos, cuadros… sueltan su objeto al romperse."),
        new("doTileDrops", "Objetos de bloques", "Los bloques sueltan su objeto al romperse."),
        new("doMobLoot", "Botín de criaturas", "Las criaturas sueltan objetos al morir."),
        new("showDeathMessages", "Mensajes de muerte", "Avisa en el chat cuando muere un jugador."),
        new("sendCommandFeedback", "Respuesta de comandos", "Muestra en el chat el resultado de los comandos."),
        new("commandBlocksEnabled", "Bloques de comandos", "Los bloques de comandos funcionan."),
        new("respawnBlocksExplode", "Explotan anclas y camas", "Las anclas de reaparición y las camas explotan en dimensiones donde no funcionan."),
        new("projectilesCanBreakBlocks", "Los proyectiles rompen bloques", "Los proyectiles pueden romper algunos bloques, como las vasijas decoradas."),
        new("playersSleepingPercentage", "Porcentaje para pasar la noche", "Porcentaje de jugadores que deben dormir para saltar la noche (0 = basta uno).", IsNumber: true, Min: 0, Max: 100),
        new("spawnRadius", "Radio de aparición", "Distancia alrededor del punto de aparición en la que aparecen los jugadores.", IsNumber: true, Min: 0, Max: 128),
        new("randomTickSpeed", "Velocidad de crecimiento", "Rapidez con que crecen las plantas y se propaga la hierba. Por defecto 1.", IsNumber: true, Min: 0, Max: 4096),
    ];

    private static readonly Dictionary<string, GameRuleDefinition> ByName = All.ToDictionary(r => r.Name, StringComparer.OrdinalIgnoreCase);

    public static GameRuleDefinition? Find(string name) => ByName.GetValueOrDefault(name);

    [GeneratedRegex(@"(?<name>[A-Za-z]+)\s*=\s*(?<value>true|false|-?\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex PairRegex();

    /// <summary>
    /// Saca los valores «nombre = valor» de la respuesta de <c>gamerule</c>
    /// (por ejemplo <c>commandblockoutput = true, dodaylightcycle = true, …</c>). Las claves se devuelven con el nombre
    /// canónico de <see cref="All"/> si se conoce.
    /// </summary>
    public static IReadOnlyDictionary<string, string> ParseValues(IEnumerable<string> lines)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines)
        {
            foreach (Match m in PairRegex().Matches(line))
            {
                var name = m.Groups["name"].Value;
                result[Find(name)?.Name ?? name] = m.Groups["value"].Value.ToLowerInvariant();
            }
        }
        return result;
    }

    /// <summary>Comando para cambiar una regla, validando nombre y valor.</summary>
    public static string SetCommand(string name, string value)
    {
        var def = Find(name) ?? throw new ArgumentException($"Regla desconocida: {name}.", nameof(name));
        if (def.IsNumber)
        {
            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) || n < def.Min || n > def.Max)
            {
                throw new ArgumentException($"{def.Label}: debe ser un número entre {def.Min} y {def.Max}.", nameof(value));
            }
            return $"gamerule {def.Name} {n.ToString(CultureInfo.InvariantCulture)}";
        }
        return value is "true" or "false"
            ? $"gamerule {def.Name} {value}"
            : throw new ArgumentException("Debe ser true o false.", nameof(value));
    }
}
