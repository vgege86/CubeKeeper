using CubeKeeper.Core.Versions;

namespace CubeKeeper.Core.Abstractions;

/// <summary>Fuente de la última versión publicada de BDS.</summary>
public interface IVersionSource
{
    Task<BdsRelease?> GetLatestAsync(ReleaseChannel channel, CancellationToken ct = default);
}

public sealed record InstallResult(string Version, bool WasUpdate, IReadOnlyList<string> AddedProperties);

/// <summary>Descarga e instala BDS en el directorio de trabajo conservando el estado.</summary>
public interface IServerInstaller
{
    /// <summary>Versión instalada en /data/server, o null.</summary>
    string? InstalledVersion { get; }

    bool IsInstalled { get; }

    Task<InstallResult> InstallAsync(BdsRelease release, IProgress<string>? progress = null, CancellationToken ct = default);
}
