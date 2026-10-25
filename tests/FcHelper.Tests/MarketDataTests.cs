using FcHelper.Market;
using Microsoft.Extensions.Time.Testing;

namespace FcHelper.Tests;

public class ListRowParserTests
{
    // Trimmed from a real PlayerList row (2026-09-25): selectors and pop-ups removed, fields kept as the site sends them.
    private const string Row = """
        <div id="area_playerunit_100190043"> <div class="tr"><div class="td default"><div class="player_info">
        <div class="info_top"> <div class="season"><img src="https://x/season/ICONTM.png" alt=""></div> <div class="name">펠레</div></div>
        <div class="info_middle"> <span class="position fw"><span class="txt">CF</span><span class="skillData_100190043">127</span> </span>
        <span class="position fw"><span class="txt">ST</span><span class="skillData_100190043">126</span> </span>
        <div class="foot">L5 - <strong>R5</strong></div> </div></div></div>
        <div class="td td_ar"> <span class="pay">34</span> </div>
        <div class="td td_ar"> <span> <span class="skillData_100190043" data-type="sprintspeed"> 129 </span> </span> </div>
        <div class="td td_ar"> <span> <span class="skillData_100190043" data-type="acceleration"> 130 </span> </span> </div>
        <div class="td td_ar_bp bp_100190043"> <span class="span_bp0" style="display:none">-</span>
        <span class="span_bp1" style="display:none" alt="330,000,000" title="330,000,000">3억 3,000만</span>
        <span class="span_bp8" style="display:none" alt="6,590,000,000" title="6,590,000,000">65억 9,000만</span> </div>
        <div class="td td_ar_score"><span>7.1 <em>(402)</em></span></div> </div> </div>
        <div id="area_playerunit_100190042"> <div class="name">마라도나</div> <div class="foot"><strong>L5</strong> - R3</div> </div>
        """;

    [Fact]
    public void Parses_a_row_as_the_site_sends_it()
    {
        var rows = ListRowParser.Parse(Row);

        var pele = rows[0];
        Assert.Equal((100190043L, "펠레", "ICONTM", 34, 127, 5), (pele.SpId, pele.Name, pele.Season, pele.Pay, pele.Ovr1, pele.WeakFoot));
        Assert.Equal(6_590_000_000L, pele.Prices[8]);
        Assert.Equal(130, pele.Stats["acceleration"]);
        Assert.Equal((7.1, 402), (pele.Rating, pele.RatingCount));
        Assert.Equal(3, rows[1].WeakFoot); // the weaker of the two feet
    }

    [Theory]
    [InlineData("1억", 100_000_000L)]
    [InlineData("2억 5,000만", 250_000_000L)]
    [InlineData("1.5조", 1_500_000_000_000L)]
    [InlineData("3000만", 30_000_000L)]
    [InlineData("12345BP", 12_345L)]
    [InlineData("10", 1_000_000_000L)]
    [InlineData("0.5", 50_000_000L)]
    [InlineData("100", 10_000_000_000L)]
    public void Reads_bp_the_way_the_game_writes_it(string text, long expected)
    {
        Assert.True(Bp.TryParse(text, out var v));
        Assert.Equal(expected, v);
    }

    [Fact]
    public void Formats_and_rejects_bp()
    {
        Assert.Equal("7.5억", Bp.Format(750_000_000));
        Assert.Equal("0.3억", Bp.Format(30_000_000));
        Assert.Equal("0.42억", Bp.Format(42_200_000));
        Assert.Equal("1,234억", Bp.Format(123_400_000_000));
        Assert.Equal("0.01억 미만", Bp.Format(1_760));
        Assert.False(Bp.TryParse("억만", out _));
        Assert.False(Bp.TryParse("", out _));
    }
}

