using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CubeKeeper.Core.Notifications;
using CubeKeeper.Infrastructure.Configuration;
using CubeKeeper.Infrastructure.State;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CubeKeeper.Infrastructure.Notifications;

public sealed record SendResult(bool Sent, string Detail);

/// <summary>La configuración que se usa de verdad: la del formulario o, si no hay, la de las variables de entorno.</summary>
public sealed record NtfyConfig(string Url, string? Topic, bool HasToken, string? PanelUrl, bool FromPanel);

/// <summary>
/// Envía avisos a ntfy (publicación JSON, así los títulos con tildes llegan bien). Respeta los tipos activados y no repite
/// el mismo aviso (tipo + clave) en <see cref="Cooldown"/>. Nunca lanza: un aviso que falla no debe romper nada.
/// </summary>
public sealed partial class Notifier(
    HttpClient http,
    IOptions<CubeKeeperOptions> options,
    AppStateStore state,
    TimeProvider time,
    ILogger<Notifier> logger,
    IDataProtectionProvider? dataProtection = null)
{
    public const string HttpClientName = "ntfy";

    public static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(10);

    private readonly NotificationOptions _env = options.Value.Notifications;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastSent = new(StringComparer.Ordinal);
    private readonly IDataProtector? _protector = dataProtection?.CreateProtector("CubeKeeper.Ntfy.Token");

    /// <summary>Configuración efectiva.</summary>
    public NtfyConfig Config => state.Current.Ntfy is { } p
        ? new NtfyConfig(p.Url, p.Topic, !string.IsNullOrEmpty(p.ProtectedToken), p.PanelUrl, true)
        : new NtfyConfig(_env.NtfyUrl, _env.NtfyTopic, !string.IsNullOrWhiteSpace(_env.NtfyToken), _env.PanelUrl, false);

    public bool Configured => !string.IsNullOrWhiteSpace(Config.Topic);

    public string? TopicUrl => Configured ? $"{Config.Url.TrimEnd('/')}/{Config.Topic}" : null;

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex TopicRegex();

    /// <summary>
    /// Guarda la configuración del formulario. <paramref name="newToken"/>: null = dejar el que hubiera, vacío = quitarlo.
    /// </summary>
    public void SaveSettings(string url, string? topic, string? newToken, string? panelUrl)
    {
        url = string.IsNullOrWhiteSpace(url) ? "https://ntfy.sh" : url.Trim();
        if (!IsHttpUrl(url))
        {
            throw new ArgumentException("La dirección de ntfy debe empezar por http:// o https://.");
        }
        topic = string.IsNullOrWhiteSpace(topic) ? null : topic.Trim();
        if (topic is not null && !TopicRegex().IsMatch(topic))
        {
            throw new ArgumentException("El tema solo puede tener letras sin tildes, números, guiones y guiones bajos (hasta 64).");
        }
        panelUrl = string.IsNullOrWhiteSpace(panelUrl) ? null : panelUrl.Trim();
        if (panelUrl is not null && !IsHttpUrl(panelUrl))
        {
            throw new ArgumentException("La dirección del panel debe empezar por http:// o https://.");
        }

        string? protectedToken = state.Current.Ntfy?.ProtectedToken;
        if (newToken is not null)
        {
            if (newToken.Length == 0)
            {
                protectedToken = null;
            }
            else if (_protector is null)
            {
                throw new InvalidOperationException("No se puede guardar el token: falta la protección de datos.");
            }
            else
            {
                protectedToken = _protector.Protect(newToken.Trim());
            }
        }
        state.Update(s => s with { Ntfy = new NtfySettings(url, topic, protectedToken, panelUrl) });
        _lastSent.Clear();
    }

    /// <summary>Vuelve a usar las variables de entorno (borra lo guardado desde el panel).</summary>
    public void ClearSettings() => state.Update(s => s with { Ntfy = null });

    private string? Token
    {
        get
        {
            var saved = state.Current.Ntfy;
            if (saved is null)
            {
                return string.IsNullOrWhiteSpace(_env.NtfyToken) ? null : _env.NtfyToken;
            }
            if (string.IsNullOrEmpty(saved.ProtectedToken) || _protector is null)
            {
                return null;
            }
            try
            {
                return _protector.Unprotect(saved.ProtectedToken);
            }
            catch (System.Security.Cryptography.CryptographicException ex)
            {
                logger.LogWarning(ex, "No se pudo descifrar el token de ntfy (¿se han perdido las claves de /data/keys?)");
                return null;
            }
        }
    }

    private static bool IsHttpUrl(string s) =>
        Uri.TryCreate(s, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp);

    public NotificationKind Enabled => state.Current.Notifications;

    public void SetEnabled(NotificationKind kinds) => state.Update(s => s with { Notifications = kinds, KnownNotificationKinds = AllKinds });

    private static NotificationKind AllKinds => NotificationKindText.All.Aggregate(NotificationKind.None, (a, k) => a | k.Kind);

    /// <summary>
    /// Los tipos de aviso nuevos que van activados por defecto se activan también en instalaciones que ya tenían su
    /// elección guardada (una sola vez: si luego se desactivan, se respeta).
    /// </summary>
    public void EnableNewDefaultKinds()
    {
        // Antes de existir este campo se conocían los seis primeros tipos.
        const NotificationKind original = NotificationKind.AccessRequest | NotificationKind.ServerDown | NotificationKind.BackupFailed
            | NotificationKind.Update | NotificationKind.NewVersion | NotificationKind.PlayerJoin;
        var known = state.Current.KnownNotificationKinds ?? original;
        var added = NotificationKind.Defaults & ~known;
        if (added != NotificationKind.None || known != AllKinds)
        {
            state.Update(s => s with { Notifications = s.Notifications | added, KnownNotificationKinds = AllKinds });
        }
    }

    /// <summary>Envía el aviso si su tipo está activado y no se envió uno igual hace poco.</summary>
    public async Task<SendResult> NotifyAsync(Notification n, CancellationToken ct = default)
    {
        if (!Configured)
        {
            return new SendResult(false, "Sin tema de ntfy configurado.");
        }
        if (!Enabled.HasFlag(n.Kind))
        {
            return new SendResult(false, "Ese tipo de aviso está desactivado.");
        }
        var key = $"{n.Kind}:{n.Key}";
        var now = time.GetUtcNow();
        if (_lastSent.TryGetValue(key, out var last) && now - last < Cooldown)
        {
            return new SendResult(false, "Ya se avisó hace poco.");
        }
        _lastSent[key] = now;
        return await SendAsync(n.Title, n.Message, n.Priority, n.Path, ct).ConfigureAwait(false);
    }

    /// <summary>Mensaje de prueba desde el panel (sin filtros).</summary>
    public Task<SendResult> SendTestAsync(CancellationToken ct = default) =>
        Configured
            ? SendAsync("CubeKeeper: prueba", "Si ves esto, los avisos funcionan. 🎉", NotificationPriority.Default, "/avisos", ct)
            : Task.FromResult(new SendResult(false, "Falta el tema de ntfy: escríbelo en el formulario y guarda."));

    private async Task<SendResult> SendAsync(string title, string message, NotificationPriority priority, string? path, CancellationToken ct)
    {
        var config = Config;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, config.Url.TrimEnd('/') + "/");
            var body = new Dictionary<string, object>
            {
                ["topic"] = config.Topic!,
                ["title"] = title,
                ["message"] = message,
                ["priority"] = (int)priority,
                ["tags"] = new[] { "video_game" },
            };
            if (!string.IsNullOrWhiteSpace(config.PanelUrl))
            {
                body["click"] = config.PanelUrl.TrimEnd('/') + (path ?? "/");
            }
            // Cuerpo completo con Content-Length (y no por partes): lo aceptan también los proxies más estrictos.
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            if (Token is { } token)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }
            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var detail = $"ntfy respondió {(int)response.StatusCode} {response.ReasonPhrase}";
                logger.LogWarning("No se pudo enviar el aviso «{Title}»: {Detail}", title, detail);
                return new SendResult(false, detail);
            }
            logger.LogInformation("Aviso enviado: {Title}", title);
            return new SendResult(true, "Enviado.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "No se pudo enviar el aviso «{Title}»", title);
            return new SendResult(false, $"No se pudo conectar con ntfy: {ex.Message}");
        }
    }
}
