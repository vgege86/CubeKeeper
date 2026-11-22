using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Text;
using CubeKeeper.Core.Maps;
using CubeKeeper.Infrastructure.Backups;
using CubeKeeper.Infrastructure.Configuration;
using CubeKeeper.Infrastructure.Maps;
using CubeKeeper.Infrastructure.Process;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CubeKeeper.Infrastructure.Tests;

public class MapServiceTests
{
    // Motor falso: comprueba que le llega un mundo con level.dat y escribe una página y una tesela en la salida.
    private const string FakeRenderer = """
        #!/bin/sh
        world=""; out=""
        while [ $# -gt 0 ]; do
          case "$1" in
            --world) world="$2"; shift ;;
            --output) out="$2"; shift ;;
          esac
          shift
        done
        echo "Dibujando $world"
        [ -f "$world/level.dat" ] || { echo "falta level.dat" >&2; exit 3; }
        mkdir -p "$out/tiles"
        echo "<html>mapa</html>" > "$out/unmined.index.html"
        echo "png" > "$out/tiles/0_0.png"
        echo "Listo"
        """;

    private static (MapService Maps, BackupService Backups) Create(SupervisorFixture f, HttpMessageHandler? handler = null)
    {
        var options = Options.Create(new CubeKeeperOptions { DataPath = f.Root });
        var backups = new BackupService(f.Supervisor, new ConsoleCommandRunner(f.Supervisor, f.Console), f.Paths, options, TimeProvider.System, NullLogger<BackupService>.Instance);
        var maps = new MapService(backups, f.Paths, f.State, new HttpClient(handler ?? new HttpClientHandler()), TimeProvider.System, NullLogger<MapService>.Instance);
        var world = Path.Combine(f.Paths.Server, "worlds", "Bedrock level");
        Directory.CreateDirectory(Path.Combine(world, "db"));
        File.WriteAllText(Path.Combine(world, "level.dat"), "nivel");
        return (maps, backups);
    }

