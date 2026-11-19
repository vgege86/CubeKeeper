using System.Security.Claims;
using CubeKeeper.Infrastructure.Persistence;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace CubeKeeper.Web.Hosting;

/// <summary>
/// Comprueba cada minuto que la sesión de un circuito de Blazor sigue siendo válida (el sello de seguridad cambia al
/// cambiar la contraseña, el rol o el segundo factor). Así, quitar permisos a alguien surte efecto enseguida.
/// </summary>
internal sealed class RevalidatingAuthStateProvider(
    ILoggerFactory loggerFactory,
    IServiceScopeFactory scopeFactory,
    IOptions<IdentityOptions> options) : RevalidatingServerAuthenticationStateProvider(loggerFactory)
{
    protected override TimeSpan RevalidationInterval => TimeSpan.FromMinutes(1);

    protected override async Task<bool> ValidateAuthenticationStateAsync(AuthenticationState authenticationState, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<PanelUser>>();
        var user = await users.GetUserAsync(authenticationState.User);
        if (user is null)
        {
            return false;
        }
        if (!users.SupportsUserSecurityStamp)
        {
            return true;
        }
        var principalStamp = authenticationState.User.FindFirstValue(options.Value.ClaimsIdentity.SecurityStampClaimType);
        return principalStamp == await users.GetSecurityStampAsync(user);
    }
}
