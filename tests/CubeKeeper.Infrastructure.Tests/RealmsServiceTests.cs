using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using CubeKeeper.Core.Realms;
using CubeKeeper.Infrastructure.Backups;
using CubeKeeper.Infrastructure.Configuration;
using CubeKeeper.Infrastructure.Process;
using CubeKeeper.Infrastructure.Realms;
using CubeKeeper.Infrastructure.State;
using CubeKeeper.Infrastructure.Worlds;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CubeKeeper.Infrastructure.Tests;

public class RealmsServiceTests
{
    /// <summary>Simula login.live.com, Xbox Live y Realms comprobando lo que de verdad miran (firma, cookie, cabeceras).</summary>
    private sealed class FakeMicrosoft : HttpMessageHandler
    {
        public int PendingPolls { get; set; } = 1;

        public string? XErr { get; set; }

        public List<string> Log { get; } = [];

        public List<string> Problems { get; } = [];

        public int RealmsFailures { get; set; }

        public int Refreshes { get; private set; }

        public byte[] World { get; set; } = [];

        public bool IdentityFails { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            var url = request.RequestUri!;
            lock (Log)
            {
                Log.Add(url.Host + url.AbsolutePath);
            }
            switch (url.Host + url.AbsolutePath)
            {
                case "login.live.com/oauth20_connect.srf":
                    Expect(body.Contains("response_type=device_code") && body.Contains("client_id=00000000441cc96b"), "petición de código");
                    var start = Json("""{"user_code":"ABCD1234","device_code":"dc-1","verification_uri":"https://www.microsoft.com/link","interval":0.1,"expires_in":60}""");
                    start.Headers.Add("Set-Cookie", "MSPOK=galleta; path=/; secure");
                    return start;
                case "login.live.com/oauth20_token.srf":
                    if (body.Contains("grant_type=refresh_token"))
                    {
                        Refreshes++;
                        Expect(body.Contains("refresh_token=rt-" + Refreshes), "token de renovación vigente");
                        return Json($$"""{"access_token":"at-r{{Refreshes}}","refresh_token":"rt-{{Refreshes + 1}}","expires_in":3600}""");
                    }
                    Expect(request.Headers.TryGetValues("Cookie", out var c) && c.Single() == "MSPOK=galleta", "cookie del código");
                    Expect(body.Contains("device_code=dc-1"), "device_code");
                    if (PendingPolls-- > 0)
                    {
                        return Json("""{"error":"authorization_pending"}""", HttpStatusCode.BadRequest);
                    }
                    return Json("""{"access_token":"at-1","refresh_token":"rt-1","expires_in":3600}""");
                case "user.auth.xboxlive.com/user/authenticate":
                    CheckSigned(request, body, "2");
                    Expect(JsonNode.Parse(body)!["Properties"]!["RpsTicket"]!.GetValue<string>().StartsWith("t=at-"), "RpsTicket");
                    return Json("""{"Token":"user-token","DisplayClaims":{"xui":[{"uhs":"uhs-1"}]}}""");
                case "device.auth.xboxlive.com/device/authenticate":
                    CheckSigned(request, body, "1");
                    Expect(JsonNode.Parse(body)!["Properties"]!["DeviceType"]!.GetValue<string>() == "Nintendo", "DeviceType");
                    return Json("""{"Token":"device-token"}""");
                case "title.auth.xboxlive.com/title/authenticate":
                    CheckSigned(request, body, "1");
                    Expect(JsonNode.Parse(body)!["Properties"]!["DeviceToken"]!.GetValue<string>() == "device-token", "DeviceToken en título");
                    return Json("""{"Token":"title-token"}""");
                case "xsts.auth.xboxlive.com/xsts/authorize":
                    CheckSigned(request, body, "1");
                    var p = JsonNode.Parse(body)!;
                    Expect(p["Properties"]!["TitleToken"]!.GetValue<string>() == "title-token", "TitleToken");
                    if (XErr is not null)
                    {
                        return Json($$"""{"XErr":{{XErr}},"Message":""}""", HttpStatusCode.Unauthorized);
                    }
                    // Como el de verdad: el token de Realms solo trae uhs; el de xboxlive.com, el perfil.
                    return p["RelyingParty"]!.GetValue<string>() switch
                    {
                        "https://pocket.realms.minecraft.net/" => Json("""{"Token":"xsts-token","NotAfter":"2099-01-01T00:00:00Z","DisplayClaims":{"xui":[{"uhs":"uhs-1"}]}}"""),
                        "http://xboxlive.com" when !IdentityFails => Json("""{"Token":"xbl-token","DisplayClaims":{"xui":[{"uhs":"uhs-1","xid":"2535400000000001","gtg":"Papa86"}]}}"""),
                        "http://xboxlive.com" => Json("""{"XErr":2148916238}""", HttpStatusCode.Unauthorized),
                        var other => Json($$"""{"error":"RelyingParty {{other}}"}""", HttpStatusCode.BadRequest),
                    };
                case "pocket.realms.minecraft.net/worlds":
                    CheckRealms(request);
                    if (RealmsFailures-- > 0)
                    {
                        return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                    }
                    return Json("""
                        {"servers":[{"id":42,"name":"Casa","owner":"Papa86","ownerUUID":"2535400000000001","state":"OPEN","activeSlot":2,"expired":false,
                          "slots":[{"slotId":1,"options":"{\"slotName\":\"Supervivencia\"}"},{"slotId":2,"options":"{}"}]},
                         {"id":7,"name":"De un amigo","owner":"Otro","ownerUUID":"999","state":"CLOSED","activeSlot":1,"expired":true},
                         {"id":8,"name":"My Realm","owner":"","ownerUUID":"2535400000000001","state":"OPEN","activeSlot":3,"expired":false}]}
                        """);
                case "pocket.realms.minecraft.net/archive/download/world/42/1/latest":
                    CheckRealms(request);
                    return Json("""{"downloadUrl":"https://cdn.realms.test/mundo.mcworld","token":"dl-token","size":123}""");
                case "cdn.realms.test/mundo.mcworld":
                    Expect(request.Headers.Authorization?.ToString() == "Bearer dl-token", "token de descarga");
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(World) };
                default:
                    Problems.Add("URL inesperada " + url);
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
            }
        }

