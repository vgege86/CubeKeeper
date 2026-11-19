using CubeKeeper.Core.Access;
using CubeKeeper.Core.GameRules;

namespace CubeKeeper.Core.Tests;

public class AllowListDocumentTests
{
    [Fact]
    public void Parses_bds_allowlist_including_name_only_entries()
    {
        var doc = AllowListDocument.Parse("""
            [
              {"ignoresPlayerLimit": false, "name": "Alucard#9583", "xuid": "2535400000000001"},
              {"ignoresPlayerLimit": true, "name": "SoloNombre"}
            ]
            """);
        Assert.Equal(2, doc.Entries.Count);
        Assert.True(doc.Contains("2535400000000001"));
        Assert.Null(doc.Entries[1].Xuid);
        Assert.True(doc.Entries[1].IgnoresPlayerLimit);
    }

    [Fact]
    public void Add_is_by_xuid_and_replaces_name_only_entry()
    {
        var doc = AllowListDocument.Parse("""[{"ignoresPlayerLimit":false,"name":"Peque"}]""");

        Assert.True(doc.Add("Peque", "2535400000000002"));
        Assert.False(doc.Add("Peque", "2535400000000002"));
        Assert.True(doc.Add("Peque#1234", "2535400000000002")); // cambio de gamertag

        var e = Assert.Single(doc.Entries);
        Assert.Equal("Peque#1234", e.Name);
        Assert.Equal("2535400000000002", e.Xuid);
    }

    [Fact]
    public void Remove_and_round_trip()
    {
        var doc = AllowListDocument.Empty();
        doc.Add("A", "1");
        doc.Add("B", "2");
        Assert.True(doc.Remove("1"));
        Assert.False(doc.Remove("1"));

        var again = AllowListDocument.Parse(doc.ToJson());
        Assert.Equal(["B"], again.Entries.Select(e => e.Name));
        Assert.Contains("\"ignoresPlayerLimit\": false", doc.ToJson());
    }

    [Fact]
    public void Empty_or_whitespace_file_is_empty_list() => Assert.Empty(AllowListDocument.Parse("  ").Entries);

    [Fact]
    public void Non_array_is_rejected() => Assert.ThrowsAny<System.Text.Json.JsonException>(() => AllowListDocument.Parse("""{"name":"x"}"""));
}

public class PermissionsDocumentTests
{
    [Fact]
    public void Parses_sets_and_serializes()
    {
        var doc = PermissionsDocument.Parse("""[{"permission":"operator","xuid":"1"},{"permission":"raro","xuid":"2"}]""");
        Assert.Equal(PermissionLevel.Operator, doc.Get("1"));
        Assert.Null(doc.Get("2"));

        Assert.True(doc.Set("2", PermissionLevel.Visitor));
        Assert.False(doc.Set("2", PermissionLevel.Visitor));
        Assert.True(doc.Set("1", null));

        var again = PermissionsDocument.Parse(doc.ToJson());
        Assert.Equal(PermissionLevel.Visitor, again.Get("2"));
        Assert.Null(again.Get("1"));
        Assert.Contains("\"permission\": \"visitor\"", doc.ToJson());
    }
}

public class AccessPolicyTests
{
    private static JoinAction Decide(string? xuid, AccessMode mode, bool enrollment, string[]? approved = null, string[]? blocked = null) =>
        AccessPolicy.Decide(xuid, mode, enrollment, x => approved?.Contains(x) == true, x => blocked?.Contains(x) == true);

    [Fact]
    public void Open_mode_lets_everyone_in_except_blocked()
    {
        Assert.Equal(JoinAction.Allow, Decide("1", AccessMode.Open, false));
        Assert.Equal(JoinAction.KickBlocked, Decide("1", AccessMode.Open, false, blocked: ["1"]));
    }

    [Fact]
    public void Approved_only_mode()
    {
        Assert.Equal(JoinAction.Allow, Decide("1", AccessMode.ApprovedOnly, false, approved: ["1"]));
        Assert.Equal(JoinAction.KickNotApproved, Decide("2", AccessMode.ApprovedOnly, false, approved: ["1"]));
        Assert.Equal(JoinAction.RecordRequestAndKick, Decide("2", AccessMode.ApprovedOnly, true, approved: ["1"]));
        Assert.Equal(JoinAction.Allow, Decide("1", AccessMode.ApprovedOnly, true, approved: ["1"]));
    }

    [Fact]
    public void Blocked_wins_over_approved()
    {
        Assert.Equal(JoinAction.KickBlocked, Decide("1", AccessMode.ApprovedOnly, true, approved: ["1"], blocked: ["1"]));
    }

    [Fact]
    public void Without_xuid_only_open_mode_allows()
    {
        Assert.Equal(JoinAction.Allow, Decide(null, AccessMode.Open, false));
        Assert.Equal(JoinAction.KickNotApproved, Decide(null, AccessMode.ApprovedOnly, true));
    }

    [Theory]
    [InlineData("Alucard#9583", "\"Alucard#9583\"")]
    [InlineData("Con \"comillas\"", "\"Con comillas\"")]
    public void Quotes_names(string name, string expected) => Assert.Equal(expected, AccessPolicy.QuoteName(name));

    [Fact]
    public void Safe_text_strips_newlines_and_quotes() => Assert.Equal("hola stop", AccessPolicy.SafeText("hola\n\"stop\""));
}

public class GameRulesTests
{
    [Fact]
    public void Parses_bedrock_gamerule_listing()
    {
        var values = GameRuleCatalog.ParseValues(["commandblockoutput = true, dodaylightcycle = false, keepinventory = true, randomtickspeed = 3"]);
        Assert.Equal("false", values["doDaylightCycle"]);
        Assert.Equal("true", values["keepInventory"]);
        Assert.Equal("3", values["randomTickSpeed"]);
        Assert.True(values.ContainsKey("commandblockoutput"));
    }

    [Fact]
    public void Builds_set_commands_with_validation()
    {
        Assert.Equal("gamerule keepInventory true", GameRuleCatalog.SetCommand("keepinventory", "true"));
        Assert.Equal("gamerule playersSleepingPercentage 50", GameRuleCatalog.SetCommand("playersSleepingPercentage", "50"));
        Assert.Throws<ArgumentException>(() => GameRuleCatalog.SetCommand("keepInventory", "sí"));
        Assert.Throws<ArgumentException>(() => GameRuleCatalog.SetCommand("playersSleepingPercentage", "150"));
        Assert.Throws<ArgumentException>(() => GameRuleCatalog.SetCommand("inventada", "true"));
    }

    [Fact]
    public void Every_rule_has_spanish_label_and_help() =>
        Assert.All(GameRuleCatalog.All, r => Assert.False(string.IsNullOrWhiteSpace(r.Help) || r.Label == r.Name));
}
