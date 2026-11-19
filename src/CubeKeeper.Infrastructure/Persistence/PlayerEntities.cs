using CubeKeeper.Core.Players;

namespace CubeKeeper.Infrastructure.Persistence;

/// <summary>Jugador conocido, identificado por XUID (los gamertags pueden cambiar).</summary>
public sealed class Player
{
    public required string Xuid { get; set; }

    /// <summary>Último nombre con el que se conectó.</summary>
    public required string Name { get; set; }

    public DateTimeOffset FirstSeen { get; set; }

    public DateTimeOffset LastSeen { get; set; }

    public List<PlayerSession> Sessions { get; set; } = [];
}

/// <summary>Una conexión de un jugador: desde que entra hasta que sale.</summary>
public sealed class PlayerSession
{
    public long Id { get; set; }

    public required string Xuid { get; set; }

    /// <summary>Nombre que tenía en esta sesión.</summary>
    public required string Name { get; set; }

    public DateTimeOffset ConnectedAt { get; set; }

    /// <summary>Null mientras sigue conectado.</summary>
    public DateTimeOffset? DisconnectedAt { get; set; }

    public SessionEndReason? EndReason { get; set; }

    public Player? Player { get; set; }

    public TimeSpan? Duration => DisconnectedAt is { } end ? end - ConnectedAt : null;
}

public enum SessionEndReason
{
    Disconnected = 0,
    ServerStopped = 1,
    NotInList = 2,
    Reconnected = 3,
    /// <summary>CubeKeeper se reinició con la sesión abierta: la hora de salida es aproximada.</summary>
    Interrupted = 4,
}

internal static class SessionEndReasonMapping
{
    public static SessionEndReason From(LeaveReason reason) => reason switch
    {
        LeaveReason.Disconnected => SessionEndReason.Disconnected,
        LeaveReason.ServerStopped => SessionEndReason.ServerStopped,
        LeaveReason.NotInList => SessionEndReason.NotInList,
        LeaveReason.Reconnected => SessionEndReason.Reconnected,
        _ => SessionEndReason.Disconnected,
    };
}