        private void CheckSigned(HttpRequestMessage request, string body, string contract)
        {
            Expect(request.Headers.GetValues("x-xbl-contract-version").Single() == contract, "contract-version " + contract);
            var jwk = JsonNode.Parse(body)!["Properties"]!["ProofKey"];
            if (jwk is null)
            {
                // La petición de usuario no lleva ProofKey: solo se comprueba que haya firma.
                Expect(request.Headers.Contains("Signature"), "firma");
                return;
            }
            using var key = System.Security.Cryptography.ECDsa.Create(new System.Security.Cryptography.ECParameters
            {
                Curve = System.Security.Cryptography.ECCurve.NamedCurves.nistP256,
                Q = new() { X = Base64Url(jwk["x"]!.GetValue<string>()), Y = Base64Url(jwk["y"]!.GetValue<string>()) },
            });
            using var verifier = new XboxRequestSigner(key);
            Expect(verifier.Verify(request.RequestUri!, "", body, request.Headers.GetValues("Signature").Single()), "firma válida en " + request.RequestUri!.Host);
        }

        private void CheckRealms(HttpRequestMessage request)
        {
            Expect(request.Headers.GetValues("Authorization").Single() == "XBL3.0 x=uhs-1;xsts-token", "Authorization XBL3.0");
            Expect(request.Headers.GetValues("Client-Version").Single() == "1.26.65", "Client-Version");
            Expect(request.Headers.GetValues("User-Agent").Single() == "MCPE/UWP", "User-Agent");
        }

        private void Expect(bool ok, string what)
        {
            if (!ok)
            {
                lock (Problems)
                {
                    Problems.Add(what);
                }
            }
        }

        private static byte[] Base64Url(string s) => Convert.FromBase64String(s.Replace('-', '+').Replace('_', '/') + new string('=', (4 - s.Length % 4) % 4));

