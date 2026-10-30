using FcHelper.Core.Models;
using FcHelper.Market;
using Xunit;

namespace FcHelper.Tests;

public class PriceBlendTests
{
    /// <summary>Enough cards for the tree part: footballers with several cards, one position, a price from OVR, a footballer's name and noise.</summary>
    private static List<MarketCard> Market(int players = 90, int seed = 3)
    {
        var random = new Random(seed);
        var cards = new List<MarketCard>();
        for (var p = 1; p <= players; p++)
        {
            var name = random.NextDouble() - 0.5;
            for (var s = 0; s < 5; s++)
            {
                var ovr = 116 + random.Next(0, 10);
                var price = Math.Exp(16 + 0.45 * (ovr - 116) + name + 0.2 * (random.NextDouble() - 0.5));
                cards.Add(new MarketCard
                {
                    Group = "ST", SpId = (100 + s) * 1_000_000L + p, Name = $"p{p}", Season = $"S{s}", Pay = 20 + random.Next(0, 10), Ovr1 = ovr, WeakFoot = 3, RatingCount = 50,
                    Prices = new Dictionary<int, long> { [1] = 1000, [11] = (long)price },
                    Positions = new Dictionary<string, int> { ["ST"] = ovr },
                    Stats = new Dictionary<string, int> { ["finishing"] = ovr + random.Next(-3, 4) },
                });
            }
        }
        return cards;
    }

    [Fact]
    public void Trees_learn_an_interaction_and_always_give_the_same_answer()
    {
        var random = new Random(1);
        var x = Enumerable.Range(0, 800).Select(_ => new[] { random.NextDouble(), random.NextDouble(), random.NextDouble() }).ToList();
        var y = x.Select(r => r[0] > 0.5 == r[1] > 0.5 ? 1.0 : -1.0).ToList(); // no line through this
        var trees = PriceTrees.Fit(x, y);
        Assert.Equal(trees.Predict(x[0]), PriceTrees.Fit(x, y).Predict(x[0]));
        Assert.True(trees.Predict([0.9, 0.9, 0.5]) > 0.5);
        Assert.True(trees.Predict([0.9, 0.1, 0.5]) < -0.5);
        Assert.True(trees.Predict([0.1, 0.1, 0.5]) > 0.5);
    }

    [Fact]
    public void Trees_send_unknown_values_one_way_without_failing()
    {
        var random = new Random(2);
        var x = Enumerable.Range(0, 200).Select(i => new[] { i % 3 == 0 ? double.NaN : random.NextDouble() }).ToList();
        var y = x.Select(r => double.IsNaN(r[0]) ? 2.0 : 0.0).ToList();
        var trees = PriceTrees.Fit(x, y);
        Assert.True(trees.Predict([double.NaN]) > 1.5);
        Assert.True(trees.Predict([0.5]) < 0.5);
    }

    [Fact]
    public void Usage_counts_starts_not_the_bench()
    {
        var match = new MatchDetail
        {
            MatchInfo =
            [
                new MatchInfo { Player = [new MatchPlayer { SpId = 1, SpPosition = 25, SpGrade = 11 }, new MatchPlayer { SpId = 2, SpPosition = 28, SpGrade = 11 }] },
                new MatchInfo { Player = [new MatchPlayer { SpId = 1, SpPosition = 3, SpGrade = 8 }] },
            ],
        };
        var usage = CardUsage.Count([match]);
        Assert.Equal(new CardUse(2, 1), usage[1]);
        Assert.False(usage.ContainsKey(2));
    }

    [Fact]
    public void Reasons_still_multiply_to_the_price_with_the_trees_in()
    {
        var cards = Market();
        var pool = PriceModel.FitPool(11, cards)!;
        foreach (var c in cards.Take(10))
        {
            var why = pool.Explain(c);
            Assert.Equal(why.Predicted, why.Base * Math.Exp(why.Reasons.Sum(r => r.LogEffect)), why.Predicted * 1e-6);
        }
        Assert.Contains(pool.Explain(cards[0]).Reasons, r => r.Label == "능력치·특성 조합 보정");
    }

    [Fact]
    public void A_cards_own_price_does_not_vouch_for_itself_through_the_trees()
    {
        var cards = Market();
        var before = PriceModel.FitPool(11, cards)!;
        var target = cards[7];
        var changed = cards.Select(c => c.SpId == target.SpId ? c with { Prices = new Dictionary<int, long> { [1] = 1000, [11] = c.PriceAt(11) * 3 } } : c).ToList();
        var after = PriceModel.FitPool(11, changed)!;
        // Tripling the price moves the estimate only through the linear fit's slight in-sample pull.
        Assert.True(after.Predict(changed[7]) / before.Predict(target) < 1.15);
    }

    [Fact]
    public void A_model_knows_where_it_is_less_sure()
    {
        var cards = Market();
        var pool = PriceModel.FitPool(11, cards)!;
        var bands = cards.Select(pool.BandOf).ToList();
        Assert.All(bands, b => Assert.NotNull(b));
        Assert.All(cards.Zip(bands), t => Assert.True(t.Second!.Low <= pool.Expected(t.First) && pool.Expected(t.First) <= t.Second.High));
        Assert.Equal(pool.BandOf(cards[0])!.Error, pool.ErrorOf(cards[0]));
        var used = PriceModel.FitPool(11, cards, usage: cards.Where(c => c.PlayerId % 2 == 0).ToDictionary(c => c.SpId, _ => new CardUse(50, 20)))!;
        Assert.Contains(used.Explain(cards[1]).Reasons, r => r.Label == "공식 경기 사용량");
    }

    [Fact]
    public void Goalkeepers_and_small_markets_show_no_band_and_keep_the_position_error()
    {
        var small = Market(players: 20);
        var pool = PriceModel.FitPool(11, small)!;
        Assert.Null(pool.BandOf(small[0]));
        Assert.Equal(pool.ValidationError, pool.ErrorOf(small[0]));
    }
}
