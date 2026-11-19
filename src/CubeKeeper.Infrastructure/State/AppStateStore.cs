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

    /// <summary>Quién puede entrar.</summary>
    public CubeKeeper.Core.Access.AccessMode AccessMode { get; init; } = CubeKeeper.Core.Access.AccessMode.Open;

    /// <summary>Hasta cuándo dura el modo inscripción, o null si no está activo.</summary>
    public DateTimeOffset? EnrollmentUntil { get; init; }

    /// <summary>Avisos activados.</summary>
    public CubeKeeper.Core.Notifications.NotificationKind Notifications { get; init; } = CubeKeeper.Core.Notifications.NotificationKind.Defaults;

    /// <summary>Ajustes de ntfy guardados desde el panel (si no, se usan las variables de entorno).</summary>
    public NtfySettings? Ntfy { get; init; }

    /// <summary>Ajustes y último resultado del mapa del mundo.</summary>
    public MapSettings Map { get; init; } = new();

    /// <summary>Última versión de BDS de la que se avisó, para no repetir el aviso.</summary>
    public string? LastNotifiedVersion { get; init; }

    /// <summary>Resultado de la última actualización (manual o programada).</summary>
    public UpdateRecord? LastUpdate { get; init; }

    /// <summary>Cuenta de Microsoft conectada para descargar mundos de Realms (secretos cifrados).</summary>
    public RealmsSession? Realms { get; init; }
}

/// <summary>Ajustes de ntfy desde el formulario. El token va cifrado con Data Protection.</summary>
public sealed record NtfySettings(string Url, string? Topic, string? ProtectedToken, string? PanelUrl);

public sealed record MapSettings
{
    public string EngineId { get; init; } = CubeKeeper.Core.Maps.MapEngines.Unmined.Id;

    /// <summary>Comando personalizado por motor (si no hay, el de por defecto).</summary>
    public Dictionary<string, string> Commands { get; init; } = [];

    public bool Nightly { get; init; } = true;

    public string Time { get; init; } = "04:15";

    public MapRenderRecord? LastRender { get; init; }
}

public sealed record MapRenderRecord(DateTimeOffset At, string EngineId, bool Succeeded, string Message, TimeSpan Duration);

/// <summary>Sesión de Realms: token de renovación y clave de firma, ambos cifrados con Data Protection.</summary>
public sealed record RealmsSession(string ProtectedRefreshToken, string ProtectedSigningKey, string? Gamertag, string? Xuid, DateTimeOffset ConnectedAt);

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
