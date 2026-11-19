using Microsoft.Extensions.Options;

namespace CubeKeeper.Infrastructure.Configuration;

/// <summary>Rutas dentro de /data. Todo lo que escribe la app vive aquí.</summary>
public sealed class DataPaths
{
    public DataPaths(IOptions<CubeKeeperOptions> options)
        : this(options.Value.DataPath)
    {
    }

    public DataPaths(string root)
    {
        Root = Path.GetFullPath(root);
    }

    public string Root { get; }

    /// <summary>Directorio de trabajo de bedrock_server (worlds/, server.properties…).</summary>
    public string Server => Path.Combine(Root, "server");

    /// <summary>Versiones de BDS extraídas, una carpeta por versión.</summary>
    public string Versions => Path.Combine(Root, "versions");

    public string Downloads => Path.Combine(Root, "downloads");

    public string Backups => Path.Combine(Root, "backups");

    public string Keys => Path.Combine(Root, "keys");

    public string Database => Path.Combine(Root, "cubekeeper.db");

    public string StateFile => Path.Combine(Root, "state.json");

    public string ServerProperties => Path.Combine(Server, "server.properties");

    public string ServerExecutable => Path.Combine(Server, "bedrock_server");

    public void EnsureCreated()
    {
        foreach (var dir in new[] { Root, Server, Versions, Downloads, Backups, Keys })
        {
            Directory.CreateDirectory(dir);
        }
    }
}
