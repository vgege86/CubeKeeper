using System.IO.Compression;
using System.Net;
using CubeKeeper.Core.Versions;
using CubeKeeper.Infrastructure.Configuration;
using CubeKeeper.Infrastructure.State;
using CubeKeeper.Infrastructure.Versions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CubeKeeper.Infrastructure.Tests;

public class ServerInstallerDownloadTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ck-dl-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Install_downloads_extracts_merges_properties_and_records_version()
    {
        var paths = new DataPaths(_root);
        paths.EnsureCreated();
        File.WriteAllText(paths.ServerProperties, "server-port=19132\nallow-list=true\n");
        Directory.CreateDirectory(Path.Combine(paths.Server, "worlds", "Mi mundo"));
        File.WriteAllText(Path.Combine(paths.Server, "worlds", "Mi mundo", "level.dat"), "mundo");

        var state = new AppStateStore(paths);
        var installer = CreateInstaller(paths, state, BuildZip("server-port=19132\nallow-list=false\nnueva=1\n# comentario de nueva\n"), acceptEula: true);
        var release = new BdsRelease(BdsVersion.Parse("1.26.10.4"),
            new Uri("https://example.test/bin-linux/bedrock-server-1.26.10.4.zip"), ReleaseChannel.Stable, "test");

        var result = await installer.InstallAsync(release);

        Assert.Equal("1.26.10.4", result.Version);
        Assert.Equal(["nueva"], result.AddedProperties);
        Assert.Equal("1.26.10.4", state.Current.InstalledVersion);
        Assert.True(installer.IsInstalled);
        Assert.Equal("1.26.10.4", installer.InstalledVersion);
        var props = File.ReadAllText(paths.ServerProperties);
        Assert.Contains("allow-list=true", props);
        Assert.Contains("nueva=1\n# comentario de nueva", props);
        Assert.Equal("mundo", File.ReadAllText(Path.Combine(paths.Server, "worlds", "Mi mundo", "level.dat")));
        Assert.True(File.Exists(Path.Combine(paths.Versions, "1.26.10.4", "bedrock_server")));
        Assert.Empty(Directory.GetFiles(paths.Downloads));
        if (!OperatingSystem.IsWindows())
        {
            Assert.True(File.GetUnixFileMode(paths.ServerExecutable).HasFlag(UnixFileMode.UserExecute));
        }
    }

    [Fact]
    public async Task Fresh_install_applies_recommended_settings()
    {
        var paths = new DataPaths(_root);
        var installer = CreateInstaller(paths, new AppStateStore(paths), BuildZip("server-port=19132\ntransport=raknet\nallow-list=false\nlevel-name=Bedrock level\n"), acceptEula: true);
        var release = new BdsRelease(BdsVersion.Parse("1.26.65.1"), new Uri("https://example.test/bedrock-server-1.26.65.1.zip"), ReleaseChannel.Stable, "test");

        await installer.InstallAsync(release);

        var props = File.ReadAllText(paths.ServerProperties);
        Assert.Contains("transport=nethernet", props);
        Assert.Contains("server-udp-ports=19140", props);
        Assert.Contains("enable-lan-visibility=true", props);
        Assert.Contains("difficulty=normal", props);
    }

    [Fact]
    public async Task Install_without_eula_is_refused()
    {
        var paths = new DataPaths(_root);
        var installer = CreateInstaller(paths, new AppStateStore(paths), BuildZip("a=1\n"), acceptEula: false);
        var release = new BdsRelease(BdsVersion.Parse("1.26.10.4"), new Uri("https://example.test/bedrock-server-1.26.10.4.zip"), ReleaseChannel.Stable, "test");
        await Assert.ThrowsAsync<EulaNotAcceptedException>(() => installer.InstallAsync(release));
    }

    [Fact]
    public async Task Zip_without_linux_binary_is_rejected()
    {
        var paths = new DataPaths(_root);
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            zip.CreateEntry("bedrock_server.exe");
        }
        var installer = CreateInstaller(paths, new AppStateStore(paths), ms.ToArray(), acceptEula: true);
        var release = new BdsRelease(BdsVersion.Parse("1.26.10.4"), new Uri("https://example.test/bedrock-server-1.26.10.4.zip"), ReleaseChannel.Stable, "test");
        await Assert.ThrowsAsync<InvalidDataException>(() => installer.InstallAsync(release));
        Assert.False(Directory.Exists(Path.Combine(paths.Versions, "1.26.10.4")));
    }

    private static ServerInstaller CreateInstaller(DataPaths paths, AppStateStore state, byte[] zip, bool acceptEula)
    {
        var options = new CubeKeeperOptions { DataPath = paths.Root };
        options.Bds.AcceptEula = acceptEula;
        return new ServerInstaller(new HttpClient(new StaticHandler(zip)), paths, state, Options.Create(options), NullLogger<ServerInstaller>.Instance);
    }

    private static byte[] BuildZip(string properties)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add(zip, "bedrock_server", "#!/bin/sh\n");
            Add(zip, "server.properties", properties);
            Add(zip, "allowlist.json", "[]");
            Add(zip, "behavior_packs/vanilla/manifest.json", "{}");
        }
        return ms.ToArray();

        static void Add(ZipArchive zip, string name, string content)
        {
            using var w = new StreamWriter(zip.CreateEntry(name).Open());
            w.Write(content);
        }
    }

    private sealed class StaticHandler(byte[] body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