public class PriceModelTests
{
    /// <summary>Cards priced by a known rule: +40% per OVR, ×1.5 for two good feet, ×2 for a trait, noise from a fixed seed.</summary>
    private static List<MarketCard> Market(int count = 400)
    {
        var rnd = new Random(7);
        return Enumerable.Range(0, count).Select(i =>
        {
            var ovr = 110 + i % 20;
            var wf = 3 + i % 3;
            var trait = i % 5 == 0;
            var price = 1e8 * Math.Pow(1.4, ovr - 110) * (wf == 5 ? 1.5 : 1) * (trait ? 2 : 1) * Math.Exp(rnd.NextDouble() * 0.1 - 0.05);
            return new MarketCard
            {
                Group = "W", SpId = 200_000_000 + i, Name = $"p{i}", Season = i % 2 == 0 ? "AAA" : "BBB", Pay = 25, Ovr1 = ovr, WeakFoot = wf,
                RatingCount = 50, Prices = new Dictionary<int, long> { [1] = 1000, [8] = (long)price },
                Stats = new Dictionary<string, int> { ["sprintspeed"] = ovr + i % 7 - 3 },
                Tags = trait ? new HashSet<string> { "trait:트릭스터" } : [],
            };
        }).ToList();
    }

    [Fact]
    public void Recovers_the_price_rules_it_was_given()
    {
        // A large market: the ridge on the factors other than OVR, salary and season barely moves them (about 10%).
        var model = PriceModel.Fit("W", 8, Market(3000))!;
        var effects = model.Effects().ToDictionary(e => e.Name);

        Assert.True(model.R2 > 0.95);
        Assert.InRange(effects["OVR +1"].Percent, 38, 42);
        Assert.InRange(effects["양발 (약발 5)"].Percent, 38, 55);
        Assert.InRange(effects["트릭스터"].Percent, 75, 110);
        Assert.InRange(effects["속력 +1 (같은 OVR)"].Percent, -2, 2); // speed carries no premium in this market
    }

    [Fact]
    public void Ovr_salary_and_season_are_not_shrunk_while_the_other_factors_are()
    {
        // MV-08: on a small market the ridge presses weak foot and traits toward 0, but never the OVR (or salary, season).
        var model = PriceModel.Fit("W", 8, Market(400))!;
        var effects = model.Effects().ToDictionary(e => e.Name);

        Assert.InRange(effects["OVR +1"].Percent, 38, 42);
        Assert.InRange(effects["양발 (약발 5)"].Percent, 10, 45); // true +50%, shrunk
        Assert.InRange(effects["트릭스터"].Percent, 20, 90);       // true +100%, shrunk
    }

    [Fact]
    public void Name_priced_cards_are_left_out_of_the_fit_and_traits_are_valued()
    {
        var cards = Market(3000);
        // 호날두-style cards: ordinary spec, 20x the price. They must not teach the model anything.
        cards.AddRange(Enumerable.Range(0, 30).Select(i => cards[i] with { SpId = 900_000_000 + i, Name = "크리스티아누 호날두",
            Prices = new Dictionary<int, long> { [1] = 1000, [8] = cards[i].PriceAt(8) * 20 } }));
        var model = PriceModel.Fit("W", 8, cards)!;

        Assert.Equal(3000, model.Cards);
        var trickster = model.Factors().Single(f => f.Key == "trait:트릭스터");
        Assert.True(trickster.IsCore && trickster.Clear && trickster.Kind == FactorKind.Trait);
        Assert.InRange(trickster.Percent, 75, 110);
        Assert.InRange(trickster.OvrEquivalent, 1.6, 2.3); // ×2 ≈ two OVR at +40% per OVR
        Assert.True(trickster.BpAtMedian > 0);
    }

    [Fact]
    public void Inconsistent_factors_are_listed_but_not_priced()
    {
        // MV-07: body type, skill moves and most traits do not set prices consistently; MV-08 leaves them out.
        var cards = Market(400).Select((c, i) => c with
        {
            Tags = new HashSet<string>(c.Tags) { i % 3 == 0 ? "body:thin" : "body:heavy", i % 4 == 0 ? "skill:5" : "skill:4", i % 6 == 0 ? "trait:크로스 포쳐" : "trait:-" },
            Prices = new Dictionary<int, long> { [1] = 1000, [8] = (long)(c.PriceAt(8) * (i % 6 == 0 ? 3 : 1)) },
        }).ToList();
        var model = PriceModel.Fit("W", 8, cards)!;
        var factors = model.Factors().ToDictionary(f => f.Key);

        foreach (var key in new[] { "body:thin", "skill:5", "trait:크로스 포쳐" })
        {
            Assert.False(factors[key].Estimated);
            Assert.StartsWith("가격 모델에서 뺌", factors[key].Excluded);
        }
        Assert.True(factors["trait:트릭스터"].Estimated); // a consistent trait stays
        var crossPoacher = cards.First(c => c.Tags.Contains("trait:크로스 포쳐"));
        Assert.Equal(model.Predict(crossPoacher with { Tags = new HashSet<string>() } ), model.Predict(crossPoacher with { Tags = new HashSet<string> { "trait:크로스 포쳐" } }), 6);
    }

