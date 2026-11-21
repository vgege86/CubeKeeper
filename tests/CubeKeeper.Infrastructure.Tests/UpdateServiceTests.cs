using CubeKeeper.Core.Abstractions;
using CubeKeeper.Core.Logs;
using CubeKeeper.Core.Players;
using CubeKeeper.Core.Server;
using CubeKeeper.Core.Versions;
using CubeKeeper.Infrastructure.Backups;
using CubeKeeper.Infrastructure.Configuration;
using CubeKeeper.Infrastructure.Process;
using CubeKeeper.Infrastructure.State;
using CubeKeeper.Infrastructure.Updates;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CubeKeeper.Infrastructure.Tests;

public class UpdateServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ck-upd-" + Guid.NewGuid().ToString("N"));

    /// <summary>Servidor simulado: "arranca" bien con cualquier versión salvo las de <see cref="Broken"/>.</summary>
    private sealed class StubServer(StubInstaller installer) : IServerController
    {
        public HashSet<string> Broken { get; } = [];
        public List<string> Installed { get; } = [];
        public ServerSnapshot Snapshot { get; private set; } = new(ServerStatus.Running, "1.0.0.0", "1.0.0.0", null, 1, null);
        public event Action<ServerSnapshot>? StateChanged;
        public event Action<BdsLogEvent>? LogEvent;

        public Task StartAsync(CancellationToken ct = default)
        {
            Snapshot = Snapshot with { Status = Broken.Contains(installer.InstalledVersion!) ? ServerStatus.Failed : ServerStatus.Running };
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken ct = default)
        {
            Snapshot = Snapshot with { Status = ServerStatus.Stopped };
            return Task.CompletedTask;
        }

        public Task RestartAsync(CancellationToken ct = default) => StartAsync(ct);

        public Task SendCommandAsync(string command, CancellationToken ct = default) => Task.CompletedTask;

        public async Task<InstallResult> InstallAsync(BdsRelease release, IProgress<string>? progress = null, CancellationToken ct = default)
        {
            Installed.Add(release.Version.Text);
            var r = await installer.InstallAsync(release, progress, ct);
            await StartAsync(ct);
            _ = StateChanged;
            _ = LogEvent;
            return r;
        }
    }

    private sealed class StubInstaller(DataPaths paths) : IServerInstaller
    {
        public string? InstalledVersion { get; private set; } = "1.0.0.0";
        public bool IsInstalled => true;

        public Task<InstallResult> InstallAsync(BdsRelease release, IProgress<string>? progress = null, CancellationToken ct = default)
        {
            var dir = Path.Combine(paths.Versions, release.Version.Text);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "bedrock_server"), release.Version.Text);
            InstalledVersion = release.Version.Text;
            return Task.FromResult(new InstallResult(release.Version.Text, true, []));
        }
    }

    private sealed class NoPlayers : IPlayerPresence
    {
        public IReadOnlyList<OnlinePlayer> Online => [];
        public event Action? Changed;
        public Task ResyncAsync(CancellationToken ct = default)
        {
            _ = Changed;
            return Task.CompletedTask;
        }
    }

    private (UpdateService Updates, StubServer Server, DataPaths Paths, BackupService Backups) Create() => Create(null).Tuple;

    private sealed record Setup(UpdateService Updates, StubServer Server, DataPaths Paths, BackupService Backups, AppStateStore State, Versions.VersionPin Pin)
    {
        public (UpdateService, StubServer, DataPaths, BackupService) Tuple => (Updates, Server, Paths, Backups);
    }

    private Setup Create(string? pinnedEnvironment)
    {
        var paths = new DataPaths(_root);
        paths.EnsureCreated();
        // Versión 1.0.0.0 ya descargada y un mundo existente.
        Directory.CreateDirectory(Path.Combine(paths.Versions, "1.0.0.0"));
        File.WriteAllText(Path.Combine(paths.Versions, "1.0.0.0", "bedrock_server"), "1.0.0.0");
        var world = Path.Combine(paths.Server, "worlds", "Bedrock level");
        Directory.CreateDirectory(world);
        File.WriteAllText(Path.Combine(world, "level.dat"), "mundo original");

        var options = new CubeKeeperOptions { DataPath = _root };
        options.Updates.HealthTimeoutSeconds = 2;
        options.Bds.PinnedVersion = pinnedEnvironment;
        var installer = new StubInstaller(paths);
        var server = new StubServer(installer);
        var console = new ConsoleLog(TimeProvider.System);
        var state = new AppStateStore(paths);
        state.Update(s => s with { DesiredRunning = true });
        var backups = new BackupService(server, new ConsoleCommandRunner(server, console), paths, Options.Create(options), TimeProvider.System, NullLogger<BackupService>.Instance);
        // Con el servidor simulado "en marcha" no hay quien responda a save hold: las copias de estos tests se hacen en frío.
        server.StopAsync().Wait();
        server.StartAsync().Wait();
        var pin = new Versions.VersionPin(state, Options.Create(options));
        var updates = new UpdateService(server, installer, new NullVersionSource(), backups, new NoPlayers(), console, paths, state,
            Options.Create(options), TimeProvider.System, NullLogger<UpdateService>.Instance, pin: pin);
        return new Setup(updates, server, paths, backups, state, pin);
    }

    private sealed class NullVersionSource : IVersionSource
    {
        public Task<BdsRelease?> GetLatestAsync(ReleaseChannel channel, CancellationToken ct = default) => Task.FromResult<BdsRelease?>(null);
    }

    private static BdsRelease Release(string v) =>
        new(BdsVersion.Parse(v), new Uri($"https://example.test/bedrock-server-{v}.zip"), ReleaseChannel.Stable, "test");

    [Fact]
    public async Task Failed_update_rolls_back_version_and_world()
    {
        var (updates, server, paths, backups) = Create();
        await server.StopAsync(); // copia en frío
        server.Broken.Add("2.0.0.0");

        var result = await updates.UpdateAsync(Release("2.0.0.0"), []);

        Assert.False(result.Succeeded);
        Assert.Contains("se ha vuelto a 1.0.0.0", result.Message);
        Assert.Equal(["2.0.0.0", "1.0.0.0"], server.Installed);
        Assert.Equal(ServerStatus.Running, server.Snapshot.Status);
        Assert.Contains(backups.List(), e => e.File.Kind == Core.Backups.BackupKind.PreUpdate);
        Assert.Equal("mundo original", File.ReadAllText(Path.Combine(paths.Server, "worlds", "Bedrock level", "level.dat")));
        Assert.False(updates.LastUpdate!.Succeeded);
    }

    [Fact]
    public async Task Successful_update_records_result_and_offers_previous_version()
    {
        var (updates, server, _, backups) = Create();
        await server.StopAsync();

        var result = await updates.UpdateAsync(Release("2.0.0.0"), []);

        Assert.True(result.Succeeded);
        Assert.Equal("1.0.0.0", updates.PreviousVersion);
        Assert.Single(backups.List());
        Assert.Equal(["2.0.0.0"], server.Installed);
    }

    [Fact]
    public async Task Installing_a_newer_version_from_the_panel_keeps_it_pinned_and_rollback_follows()
    {
        var s = Create("1.0.0.0");
        await s.Server.StopAsync();
        Assert.True(s.Updates.IsPinned);
        Assert.False(s.Pin.FromPanel);

        var up = await s.Updates.UpdateAsync(Release("2.0.0.0"), []);

        Assert.True(up.Succeeded);
        Assert.Equal("2.0.0.0", s.Pin.Version);
        Assert.True(s.Pin.FromPanel);
        Assert.False(s.Updates.AutoUpdateActive);

        await s.Server.StopAsync();
        var back = await s.Updates.RollbackAsync();

        // Al volver a la de la variable, manda otra vez la variable.
        Assert.True(back.Succeeded);
        Assert.Equal("1.0.0.0", s.Pin.Version);
        Assert.False(s.Pin.FromPanel);
    }

    [Fact]
    public async Task A_failed_update_does_not_change_the_pinned_version()
    {
        var s = Create("1.0.0.0");
        await s.Server.StopAsync();
        s.Server.Broken.Add("2.0.0.0");

        var result = await s.Updates.UpdateAsync(Release("2.0.0.0"), []);

        Assert.False(result.Succeeded);
        Assert.Equal("1.0.0.0", s.Pin.Version);
        Assert.False(s.Pin.FromPanel);
    }

    [Fact]
    public async Task Without_a_pinned_version_installs_do_not_pin_and_the_panel_can_go_back_to_the_variable()
    {
        var s = Create(null);
        await s.Server.StopAsync();
        Assert.True((await s.Updates.UpdateAsync(Release("2.0.0.0"), [])).Succeeded);
        Assert.False(s.Updates.IsPinned);
        Assert.Null(s.State.Current.BdsPin);

        var pinned = Create("1.0.0.0");
        pinned.State.Update(st => st with { BdsPin = new BdsPinSetting("3.0.0.0") });
        Assert.Equal("3.0.0.0", pinned.Pin.Version);
        pinned.Pin.UseEnvironment();
        Assert.Equal("1.0.0.0", pinned.Pin.Version);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
