using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CubeKeeper.Core.Realms;
using CubeKeeper.Infrastructure.Configuration;

namespace CubeKeeper.Infrastructure.Realms;

/// <summary>Código para iniciar sesión en microsoft.com/link.</summary>
public sealed record DeviceCode(string UserCode, string VerificationUri, string DeviceCodeValue, TimeSpan Interval, DateTimeOffset ExpiresAt, string? Cookie);

public sealed record MsaToken(string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt);

public sealed record XstsToken(string UserHash, string Token, string? Xuid, string? Gamertag, DateTimeOffset ExpiresAt)
{
    public string AuthorizationHeader => $"XBL3.0 x={UserHash};{Token}";
}

/// <summary>Error de la cadena de inicio de sesión, con un mensaje en castellano para el panel.</summary>
public sealed class RealmsAuthException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Cadena de inicio de sesión: cuenta Microsoft (código por dispositivo de login.live.com) → token de usuario de Xbox →
/// token de dispositivo y de título (firmados) → XSTS para Realms. Igual que prismarine-auth con el flujo «live».
/// </summary>
public sealed class RealmsAuth(HttpClient http, RealmsOptions options, TimeProvider time)
{
    private static readonly Dictionary<long, string> XboxErrors = new()
    {
        [2148916227] = "Xbox ha bloqueado esta cuenta por incumplir sus normas.",
        [2148916229] = "La cuenta es de un menor y su tutor no le permite jugar en línea (account.microsoft.com/family).",
        [2148916233] = "La cuenta no tiene perfil de Xbox. Créalo en xbox.com e inténtalo de nuevo.",
        [2148916234] = "La cuenta no ha aceptado las condiciones de Xbox. Entra en xbox.com y acéptalas.",
        [2148916235] = "Xbox no está disponible en la región de la cuenta.",
        [2148916236] = "La cuenta necesita verificar la edad en login.live.com.",
        [2148916237] = "La cuenta ha llegado a su límite de tiempo de juego.",
        [2148916238] = "La cuenta es de un menor de 18 y tiene que estar en una familia de Microsoft con un adulto.",
    };

