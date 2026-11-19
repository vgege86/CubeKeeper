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

    public BackupOptions Backups { get; set; } = new();

    public UpdateOptions Updates { get; set; } = new();

    public NotificationOptions Notifications { get; set; } = new();

    public RealmsOptions Realms { get; set; } = new();

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

public sealed class BackupOptions
{
    /// <summary>Cada cuántos minutos se hace una copia programada. 0 = desactivadas.</summary>
    public int IntervalMinutes { get; set; } = 60;

    /// <summary>No hacer la copia programada si nadie ha jugado desde la anterior.</summary>
    public bool SkipIfIdle { get; set; } = true;

    public int KeepHourly { get; set; } = 24;

    public int KeepDaily { get; set; } = 14;

    public int KeepWeekly { get; set; } = 8;

    public int KeepManual { get; set; } = 20;

    /// <summary>Copias automáticas antes de actualizar o restaurar.</summary>
    public int KeepSafety { get; set; } = 5;

    /// <summary>Segundo destino (por ejemplo /backups, montado desde el pool DATOS). Vacío = solo /data/backups.</summary>
    public string? SecondaryPath { get; set; }

    /// <summary>Cuánto esperar a que BDS deje los archivos listos (save query).</summary>
    public int SaveTimeoutSeconds { get; set; } = 60;
}

public sealed class UpdateOptions
{
    /// <summary>Comprobar cada día si hay versión nueva y actualizar. No tiene efecto con una versión fijada.</summary>
    public bool AutoUpdate { get; set; } = true;

    /// <summary>Hora local de la comprobación diaria.</summary>
    public string Time { get; set; } = "05:00";

    /// <summary>Minutos de antelación de los avisos por el chat, por ejemplo "5,1".</summary>
    public string WarningMinutes { get; set; } = "5,1";

    /// <summary>Si hay alguien jugando a la hora programada, dejarlo para el día siguiente.</summary>
    public bool SkipIfPlayersOnline { get; set; }

    /// <summary>Tiempo máximo para que la versión nueva llegue a "En marcha"; si no, se vuelve a la anterior.</summary>
    public int HealthTimeoutSeconds { get; set; } = 180;
}

public sealed class NotificationOptions
{
    /// <summary>Servidor ntfy (https://ntfy.sh o uno propio).</summary>
    public string NtfyUrl { get; set; } = "https://ntfy.sh";

    /// <summary>Tema de ntfy. Vacío = sin avisos. En ntfy.sh cualquiera que lo sepa puede leerlo: que sea difícil de adivinar.</summary>
    public string? NtfyTopic { get; set; }

    /// <summary>Token de acceso para temas protegidos (opcional).</summary>
    public string? NtfyToken { get; set; }

    /// <summary>Dirección del panel, para que al tocar el aviso se abra (por ejemplo https://cubekeeper.alucard86.fyi).</summary>
    public string? PanelUrl { get; set; }
}

/// <summary>
/// API no oficial de Realms (Bedrock). Los valores por defecto son los del cliente de Switch, como prismarine-auth y
/// prismarine-realms. Si Mojang o Microsoft cambian algo, se pueden ajustar aquí sin recompilar.
/// </summary>
public sealed class RealmsOptions
{
    public string ClientId { get; set; } = "00000000441cc96b";

    public string Scope { get; set; } = "service::user.auth.xboxlive.com::MBI_SSL";

    public string DeviceCodeUrl { get; set; } = "https://login.live.com/oauth20_connect.srf";

    public string TokenUrl { get; set; } = "https://login.live.com/oauth20_token.srf";

    public string UserAuthUrl { get; set; } = "https://user.auth.xboxlive.com/user/authenticate";

    public string DeviceAuthUrl { get; set; } = "https://device.auth.xboxlive.com/device/authenticate";

    public string TitleAuthUrl { get; set; } = "https://title.auth.xboxlive.com/title/authenticate";

    public string XstsUrl { get; set; } = "https://xsts.auth.xboxlive.com/xsts/authorize";

    public string DeviceType { get; set; } = "Nintendo";

    public string Host { get; set; } = "https://pocket.realms.minecraft.net";

    public string RelyingParty { get; set; } = "https://pocket.realms.minecraft.net/";

    /// <summary>Cabecera Client-Version. Vacío = la versión de BDS instalada (o 0.0.0).</summary>
    public string? ClientVersion { get; set; }

    public string UserAgent { get; set; } = "MCPE/UWP";
}
