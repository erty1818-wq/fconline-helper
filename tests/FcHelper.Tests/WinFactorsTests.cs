using FcHelper.Services;
using FcHelper.Tests.Fixtures;

namespace FcHelper.Tests;

/// <summary>내 전적 tab: which stats go with winning (OS-18).</summary>
public class WinFactorsTests
{
    [Fact]
    public void Too_few_matches_gives_no_factors()
    {
        var matches = Enumerable.Range(0, WinFactors.MinMatches - 1).Select(_ => new MatchBuilder("me", "x").Build());
        var (count, factors) = WinFactors.Of(matches, "me");
        Assert.Equal(WinFactors.MinMatches - 1, count);
        Assert.Empty(factors);
    }

    [Fact]
    public void Halves_split_at_the_median_and_the_bigger_gap_comes_first()
    {
        // 40 matches: high possession (60) wins every time, low possession (40) always loses.
        var matches = Enumerable.Range(0, 40).Select(i => i % 2 == 0
            ? new MatchBuilder("me", "x").A(s => s.Possession(60).Goal(1)).Build()
            : new MatchBuilder("me", "x").A(s => s.Possession(40)).B(s => s.Goal(2)).Build()).ToList();
        matches.Add(new MatchBuilder("me", "x").A(s => s.Forfeit()).Build()); // left out

        var (count, factors) = WinFactors.Of(matches, "me");

        Assert.Equal(40, count);
        var possession = factors.First();
        Assert.Equal("점유율", possession.Label);
        Assert.Equal(50, possession.Median);
        Assert.Equal((20, 1.0, 20, 0.0), (possession.HighMatches, possession.HighWinRate, possession.LowMatches, possession.LowWinRate));
        Assert.Equal(1.0, possession.Gap);
    }
}
