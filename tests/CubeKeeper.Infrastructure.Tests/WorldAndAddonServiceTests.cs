using System.IO.Compression;
using System.Text;
using CubeKeeper.Core.Addons;
using CubeKeeper.Core.Backups;
using CubeKeeper.Infrastructure.Addons;
using CubeKeeper.Infrastructure.Backups;
using CubeKeeper.Infrastructure.Configuration;
using CubeKeeper.Infrastructure.Process;
using CubeKeeper.Infrastructure.Worlds;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CubeKeeper.Infrastructure.Tests;

public class WorldAndAddonServiceTests
{
    private static (WorldService Worlds, AddonService Addons, BackupService Backups) Create(SupervisorFixture f)
    {
        var options = Options.Create(new CubeKeeperOptions { DataPath = f.Root });
        var backups = new BackupService(f.Supervisor, new ConsoleCommandRunner(f.Supervisor, f.Console), f.Paths, options, TimeProvider.System, NullLogger<BackupService>.Instance);
        return (new WorldService(f.Supervisor, backups, f.Paths, TimeProvider.System, NullLogger<WorldService>.Instance),
                new AddonService(backups, f.Paths, NullLogger<AddonService>.Instance),
                backups);
    }

    private static void MakeWorld(SupervisorFixture f, string name, string content = "nivel")
    {
        var dir = Path.Combine(f.Paths.Server, "worlds", name);
        Directory.CreateDirectory(Path.Combine(dir, "db"));
        File.WriteAllText(Path.Combine(dir, "level.dat"), content);
    }

