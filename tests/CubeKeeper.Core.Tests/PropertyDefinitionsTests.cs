using CubeKeeper.Core.Properties;

namespace CubeKeeper.Core.Tests;

public class PropertyDefinitionsTests
{
    [Theory]
    [InlineData("difficulty", "normal", true)]
    [InlineData("difficulty", "Normal", false)]
    [InlineData("difficulty", "", false)]
    [InlineData("online-mode", "true", true)]
    [InlineData("online-mode", "sí", false)]
    [InlineData("max-players", "10", true)]
    [InlineData("max-players", "0", false)]
    [InlineData("max-players", "diez", false)]
    [InlineData("server-port", "70000", false)]
    [InlineData("level-seed", "", true)]
    [InlineData("server-ip", "", true)]
    [InlineData("server-name", "Casa", true)]
    [InlineData("server-name", "a\nallow-list=true", false)]
    [InlineData("clave-desconocida", "lo que sea", true)]
    public void Validates_values(string key, string value, bool valid)
    {
        Assert.Equal(valid, PropertyDefinitions.For(key).Validate(value) is null);
    }

    [Fact]
    public void Unknown_keys_are_free_text_in_other_category()
    {
        var def = PropertyDefinitions.For("algo-nuevo-de-mojang");
        Assert.Equal(PropertyKind.Text, def.Kind);
        Assert.Equal(PropertyDefinitions.Other, def.Category);
        Assert.Null(PropertyDefinitions.Find("algo-nuevo-de-mojang"));
    }

    [Fact]
    public void Udp_port_cannot_match_tcp_port()
    {
        var errors = PropertyDefinitions.ValidateTogether(new Dictionary<string, string>
        {
            ["server-port"] = "19132",
            ["server-udp-ports"] = "19132",
        }).ToList();
        Assert.Equal("server-udp-ports", Assert.Single(errors).Key);

        Assert.Empty(PropertyDefinitions.ValidateTogether(new Dictionary<string, string>
        {
            ["server-port"] = "19132",
            ["server-udp-ports"] = "19140",
        }));
    }

    [Fact]
    public void Every_definition_has_a_known_category()
    {
        Assert.All(PropertyDefinitions.All, d => Assert.Contains(d.Category, PropertyDefinitions.CategoryOrder));
        Assert.Equal(PropertyDefinitions.All.Count, PropertyDefinitions.All.Select(d => d.Key).Distinct().Count());
    }

    [Fact]
    public void Document_returns_comment_lines_after_key()
    {
        var doc = ServerPropertiesDocument.Parse("gamemode=survival\n# Sets the game mode.\n# Allowed values: survival\n\ndifficulty=easy\n");
        Assert.Equal("Sets the game mode. Allowed values: survival", doc.GetComment("gamemode"));
        Assert.Null(doc.GetComment("difficulty"));
        Assert.Null(doc.GetComment("no-existe"));
    }
}
