using CubeKeeper.Core.Properties;
using CubeKeeper.Infrastructure.Configuration;
using Microsoft.Extensions.Options;

namespace CubeKeeper.Web.Hosting;

/// <summary>Cómo conectarse al servidor: dirección, puerto y enlace para añadirlo en Minecraft (útil en la Xbox).</summary>
public sealed record ServerConnection(string Name, string Host, int Port)
{
    /// <summary>
    /// Enlace que abre Minecraft y añade el servidor a la pestaña «Servidores». En la Xbox se abre desde Microsoft Edge,
    /// que es la forma de añadir un servidor por dirección en una consola.
    /// </summary>
    public string MinecraftLink => $"minecraft://?addExternalServer={Uri.EscapeDataString(Name)}|{Host}:{Port}";
}

public sealed class ConnectionInfoService(IOptions<CubeKeeperOptions> options, DataPaths paths)
{
    public const int DefaultPort = 19132;

    /// <param name="requestHost">Host con el que se ha abierto el panel, si no hay CUBEKEEPER_PUBLIC_HOST.</param>
    public async Task<ServerConnection> GetAsync(string requestHost, CancellationToken ct = default)
    {
        var props = await ServerPropertiesDocument.LoadAsync(paths.ServerProperties, ct);
        var host = string.IsNullOrWhiteSpace(options.Value.PublicHost) ? requestHost : options.Value.PublicHost!.Trim();
        var port = props.GetInt("server-port") is int p and > 0 and < 65536 ? p : DefaultPort;
        return new ServerConnection(CleanName(props.Get("server-name")), host, port);
    }

    /// <summary>El nombre va dentro del enlace: sin «|» ni caracteres de control, y como mucho 40 caracteres.</summary>
    internal static string CleanName(string? serverName)
    {
        var name = new string((serverName ?? string.Empty).Where(c => c != '|' && c != ';' && !char.IsControl(c)).ToArray()).Trim();
        if (name.Length == 0 || name == "Dedicated Server")
        {
            return "CubeKeeper";
        }
        return name.Length > 40 ? name[..40].Trim() : name;
    }
}