    public async Task<DeviceCode> StartDeviceCodeAsync(CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, options.DeviceCodeUrl)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["scope"] = options.Scope,
                ["client_id"] = options.ClientId,
                ["response_type"] = "device_code",
            }),
        };
        using var response = await SendAsync(request, ct).ConfigureAwait(false);
        var json = await ReadJsonAsync(response, "pedir el código de inicio de sesión", ct).ConfigureAwait(false);
        // login.live.com espera que la misma cookie acompañe a las consultas siguientes.
        var cookie = response.Headers.TryGetValues("Set-Cookie", out var cookies) ? cookies.FirstOrDefault()?.Split(';')[0] : null;
        return new DeviceCode(
            json["user_code"]!.GetValue<string>(),
            json["verification_uri"]?.GetValue<string>() ?? "https://www.microsoft.com/link",
            json["device_code"]!.GetValue<string>(),
            TimeSpan.FromSeconds(Math.Max(0.1, json["interval"]?.GetValue<double>() ?? 5)),
            time.GetUtcNow().AddSeconds(json["expires_in"]?.GetValue<int>() ?? 900),
            cookie);
    }

    /// <summary>Una consulta: el token si ya se ha iniciado sesión, o null si todavía está pendiente.</summary>
    public async Task<MsaToken?> PollDeviceCodeAsync(DeviceCode code, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{options.TokenUrl}?client_id={Uri.EscapeDataString(options.ClientId)}")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = options.ClientId,
                ["device_code"] = code.DeviceCodeValue,
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
            }),
        };
        if (code.Cookie is not null)
        {
            request.Headers.Add("Cookie", code.Cookie);
        }
        using var response = await SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var json = JsonNode.Parse(body)?.AsObject() ?? throw new RealmsAuthException("Respuesta vacía de login.live.com.");
        if (json["error"]?.GetValue<string>() is { } error)
        {
            return error switch
            {
                "authorization_pending" or "slow_down" => null,
                "expired_token" => throw new RealmsAuthException("El código ha caducado. Vuelve a empezar."),
                "authorization_declined" or "access_denied" => throw new RealmsAuthException("Se ha rechazado el inicio de sesión."),
                _ => throw new RealmsAuthException($"Error al iniciar sesión: {error} {json["error_description"]?.GetValue<string>()}"),
            };
        }
        return ToMsaToken(json);
    }

    public async Task<MsaToken> RefreshAsync(string refreshToken, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, options.TokenUrl)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["scope"] = options.Scope,
                ["client_id"] = options.ClientId,
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refreshToken,
            }),
        };
        using var response = await SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new RealmsAuthException("La sesión de Microsoft ha caducado o se ha revocado. Vuelve a conectar la cuenta.");
        }
        return ToMsaToken(await ReadJsonAsync(response, "renovar la sesión", ct).ConfigureAwait(false));
    }

    /// <summary>De token de Microsoft a XSTS para Realms (usuario + dispositivo + título).</summary>
    public async Task<XstsToken> GetXstsAsync(string msaAccessToken, XboxRequestSigner signer, CancellationToken ct)
    {
        var proofKey = signer.ProofKey();

        var user = await PostSignedAsync(options.UserAuthUrl, signer, new JsonObject
        {
            ["RelyingParty"] = "http://auth.xboxlive.com",
            ["TokenType"] = "JWT",
            ["Properties"] = new JsonObject
            {
                ["AuthMethod"] = "RPS",
                ["SiteName"] = "user.auth.xboxlive.com",
                ["RpsTicket"] = "t=" + msaAccessToken,
            },
        }, contractVersion: "2", "obtener el token de usuario de Xbox", ct).ConfigureAwait(false);

        var device = await PostSignedAsync(options.DeviceAuthUrl, signer, new JsonObject
        {
            ["Properties"] = new JsonObject
            {
                ["AuthMethod"] = "ProofOfPossession",
                ["Id"] = "{" + Guid.NewGuid() + "}",
                ["DeviceType"] = options.DeviceType,
                ["SerialNumber"] = "{" + Guid.NewGuid() + "}",
                ["Version"] = "0.0.0",
                ["ProofKey"] = proofKey.DeepClone(),
            },
            ["RelyingParty"] = "http://auth.xboxlive.com",
            ["TokenType"] = "JWT",
        }, contractVersion: "1", "obtener el token de dispositivo de Xbox", ct).ConfigureAwait(false);
        var deviceToken = device["Token"]!.GetValue<string>();

        var title = await PostSignedAsync(options.TitleAuthUrl, signer, new JsonObject
        {
            ["Properties"] = new JsonObject
            {
                ["AuthMethod"] = "RPS",
                ["DeviceToken"] = deviceToken,
                ["RpsTicket"] = "t=" + msaAccessToken,
                ["SiteName"] = "user.auth.xboxlive.com",
                ["ProofKey"] = proofKey.DeepClone(),
            },
            ["RelyingParty"] = "http://auth.xboxlive.com",
            ["TokenType"] = "JWT",
        }, contractVersion: "1", "obtener el token de título de Xbox", ct).ConfigureAwait(false);

        JsonObject XstsRequest(string relyingParty) => new()
        {
            ["RelyingParty"] = relyingParty,
            ["TokenType"] = "JWT",
            ["Properties"] = new JsonObject
            {
                ["UserTokens"] = new JsonArray(user["Token"]!.GetValue<string>()),
                ["DeviceToken"] = deviceToken,
                ["TitleToken"] = title["Token"]!.GetValue<string>(),
                ["ProofKey"] = proofKey.DeepClone(),
                ["SandboxId"] = "RETAIL",
            },
        };

        var xsts = await PostSignedAsync(options.XstsUrl, signer, XstsRequest(options.RelyingParty), contractVersion: "1",
            "obtener el token de Realms (XSTS)", ct).ConfigureAwait(false);
        var claims = xsts["DisplayClaims"]?["xui"]?[0];
        var uhs = claims?["uhs"]?.GetValue<string>() ?? throw new RealmsAuthException("Xbox no devolvió el identificador de usuario (uhs).");
        var xuid = claims?["xid"]?.GetValue<string>();
        var gamertag = claims?["gtg"]?.GetValue<string>();

        // El token de Realms solo trae «uhs». El XUID y el gamertag (para saber de qué Realms eres dueño) vienen en el
        // token de xboxlive.com. Si falla, se sigue sin ellos: Realms decide al descargar.
        if (xuid is null || gamertag is null)
        {
            try
            {
                var identity = await PostSignedAsync(options.XstsUrl, signer, XstsRequest(options.IdentityRelyingParty), contractVersion: "1",
                    "obtener el perfil de Xbox", ct).ConfigureAwait(false);
                var id = identity["DisplayClaims"]?["xui"]?[0];
                xuid ??= id?["xid"]?.GetValue<string>();
                gamertag ??= id?["gtg"]?.GetValue<string>();
            }
            catch (RealmsAuthException)
            {
            }
        }

        return new XstsToken(
            uhs,
            xsts["Token"]!.GetValue<string>(),
            xuid,
            gamertag,
            DateTimeOffset.TryParse(xsts["NotAfter"]?.GetValue<string>(), out var notAfter) ? notAfter : time.GetUtcNow().AddHours(1));
    }

    private async Task<JsonObject> PostSignedAsync(string url, XboxRequestSigner signer, JsonObject payload, string contractVersion, string what, CancellationToken ct)
    {
        var body = payload.ToJsonString();
        var uri = new Uri(url);
        using var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("x-xbl-contract-version", contractVersion);
        request.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true, NoCache = true, MustRevalidate = true };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Add("Signature", signer.Sign(uri, string.Empty, body, time.GetUtcNow()));

        using var response = await SendAsync(request, ct).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            long? xerr = null;
            try
            {
                xerr = JsonNode.Parse(text)?["XErr"]?.GetValue<long>();
            }
            catch (JsonException)
            {
            }
            if (xerr is long code && XboxErrors.TryGetValue(code, out var known))
            {
                throw new RealmsAuthException(known);
            }
            throw new RealmsAuthException($"Xbox Live rechazó la petición al {what} ({(int)response.StatusCode}{(xerr is null ? "" : $", XErr {xerr}")}).");
        }
        return JsonNode.Parse(text)?.AsObject() ?? throw new RealmsAuthException($"Respuesta vacía al {what}.");
    }

    private MsaToken ToMsaToken(JsonObject json) => new(
        json["access_token"]!.GetValue<string>(),
        json["refresh_token"]!.GetValue<string>(),
        time.GetUtcNow().AddSeconds(json["expires_in"]?.GetValue<int>() ?? 3600));

    private static async Task<JsonObject> ReadJsonAsync(HttpResponseMessage response, string what, CancellationToken ct)
    {
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new RealmsAuthException($"No se pudo {what}: {(int)response.StatusCode} {response.ReasonPhrase}.");
        }
        try
        {
            return JsonNode.Parse(text)?.AsObject() ?? throw new RealmsAuthException($"Respuesta vacía al {what}.");
        }
        catch (JsonException ex)
        {
            throw new RealmsAuthException($"Respuesta inesperada al {what}.", ex);
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
