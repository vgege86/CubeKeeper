using CubeKeeper.Infrastructure.Backups;
using CubeKeeper.Infrastructure.Configuration;
using CubeKeeper.Infrastructure.Process;
using CubeKeeper.Infrastructure.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CubeKeeper.Infrastructure.Tests;

public class StorageTests
{
    private static (StorageService Storage, CubeKeeperOptions Options, SupervisorFixture Fixture) Create(SupervisorFixture f)
    {
        var options = new CubeKeeperOptions { DataPath = f.Root };
        var backups = new BackupService(f.Supervisor, new ConsoleCommandRunner(f.Supervisor, f.Console), f.Paths, Options.Create(options), TimeProvider.System, NullLogger<BackupService>.Instance);
        return (new StorageService(f.Paths, backups, Options.Create(options), TimeProvider.System, NullLogger<StorageService>.Instance), options, f);
    }

    [Fact]
    public async Task Measures_free_space_and_folder_sizes()
    {
        await using var f = new SupervisorFixture();
        var (storage, _, _) = Create(f);
        Directory.CreateDirectory(f.Paths.Backups);
        await File.WriteAllBytesAsync(Path.Combine(f.Paths.Backups, "x.mcworld"), new byte[3000]);

        var report = storage.GetReport(refresh: true);

        var data = Assert.Single(report.Disks);
        Assert.True(data.Total > 0);
        Assert.InRange(data.FreePercent, 0, 100);
        Assert.Equal(3000, Assert.Single(report.Folders, x => x.Label == "Copias del mundo").Bytes);
    }

    [Theory]
    [InlineData(100L, 5L, true)]    // 5 % libre
    [InlineData(1000L, 500L, true)] // la mitad libre, pero menos de 5 GB
    public async Task Low_when_below_percent_or_gigabytes(long totalGb, long freeGb, bool low)
    {
        await using var f = new SupervisorFixture();
        var (storage, _, _) = Create(f);
        const long gb = 1024L * 1024 * 1024;
        Assert.Equal(low, storage.IsLow(totalGb * gb / 1000, freeGb * gb / 1000));
        Assert.False(storage.IsLow(1000 * gb, 500 * gb));
    }

    [Fact]
    public async Task Monitor_notifies_once_per_disk_until_there_is_space_again()
    {
        await using var f = new SupervisorFixture();
        var (storage, options, _) = Create(f);
        var ntfy = new NotifierTests.CaptureHandler();
        var clock = new ManualClock();
        var monitor = new StorageMonitor(storage, TimeProvider.System, NullLogger<StorageMonitor>.Instance, NotifierTests.Create(f.Root, ntfy, time: clock));

        options.Storage.WarnPercent = 100.1; // cualquier disco está «lleno»
        await monitor.CheckOnceAsync(default);
        await monitor.CheckOnceAsync(default);
        var (_, body) = Assert.Single(ntfy.Requests);
        Assert.Contains("poco espacio", body);

        options.Storage.WarnPercent = 0;
        options.Storage.WarnGigabytes = 0;
        await monitor.CheckOnceAsync(default);
        clock.Now += TimeSpan.FromHours(1); // pasada la pausa entre avisos iguales del Notifier
        options.Storage.WarnPercent = 100.1;
        await monitor.CheckOnceAsync(default);
        Assert.Equal(2, ntfy.Requests.Count);
    }

    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
