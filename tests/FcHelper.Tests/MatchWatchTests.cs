using FcHelper.Core;

namespace FcHelper.Tests;

/// <summary>완전 자동: when a match starts, which opponent it is, and when it is over (AM-02).</summary>
public class MatchWatchTests
{
    [Fact]
    public void A_full_match_ends_after_the_scoreboard_stays_away()
    {
        var w = new MatchWatch();
        Assert.Equal(WatchEvent.NewOpponent, w.Found("AUD콜대원", fromMatchScreen: true));
        foreach (var minute in new[] { 1, 30, 60, 90 }) Assert.Equal(WatchEvent.None, w.Scoreboard(minute));
        for (var i = 1; i < MatchWatch.EndMissingLooks; i++) Assert.Equal(WatchEvent.None, w.Scoreboard(null));
        Assert.Equal(WatchEvent.MatchEnded, w.Scoreboard(null));
        Assert.Equal(90, w.LastClock);
        Assert.Equal("AUD콜대원", w.Opponent);
    }

    [Fact]
    public void A_replay_that_hides_the_scoreboard_is_not_the_end()
    {
        var w = new MatchWatch();
        w.Scoreboard(70);
        for (var i = 0; i < MatchWatch.EndMissingLooks + 3; i++) Assert.Equal(WatchEvent.None, w.Scoreboard(null));
        Assert.Equal(WatchEvent.None, w.Scoreboard(71));
        Assert.True(w.InGame);
    }

    [Fact]
    public void A_long_absence_mid_match_means_someone_left()
    {
        var w = new MatchWatch();
        w.Scoreboard(40);
        var events = Enumerable.Range(0, MatchWatch.QuitMissingLooks).Select(_ => w.Scoreboard(null)).ToList();
        Assert.Equal(WatchEvent.MatchEnded, events[^1]);
        Assert.All(events.SkipLast(1), e => Assert.Equal(WatchEvent.None, e));
    }

    [Fact]
    public void The_same_opponent_is_reported_once_and_a_clock_reset_starts_a_new_match()
    {
        var w = new MatchWatch();
        w.Scoreboard(10);
        Assert.True(w.NeedsOpponent);
        Assert.Equal(WatchEvent.NewOpponent, w.Found("상대", fromMatchScreen: false));
        Assert.Equal(WatchEvent.None, w.Found("상대", fromMatchScreen: false));
        w.Scoreboard(80);
        w.Scoreboard(2); // next match already running
        Assert.True(w.NeedsOpponent);
        Assert.Equal(WatchEvent.None, w.Scoreboard(null)); // nothing seen at 89+ yet
    }

    [Fact]
    public void Nothing_happens_outside_a_match()
    {
        var w = new MatchWatch();
        Assert.All(Enumerable.Range(0, 50).Select(_ => w.Scoreboard(null)), e => Assert.Equal(WatchEvent.None, e));
    }
}