    private static MemoryStream Zip(params (string Path, string Content)[] entries)
    {
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, content) in entries)
            {
                using var w = new StreamWriter(zip.CreateEntry(path).Open());
                w.Write(content);
            }
        }
        ms.Position = 0;
        return ms;
    }

    private static byte[] ZipBytes(params (string Path, string Content)[] entries) => Zip(entries).ToArray();

    private static string Manifest(string uuid, string type, string name = "Paquete", string version = "[1,0,0]") =>
        $$"""{"format_version":2,"header":{"name":"{{name}}","uuid":"{{uuid}}","version":{{version}}},"modules":[{"type":"{{type}}","uuid":"{{Guid.NewGuid()}}","version":[1,0,0]}]}""";

    [Fact]
    public async Task Import_mcworld_with_subfolder_uses_levelname_and_can_activate()
    {
        await using var f = new SupervisorFixture();
        var (worlds, _, backups) = Create(f);
        MakeWorld(f, "Bedrock level");

        using var zip = Zip(("Mundo Realm/level.dat", "x"), ("Mundo Realm/levelname.txt", "Mi Realm"), ("Mundo Realm/db/CURRENT", "y"));
        var name = await worlds.ImportAsync(zip, "export.mcworld", null, activate: true);

        Assert.Equal("Mi Realm", name);
        Assert.True(File.Exists(Path.Combine(f.Paths.Server, "worlds", "Mi Realm", "db", "CURRENT")));
        Assert.Equal("Mi Realm", await backups.GetWorldNameAsync());
        Assert.Contains(backups.List(), e => e.File.Kind == BackupKind.PreChange && e.File.World == "Bedrock level");
        Assert.Empty(Directory.GetDirectories(Path.Combine(f.Paths.Server, "worlds"), ".*"));
    }

    [Fact]
    public async Task Import_rejects_non_worlds_and_existing_names()
    {
        await using var f = new SupervisorFixture();
        var (worlds, _, _) = Create(f);
        MakeWorld(f, "Existe");

        await Assert.ThrowsAsync<InvalidDataException>(() => worlds.ImportAsync(Zip(("algo.txt", "x")), "x.mcworld", null, false));
        await Assert.ThrowsAsync<InvalidOperationException>(() => worlds.ImportAsync(Zip(("level.dat", "x")), "x.mcworld", "Existe", false));
        await Assert.ThrowsAsync<ArgumentException>(() => worlds.ImportAsync(Zip(("level.dat", "x")), "x.mcworld", "../fuera", false));
        Assert.Empty(Directory.GetDirectories(Path.Combine(f.Paths.Server, "worlds"), ".*"));
    }

    [Fact]
    public async Task Create_sets_properties_and_switch_changes_level_name()
    {
        await using var f = new SupervisorFixture();
        var (worlds, _, backups) = Create(f);
        MakeWorld(f, "Bedrock level");
        MakeWorld(f, "Otro");

        await worlds.CreateAsync(new NewWorldOptions("Nuevo", "12345", "creative", "peaceful"));
        var props = File.ReadAllText(f.Paths.ServerProperties);
        Assert.Contains("level-name=Nuevo", props);
        Assert.Contains("level-seed=12345", props);
        Assert.Contains("gamemode=creative", props);
        Assert.Contains("difficulty=peaceful", props);
        Assert.Contains(await worlds.ListAsync(), w => w.Name == "Nuevo" && w.IsActive);

        await worlds.SwitchAsync("Otro");
        Assert.Equal("Otro", await backups.GetWorldNameAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => worlds.SwitchAsync("No existe"));
        await Assert.ThrowsAsync<ArgumentException>(() => worlds.CreateAsync(new NewWorldOptions("Malo", null, "hardcore", "normal")));
    }

    [Fact]
    public async Task Switch_restarts_running_server_with_new_world()
    {
        await using var f = new SupervisorFixture();
        var (worlds, _, _) = Create(f);
        MakeWorld(f, "Otro");
        await f.Supervisor.StartAsync();
        await f.WaitForStatusAsync(Core.Server.ServerStatus.Running);

        await worlds.SwitchAsync("Otro");
        await f.WaitForStatusAsync(Core.Server.ServerStatus.Running);

        Assert.Contains(f.Console.Snapshot(), l => l.Text.EndsWith("Level Name: Otro", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Delete_backs_up_inactive_world_and_refuses_active()
    {
        await using var f = new SupervisorFixture();
        var (worlds, _, backups) = Create(f);
        MakeWorld(f, "Bedrock level");
        MakeWorld(f, "Viejo", "contenido viejo");

        await Assert.ThrowsAsync<InvalidOperationException>(() => worlds.DeleteAsync("Bedrock level"));
        await worlds.DeleteAsync("Viejo");

        Assert.False(Directory.Exists(Path.Combine(f.Paths.Server, "worlds", "Viejo")));
        var b = Assert.Single(backups.List(), e => e.File.World == "Viejo");
        Assert.Equal(BackupKind.PreChange, b.File.Kind);
    }

    [Fact]
    public async Task Export_inactive_world_zips_to_temporary_file()
    {
        await using var f = new SupervisorFixture();
        var (worlds, _, _) = Create(f);
        MakeWorld(f, "Bedrock level");
        MakeWorld(f, "Otro", "abc");

        var (stream, fileName) = await worlds.ExportAsync("Otro");
        await using (stream)
        {
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
            Assert.Contains(zip.Entries, e => e.FullName == "level.dat");
        }
        Assert.Equal("Otro.mcworld", fileName);
        Assert.Empty(Directory.GetFiles(f.Paths.Downloads, ".export-*"));
    }

    [Fact]
    public async Task Installs_mcpack_and_mcaddon_and_manages_world_json()
    {
        await using var f = new SupervisorFixture();
        var (_, addons, _) = Create(f);
        MakeWorld(f, "Bedrock level");
        const string bp = "11111111-2222-3333-4444-555555555555";
        const string rp = "22222222-2222-3333-4444-555555555555";

        // .mcpack de comportamiento con nombre traducido.
        using (var pack = Zip(("manifest.json", Manifest(bp, "data", "pack.name")), ("texts/es_ES.lang", "pack.name=Comportamiento\n")))
        {
            var installed = await addons.InstallAsync(pack, "bp.mcpack");
            Assert.Equal("Comportamiento", Assert.Single(installed).Name);
        }

        // .mcaddon con un .mcpack de recursos dentro y una nueva versión del de comportamiento.
        var nestedRp = ZipBytes(("manifest.json", Manifest(rp, "resources", "Texturas")));
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var s = zip.CreateEntry("Texturas.mcpack").Open())
            {
                s.Write(nestedRp);
            }
            using (var w = new StreamWriter(zip.CreateEntry("BP/manifest.json").Open()))
            {
                w.Write(Manifest(bp, "data", "Comportamiento", "[1,1,0]"));
            }
        }
        ms.Position = 0;
        var fromAddon = await addons.InstallAsync(ms, "todo.mcaddon");
        Assert.Equal(2, fromAddon.Count);

        var list = await addons.ListAsync();
        Assert.Equal(2, list.Count);
        Assert.All(list, p => Assert.True(p.Enabled));
        Assert.Equal("1.1.0", list.Single(p => p.Manifest.Type == PackType.Behavior).Manifest.VersionText);

        var world = Path.Combine(f.Paths.Server, "worlds", "Bedrock level");
        Assert.Single(Directory.GetDirectories(Path.Combine(world, "behavior_packs"))); // la versión vieja se sustituye
        Assert.Contains(bp, File.ReadAllText(Path.Combine(world, "world_behavior_packs.json")));
        Assert.Contains("1,", File.ReadAllText(Path.Combine(world, "world_behavior_packs.json")).Replace(" ", "").Replace("\n", ""));

        await addons.SetEnabledAsync(Guid.Parse(rp), PackType.Resource, false);
        Assert.DoesNotContain(rp, File.ReadAllText(Path.Combine(world, "world_resource_packs.json")));
        Assert.False((await addons.ListAsync()).Single(p => p.Manifest.Type == PackType.Resource).Enabled);

        await addons.RemoveAsync(Guid.Parse(bp), PackType.Behavior);
        Assert.Empty(Directory.GetDirectories(Path.Combine(world, "behavior_packs")));
        Assert.DoesNotContain(bp, File.ReadAllText(Path.Combine(world, "world_behavior_packs.json")));
        Assert.Empty(Directory.GetDirectories(f.Paths.Downloads, ".addon-*"));
    }

    [Fact]
    public async Task Rejects_files_without_packs_or_without_world()
    {
        await using var f = new SupervisorFixture();
        var (_, addons, _) = Create(f);
        await Assert.ThrowsAsync<InvalidOperationException>(() => addons.InstallAsync(Zip(("manifest.json", "{}")), "x.mcpack"));

        MakeWorld(f, "Bedrock level");
        await Assert.ThrowsAsync<InvalidDataException>(() => addons.InstallAsync(Zip(("readme.txt", "hola")), "x.mcpack"));
        await Assert.ThrowsAsync<InvalidDataException>(() => addons.InstallAsync(new MemoryStream(Encoding.UTF8.GetBytes("no soy un zip")), "x.mcpack"));
    }
}
