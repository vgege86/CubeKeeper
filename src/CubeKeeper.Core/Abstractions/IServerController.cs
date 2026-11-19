using CubeKeeper.Core.Logs;
using CubeKeeper.Core.Server;
using CubeKeeper.Core.Versions;

namespace CubeKeeper.Core.Abstractions;

/// <summary>Instantánea del estado del servidor para mostrar en la interfaz.</summary>
public sealed record ServerSnapshot(
    ServerStatus Status,
    string? InstalledVersion,
    string? RunningVersion,
    DateTimeOffset? StartedAt,
    int? ProcessId,
    string? LastError);

/// <summary>Control del ciclo de vida de bedrock_server.</summary>
public interface IServerController
{
    ServerSnapshot Snapshot { get; }

    /// <summary>Se dispara cada vez que cambia el estado.</summary>
    event Action<ServerSnapshot>? StateChanged;

    /// <summary>Se dispara por cada evento reconocido en el log (jugadores, versión…).</summary>
    event Action<BdsLogEvent>? LogEvent;

    Task StartAsync(CancellationToken ct = default);

    /// <summary>Envía "stop" y espera a que termine; si no termina a tiempo, mata el proceso.</summary>
    Task StopAsync(CancellationToken ct = default);

    Task RestartAsync(CancellationToken ct = default);

    /// <summary>Escribe un comando en la entrada estándar del servidor.</summary>
    Task SendCommandAsync(string command, CancellationToken ct = default);

    /// <summary>Instala o actualiza BDS. Si estaba en marcha, lo para antes y lo arranca después.</summary>
    Task<InstallResult> InstallAsync(BdsRelease release, IProgress<string>? progress = null, CancellationToken ct = default);
}
