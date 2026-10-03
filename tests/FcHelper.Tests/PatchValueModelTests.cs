using FcHelper.Market;

namespace FcHelper.Tests;

public class PatchValueModelTests
{
    [Fact]
    public void Matched_changes_remove_common_season_and_ovr_trends()
    {
        var before = MarketValueReviewTests.Market(11).Select(c => c with
        {
            Tags = c.PlayerId % 5 < 4 ? new HashSet<string> { $"trait:{PatchValueModel.Traits[c.PlayerId % 5]}" } : [],
        }).ToList();
        var after = before.Select(c => c with
        {
            Prices = new Dictionary<int, long>
            {
                [1] = c.PriceAt(1),
                [8] = (long)(c.PriceAt(8) * Math.Exp(0.4 + (c.Season[1] - '0') * 0.03 + (c.Ovr1 / 5) * 0.005)
                    * (c.Tags.Contains("trait:스피드스터") ? 1.4 : 1)),
            },
        }).ToList();
        var estimates = PatchValueModel.Compare(before, after).ToDictionary(v => v.Trait);
        Assert.InRange(estimates["스피드스터"].Percent, 39.99, 40.01);
        Assert.InRange(estimates["크로스 포쳐"].Percent, -0.01, 0.01);
        Assert.InRange(estimates["아크로바틱 피니셔"].Percent, -0.01, 0.01);
        Assert.InRange(estimates["2개의 심장"].Percent, -0.01, 0.01);
        Assert.True(estimates["스피드스터"].MatchedCards > estimates["스피드스터"].MatchedPlayers);
    }

    [Fact]
    public void Changed_specs_and_new_cards_are_not_treated_as_patch_returns()
    {
        var before = MarketValueReviewTests.Market(11).Select(c => c with { Tags = new HashSet<string> { "trait:스피드스터" } }).ToList();
        var after = before.Select(c => c with { Ovr1 = c.Ovr1 + 1 }).ToList();
        after.Add(before[0] with { SpId = 999999999 });
        Assert.Empty(PatchValueModel.Compare(before, after));
    }
}
