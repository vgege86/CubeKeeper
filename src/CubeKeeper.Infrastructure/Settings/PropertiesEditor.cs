using CubeKeeper.Core.Properties;
using CubeKeeper.Infrastructure.Configuration;

namespace CubeKeeper.Infrastructure.Settings;

/// <summary>Un ajuste tal como lo muestra el editor.</summary>
public sealed record PropertyField(PropertyDefinition Definition, string Value, string? FileComment, RecommendedSetting? Recommended);

public sealed record SaveResult(IReadOnlyList<string> Changed, IReadOnlyDictionary<string, string> Errors, string? BackupPath)
{
    public bool Succeeded => Errors.Count == 0;
}

/// <summary>Lee y guarda server.properties desde la web, validando y conservando comentarios y orden.</summary>
public sealed class PropertiesEditor(DataPaths paths, TimeProvider time)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public bool Exists => File.Exists(paths.ServerProperties);

    public async Task<IReadOnlyList<PropertyField>> LoadAsync(CancellationToken ct = default)
    {
        var doc = await ServerPropertiesDocument.LoadAsync(paths.ServerProperties, ct).ConfigureAwait(false);
        var recommended = RecommendedSettings.Required.ToDictionary(r => r.Key, StringComparer.Ordinal);
        return doc.Keys
            .Select(k => new PropertyField(PropertyDefinitions.For(k), doc.Get(k) ?? string.Empty, doc.GetComment(k), recommended.GetValueOrDefault(k)))
            .ToList();
    }

    /// <summary>
    /// Valida y guarda los valores indicados (solo los que cambian). Si alguno no es válido no se guarda nada.
    /// Antes de escribir se hace una copia server.properties.AAAAMMDD-HHMMSS.bak.
    /// </summary>
    public async Task<SaveResult> SaveAsync(IReadOnlyDictionary<string, string> values, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(values);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var doc = await ServerPropertiesDocument.LoadAsync(paths.ServerProperties, ct).ConfigureAwait(false);
            var errors = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var (key, value) in values)
            {
                if (!doc.ContainsKey(key))
                {
                    errors[key] = "Este ajuste no existe en el archivo.";
                    continue;
                }
                if (PropertyDefinitions.For(key).Validate(value) is { } error)
                {
                    errors[key] = error;
                }
            }

            var merged = doc.Keys.ToDictionary(k => k, k => values.TryGetValue(k, out var v) ? v : doc.Get(k) ?? string.Empty, StringComparer.Ordinal);
            foreach (var (key, error) in PropertyDefinitions.ValidateTogether(merged))
            {
                errors.TryAdd(key, error);
            }

            if (errors.Count > 0)
            {
                return new SaveResult([], errors, null);
            }

            var changed = values.Where(kv => doc.Set(kv.Key, kv.Value)).Select(kv => kv.Key).ToList();
            if (changed.Count == 0)
            {
                return new SaveResult([], errors, null);
            }

            var backup = PropertiesBackup.Create(paths, time);
            await doc.SaveAsync(paths.ServerProperties, ct).ConfigureAwait(false);
            return new SaveResult(changed, errors, backup);
        }
        finally
        {
            _gate.Release();
        }
    }
}

internal static class PropertiesBackup
{
    /// <summary>Copia server.properties junto a él con la fecha y hora. Devuelve la ruta, o null si no existía.</summary>
    public static string? Create(DataPaths paths, TimeProvider time)
    {
        if (!File.Exists(paths.ServerProperties))
        {
            return null;
        }
        var backup = $"{paths.ServerProperties}.{time.GetLocalNow():yyyyMMdd-HHmmss}.bak";
        File.Copy(paths.ServerProperties, backup, overwrite: true);
        return backup;
    }
}
