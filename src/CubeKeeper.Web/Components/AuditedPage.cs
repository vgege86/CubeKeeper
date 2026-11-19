using CubeKeeper.Infrastructure.Audit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;

namespace CubeKeeper.Web.Components;

/// <summary>Base para las páginas con acciones de administración: anota en la auditoría quién hace qué.</summary>
public abstract class AuditedPage : ComponentBase
{
    [Inject]
    protected AuditLog Audit { get; set; } = default!;

    [CascadingParameter]
    private Task<AuthenticationState>? AuthenticationState { get; set; }

    protected async Task<string> CurrentUserAsync() =>
        AuthenticationState is null ? "?" : (await AuthenticationState).User.Identity?.Name ?? "?";

    protected async Task<bool> IsAdminAsync() =>
        AuthenticationState is not null && (await AuthenticationState).User.IsInRole(CubeKeeper.Infrastructure.Persistence.PanelRoles.Admin);

    protected async Task AuditAsync(string action, string? target = null, string? details = null) =>
        await Audit.RecordAsync(await CurrentUserAsync(), action, target, details);
}
