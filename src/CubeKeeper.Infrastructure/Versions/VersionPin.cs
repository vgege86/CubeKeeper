using CubeKeeper.Core.Versions;
using CubeKeeper.Infrastructure.Configuration;
using CubeKeeper.Infrastructure.State;
using Microsoft.Extensions.Options;

namespace CubeKeeper.Infrastructure.Versions;

/// <summary>
/// La versión de BDS fijada. CUBEKEEPER_BDS_VERSION es el valor inicial; desde el panel se puede pasar a otra (al
/// instalar una más nueva o volver a la anterior) sin tocar la app de TrueNAS. Lo del panel se guarda en state.json
/// y manda sobre la variable mientras sean distintas.
/// </summary>
public sealed class VersionPin(AppStateStore state, IOptions<CubeKeeperOptions> options)
{
    /// <summary>Versión de CUBEKEEPER_BDS_VERSION, o null.</summary>
    public string? EnvironmentVersion => string.IsNullOrWhiteSpace(options.Value.Bds.PinnedVersion) ? null : options.Value.Bds.PinnedVersion.Trim();

    /// <summary>La que se usa: la del panel si hay, si no la de la variable.</summary>
    public string? Version => state.Current.BdsPin is { } pin ? pin.Version : EnvironmentVersion;

    public bool IsPinned => !string.IsNullOrWhiteSpace(Version);

    /// <summary>La versión fijada viene del panel (y no coincide con la variable).</summary>
    public bool FromPanel => state.Current.BdsPin is not null;

    /// <summary>
    /// Tras instalar <paramref name="version"/> con versión fijada, la fija: si coincide con la variable, vuelve a
    /// mandar la variable; si no, se guarda como fijada desde el panel.
    /// </summary>
    public void Follow(string version)
    {
        if (!IsPinned)
        {
            return;
        }
        if (!BdsVersion.TryParse(version, out var parsed) || parsed is null)
        {
            throw new ArgumentException($"Versión no válida: '{version}'.", nameof(version));
        }
        var matchesEnvironment = BdsVersion.TryParse(EnvironmentVersion, out var env) && env is not null && env.Equals(parsed);
        state.Update(s => s with { BdsPin = matchesEnvironment ? null : new BdsPinSetting(parsed.Text) });
    }

    /// <summary>Olvida lo fijado desde el panel: vuelve a mandar CUBEKEEPER_BDS_VERSION.</summary>
    public void UseEnvironment() => state.Update(s => s with { BdsPin = null });
}
