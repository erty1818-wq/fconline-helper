using FcHelper.Market;

namespace FcHelper.Tests;

public class ChartAndTeamColorParserTests
{
    // Shapes trimmed from real daily-chart and team-colour responses (2026-09-25).
    private const string PositionPlayers = """
        <div class="item_list swiper-slide"> <div class="player fw _25UCL">
        <a href="/DataCenter/PlayerInfo?spid=856231747&n1Strong=8" target="_blank" class="outlink"></a>
        <div class="playerCardInfoSide"> <div class="ovr value">137</div> <div class="position fw"> ST </div>
        <div class="pay"> <svg width="34px"><path d="M17,33"/></svg> <span>31</span> </div></div>
        <span class="name"><span>킬리안 음바페</span></span>
        <div class="price_wrap"><span class="price span_bp8" title="1,000">1억</span></div> 50명(0.3%) </div></div>
        <div class="item_list swiper-slide"> <a href="/DataCenter/PlayerInfo?spid=253000001&amp;n1Strong=9"></a>
        <div class="ovr value">139</div> <div class="pay"><svg></svg><span>33</span></div> <span class="name"><span>이언 라이트</span></span> 1,246명(10.1%) </div>
        """;

    private const string Matchups = """
        <div class="vs_rank vs_rank4"> <div class="txt">4-2-4</div> <div class="per">222명 (4.3%)</div>
        <div class="win"> <span></span> 79.3% 73승 5무 19패 </div> <div class="lose"> <span></span> 20.7% 19승 5무 73패 </div> </div>
        <div class="vs_rank vs_rank2"> <div class="txt">4-2-2-1-1</div> <div class="per">1,241명 (23.9%)</div>
        <div class="win"> 40.2% 183승 41무 272패 </div> <div class="lose"> 59.8% 272승 41무 183패 </div> </div>
        """;

    private const string TeamColorDetail = """
        <div class="tit">적용조건</div> <div class="content">
        <div class="level lvs1"> <div class="tit">1 단계</div> <div class="lv_content"> <div class="num">3</div> </div> <div class="desc">3명</div>
          <div class="ap_list"> <ul> <li>전체 능력치 +1</li> <li>-</li> </ul> </div> </div>
        <div class="level lvs4"> <div class="tit">4 단계</div> <div class="lv_content"> <div class="num">11</div> </div> <div class="desc">11명</div>
          <div class="ap_list"> <ul> <li>전체 능력치 +4</li> <li>짧은 패스 +3</li> </ul> </div> </div>
        </div> <div class="tit">적용 선수 목록</div>
        """;

    [Fact]
    public void Reads_ranker_picks_with_grade_and_usage()
    {
        var picks = ChartParser.Picks("ST", PositionPlayers);

        Assert.Equal(2, picks.Count);
        Assert.Equal(new RankerPick("ST", 856231747, "킬리안 음바페", 8, 137, 31, 50, 0.003), picks[0]);
        Assert.Equal((9, 1246), (picks[1].Grade, picks[1].Users));
    }

    [Fact]
    public void Matchups_read_the_selected_formations_own_record()
    {
        var m = ChartParser.Matchups("4-2-2-2", Matchups);

        Assert.Equal(new FormationMatchup("4-2-2-2", "4-2-4", 73, 5, 19), m[0]);
        Assert.True(m[0].WinRate > 0.7);
        Assert.Equal((183, 272), (m[1].Wins, m[1].Losses));
    }

    [Fact]
    public void Reads_team_colour_levels_and_a_cards_colours()
    {
        var levels = TeamColorParser.Levels(TeamColorDetail);
        Assert.Equal([(1, 3, 1), (4, 11, 4)], levels.Select(l => (l.Level, l.Members, l.AllStats)));
        Assert.Equal(["전체 능력치 +4", "짧은 패스 +3"], levels[1].Effects);

        var ids = TeamColorParser.CardTeamColors("""<a class="selector_item tdefault0">소속</a><a class="selector_item tdefault2001">대한민국</a><a class="selector_item tspecial30012">트로이카</a>""");
        Assert.Equal([2001, 30012], ids);
    }