    [Fact]
    public void Prices_height_and_weight_where_kept_and_affiliation_colours_but_not_feature_colours()
    {
        // 센터백 keeps height and weight (user's call, MV-08); 특성 팀컬러 is left out (MV-07: never consistent).
        var cards = Market(2000).Select((c, i) =>
        {
            var height = new[] { 168, 172, 177, 182 }[i % 4];
            var weight = new[] { 62, 67, 72, 87 }[(i / 4) % 4];
            var multiplier = (height == 182 ? 1.3 : 1) * (weight == 87 ? 1.2 : 1)
                * (i % 7 == 0 ? 1.4 : 1) * (i % 11 == 0 ? 1.25 : 1);
            return c with
            {
                Group = "CB",
                Stats = new Dictionary<string, int>(c.Stats) { ["height"] = height, ["weight"] = weight },
                Prices = new Dictionary<int, long> { [1] = 1000, [8] = (long)(c.PriceAt(8) * multiplier) },
            };
        }).ToList();
        var affiliation = cards.Where((_, i) => i % 7 == 0).Select(c => c.SpId).ToHashSet();
        var feature = cards.Where((_, i) => i % 11 == 0).Select(c => c.SpId).ToHashSet();
        IReadOnlyList<MarketMembership> colours =
        [
            new("tc:aff:1", "소속 팀컬러: A", FactorKind.TeamColor, affiliation),
            new("tc:feature:2", "특성 팀컬러: B", FactorKind.FeatureTeamColor, feature),
        ];
        var model = PriceModel.Fit("CB", 8, cards, colours)!;
        var factors = model.Factors().ToDictionary(f => f.Key);

        Assert.Equal(FactorKind.Height, factors["height:180-184"].Kind);
        Assert.Equal(FactorKind.Weight, factors["weight:85+"].Kind);
        // True +30% and +20%. Height and weight are shrunk factors, and buckets that split the cards between them shrink
        // more (their shared part goes into the unshrunk constant), as in MV-07: the direction and order must hold.
        Assert.InRange(factors["height:180-184"].Percent, 8, 40);
        Assert.InRange(factors["weight:85+"].Percent, 4, 30);
        Assert.Equal(FactorKind.TeamColor, factors["tc:aff:1"].Kind);
        Assert.True(factors["tc:aff:1"].Estimated);
        Assert.False(factors["tc:feature:2"].Estimated);
        Assert.NotNull(factors["tc:feature:2"].Excluded);
        Assert.InRange(model.ValidationError, 0, 0.12);

        // A 윙어's height and weight are not priced at all.
        var winger = PriceModel.Fit("W", 8, cards.Select(c => c with { Group = "W" }), colours)!.Factors();
        Assert.DoesNotContain(winger, f => f.Kind is FactorKind.Height or FactorKind.Weight && f.Estimated);
        Assert.Contains(winger, f => f.Key == "height:excluded" && f.Excluded is not null);
    }