    private static byte[] TarGz(string name, string content)
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true))
        using (var tar = new TarWriter(gz))
        {
            var entry = new PaxTarEntry(TarEntryType.RegularFile, $"unmined-cli-linux-x64/{name}")
            {
                DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content)),
                Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
            };
            tar.WriteEntry(entry);
        }
        return ms.ToArray();
    }

    [Fact]
    public async Task Installs_from_tar_gz_and_renders_from_a_fresh_backup()
    {
        await using var f = new SupervisorFixture();
        var (maps, backups) = Create(f);

        var exe = await maps.InstallAsync(MapEngines.Unmined, new MemoryStream(TarGz("unmined-cli", FakeRenderer)), "unmined-cli_linux-x64.tar.gz");
        Assert.EndsWith("unmined-cli", exe);
        if (!OperatingSystem.IsWindows())
        {
            Assert.True(File.GetUnixFileMode(exe).HasFlag(UnixFileMode.UserExecute));
        }

        var result = await maps.RenderAsync();

        Assert.True(result.Succeeded, result.Message + "\n" + string.Join("\n", maps.RecentLog));
        Assert.Equal("unmined.index.html", maps.EntryPage(MapEngines.Unmined));
        Assert.NotNull(maps.ResolveMapFile("tiles/0_0.png"));
        Assert.Contains(maps.RecentLog, l => l == "Listo");
        Assert.Single(backups.List()); // hizo una copia porque no había ninguna reciente
        Assert.Empty(Directory.GetDirectories(maps.MapsRoot, ".work-*"));
        Assert.True(maps.Settings.LastRender!.Succeeded);
    }

    [Fact]
    public async Task Failed_render_keeps_the_previous_map()
    {
        await using var f = new SupervisorFixture();
        var (maps, _) = Create(f);
        await maps.InstallAsync(MapEngines.Unmined, new MemoryStream(TarGz("unmined-cli", FakeRenderer)), "u.tar.gz");
        Assert.True((await maps.RenderAsync()).Succeeded);

        // Ahora el motor falla: el mapa de antes tiene que seguir ahí.
        await maps.InstallAsync(MapEngines.Unmined, new MemoryStream(TarGz("unmined-cli", "#!/bin/sh\necho roto >&2\nexit 7\n")), "u.tar.gz");
        var result = await maps.RenderAsync();

        Assert.False(result.Succeeded);
        Assert.Contains("código 7", result.Message);
        Assert.Contains(maps.RecentLog, l => l == "roto");
        Assert.Equal("unmined.index.html", maps.EntryPage(MapEngines.Unmined));
    }

    [Fact]
    public async Task Render_without_html_output_fails_clearly()
    {
        await using var f = new SupervisorFixture();
        var (maps, _) = Create(f);
        await maps.InstallAsync(MapEngines.Unmined, new MemoryStream(TarGz("unmined-cli", "#!/bin/sh\necho nada\n")), "u.tar.gz");
        var result = await maps.RenderAsync();
        Assert.False(result.Succeeded);
        Assert.Contains(".html", result.Message);
    }

    [Fact]
    public async Task Installs_from_zip_raw_elf_and_url_and_rejects_junk()
    {
        await using var f = new SupervisorFixture();
        var handler = new StaticHandler(TarGz("unmined-cli", FakeRenderer));
        var (maps, _) = Create(f, handler);

        // Desde un enlace (el nombre sale de la URL).
        await maps.InstallFromUrlAsync(MapEngines.Unmined, "https://unmined.example/download/unmined-cli_linux-x64.tar.gz");
        Assert.NotNull(maps.GetStatus(MapEngines.Unmined).Executable);
        await Assert.ThrowsAsync<ArgumentException>(() => maps.InstallFromUrlAsync(MapEngines.Unmined, "http://inseguro/x.tar.gz"));

        // Un ejecutable suelto de Linux (ELF).
        var elf = File.ReadAllBytes("/bin/true");
        await maps.InstallAsync(MapEngines.BedrockViz, new MemoryStream(elf), "bedrock-viz");
        Assert.EndsWith("bedrock-viz", maps.GetStatus(MapEngines.BedrockViz).Executable);

        // Un .zip con el ejecutable dentro de una carpeta.
        using (var ms = new MemoryStream())
        {
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            {
                using var w = zip.CreateEntry("bedrock-viz-0.1.8/bin/bedrock-viz").Open();
                w.Write(elf);
            }
            ms.Position = 0;
            await maps.InstallAsync(MapEngines.BedrockViz, ms, "bedrock-viz.zip");
        }
        Assert.Contains("bin", maps.GetStatus(MapEngines.BedrockViz).Executable);

        await Assert.ThrowsAsync<InvalidDataException>(() => maps.InstallAsync(MapEngines.Unmined, new MemoryStream(Encoding.UTF8.GetBytes("hola")), "x.txt"));
        await Assert.ThrowsAsync<InvalidDataException>(() => maps.InstallAsync(MapEngines.Unmined, new MemoryStream(TarGz("otro-programa", "x")), "x.tar.gz"));

        maps.Uninstall(MapEngines.BedrockViz);
        Assert.Null(maps.GetStatus(MapEngines.BedrockViz).Executable);
    }

    [Fact]
    public async Task Settings_validate_command_and_time_and_map_files_cannot_escape()
    {
        await using var f = new SupervisorFixture();
        var (maps, _) = Create(f);

        maps.SaveSettings("bedrock-viz", "--db {world} --out {output}", nightly: false, time: "03:30");
        Assert.Equal("bedrock-viz", maps.ActiveEngine.Id);
        Assert.True(maps.GetStatus(MapEngines.BedrockViz).IsCustomCommand);
        Assert.False(maps.Settings.Nightly);

        maps.SaveSettings("bedrock-viz", MapEngines.BedrockViz.DefaultCommand, true, "04:15");
        Assert.False(maps.GetStatus(MapEngines.BedrockViz).IsCustomCommand);

        Assert.Throws<ArgumentException>(() => maps.SaveSettings("unmined", "--world {mundo}", true, "04:15"));
        Assert.Throws<ArgumentException>(() => maps.SaveSettings("unmined", null, true, "25:00"));
        Assert.Throws<ArgumentException>(() => maps.SaveSettings("otro", null, true, "04:15"));

        Assert.Null(maps.ResolveMapFile("../../state.json"));
        Assert.Null(maps.ResolveMapFile("/etc/passwd"));
    }

    private sealed class StaticHandler(byte[] body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body), RequestMessage = request });
    }
}
