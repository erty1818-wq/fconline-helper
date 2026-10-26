using FcHelper.Market;
using FcHelper.NexonApi;
using FcHelper.Tests.Fixtures;

namespace FcHelper.Tests;

public class SquadAffiliationTests
{
    private static MarketCard Card(int id, string position, int ovr = 120, long price = 100_000_000) => new()
    {
        SpId = 300_000_000 + id, Name = $"선수{id}", Season = "S", Group = Formations.GroupOf(position),
        Ovr1 = ovr, Pay = 25, WeakFoot = 3,
        Positions = new Dictionary<string, int> { [position] = ovr },
        Prices = new Dictionary<int, long> { [1] = 5000, [11] = price },
        Stats = MarketGroups.StatNames.Keys.ToDictionary(k => k, _ => ovr),
    };

    private static List<MarketCard> Eleven(int firstId, long price = 100_000_000) =>
        new[] { "GK", "LB", "CB", "CB", "RB", "CDM", "CDM", "RW", "LW", "ST", "ST" }
            .Select((p, i) => Card(firstId + i, p, price: price)).ToList();

    private static TeamColorTarget Target(int id, IEnumerable<MarketCard> cards) => new(
        new(id, $"소속{id}", TeamColorCategory.Affiliation, 11, [new(4, 11, 4, [])]), cards.Select(c => c.SpId).ToHashSet());

    private static RankerSquad Sample(int rank, IEnumerable<MarketCard> cards) =>
        new(rank, $"표본{rank}", 0, cards.Select(c => new RankerSquadPlayer(c.SpId, 11, c.MainPosition)).ToList());

    [Fact]
    public void Budget_specific_elevens_outrank_a_more_popular_expensive_affiliation()
    {
        var nearby = Eleven(1);
        var expensive = Eleven(101, 10_000_000_000);
        var request = new SquadRequest { Formation = Formations.Find("4-2-2-2")!, Budget = 1_100_000_000 };
        var choices = SquadAffiliations.Rank(request, [Target(1, nearby), Target(2, expensive)],
            [Sample(1, nearby), Sample(2, nearby), Sample(3, nearby), Sample(4, expensive)],
            nearby.Concat(expensive).ToDictionary(c => c.SpId), [new("소속1", 10, .1) { Id = 1 }, new("소속2", 90, .9) { Id = 2 }]);
        Assert.Equal(1, choices[0].Target.Color.Id);
        Assert.True(choices[0].BudgetEvidence);
        Assert.Equal((3, 3), (choices[0].NearbyUsers, choices[0].NearbySquads));
        Assert.Equal(0, choices[1].NearbyUsers);
    }

    [Fact]
    public void Incomplete_or_sparse_samples_are_labelled_as_an_overall_popularity_estimate()
    {
        var a = Eleven(1);
        var b = Eleven(101);
        var choices = SquadAffiliations.Rank(new() { Formation = Formations.Find("4-2-2-2")!, Budget = 1_100_000_000 },
            [Target(1, a), Target(2, b)], [Sample(1, a), Sample(2, a.Take(10))], a.Concat(b).ToDictionary(c => c.SpId),
            [new("소속1", 10, .1) { Id = 1 }, new("소속2", 90, .9) { Id = 2 }]);
        Assert.Equal(2, choices[0].Target.Color.Id);
        Assert.All(choices, c => { Assert.False(c.BudgetEvidence); Assert.Contains("[추정]", c.Note); });
    }

    [Fact]
    public void Locked_outsiders_are_rejected_instead_of_breaking_the_affiliation()
    {
        var a = Eleven(1);
        var outsider = Card(999, "ST");
        var formation = Formations.Find("4-2-2-2")!;
        var request = new SquadRequest { Formation = formation, Budget = 2_000_000_000,
            TeamColors = [Target(1, a)], Locked = new Dictionary<int, LockedCard> { [9] = new(outsider.SpId, 11, false) } };
        var builder = new SquadBuilder([.. a, outsider], _ => null);
        Assert.Throws<InvalidOperationException>(() => builder.Build(request));
        Assert.Empty(SquadAffiliations.Rank(request, [Target(1, a)], [], a.Append(outsider).ToDictionary(c => c.SpId), []));
    }

