using CubeKeeper.Core.Logs;

namespace CubeKeeper.Core.Tests;

public class BdsLogParserTests
{
    [Theory]
    [InlineData("[2026-11-19 10:00:00:123 INFO] Player connected: Steve, xuid: 2535412345678901", "Steve", "2535412345678901")]
    [InlineData("[2026-11-19 10:00:00:123 INFO] Player connected: Alucard#9583, xuid: 2535400000000001", "Alucard#9583", "2535400000000001")]
    [InlineData("[2026-11-19 10:00:00 INFO] Player connected: Nombre Con Espacios, xuid: 42", "Nombre Con Espacios", "42")]
    [InlineData("Player connected: SinPrefijo, xuid: 7", "SinPrefijo", "7")]
    [InlineData("[2026-11-19 10:00:00:123 INFO] Player connected: Steve, xuid: 2535412345678901, pfid: abcdef0123456789", "Steve", "2535412345678901")]
    public void Parses_player_connected(string line, string name, string xuid)
    {
        var evt = Assert.IsType<PlayerConnected>(BdsLogParser.Parse(line));
        Assert.Equal(name, evt.Name);
        Assert.Equal(xuid, evt.Xuid);
    }

    [Fact]
    public void Parses_player_disconnected_with_pfid()
    {
        var evt = BdsLogParser.Parse("[2026-11-19 10:05:00:001 INFO] Player disconnected: Alucard#9583, xuid: 2535400000000001, pfid: 0123456789abcdef");
        var d = Assert.IsType<PlayerDisconnected>(evt);
        Assert.Equal("Alucard#9583", d.Name);
        Assert.Equal("2535400000000001", d.Xuid);
    }

    [Fact]
    public void Parses_player_spawned()
    {
        var evt = BdsLogParser.Parse("[2026-11-19 10:00:02:000 INFO] Player Spawned: Steve xuid: 2535412345678901, pfid: 0000");
        var s = Assert.IsType<PlayerSpawned>(evt);
        Assert.Equal("Steve", s.Name);
        Assert.Equal("2535412345678901", s.Xuid);
    }

    [Fact]
    public void Offline_player_has_empty_xuid()
    {
        var evt = Assert.IsType<PlayerConnected>(BdsLogParser.Parse("[2026-11-19 10:00:00:000 INFO] Player connected: Local, xuid: "));
        Assert.Equal("Local", evt.Name);
        Assert.Equal(string.Empty, evt.Xuid);
    }

    [Fact]
    public void Parses_server_started_and_version()
    {
        Assert.IsType<ServerStarted>(BdsLogParser.Parse("[2026-11-19 10:00:00:000 INFO] Server started."));
        var v = Assert.IsType<ServerVersionReported>(BdsLogParser.Parse("[2026-11-19 10:00:00:000 INFO] Version: 1.26.10.4"));
        Assert.Equal("1.26.10.4", v.Version);
        Assert.IsType<ServerStopping>(BdsLogParser.Parse("[2026-11-19 10:00:00:000 INFO] Stopping server..."));
    }

    [Fact]
    public void Parses_list_header()
    {
        var evt = Assert.IsType<PlayerCountReported>(BdsLogParser.Parse("[2026-11-19 10:00:00:000 INFO] There are 2/10 players online:"));
        Assert.Equal(2, evt.Online);
        Assert.Equal(10, evt.Max);
    }

    [Theory]
    [InlineData("")]
    [InlineData("NO LOG FILE! - setting up server logging...")]
    [InlineData("[2026-11-19 10:00:00:000 INFO] Level Name: Bedrock level")]
    [InlineData("[2026-11-19 10:00:00:000 INFO] [Server] Player connected: Falso, xuid: 1")]
    [InlineData("Steve, Alex")]
    public void Ignores_other_lines(string line) => Assert.Null(BdsLogParser.Parse(line));

    [Fact]
    public void Splits_prefix_into_timestamp_level_and_message()
    {
        var line = BdsLogLine.Parse("[2026-11-19 10:00:01:250 WARN] Algo raro\r");
        Assert.Equal("WARN", line.Level);
        Assert.Equal("Algo raro", line.Message);
        Assert.NotNull(line.Timestamp);
        Assert.Equal(new DateTime(2026, 11, 19, 10, 0, 1, 250), line.Timestamp!.Value.DateTime);
    }

    [Fact]
    public void Line_without_prefix_keeps_raw_message()
    {
        var line = BdsLogLine.Parse("Quit correctly");
        Assert.Null(line.Timestamp);
        Assert.Null(line.Level);
        Assert.Equal("Quit correctly", line.Message);
    }
}
