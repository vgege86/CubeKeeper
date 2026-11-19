using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CubeKeeper.Core.Notifications;
using CubeKeeper.Infrastructure.Configuration;
using CubeKeeper.Infrastructure.State;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CubeKeeper.Infrastructure.Notifications;

public sealed record SendResult(bool Sent, string Detail);

/// <summary>
/// Envía avisos a ntfy (publicación JSON, así los títulos con tildes llegan bien). Respeta los tipos activados y no repite
/// el mismo aviso (tipo + clave) en <see cref="Cooldown"/>. Nunca lanza: un aviso que falla no debe romper nada.
/// </summary>
public sealed class Notifier(HttpClient http, IOptions<CubeKeeperOptions> options, AppStateStore state, TimeProvider time, ILogger<Notifier> logger)
{
    public const string HttpClientName = "ntfy";

    public static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(10);

    private readonly NotificationOptions _options = options.Value.Notifications;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastSent = new(StringComparer.Ordinal);

    public bool Configured => !string.IsNullOrWhiteSpace(_options.NtfyTopic);

    public string? TopicUrl => Configured ? $"{_options.NtfyUrl.TrimEnd('/')}/{_options.NtfyTopic}" : null;

    public NotificationKind Enabled => state.Current.Notifications;

    public void SetEnabled(NotificationKind kinds) => state.Update(s => s with { Notifications = kinds });

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
            : Task.FromResult(new SendResult(false, "Sin tema de ntfy configurado (CUBEKEEPER_NTFY_TOPIC)."));

    private async Task<SendResult> SendAsync(string title, string message, NotificationPriority priority, string? path, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, _options.NtfyUrl.TrimEnd('/') + "/");
            var body = new Dictionary<string, object>
            {
                ["topic"] = _options.NtfyTopic!,
                ["title"] = title,
                ["message"] = message,
                ["priority"] = (int)priority,
                ["tags"] = new[] { "video_game" },
            };
            if (!string.IsNullOrWhiteSpace(_options.PanelUrl))
            {
                body["click"] = _options.PanelUrl.TrimEnd('/') + (path ?? "/");
            }
            // Cuerpo completo con Content-Length (y no por partes): lo aceptan también los proxies más estrictos.
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            if (!string.IsNullOrWhiteSpace(_options.NtfyToken))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.NtfyToken);
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
