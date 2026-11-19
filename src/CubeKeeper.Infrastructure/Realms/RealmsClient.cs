using System.Net;
using System.Text.Json.Nodes;
using CubeKeeper.Infrastructure.Configuration;

namespace CubeKeeper.Infrastructure.Realms;

/// <summary>Un Realm. <see cref="IsOwner"/> es null si no se puede saber (Realms decide al descargar).</summary>
public sealed record RealmInfo(long Id, string Name, string? Owner, string? State, int ActiveSlot, bool Expired, bool? IsOwner, IReadOnlyList<(int Slot, string Name)> Slots);

public sealed record RealmDownload(Uri Url, string? Token, long? Size);

/// <summary>Cliente de la API de Realms de Bedrock (pocket.realms.minecraft.net), como prismarine-realms.</summary>
public sealed class RealmsClient(HttpClient http, RealmsOptions options, Func<string> clientVersion)
{
    private const int MaxRetries = 4;

    public async Task<IReadOnlyList<RealmInfo>> GetRealmsAsync(XstsToken xsts, CancellationToken ct)
    {
        var json = await GetJsonAsync("/worlds", xsts, ct).ConfigureAwait(false);
        var list = new List<RealmInfo>();
        foreach (var s in json["servers"] as JsonArray ?? [])
        {
            if (s is null)
            {
                continue;
            }
            var slots = new List<(int, string)>();
            if (s["slots"] is JsonArray slotArray)
            {
                foreach (var slot in slotArray)
                {
                    var id = slot?["slotId"]?.GetValue<int>() ?? 0;
                    var options = slot?["options"]?.GetValue<string>();
                    string? name = null;
                    if (options is not null)
                    {
                        try
                        {
                            name = JsonNode.Parse(options)?["slotName"]?.GetValue<string>();
                        }
                        catch (System.Text.Json.JsonException)
                        {
                        }
                    }
                    if (id > 0)
                    {
                        slots.Add((id, string.IsNullOrWhiteSpace(name) ? $"Mundo {id}" : name));
                    }
                }
            }
            var activeSlot = s["activeSlot"]?.GetValue<int>() ?? 1;
            if (slots.Count == 0)
            {
                // Bedrock no siempre manda las ranuras; un Realm tiene tres.
                slots.AddRange(Enumerable.Range(1, Math.Max(3, activeSlot)).Select(i => (i, $"Mundo {i}")));
            }
            var ownerUuid = s["ownerUUID"]?.GetValue<string>();
            var owner = s["owner"]?.GetValue<string>();
            list.Add(new RealmInfo(
                s["id"]!.GetValue<long>(),
                s["name"]?.GetValue<string>() ?? "(sin nombre)",
                string.IsNullOrWhiteSpace(owner) ? null : owner,
                s["state"]?.GetValue<string>(),
                activeSlot,
                s["expired"]?.GetValue<bool>() ?? false,
                IsOwner(ownerUuid, owner, xsts),
                slots));
        }
        return list;
    }

    private static bool? IsOwner(string? ownerUuid, string? owner, XstsToken xsts)
    {
        if (!string.IsNullOrEmpty(ownerUuid) && xsts.Xuid is not null)
        {
            return string.Equals(ownerUuid, xsts.Xuid, StringComparison.OrdinalIgnoreCase);
        }
        if (!string.IsNullOrWhiteSpace(owner) && xsts.Gamertag is not null)
        {
            return string.Equals(owner, xsts.Gamertag, StringComparison.OrdinalIgnoreCase);
        }
        return null;
    }

    /// <summary>Enlace de descarga del mundo de una ranura tal como está ahora («latest»).</summary>
    public async Task<RealmDownload> GetDownloadAsync(XstsToken xsts, long realmId, int slot, CancellationToken ct)
    {
        var json = await GetJsonAsync($"/archive/download/world/{realmId}/{slot}/latest", xsts, ct).ConfigureAwait(false);
        var url = json["downloadUrl"]?.GetValue<string>() ?? json["downloadLink"]?.GetValue<string>()
                  ?? throw new RealmsAuthException("Realms no devolvió el enlace de descarga.");
        return new RealmDownload(new Uri(url), json["token"]?.GetValue<string>(), json["size"]?.GetValue<long>());
    }

    /// <summary>Abre la descarga del .mcworld.</summary>
    public async Task<HttpResponseMessage> OpenDownloadAsync(RealmDownload download, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, download.Url);
        if (!string.IsNullOrEmpty(download.Token))
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", download.Token);
        }
        var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var status = response.StatusCode;
            response.Dispose();
            throw new RealmsAuthException($"No se pudo descargar el mundo: {(int)status}.");
        }
        return response;
    }

    private async Task<JsonObject> GetJsonAsync(string route, XstsToken xsts, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, options.Host.TrimEnd('/') + route);
            request.Headers.TryAddWithoutValidation("Authorization", xsts.AuthorizationHeader);
            request.Headers.TryAddWithoutValidation("Client-Version", clientVersion());
            request.Headers.TryAddWithoutValidation("User-Agent", options.UserAgent);
            using var response = await SendAsync(request, ct).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                return JsonNode.Parse(text)?.AsObject() ?? throw new RealmsAuthException("Respuesta vacía de Realms.");
            }
            // La API de Realms falla a ratos con 5xx: se reintenta con espera creciente, como prismarine-realms.
            if ((int)response.StatusCode >= 500 && attempt < MaxRetries)
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), ct).ConfigureAwait(false);
                continue;
            }
            throw response.StatusCode switch
            {
                HttpStatusCode.Unauthorized => new RealmsAuthException("Realms no acepta la sesión. Vuelve a conectar la cuenta."),
                HttpStatusCode.Forbidden when route.StartsWith("/archive/", StringComparison.Ordinal) =>
                    new RealmsAuthException("Realms ha denegado la descarga: solo el dueño del Realm puede descargar sus mundos."),
                HttpStatusCode.Forbidden or HttpStatusCode.UpgradeRequired =>
                    new RealmsAuthException($"Realms ha rechazado la petición ({(int)response.StatusCode}). Puede que exija una versión del juego más reciente que la enviada ({clientVersion()}): ajusta CUBEKEEPER_REALMS_CLIENT_VERSION."),
                HttpStatusCode.NotFound => new RealmsAuthException("Realms no encuentra ese Realm o esa ranura."),
                _ => new RealmsAuthException($"Realms respondió {(int)response.StatusCode} {response.ReasonPhrase}."),
            };
        }
    }

    /// <summary>Las llamadas a la API (no las descargas) no deberían tardar más de un minuto.</summary>
    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(1));
        try
        {
            return await http.SendAsync(request, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new RealmsAuthException($"Sin respuesta de {request.RequestUri?.Host} en un minuto.");
        }
        catch (HttpRequestException ex)
        {
            throw new RealmsAuthException($"No se pudo conectar con {request.RequestUri?.Host}: {ex.Message}", ex);
        }
    }
}