    [Fact]
    public void Team_colour_level_follows_the_member_count()
    {
        var tc = new TeamColor(1016, "FC 바르셀로나", TeamColorKind.Club, 11, [new(1, 3, 1, []), new(2, 6, 3, []), new(4, 11, 4, [])]);
        Assert.Null(tc.LevelFor(2));
        Assert.Equal(2, tc.LevelFor(7)!.Level);
        Assert.Equal(TeamColorKind.Nation, TeamColor.KindOf(2001));
    }
}

public class SquadBuilderTests
{
    private static int _id;

    private static MarketCard Card(string position, int ovr1, long price8, int pay = 25, int player = 0, string season = "S1", long price5 = 0)
    {
        var id = Interlocked.Increment(ref _id);
        return new MarketCard
        {
            Group = Formations.GroupOf(position), SpId = 300_000_000L + (player == 0 ? id : player) + id * 1_000_000L % 1_000_000_000,
            Name = $"{position}{id}", Season = season, Pay = pay, Ovr1 = ovr1, WeakFoot = 3, RatingCount = 50,
            Prices = new Dictionary<int, long> { [1] = 5000, [5] = price5 > 0 ? price5 : price8 / 4, [8] = price8 },
            Positions = new Dictionary<string, int> { [position] = ovr1 },
        };
    }

    /// <summary>Per slot a cheap, a good and a star card; the star is 5 OVR better and 10× the price.</summary>
    private static List<MarketCard> Market()
    {
        var cards = new List<MarketCard>();
        foreach (var pos in new[] { "GK", "LB", "CB", "CB", "RB", "CDM", "CDM", "CAM", "CAM", "ST", "ST" }.Distinct())
        {
            var copies = pos is "CB" or "CDM" or "CAM" or "ST" ? 2 : 1;
            for (var i = 0; i < copies; i++)
            {
                cards.Add(Card(pos, 120, 10_000_000));
                cards.Add(Card(pos, 123, 30_000_000));
                cards.Add(Card(pos, 125, 300_000_000));
            }
        }
        return cards;
    }

    private static SquadBuilder Builder(IReadOnlyList<MarketCard> cards) => new(cards, _ => null);

    [Fact]
    public void Strongest_uses_the_budget_and_never_repeats_a_footballer()
    {
        var plan = Builder(Market()).Build(new SquadRequest { Formation = Formations.Find("4-2-2-2")!, Budget = 1_000_000_000 })[0];

        Assert.Equal(11, plan.Slots.Count);
        Assert.True(plan.TotalPrice <= 1_000_000_000);
        Assert.Equal(11, plan.Slots.Select(s => s.Card.PlayerId).Distinct().Count());
        Assert.Contains(plan.Slots, s => s.Ovr == 125 + 15); // some stars fit into 10억
        Assert.All(plan.Slots, s => Assert.Equal(s.Position, s.Card.Positions.Keys.Single()));
    }

    [Fact]
    public void Value_mode_spends_less_than_strongest_for_nearly_the_same_ovr()
    {
        var request = new SquadRequest { Formation = Formations.Find("4-2-2-2")!, Budget = 2_000_000_000 };
        var strongest = Builder(Market()).Build(request)[0];
        var value = Builder(Market()).Build(request with { Mode = SquadMode.Value })[0];

        Assert.True(value.TotalPrice < strongest.TotalPrice / 2);
        Assert.True(value.AverageOvr >= strongest.AverageOvr - 3);
    }

    [Fact]
    public void Salary_cap_and_locked_owned_cards_are_respected()
    {
        var cards = Market();
        var mine = cards.First(c => c.Positions.ContainsKey("GK") && c.Ovr1 == 125);
        var plan = Builder(cards).Build(new SquadRequest
        {
            Formation = Formations.Find("4-2-2-2")!, Budget = 200_000_000, SalaryCap = 11 * 25,
            Locked = new Dictionary<int, LockedCard> { [0] = new(mine.SpId, 8, Owned: true) },
        })[0];

        Assert.Equal(mine.SpId, plan.Slots[0].Card.SpId);
        Assert.True(plan.Slots[0].Owned);
        Assert.True(plan.TotalPrice <= 200_000_000); // the owned star costs nothing
        Assert.True(plan.TotalPay <= 11 * 25);
    }

