using CubeKeeper.Infrastructure.Versions;

namespace CubeKeeper.Infrastructure.Tests;

public class ServerInstallerTests
{
    [Fact]
    public void Copy_preserves_world_and_config_files()
    {
        var root = Path.Combine(Path.GetTempPath(), "ck-install-" + Guid.NewGuid().ToString("N"));
        var src = Path.Combine(root, "version");
        var dst = Path.Combine(root, "server");
        try
        {
            Write(src, "bedrock_server", "nuevo binario");
            Write(src, "server.properties", "plantilla");
            Write(src, "allowlist.json", "[]");
            Write(src, "permissions.json", "[]");
            Write(src, Path.Combine("behavior_packs", "vanilla", "manifest.json"), "nuevo");
            Write(src, "release-notes.txt", "nuevo");

            Write(dst, "bedrock_server", "binario viejo");
            Write(dst, "server.properties", "mis ajustes");
            Write(dst, "allowlist.json", "[mi lista]");
            Write(dst, Path.Combine("worlds", "Bedrock level", "level.dat"), "mi mundo");

            ServerInstaller.CopyIntoServer(src, dst);

            Assert.Equal("nuevo binario", Read(dst, "bedrock_server"));
            Assert.Equal("mis ajustes", Read(dst, "server.properties"));
            Assert.Equal("[mi lista]", Read(dst, "allowlist.json"));
            Assert.Equal("[]", Read(dst, "permissions.json")); // no existía: se copia
            Assert.Equal("mi mundo", Read(dst, Path.Combine("worlds", "Bedrock level", "level.dat")));
            Assert.Equal("nuevo", Read(dst, Path.Combine("behavior_packs", "vanilla", "manifest.json")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void Write(string dir, string rel, string content)
    {
        var path = Path.Combine(dir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static string Read(string dir, string rel) => File.ReadAllText(Path.Combine(dir, rel));
}
