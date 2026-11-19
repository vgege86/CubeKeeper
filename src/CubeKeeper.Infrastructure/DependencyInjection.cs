using CubeKeeper.Core.Abstractions;
using CubeKeeper.Infrastructure.Configuration;
using CubeKeeper.Infrastructure.Health;
using CubeKeeper.Infrastructure.Persistence;
using CubeKeeper.Infrastructure.Process;
using CubeKeeper.Infrastructure.State;
using CubeKeeper.Infrastructure.Versions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CubeKeeper.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddCubeKeeperInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<CubeKeeperOptions>().Bind(configuration.GetSection(CubeKeeperOptions.SectionName));
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<DataPaths>();
        services.AddSingleton<AppStateStore>();
        services.AddSingleton<IConsoleLog>(sp => new ConsoleLog(sp.GetRequiredService<TimeProvider>()));

        var userAgent = $"CubeKeeper/{typeof(DependencyInjection).Assembly.GetName().Version?.ToString(3)}";
        services.AddHttpClient<IVersionSource, VersionResolver>(c =>
        {
            c.Timeout = TimeSpan.FromSeconds(30);
            c.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
        });
        services.AddHttpClient<IServerInstaller, ServerInstaller>(c =>
        {
            c.Timeout = TimeSpan.FromMinutes(15);
            c.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
        });
        // El instalador es "transient" como cliente tipado; el supervisor (singleton) necesita uno estable.
        services.AddSingleton<ServerSupervisor>(sp => ActivatorUtilitiesCreate(sp));
        services.AddSingleton<IServerController>(sp => sp.GetRequiredService<ServerSupervisor>());
        services.AddHostedService<ServerSupervisorHost>();

        // Fábrica para los servicios singleton (jugadores) y contexto con ámbito para Identity.
        services.AddDbContextFactory<CubeKeeperDbContext>((sp, o) =>
            o.UseSqlite($"Data Source={sp.GetRequiredService<DataPaths>().Database}"));
        services.AddScoped(sp => sp.GetRequiredService<IDbContextFactory<CubeKeeperDbContext>>().CreateDbContext());

        services.AddSingleton<Players.PlayerTrackingService>();
        services.AddSingleton<IPlayerPresence>(sp => sp.GetRequiredService<Players.PlayerTrackingService>());
        services.AddHostedService(sp => sp.GetRequiredService<Players.PlayerTrackingService>());
        services.AddSingleton<Players.PlayerStatsService>();
        services.AddSingleton<Settings.PropertiesEditor>();
        services.AddSingleton<ConsoleCommandRunner>();
        services.AddSingleton<Backups.BackupService>();
        services.AddSingleton<Updates.UpdateService>();
        services.AddSingleton<Access.AccessService>();
        services.AddHostedService(sp => sp.GetRequiredService<Access.AccessService>());
        services.AddSingleton<Game.GameControlService>();
        services.AddSingleton<Scheduling.MaintenanceScheduler>();
        services.AddHostedService(sp => sp.GetRequiredService<Scheduling.MaintenanceScheduler>());

        services.AddSingleton<Settings.ConfigurationAdvisor>();
        services.AddHealthChecks().AddCheck<BdsHealthProbe>("bds");
        return services;
    }

    private static ServerSupervisor ActivatorUtilitiesCreate(IServiceProvider sp) =>
        ActivatorUtilities.CreateInstance<ServerSupervisor>(sp, sp.GetRequiredService<IServerInstaller>());
}
