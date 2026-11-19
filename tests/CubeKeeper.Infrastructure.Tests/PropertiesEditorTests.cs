using CubeKeeper.Infrastructure.Configuration;
using CubeKeeper.Infrastructure.Settings;

namespace CubeKeeper.Infrastructure.Tests;

public class PropertiesEditorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ck-ed-" + Guid.NewGuid().ToString("N"));
    private readonly DataPaths _paths;
    private const string Original = "server-name=Casa\n# Used as the server name\ndifficulty=easy\nserver-port=19132\nserver-udp-ports=19140\n";

    public PropertiesEditorTests()
    {
        _paths = new DataPaths(_root);
        _paths.EnsureCreated();
        File.WriteAllText(_paths.ServerProperties, Original);
    }

    [Fact]
    public async Task Load_returns_fields_with_definitions_and_comments()
    {
        var fields = await new PropertiesEditor(_paths, TimeProvider.System).LoadAsync();
        var name = fields.Single(f => f.Definition.Key == "server-name");
        Assert.Equal("Casa", name.Value);
        Assert.Equal("Nombre del servidor", name.Definition.Label);
        Assert.Equal("Used as the server name", name.FileComment);
    }

    [Fact]
    public async Task Save_changes_values_keeps_comments_and_makes_backup()
    {
        var result = await new PropertiesEditor(_paths, TimeProvider.System).SaveAsync(new Dictionary<string, string>
        {
            ["difficulty"] = "normal",
            ["server-name"] = "Casa",
        });

        Assert.True(result.Succeeded);
        Assert.Equal(["difficulty"], result.Changed);
        Assert.Equal(Original.Replace("difficulty=easy", "difficulty=normal"), File.ReadAllText(_paths.ServerProperties));
        Assert.Equal(Original, File.ReadAllText(result.BackupPath!));
    }

    [Fact]
    public async Task Invalid_values_save_nothing()
    {
        var result = await new PropertiesEditor(_paths, TimeProvider.System).SaveAsync(new Dictionary<string, string>
        {
            ["difficulty"] = "imposible",
            ["server-udp-ports"] = "19132",
            ["server-name"] = "Otro",
        });

        Assert.False(result.Succeeded);
        Assert.Contains("difficulty", result.Errors.Keys);
        Assert.Contains("server-udp-ports", result.Errors.Keys);
        Assert.Equal(Original, File.ReadAllText(_paths.ServerProperties));
        Assert.Empty(Directory.GetFiles(_paths.Server, "*.bak"));
    }

    [Fact]
    public async Task Unknown_keys_cannot_be_added_from_the_editor()
    {
        var result = await new PropertiesEditor(_paths, TimeProvider.System).SaveAsync(new Dictionary<string, string> { ["inventada"] = "1" });
        Assert.Contains("inventada", result.Errors.Keys);
    }

    [Fact]
    public async Task No_changes_means_no_backup()
    {
        var result = await new PropertiesEditor(_paths, TimeProvider.System).SaveAsync(new Dictionary<string, string> { ["difficulty"] = "easy" });
        Assert.True(result.Succeeded);
        Assert.Empty(result.Changed);
        Assert.Null(result.BackupPath);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
