using CubeKeeper.Core.Players;

namespace CubeKeeper.Core.Abstractions;

/// <summary>Quién está conectado ahora mismo.</summary>
public interface IPlayerPresence
{
    IReadOnlyList<OnlinePlayer> Online { get; }

    /// <summary>Se dispara cuando alguien entra o sale.</summary>
    event Action? Changed;

    /// <summary>Envía <c>list</c> al servidor para corregir la lista si se ha perdido algún evento.</summary>
    Task ResyncAsync(CancellationToken ct = default);
}
