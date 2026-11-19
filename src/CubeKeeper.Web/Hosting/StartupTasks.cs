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
        var status = sp.GetRequiredService<AdminSeedStatus>();
        var admin = sp.GetRequiredService<IOptions<CubeKeeperOptions>>().Value.Admin;
        var anyUser = await users.Users.AnyAsync();

        if (anyUser && !admin.ResetPassword)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(admin.Password))
        {
            status.Problem = anyUser
                ? "CUBEKEEPER_ADMIN_RESET está activado pero falta CUBEKEEPER_ADMIN_PASSWORD."
                : "No hay usuarios y no se ha indicado CUBEKEEPER_ADMIN_PASSWORD.";
            logger.LogError("{Problem}", status.Problem);
            return;
        }

        if (admin.Password.Length < AdminSeedStatus.MinPasswordLength)
        {
            status.Problem =
                $"La contraseña de CUBEKEEPER_ADMIN_PASSWORD tiene {admin.Password.Length} caracteres; " +
                $"hacen falta al menos {AdminSeedStatus.MinPasswordLength}.";
            logger.LogError("{Problem}", status.Problem);
            return;
        }

        var user = await users.FindByNameAsync(admin.UserName);
        IdentityResult result;
        if (user is null)
        {
            user = new PanelUser { UserName = admin.UserName };
            result = await users.CreateAsync(user, admin.Password);
            if (result.Succeeded)
            {
                await users.AddToRoleAsync(user, PanelRoles.Admin);
            }
        }
        else
        {
            var token = await users.GeneratePasswordResetTokenAsync(user);
            result = await users.ResetPasswordAsync(user, token, admin.Password);
            if (result.Succeeded)
            {
                await users.SetLockoutEndDateAsync(user, null);
                await users.ResetAccessFailedCountAsync(user);
                if (!await users.IsInRoleAsync(user, PanelRoles.Admin))
                {
                    await users.AddToRoleAsync(user, PanelRoles.Admin);
                }
                status.WasReset = true;
            }
        }

        if (!result.Succeeded)
        {
            status.Problem = "No se pudo preparar el administrador: " + string.Join("; ", result.Errors.Select(e => e.Description));
            logger.LogError("{Problem}", status.Problem);
            return;
        }

        logger.LogInformation(status.WasReset
                ? "Contraseña de '{User}' restablecida. Quita CUBEKEEPER_ADMIN_RESET y CUBEKEEPER_ADMIN_PASSWORD."
                : "Administrador inicial '{User}' creado. Ya puedes quitar CUBEKEEPER_ADMIN_PASSWORD.",
            admin.UserName);
    }
}
