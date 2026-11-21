using System.Net;
using System.Text;
using CubeKeeper.Infrastructure.Configuration;
using CubeKeeper.Infrastructure.Notifications;
using CubeKeeper.Infrastructure.State;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CubeKeeper.Infrastructure.Tests;

public sealed class BedrockConnectWatcherTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ck-bc-" + Guid.NewGuid().ToString("N"));

    private const string Tags = """
        {"count":30,"results":[{"name":"latest"},{"name":"1.72.1"},{"name":"dev"},{"name":"1.72.0"},{"name":"1.73"},{"name":"1.66.1"}]}
        """;

    private sealed class Hub(string json) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }

    [Fact]
    public void Picks_the_highest_version_tag() => Assert.Equal("1.73", BedrockConnectWatcher.Newest(Tags));

    [Fact]
    public async Task Notifies_once_when_a_newer_version_than_the_one_at_home_is_published()
    {
        var state = new AppStateStore(new DataPaths(_root));
        var ntfy = new NotifierTests.CaptureHandler();
        var notifier = NotifierTests.Create(_root, ntfy);
        var hub = new Hub(Tags);
        var watcher = new BedrockConnectWatcher(new HttpClient(hub), state, Options.Create(new CubeKeeperOptions { DataPath = _root }),
            TimeProvider.System, NullLogger<BedrockConnectWatcher>.Instance, notifier);

        Assert.Contains("no es válida", watcher.SetInstalledVersion("la última"));
        Assert.Null(watcher.SetInstalledVersion("1.72.1"));

        Assert.Equal("1.73", await watcher.CheckAsync());
        Assert.Equal("1.73", watcher.Watch.NewestSeen);
        var (_, body) = Assert.Single(ntfy.Requests);
        Assert.Contains("BedrockConnect 1.73 disponible", body);
        Assert.Contains("pugmatt/bedrock-connect:1.73", body);

        await watcher.CheckAsync(); // la misma versión: no se repite
        Assert.Single(ntfy.Requests);

        watcher.SetInstalledVersion("1.73");
        await watcher.CheckAsync();
        Assert.Single(ntfy.Requests);
    }

    [Fact]
    public async Task Without_a_version_at_home_nothing_is_notified()
    {
        var state = new AppStateStore(new DataPaths(_root));
        var ntfy = new NotifierTests.CaptureHandler();
        var watcher = new BedrockConnectWatcher(new HttpClient(new Hub(Tags)), state, Options.Create(new CubeKeeperOptions { DataPath = _root }),
            TimeProvider.System, NullLogger<BedrockConnectWatcher>.Instance, NotifierTests.Create(_root, ntfy));
        Assert.Equal("1.73", await watcher.CheckAsync());
        Assert.Empty(ntfy.Requests);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
