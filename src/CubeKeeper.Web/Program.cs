using System.Text.Json;
using CubeKeeper.Infrastructure;
using CubeKeeper.Infrastructure.Configuration;
using CubeKeeper.Infrastructure.Persistence;
using CubeKeeper.Web.Components;
using CubeKeeper.Web.Hosting;
using Microsoft.AspNetCore.Authorization;
using CubeKeeper.Infrastructure.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
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

// Últimos mensajes del registro en memoria, para el diagnóstico descargable.
var recentLogs = new CubeKeeper.Infrastructure.Diagnostics.RecentLogs();
builder.Logging.AddProvider(recentLogs);
builder.Services.AddSingleton(recentLogs);

builder.Services.AddCubeKeeperInfrastructure(builder.Configuration);
builder.Services.AddSingleton<AdminSeedStatus>();

var dataPath = builder.Configuration["CubeKeeper:DataPath"] ?? "/data";
var keysDirectory = Path.Combine(dataPath, "keys");
// Claves de cookies, token de ntfy y sesión de Realms: cifradas con CUBEKEEPER_KEY_SECRET si está puesto.
var keySecret = new KeyEncryptionSecret(builder.Configuration["CubeKeeper:Security:KeySecret"]);
KeyFileMigration.VerifyReadable(keysDirectory, keySecret);
builder.Services.AddSingleton(keySecret);
builder.Services.AddDataProtection()
    .SetApplicationName("CubeKeeper")
    .PersistKeysToFileSystem(new DirectoryInfo(keysDirectory));
var encryptedKeyFiles = 0;
if (keySecret.IsSet)
{
    var encryptor = new SecretXmlEncryptor(keySecret);
    builder.Services.Configure<KeyManagementOptions>(o => o.XmlEncryptor = encryptor);
    encryptedKeyFiles = KeyFileMigration.EncryptExistingKeys(keysDirectory, encryptor);
}

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
    // Lax y no Strict: con Strict, el navegador no envía la cookie cuando el panel se abre desde fuera (la app instalada
    // en Android, un enlace, el aviso de ntfy) y pediría el login cada vez aunque se marque «Recordarme». Lax sigue sin
    // enviarla en formularios de otros sitios, y cada formulario del panel lleva además su token antifalsificación.
    o.Cookie.SameSite = SameSiteMode.Lax;
    o.LoginPath = "/login";
    o.AccessDeniedPath = "/sin-permiso";
    o.ExpireTimeSpan = TimeSpan.FromDays(7);
    o.SlidingExpiration = true;
});
// La cookie antifalsificación, también Lax (por defecto es Strict): si no, al abrir el login desde la app instalada
// no se enviaba ni se guardaba, y el primer intento de entrar fallaba siempre con «La página había caducado».
builder.Services.AddAntiforgery(o => o.Cookie.SameSite = SameSiteMode.Lax);
builder.Services.AddAuthorizationBuilder()
    .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build())
    .AddPolicy("Admin", p => p.RequireRole(PanelRoles.Admin));
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<Microsoft.AspNetCore.Components.Authorization.AuthenticationStateProvider, RevalidatingAuthStateProvider>();
// Las cookies también se revalidan cada minuto (contraseña, rol o segundo factor cambiados).
builder.Services.Configure<SecurityStampValidatorOptions>(o => o.ValidationInterval = TimeSpan.FromMinutes(1));
builder.Services.AddScoped<CubeKeeper.Infrastructure.Users.PanelUserService>();
builder.Services.AddSingleton<ConnectionInfoService>();

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
if (encryptedKeyFiles > 0)
{
    app.Logger.LogInformation("Cifradas {Count} claves de {Directory} que estaban sin cifrar", encryptedKeyFiles, keysDirectory);
}
else if (!keySecret.IsSet)
{
    app.Logger.LogWarning("Las claves de {Directory} no están cifradas: define CUBEKEEPER_KEY_SECRET", keysDirectory);
}

app.UseForwardedHeaders();
app.UseMiddleware<NetworkAccessMiddleware>();
app.UseMiddleware<SecurityHeadersMiddleware>();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

