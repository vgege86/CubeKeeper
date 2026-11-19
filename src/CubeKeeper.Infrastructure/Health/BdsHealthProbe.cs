using System.Net.Sockets;
using CubeKeeper.Core.Abstractions;
using CubeKeeper.Core.Properties;
using CubeKeeper.Core.Server;
using CubeKeeper.Infrastructure.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace CubeKeeper.Infrastructure.Health;

/// <summary>
/// Salud del servidor de juego. Con NetherNet, BDS escucha en TCP en <c>server-port</c> (negociación HTTP),
/// así que basta con abrir una conexión TCP a 127.0.0.1. No usa RakNet (que falla con NetherNet).
/// Si el servidor está parado a propósito, se informa como sano: el contenedor (el panel) funciona.
/// </summary>
public sealed class BdsHealthProbe(IServerController controller, DataPaths paths) : IHealthCheck
{
    public const int DefaultPort = 19132;

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var snap = controller.Snapshot;
        var data = new Dictionary<string, object> { ["status"] = snap.Status.ToString() };

        switch (snap.Status)
        {
            case ServerStatus.NotInstalled:
            case ServerStatus.Stopped:
            case ServerStatus.Installing:
            case ServerStatus.Stopping:
                return HealthCheckResult.Healthy($"Servidor {snap.Status.ToSpanish().ToLowerInvariant()}", data);
            case ServerStatus.Starting:
                return HealthCheckResult.Degraded("Servidor arrancando", data: data);
            case ServerStatus.Crashed:
            case ServerStatus.Failed:
                return HealthCheckResult.Unhealthy(snap.LastError ?? "Servidor caído", data: data);
        }

        var (host, port) = await GetEndpointAsync(cancellationToken).ConfigureAwait(false);
        data["port"] = port;
        data["host"] = host;
        using var client = new TcpClient();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            await client.ConnectAsync(host, port, timeout.Token).ConfigureAwait(false);
            return HealthCheckResult.Healthy($"TCP {port} responde", data);
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            return HealthCheckResult.Unhealthy($"TCP {port} no responde", ex, data);
        }
    }

    /// <summary>127.0.0.1 salvo que server-ip limite BDS a una IP concreta (entonces solo escucha ahí).</summary>
    private async Task<(string Host, int Port)> GetEndpointAsync(CancellationToken ct)
    {
        var doc = await ServerPropertiesDocument.LoadAsync(paths.ServerProperties, ct).ConfigureAwait(false);
        var port = doc.GetInt("server-port") is int p and > 0 and < 65536 ? p : DefaultPort;
        var ip = doc.Get("server-ip")?.Trim();
        var host = !string.IsNullOrEmpty(ip) && System.Net.IPAddress.TryParse(ip, out var addr) && !addr.Equals(System.Net.IPAddress.Any)
            ? ip
            : "127.0.0.1";
        return (host, port);
    }
}
