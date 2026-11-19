using System.Text.Json;
using CubeKeeper.Infrastructure.Configuration;

namespace CubeKeeper.Infrastructure.State;

/// <summary>Estado pequeño que debe sobrevivir a reinicios del contenedor.</summary>
public sealed record AppState
{
    /// <summary>Si el administrador quiere el servidor encendido.</summary>
    public bool DesiredRunning { get; init; }

    /// <summary>Versión de BDS copiada a /data/server.</summary>
    public string? InstalledVersion { get; init; }

    /// <summary>Resultado de la última actualización (manual o programada).</summary>
    public UpdateRecord? LastUpdate { get; init; }
}

public sealed record UpdateRecord(DateTimeOffset At, string? From, string To, bool Succeeded, string Message);

/// <summary>Guarda <see cref="AppState"/> en /data/state.json de forma atómica.</summary>
public sealed class AppStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _path;
    private readonly Lock _lock = new();
    private AppState? _cached;

    public AppStateStore(DataPaths paths)
    {
        _path = paths.StateFile;
    }

    public AppState Current
    {
        get
        {
            lock (_lock)
            {
                return _cached ??= Load();
            }
        }
    }

    public AppState Update(Func<AppState, AppState> change)
    {
        lock (_lock)
        {
            var next = change(_cached ??= Load());
            var dir = Path.GetDirectoryName(_path)!;
            Directory.CreateDirectory(dir);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(next, JsonOptions));
            File.Move(tmp, _path, overwrite: true);
            _cached = next;
            return next;
        }
    }

    private AppState Load()
    {
        try
        {
            return File.Exists(_path)
                ? JsonSerializer.Deserialize<AppState>(File.ReadAllText(_path)) ?? new AppState()
                : new AppState();
        }
        catch (JsonException)
        {
            return new AppState();
        }
    }
}
