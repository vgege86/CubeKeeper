using CubeKeeper.Core.Logs;

namespace CubeKeeper.Core.Players;

/// <summary>Jugador conectado ahora mismo. <see cref="Xuid"/> es null si solo se conoce por <c>list</c>.</summary>
public sealed record OnlinePlayer(string Name, string? Xuid, DateTimeOffset Since);

public enum LeaveReason
{
    /// <summary>"Player disconnected" en el log.</summary>
    Disconnected,
    /// <summary>El servidor se ha parado o caído.</summary>
    ServerStopped,
    /// <summary>No aparecía en la respuesta a <c>list</c>.</summary>
    NotInList,
    /// <summary>Ha vuelto a conectarse sin que constara la desconexión anterior.</summary>
    Reconnected,
}

public abstract record PlayerChange(OnlinePlayer Player, DateTimeOffset At);

public sealed record PlayerJoined(OnlinePlayer Player, DateTimeOffset At) : PlayerChange(Player, At);

public sealed record PlayerLeft(OnlinePlayer Player, DateTimeOffset At, LeaveReason Reason) : PlayerChange(Player, At);

/// <summary>
/// Mantiene quién está conectado a partir de las líneas de salida de BDS.
/// Entiende "Player connected/disconnected" y la respuesta de <c>list</c>, que ocupa dos líneas:
/// <c>There are 2/10 players online:</c> seguida de <c>Steve, Alex</c>. No es seguro para hilos: hay que
/// llamarlo desde un único consumidor.
/// </summary>
public sealed class PlayerTracker
{
    private readonly Dictionary<string, OnlinePlayer> _online = new(StringComparer.Ordinal);
    private readonly Func<string, string?>? _xuidByName;
    private int? _pendingListCount;

    /// <param name="xuidByName">Para resolver el XUID de un nombre que aparece en <c>list</c> sin haberse visto conectar.</param>
    public PlayerTracker(Func<string, string?>? xuidByName = null)
    {
        _xuidByName = xuidByName;
    }

    /// <summary>Conectados, ordenados por nombre.</summary>
    public IReadOnlyList<OnlinePlayer> Online =>
        [.. _online.Values.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)];

    public IReadOnlyList<PlayerChange> Process(string rawLine, DateTimeOffset now) => Process(BdsLogLine.Parse(rawLine), now);

    public IReadOnlyList<PlayerChange> Process(BdsLogLine line, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(line);
        var evt = BdsLogParser.Parse(line);

        if (_pendingListCount is not null)
        {
            _pendingListCount = null;
            if (evt is null)
            {
                // Esta es la línea con los nombres.
                var names = line.Message.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                return Resync(names, now);
            }
            // Llegó otro evento en lugar de los nombres: se procesa el evento sin resincronizar.
        }

        switch (evt)
        {
            case PlayerConnected c:
                return Connect(c.Name, NullIfEmpty(c.Xuid), now);
            case PlayerDisconnected d:
                return Disconnect(d.Name, NullIfEmpty(d.Xuid), now);
            case PlayerCountReported { Online: 0 }:
                return Resync([], now);
            case PlayerCountReported r:
                _pendingListCount = r.Online;
                return [];
            case ServerStopping:
                return ServerStopped(now);
            default:
                return [];
        }
    }

    /// <summary>Cierra todas las sesiones (el servidor se ha parado o caído).</summary>
    public IReadOnlyList<PlayerChange> ServerStopped(DateTimeOffset now)
    {
        _pendingListCount = null;
        var left = _online.Values.Select(p => (PlayerChange)new PlayerLeft(p, now, LeaveReason.ServerStopped)).ToList();
        _online.Clear();
        return left;
    }

    private List<PlayerChange> Connect(string name, string? xuid, DateTimeOffset now)
    {
        var changes = new List<PlayerChange>();
        var key = Key(name, xuid);
        // Si ya estaba (por XUID o, si se había visto solo en list, por nombre), se cierra la sesión anterior.
        var previousKey = _online.ContainsKey(key) ? key : _online.FirstOrDefault(kv => kv.Value.Xuid is null && NameEquals(kv.Value.Name, name)).Key;
        if (previousKey is not null && _online.Remove(previousKey, out var previous))
        {
            changes.Add(new PlayerLeft(previous, now, LeaveReason.Reconnected));
        }

        var player = new OnlinePlayer(name, xuid, now);
        _online[key] = player;
        changes.Add(new PlayerJoined(player, now));
        return changes;
    }

    private List<PlayerChange> Disconnect(string name, string? xuid, DateTimeOffset now)
    {
        var key = Key(name, xuid);
        if (!_online.Remove(key, out var player))
        {
            var byName = _online.FirstOrDefault(kv => NameEquals(kv.Value.Name, name)).Key;
            if (byName is null || !_online.Remove(byName, out player))
            {
                return [];
            }
        }
        return [new PlayerLeft(player, now, LeaveReason.Disconnected)];
    }

    private List<PlayerChange> Resync(IReadOnlyCollection<string> names, DateTimeOffset now)
    {
        var changes = new List<PlayerChange>();
        foreach (var (key, player) in _online.ToList())
        {
            if (!names.Any(n => NameEquals(n, player.Name)))
            {
                _online.Remove(key);
                changes.Add(new PlayerLeft(player, now, LeaveReason.NotInList));
            }
        }
        foreach (var name in names)
        {
            if (_online.Values.Any(p => NameEquals(p.Name, name)))
            {
                continue;
            }
            var xuid = NullIfEmpty(_xuidByName?.Invoke(name));
            var player = new OnlinePlayer(name, xuid, now);
            _online[Key(name, xuid)] = player;
            changes.Add(new PlayerJoined(player, now));
        }
        return changes;
    }

    private static string Key(string name, string? xuid) => xuid is null ? "name:" + name.ToUpperInvariant() : "xuid:" + xuid;

    private static bool NameEquals(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
