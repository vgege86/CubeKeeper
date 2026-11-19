using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace CubeKeeper.Infrastructure.Persistence;

/// <summary>Usuario del panel (no confundir con los jugadores).</summary>
public sealed class PanelUser : IdentityUser
{
}

public static class PanelRoles
{
    public const string Admin = "Admin";
}

public sealed class CubeKeeperDbContext(DbContextOptions<CubeKeeperDbContext> options)
    : IdentityDbContext<PanelUser>(options)
{
}
