using CubeKeeper.Core.Properties;

namespace CubeKeeper.Core.Tests;

public class ServerPropertiesDocumentTests
{
    private const string Sample =
        "server-name=Dedicated Server\n" +
        "# Used as the server name\n" +
        "# Allowed values: Any string without semicolon symbol.\n" +
        "\n" +
        "gamemode=survival\n" +
        "# Sets the game mode for new players.\n" +
        "\n" +
        "server-port=19132\n" +
        "allow-list=false\n" +
        "level-name=Bedrock level\n" +
        "transport=nethernet\n" +
        "server-udp-ports=19140\n";

    [Fact]
    public void Round_trip_is_byte_identical()
    {
        Assert.Equal(Sample, ServerPropertiesDocument.Parse(Sample).ToString());
    }

    [Fact]
    public void Round_trip_preserves_crlf_and_missing_final_newline()
    {
        var text = "a=1\r\n# comentario\r\nb=2";
        Assert.Equal(text, ServerPropertiesDocument.Parse(text).ToString());
    }

    [Fact]
    public void Reads_values()
    {
        var doc = ServerPropertiesDocument.Parse(Sample);
        Assert.Equal("Bedrock level", doc.Get("level-name"));
        Assert.Equal(19132, doc.GetInt("server-port"));
        Assert.False(doc.GetBool("allow-list"));
        Assert.Equal("19140", doc.Get("server-udp-ports"));
        Assert.Null(doc.Get("no-existe"));
    }

    [Fact]
    public void Set_changes_only_that_line_and_keeps_comments()
    {
        var doc = ServerPropertiesDocument.Parse(Sample);
        Assert.True(doc.Set("allow-list", "true"));
        var expected = Sample.Replace("allow-list=false", "allow-list=true");
        Assert.Equal(expected, doc.ToString());
    }

    [Fact]
    public void Set_same_value_reports_no_change()
    {
        var doc = ServerPropertiesDocument.Parse(Sample);
        Assert.False(doc.Set("gamemode", "survival"));
        Assert.Equal(Sample, doc.ToString());
    }

    [Fact]
    public void Set_new_key_appends_at_end()
    {
        var doc = ServerPropertiesDocument.Parse(Sample);
        doc.Set("online-mode", "true");
        Assert.EndsWith("server-udp-ports=19140\nonline-mode=true\n", doc.ToString());
    }

    [Fact]
    public void Duplicate_key_last_wins_and_is_the_one_edited()
    {
        var doc = ServerPropertiesDocument.Parse("difficulty=easy\n# x\ndifficulty=hard\n");
        Assert.Equal("hard", doc.Get("difficulty"));
        doc.Set("difficulty", "normal");
        Assert.Equal("difficulty=easy\n# x\ndifficulty=normal\n", doc.ToString());
    }

    [Fact]
    public void Value_with_equals_sign_is_kept()
    {
        var doc = ServerPropertiesDocument.Parse("motd=a=b\n");
        Assert.Equal("a=b", doc.Get("motd"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("con=igual")]
    [InlineData("#comentario")]
    [InlineData(" espacio")]
    public void Rejects_invalid_keys(string key)
    {
        var doc = ServerPropertiesDocument.Empty();
        Assert.Throws<ArgumentException>(() => doc.Set(key, "x"));
    }

    [Fact]
    public void Rejects_values_with_newlines()
    {
        var doc = ServerPropertiesDocument.Empty();
        Assert.Throws<ArgumentException>(() => doc.Set("server-name", "a\nallow-list=false"));
    }

    [Fact]
    public void AddMissingFrom_adds_new_keys_with_their_comments_and_keeps_existing_values()
    {
        var current = ServerPropertiesDocument.Parse("server-port=19132\nallow-list=true\n");
        var template = ServerPropertiesDocument.Parse(
            "server-port=19132\n# puerto\nallow-list=false\nnueva-opcion=valor\n# Explica la nueva opción\n# en dos líneas\n\nlevel-name=x\n");

        var added = current.AddMissingFrom(template);

        Assert.Equal(["nueva-opcion", "level-name"], added);
        Assert.Equal(
            "server-port=19132\nallow-list=true\nnueva-opcion=valor\n# Explica la nueva opción\n# en dos líneas\nlevel-name=x\n",
            current.ToString());
    }

    [Fact]
    public async Task Save_and_load_round_trip()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ck-{Guid.NewGuid():N}.properties");
        try
        {
            var doc = ServerPropertiesDocument.Parse(Sample);
            doc.Set("server-name", "CubeKeeper");
            await doc.SaveAsync(path);
            var loaded = await ServerPropertiesDocument.LoadAsync(path);
            Assert.Equal("CubeKeeper", loaded.Get("server-name"));
            Assert.Equal(doc.ToString(), loaded.ToString());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Loading_missing_file_gives_empty_document()
    {
        var doc = await ServerPropertiesDocument.LoadAsync(Path.Combine(Path.GetTempPath(), "no-existe-" + Guid.NewGuid()));
        Assert.Empty(doc.Keys);
    }
}
