namespace CubeKeeper.Web.Hosting;

/// <summary>
/// Traduce las variables de entorno "amigables" del contenedor (CUBEKEEPER_*) a la configuración de .NET.
/// También se pueden usar directamente las de .NET, por ejemplo <c>CubeKeeper__Bds__StopTimeoutSeconds</c>.
/// Para los secretos se admite el sufijo <c>_FILE</c> (ruta a un archivo con el valor), al estilo de Docker secrets.
/// </summary>
public static class EnvironmentMapping
{
    private static readonly (string Env, string Key)[] Map =
    [
        ("CUBEKEEPER_DATA", "CubeKeeper:DataPath"),
        ("CUBEKEEPER_PUBLIC_HOST", "CubeKeeper:PublicHost"),
        ("CUBEKEEPER_CHANNEL", "CubeKeeper:Bds:Channel"),
        ("CUBEKEEPER_ACCEPT_EULA", "CubeKeeper:Bds:AcceptEula"),
        ("CUBEKEEPER_AUTOSTART", "CubeKeeper:Bds:AutoStart"),
        ("CUBEKEEPER_BDS_VERSION", "CubeKeeper:Bds:PinnedVersion"),
        ("CUBEKEEPER_DIRECT_DOWNLOAD_URL", "CubeKeeper:Bds:DirectDownloadUrl"),
        ("CUBEKEEPER_ADMIN_USER", "CubeKeeper:Admin:UserName"),
        ("CUBEKEEPER_ADMIN_PASSWORD", "CubeKeeper:Admin:Password"),
        ("CUBEKEEPER_ADMIN_RESET", "CubeKeeper:Admin:ResetPassword"),
        ("CUBEKEEPER_BACKUP_INTERVAL_MINUTES", "CubeKeeper:Backups:IntervalMinutes"),
        ("CUBEKEEPER_BACKUP_SKIP_IF_IDLE", "CubeKeeper:Backups:SkipIfIdle"),
        ("CUBEKEEPER_BACKUP_KEEP_HOURLY", "CubeKeeper:Backups:KeepHourly"),
        ("CUBEKEEPER_BACKUP_KEEP_DAILY", "CubeKeeper:Backups:KeepDaily"),
        ("CUBEKEEPER_BACKUP_KEEP_WEEKLY", "CubeKeeper:Backups:KeepWeekly"),
        ("CUBEKEEPER_BACKUP_KEEP_MANUAL", "CubeKeeper:Backups:KeepManual"),
        ("CUBEKEEPER_BACKUP_SECONDARY", "CubeKeeper:Backups:SecondaryPath"),
        ("CUBEKEEPER_UPDATE_AUTO", "CubeKeeper:Updates:AutoUpdate"),
        ("CUBEKEEPER_UPDATE_TIME", "CubeKeeper:Updates:Time"),
        ("CUBEKEEPER_UPDATE_WARN_MINUTES", "CubeKeeper:Updates:WarningMinutes"),
        ("CUBEKEEPER_UPDATE_SKIP_IF_PLAYERS", "CubeKeeper:Updates:SkipIfPlayersOnline"),
        ("CUBEKEEPER_NTFY_URL", "CubeKeeper:Notifications:NtfyUrl"),
        ("CUBEKEEPER_NTFY_TOPIC", "CubeKeeper:Notifications:NtfyTopic"),
        ("CUBEKEEPER_NTFY_TOKEN", "CubeKeeper:Notifications:NtfyToken"),
        ("CUBEKEEPER_PANEL_URL", "CubeKeeper:Notifications:PanelUrl"),
        ("CUBEKEEPER_ALLOWED_NETWORKS", "CubeKeeper:Network:AllowedNetworks"),
        ("CUBEKEEPER_TRUSTED_PROXIES", "CubeKeeper:Network:TrustedProxies"),
        ("CUBEKEEPER_REALMS_CLIENT_VERSION", "CubeKeeper:Realms:ClientVersion"),
        ("CUBEKEEPER_KEY_SECRET", "CubeKeeper:Security:KeySecret"),
    ];

    public static void Apply(ConfigurationManager configuration, Func<string, string?>? getEnv = null)
    {
        getEnv ??= Environment.GetEnvironmentVariable;
        var values = new Dictionary<string, string?>();
        foreach (var (env, key) in Map)
        {
            var value = getEnv(env);
            var file = getEnv(env + "_FILE");
            if (string.IsNullOrEmpty(value) && !string.IsNullOrEmpty(file) && File.Exists(file))
            {
                value = File.ReadAllText(file).Trim();
            }
            if (!string.IsNullOrEmpty(value))
            {
                values[key] = value;
            }
        }
        if (values.Count > 0)
        {
            configuration.AddInMemoryCollection(values);
        }
    }

    /// <summary>Puerto del panel (CUBEKEEPER_PORT). Null si no se indica.</summary>
    public static int? PanelPort(Func<string, string?>? getEnv = null)
    {
        getEnv ??= Environment.GetEnvironmentVariable;
        return int.TryParse(getEnv("CUBEKEEPER_PORT"), out var p) && p is > 0 and < 65536 ? p : null;
    }
}
