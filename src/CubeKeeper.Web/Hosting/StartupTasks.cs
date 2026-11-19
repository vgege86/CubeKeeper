using CubeKeeper.Infrastructure.Configuration;
using CubeKeeper.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CubeKeeper.Web.Hosting;

public static class StartupTasks
{
    /// <summary>Crea las carpetas de /data, aplica migraciones y crea el administrador inicial si no hay usuarios.</summary>
    public static async Task InitializeAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var sp = scope.ServiceProvider;
        var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("CubeKeeper.Startup");
        var paths = sp.GetRequiredService<DataPaths>();
        paths.EnsureCreated();
        logger.LogInformation("Datos en {Root}", paths.Root);

        var db = sp.GetRequiredService<CubeKeeperDbContext>();
        await db.Database.MigrateAsync();

        var roles = sp.GetRequiredService<RoleManager<IdentityRole>>();
        if (!await roles.RoleExistsAsync(PanelRoles.Admin))
        {
            await roles.CreateAsync(new IdentityRole(PanelRoles.Admin));
        }

        var users = sp.GetRequiredService<UserManager<PanelUser>>();
        if (await users.Users.AnyAsync())
        {
            return;
        }

        var admin = sp.GetRequiredService<IOptions<CubeKeeperOptions>>().Value.Admin;
        if (string.IsNullOrWhiteSpace(admin.Password))
        {
            logger.LogError("No hay usuarios y no se ha indicado CUBEKEEPER_ADMIN_PASSWORD: no se podrá entrar al panel.");
            return;
        }

        var user = new PanelUser { UserName = admin.UserName };
        var result = await users.CreateAsync(user, admin.Password);
        if (!result.Succeeded)
        {
            logger.LogError("No se pudo crear el administrador inicial: {Errors}",
                string.Join("; ", result.Errors.Select(e => e.Description)));
            return;
        }
        await users.AddToRoleAsync(user, PanelRoles.Admin);
        logger.LogInformation("Administrador inicial '{User}' creado. Ya puedes quitar CUBEKEEPER_ADMIN_PASSWORD.", admin.UserName);
    }
}
