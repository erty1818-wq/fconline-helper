using FcHelper.Market;
using FcHelper.NexonApi;

namespace FcHelper.Tests;

/// <summary>MV-10: playable +11 defaults, censoring and reproducible audits.</summary>
public class MarketGradeTests
{
    private static List<MarketCard> Cards()
    {
        var cards = new List<MarketCard>();
        for (var p = 0; p < 120; p++)
        for (var s = 0; s < 3; s++)
        {
            var ovr = 110 + (p * 7 + s * 3) % 17;
            var trait = (p + s) % 3 == 0;
            var latent = (long)Math.Exp(18 + .3 * (ovr - 115) + s * .2 + (p % 2) * .5
                + (trait ? Math.Log(1.8) + .1 * Math.Max(0, ovr - 115) : 0));
            var lower = (long)(latent * (p % 4 == 0 ? .8 : .25));
            cards.Add(new MarketCard
            {
                Group = "W", SpId = (300L + s) * 1_000_000 + p, Name = $"p{p}", Season = $"S{s}",
                Pay = 25, Ovr1 = ovr, WeakFoot = 3, RatingCount = 50,
                Prices = new Dictionary<int, long> { [1] = 1000, [8] = lower / 4, [10] = lower, [11] = Math.Max(latent, (long)Math.Round(lower * 1.55)) },
                Tags = trait ? new HashSet<string> { "trait:라인 브레이커" } : [],
                Positions = new Dictionary<string, int> { ["RW"] = ovr },
            });
        }
        return cards;
    }

    [Theory]
    [InlineData(31_000_000, true)]
    [InlineData(31_200_000, true)]
    [InlineData(31_300_000, false)]
    public void Grade11_floor_uses_grade10_and_rounding_tolerance(long price, bool floor)
    {
        var c = Cards()[0] with { Prices = new Dictionary<int, long> { [10] = 20_000_000, [11] = price } };
        Assert.Equal(31_000_000, PriceModel.FloorAt(c, 11));
        Assert.Equal(floor, PriceModel.AtFloor(c, 11));
        Assert.False(PriceModel.AtFloor(c with { Prices = new Dictionary<int, long> { [11] = price } }, 11));
    }

    [Fact]
    public void Defaults_keep_the_same_playable_cards_at_grade10_and_grade11()
    {
        Assert.Equal(11, new ValueQuery { Group = "W" }.Grade);
        Assert.Equal([11], new SquadRequest { Formation = Formations.All[0] }.Grades);
        Assert.Equal(11, SquadService.ModelGrade);
        Assert.Equal(140, MarketDefaults.MinOvr());
        Assert.Equal(138, MarketDefaults.MinOvr(10));
        Assert.Equal(4, MarketService.PlayableMargin);
        Assert.Equal(115, PriceModel.TraitHinge);
        Assert.Equal(122, PriceModel.TraitTopOvr);
        Assert.Equal(3.0, PriceModel.TraitCritical);
        foreach (var c in Cards())
            Assert.Equal(c.OvrAt(11) >= 140, c.OvrAt(10) >= 138);
    }

    [Fact]
    public void Default_value_query_uses_grade11_prices_and_excludes_its_floor()
    {
        var cards = Cards();
        var model = PriceModel.Fit("W", 11, cards)!;
        var picks = ValueFinder.Find(model, cards, new ValueQuery { Group = "W" });
        Assert.NotEmpty(picks);
        Assert.Contains(cards, c => PriceModel.AtFloor(c, 11));
        Assert.All(picks, p =>
        {
            Assert.Equal(11, p.Grade);
            Assert.Equal(p.Card.PriceAt(11), p.Price);
            Assert.False(PriceModel.AtFloor(p.Card, 11));
            Assert.True(p.Expected >= PriceModel.FloorAt(p.Card, 11));
        });
    }

    [Fact]
    public void Five_card_folds_cover_every_card_and_never_train_on_its_own_price()
    {
        var cards = Cards();
        var before = PriceModel.CrossValidate("W", 11, cards);
        Assert.Equal(cards.Count, before.Count);
        Assert.Equal(cards.Count, before.Select(r => r.Card.SpId).Distinct().Count());
        Assert.Equal([0, 1, 2, 3, 4], before.Select(r => r.Fold).Distinct().Order());
        var target = cards.First(c => !PriceModel.AtFloor(c, 11));
        var changed = cards.Select(c => c.SpId == target.SpId ? c with
        {
            Prices = new Dictionary<int, long>(c.Prices) { [11] = c.PriceAt(11) * 2 },
        } : c).ToList();
        var after = PriceModel.CrossValidate("W", 11, changed);
        Assert.Equal(before.Single(r => r.Card.SpId == target.SpId).Expected, after.Single(r => r.Card.SpId == target.SpId).Expected);
        Assert.All(before, r => Assert.True(r.Expected >= PriceModel.FloorAt(r.Card, 11)));
    }

