using CubeKeeper.Core.Properties;
using CubeKeeper.Infrastructure.Configuration;

namespace CubeKeeper.Infrastructure.Settings;

/// <summary>Compara server.properties con la configuración recomendada y la aplica bajo demanda.</summary>
public sealed class ConfigurationAdvisor(DataPaths paths, TimeProvider time)
{
    public bool PropertiesExist => File.Exists(paths.ServerProperties);

    public async Task<IReadOnlyList<SettingDeviation>> CheckAsync(CancellationToken ct = default)
    {
        if (!PropertiesExist)
        {
            return [];
        }
        var doc = await ServerPropertiesDocument.LoadAsync(paths.ServerProperties, ct).ConfigureAwait(false);
        return RecommendedSettings.Check(doc);
    }

    /// <summary>Guarda una copia (server.properties.AAAAMMDD-HHMMSS.bak) y aplica los valores recomendados.</summary>
    public async Task<IReadOnlyList<string>> ApplyAsync(CancellationToken ct = default)
    {
        if (!PropertiesExist)
        {
            return [];
        }
        var doc = await ServerPropertiesDocument.LoadAsync(paths.ServerProperties, ct).ConfigureAwait(false);
        var changed = RecommendedSettings.Apply(doc);
        if (changed.Count == 0)
        {
            return changed;
        }
        PropertiesBackup.Create(paths, time);
        await doc.SaveAsync(paths.ServerProperties, ct).ConfigureAwait(false);
        return changed;
    }
}
