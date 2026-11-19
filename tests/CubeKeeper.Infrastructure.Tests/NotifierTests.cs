using System.Net;
using System.Text.Json;
using CubeKeeper.Core.Access;
using CubeKeeper.Core.Notifications;
using CubeKeeper.Core.Server;
using CubeKeeper.Infrastructure.Access;
using CubeKeeper.Infrastructure.Configuration;
using CubeKeeper.Infrastructure.Notifications;
using CubeKeeper.Infrastructure.Persistence;
using CubeKeeper.Infrastructure.Players;
using CubeKeeper.Infrastructure.State;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CubeKeeper.Infrastructure.Tests;

public sealed class NotifierTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ck-ntfy-" + Guid.NewGuid().ToString("N"));

    internal sealed class CaptureHandler(HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            lock (Requests)
            {
                Requests.Add((request, body));
            }
            return new HttpResponseMessage(status);
        }
    }

    internal static Notifier Create(string root, CaptureHandler handler, Action<NotificationOptions>? configure = null, TimeProvider? time = null)
    {
        var options = new CubeKeeperOptions { DataPath = root };
        options.Notifications.NtfyTopic = "cubekeeper-prueba";
        options.Notifications.PanelUrl = "https://cubekeeper.alucard86.fyi/";
        configure?.Invoke(options.Notifications);
        var state = new AppStateStore(new DataPaths(root));
        return new Notifier(new HttpClient(handler), Options.Create(options), state, time ?? TimeProvider.System, NullLogger<Notifier>.Instance);
    }

    private static Notification Sample(NotificationKind kind = NotificationKind.ServerDown, string key = "k") =>
        new(kind, "El servidor se ha caído", "Código 139 · reintentando", key, NotificationPriority.High, "/consola");

    [Fact]
    public async Task Publishes_json_with_title_priority_and_click_link()
    {
        var handler = new CaptureHandler();
        var notifier = Create(_root, handler, o => o.NtfyToken = "tk_secreto");

        var result = await notifier.NotifyAsync(Sample());

        Assert.True(result.Sent);
        var (request, body) = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://ntfy.sh/", request.RequestUri!.ToString());
        Assert.Equal("Bearer tk_secreto", request.Headers.Authorization!.ToString());
        using var json = JsonDocument.Parse(body);
        Assert.Equal("cubekeeper-prueba", json.RootElement.GetProperty("topic").GetString());
        Assert.Equal("El servidor se ha caído", json.RootElement.GetProperty("title").GetString());
        Assert.Equal(4, json.RootElement.GetProperty("priority").GetInt32());
        Assert.Equal("https://cubekeeper.alucard86.fyi/consola", json.RootElement.GetProperty("click").GetString());
    }

    [Fact]
    public async Task Respects_disabled_kinds_and_cooldown()
    {
        var handler = new CaptureHandler();
        var notifier = Create(_root, handler);

        Assert.False((await notifier.NotifyAsync(Sample(NotificationKind.PlayerJoin))).Sent); // desactivado por defecto
        Assert.True((await notifier.NotifyAsync(Sample(key: "a"))).Sent);
        Assert.False((await notifier.NotifyAsync(Sample(key: "a"))).Sent); // repetido
        Assert.True((await notifier.NotifyAsync(Sample(key: "b"))).Sent);  // otra clave

        notifier.SetEnabled(NotificationKind.Defaults & ~NotificationKind.ServerDown);
        Assert.False((await notifier.NotifyAsync(Sample(key: "c"))).Sent);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Without_topic_nothing_is_sent_and_failures_do_not_throw()
    {
        var handler = new CaptureHandler();
        var unconfigured = Create(_root, handler, o => o.NtfyTopic = null);
        Assert.False(unconfigured.Configured);
        Assert.False((await unconfigured.NotifyAsync(Sample())).Sent);
        Assert.False((await unconfigured.SendTestAsync()).Sent);
        Assert.Empty(handler.Requests);

        var failing = Create(_root, new CaptureHandler(HttpStatusCode.Forbidden));
        var result = await failing.SendTestAsync();
        Assert.False(result.Sent);
        Assert.Contains("403", result.Detail);
    }

    [Fact]
    public async Task Access_request_during_enrollment_sends_a_notification()
    {
        await using var f = new SupervisorFixture();
        var handler = new CaptureHandler();
        var notifier = Create(f.Root, handler);
        var factory = new DbFactory(f.Paths.Database);
        await using (var db = factory.CreateDbContext())
        {
            await db.Database.MigrateAsync();
        }
        var tracking = new PlayerTrackingService(f.Supervisor, f.Console, factory, TimeProvider.System, NullLogger<PlayerTrackingService>.Instance);
        var access = new AccessService(f.Supervisor, tracking, f.Paths, f.State, factory, TimeProvider.System, NullLogger<AccessService>.Instance, notifier);
        await tracking.StartAsync(CancellationToken.None);
        await access.StartAsync(CancellationToken.None);
        try
        {
            await f.Supervisor.StartAsync();
            await f.WaitForStatusAsync(ServerStatus.Running);
            await access.SetModeAsync(AccessMode.ApprovedOnly);
            await access.StartEnrollmentAsync(5);
            await f.Supervisor.SendCommandAsync("join Primo 2535400000000007");

            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (handler.Requests.Count == 0)
            {
                Assert.True(DateTime.UtcNow < deadline, "No llegó el aviso.");
                await Task.Delay(50);
            }
            Assert.Contains("Solicitud de acceso: Primo", handler.Requests[0].Body);
        }
        finally
        {
            await access.StopAsync(CancellationToken.None);
            await tracking.StopAsync(CancellationToken.None);
        }
    }

    private sealed class DbFactory(string path) : IDbContextFactory<CubeKeeperDbContext>
    {
        public CubeKeeperDbContext CreateDbContext() =>
            new(new DbContextOptionsBuilder<CubeKeeperDbContext>().UseSqlite($"Data Source={path}").Options);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