    [Fact]
    public void Card_filter_applies_every_condition()
    {
        var card = new MarketCard
        {
            Group = "CB", SpId = 1, Name = "김민재", Season = "S", Pay = 25, Ovr1 = 120, WeakFoot = 4, RatingCount = 50,
            Prices = new Dictionary<int, long> { [1] = 1000, [8] = 50_000_000 }, Positions = new Dictionary<string, int> { ["CB"] = 120 },
            Stats = new Dictionary<string, int> { ["sprintspeed"] = 125, ["height"] = 190, ["marking"] = 124, ["standingtackle"] = 123, ["balance"] = 118 },
            Tags = new HashSet<string> { "trait:파이터" },
        };
        Assert.True(new CardFilter { MinOvr = 135 }.Matches(card, 8)); // 120 − 3 + 18 = 135
        Assert.False(new CardFilter { MinOvr = 136 }.Matches(card, 8));
        Assert.False(new CardFilter { MaxOvr = 130 }.Matches(card, 8));
        Assert.True(new CardFilter { Body = "normal", Traits = ["파이터"], MinHeight = 183, MaxHeight = 192 }.Matches(card, 8));
        Assert.False(new CardFilter { Body = "thin" }.Matches(card, 8));
        Assert.False(new CardFilter { MinHeight = 193 }.Matches(card, 8));
        Assert.True(new CardFilter { MinStats = new Dictionary<string, int> { ["sprintspeed"] = 125 } }.Matches(card, 8));
        Assert.False(new CardFilter { MinStats = new Dictionary<string, int> { ["agility"] = 100 } }.Matches(card, 8)); // not collected
        Assert.False(new CardFilter { Members = new HashSet<long> { 2 } }.Matches(card, 8));
        Assert.False(new CardFilter { MaxPay = 24 }.Matches(card, 8));
        Assert.True(MarketGroups.Get("CB").CoreGap(card) > 0);
    }

    [Fact]
    public void Value_finder_keeps_the_price_range_and_filters()
    {
        var cards = Market();
        cards.Add(cards[0] with { SpId = 1, Name = "bargain", Prices = new Dictionary<int, long> { [1] = 1000, [8] = cards[0].PriceAt(8) / 3 } });
        var model = PriceModel.Fit("W", 8, cards)!;

        var picks = ValueFinder.Find(model, cards, new ValueQuery { Group = "W", Grade = 8, Filter = new CardFilter { MinPrice = 20_000_000, MaxPrice = 200_000_000 } });

        Assert.Equal("bargain", picks[0].Card.Name);
        Assert.InRange(picks[0].Discount, -0.72, -0.55); // a third of the price; the shrunk trait premium moves the expectation a little
        Assert.All(picks, p => Assert.InRange(p.Price, 20_000_000, 200_000_000));
        Assert.All(ValueFinder.Find(model, cards, new ValueQuery { Group = "W", Filter = new CardFilter { MinWeakFoot = 5, Traits = ["트릭스터"] } }),
            p => Assert.True(p.Card.WeakFoot == 5 && p.Card.Tags.Contains("trait:트릭스터")));
    }
}

/// <summary>MV-08: enhancement price floors (docs/market-value/DETERMINANTS.md) in the price model and the value finder.</summary>
public class PriceFloorTests
{
    private static MarketCard Card(params (int Grade, long Price)[] prices) => new()
    {
        Group = "W", SpId = 1, Name = "c", Season = "S", Ovr1 = 120, Prices = prices.ToDictionary(p => p.Grade, p => p.Price),
    };

    [Fact]
    public void Floors_are_the_multiples_of_the_grade_below_with_rounding_room()
    {
        Assert.True(PriceModel.AtFloor(Card((5, 1_500_000), (8, 14_250_000)), 8));   // exactly 9.5×
        Assert.True(PriceModel.AtFloor(Card((5, 1_500_000), (8, 14_300_000)), 8));   // rounded to three digits (+0.35%)
        Assert.False(PriceModel.AtFloor(Card((5, 1_500_000), (8, 14_600_000)), 8));  // 2.5% above: a real price
        Assert.False(PriceModel.AtFloor(Card((8, 14_250_000)), 8));                  // no +5 price to compare with
        Assert.True(PriceModel.AtFloor(Card((3, 1_000_000), (5, 1_500_000)), 5));
        Assert.True(PriceModel.AtFloor(Card((8, 10_000_000), (10, 21_000_000)), 10));
        Assert.True(PriceModel.AtFloor(Card((10, 20_000_000), (11, 31_000_000)), 11));
        Assert.False(PriceModel.AtFloor(Card((1, 1_000_000), (2, 1_000_000)), 2));   // no floor rule at +2
        Assert.Equal(14_250_000, PriceModel.FloorAt(Card((5, 1_500_000)), 8));
        Assert.Null(PriceModel.FloorAt(Card((5, 1_500_000)), 1));
    }

