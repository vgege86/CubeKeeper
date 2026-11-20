using System.IO.Compression;
using CubeKeeper.Core.Backups;
using CubeKeeper.Core.Server;
using CubeKeeper.Infrastructure.Backups;
using CubeKeeper.Infrastructure.Configuration;
using CubeKeeper.Infrastructure.Process;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CubeKeeper.Infrastructure.Tests;

public class BackupServiceTests
{
    private static BackupService Create(SupervisorFixture f, Action<BackupOptions>? configure = null)
    {
        var options = new CubeKeeperOptions { DataPath = f.Root };
        configure?.Invoke(options.Backups);
        return new BackupService(f.Supervisor, new ConsoleCommandRunner(f.Supervisor, f.Console), f.Paths,
            Options.Create(options), TimeProvider.System, NullLogger<BackupService>.Instance);
    }

    private static string WorldDir(SupervisorFixture f) => Path.Combine(f.Paths.Server, "worlds", "Bedrock level");

    [Fact]
    public async Task Hot_backup_uses_save_hold_query_resume_and_truncates_files()
    {
        await using var f = new SupervisorFixture();
        var backups = Create(f);
        await f.Supervisor.StartAsync();
        await f.WaitForStatusAsync(ServerStatus.Running);

        var b = await backups.CreateAsync(BackupKind.Manual);

        var commands = f.Console.Snapshot().Where(l => l.Kind == Core.Abstractions.ConsoleLineKind.Command).Select(l => l.Text).ToList();
        Assert.Equal(["save hold", "save query", "save query", "save resume"], commands);
        Assert.Contains(f.Console.Snapshot(), l => l.Text.Contains("Changes to the level are resumed"));

        using var zip = ZipFile.OpenRead(Path.Combine(f.Paths.Backups, b.FileName));
        var names = zip.Entries.Select(e => e.FullName).ToHashSet();
        Assert.Contains("level.dat", names);
        Assert.Contains("db/000005.ldb", names);
        Assert.Contains("levelname.txt", names); // no estaba en la lista de BDS: se copia entero
        // El servidor falso escribe "basura" después de save query: la copia debe tener solo la longitud indicada.
        Assert.Equal(4096, zip.GetEntry("db/000005.ldb")!.Length);
        Assert.True(new FileInfo(Path.Combine(WorldDir(f), "db", "000005.ldb")).Length > 4096);
    }

    [Fact]
    public async Task Cold_backup_zips_world_when_server_is_stopped()
    {
        await using var f = new SupervisorFixture();
        Directory.CreateDirectory(Path.Combine(WorldDir(f), "db"));
        File.WriteAllText(Path.Combine(WorldDir(f), "level.dat"), "nivel");
        File.WriteAllText(Path.Combine(WorldDir(f), "db", "CURRENT"), "x");

        var b = await Create(f).CreateAsync(BackupKind.Scheduled);

        using var zip = ZipFile.OpenRead(Path.Combine(f.Paths.Backups, b.FileName));
        Assert.Equal(["db/CURRENT", "level.dat"], zip.Entries.Select(e => e.FullName.Replace('\\', '/')).Order());
    }