    [Fact]
    public void Value_factors_and_trait_average_use_the_same_grade11_playable_model()
    {
        using var temp = new TempDb();
        var store = new MarketStore(temp.Path);
        var now = DateTime.UtcNow;
        var snapshot = store.StartSnapshot("Full", now);
        var cards = Cards();
        store.SaveRows(snapshot, "W", cards.Select(c => new ListRow(c.SpId, c.Name, c.Season, c.Pay, c.Ovr1, c.WeakFoot,
            c.Rating, c.RatingCount, new Dictionary<int, long>(c.Prices), new Dictionary<string, int>(c.Stats))));
        store.SaveTag(snapshot, "W", cards.Where(c => c.Tags.Count > 0).Select(c => c.SpId).ToList(), "trait:라인 브레이커");
        store.FinishSnapshot(snapshot, now);
        store.RecordPriceHistory(snapshot, now);
        var service = new MarketService(new NoRequests(), store, _ => Task.FromResult("[]"));
        var query = new ValueQuery { Group = "W", Filter = new() { MinOvr = 140 } };
        var model = service.ModelFor(query)!;
        Assert.Equal(11, model.Grade);
        Assert.Equal(cards.Count(c => c.Ovr1 >= 115), model.Cards);
        Assert.Same(model, service.ModelAbove("W", 11, 136));
        var average = Assert.Single(service.TraitValues(minOvr: 140));
        Assert.Equal(model.Factors().Single(f => f.Kind == FactorKind.Trait).Percent, Assert.Single(average.ByGroup).Factor.Percent);
        Assert.Equal(cards[0].PriceAt(11), store.PriceHistory(DateOnly.FromDateTime(now))[("W", cards[0].SpId)][11]);
    }

    [Fact]
    public void Squad_builder_buys_grade11_without_an_explicit_grade_list()
    {
        var formation = Formations.All[0];
        var cards = formation.Slots.Distinct().SelectMany((pos, pi) => Enumerable.Range(0, 8).Select(i => new MarketCard
        {
            Group = Formations.GroupOf(pos), SpId = 300_000_000 + pi * 100 + i, Name = $"p{pi}-{i}", Season = "S",
            Pay = 25, Ovr1 = 119 + i % 3, RatingCount = 50, Positions = new Dictionary<string, int> { [pos] = 119 + i % 3 },
            Prices = new Dictionary<int, long> { [1] = 1000, [11] = 10_000_000 + i * 1000 },
        })).ToList();
        var plan = new SquadBuilder(cards, _ => null).Build(new SquadRequest { Formation = formation, Budget = 1_000_000_000 })[0];
        Assert.Equal(11, plan.Slots.Count);
        Assert.All(plan.Slots, s => { Assert.Equal(11, s.Grade); Assert.True(s.Ovr >= 140); });
    }

    [Fact]
    public async Task Hidden_ranker_picks_default_to_actual_grade11_usage_and_allow_grade10()
    {
        using var temp = new TempDb();
        using var http = new HttpClient();
        var store = new MarketStore(temp.Path);
        var now = DateTime.UtcNow;
        var snapshot = store.StartSnapshot("Full", now);
        var cards = Cards();
        store.SaveRows(snapshot, "W", cards.Select(c => new ListRow(c.SpId, c.Name, c.Season, c.Pay, c.Ovr1, c.WeakFoot,
            c.Rating, c.RatingCount, new Dictionary<int, long>(c.Prices), new Dictionary<string, int>(c.Stats))));
        store.SaveTag(snapshot, "W", cards.Where(c => c.Tags.Count > 0).Select(c => c.SpId).ToList(), "trait:라인 브레이커");
        store.FinishSnapshot(snapshot, now);
        var picks = cards.SelectMany(c => new[] { 10, 11 }.Select(g => new RankerPick("RW", c.SpId, c.Name, g, c.OvrAt(g), c.Pay, 30, .03))).ToList();
        store.SaveChart(new RankerChartData(now.ToString("yyyy-MM-dd"), 1, 10000, picks, [], [],
            [new TeamColorUsage("test", 30, .03) { Id = 1 }]), now);
        var source = new NoRequests();
        var market = new MarketService(source, store, _ => Task.FromResult("[]"));
        var service = new SquadService(market, store, new NoCharts(),
            new TeamColorCache(store, new DataCenterTeamColorClient(http, new RateLimiter(.5), source)));
        var at11 = await service.HiddenRankerPicksAsync();
        Assert.NotEmpty(at11);
        Assert.All(at11, p => { Assert.Equal(11, p.Grade); Assert.True(p.Ovr >= 140); });
        var at10 = await service.HiddenRankerPicksAsync(grade: 10);
        Assert.NotEmpty(at10);
        Assert.All(at10, p => { Assert.Equal(10, p.Grade); Assert.True(p.Ovr >= 138); });
    }

    private sealed class NoCharts : IRankerChartSource
    {
        public Task<RankerChartData> FetchAsync(int rankFrom, int rankTo, CancellationToken ct = default)
            => throw new InvalidOperationException("The cached ranker chart must be used.");
    }

    private sealed class NoRequests : IMarketListSource
    {
        public Task<IReadOnlyList<ListRow>> QueryAsync(ListQuery query, CancellationToken ct = default)
            => throw new InvalidOperationException("No network requests during valuation.");
    }
}
