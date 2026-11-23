using System.Net;
using System.Net.Sockets;
using CubeKeeper.Core.Abstractions;
using CubeKeeper.Core.Server;
using CubeKeeper.Core.Versions;
using CubeKeeper.Infrastructure.Configuration;
using CubeKeeper.Infrastructure.Process;
using CubeKeeper.Infrastructure.State;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CubeKeeper.Infrastructure.Tests;

/// <summary>Monta un <see cref="ServerSupervisor"/> que lanza FakeBedrockServer en un directorio temporal.</summary>
internal sealed class SupervisorFixture : IAsyncDisposable
{
    public SupervisorFixture(Action<BdsOptions>? configure = null, IDictionary<string, string>? env = null, ITcpPortProbe? ports = null)
    {
        Root = Path.Combine(Path.GetTempPath(), "cubekeeper-tests", Guid.NewGuid().ToString("N"));
        Paths = new DataPaths(Root);
        Paths.EnsureCreated();
        Port = FreeTcpPort();
        File.WriteAllText(Paths.ServerProperties, $"server-port={Port}\nmax-players=10\n");

        foreach (var (k, v) in env ?? new Dictionary<string, string>())
        {
            Environment.SetEnvironmentVariable(k, v);
        }
        _env = env?.Keys.ToArray() ?? [];

        var options = new CubeKeeperOptions { DataPath = Root };
        options.Bds.LaunchCommand = "dotnet";
        options.Bds.LaunchArguments = $"\"{FakeServerDll}\"";
        options.Bds.StopTimeoutSeconds = 5;
        options.Bds.RestartDelaySeconds = 0;
        options.Bds.MaxRestarts = 2;
        configure?.Invoke(options.Bds);

        Console = new ConsoleLog(TimeProvider.System);
        State = new AppStateStore(Paths);
        Supervisor = new ServerSupervisor(
            Options.Create(options), Paths, Console, new NullInstaller(), State, TimeProvider.System, NullLoggerFactory.Instance, ports);
    }

    private readonly string[] _env;

    public string Root { get; }
    public DataPaths Paths { get; }
    public int Port { get; }
    public ConsoleLog Console { get; }
    public AppStateStore State { get; }
    public ServerSupervisor Supervisor { get; }

    public static string FakeServerDll
    {
        get
        {
            var baseDir = AppContext.BaseDirectory;
            var config = baseDir.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}") ? "Release" : "Debug";
            var dll = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "FakeBedrockServer", "bin", config, "net10.0", "FakeBedrockServer.dll"));
            return File.Exists(dll) ? dll : throw new FileNotFoundException("Compila FakeBedrockServer antes de los tests.", dll);
        }
    }

    public async Task WaitForStatusAsync(ServerStatus status, int timeoutMs = 20000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (Supervisor.Snapshot.Status != status)
        {
            if (DateTime.UtcNow > deadline)
            {
                var log = string.Join("\n", Console.Snapshot().Select(l => $"[{l.Kind}] {l.Text}"));
                throw new TimeoutException($"Esperaba {status} y está en {Supervisor.Snapshot.Status}.\n{log}");
            }
            await Task.Delay(50);
        }
    }

    public async Task WaitForLineAsync(Func<string, bool> predicate, int timeoutMs = 10000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!Console.Snapshot().Any(l => predicate(l.Text)))
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("No apareció la línea esperada en la consola.");
            }
            await Task.Delay(50);
        }
    }

    private static int FreeTcpPort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await Supervisor.ShutdownAsync(CancellationToken.None);
        }
        catch
        {
            // Limpieza de pruebas.
        }
        Supervisor.Dispose();
        foreach (var k in _env)
        {
            Environment.SetEnvironmentVariable(k, null);
        }
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private sealed class NullInstaller : IServerInstaller
    {
        public string? InstalledVersion => null;
        public bool IsInstalled => false;
        public Task<InstallResult> InstallAsync(BdsRelease release, IProgress<string>? progress = null, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
