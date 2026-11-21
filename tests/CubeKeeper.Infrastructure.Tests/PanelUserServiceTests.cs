using System.Security.Cryptography;
using CubeKeeper.Infrastructure.Persistence;
using CubeKeeper.Infrastructure.Users;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CubeKeeper.Infrastructure.Tests;

public sealed class PanelUserServiceTests : IAsyncLifetime
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"ck-users-{Guid.NewGuid():N}.db");
    private ServiceProvider _sp = default!;
    private AsyncServiceScope _scope;

    private PanelUserService Service => _scope.ServiceProvider.GetRequiredService<PanelUserService>();
    private UserManager<PanelUser> Users => _scope.ServiceProvider.GetRequiredService<UserManager<PanelUser>>();

    public async Task InitializeAsync()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataProtection().UseEphemeralDataProtectionProvider();
        services.AddDbContext<CubeKeeperDbContext>(o => o.UseSqlite($"Data Source={_db}"));
        services.AddIdentityCore<PanelUser>(o =>
            {
                o.Password.RequiredLength = 10;
                o.Password.RequireDigit = false;
                o.Password.RequireLowercase = false;
                o.Password.RequireUppercase = false;
                o.Password.RequireNonAlphanumeric = false;
                o.Password.RequiredUniqueChars = 1;
            })
            .AddRoles<IdentityRole>()
            .AddEntityFrameworkStores<CubeKeeperDbContext>()
            .AddDefaultTokenProviders();
        services.AddScoped<PanelUserService>();
        _sp = services.BuildServiceProvider();
        _scope = _sp.CreateAsyncScope();
        await _scope.ServiceProvider.GetRequiredService<CubeKeeperDbContext>().Database.MigrateAsync();
        await Service.EnsureRolesAsync();
    }

    public async Task DisposeAsync()
    {
        await _scope.DisposeAsync();
        await _sp.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(_db);
    }

    [Fact]
    public async Task Creates_users_with_roles_and_rejects_bad_input()
    {
        var admin = await Service.CreateAsync("admin", "contraseña-larga", PanelRoles.Admin);
        var op = await Service.CreateAsync("papa", "otra-contraseña", PanelRoles.Operator);

        Assert.Equal(PanelRoles.Admin, admin.Role);
        Assert.Equal(PanelRoles.Operator, op.Role);
        Assert.Equal(["admin", "papa"], (await Service.ListAsync()).Select(u => u.UserName));

        var dup = await Assert.ThrowsAsync<InvalidOperationException>(() => Service.CreateAsync("papa", "contraseña-larga", PanelRoles.Operator));
        Assert.Contains("Ya existe", dup.Message);
        var shortPw = await Assert.ThrowsAsync<InvalidOperationException>(() => Service.CreateAsync("mama", "corta", PanelRoles.Operator));
        Assert.Contains("demasiado corta", shortPw.Message);
        await Assert.ThrowsAsync<ArgumentException>(() => Service.CreateAsync("con espacio", "contraseña-larga", PanelRoles.Operator));
        await Assert.ThrowsAsync<ArgumentException>(() => Service.CreateAsync("mama", "contraseña-larga", "SuperAdmin"));
    }

    [Fact]
    public async Task Never_leaves_the_panel_without_an_admin()
    {
        var admin = await Service.CreateAsync("admin", "contraseña-larga", PanelRoles.Admin);
        var op = await Service.CreateAsync("papa", "otra-contraseña", PanelRoles.Operator);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Service.SetRoleAsync(admin.Id, PanelRoles.Operator));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service.DeleteAsync(admin.Id, "papa"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service.DeleteAsync(op.Id, "papa")); // a uno mismo, no

        await Service.SetRoleAsync(op.Id, PanelRoles.Admin);
        await Service.SetRoleAsync(admin.Id, PanelRoles.Operator); // ya hay otro administrador
        await Service.DeleteAsync(admin.Id, "papa");
        Assert.Equal(PanelRoles.Admin, Assert.Single(await Service.ListAsync()).Role);
    }

    [Fact]
    public async Task Role_change_invalidates_sessions_and_reset_password_works()
    {
        var op = await Service.CreateAsync("papa", "otra-contraseña", PanelRoles.Operator);
        await Service.CreateAsync("admin", "contraseña-larga", PanelRoles.Admin);
        var user = (await Users.FindByIdAsync(op.Id))!;
        var stamp = await Users.GetSecurityStampAsync(user);

        await Service.SetRoleAsync(op.Id, PanelRoles.Admin);
        user = (await Users.FindByIdAsync(op.Id))!;
        Assert.NotEqual(stamp, await Users.GetSecurityStampAsync(user));

        await Service.ResetPasswordAsync(op.Id, "contraseña-nueva");
        user = (await Users.FindByIdAsync(op.Id))!;
        Assert.True(await Users.CheckPasswordAsync(user, "contraseña-nueva"));
        Assert.False(await Users.CheckPasswordAsync(user, "otra-contraseña"));
    }

    [Fact]
    public async Task Sign_out_everywhere_changes_the_security_stamp()
    {
        var op = await Service.CreateAsync("papa", "otra-contraseña", PanelRoles.Operator);
        var user = (await Users.FindByIdAsync(op.Id))!;
        var stamp = await Users.GetSecurityStampAsync(user);

        await Service.SignOutEverywhereAsync(op.Id);

        user = (await Users.FindByIdAsync(op.Id))!;
        Assert.NotEqual(stamp, await Users.GetSecurityStampAsync(user));
        Assert.True(await Users.CheckPasswordAsync(user, "otra-contraseña"));
    }

    [Fact]
    public async Task Totp_from_authenticator_key_enables_two_factor_and_admin_can_remove_it()
    {
        var info = await Service.CreateAsync("papa", "otra-contraseña", PanelRoles.Operator);
        var user = (await Users.FindByIdAsync(info.Id))!;
        await Users.ResetAuthenticatorKeyAsync(user);
        var key = (await Users.GetAuthenticatorKeyAsync(user))!;

        // Lo que calcula la aplicación del móvil (RFC 6238, SHA-1, 6 cifras, 30 s).
        var code = Totp(key, DateTimeOffset.UtcNow);
        Assert.True(await Users.VerifyTwoFactorTokenAsync(user, Users.Options.Tokens.AuthenticatorTokenProvider, code));
        Assert.False(await Users.VerifyTwoFactorTokenAsync(user, Users.Options.Tokens.AuthenticatorTokenProvider, "000000") && code != "000000");

        await Users.SetTwoFactorEnabledAsync(user, true);
        Assert.True((await Service.ListAsync()).Single().TwoFactorEnabled);

        await Service.DisableTwoFactorAsync(info.Id);
        Assert.False((await Service.ListAsync()).Single().TwoFactorEnabled);
    }

    internal static string Totp(string base32Key, DateTimeOffset at)
    {
        var key = Base32(base32Key);
        var counter = at.ToUnixTimeSeconds() / 30;
        var msg = BitConverter.GetBytes(counter);
        if (BitConverter.IsLittleEndian)
        {
            Array.Reverse(msg);
        }
        var hash = HMACSHA1.HashData(key, msg);
        var offset = hash[^1] & 0x0f;
        var binary = ((hash[offset] & 0x7f) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (binary % 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static byte[] Base32(string s)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bits = string.Concat(s.TrimEnd('=').ToUpperInvariant().Select(c => Convert.ToString(alphabet.IndexOf(c), 2).PadLeft(5, '0')));
        return Enumerable.Range(0, bits.Length / 8).Select(i => Convert.ToByte(bits.Substring(i * 8, 8), 2)).ToArray();
    }
}
