using System.IO.Compression;
using System.Text;
using CubeKeeper.Infrastructure.Backups;
using CubeKeeper.Infrastructure.Configuration;
using CubeKeeper.Infrastructure.Diagnostics;
using CubeKeeper.Infrastructure.Security;
using CubeKeeper.Infrastructure.State;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CubeKeeper.Infrastructure.Tests;

public sealed class BackupVerifierAndDiagnosticsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ck-verify-" + Guid.NewGuid().ToString("N"));

    public BackupVerifierAndDiagnosticsTests() => Directory.CreateDirectory(_dir);

    private string Zip(string name, params (string Path, string Content)[] entries)
    {
        var path = Path.Combine(_dir, name);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (p, content) in entries)
        {
            using var w = new StreamWriter(zip.CreateEntry(p).Open());
            w.Write(content);
        }
        return path;
    }

    private static readonly (string, string)[] GoodWorld =
    [
        ("level.dat", "nivel"),
        ("db/CURRENT", "MANIFEST-000012\n"),
        ("db/MANIFEST-000012", "manifiesto"),
        ("db/000013.ldb", new string('x', 50_000)),
    ];

    [Fact]
    public void A_complete_world_passes()
    {
        var (ok, message) = BackupVerifier.Check(Zip("bien.mcworld", GoodWorld), default);
        Assert.True(ok, message);
        Assert.Contains("4 archivos", message);
    }

    [Fact]
    public void A_world_in_a_subfolder_passes()
    {
        var entries = GoodWorld.Select(e => ("Mi mundo/" + e.Item1, e.Item2)).ToArray();
        Assert.True(BackupVerifier.Check(Zip("carpeta.mcworld", entries), default).Ok);
    }

    [Theory]
    [InlineData("db/CURRENT", "Falta db/CURRENT")]
    [InlineData("db/MANIFEST-000012", "no está en la copia")]
    [InlineData("level.dat", "level.dat")]
    public void Missing_pieces_fail(string missing, string expected)
    {
        var (ok, message) = BackupVerifier.Check(Zip("incompleta.mcworld", GoodWorld.Where(e => e.Item1 != missing).ToArray()), default);
        Assert.False(ok);
        Assert.Contains(expected, message);
    }

    [Fact]
    public void Corrupted_data_fails()
    {
        var path = Zip("dañada.mcworld", GoodWorld);
        var bytes = File.ReadAllBytes(path);
        // Estropea bytes en medio de los datos comprimidos del archivo grande.
        var marker = Encoding.ASCII.GetBytes("000013.ldb");
        var at = bytes.AsSpan().IndexOf(marker) + marker.Length + 20;
        for (var i = 0; i < 30; i++)
        {
            bytes[at + i] ^= 0x5A;
        }
        File.WriteAllBytes(path, bytes);

        Assert.False(BackupVerifier.Check(path, default).Ok);
    }

    [Fact]
    public async Task A_backup_made_by_CubeKeeper_passes_and_the_result_is_saved()
    {
        await using var f = new SupervisorFixture();
        var options = Microsoft.Extensions.Options.Options.Create(new CubeKeeperOptions { DataPath = f.Root });
        var backups = new BackupService(f.Supervisor, new Process.ConsoleCommandRunner(f.Supervisor, f.Console), f.Paths, options, TimeProvider.System, NullLogger<BackupService>.Instance);
        var world = Path.Combine(f.Paths.Server, "worlds", "Bedrock level");
        Directory.CreateDirectory(Path.Combine(world, "db"));
        await File.WriteAllTextAsync(Path.Combine(world, "level.dat"), "nivel");
        await File.WriteAllTextAsync(Path.Combine(world, "db", "CURRENT"), "MANIFEST-000001\n");
        await File.WriteAllTextAsync(Path.Combine(world, "db", "MANIFEST-000001"), "m");
        var made = await backups.CreateAsync(Core.Backups.BackupKind.Manual);
        var state = new AppStateStore(f.Paths);
        var verifier = new BackupVerifier(backups, state, TimeProvider.System, NullLogger<BackupVerifier>.Instance);

        var result = await verifier.VerifyAsync();

        Assert.NotNull(result);
        Assert.True(result.Ok, result.Message);
        Assert.Equal(made.FileName, result.File);
        Assert.Equal(result, state.Current.BackupCheck);
    }

    [Fact]
    public async Task Diagnostics_zip_has_the_expected_files_and_no_secrets()
    {
        const string adminPassword = "contraseña-super-secreta";
        const string keySecret = "secreto-de-las-claves-123456";
        const string topic = "cubekeeper-tema-privado-xyz";
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["CubeKeeper:DataPath"] = _dir,
            ["CubeKeeper:Admin:Password"] = adminPassword,
            ["CubeKeeper:Notifications:NtfyTopic"] = topic,
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new KeyEncryptionSecret(keySecret));
        services.AddCubeKeeperInfrastructure(config);
        using var sp = services.BuildServiceProvider();
        var paths = sp.GetRequiredService<DataPaths>();
        paths.EnsureCreated();
        await File.WriteAllTextAsync(paths.ServerProperties, "server-port=19132\nlevel-name=Mundo\n");
        // Un mensaje de registro que, por error, llevase un secreto.
        sp.GetRequiredService<RecentLogs>().CreateLogger("Prueba").LogWarning("Algo con {Secret} y el tema {Topic}", adminPassword, topic);

        using var buffer = new MemoryStream();
        await sp.GetRequiredService<DiagnosticsService>().WriteZipAsync(buffer);
        buffer.Position = 0;
        using var zip = new ZipArchive(buffer, ZipArchiveMode.Read);

        var names = zip.Entries.Select(e => e.FullName).ToList();
        Assert.Contains("resumen.txt", names);
        Assert.Contains("registro-cubekeeper.txt", names);
        Assert.Contains("consola-servidor.txt", names);
        Assert.Contains("server.properties", names);
        var all = string.Join("\n", zip.Entries.Select(e => new StreamReader(e.Open()).ReadToEnd()));
        Assert.DoesNotContain(adminPassword, all);
        Assert.DoesNotContain(keySecret, all);
        Assert.DoesNotContain(topic, all);
        Assert.Contains("Algo con *** y el tema ***", all);
        Assert.Contains("Claves cifradas (CUBEKEEPER_KEY_SECRET): sí", all);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}
