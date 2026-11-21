using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using CubeKeeper.Core.Notifications;
using CubeKeeper.Infrastructure.Configuration;
using CubeKeeper.Infrastructure.State;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CubeKeeper.Infrastructure.Notifications;

/// <summary>
/// Mira cada 6 horas si hay una versión nueva de la imagen de BedrockConnect (pugmatt/bedrock-connect en Docker Hub)
/// y avisa una vez por versión. Solo si se ha dicho en Avisos qué versión hay en casa: CubeKeeper no puede verla.
/// </summary>
public sealed partial class BedrockConnectWatcher(
    HttpClient http,
    AppStateStore state,
    IOptions<CubeKeeperOptions> options,
    TimeProvider time,
    ILogger<BedrockConnectWatcher> logger,
    Notifier? notifier = null) : BackgroundService
{
    public const string HttpClientName = "bedrockconnect";
    public static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    public BedrockConnectWatch Watch => state.Current.BedrockConnect;

    /// <summary>Guarda la versión que hay en casa (vacía = no se vigila). Devuelve un error o null.</summary>
    public string? SetInstalledVersion(string? version)
    {
        var v = version?.Trim();
        if (!string.IsNullOrEmpty(v) && !TryParse(v, out _))
        {
            return "La versión no es válida: escríbela como en la línea image: del YAML, por ejemplo 1.72.1.";
        }
        state.Update(s => s with { BedrockConnect = s.BedrockConnect with { InstalledVersion = string.IsNullOrEmpty(v) ? null : v } });
        return null;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(3), time, stoppingToken).ConfigureAwait(false);
            while (!stoppingToken.IsCancellationRequested)
            {
                if (Watch.InstalledVersion is not null)
                {
                    await CheckAsync(stoppingToken).ConfigureAwait(false);
                }
                await Task.Delay(Interval, time, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Consulta Docker Hub y avisa si hay una más nueva que la de casa. Devuelve la última publicada.</summary>
    public async Task<string?> CheckAsync(CancellationToken ct = default)
    {
        string? newest;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            using var response = await http.GetAsync(options.Value.BedrockConnect.TagsUrl, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Docker Hub respondió {Status} al mirar las versiones de BedrockConnect", (int)response.StatusCode);
                return Watch.NewestSeen;
            }
            newest = Newest(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException && !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "No se pudieron mirar las versiones de BedrockConnect");
            return Watch.NewestSeen;
        }
        state.Update(s => s with { BedrockConnect = s.BedrockConnect with { NewestSeen = newest, CheckedAt = time.GetUtcNow() } });

        var watch = Watch;
        if (newest is null || watch.InstalledVersion is not { } installed || !TryParse(newest, out var n) || !TryParse(installed, out var i) ||
            n <= i || newest == watch.LastNotifiedVersion || notifier is null)
        {
            return newest;
        }
        var result = await notifier.NotifyAsync(new Notification(
            NotificationKind.NewVersion,
            $"BedrockConnect {newest} disponible",
            $"Tienes la {installed}. Cambia la línea image: de la app bedrockconnect en TrueNAS a pugmatt/bedrock-connect:{newest}. " +
            "Si la Xbox se ha actualizado, instala también la versión nueva de BDS desde el Panel. Luego pon la nueva versión en Avisos.",
            "bedrockconnect:" + newest,
            NotificationPriority.Default,
            "/avisos"), ct).ConfigureAwait(false);
        if (result.Sent)
        {
            state.Update(s => s with { BedrockConnect = s.BedrockConnect with { LastNotifiedVersion = newest } });
        }
        return newest;
    }

    /// <summary>La versión más alta de la lista de etiquetas de Docker Hub (sin latest, dev…).</summary>
    internal static string? Newest(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
        {
            return null;
        }
        return results.EnumerateArray()
            .Select(r => r.TryGetProperty("name", out var name) ? name.GetString() : null)
            .Where(n => n is not null && VersionTag().IsMatch(n))
            .Select(n => (Text: n!, Version: TryParse(n!, out var v) ? v : null))
            .Where(t => t.Version is not null)
            .OrderByDescending(t => t.Version)
            .Select(t => t.Text)
            .FirstOrDefault();
    }

    internal static bool TryParse(string text, out Version? version)
    {
        // «1.66» y «1.66.0» son la misma: se rellena hasta tres números.
        var parts = text.Split('.');
        var padded = parts.Length >= 3 ? text : string.Join('.', parts.Concat(Enumerable.Repeat("0", 3 - parts.Length)));
        return Version.TryParse(padded, out version) && VersionTag().IsMatch(text);
    }

    [GeneratedRegex(@"^\d+(\.\d+){1,3}$", RegexOptions.CultureInvariant)]
    private static partial Regex VersionTag();
}
