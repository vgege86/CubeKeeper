using CubeKeeper.Core.Versions;

namespace CubeKeeper.Infrastructure.Configuration;

public sealed class CubeKeeperOptions
{
    public const string SectionName = "CubeKeeper";

    /// <summary>Raíz de datos persistentes. En el contenedor, /data (Host Path del NAS).</summary>
    public string DataPath { get; set; } = "/data";

    /// <summary>Nombre o IP que verán los clientes (para el enlace de la Xbox). Si está vacío, se usa el host de la petición.</summary>
    public string? PublicHost { get; set; }

    public BdsOptions Bds { get; set; } = new();

    public AdminOptions Admin { get; set; } = new();

    public NetworkOptions Network { get; set; } = new();
}

public sealed class BdsOptions
{
    public ReleaseChannel Channel { get; set; } = ReleaseChannel.Stable;

    /// <summary>Aceptación de la EULA de Minecraft y la política de privacidad. Sin ella no se descarga nada.</summary>
    public bool AcceptEula { get; set; }

    /// <summary>Arrancar el servidor al iniciar el contenedor si estaba encendido la última vez.</summary>
    public bool AutoStart { get; set; } = true;

    public int StopTimeoutSeconds { get; set; } = 30;

    public int MaxRestarts { get; set; } = 3;

    public int RestartWindowMinutes { get; set; } = 10;

    public int RestartDelaySeconds { get; set; } = 5;

    /// <summary>Solo para desarrollo/pruebas: ejecutable alternativo (por ejemplo "dotnet").</summary>
    public string? LaunchCommand { get; set; }

    /// <summary>Solo para desarrollo/pruebas: argumentos del ejecutable alternativo.</summary>
    public string? LaunchArguments { get; set; }

    /// <summary>
    /// Versión fijada (por ejemplo 1.26.65.1). Si se indica, se instala exactamente esa y el panel no propone otras.
    /// </summary>
    public string? PinnedVersion { get; set; }

    /// <summary>Enlace directo a un zip concreto; si se indica, no se consultan las fuentes.</summary>
    public string? DirectDownloadUrl { get; set; }

    public string DownloadLinksUrl { get; set; } = "https://net.web.minecraft-services.net/api/v1.0/download/links";

    public bool UseCommunityIndexFallback { get; set; } = true;

    public string CommunityIndexUrl { get; set; } =
        "https://raw.githubusercontent.com/kittizz/bedrock-server-downloads/refs/heads/main/bedrock-server-downloads.json";

    /// <summary>Versiones descargadas que se conservan en /data/versions (para volver atrás).</summary>
    public int VersionsToKeep { get; set; } = 2;
}

public sealed class AdminOptions
{
    public string UserName { get; set; } = "admin";

    /// <summary>Contraseña inicial. Solo se usa si todavía no existe ningún usuario (o con <see cref="ResetPassword"/>).</summary>
    public string? Password { get; set; }

    /// <summary>
    /// Si es true, al arrancar se pone <see cref="Password"/> al usuario <see cref="UserName"/> (creándolo si no existe)
    /// y se desbloquea. Para recuperar el acceso; después hay que quitarlo.
    /// </summary>
    public bool ResetPassword { get; set; }
}

public sealed class NetworkOptions
{
    /// <summary>
    /// Redes desde las que se permite usar el panel (CIDR, separadas por comas).
    /// Vacío = redes privadas (RFC 1918), loopback y CGNAT.
    /// </summary>
    public string? AllowedNetworks { get; set; }

    /// <summary>Proxies inversos de confianza (CIDR) cuya cabecera X-Forwarded-For se acepta.</summary>
    public string? TrustedProxies { get; set; }
}
