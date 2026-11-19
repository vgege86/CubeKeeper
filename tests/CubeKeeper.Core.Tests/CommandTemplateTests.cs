using CubeKeeper.Core.Maps;

namespace CubeKeeper.Core.Tests;

public class CommandTemplateTests
{
    private static readonly Dictionary<string, string> Values = new()
    {
        ["world"] = "/data/map/.work/world con espacios",
        ["output"] = "/data/map/.work/output",
        ["threads"] = "4",
    };

    [Fact]
    public void Builds_unmined_default_command()
    {
        var args = CommandTemplate.Build(MapEngines.Unmined.DefaultCommand, Values);
        Assert.Equal(["web", "render", "--world", "/data/map/.work/world con espacios", "--output", "/data/map/.work/output"], args);
    }

    [Fact]
    public void Substituted_values_are_never_split_even_without_quotes()
    {
        var args = CommandTemplate.Build("--world={world} -t {threads}", Values);
        Assert.Equal(["--world=/data/map/.work/world con espacios", "-t", "4"], args);
    }

    [Fact]
    public void Quotes_group_literal_text()
    {
        Assert.Equal(["--title", "Mi mapa bonito", "x"], CommandTemplate.Build("--title \"Mi mapa bonito\"   x", Values));
        Assert.Equal(["", "a"], CommandTemplate.Build("\"\" a", Values));
    }

    [Theory]
    [InlineData("--world {mundo}")]
    [InlineData("--world {world")]
    [InlineData("--title \"sin cerrar")]
    [InlineData("a\nb")]
    public void Rejects_bad_templates(string template) =>
        Assert.Throws<ArgumentException>(() => CommandTemplate.Build(template, Values));

    [Fact]
    public void Every_engine_default_command_is_valid()
    {
        foreach (var engine in MapEngines.All)
        {
            var args = CommandTemplate.Build(engine.DefaultCommand, Values);
            Assert.Contains(Values["world"], args);
            Assert.Contains(Values["output"], args);
        }
    }
}
