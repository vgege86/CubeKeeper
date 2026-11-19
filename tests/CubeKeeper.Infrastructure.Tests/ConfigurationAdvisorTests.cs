using CubeKeeper.Infrastructure.Configuration;
using CubeKeeper.Infrastructure.Settings;

namespace CubeKeeper.Infrastructure.Tests;

public class ConfigurationAdvisorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ck-adv-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Apply_backs_up_and_fixes_file()
    {
        var paths = new DataPaths(_root);
        paths.EnsureCreated();
        File.WriteAllText(paths.ServerProperties, "transport=raknet\nserver-port=19132\n");
        var advisor = new ConfigurationAdvisor(paths, TimeProvider.System);

        Assert.NotEmpty(await advisor.CheckAsync());
        var changed = await advisor.ApplyAsync();

        Assert.Contains("transport", changed);
        Assert.Empty(await advisor.CheckAsync());
        var backup = Assert.Single(Directory.GetFiles(paths.Server, "server.properties.*.bak"));
        Assert.Equal("transport=raknet\nserver-port=19132\n", File.ReadAllText(backup));
    }

    [Fact]
    public async Task Missing_file_reports_nothing()
    {
        var advisor = new ConfigurationAdvisor(new DataPaths(_root), TimeProvider.System);
        Assert.Empty(await advisor.CheckAsync());
        Assert.Empty(await advisor.ApplyAsync());
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