    [Fact]
    public void Both_wide_ams_in_4222_prefer_wingers_and_use_the_target_cam_rating()
    {
        var cards = Eleven(1);
        cards.AddRange([Card(201, "CAM", 130), Card(202, "CAM", 130)]);
        var request = new SquadRequest { Formation = Formations.Find("4-2-2-2")!, Budget = 2_000_000_000, TeamColors = [Target(1, cards)] };
        var plan = new SquadBuilder(cards, _ => null).Build(request)[0];
        var wide = plan.Slots.Where(s => s.Position == "CAM").ToList();
        Assert.Equal(2, wide.Count);
        Assert.All(wide, s => { Assert.Contains(s.Card.MainPosition, new[] { "RW", "LW" }); Assert.Equal(141, s.Ovr); });
        Assert.Equal(11, plan.Slots.Select(s => s.Card.PlayerId).Distinct().Count());
        Assert.Equal(11, plan.TeamColors.Single().Members);
        Assert.True(plan.TotalPrice <= request.Budget && plan.TotalPay <= 310);
    }

    [Fact]
    public void Supporting_cf_in_42211_accepts_a_cam_using_complete_stats()
    {
        var cards = Eleven(1);
        cards.AddRange([Card(201, "LM"), Card(202, "RM"), Card(203, "CAM", 122)]);
        var formation = Formations.Find("4-2-2-1-1")!;
        var request = new SquadRequest { Formation = formation, Budget = 2_000_000_000 };
        var plan = new SquadBuilder(cards, _ => null).Build(request)[0];
        var cf = plan.Slots.Single(s => s.Position == "CF");
        Assert.Equal("CAM", cf.Card.MainPosition);
        Assert.Equal(143, cf.Ovr);
        Assert.Equal(147, cf.ShownOvr);
        var incomplete = cf.Card with { Stats = new Dictionary<string, int>() };
        Assert.Null(SquadRoles.Ovr(incomplete, formation, cf.Index, 11));
        Assert.Null(SquadRoles.Ovr(cards[0], formation, cf.Index, 11));
    }

    [Fact]
    public void Formation_change_keeps_the_eleven_and_recalculates_each_new_position()
    {
        using var temp = new TempDb();
        var store = new MarketStore(temp.Path);
        using var http = new HttpClient();
        var lists = new NoLists();
        var market = new MarketService(lists, store, _ => Task.FromResult("[]"));
        var service = new SquadService(market, store, new NoCharts(), new TeamColorCache(store, new(http, new RateLimiter(10), lists)));
        var maker = new SquadMaker(service);
        var fromFormation = Formations.Find("4-2-2-2")!;
        var cards = Eleven(1);
        var from = cards.Select((c, i) => (SquadSlot?)maker.Slot(i, fromFormation.Slots[i], c, 11, owned: i == 0, locked: i == 1)).ToList();
        var remapped = maker.Remap(from, Formations.Find("4-2-2-1-1")!);
        Assert.All(remapped, Assert.NotNull);
        Assert.Equal(cards.Select(c => c.SpId).Order(), remapped.Select(s => s!.Card.SpId).Order());
        Assert.All(remapped, s => { Assert.Equal(11, s!.Grade); Assert.Equal(141, s.Ovr); });
        Assert.True(remapped.Single(s => s!.Card.SpId == cards[0].SpId)!.Owned);
        Assert.True(remapped.Single(s => s!.Card.SpId == cards[1].SpId)!.Locked);
        Assert.Equal("GK", remapped.Single(s => s!.Position == "GK")!.Card.MainPosition);
    }

    [Fact]
    public void Value_chooses_lower_cost_near_the_best_whole_team_ovr()
    {
        SquadPlan Plan(int ovr, long cost) => new("", SquadMode.Strongest, Formations.Find("4-2-2-2")!,
            Enumerable.Range(0, 11).Select(i => new SquadSlot(i, "ST", Card(i + 1, "ST"), 11, ovr, 0, 0, cost, cost, 25, 0, 0, false, false)).ToList(), []);
        var best = Plan(150, 100_000_000);
        var value = Plan(149, 50_000_000);
        var weak = Plan(140, 1_000_000);
        Assert.Equal(best.TotalPrice, SquadAffiliations.Select([best, value, weak], SquadMode.Strongest)[0].TotalPrice);
        Assert.Equal(value.TotalPrice, SquadAffiliations.Select([best, value, weak], SquadMode.Value)[0].TotalPrice);
        Assert.Equal(best.TotalPrice, SquadAffiliations.Select([best, value, weak], SquadMode.Balanced)[0].TotalPrice);
    }

    private sealed class NoLists : IMarketListSource
    {
        public Task<IReadOnlyList<ListRow>> QueryAsync(ListQuery query, CancellationToken ct = default) => throw new InvalidOperationException("No network in squad tests.");
    }
    private sealed class NoCharts : IRankerChartSource
    {
        public Task<RankerChartData> FetchAsync(int from, int to, CancellationToken ct = default) => throw new InvalidOperationException("No network in squad tests.");
    }
}
