using System.Text.Json;
using CubeKeeper.Infrastructure;
using CubeKeeper.Infrastructure.Configuration;
using CubeKeeper.Infrastructure.Persistence;
using CubeKeeper.Web.Components;
using CubeKeeper.Web.Hosting;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;

var builder = WebApplication.CreateBuilder(args);
EnvironmentMapping.Apply(builder.Configuration);
if (EnvironmentMapping.PanelPort() is int port)
{
    builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
}

// Dar tiempo a "stop" de BDS cuando el contenedor recibe SIGTERM (ajustar stop_grace_period en Docker).
builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(60));

builder.Services.AddCubeKeeperInfrastructure(builder.Configuration);
builder.Services.AddSingleton<AdminSeedStatus>();

var dataPath = builder.Configuration["CubeKeeper:DataPath"] ?? "/data";
builder.Services.AddDataProtection()
    .SetApplicationName("CubeKeeper")
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataPath, "keys")));

// Autenticación: Identity con cookie. Se usa ya el modelo completo (roles, TOTP) para las fases siguientes.
builder.Services.AddAuthentication(IdentityConstants.ApplicationScheme).AddIdentityCookies();
builder.Services.AddIdentityCore<PanelUser>(o =>
    {
        // Solo cuenta la longitud: una frase larga es mejor que reglas de "una mayúscula y un número".
        o.Password.RequiredLength = AdminSeedStatus.MinPasswordLength;
        o.Password.RequiredUniqueChars = 1;
        o.Password.RequireNonAlphanumeric = false;
        o.Password.RequireUppercase = false;
        o.Password.RequireLowercase = false;
        o.Password.RequireDigit = false;
        o.Lockout.MaxFailedAccessAttempts = 5;
        o.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
        o.User.RequireUniqueEmail = false;
    })
    .AddRoles<IdentityRole>()
    .AddEntityFrameworkStores<CubeKeeperDbContext>()
    .AddSignInManager()
    .AddDefaultTokenProviders();
builder.Services.ConfigureApplicationCookie(o =>
{
    o.Cookie.Name = "CubeKeeper.Auth";
    o.Cookie.HttpOnly = true;
    o.Cookie.SameSite = SameSiteMode.Strict;
    o.LoginPath = "/login";
    o.AccessDeniedPath = "/sin-permiso";
    o.ExpireTimeSpan = TimeSpan.FromDays(7);
    o.SlidingExpiration = true;
});
builder.Services.AddAuthorizationBuilder()
    .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build())
    .AddPolicy("Admin", p => p.RequireRole(PanelRoles.Admin));
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<Microsoft.AspNetCore.Components.Authorization.AuthenticationStateProvider, RevalidatingAuthStateProvider>();
// Las cookies también se revalidan cada minuto (contraseña, rol o segundo factor cambiados).
builder.Services.Configure<SecurityStampValidatorOptions>(o => o.ValidationInterval = TimeSpan.FromMinutes(1));
builder.Services.AddScoped<CubeKeeper.Infrastructure.Users.PanelUserService>();

// Detrás de Nginx Proxy Manager: aceptar X-Forwarded-* solo de los proxies de confianza.
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
    var trusted = NetworkAccessMiddleware.ParseList(builder.Configuration["CubeKeeper:Network:TrustedProxies"]);
    if (trusted.Length == 0)
    {
        // Por defecto: redes de Docker (NPM en modo bridge). La IP del NAS se añade por configuración.
        trusted = NetworkAccessMiddleware.ParseList("172.16.0.0/12");
    }
    foreach (var net in trusted)
    {
        o.KnownIPNetworks.Add(net);
    }
});

builder.Services.AddRazorComponents().AddInteractiveServerComponents();

var app = builder.Build();

await StartupTasks.InitializeAsync(app.Services);

app.UseForwardedHeaders();
app.UseMiddleware<NetworkAccessMiddleware>();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapHealthChecks("/health", new HealthCheckOptions
{
    ResultStatusCodes =
    {
        [Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Healthy] = StatusCodes.Status200OK,
        // Arrancando: el contenedor está bien, el juego todavía no.
        [Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Degraded] = StatusCodes.Status200OK,
        [Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Unhealthy] = StatusCodes.Status503ServiceUnavailable,
    },
    ResponseWriter = async (ctx, report) =>
    {
        ctx.Response.ContentType = "application/json";
        await JsonSerializer.SerializeAsync(ctx.Response.Body, new
        {
            status = report.Status.ToString(),
            checks = report.Entries.ToDictionary(e => e.Key, e => new
            {
                status = e.Value.Status.ToString(),
                description = e.Value.Description,
            }),
        });
    },
}).AllowAnonymous();

app.MapPost("/logout", async (SignInManager<PanelUser> signIn, HttpContext http, CubeKeeper.Infrastructure.Audit.AuditLog audit) =>
{
    await audit.RecordAsync(http.User.Identity?.Name ?? "?", "logout", http.Connection.RemoteIpAddress?.ToString());
    await signIn.SignOutAsync();
    return Results.LocalRedirect("/login");
});

// Descarga de copias (solo administradores).
app.MapGet("/copias/descargar/{file}", (string file, CubeKeeper.Infrastructure.Backups.BackupService backups) =>
    backups.ResolveExisting(file) is { } path
        ? Results.File(path, "application/octet-stream", file, enableRangeProcessing: true)
        : Results.NotFound()).RequireAuthorization("Admin");

// Descarga de un mundo como .mcworld (el activo se copia en caliente).
app.MapGet("/mundos/descargar/{name}", async (string name, CubeKeeper.Infrastructure.Worlds.WorldService worlds, CancellationToken ct) =>
{
    try
    {
        var (stream, fileName) = await worlds.ExportAsync(name, ct);
        return Results.File(stream, "application/octet-stream", fileName);
    }
    catch (Exception ex) when (ex is ArgumentException or FileNotFoundException)
    {
        return Results.NotFound();
    }
}).RequireAuthorization("Admin");

app.MapStaticAssets().AllowAnonymous();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

app.Run();