    [Fact]
    public async Task Backup_without_world_fails_clearly()
    {
        await using var f = new SupervisorFixture();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Create(f).CreateAsync(BackupKind.Manual));
        Assert.Contains("No existe el mundo", ex.Message);
    }

    [Fact]
    public async Task Restore_replaces_world_and_keeps_a_safety_copy()
    {
        await using var f = new SupervisorFixture();
        var backups = Create(f);
        Directory.CreateDirectory(WorldDir(f));
        File.WriteAllText(Path.Combine(WorldDir(f), "level.dat"), "version 1");
        var v1 = await backups.CreateAsync(BackupKind.Manual);

        File.WriteAllText(Path.Combine(WorldDir(f), "level.dat"), "version 2");
        File.WriteAllText(Path.Combine(WorldDir(f), "nuevo.txt"), "solo en la 2");

        await backups.RestoreAsync(v1.FileName);

        Assert.Equal("version 1", File.ReadAllText(Path.Combine(WorldDir(f), "level.dat")));
        Assert.False(File.Exists(Path.Combine(WorldDir(f), "nuevo.txt")));
        Assert.Contains(backups.List(), e => e.File.Kind == BackupKind.PreRestore);
        Assert.Empty(Directory.GetDirectories(Path.GetDirectoryName(WorldDir(f))!, ".*"));
    }

    [Fact]
    public async Task Restore_while_running_stops_and_starts_server()
    {
        await using var f = new SupervisorFixture();
        var backups = Create(f);
        await f.Supervisor.StartAsync();
        await f.WaitForStatusAsync(ServerStatus.Running);
        var b = await backups.CreateAsync(BackupKind.Manual);

        await backups.RestoreAsync(b.FileName);
        await f.WaitForStatusAsync(ServerStatus.Running);

        Assert.Equal(2, f.Console.Snapshot().Count(l => l.Text.EndsWith("Server started.", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Restore_rejects_zip_without_level_dat()
    {
        await using var f = new SupervisorFixture();
        Directory.CreateDirectory(WorldDir(f));
        File.WriteAllText(Path.Combine(WorldDir(f), "level.dat"), "actual");
        var name = BackupName.Format("Bedrock level", DateTimeOffset.UtcNow, BackupKind.Manual);
        using (var zip = ZipFile.Open(Path.Combine(f.Paths.Backups, name), ZipArchiveMode.Create))
        {
            zip.CreateEntry("otra-cosa.txt");
        }

        await Assert.ThrowsAsync<InvalidDataException>(() => Create(f).RestoreAsync(name));
        Assert.Equal("actual", File.ReadAllText(Path.Combine(WorldDir(f), "level.dat")));
    }

    [Fact]
    public async Task Copies_to_secondary_destination_and_applies_retention_to_both()
    {
        await using var f = new SupervisorFixture();
        var secondary = Path.Combine(f.Root, "segundo");
        Directory.CreateDirectory(secondary);
        var backups = Create(f, o =>
        {
            o.SecondaryPath = secondary;
            o.KeepManual = 2;
        });
        Directory.CreateDirectory(WorldDir(f));
        File.WriteAllText(Path.Combine(WorldDir(f), "level.dat"), "x");

        for (var i = 0; i < 3; i++)
        {
            await backups.CreateAsync(BackupKind.Manual);
            await Task.Delay(1100); // nombres distintos (resolución de segundos)
        }

        Assert.Equal(2, backups.List().Count);
        Assert.All(backups.List(), e => Assert.True(e.InSecondary));
        Assert.Equal(2, Directory.GetFiles(secondary, "*.mcworld").Length);
        Assert.Null(backups.SecondaryProblem);
    }

    [Fact]
    public async Task Missing_secondary_destination_is_reported_and_clears_when_mounted()
    {
        await using var f = new SupervisorFixture();
        var secondary = Path.Combine(f.Root, "sin-montar");
        var backups = Create(f, o => o.SecondaryPath = secondary);
        Directory.CreateDirectory(WorldDir(f));
        File.WriteAllText(Path.Combine(WorldDir(f), "level.dat"), "x");

        await backups.CreateAsync(BackupKind.Manual);
        Assert.Contains("¿está montado", backups.SecondaryProblem);
        Assert.Single(backups.List());

        Directory.CreateDirectory(secondary);
        await Task.Delay(1100);
        await backups.CreateAsync(BackupKind.Manual);
        Assert.Null(backups.SecondaryProblem);
    }

    [Fact]
    public async Task Resolve_rejects_path_traversal()
    {
        await using var f = new SupervisorFixture();
        Assert.Null(Create(f).ResolveExisting("../state.json"));
        Assert.Null(Create(f).ResolveExisting("../../etc/passwd__20261119-163005Z__manual.mcworld"));
    }
}
