using CubeKeeper.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace CubeKeeper.Infrastructure.Users;

public sealed record PanelUserInfo(string Id, string UserName, string Role, bool TwoFactorEnabled, bool LockedOut);

/// <summary>Gestión de usuarios del panel. Nunca deja el panel sin ningún administrador.</summary>
public sealed class PanelUserService(UserManager<PanelUser> users, RoleManager<IdentityRole> roles)
{
    public async Task<IReadOnlyList<PanelUserInfo>> ListAsync()
    {
        var result = new List<PanelUserInfo>();
        foreach (var u in await users.Users.OrderBy(u => u.UserName).ToListAsync())
        {
            result.Add(await ToInfoAsync(u));
        }
        return result;
    }

    public async Task<PanelUserInfo> CreateAsync(string userName, string password, string role)
    {
        ValidateRole(role);
        userName = userName?.Trim() ?? string.Empty;
        if (userName.Length is < 3 or > 32 || !userName.All(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_'))
        {
            throw new ArgumentException("El usuario debe tener entre 3 y 32 letras, números, puntos, guiones o guiones bajos.");
        }
        await EnsureRolesAsync();
        var user = new PanelUser { UserName = userName };
        Check(await users.CreateAsync(user, password));
        Check(await users.AddToRoleAsync(user, role));
        return await ToInfoAsync(user);
    }

    public async Task SetRoleAsync(string userId, string role)
    {
        ValidateRole(role);
        var user = await FindAsync(userId);
        var current = await RoleOfAsync(user);
        if (current == role)
        {
            return;
        }
        if (current == PanelRoles.Admin && await AdminCountAsync() <= 1)
        {
            throw new InvalidOperationException("Tiene que quedar al menos un administrador.");
        }
        await EnsureRolesAsync();
        Check(await users.RemoveFromRolesAsync(user, await users.GetRolesAsync(user)));
        Check(await users.AddToRoleAsync(user, role));
        // Cierra sus sesiones abiertas: el permiso cambia ya, no cuando caduque la cookie.
        await users.UpdateSecurityStampAsync(user);
    }

    public async Task ResetPasswordAsync(string userId, string newPassword)
    {
        var user = await FindAsync(userId);
        var token = await users.GeneratePasswordResetTokenAsync(user);
        Check(await users.ResetPasswordAsync(user, token, newPassword));
        await users.SetLockoutEndDateAsync(user, null);
        await users.ResetAccessFailedCountAsync(user);
    }

    public async Task UnlockAsync(string userId)
    {
        var user = await FindAsync(userId);
        await users.SetLockoutEndDateAsync(user, null);
        await users.ResetAccessFailedCountAsync(user);
    }

    /// <summary>Quita la verificación en dos pasos (por si alguien pierde el móvil y los códigos de recuperación).</summary>
    public async Task DisableTwoFactorAsync(string userId)
    {
        var user = await FindAsync(userId);
        Check(await users.SetTwoFactorEnabledAsync(user, false));
        Check(await users.ResetAuthenticatorKeyAsync(user));
    }

    public async Task DeleteAsync(string userId, string currentUserName)
    {
        var user = await FindAsync(userId);
        if (string.Equals(user.UserName, currentUserName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("No puedes borrar tu propio usuario.");
        }
        if (await RoleOfAsync(user) == PanelRoles.Admin && await AdminCountAsync() <= 1)
        {
            throw new InvalidOperationException("Tiene que quedar al menos un administrador.");
        }
        Check(await users.DeleteAsync(user));
    }

    public async Task EnsureRolesAsync()
    {
        foreach (var role in PanelRoles.All)
        {
            if (!await roles.RoleExistsAsync(role))
            {
                Check(await roles.CreateAsync(new IdentityRole(role)));
            }
        }
    }

    private async Task<int> AdminCountAsync() => (await users.GetUsersInRoleAsync(PanelRoles.Admin)).Count;

    private async Task<string> RoleOfAsync(PanelUser user)
    {
        var r = await users.GetRolesAsync(user);
        return r.Contains(PanelRoles.Admin) ? PanelRoles.Admin : r.FirstOrDefault() ?? PanelRoles.Operator;
    }

    private async Task<PanelUserInfo> ToInfoAsync(PanelUser u) =>
        new(u.Id, u.UserName!, await RoleOfAsync(u), u.TwoFactorEnabled, await users.IsLockedOutAsync(u));

    private async Task<PanelUser> FindAsync(string userId) =>
        await users.FindByIdAsync(userId) ?? throw new InvalidOperationException("Ese usuario ya no existe.");

    private static void ValidateRole(string role)
    {
        if (!PanelRoles.All.Contains(role))
        {
            throw new ArgumentException($"Rol desconocido: {role}.");
        }
    }

    private static void Check(IdentityResult result)
    {
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(string.Join(" ", result.Errors.Select(e => Translate(e))));
        }
    }

    /// <summary>Los mensajes de Identity vienen en inglés: los habituales, en castellano.</summary>
    private static string Translate(IdentityError e) => e.Code switch
    {
        "DuplicateUserName" => "Ya existe un usuario con ese nombre.",
        "PasswordTooShort" => "La contraseña es demasiado corta (mínimo 10 caracteres).",
        "PasswordMismatch" => "La contraseña actual no es correcta.",
        "InvalidUserName" => "Nombre de usuario no válido.",
        "PasswordRequiresUniqueChars" => "La contraseña necesita más caracteres distintos.",
        _ => e.Description,
    };
}
