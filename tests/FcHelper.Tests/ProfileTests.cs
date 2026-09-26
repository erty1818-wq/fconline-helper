using FcHelper.Services;
using FcHelper.Tests.Fixtures;

namespace FcHelper.Tests;

/// <summary>비교 tab: per-match averages for me against the opponent (OS-11).</summary>
public class ProfileTests
{
    [Fact]
    public void Averages_cover_matches_with_stats_and_goals_against_come_from_the_other_side()
    {
        var matches = new[]
        {
            new MatchBuilder("opp", "a").A(s => s.Possession(60).Passes(100, 5)).B(s => s.Goal(9).Goal(9)).Build(),
            new MatchBuilder("opp", "b").A(s => s.Possession(40).Passes(50, 2).Goal(1)).B(s => s.OwnGoals(1)).Build(),
            new MatchBuilder("opp", "c").A(s => s.NoStats()).Build(),
        };

        var p = Profile.Of(matches, "opp");

        Assert.Equal(2, p.Matches);
        var byLabel = p.Metrics.ToDictionary(m => m.Label);
        Assert.Equal(50, byLabel["점유율"].Value);
        Assert.Equal("50%", byLabel["점유율"].Display);
        Assert.False(byLabel["평균 실점"].HigherIsBetter);
        Assert.Equal(1.0, byLabel["평균 실점"].Value);
        Assert.Equal(1.0, byLabel["평균 득점"].Value); // one goal and one own goal over two matches
        Assert.Equal(9, p.Metrics.Count);
    }

    [Fact]
    public void No_matches_gives_an_empty_profile() => Assert.Equal(0, Profile.Of([], "opp").Matches);
}
