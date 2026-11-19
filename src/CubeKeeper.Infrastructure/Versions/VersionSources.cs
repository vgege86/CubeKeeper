using CubeKeeper.Core.Abstractions;
using CubeKeeper.Core.Versions;
using CubeKeeper.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CubeKeeper.Infrastructure.Versions;

/// <summary>
/// Resuelve la última versión de BDS para Linux, igual que hace itzg/minecraft-bedrock-server:
/// 1) enlace directo configurado; 2) API oficial de minecraft-services; 3) índice de la comunidad (opcional).
/// </summary>
public sealed class VersionResolver(
    HttpClient http,
    IOptions<CubeKeeperOptions> options,
    ILogger<VersionResolver> logger) : IVersionSource
{
    public const string HttpClientName = "bds-versions";

    private readonly BdsOptions _options = options.Value.Bds;

    public async Task<BdsRelease?> GetLatestAsync(ReleaseChannel channel, CancellationToken ct = default)
    {
        var latest = await ResolveLatestAsync(channel, ct).ConfigureAwait(false);
        if (latest is null || string.IsNullOrWhiteSpace(_options.PinnedVersion) || !string.IsNullOrWhiteSpace(_options.DirectDownloadUrl))
        {
            return latest;
        }
        if (!BdsVersion.TryParse(_options.PinnedVersion, out var pinned) || pinned is null)
        {
            throw new InvalidOperationException($"CUBEKEEPER_BDS_VERSION no es una versión válida: '{_options.PinnedVersion}'.");
        }
        return latest.WithVersion(pinned);
    }

    private async Task<BdsRelease?> ResolveLatestAsync(ReleaseChannel channel, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(_options.DirectDownloadUrl))
        {
            var url = _options.DirectDownloadUrl!;
            var version = BdsVersion.FromDownloadUrl(url);
            if (version is null || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                throw new InvalidOperationException(
                    "DirectDownloadUrl debe ser un enlace https a un archivo bedrock-server-X.Y.Z.W.zip.");
            }
            return new BdsRelease(version, uri, channel, "enlace directo");
        }

        var official = await TryFetchAsync(_options.DownloadLinksUrl, json =>
            DownloadLinksParser.ParseMinecraftServices(json, channel), ct).ConfigureAwait(false);
        if (official is not null)
        {
            return official;
        }

        if (_options.UseCommunityIndexFallback)
        {
            logger.LogWarning("La API oficial no devolvió enlace; probando el índice de la comunidad.");
            return await TryFetchAsync(_options.CommunityIndexUrl, json =>
                DownloadLinksParser.ParseCommunityIndex(json, channel), ct).ConfigureAwait(false);
        }
        return null;
    }

    private async Task<BdsRelease?> TryFetchAsync(string url, Func<string, BdsRelease?> parse, CancellationToken ct)
    {
        try
        {
            using var resp = await http.GetAsync(url, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                logger.LogWarning("{Url} respondió {Status}", url, (int)resp.StatusCode);
                return null;
            }
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var release = parse(json);
            if (release is null)
            {
                logger.LogWarning("No se encontró un enlace de Linux válido en {Url}", url);
            }
            return release;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Error consultando {Url}", url);
            return null;
        }
    }
}