    private static double Normal(Random r) => Math.Sqrt(-2 * Math.Log(1 - r.NextDouble())) * Math.Cos(2 * Math.PI * r.NextDouble());

    [Fact]
    public void Tobit_recovers_a_known_effect_that_floor_prices_hide()
    {
        // ln value = 18 + ln1.4 per OVR + 0.40 for a trait + noise 0.4. The floor (the +5 price × 9.5) follows the
        // card's spec but not its own noise, so about 28% of the cards show the floor instead of their value.
        var r = new Random(20261025);
        var x = new List<double[]>();
        var y = new List<double>();
        var cens = new List<bool>();
        for (var i = 0; i < 3000; i++)
        {
            var d = r.Next(-10, 11);
            var trait = r.NextDouble() < 0.3 ? 1.0 : 0.0;
            var value = 18 + Math.Log(1.4) * d + 0.40 * trait + Normal(r) * 0.4;
            var floor = 18 + Math.Log(1.4) * d - 0.25 + Normal(r) * 0.2;
            x.Add([1, d, trait]);
            y.Add(Math.Max(value, floor));
            cens.Add(floor >= value);
        }
        var clusters = Enumerable.Range(0, 3000).ToList();
        bool[] notShrunk = [true, true, true]; // compare the estimators, not the ridge
        var tobit = PriceModel.RidgeTobit(x, y, clusters, notShrunk, cens);
        var asValue = PriceModel.RidgeTobit(x, y, clusters, notShrunk);

        Assert.InRange(cens.Count(c => c) / 3000.0, 0.2, 0.36);
        Assert.InRange(tobit.Beta[2], 0.36, 0.44);         // the true +0.40 comes back
        Assert.True(asValue.Beta[2] < tobit.Beta[2] - 0.05); // taking the floor as the value understates it
        Assert.InRange(tobit.Beta[1], Math.Log(1.4) - 0.01, Math.Log(1.4) + 0.01);
        Assert.InRange(tobit.Sigma, 0.36, 0.44);
    }

    [Fact]
    public void Mills_ratio_matches_known_values()
    {
        Assert.Equal(0.7978846, PriceModel.Mills(0), 6);   // φ(0)/Φ(0) = 2/√(2π)
        Assert.Equal(0.2875999, PriceModel.Mills(1), 5);
        Assert.Equal(8.1213681, PriceModel.Mills(-8), 5);  // stays accurate far in the tail (≈ −a + 1/|a|)
    }

    [Fact]
    public void Floor_prices_are_never_value_picks_and_expectations_never_go_below_the_floor()
    {
        var rnd = new Random(3);
        var cards = Enumerable.Range(0, 400).Select(i =>
        {
            var ovr = 110 + i % 20;
            var p8 = 1e8 * Math.Pow(1.4, ovr - 110) * Math.Exp(rnd.NextDouble() * 0.1 - 0.05);
            return new MarketCard
            {
                Group = "W", SpId = 300_000_000 + i, Name = $"p{i}", Season = "S", Pay = 25, Ovr1 = ovr, WeakFoot = 3, RatingCount = 50,
                Prices = new Dictionary<int, long> { [1] = 1000, [5] = (long)(p8 / 20), [8] = (long)p8 },
            };
        }).ToList();
        // Two cards that look like a third of their spec's price: one is a real bargain, the other sits on its floor
        // (its +5 is cheap too, and +8 is exactly 9.5 times +5).
        var basis = cards[0];
        // The market never lets +8 drop under 9.5×+5, so the bargain's +5 is cheap too (+8 = 12×+5, above the floor).
        var bargain = basis with { SpId = 1, Name = "bargain", Prices = new Dictionary<int, long> { [1] = 1000, [5] = basis.PriceAt(8) / 3 / 12, [8] = basis.PriceAt(8) / 3 } };
        var floor5 = basis.PriceAt(8) / 3 / 9.5;
        var onFloor = basis with { SpId = 2, Name = "floor", Prices = new Dictionary<int, long> { [1] = 1000, [5] = (long)floor5, [8] = (long)Math.Round(floor5 * 9.5) } };
        cards.AddRange([bargain, onFloor]);
        var model = PriceModel.Fit("W", 8, cards)!;

        Assert.True(PriceModel.AtFloor(onFloor, 8));
        Assert.False(PriceModel.AtFloor(bargain, 8));
        var picks = ValueFinder.Find(model, cards, new ValueQuery { Group = "W", Grade = 8, Filter = new CardFilter { MinRatings = 0 } });
        Assert.Contains(picks, p => p.Card.Name == "bargain");
        Assert.DoesNotContain(picks, p => p.Card.Name == "floor");
        Assert.Equal("bargain", picks[0].Card.Name);
        Assert.All(cards, c => Assert.True(model.Expected(c) >= (PriceModel.FloorAt(c, 8) ?? 0)));

        // A card the model prices below its floor shows the floor as its expected price.
        var lowSpec = cards.MinBy(c => model.Predict(c))!;
        var highFloor = lowSpec with { Prices = new Dictionary<int, long>(lowSpec.Prices) { [5] = (long)(model.Predict(lowSpec) / 5) } };
        Assert.Equal(PriceModel.FloorAt(highFloor, 8), model.Expected(highFloor));
    }
}

