using System.Net;
using CubeKeeper.Infrastructure.Configuration;
using Microsoft.Extensions.Options;

namespace CubeKeeper.Web.Hosting;

/// <summary>
/// Filtro de IP del panel (defensa en profundidad). Con Host Network el panel también responde en la IP del NAS,
/// saltándose la Access List de Nginx Proxy Manager; aquí se rechaza lo que no venga de las redes permitidas.
/// </summary>
public sealed class NetworkAccessMiddleware(RequestDelegate next, IOptions<CubeKeeperOptions> options, ILogger<NetworkAccessMiddleware> logger)
{
    private static readonly IPNetwork[] DefaultAllowed =
    [
        IPNetwork.Parse("10.0.0.0/8"),
        IPNetwork.Parse("172.16.0.0/12"),
        IPNetwork.Parse("192.168.0.0/16"),
        IPNetwork.Parse("100.64.0.0/10"),
        IPNetwork.Parse("fc00::/7"),
        IPNetwork.Parse("fe80::/10"),
    ];

    private readonly IPNetwork[] _allowed = ParseList(options.Value.Network.AllowedNetworks) is { Length: > 0 } list ? list : DefaultAllowed;

    public async Task InvokeAsync(HttpContext context)
    {
        var ip = context.Connection.RemoteIpAddress;
        if (ip is null || IsAllowed(ip))
        {
            await next(context);
            return;
        }

        logger.LogWarning("Acceso al panel rechazado desde {Ip}", ip);
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await context.Response.WriteAsync("Acceso no permitido desde esta red.");
    }

    internal bool IsAllowed(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6)
        {
            ip = ip.MapToIPv4();
        }
        return IPAddress.IsLoopback(ip) || _allowed.Any(n => n.Contains(ip));
    }

    public static IPNetwork[] ParseList(string? csv)
    {
        if (string.IsNullOrWhiteSpace(csv))
        {
            return [];
        }
        var result = new List<IPNetwork>();
        foreach (var part in csv.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (IPNetwork.TryParse(part, out var net))
            {
                result.Add(net);
            }
            else if (IPAddress.TryParse(part, out var single))
            {
                result.Add(new IPNetwork(single, single.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128));
            }
            else
            {
                throw new FormatException($"Red no válida en la configuración: '{part}'. Usa formato CIDR, por ejemplo 192.168.1.0/24.");
            }
        }
        return [.. result];
    }
}
