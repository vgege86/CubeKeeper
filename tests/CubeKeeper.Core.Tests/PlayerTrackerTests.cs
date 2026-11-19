using CubeKeeper.Core.Players;

namespace CubeKeeper.Core.Tests;

public class PlayerTrackerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 19, 18, 0, 0, TimeSpan.Zero);
    private const string Ts = "[2026-11-19 18:00:00:000 INFO] ";

    [Fact]
    public void Connect_and_disconnect_produce_joined_and_left()
    {
        var t = new PlayerTracker();

        var joined = Assert.IsType<PlayerJoined>(Assert.Single(t.Process(Ts + "Player connected: Alucard#9583, xuid: 2535400000000001", T0)));
        Assert.Equal("2535400000000001", joined.Player.Xuid);
        Assert.Single(t.Online);

        var left = Assert.IsType<PlayerLeft>(Assert.Single(t.Process(Ts + "Player disconnected: Alucard#9583, xuid: 2535400000000001, pfid: 0", T0.AddMinutes(30))));
        Assert.Equal(LeaveReason.Disconnected, left.Reason);
        Assert.Equal(T0, left.Player.Since);
        Assert.Empty(t.Online);
    }

    [Fact]
    public void Unknown_disconnect_is_ignored()
    {
        var t = new PlayerTracker();
        Assert.Empty(t.Process(Ts + "Player disconnected: Nadie, xuid: 1", T0));
    }

    [Fact]
    public void Reconnect_without_disconnect_closes_previous_session()
    {
        var t = new PlayerTracker();
        t.Process(Ts + "Player connected: Steve, xuid: 1", T0);

        var changes = t.Process(Ts + "Player connected: Steve, xuid: 1", T0.AddMinutes(5));

        Assert.Collection(changes,
            c => Assert.Equal(LeaveReason.Reconnected, Assert.IsType<PlayerLeft>(c).Reason),
            c => Assert.IsType<PlayerJoined>(c));
        Assert.Single(t.Online);
    }

    [Fact]
    public void List_resync_removes_missing_and_adds_unseen_players()
    {
        var t = new PlayerTracker(name => name == "Alex" ? "2" : null);
        t.Process(Ts + "Player connected: Steve, xuid: 1", T0);

        Assert.Empty(t.Process(Ts + "There are 2/10 players online:", T0.AddMinutes(1)));
        var changes = t.Process("Alex, Nuevo", T0.AddMinutes(1));

        Assert.Contains(changes, c => c is PlayerLeft { Reason: LeaveReason.NotInList } l && l.Player.Name == "Steve");
        Assert.Contains(changes, c => c is PlayerJoined j && j.Player.Name == "Alex" && j.Player.Xuid == "2");
        Assert.Contains(changes, c => c is PlayerJoined j && j.Player.Name == "Nuevo" && j.Player.Xuid == null);
        Assert.Equal(["Alex", "Nuevo"], t.Online.Select(p => p.Name));
    }

    [Fact]
    public void List_keeps_players_already_known()
    {
        var t = new PlayerTracker();
        t.Process(Ts + "Player connected: Steve, xuid: 1", T0);
        t.Process(Ts + "There are 1/10 players online:", T0.AddMinutes(1));
        Assert.Empty(t.Process("Steve", T0.AddMinutes(1)));
        Assert.Equal(T0, Assert.Single(t.Online).Since);
    }

    [Fact]
    public void List_with_zero_players_clears_immediately()
    {
        var t = new PlayerTracker();
        t.Process(Ts + "Player connected: Steve, xuid: 1", T0);

        var changes = t.Process(Ts + "There are 0/10 players online:", T0.AddMinutes(1));

        Assert.Equal(LeaveReason.NotInList, Assert.IsType<PlayerLeft>(Assert.Single(changes)).Reason);
        Assert.Empty(t.Online);
    }

    [Fact]
    public void Player_seen_in_list_is_matched_when_connect_line_arrives_later()
    {
        var t = new PlayerTracker();
        t.Process(Ts + "There are 1/10 players online:", T0);
        t.Process("Steve", T0);

        var changes = t.Process(Ts + "Player connected: Steve, xuid: 1", T0.AddMinutes(1));

        Assert.Contains(changes, c => c is PlayerLeft { Reason: LeaveReason.Reconnected });
        Assert.Equal("1", Assert.Single(t.Online).Xuid);
    }

    [Fact]
    public void Event_instead_of_names_line_is_processed_normally()
    {
        var t = new PlayerTracker();
        t.Process(Ts + "There are 1/10 players online:", T0);
        var changes = t.Process(Ts + "Player connected: Steve, xuid: 1", T0);
        Assert.IsType<PlayerJoined>(Assert.Single(changes));
    }

    [Fact]
    public void Server_stop_closes_everyone()
    {
        var t = new PlayerTracker();
        t.Process(Ts + "Player connected: Steve, xuid: 1", T0);
        t.Process(Ts + "Player connected: Alex, xuid: 2", T0);

        var changes = t.Process(Ts + "Stopping server...", T0.AddHours(1));

        Assert.Equal(2, changes.Count);
        Assert.All(changes, c => Assert.Equal(LeaveReason.ServerStopped, Assert.IsType<PlayerLeft>(c).Reason));
        Assert.Empty(t.Online);
        Assert.Empty(t.ServerStopped(T0.AddHours(1)));
    }
}
