using FcHelper.Services;
using FcHelper.Tests.Fixtures;

namespace FcHelper.Tests;

/// <summary>비교 tab: results by controller (OS-12) and by formation (OS-13).</summary>
public class SplitsTests
{
    [Fact]
    public void Controller_lines_add_up_to_all_matches()
    {
        var matches = new[]
        {
            new MatchBuilder("opp", "a").A(s => s.Controller("keyboard").Goal(1)).Build(),
            new MatchBuilder("opp", "b").A(s => s.Controller("keyboard")).B(s => s.Goal(2)).Build(),
            new MatchBuilder("opp", "c").A(s => s.Controller("gamepad")).Build(),
            new MatchBuilder("opp", "d").A(s => s.NoStats()).Build(),
        };

        var lines = Splits.ByController(matches, "opp");

        Assert.Equal(matches.Length, lines.Sum(l => l.Matches));
        Assert.Equal(Splits.Unknown, lines[^1].Key);
        var keyboard = lines[0];
        Assert.Equal((2, 1, 0, 1), (keyboard.Matches, keyboard.Wins, keyboard.Draws, keyboard.Losses));
        Assert.Equal(0.5, keyboard.WinRate);
        Assert.Equal((0.5, 0.5), (keyboard.GoalsFor, keyboard.GoalsAgainst));
    }

    [Fact]
    public void Formation_is_read_from_the_starters()
    {
        // GK, four at the back, two CDMs, CAM, two wide, one ST: a 4-2-3-1.
        int[] positions = [0, 3, 4, 6, 7, 10, 10, 18, 12, 16, 25];
        var match = new MatchBuilder("opp", "a").A(s => { foreach (var p in positions) s.Player(100 + p, p); }).Build();

        var line = Assert.Single(Splits.ByFormation([match], "opp"));

        Assert.Equal("4-2-3-1", line.Key);
    }
}