public class MarketRefreshTests : IDisposable
{
    private readonly TempDb _t = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 25, 0, 0, 0, TimeSpan.Zero));
    private readonly FakeList _source = new();
    private string _seasons = """[{"seasonId":100,"className":"ICON"},{"seasonId":200,"className":"LIVE"}]""";

    public void Dispose() => _t.Dispose();

    /// <summary>Two cards per position group and OVR; season 300 exists only once "released".</summary>
    private sealed class FakeList : IMarketListSource
    {
        public int Calls;
        public int FailAt = -1;
        public bool SeasonReleased;
        public List<ListQuery> Queries { get; } = [];

        public Task<IReadOnlyList<ListRow>> QueryAsync(ListQuery q, CancellationToken ct = default)
        {
            if (Calls++ == FailAt) throw new HttpRequestException("down");
            Queries.Add(q);
            var rows = new List<ListRow>();
            var seasons = SeasonReleased ? new[] { 100, 200, 300 } : [100, 200];
            for (var ovr = q.OvrMin; ovr <= q.OvrMax; ovr++)
            {
                foreach (var season in seasons)
                {
                    var spid = season * 1_000_000L + q.Positions.Length * 1000 + ovr;
                    if (q.Seasons.Length > 0 && !q.Seasons.Contains($",{season},")) continue;
                    if (q.Trait.Length > 0 && spid % 3 != 0) continue;
                    if (q.SkillMove > 0 || q.Body.Length > 0) continue;
                    rows.Add(new ListRow(spid, $"p{spid}", $"S{season}", 25, ovr, 4, 7, 30,
                        new Dictionary<int, long> { [5] = 1_000_000L * ovr, [8] = 5_000_000L * ovr },
                        q.Stats.ToDictionary(s => s, _ => ovr)));
                }
            }
            return Task.FromResult<IReadOnlyList<ListRow>>(rows);
        }
    }

    // Same file as the app's cache, as in the app.
    private MarketStore Store() => new(_t.Path);
    private MarketService Service(MarketStore store) => new(_source, store, _ => Task.FromResult(_seasons), _time);

    [Fact]
    public async Task First_run_collects_everything_then_waits_a_day()
    {
        var store = Store();
        var svc = Service(store);

        Assert.True(await svc.RefreshIfDueAsync());
        var cards = store.LoadCards(store.LatestFinished()!.Id, "ST");
        Assert.Contains(cards, c => c.Stats.ContainsKey("sprintspeed") && c.Stats.ContainsKey("composure")); // both stat passes
        Assert.Contains(cards, c => MarketGroups.Get("ST").AllStats.All(c.Stats.ContainsKey));
        Assert.Contains(cards, c => c.Tags.Contains("trait:라인 브레이커"));

        var calls = _source.Calls;
        _time.Advance(TimeSpan.FromHours(10));
        Assert.False(await svc.RefreshIfDueAsync());
        Assert.Equal(calls, _source.Calls);
    }

    [Fact]
    public async Task Daily_refresh_rereads_prices_and_carries_the_rest_over()
    {
        var store = Store();
        var svc = Service(store);
        await svc.RefreshIfDueAsync();
        var fullCalls = _source.Calls;

        _time.Advance(TimeSpan.FromHours(21));
        _source.Calls = 0;
        Assert.True(await svc.RefreshIfDueAsync());

        Assert.True(_source.Calls < fullCalls / 1.5);
        Assert.All(_source.Queries.TakeLast(_source.Calls), q => Assert.True(q.Trait.Length == 0));
        var card = store.LoadCards(store.LatestFinished()!.Id, "ST").First(c => c.Tags.Count > 0);
        Assert.True(card.Stats.ContainsKey("composure")); // second pass carried over
    }

    [Fact]
    public async Task A_new_season_is_collected_at_once()
    {
        var store = Store();
        var svc = Service(store);
        await svc.RefreshIfDueAsync();

        _time.Advance(TimeSpan.FromHours(2));
        _source.SeasonReleased = true;
        _seasons = _seasons.Replace("]", """,{"seasonId":300,"className":"NEW"}]""");
        Assert.True(await svc.RefreshIfDueAsync());

        var fresh = store.LoadCards(store.LatestFinished()!.Id, "ST").Where(c => c.SeasonId == 300).ToList();
        Assert.NotEmpty(fresh);
        Assert.All(fresh, c => Assert.True(c.Stats.ContainsKey("composure")));
        Assert.Contains(fresh, c => c.Tags.Contains("trait:라인 브레이커"));
        Assert.False(await svc.RefreshIfDueAsync()); // recorded: not new any more
    }

    [Fact]
    public async Task An_interrupted_refresh_resumes_without_repeating_finished_queries()
    {
        // Reference: how many queries an uninterrupted first run makes.
        using (var other = new TempDb())
        {
            var clean = new FakeList();
            await new MarketService(clean, new MarketStore(other.Path), _ => Task.FromResult(_seasons), _time).RefreshIfDueAsync();
            _source.FailAt = clean.Queries.Count / 2;
            var store = Store();
            var svc = Service(store);

            Assert.False(await svc.RefreshIfDueAsync());
            Assert.Null(store.LatestFinished()); // nothing half-finished is ever searched
            Assert.NotNull(svc.Status.LastError);

            Assert.True(await svc.RefreshIfDueAsync());
            Assert.NotNull(store.LatestFinished());
            Assert.Equal(clean.Queries.Count, _source.Queries.Count);
        }
    }

    [Fact]
    public async Task New_stats_to_collect_trigger_one_full_refresh()
    {
        var store = Store();
        var svc = Service(store);
        await svc.RefreshIfDueAsync();
        var fullCalls = _source.Calls;

        // As after an update that collects more stats: the data is fresh, but the schema differs.
        store.SetValue("market.schema", "old", _time.GetUtcNow().UtcDateTime);
        _time.Advance(TimeSpan.FromHours(1));
        Assert.Null(svc.NextDue);
        _source.Calls = 0;
        Assert.True(await svc.RefreshIfDueAsync());
        Assert.Equal(fullCalls, _source.Calls);
        Assert.Contains(store.LoadCards(store.LatestFinished()!.Id, "CB"), c => c.Stats.ContainsKey("height") && c.Stats.ContainsKey("balance"));
        Assert.False(await svc.RefreshIfDueAsync());
    }

    [Fact]
    public async Task Only_the_newest_snapshots_are_kept()
    {
        var store = Store();
        var svc = Service(store);
        for (var i = 0; i < 5; i++)
        {
            await svc.RefreshIfDueAsync(force: true);
            _time.Advance(TimeSpan.FromHours(21));
        }
        var ids = Enumerable.Range(1, 5).Count(id => store.CountCards(id) > 0);
        Assert.Equal(MarketStore.KeepSnapshots, ids);
    }
}
