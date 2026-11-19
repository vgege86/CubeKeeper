using CubeKeeper.Core.Properties;
using CubeKeeper.Core.Versions;

namespace CubeKeeper.Core.Tests;

public class RecommendedSettingsTests
{
    private const string Good =
        "server-name=Casa\ntransport=nethernet\nserver-port=19132\nserver-udp-ports=19140\n" +
        "enable-lan-visibility=true\nonline-mode=true\nallow-list=false\n";

    [Fact]
    public void Tested_configuration_has_no_deviations()
    {
        Assert.Empty(RecommendedSettings.Check(ServerPropertiesDocument.Parse(Good)));
    }

    [Fact]
    public void Detects_raknet_colliding_ports_and_server_ip()
    {
        var doc = ServerPropertiesDocument.Parse(
            Good.Replace("transport=nethernet", "transport=raknet").Replace("server-udp-ports=19140", "server-udp-ports=19132")
            + "server-ip=192.168.1.21\n");

        var keys = RecommendedSettings.Check(doc).Select(d => d.Setting.Key).ToArray();

        Assert.Equal(["transport", "server-udp-ports", "server-ip"], keys);
    }

    [Fact]
    public void Empty_server_ip_is_fine_and_missing_server_ip_is_not_added()
    {
        Assert.Empty(RecommendedSettings.Check(ServerPropertiesDocument.Parse(Good + "server-ip=\n")));
        var doc = ServerPropertiesDocument.Parse(Good.Replace("allow-list=false", "allow-list=true"));
        RecommendedSettings.Apply(doc);
        Assert.False(doc.ContainsKey("server-ip"));
    }

    [Fact]
    public void Apply_fixes_values_and_keeps_comments()
    {
        var doc = ServerPropertiesDocument.Parse("transport=raknet\n# Transporte de red\nserver-ip=192.168.1.21\nallow-list=true\n");

        var changed = RecommendedSettings.Apply(doc);

        Assert.Contains("transport", changed);
        Assert.Contains("server-ip", changed);
        Assert.Contains("server-udp-ports", changed);
        var text = doc.ToString();
        Assert.StartsWith("transport=nethernet\n# Transporte de red\nserver-ip=\nallow-list=false\n", text);
        Assert.Empty(RecommendedSettings.Check(doc));
    }

    [Fact]
    public void Initial_defaults_only_when_asked()
    {
        var doc = ServerPropertiesDocument.Parse("level-name=Otro\n");
        RecommendedSettings.Apply(doc);
        Assert.Equal("Otro", doc.Get("level-name"));
        RecommendedSettings.Apply(doc, includeInitialDefaults: true);
        Assert.Equal("Bedrock level", doc.Get("level-name"));
        Assert.Equal("normal", doc.Get("difficulty"));
        Assert.Equal("survival", doc.Get("gamemode"));
    }

    [Fact]
    public void Pinned_version_rewrites_download_url()
    {
        var latest = new BdsRelease(BdsVersion.Parse("1.26.70.2"),
            new Uri("https://www.minecraft.net/bedrockdedicatedserver/bin-linux/bedrock-server-1.26.70.2.zip"),
            ReleaseChannel.Stable, "minecraft-services");

        var pinned = latest.WithVersion(BdsVersion.Parse("1.26.65.1"));

        Assert.Equal("1.26.65.1", pinned.Version.Text);
        Assert.Equal("https://www.minecraft.net/bedrockdedicatedserver/bin-linux/bedrock-server-1.26.65.1.zip", pinned.DownloadUrl.ToString());
        Assert.Contains("fijada", pinned.Source);
    }
}