// Un formulario (login, verificación en dos pasos, Mi cuenta) enviado con la protección caducada: pasa en el móvil
// cuando el sistema cierra la app instalada y, al volver, restaura la página sin la cookie de sesión que la protege.
// En lugar de un 400 en blanco, se vuelve a cargar la página con un aviso (la protección sigue igual).
app.Use(async (ctx, next) =>
{
    if (HttpMethods.IsPost(ctx.Request.Method) &&
        ctx.Features.Get<Microsoft.AspNetCore.Antiforgery.IAntiforgeryValidationFeature>() is { IsValid: false })
    {
        var target = ctx.Request.Path.StartsWithSegments("/cuenta") ? "/cuenta?caducado=true" : "/login?caducado=true";
        var returnUrl = ctx.Request.Query["ReturnUrl"].ToString();
        if (target.StartsWith("/login", StringComparison.Ordinal) && returnUrl.StartsWith('/') &&
            !returnUrl.StartsWith("//", StringComparison.Ordinal) && !returnUrl.StartsWith("/\\", StringComparison.Ordinal))
        {
            target += "&ReturnUrl=" + Uri.EscapeDataString(returnUrl);
        }
        app.Logger.LogInformation("Formulario con la protección caducada en {Path}: se vuelve a cargar", ctx.Request.Path);
        ctx.Response.Redirect(target);
        return;
    }
    await next();
});

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
// Botones «Aprobar» y «Rechazar» del aviso de solicitud de acceso: los pulsa la app ntfy del móvil (POST). El enlace
// de un solo uso es la autorización; el filtro de redes (casa y VPN) sigue aplicándose.
app.MapPost("/acceso/aviso/{token}/{decision}", async (string token, string decision,
    CubeKeeper.Infrastructure.Access.AccessService access, CubeKeeper.Infrastructure.Audit.AuditLog audit,
    CubeKeeper.Infrastructure.Notifications.Notifier notifier, CancellationToken ct) =>
{
    if (decision is not ("aprobar" or "rechazar"))
    {
        return Results.NotFound();
    }
    try
    {
        var approve = decision == "aprobar";
        var result = await access.DecideFromNotificationAsync(token, approve, ct);
        await audit.RecordAsync("aviso del móvil", approve ? "access.approve" : "access.reject", result, "botón del aviso de ntfy", ct);
        await notifier.NotifyAsync(new CubeKeeper.Core.Notifications.Notification(
            CubeKeeper.Core.Notifications.NotificationKind.AccessRequest, "Acceso", result, "decision:" + token,
            CubeKeeper.Core.Notifications.NotificationPriority.Low, "/acceso"), ct);
        return Results.Text(result);
    }
    catch (InvalidOperationException ex)
    {
        return Results.Text(ex.Message, statusCode: StatusCodes.Status409Conflict);
    }
}).AllowAnonymous().DisableAntiforgery();

app.MapGet("/diagnostico/descargar", async (HttpContext http, CubeKeeper.Infrastructure.Diagnostics.DiagnosticsService diagnostics,
    CubeKeeper.Infrastructure.Audit.AuditLog audit, CancellationToken ct) =>
{
    var buffer = new MemoryStream();
    await diagnostics.WriteZipAsync(buffer, ct);
    buffer.Position = 0;
    await audit.RecordAsync(http.User.Identity?.Name ?? "?", "diagnostics.download", null, null, ct);
    return Results.File(buffer, "application/zip", diagnostics.FileName());
}).RequireAuthorization("Admin");

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

// Archivos del mapa generado (cualquier usuario con sesión).
var mapContentTypes = new Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider();
app.MapGet("/mapa/ver/{**path}", (string? path, CubeKeeper.Infrastructure.Maps.MapService maps) =>
{
    if (string.IsNullOrEmpty(path) || maps.ResolveMapFile(path) is not { } file)
    {
        return Results.NotFound();
    }
    var type = mapContentTypes.TryGetContentType(file, out var t) ? t : "application/octet-stream";
    return Results.File(file, type, enableRangeProcessing: true);
});

app.MapStaticAssets().AllowAnonymous();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

app.Run();
