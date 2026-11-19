namespace CubeKeeper.Core.Logs;

/// <summary>Evento reconocido en una línea de salida de bedrock_server.</summary>
public abstract record BdsLogEvent;

/// <summary>"Version: 1.26.10.4" al arrancar.</summary>
public sealed record ServerVersionReported(string Version) : BdsLogEvent;

/// <summary>"Server started." El servidor ya acepta jugadores.</summary>
public sealed record ServerStarted : BdsLogEvent;

/// <summary>"Stopping server..." tras el comando stop.</summary>
public sealed record ServerStopping : BdsLogEvent;

/// <summary>"Player connected: &lt;nombre&gt;, xuid: &lt;xuid&gt;".</summary>
public sealed record PlayerConnected(string Name, string Xuid) : BdsLogEvent;

/// <summary>"Player disconnected: &lt;nombre&gt;, xuid: &lt;xuid&gt;".</summary>
public sealed record PlayerDisconnected(string Name, string Xuid) : BdsLogEvent;

/// <summary>"Player Spawned: &lt;nombre&gt; xuid: &lt;xuid&gt;" (el jugador ya está dentro del mundo).</summary>
public sealed record PlayerSpawned(string Name, string Xuid) : BdsLogEvent;

/// <summary>Cabecera de la respuesta a "list": "There are 1/10 players online:".</summary>
public sealed record PlayerCountReported(int Online, int Max) : BdsLogEvent;