    [Fact]
    public void Grade_choice_and_impossible_budgets()
    {
        var cards = Market();
        var plan = Builder(cards).Build(new SquadRequest { Formation = Formations.Find("4-2-2-2")!, Budget = 400_000_000, Grades = [5, 8] })[0];
        Assert.All(plan.Slots, s => Assert.Contains(s.Grade, new[] { 5, 8 }));

        var e = Assert.Throws<InvalidOperationException>(() => Builder(cards).Build(new SquadRequest { Formation = Formations.Find("4-2-2-2")!, Budget = 1_000_000 }));
        Assert.Contains("예산", e.Message);
    }

    [Fact]
    public void Team_colour_members_get_the_level_bonus()
    {
        var cards = Market();
        var members = cards.Where(c => c.Ovr1 == 123).Select(c => c.SpId).ToHashSet();
        var tc = new TeamColor(1, "테스트", TeamColorKind.Club, 11, [new(1, 3, 1, []), new(4, 11, 4, [])]);

        var plan = Builder(cards).Build(new SquadRequest
        {
            Formation = Formations.Find("4-2-2-2")!, Budget = 400_000_000, TeamColor = tc, TeamColorMembers = members,
        })[0];

        Assert.Equal(4, plan.TeamColorLevel!.Level);
        Assert.All(plan.Slots, s => Assert.Equal(4, s.TeamColorBonus));
    }
}

public class AdvisorTests
{
    [Fact]
    public void Estimates_the_formation_from_starter_positions()
    {
        // GK, RB, RCB, LCB, LB, RDM, LDM, RAM, LAM, RS, LS
        Assert.Equal("4-2-2-2", Advisors.EstimateFormation([0, 3, 4, 6, 7, 9, 11, 17, 19, 24, 26]));
        // GK, RB, RCB, LCB, LB, CDM, RCM, LCM, RW, LW, ST
        Assert.Equal("4-1-2-3", Advisors.EstimateFormation([0, 3, 4, 6, 7, 10, 13, 15, 23, 27, 25]));
        Assert.Null(Advisors.EstimateFormation([0, 3]));
    }

    [Fact]
    public void Formation_advice_needs_enough_games()
    {
        var m = new[] { new FormationMatchup("4-1-4-1", "4-2-2-2", 41, 9, 25), new FormationMatchup("4-3-3", "4-2-2-2", 5, 0, 1), new FormationMatchup("4-2-4", "4-2-2-2", 19, 5, 73) };
        var a = Advisors.Formation("4-2-2-2", m);
        Assert.Equal("4-1-4-1", a.Best[0].Formation); // 4-3-3 won 5 of 6 but that is too few games
        Assert.Equal("4-2-4", a.Worst[0].Formation);
    }

    [Fact]
    public void Grade_advice_compares_with_the_cheapest_equal_ovr_card()
    {
        MarketCard C(long id, int ovr, params long[] prices) => new()
        {
            Group = "ST", SpId = id, Name = $"c{id}", Season = "S", Ovr1 = ovr, RatingCount = 50,
            Prices = prices.Select((p, i) => (p, i)).ToDictionary(t => t.i + 1, t => t.p), Positions = new Dictionary<string, int> { ["ST"] = ovr },
        };
        var star = C(100_000_001, 125, 100_000, 200_000, 400_000, 800_000, 1_600_000, 3_200_000, 6_400_000, 50_000_000);
        var other = C(100_000_002, 130, 20_000, 40_000, 80_000, 160_000, 320_000, 640_000, 1_280_000, 2_560_000);

        var a = Advisors.Grade(star, "ST", [star, other], from: 5, to: 8);

        // star +8 is OVR 140; the other card reaches 140 at +7 (130 - 3 + 14 = 141) for 128만, far below the 4,840만 upgrade.
        Assert.Equal(8, a.Steps.Count);
        Assert.Equal((other.SpId, 7), (a.Alternative!.SpId, a.AlternativeGrade));
        Assert.True(a.AlternativePrice < a.UpgradeCost);
        Assert.Null(a.CompetitiveUpTo); // the star is dearer than the alternative at every grade
    }
}
