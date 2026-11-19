// Imitación mínima de bedrock_server para desarrollo y pruebas de integración.
// Escribe líneas con el mismo formato que BDS, abre el puerto TCP de server.properties (como NetherNet)
// y responde a algunos comandos por stdin.
//
// Comandos: stop, list, say <texto>, join <nombre> <xuid>, leave <nombre> <xuid>, crash, hang,
//           save hold | save query | save resume (la primera "save query" responde que aún no está listo),
//           kick "nombre" motivo, allowlist on|off|reload, permission reload, op|deop "nombre",
//           gamerule [regla valor], time set X, weather X.
// Con la lista de permitidos activa, "join" de alguien que no está en allowlist.json no deja rastro (como BDS).
// Variables: FAKE_BDS_CRASH_ON_START=1 (se cae nada más arrancar), FAKE_BDS_START_DELAY_MS.

using System.Globalization;
using System.Net;
using System.Net.Sockets;

const string Version = "1.26.0.2";
var players = new Dictionary<string, string>(StringComparer.Ordinal);

static void Log(string message, string level = "INFO") =>
    Console.WriteLine($"[{DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss:fff", CultureInfo.InvariantCulture)} {level}] {message}");

var port = 19132;
var maxPlayers = 10;
var levelName = "Bedrock level";
var allowListOn = false;
var rules = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
{
    ["commandBlockOutput"] = "true", ["doDaylightCycle"] = "true", ["doEntityDrops"] = "true", ["doFireTick"] = "true",
    ["doInsomnia"] = "true", ["doImmediateRespawn"] = "false", ["doMobLoot"] = "true", ["doMobSpawning"] = "true",
    ["doTileDrops"] = "true", ["doWeatherCycle"] = "true", ["drowningDamage"] = "true", ["fallDamage"] = "true",
    ["fireDamage"] = "true", ["keepInventory"] = "false", ["mobGriefing"] = "true", ["naturalRegeneration"] = "true",
    ["pvp"] = "true", ["randomTickSpeed"] = "1", ["showCoordinates"] = "false", ["tntExplodes"] = "true",
};
bool InAllowList(string xuid) =>
    File.Exists("allowlist.json") && File.ReadAllText("allowlist.json").Contains($"\"{xuid}\"", StringComparison.Ordinal);
static string Unquote(string s) => s.Trim().Trim('"');
if (File.Exists("server.properties"))
{
    foreach (var raw in File.ReadAllLines("server.properties"))
    {
        var line = raw.Trim();
        if (line.StartsWith('#') || !line.Contains('='))
        {
            continue;
        }
        var key = line[..line.IndexOf('=')];
        var value = line[(line.IndexOf('=') + 1)..];
        switch (key)
        {
            case "server-port" when int.TryParse(value, out var p):
                port = p;
                break;
            case "max-players" when int.TryParse(value, out var m):
                maxPlayers = m;
                break;
            case "level-name":
                levelName = value;
                break;
            case "allow-list":
                allowListOn = value == "true";
                break;
        }
    }
}

Console.WriteLine("NO LOG FILE! - setting up server logging...");
Log("Starting Server");
Log($"Version: {Version}");
Log("Session ID: 00000000-0000-0000-0000-000000000000");
Log($"Level Name: {levelName}");

if (Environment.GetEnvironmentVariable("FAKE_BDS_CRASH_ON_START") == "1")
{
    Log("Simulated crash during startup", "ERROR");
    return 3;
}

if (int.TryParse(Environment.GetEnvironmentVariable("FAKE_BDS_START_DELAY_MS"), out var delay))
{
    Thread.Sleep(delay);
}

var listener = new TcpListener(IPAddress.Any, port);
listener.Start();
_ = Task.Run(async () =>
{
    while (true)
    {
        try
        {
            using var c = await listener.AcceptTcpClientAsync();
        }
        catch
        {
            return;
        }
    }
});

// Crear el mundo si no existe, como hace BDS.
var worldDir = Path.Combine("worlds", levelName);
if (!Directory.Exists(worldDir))
{
    Directory.CreateDirectory(Path.Combine(worldDir, "db"));
    File.WriteAllText(Path.Combine(worldDir, "level.dat"), "fake level " + new string('x', 100));
    File.WriteAllText(Path.Combine(worldDir, "levelname.txt"), levelName);
    File.WriteAllText(Path.Combine(worldDir, "db", "CURRENT"), "MANIFEST-000001\n");
    File.WriteAllText(Path.Combine(worldDir, "db", "000005.ldb"), new string('d', 4096));
}
var holding = false;
var queries = 0;

Log("Game mode: 0 Survival");
Log("Difficulty: 1 EASY");
Log($"IPv4 supported, port: {port}: Used for gameplay and LAN discovery");
Log("Server started.");

