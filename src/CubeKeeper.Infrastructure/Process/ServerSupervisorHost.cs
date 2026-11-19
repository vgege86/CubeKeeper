using Microsoft.Extensions.Hosting;

namespace CubeKeeper.Infrastructure.Process;

/// <summary>Conecta el <see cref="ServerSupervisor"/> con el ciclo de vida del host (arranque y SIGTERM).</summary>
internal sealed class ServerSupervisorHost(ServerSupervisor supervisor, IHostApplicationLifetime lifetime) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // No bloquear el arranque de la web mientras arranca BDS.
        lifetime.ApplicationStarted.Register(() => _ = supervisor.InitializeAsync(lifetime.ApplicationStopping));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => supervisor.ShutdownAsync(cancellationToken);
}
