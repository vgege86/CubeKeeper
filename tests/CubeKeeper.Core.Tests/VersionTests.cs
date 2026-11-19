using CubeKeeper.Core.Versions;

namespace CubeKeeper.Core.Tests;

public class VersionTests
{
    [Theory]
    [InlineData("1.26.10.4", "1.26.9.10", 1)]
    [InlineData("1.26.0.2", "1.26.0.2", 0)]
    [InlineData("1.26", "1.26.0.0", 0)]
    [InlineData("1.21.130.3", "1.26.0.2", -1)]
    public void Compares_numerically(string a, string b, int expected)
    {
        Assert.Equal(expected, Math.Sign(BdsVersion.Parse(a).CompareTo(BdsVersion.Parse(b))));
    }

    [Theory]
    [InlineData("https://www.minecraft.net/bedrockdedicatedserver/bin-linux/bedrock-server-1.26.10.4.zip", "1.26.10.4")]
    [InlineData("https://www.minecraft.net/bedrockdedicatedserver/bin-linux-preview/bedrock-server-1.26.20.21.zip", "1.26.20.21")]
    public void Extracts_version_from_url(string url, string version)
    {
        Assert.Equal(version, BdsVersion.FromDownloadUrl(url)!.Text);
    }

    [Theory]
    [InlineData("latest")]
    [InlineData("1")]
    [InlineData("1.x.2")]
    [InlineData(null)]
    public void Rejects_invalid_versions(string? text) => Assert.False(BdsVersion.TryParse(text, out _));

    private const string OfficialJson = """
        {
          "result": {
            "links": [
              { "downloadType": "serverBedrockWindows", "downloadUrl": "https://www.minecraft.net/bedrockdedicatedserver/bin-win/bedrock-server-1.26.10.4.zip" },
              { "downloadType": "serverBedrockLinux", "downloadUrl": "https://www.minecraft.net/bedrockdedicatedserver/bin-linux/bedrock-server-1.26.10.4.zip" },
              { "downloadType": "serverBedrockPreviewWindows", "downloadUrl": "https://www.minecraft.net/bedrockdedicatedserver/bin-win-preview/bedrock-server-1.26.20.21.zip" },
              { "downloadType": "serverBedrockPreviewLinux", "downloadUrl": "https://www.minecraft.net/bedrockdedicatedserver/bin-linux-preview/bedrock-server-1.26.20.21.zip" }
            ]
          }
        }
        """;

    [Fact]
    public void Parses_official_links_for_stable_linux()
    {
        var r = DownloadLinksParser.ParseMinecraftServices(OfficialJson, ReleaseChannel.Stable)!;
        Assert.Equal("1.26.10.4", r.Version.Text);
        Assert.Contains("bin-linux/", r.DownloadUrl.ToString());
        Assert.Equal(ReleaseChannel.Stable, r.Channel);
    }

    [Fact]
    public void Parses_official_links_for_preview_linux()
    {
        var r = DownloadLinksParser.ParseMinecraftServices(OfficialJson, ReleaseChannel.Preview)!;
        Assert.Equal("1.26.20.21", r.Version.Text);
        Assert.Contains("bin-linux-preview/", r.DownloadUrl.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("no es json")]
    [InlineData("{}")]
    [InlineData("""{"result":{"links":[]}}""")]
    [InlineData("""{"result":{"links":[{"downloadType":"serverBedrockLinux","downloadUrl":"http://inseguro/bedrock-server-1.2.3.4.zip"}]}}""")]
    public void Official_parser_returns_null_for_bad_input(string json) =>
        Assert.Null(DownloadLinksParser.ParseMinecraftServices(json, ReleaseChannel.Stable));

    [Fact]
    public void Parses_community_index_and_picks_highest_version()
    {
        const string json = """
            {
              "release": {
                "1.21.130.3": { "linux": { "url": "https://www.minecraft.net/bedrockdedicatedserver/bin-linux/bedrock-server-1.21.130.3.zip" } },
                "1.26.10.4": { "linux": { "url": "https://www.minecraft.net/bedrockdedicatedserver/bin-linux/bedrock-server-1.26.10.4.zip" } },
                "1.26.0.2": { "linux": { "url": "https://www.minecraft.net/bedrockdedicatedserver/bin-linux/bedrock-server-1.26.0.2.zip" } }
              },
              "preview": {}
            }
            """;
        var r = DownloadLinksParser.ParseCommunityIndex(json, ReleaseChannel.Stable)!;
        Assert.Equal("1.26.10.4", r.Version.Text);
        Assert.Null(DownloadLinksParser.ParseCommunityIndex(json, ReleaseChannel.Preview));
    }
}
