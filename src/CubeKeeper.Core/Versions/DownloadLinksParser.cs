using System.Text.Json;

namespace CubeKeeper.Core.Versions;

/// <summary>
/// Interpreta las respuestas de las fuentes de versiones:
/// <list type="bullet">
/// <item>API oficial: <c>https://net.web.minecraft-services.net/api/v1.0/download/links</c>,
/// con <c>{"result":{"links":[{"downloadType":"serverBedrockLinux","downloadUrl":"..."}]}}</c>.</item>
/// <item>Índice de la comunidad (kittizz/bedrock-server-downloads):
/// <c>{"release":{"1.26.0.2":{"linux":{"url":"..."}}},"preview":{...}}</c>.</item>
/// </list>
/// </summary>
public static class DownloadLinksParser
{
    public const string LinuxStableType = "serverBedrockLinux";
    public const string LinuxPreviewType = "serverBedrockPreviewLinux";

    public static BdsRelease? ParseMinecraftServices(string json, ReleaseChannel channel, string source = "minecraft-services")
    {
        var wanted = channel == ReleaseChannel.Preview ? LinuxPreviewType : LinuxStableType;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("result", out var result) ||
                !result.TryGetProperty("links", out var links) ||
                links.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (var link in links.EnumerateArray())
            {
                if (link.TryGetProperty("downloadType", out var type) &&
                    type.ValueKind == JsonValueKind.String &&
                    string.Equals(type.GetString(), wanted, StringComparison.Ordinal) &&
                    link.TryGetProperty("downloadUrl", out var urlEl) &&
                    urlEl.ValueKind == JsonValueKind.String)
                {
                    return Build(urlEl.GetString(), null, channel, source);
                }
            }
        }
        catch (JsonException)
        {
            return null;
        }
        return null;
    }

    public static BdsRelease? ParseCommunityIndex(string json, ReleaseChannel channel, string source = "community-index")
    {
        var section = channel == ReleaseChannel.Preview ? "preview" : "release";
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty(section, out var versions) || versions.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            BdsRelease? best = null;
            foreach (var prop in versions.EnumerateObject())
            {
                if (!BdsVersion.TryParse(prop.Name, out var version) ||
                    !prop.Value.TryGetProperty("linux", out var linux) ||
                    !linux.TryGetProperty("url", out var urlEl) ||
                    urlEl.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var candidate = Build(urlEl.GetString(), version, channel, source);
                if (candidate is not null && (best is null || candidate.Version > best.Version))
                {
                    best = candidate;
                }
            }
            return best;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static BdsRelease? Build(string? url, BdsVersion? version, ReleaseChannel channel, string source)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            return null;
        }
        version ??= BdsVersion.FromDownloadUrl(url!);
        return version is null ? null : new BdsRelease(version, uri, channel, source);
    }
}

public static class BdsReleaseExtensions
{
    /// <summary>
    /// Devuelve la misma publicación apuntando a otra versión, cambiando el número en el nombre del zip
    /// (es lo que hace itzg con VERSION=x.y.z.w). Mojang mantiene descargables las versiones anteriores.
    /// </summary>
    public static BdsRelease WithVersion(this BdsRelease release, BdsVersion version)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(version);
        var url = release.DownloadUrl.ToString();
        var current = $"bedrock-server-{release.Version.Text}.zip";
        if (!url.Contains(current, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"No se puede deducir el enlace de la versión {version} a partir de {url}.");
        }
        var replaced = url.Replace(current, $"bedrock-server-{version.Text}.zip", StringComparison.OrdinalIgnoreCase);
        return release with { Version = version, DownloadUrl = new Uri(replaced), Source = $"{release.Source}, versión fijada" };
    }
}
