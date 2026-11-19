namespace CubeKeeper.Core.Server;

public enum ServerStatus
{
    /// <summary>No hay ningún bedrock_server instalado en /data/server.</summary>
    NotInstalled,
    Stopped,
    Starting,
    Running,
    Stopping,
    /// <summary>Se ha caído y se reintentará el arranque.</summary>
    Crashed,
    /// <summary>Se ha caído demasiadas veces seguidas: no se reintenta hasta arrancarlo a mano.</summary>
    Failed,
    Installing,
}

public static class ServerStatusExtensions
{
    public static string ToSpanish(this ServerStatus s) => s switch
    {
        ServerStatus.NotInstalled => "No instalado",
        ServerStatus.Stopped => "Parado",
        ServerStatus.Starting => "Arrancando",
        ServerStatus.Running => "En marcha",
        ServerStatus.Stopping => "Parando",
        ServerStatus.Crashed => "Caído (reintentando)",
        ServerStatus.Failed => "Caído (demasiados reinicios)",
        ServerStatus.Installing => "Instalando",
        _ => s.ToString(),
    };

    public static bool IsProcessAlive(this ServerStatus s) =>
        s is ServerStatus.Starting or ServerStatus.Running or ServerStatus.Stopping;
}
