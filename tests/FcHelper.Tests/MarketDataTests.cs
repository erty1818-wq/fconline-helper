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
    [InlineData("12345", 12_345L)]
    public void Reads_bp_the_way_the_game_writes_it(string text, long expected)
    {
        Assert.True(Bp.TryParse(text, out var v));
        Assert.Equal(expected, v);
    }

    [Fact]
    public void Formats_and_rejects_bp()
    {
        Assert.Equal("7.5억", Bp.Format(750_000_000));
        Assert.Equal("3,000만", Bp.Format(30_000_000));
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
        var model = PriceModel.Fit("W", 8, Market())!;
        var effects = model.Effects().ToDictionary(e => e.Name);

        Assert.True(model.R2 > 0.95);
        Assert.InRange(effects["양발 (약발 5)"].Percent, 45, 55);
        Assert.InRange(effects["트릭스터"].Percent, 90, 110);
        Assert.InRange(effects["속력 +1 (같은 OVR)"].Percent, -2, 2); // speed carries no premium in this market
    }

    [Fact]
    public void Value_finder_keeps_the_price_range_and_filters()
    {
        var cards = Market();
        cards.Add(cards[0] with { SpId = 1, Name = "bargain", Prices = new Dictionary<int, long> { [1] = 1000, [8] = cards[0].PriceAt(8) / 3 } });
        var model = PriceModel.Fit("W", 8, cards)!;

        var picks = ValueFinder.Find(model, cards, new ValueQuery { Group = "W", Grade = 8, MinPrice = 20_000_000, MaxPrice = 200_000_000 });

        Assert.Equal("bargain", picks[0].Card.Name);
        Assert.InRange(picks[0].Discount, -0.72, -0.62);
        Assert.All(picks, p => Assert.InRange(p.Price, 20_000_000, 200_000_000));
        Assert.All(ValueFinder.Find(model, cards, new ValueQuery { Group = "W", MinWeakFoot = 5, Trait = "트릭스터" }),
            p => Assert.True(p.Card.WeakFoot == 5 && p.Card.Tags.Contains("trait:트릭스터")));
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
