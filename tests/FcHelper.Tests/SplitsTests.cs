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

/// <summary>비교 tab: the lineup that stands for a formation (OS-14).</summary>
public class RepresentativeLineupTests
{
    private static readonly int[] Shape = [0, 3, 4, 6, 7, 10, 10, 18, 12, 16, 25]; // 4-2-3-1

    private static FcHelper.Core.Models.MatchDetail Match(int striker, int daysAgo) =>
        new MatchBuilder("opp", "x").At(new DateTime(2026, 9, 20, 0, 0, 0, DateTimeKind.Utc).AddDays(-daysAgo))
            .A(s => { foreach (var p in Shape) s.Player(p == 25 ? striker : 100 + p, p); }).Build();

    [Fact]
    public void The_usual_eleven_wins_over_a_one_off_change()
    {
        // Newest first, like report.Matches: the newest match tried striker 2, the two before used striker 1.
        var matches = new[] { Match(2, 0), Match(1, 1), Match(1, 2) };

        var pick = Splits.Representative(matches, "opp", "4-2-3-1");

        Assert.NotNull(pick);
        Assert.Equal(3, pick.Value.Matches);
        Assert.Contains(pick.Value.Side.Player, p => p.SpId == 1);
        Assert.Same(matches[1].SideOf("opp"), pick.Value.Side); // the newer of the two equal ones
    }

    [Fact]
    public void Unused_formation_has_no_lineup() => Assert.Null(Splits.Representative([Match(1, 0)], "opp", "4-4-2"));
}
