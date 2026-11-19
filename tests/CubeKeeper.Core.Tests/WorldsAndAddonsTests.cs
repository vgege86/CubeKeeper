using CubeKeeper.Core.Addons;
using CubeKeeper.Core.Worlds;

namespace CubeKeeper.Core.Tests;

public class WorldNamesTests
{
    [Theory]
    [InlineData("Bedrock level", true)]
    [InlineData("Mundo de Lucía", true)]
    [InlineData("", false)]
    [InlineData(" espacio", false)]
    [InlineData(".oculto", false)]
    [InlineData("..", false)]
    [InlineData("a/b", false)]
    [InlineData("a\\b", false)]
    [InlineData("dos:puntos", false)]
    public void Validates(string name, bool valid) => Assert.Equal(valid, WorldNames.Validate(name) is null);

    [Theory]
    [InlineData("Mi/Mundo: v2", "Mi Mundo v2")]
    [InlineData("..raro", "raro")]
    [InlineData("", "Mundo importado")]
    public void Builds_names_from_text(string text, string expected) => Assert.Equal(expected, WorldNames.FromText(text));

    [Fact]
    public void Long_text_is_truncated() => Assert.Equal(WorldNames.MaxLength, WorldNames.FromText(new string('a', 200)).Length);
}

public class PackManifestTests
{
    [Fact]
    public void Parses_behavior_pack_with_comments_and_trailing_commas()
    {
        var m = PackManifest.Parse("""
            {
              // comentario
              "format_version": 2,
              "header": { "name": "Mi complemento", "description": "Cosas", "uuid": "11111111-2222-3333-4444-555555555555", "version": [1, 2, 3], },
              "modules": [ { "type": "data", "uuid": "aaaaaaaa-2222-3333-4444-555555555555", "version": [1, 2, 3] } ]
            }
            """)!;
        Assert.Equal(PackType.Behavior, m.Type);
        Assert.Equal("1.2.3", m.VersionText);
        Assert.Equal("Mi complemento", m.Name);
        Assert.False(m.UsesScripts);
    }

    [Fact]
    public void Detects_resource_pack_scripts_and_string_versions()
    {
        var r = PackManifest.Parse("""{"header":{"name":"Texturas","uuid":"11111111-2222-3333-4444-555555555555","version":"2.0.1"},"modules":[{"type":"resources"}]}""")!;
        Assert.Equal(PackType.Resource, r.Type);
        Assert.Equal([2, 0, 1], r.Version);

        var s = PackManifest.Parse("""{"header":{"name":"S","uuid":"11111111-2222-3333-4444-555555555556","version":[1,0,0]},"modules":[{"type":"script","language":"javascript"}]}""")!;
        Assert.Equal(PackType.Behavior, s.Type);
        Assert.True(s.UsesScripts);
    }

    [Fact]
    public void Resolves_localized_names()
    {
        var lang = "## comentario\npack.name=Mi paquete\npack.description=Hace cosas\t## nota\n";
        var m = PackManifest.Parse("""{"header":{"name":"pack.name","description":"pack.description","uuid":"11111111-2222-3333-4444-555555555555","version":[1,0,0]},"modules":[{"type":"data"}]}""",
            key => PackManifest.LookupLang(lang, key))!;
        Assert.Equal("Mi paquete", m.Name);
        Assert.Equal("Hace cosas", m.Description);
    }

    [Theory]
    [InlineData("no es json")]
    [InlineData("""{"header":{"name":"x","uuid":"no-uuid","version":[1,0,0]},"modules":[{"type":"data"}]}""")]
    [InlineData("""{"header":{"name":"x","uuid":"11111111-2222-3333-4444-555555555555","version":[1,0,0]},"modules":[{"type":"skin_pack"}]}""")]
    [InlineData("""{"header":{"name":"x","uuid":"11111111-2222-3333-4444-555555555555"},"modules":[{"type":"data"}]}""")]
    public void Rejects_non_activatable_or_invalid(string json) => Assert.Null(PackManifest.Parse(json));

    [Fact]
    public void World_packs_document_enables_updates_and_disables()
    {
        var id = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var doc = WorldPacksDocument.Parse("""[{"pack_id":"22222222-2222-3333-4444-555555555555","version":[1,0,0]}]""");

        Assert.True(doc.Enable(id, [1, 0, 0]));
        Assert.False(doc.Enable(id, [1, 0, 0]));
        Assert.True(doc.Enable(id, [1, 1, 0]));
        Assert.Equal(2, doc.Packs.Count);

        var again = WorldPacksDocument.Parse(doc.ToJson());
        Assert.Equal([1, 1, 0], again.Packs.Single(p => p.Id == id).Version);
        Assert.True(again.Disable(id));
        Assert.False(again.Contains(id));
        Assert.Empty(WorldPacksDocument.Parse("").Packs);
    }
}
