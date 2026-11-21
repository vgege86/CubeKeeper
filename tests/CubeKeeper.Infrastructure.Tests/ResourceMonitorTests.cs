using CubeKeeper.Core.Abstractions;
using CubeKeeper.Core.Logs;
using CubeKeeper.Core.Server;
using CubeKeeper.Core.Versions;
using CubeKeeper.Infrastructure.Resources;
using Microsoft.Extensions.Logging.Abstractions;

namespace CubeKeeper.Infrastructure.Tests;

public sealed class ResourceMonitorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ck-res-" + Guid.NewGuid().ToString("N"));

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Server(int? pid) : IServerController
    {
        public ServerSnapshot Snapshot => new(ServerStatus.Running, "1.0", "1.0", null, pid, null);
        public event Action<ServerSnapshot>? StateChanged;
        public event Action<BdsLogEvent>? LogEvent;
        public Task StartAsync(CancellationToken ct = default) { _ = StateChanged; _ = LogEvent; return Task.CompletedTask; }
        public Task StopAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task RestartAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task SendCommandAsync(string command, CancellationToken ct = default) => Task.CompletedTask;
        public Task<InstallResult> InstallAsync(BdsRelease release, IProgress<string>? progress = null, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private string Proc => Path.Combine(_root, "proc");
    private string Cgroup => Path.Combine(_root, "cgroup");

    private void WriteProcess(long ticks, long rssKb)
    {
        var dir = Path.Combine(Proc, "77");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "status"), $"Name:\tbedrock_server\nVmRSS:\t{rssKb} kB\n");
        File.WriteAllText(Path.Combine(dir, "stat"), $"77 (bedrock_server) S 1 77 77 0 -1 0 0 0 0 0 {ticks - 100} 100 0 0 20 0 40 0 1 1 1");
    }

    private void WriteCgroup(long current, long inactive, string max = "4294967296")
    {
        Directory.CreateDirectory(Cgroup);
        File.WriteAllText(Path.Combine(Cgroup, "memory.current"), current + "\n");
        File.WriteAllText(Path.Combine(Cgroup, "memory.stat"), $"anon 1\ninactive_file {inactive}\n");
        File.WriteAllText(Path.Combine(Cgroup, "memory.max"), max + "\n");
        File.WriteAllText(Path.Combine(Cgroup, "cpu.max"), "400000 100000\n");
    }

    [Fact]
    public async Task Measures_cpu_and_memory_from_proc_and_cgroup_v2()
    {
        var clock = new Clock();
        var monitor = new ResourceMonitor(new Server(77), clock, NullLogger<ResourceMonitor>.Instance) { ProcRoot = Proc, CgroupRoot = Cgroup };
        WriteProcess(1000, 512 * 1024);
        WriteCgroup(current: 3L << 30, inactive: 1L << 30);

        var first = await monitor.SampleAsync(default);
        Assert.Null(first.ServerCpuPercent); // hace falta una medida anterior
        Assert.Equal(512L * 1024 * 1024, first.ServerMemory);
        Assert.Equal(2L << 30, first.ContainerMemory);
        Assert.Equal(4L << 30, first.ContainerLimit);
        Assert.Equal(50, first.ContainerMemoryPercent);
        Assert.Equal(4, monitor.Cpus);

        clock.Now += TimeSpan.FromSeconds(30);
        WriteProcess(1300, 600 * 1024);
        var second = await monitor.SampleAsync(default);
        // 300 ticks = 3 s de CPU en 30 s con 4 CPU = 2,5 %.
        Assert.Equal(2.5, second.ServerCpuPercent!.Value, 3);
        Assert.Equal(2, monitor.History(TimeSpan.FromHours(1)).Count);
    }

    [Fact]
    public async Task Without_server_or_limit_there_are_no_figures()
    {
        var monitor = new ResourceMonitor(new Server(null), new Clock(), NullLogger<ResourceMonitor>.Instance) { ProcRoot = Proc, CgroupRoot = Cgroup };
        WriteCgroup(1000, 0, max: "max");
        var s = await monitor.SampleAsync(default);
        Assert.Null(s.ServerMemory);
        Assert.Null(s.ContainerLimit);
        Assert.Null(s.ContainerMemoryPercent);
    }

    [Fact]
    public async Task Warns_once_when_memory_stays_near_the_limit()
    {
        var clock = new Clock();
        var ntfy = new NotifierTests.CaptureHandler();
        var monitor = new ResourceMonitor(new Server(77), clock, NullLogger<ResourceMonitor>.Instance, NotifierTests.Create(_root, ntfy, time: clock))
        {
            ProcRoot = Proc,
            CgroupRoot = Cgroup,
        };
        WriteProcess(1000, 1);
        WriteCgroup(current: 95, inactive: 0, max: "100");
        for (var i = 0; i < 6; i++)
        {
            clock.Now += TimeSpan.FromSeconds(30);
            await monitor.SampleAsync(default);
        }
        var (_, body) = Assert.Single(ntfy.Requests);
        Assert.Contains("memoria", body);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
