namespace CubeKeeper.Web.Hosting;

/// <summary>
/// Cabeceras de seguridad para todas las respuestas. La política de contenido solo deja cargar scripts, estilos e
/// imágenes del propio panel y que solo el propio panel lo meta en un marco. El mapa (/mapa/ver) es HTML generado por
/// otro programa, con sus propios scripts en línea: ahí solo se limita quién puede enmarcarlo.
/// </summary>
public sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    public const string PanelPolicy =
        "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; " +
        "connect-src 'self'; frame-src 'self'; frame-ancestors 'self'; form-action 'self'; base-uri 'self'; object-src 'none'";

    public const string MapPolicy = "frame-ancestors 'self'; object-src 'none'; base-uri 'self'";

    public Task InvokeAsync(HttpContext context)
    {
        var isMap = context.Request.Path.StartsWithSegments("/mapa/ver", StringComparison.OrdinalIgnoreCase);
        context.Response.OnStarting(() =>
        {
            var h = context.Response.Headers;
            h["X-Content-Type-Options"] = "nosniff";
            h["X-Frame-Options"] = "SAMEORIGIN";
            h["Referrer-Policy"] = "same-origin";
            h["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=(), usb=()";
            h["Cross-Origin-Opener-Policy"] = "same-origin";
            h["Content-Security-Policy"] = isMap ? MapPolicy : PanelPolicy;
            return Task.CompletedTask;
        });
        return next(context);
    }
}
