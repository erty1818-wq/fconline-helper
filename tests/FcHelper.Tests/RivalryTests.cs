using FcHelper.Services;
using FcHelper.Tests.Fixtures;

namespace FcHelper.Tests;

/// <summary>비교 tab: two managers against each other (OS-16).</summary>
public class RivalryTests
{
    [Fact]
    public void Only_games_between_the_two_count_from_the_first_ones_side()
    {
        var matches = new[]
        {
            new MatchBuilder("a", "b").At(new DateTime(2026, 9, 10, 0, 0, 0, DateTimeKind.Utc)).A(s => s.Goal(1).Goal(1)).B(s => s.Goal(2)).Build(),
            new MatchBuilder("b", "a").At(new DateTime(2026, 9, 12, 0, 0, 0, DateTimeKind.Utc)).A(s => s.Goal(2)).Build(),
            new MatchBuilder("a", "c").A(s => s.Goal(1)).Build(),
        };

        var r = Rivalry.Of(matches, "a", "b");

        Assert.Equal((2, 1, 0, 1), (r.Matches, r.Wins, r.Draws, r.Losses));
        Assert.Equal((2, 2, 0), (r.GoalsFor, r.GoalsAgainst, r.GoalDifference));
        Assert.Equal(new DateTime(2026, 9, 12, 0, 0, 0, DateTimeKind.Utc), r.Last);
    }

    [Fact]
    public void No_meetings_is_empty() => Assert.Equal(0, Rivalry.Of([new MatchBuilder("a", "c").Build()], "a", "b").Matches);
}
