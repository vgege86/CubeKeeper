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
        ("CUBEKEEPER_ALLOWED_NETWORKS", "CubeKeeper:Network:AllowedNetworks"),
        ("CUBEKEEPER_TRUSTED_PROXIES", "CubeKeeper:Network:TrustedProxies"),
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
