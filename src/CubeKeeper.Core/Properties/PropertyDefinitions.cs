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
    public const string AntiCheat = "Anti-trampas";
    public const string Performance = "Rendimiento";
    public const string Diagnostics = "Registros y desarrollo";
    public const string Other = "Otros";

    public static readonly IReadOnlyList<string> CategoryOrder =
        [General, Gameplay, Players, World, Network, AntiCheat, Performance, Diagnostics, Other];

    public static readonly IReadOnlyList<PropertyDefinition> All =
    [
        // General
        new("server-name", "Nombre del servidor", General, PropertyKind.Text,
            "Nombre que se ve en la lista de servidores y en «Partidas LAN». No puede llevar punto y coma.", MaxLength: 100),

        // Juego
        new("gamemode", "Modo de juego", Gameplay, PropertyKind.Choice,
            "Modo para los jugadores que entran por primera vez: supervivencia, creativo o aventura.", Choices: ["survival", "creative", "adventure"]),
        new("force-gamemode", "Forzar modo de juego", Gameplay, PropertyKind.Boolean,
            "Si está activado, todos entran en el modo de juego del servidor aunque tuvieran otro guardado en el mundo."),
        new("difficulty", "Dificultad", Gameplay, PropertyKind.Choice,
            "Pacífico (sin monstruos), fácil, normal o difícil.", Choices: ["peaceful", "easy", "normal", "hard"]),
        new("allow-cheats", "Permitir trucos", Gameplay, PropertyKind.Boolean,
            "Permite usar comandos como /give, /tp o /time dentro del juego (a quien tenga permiso de operador)."),
        new("disable-player-interaction", "Desactivar interacción entre jugadores", Gameplay, PropertyKind.Boolean,
            "Los jugadores no pueden interactuar entre sí: por ejemplo, no pueden pegarse ni intercambiar objetos."),

        // Jugadores
        new("max-players", "Jugadores máximos", Players, PropertyKind.Integer,
            "Número máximo de jugadores conectados a la vez.", Min: 1, Max: 100),
        new("online-mode", "Modo en línea", Players, PropertyKind.Boolean,
            "Exige iniciar sesión con una cuenta Microsoft. Necesario para identificar a los jugadores por XUID.",
            Warning: "Con false los XUID no son fiables y cualquiera puede suplantar un nombre."),
        new("allow-list", "Lista de permitidos", Players, PropertyKind.Boolean,
            "Si está activado, solo pueden entrar los jugadores de allowlist.json.",
            Warning: "La lista de permitidos la gestionará CubeKeeper en la fase 2; de momento se recomienda false."),
        new("default-player-permission-level", "Permiso por defecto", Players, PropertyKind.Choice,
            "Permiso de quien entra por primera vez: visitante (solo mirar), miembro (jugar normal) u operador (también comandos).",
            Choices: ["visitor", "member", "operator"]),
        new("player-idle-timeout", "Expulsar por inactividad (minutos)", Players, PropertyKind.Integer,
            "Expulsa a quien lleve este tiempo sin moverse. 0 = nunca.", Min: 0, Max: 1440),
        new("chat-restriction", "Restricción del chat", Players, PropertyKind.Choice,
            "None: chat normal. Dropped: los mensajes no se envían a nadie, pero el jugador no lo sabe. Disabled: el chat está desactivado y se avisa.",
            Choices: ["None", "Dropped", "Disabled"]),
        new("disable-persona", "Desactivar aspectos del creador de personajes", Players, PropertyKind.Boolean,
            "Si está activado, no se pueden usar los aspectos hechos con el creador de personajes."),
        new("disable-custom-skins", "Desactivar aspectos personalizados", Players, PropertyKind.Boolean,
            "Si está activado, no se pueden usar aspectos personalizados (los creados fuera de Minecraft o del Marketplace)."),

        // Mundo
        new("level-name", "Nombre del mundo", World, PropertyKind.Text,
            "Carpeta del mundo dentro de worlds/. Si no existe, se crea un mundo nuevo con ese nombre.", MaxLength: 100),
        new("level-seed", "Semilla", World, PropertyKind.Text,
            "Semilla para generar el mundo. Solo se usa al crear uno nuevo. Vacía = aleatoria.", AllowEmpty: true, MaxLength: 100),
        new("view-distance", "Distancia de visión (chunks)", World, PropertyKind.Integer,
            "Distancia máxima que pueden ver los jugadores, en chunks (bloques de 16×16). Más distancia, más carga para el servidor.", Min: 5, Max: 96),
        new("tick-distance", "Distancia de simulación (chunks)", World, PropertyKind.Integer,
            "Hasta qué distancia de los jugadores el mundo sigue «vivo» (crecen cultivos, se mueven criaturas…). Entre 4 y 12.", Min: 4, Max: 12),
        new("texturepack-required", "Exigir paquetes de recursos", World, PropertyKind.Boolean,
            "Obliga a los jugadores a descargar los paquetes de recursos del mundo para poder entrar."),
        new("client-side-chunk-generation-enabled", "Generación de chunks en el cliente", World, PropertyKind.Boolean,
            "Permite que los dispositivos de los jugadores generen el terreno lejano visible, para aligerar el servidor."),
        new("block-network-ids-are-hashes", "Identificadores de bloque por hash", World, PropertyKind.Boolean,
            "Envía los bloques a los clientes con identificadores calculados en lugar de números consecutivos. Déjalo como venga."),
        new("server-build-radius-ratio", "Proporción de radio de construcción", World, PropertyKind.Text,
            "Parte de la zona visible que prepara el servidor en lugar del cliente: «Disabled» (lo decide el servidor) o un número entre 0.0 y 1.0.",
            MaxLength: 20),
        new("disable-client-vibrant-visuals", "Desactivar «Vibrant Visuals» en los clientes", World, PropertyKind.Boolean,
            "Impide que los jugadores usen el modo gráfico Vibrant Visuals en este servidor."),

        // Red
        new("transport", "Transporte", Network, PropertyKind.Choice,
            "Protocolo de red. nethernet es el único que funciona en 1.26.x.", Choices: ["nethernet", "raknet"],
            Warning: "Con raknet el servidor arranca pero nadie puede conectar."),
        new("server-port", "Puerto TCP (negociación)", Network, PropertyKind.Integer,
            "Puerto al que se conectan los jugadores (con NetherNet, la negociación inicial por TCP). Por defecto 19132.", Min: 1, Max: 65535),
        new("server-portv6", "Puerto IPv6", Network, PropertyKind.Integer,
            "Puerto para conexiones por IPv6. Por defecto 19133.", Min: 1, Max: 65535),
        new("server-udp-ports", "Puertos UDP del juego", Network, PropertyKind.Text,
            "Puertos UDP por los que va el tráfico del juego con NetherNet, por ejemplo 19140. No puede coincidir con el puerto TCP.", MaxLength: 100),
        new("enable-lan-visibility", "Visible en «Partidas LAN»", Network, PropertyKind.Boolean,
            "Anuncia el servidor en la red de casa para que aparezca en la pestaña «Amigos → Partidas LAN»."),
        new("server-ip", "IP de escucha", Network, PropertyKind.Text,
            "IP en la que escucha el servidor. Vacío = todas las interfaces.", AllowEmpty: true, MaxLength: 64,
            Warning: "Con la IP del NAS solo escucha ahí y la comprobación de salud local falla. Déjalo vacío."),
        new("compression-threshold", "Umbral de compresión (bytes)", Network, PropertyKind.Integer,
            "Tamaño mínimo de los paquetes de red que se comprimen. 0 = todos, 65535 = ninguno.", Min: 0, Max: 65535),
        new("compression-algorithm", "Algoritmo de compresión", Network, PropertyKind.Choice,
            "Algoritmo para comprimir el tráfico: zlib (por defecto) o snappy (más rápido, comprime menos).", Choices: ["zlib", "snappy"]),

        // Anti-trampas: movimiento autoritativo del servidor
        new("server-authoritative-movement-strict", "Movimiento: corrección estricta", AntiCheat, PropertyKind.Boolean,
            "Corrige con más rigor la posición de los jugadores cuando el servidor no está de acuerdo con su movimiento. Ayuda contra trampas, pero con mala conexión puede dar tirones."),
        new("server-authoritative-dismount-strict", "Desmontar: corrección estricta", AntiCheat, PropertyKind.Boolean,
            "Aplica el mismo control estricto a la posición al bajarse de un caballo, barco o vagoneta."),
        new("server-authoritative-entity-interactions-strict", "Interacciones con criaturas: control estricto", AntiCheat, PropertyKind.Boolean,
            "El servidor comprueba con más rigor las interacciones con criaturas (por ejemplo, la distancia al golpear)."),
        new("server-authoritative-movement", "Movimiento autoritativo", AntiCheat, PropertyKind.Choice,
            "Quién decide la posición de los jugadores: el cliente, el servidor, o el servidor con rebobinado (lo recomendado). Solo existe en versiones antiguas.",
            Choices: ["client-auth", "server-auth", "server-auth-with-rewind"]),
        new("correct-player-movement", "Corregir el movimiento de los jugadores", AntiCheat, PropertyKind.Boolean,
            "Si el movimiento del jugador supera los umbrales, el servidor lo devuelve a su posición. Versiones antiguas."),
        new("player-position-acceptance-threshold", "Tolerancia de posición", AntiCheat, PropertyKind.Text,
            "Diferencia de posición entre cliente y servidor que se tolera antes de corregir al jugador. Más alto = menos correcciones.", MaxLength: 20),
        new("player-movement-action-direction-threshold", "Tolerancia de dirección al actuar", AntiCheat, PropertyKind.Text,
            "Cuánto puede diferir la dirección en la que mira el jugador al golpear o usar algo de lo que espera el servidor (entre 0 y 1).", MaxLength: 20),
        new("player-movement-score-threshold", "Umbral de anomalías de movimiento", AntiCheat, PropertyKind.Integer,
            "Número de movimientos sospechosos seguidos antes de corregir al jugador. Versiones antiguas.", Min: 0, Max: 10000),
        new("player-movement-distance-threshold", "Umbral de distancia de movimiento", AntiCheat, PropertyKind.Text,
            "Diferencia de distancia entre cliente y servidor que cuenta como movimiento sospechoso. Versiones antiguas.", MaxLength: 20),
        new("player-movement-duration-threshold-in-ms", "Duración de la discrepancia (ms)", AntiCheat, PropertyKind.Integer,
            "Tiempo que puede durar la diferencia de posición antes de contar como sospechosa. Versiones antiguas.", Min: 0, Max: 100000),
        new("server-authoritative-block-breaking", "Romper bloques: control del servidor", AntiCheat, PropertyKind.Boolean,
            "El servidor comprueba que romper bloques sea posible (tiempo, herramienta, distancia). Versiones antiguas."),
        new("server-authoritative-block-breaking-pick-range-scalar", "Alcance al romper bloques", AntiCheat, PropertyKind.Text,
            "Multiplicador de la distancia máxima a la que se puede romper un bloque. 1.5 es el valor por defecto.", MaxLength: 20),

        // Rendimiento
        new("max-threads", "Hilos máximos", Performance, PropertyKind.Integer,
            "Hilos de CPU que puede usar el servidor. 0 = todos los disponibles (el límite real lo pone el contenedor).", Min: 0, Max: 64),

        // Registros y desarrollo
        new("content-log-file-enabled", "Guardar errores de contenido", Diagnostics, PropertyKind.Boolean,
            "Guarda en un archivo los errores de los complementos y paquetes (útil para depurar complementos)."),
        new("content-log-level", "Nivel de los errores de contenido", Diagnostics, PropertyKind.Choice,
            "Gravedad mínima de los mensajes de complementos que se registran.", Choices: ["verbose", "info", "warning", "error"]),
        new("content-log-console-output-enabled", "Errores de contenido en la consola", Diagnostics, PropertyKind.Boolean,
            "Muestra también en la consola los errores de los complementos."),
        new("emit-server-telemetry", "Enviar telemetría a Mojang", Diagnostics, PropertyKind.Boolean,
            "Envía a Mojang datos técnicos anónimos sobre el funcionamiento del servidor."),
        new("allow-outbound-script-debugging", "Depuración de scripts (saliente)", Diagnostics, PropertyKind.Boolean,
            "Permite que los scripts de los complementos se conecten a un depurador externo con el comando /script debugger connect. Solo para desarrollar complementos."),
        new("allow-inbound-script-debugging", "Depuración de scripts (entrante)", Diagnostics, PropertyKind.Boolean,
            "Permite que un depurador externo se conecte al servidor con /script debugger listen. Solo para desarrollar complementos."),
        new("force-inbound-debug-port", "Puerto fijo del depurador", Diagnostics, PropertyKind.Integer,
            "Puerto en el que escucha el depurador de scripts entrante.", Min: 1, Max: 65535),
        new("script-debugger-auto-attach", "Conectar el depurador al arrancar", Diagnostics, PropertyKind.Choice,
            "Conecta el depurador de scripts al cargar el mundo: disabled (no), connect (se conecta a un depurador) o listen (espera a uno).",
            Choices: ["disabled", "connect", "listen"]),
        new("script-debugger-auto-attach-connect-address", "Dirección del depurador", Diagnostics, PropertyKind.Text,
            "Dirección (host:puerto) a la que se conecta el depurador si «Conectar el depurador al arrancar» es connect.", AllowEmpty: true, MaxLength: 100),
        new("script-watchdog-enable", "Vigilante de scripts", Diagnostics, PropertyKind.Boolean,
            "Vigila los scripts de los complementos y avisa si tardan demasiado."),
        new("script-watchdog-enable-exception-handling", "Vigilante: gestionar excepciones", Diagnostics, PropertyKind.Boolean,
            "El vigilante captura las excepciones de los scripts en lugar de detener el servidor."),
        new("script-watchdog-enable-shutdown", "Vigilante: parar el servidor", Diagnostics, PropertyKind.Boolean,
            "El vigilante puede parar el servidor si un script se queda bloqueado."),
        new("script-watchdog-hang-exception", "Vigilante: excepción por bloqueo", Diagnostics, PropertyKind.Boolean,
            "Lanza una excepción en el script cuando se queda bloqueado."),
        new("script-watchdog-hang-threshold", "Vigilante: umbral de bloqueo (ms)", Diagnostics, PropertyKind.Integer,
            "Milisegundos que puede tardar un script antes de considerarlo bloqueado.", Min: 0, Max: 600000),
        new("script-watchdog-spike-threshold", "Vigilante: umbral de pico (ms)", Diagnostics, PropertyKind.Integer,
            "Milisegundos de un solo tick de scripts a partir de los cuales se avisa de un pico.", Min: 0, Max: 600000),
        new("script-watchdog-slow-threshold", "Vigilante: umbral de lentitud (ms)", Diagnostics, PropertyKind.Integer,
            "Tiempo medio de los scripts por tick a partir del cual se avisa de que van lentos.", Min: 0, Max: 600000),
        new("script-watchdog-memory-warning", "Vigilante: aviso de memoria (MB)", Diagnostics, PropertyKind.Integer,
            "Memoria de los scripts a partir de la cual se avisa. 0 = sin aviso.", Min: 0, Max: 65536),
        new("script-watchdog-memory-limit", "Vigilante: límite de memoria (MB)", Diagnostics, PropertyKind.Integer,
            "Memoria máxima de los scripts; al superarla se detiene el mundo. 0 = sin límite.", Min: 0, Max: 65536),
        new("diagnostics-capture-auto-start", "Capturar diagnósticos al arrancar", Diagnostics, PropertyKind.Boolean,
            "Empieza a recoger datos de rendimiento nada más arrancar el servidor."),
        new("diagnostics-capture-max-files", "Diagnósticos: archivos máximos", Diagnostics, PropertyKind.Integer,
            "Número máximo de archivos de diagnóstico que se guardan.", Min: 0, Max: 10000),
        new("diagnostics-capture-max-file-size", "Diagnósticos: tamaño máximo (bytes)", Diagnostics, PropertyKind.Integer,
            "Tamaño máximo de cada archivo de diagnóstico.", Min: 0, Max: int.MaxValue),
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