        private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
            new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }

    private sealed record Setup(SupervisorFixture Fixture, FakeMicrosoft Fake, RealmsService Realms, AppStateStore State, WorldService Worlds) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Realms.Dispose();
            await Fixture.DisposeAsync();
        }
    }

    private static Setup Create(IDataProtectionProvider? protection = null, SupervisorFixture? fixture = null)
    {
        var f = fixture ?? new SupervisorFixture();
        var options = Options.Create(new CubeKeeperOptions { DataPath = f.Root });
        var state = new AppStateStore(f.Paths);
        state.Update(s => s with { InstalledVersion = "1.26.65.1" });
        var backups = new BackupService(f.Supervisor, new ConsoleCommandRunner(f.Supervisor, f.Console), f.Paths, options, TimeProvider.System, NullLogger<BackupService>.Instance);
        var worlds = new WorldService(f.Supervisor, backups, f.Paths, TimeProvider.System, NullLogger<WorldService>.Instance);
        var fake = new FakeMicrosoft();
        var realms = new RealmsService(new HttpClient(fake), options, state, worlds, TimeProvider.System,
            protection ?? new EphemeralDataProtectionProvider(), NullLogger<RealmsService>.Instance);
        return new Setup(f, fake, realms, state, worlds);
    }

    private static async Task ConnectAsync(RealmsService realms)
    {
        var code = await realms.StartLoginAsync();
        Assert.Equal("ABCD1234", code.UserCode);
        Assert.Equal(RealmsStatus.WaitingForCode, realms.View.Status);
        await WaitUntilAsync(() => realms.View.Status != RealmsStatus.WaitingForCode);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < until, "tiempo de espera agotado");
            await Task.Delay(50);
        }
    }

    private static byte[] WorldZip()
    {
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, content) in new[] { ("level.dat", "nivel"), ("levelname.txt", "Casa Realm"), ("db/CURRENT", "x") })
            {
                using var w = new StreamWriter(zip.CreateEntry(path).Open());
                w.Write(content);
            }
        }
        return ms.ToArray();
    }

    [Fact]
    public async Task Device_code_login_runs_the_signed_xbox_chain_and_stores_an_encrypted_session()
    {
        await using var s = Create();
        await ConnectAsync(s.Realms);

        Assert.Empty(s.Fake.Problems);
        var view = s.Realms.View;
        Assert.Equal(RealmsStatus.Connected, view.Status);
        Assert.Equal("Papa86", view.Gamertag);
        Assert.Null(view.LastError);
        var session = s.State.Current.Realms!;
        Assert.Equal("2535400000000001", session.Xuid);
        var stateFile = await File.ReadAllTextAsync(s.Fixture.Paths.StateFile);
        Assert.DoesNotContain("rt-1", stateFile);
        Assert.Contains("ProtectedRefreshToken", stateFile);
        Assert.Equal(
            ["login.live.com/oauth20_connect.srf", "login.live.com/oauth20_token.srf", "login.live.com/oauth20_token.srf",
             "user.auth.xboxlive.com/user/authenticate", "device.auth.xboxlive.com/device/authenticate",
             "title.auth.xboxlive.com/title/authenticate", "xsts.auth.xboxlive.com/xsts/authorize", "xsts.auth.xboxlive.com/xsts/authorize"],
            s.Fake.Log);
    }

    [Fact]
    public async Task Lists_realms_with_slots_ownership_and_retries_server_errors()
    {
        await using var s = Create();
        await ConnectAsync(s.Realms);
        s.Fake.RealmsFailures = 1;

        var list = await s.Realms.ListAsync();

        Assert.Empty(s.Fake.Problems);
        Assert.Equal(3, list.Count);
        var casa = list[0];
        Assert.Equal(42, casa.Id);
        Assert.True(casa.IsOwner);
        Assert.Equal(2, casa.ActiveSlot);
        Assert.Equal([(1, "Supervivencia"), (2, "Mundo 2"), (3, "Mundo 3")], casa.Slots);
        Assert.False(list[1].IsOwner);
        Assert.True(list[1].Expired);
        // Sin nombre de dueño ni ranuras (como lo manda Bedrock): se reconoce por el XUID y se ofrecen las tres.
        var mine = list[2];
        Assert.True(mine.IsOwner);
        Assert.Null(mine.Owner);
        Assert.Equal(3, mine.ActiveSlot);
        Assert.Equal([1, 2, 3], mine.Slots.Select(x => x.Slot));
        Assert.Equal(2, s.Fake.Log.Count(l => l.EndsWith("/worlds")));
    }

    [Fact]
    public async Task Imports_a_slot_as_a_world_and_can_activate_it()
    {
        await using var s = Create();
        await ConnectAsync(s.Realms);
        s.Fake.World = WorldZip();
        var casa = (await s.Realms.ListAsync())[0];
        long last = 0;

        var name = await s.Realms.ImportAsync(casa, 1, null, activate: true, new SyncProgress(b => last = b));

        Assert.Empty(s.Fake.Problems);
        Assert.Equal("Casa Realm", name);
        Assert.Equal(s.Fake.World.Length, last);
        Assert.True(File.Exists(Path.Combine(s.Worlds.WorldsDirectory, "Casa Realm", "db", "CURRENT")));
        Assert.Contains(await s.Worlds.ListAsync(), w => w.Name == "Casa Realm" && w.IsActive);
    }

    [Fact]
    public async Task After_a_restart_the_session_is_renewed_with_the_refresh_token()
    {
        var protection = new EphemeralDataProtectionProvider();
        var f = new SupervisorFixture();
        await using (var first = Create(protection, f))
        {
            await ConnectAsync(first.Realms);
            first.Realms.Dispose();

            // Otro RealmsService (como tras reiniciar el contenedor) con el mismo state.json y las mismas claves.
            var options = Options.Create(new CubeKeeperOptions { DataPath = f.Root });
            var fake = new FakeMicrosoft();
            using var second = new RealmsService(new HttpClient(fake), options, new AppStateStore(f.Paths), first.Worlds, TimeProvider.System,
                protection, NullLogger<RealmsService>.Instance);

            Assert.Equal(RealmsStatus.Connected, second.View.Status);
            Assert.Equal(3, (await second.ListAsync()).Count);
            Assert.Empty(fake.Problems);
            Assert.Equal(1, fake.Refreshes);
            // Se ha guardado el token de renovación nuevo, y la siguiente consulta reutiliza el XSTS en memoria.
            await second.ListAsync();
            Assert.Equal(1, fake.Log.Count(l => l.Contains("user.auth")));
        }
    }

    [Fact]
    public async Task Without_a_profile_ownership_is_unknown_and_import_is_left_to_realms()
    {
        await using var s = Create();
        s.Fake.IdentityFails = true;
        await ConnectAsync(s.Realms);

        Assert.Equal(RealmsStatus.Connected, s.Realms.View.Status);
        Assert.Null(s.Realms.View.Gamertag);
        var list = await s.Realms.ListAsync();
        Assert.Null(list[2].IsOwner);
        // Con nombre de dueño pero sin gamertag propio tampoco se puede saber.
        Assert.Null(list[1].IsOwner);
        Assert.Empty(s.Fake.Problems);
    }

    [Fact]
    public async Task Xbox_errors_are_explained_in_spanish()
    {
        await using var s = Create();
        s.Fake.XErr = "2148916233";

        await ConnectAsync(s.Realms);

        Assert.Equal(RealmsStatus.Disconnected, s.Realms.View.Status);
        Assert.Contains("no tiene perfil de Xbox", s.Realms.View.LastError);
        Assert.Null(s.State.Current.Realms);
    }

    [Fact]
    public async Task Disconnect_forgets_the_session_and_cancel_stops_waiting()
    {
        await using var s = Create();
        s.Fake.PendingPolls = int.MaxValue;
        await s.Realms.StartLoginAsync();
        s.Realms.CancelLogin();
        Assert.Equal(RealmsStatus.Disconnected, s.Realms.View.Status);

        s.Fake.PendingPolls = 0;
        await ConnectAsync(s.Realms);
        Assert.Equal(RealmsStatus.Connected, s.Realms.View.Status);

        s.Realms.Disconnect();
        Assert.Equal(RealmsStatus.Disconnected, s.Realms.View.Status);
        Assert.Null(s.State.Current.Realms);
        await Assert.ThrowsAsync<RealmsAuthException>(() => s.Realms.ListAsync());
    }

    [Theory]
    [InlineData("1.26.65.1", "1.26.65")]
    [InlineData("1.21.0", "1.21.0")]
    [InlineData(null, "0.0.0")]
    public void Client_version_uses_three_numbers(string? installed, string expected) =>
        Assert.Equal(expected, RealmsService.ShortVersion(installed));

    private sealed class SyncProgress(Action<long> report) : IProgress<long>
    {
        public void Report(long value) => report(value);
    }
}