string? input;
while ((input = Console.ReadLine()) is not null)
{
    var parts = input.Trim().Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
    if (parts.Length == 0)
    {
        continue;
    }
    switch (parts[0])
    {
        case "stop":
            Log("Server stop requested.");
            Log("Stopping server...");
            listener.Stop();
            Console.WriteLine("Quit correctly");
            return 0;
        case "list":
            Log($"There are {players.Count}/{maxPlayers} players online:");
            Console.WriteLine(string.Join(", ", players.Keys));
            break;
        case "say" when parts.Length > 1:
            Log($"[Server] {string.Join(' ', parts.Skip(1))}");
            break;
        case "join" when parts.Length == 3:
            if (allowListOn && !InAllowList(parts[2]))
            {
                break; // BDS rechaza en silencio: el cliente ve "No estás invitado".
            }
            players[parts[1]] = parts[2];
            Log($"Player connected: {parts[1]}, xuid: {parts[2]}");
            Log($"Player Spawned: {parts[1]} xuid: {parts[2]}, pfid: 0000000000000000");
            break;
        case "leave" when parts.Length == 3:
            players.Remove(parts[1]);
            Log($"Player disconnected: {parts[1]}, xuid: {parts[2]}, pfid: 0000000000000000");
            break;
        case "save" when parts.Length > 1 && parts[1] == "hold":
            if (holding)
            {
                Log("The command is already running", "ERROR");
            }
            else
            {
                holding = true;
                queries = 0;
                Log("Saving...");
            }
            break;
        case "save" when parts.Length > 1 && parts[1] == "query":
            if (!holding)
            {
                Log("A previous save has not been completed.");
                break;
            }
            if (queries++ == 0)
            {
                Log("A previous save has not been completed.");
                break;
            }
            Log("Data saved. Files are now ready to be copied.");
            // Se simula que BDS sigue escribiendo: el archivo crece, pero hay que copiar solo la longitud indicada.
            var listed = Directory.EnumerateFiles(worldDir, "*", SearchOption.AllDirectories)
                .Where(f => !f.EndsWith("levelname.txt", StringComparison.Ordinal))
                .Select(f => $"{Path.GetRelativePath("worlds", f).Replace('\\', '/')}:{new FileInfo(f).Length}")
                .ToList();
            foreach (var f in Directory.EnumerateFiles(Path.Combine(worldDir, "db")))
            {
                File.AppendAllText(f, "BASURA-POSTERIOR");
            }
            Console.WriteLine(string.Join(", ", listed));
            break;
        case "save" when parts.Length > 1 && parts[1] == "resume":
            holding = false;
            Log("Changes to the level are resumed.");
            break;
        case "kick" when parts.Length > 1:
        {
            // kick "Nombre con espacios" motivo
            var rest = input.Trim()[5..].Trim();
            string target, reason;
            if (rest.StartsWith('"'))
            {
                var close = rest.IndexOf('"', 1);
                target = rest[1..close];
                reason = rest[(close + 1)..].Trim();
            }
            else
            {
                var sp = rest.IndexOf(' ');
                target = sp < 0 ? rest : rest[..sp];
                reason = sp < 0 ? "" : rest[(sp + 1)..];
            }
            if (players.Remove(target, out var kx))
            {
                Log($"Kicked {target} from the game: '{reason}'");
                Log($"Player disconnected: {target}, xuid: {kx}, pfid: 0000000000000000");
            }
            else
            {
                Log("No targets matched selector", "ERROR");
            }
            break;
        }
        case "allowlist" when parts.Length > 1:
            switch (parts[1])
            {
                case "on": allowListOn = true; Log("Allowlist is now on"); break;
                case "off": allowListOn = false; Log("Allowlist is now off"); break;
                case "reload": Log("Allowlist reloaded from file."); break;
                default: Log("Unknown allowlist command", "ERROR"); break;
            }
            break;
        case "tell" when parts.Length > 1:
            Log($"[Server -> {Unquote(input.Trim()[5..].Split('"', StringSplitOptions.RemoveEmptyEntries)[0])}] (privado)");
            break;
        case "permission" when parts.Length > 1 && parts[1] == "reload":
            Log("Permissions reloaded from file.");
            break;
        case "op" or "deop" when parts.Length > 1:
            Log(parts[0] == "op" ? $"Opped: {Unquote(input.Trim()[3..])}" : $"De-opped: {Unquote(input.Trim()[5..])}");
            break;
        case "gamerule" when parts.Length == 1:
            Log(string.Join(", ", rules.Select(kv => $"{kv.Key.ToLowerInvariant()} = {kv.Value}")));
            break;
        case "gamerule" when parts.Length == 3:
        {
            var kv = parts[2].Split(' ', 2);
            rules[parts[1]] = parts[2].Trim();
            Log($"Game rule {parts[1]} has been updated to {parts[2].Trim()}");
            _ = kv;
            break;
        }
        case "time" when parts.Length == 3 && parts[1] == "set":
            Log($"Set the time to {parts[2]}");
            break;
        case "weather" when parts.Length > 1:
            Log($"Changing to {parts[1]} weather");
            break;
        case "crash":
            Log("Simulated crash", "ERROR");
            return 139;
        case "hang":
            // Deja de leer stdin: sirve para probar la parada forzada.
            Thread.Sleep(Timeout.Infinite);
            break;
        default:
            Log($"Unknown command: {parts[0]}. Please check that the command exists and that you have permission to use it.", "ERROR");
            break;
    }
}

return 0;
