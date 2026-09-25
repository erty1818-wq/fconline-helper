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
    public void Reads_the_thirty_team_colours_of_the_daily_chart_page()
    {
        const string html = """
            <div class="swiper-slide teamcolor_list__item" title="전체 능력치 +4">
                <div class="txt">레알 마드리드</div>
                <div class="per">409명 (6.9%)</div>
                <a href="javascript:;" class="show_link" onclick="DailyChart.GetTeamColorVsInfo('1018');"><span></span></a>
            </div>
            <div class="swiper-slide teamcolor_list__item" title="">
                <div class="txt">잉글랜드</div>
                <div class="per">1,145명 (2.5%)</div>
                <a href="javascript:;" class="show_link" onclick="DailyChart.GetTeamColorVsInfo('2004');"><span></span></a>
            </div>
            """;
        var colors = ChartParser.TeamColorList(html);
        Assert.Equal(2, colors.Count);
        Assert.Equal(("레알 마드리드", 409, 1018), (colors[0].Name, colors[0].Users, colors[0].Id));
        Assert.Equal(1145, colors[1].Users);
        Assert.Equal(0.025, colors[1].Share, 3);
    }

    [Fact]
    public void Team_colour_level_follows_the_member_count()
    {
        var tc = new TeamColor(1016, "FC 바르셀로나", TeamColorCategory.Affiliation, 11, [new(1, 3, 1, []), new(2, 6, 3, []), new(4, 11, 4, [])]);
        Assert.Null(tc.LevelFor(2));
        Assert.Equal(2, tc.LevelFor(7)!.Level);
        Assert.Equal(TeamColorKind.Club, tc.Kind);
        Assert.True(tc.AppliesToSquad);
        Assert.Equal(TeamColorKind.Nation, (tc with { Id = 2002 }).Kind);
        // "2026 프랑스" is listed under 특성 even though its id looks like a club season: only its cards get the bonus.
        var feature = new TeamColor(40515, "2024 프랑스", TeamColorCategory.Feature, 8, []);
        Assert.Equal(TeamColorKind.Feature, feature.Kind);
        Assert.False(feature.AppliesToSquad);
    }

    [Fact]
    public void Single_stat_bonuses_count_by_their_weight_in_the_position()
    {
        var level = new TeamColorLevel(1, 8, 0, ["골 결정력 +3", "위치 선정 +3", "가속력 +2", "속력 +2"]);
        Assert.Equal(new Dictionary<string, int> { ["골 결정력"] = 3, ["위치 선정"] = 3, ["가속력"] = 2, ["속력"] = 2 }, TeamColorParser.StatBonuses(level.Effects));
        Assert.Equal(3 * .18 + 3 * .13 + 2 * .04 + 2 * .05, level.OvrGain("RS"), 3);
        Assert.True(level.OvrGain("CB") < level.OvrGain("ST"));
        Assert.Equal(4 + 3 * .15, new TeamColorLevel(4, 11, 4, ["전체 능력치 +4", "드리블 +3"]).OvrGain("LM"), 3);
    }

    [Fact]
    public void Reads_the_salary_cap_from_the_squad_maker_script()
    {
        const string js = "t=e?.totalPay,i=Number(t??0)>310,a=Math.round(Math.min(Number(t??0)/310*100,100)),x=Number(n??0)>310";
        Assert.Equal(310, SalaryCapSource.Parse(js));
        Assert.Null(SalaryCapSource.Parse("i=Number(t??0)>310,a=Math.round(Math.min(Number(t??0)/300*100,100))"));
        Assert.Null(SalaryCapSource.Parse("nothing here"));
    }

    [Fact]
    public void Sale_fee_follows_the_official_calculator()
    {
        const long price = 10_000_000_000; // 100억
        Assert.Equal(4_000_000_000, SaleFee.Standard.FeeOn(price));
        Assert.Equal(2_800_000_000, new SaleFee(PcRoom: true).FeeOn(price));
        Assert.Equal(3_200_000_000, new SaleFee(TopClass: true).FeeOn(price));
        Assert.Equal(2_000_000_000, new SaleFee(true, true).FeeOn(price));
        Assert.Equal(2_400_000_000, new SaleFee(PcRoom: true, CouponPercent: 10).FeeOn(price));
        Assert.Equal(2_700_000_000, new SaleFee(PcRoom: true, CouponPercent: 10, CouponMaxDiscount: 100_000_000).FeeOn(price));
        Assert.Equal(0, new SaleFee(true, true, CouponPercent: 80).FeeOn(price)); // coupon limited to 100 − 50
        Assert.Equal(7_200_000_000, new SaleFee(PcRoom: true).NetOf(price));
        Assert.Equal(0.28, new SaleFee(PcRoom: true).Rate, 3);
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
        var tc = new TeamColor(1, "테스트", TeamColorCategory.Affiliation, 11, [new(1, 3, 1, []), new(4, 11, 4, [])]);

        var plan = Builder(cards).Build(new SquadRequest
        {
            Formation = Formations.Find("4-2-2-2")!, Budget = 400_000_000, TeamColors = [new TeamColorTarget(tc, members)],
        })[0];

        Assert.Equal(4, plan.TeamColors[0].Level!.Level);
        Assert.All(plan.Slots, s => Assert.Equal(4, s.TeamColorBonus));
    }

    [Fact]
    public void Affiliation_bonus_reaches_every_starter_once_the_level_is_met()
    {
        var cards = Market();
        // Only three weaker cards count, but +1 for all eleven beats 3 OVR lost on three slots.
        var members = cards.Where(c => c.Ovr1 == 120 && c.Group is "GK" or "FB").Select(c => c.SpId).ToHashSet();
        var tc = new TeamColor(2002, "프랑스", TeamColorCategory.Affiliation, 11, [new(1, 3, 1, []), new(4, 11, 4, [])]);

        var plan = Builder(cards).Build(new SquadRequest
        {
            Formation = Formations.Find("4-2-2-2")!, Budget = 400_000_000, TeamColors = [new TeamColorTarget(tc, members)], OnlyAffiliationMembers = false,
        })[0];

        Assert.Equal(3, plan.TeamColors[0].Members);
        Assert.All(plan.Slots, s => Assert.Equal(1, s.TeamColorBonus));

        // By default a 소속 squad is members only: here only three slots have members, so it cannot be built.
        Assert.Throws<InvalidOperationException>(() => Builder(cards).Build(new SquadRequest
        {
            Formation = Formations.Find("4-2-2-2")!, Budget = 400_000_000, TeamColors = [new TeamColorTarget(tc, members)],
        }));
    }

    [Fact]
    public void Enhance_colour_counts_cards_by_grade_and_only_the_best_one_applies()
    {
        var platinum = new TeamColor(10004, "백금빛 물결", TeamColorCategory.Enhance, 8, [new(1, 5, 4, ["전체 능력치 +4"]), new(2, 8, 5, ["전체 능력치 +5"])]);
        var gold = new TeamColor(10001, "금빛 물결", TeamColorCategory.Enhance, 8, [new(1, 5, 3, ["전체 능력치 +3"]), new(2, 8, 4, ["전체 능력치 +4"])]);
        Assert.Equal(11, platinum.EnhanceMinGrade);
        var targets = new[] { new TeamColorTarget(platinum, new HashSet<long>()), new TeamColorTarget(gold, new HashSet<long>()) };
        Assert.True(targets[0].Counts(1, 11));
        Assert.False(targets[0].Counts(1, 10));

        // Eight +11 cards: 백금빛 8명 (+5) and 금빛 8명 (+4) both reached; only +5 applies, to every starter.
        Assert.Equal(5, TeamColorTarget.Gain(targets, [8, 11], _ => true, "ST"));

        // The builder: +11 costs twice +8 here, but +5 for all eleven is worth eight +11 cards.
        var cards = Market();
        foreach (var i in Enumerable.Range(0, cards.Count))
            cards[i] = cards[i] with { Prices = new Dictionary<int, long>(cards[i].Prices) { [11] = cards[i].PriceAt(8) * 2 } };
        var plan = Builder(cards).Build(new SquadRequest
        {
            Formation = Formations.Find("4-2-2-2")!, Budget = 600_000_000, Grades = [8, 11], TeamColors = targets,
        })[0];
        Assert.True(plan.Slots.Count(s => s.Grade == 11) >= 8);
        Assert.Single(plan.TeamColors);
        Assert.Equal("백금빛 물결", plan.TeamColors[0].Color.Name);
        Assert.All(plan.Slots, s => Assert.Equal(5, s.TeamColorBonus));
    }

    [Fact]
    public void Excluded_cards_are_not_used_at_that_grade()
    {
        var cards = Market();
        var request = new SquadRequest { Formation = Formations.Find("4-2-2-2")!, Budget = 4_000_000_000 };
        var first = Builder(cards).Build(request)[0];
        var gk = first.Slots.Single(s => s.Position == "GK");

        var again = Builder(cards).Build(request with { ExcludedCards = new HashSet<(long, int)> { (gk.Card.SpId, gk.Grade) } })[0];

        Assert.DoesNotContain(again.Slots, s => s.Card.SpId == gk.Card.SpId && s.Grade == gk.Grade);
    }

    [Fact]
    public void Ranker_allocation_moves_the_money_to_the_strikers()
    {
        var cards = Market();
        var request = new SquadRequest { Formation = Formations.Find("4-2-2-2")!, Budget = 1_000_000_000 };
        var free = Builder(cards).Build(request)[0];
        // Rankers put 30% of the squad on each striker and little on the rest.
        var roles = new[] { "GK", "FB", "CB", "CDM", "CAM" }.ToDictionary(r => r, r => new RoleShare(r, 10, 0.03, 0.005, 0.05, 25, 20, 30, 130));
        roles["ST"] = new RoleShare("ST", 10, 0.3, 0.25, 0.35, 25, 20, 30, 140);
        var alloc = new RankerAllocation(10, 0, 1_000_000_000, 280, roles);

        var plan = Builder(cards).Build(request with { Allocation = alloc })[0];

        Assert.All(plan.Slots.Where(s => s.Position == "ST"), s => Assert.True(s.Price >= 0.25 * 0.7 * 1_000_000_000));
        Assert.True(plan.Slots.Where(s => s.Position == "ST").Sum(s => s.Price) > free.Slots.Where(s => s.Position == "ST").Sum(s => s.Price));
    }

    [Fact]
    public void Affiliation_squad_uses_only_member_cards()
    {
        var cards = Market();
        // Members: the cheap cards everywhere; the stronger ones are not in the colour.
        var members = cards.Where(c => c.Ovr1 == 120).Select(c => c.SpId).ToHashSet();
        var tc = new TeamColor(1016, "FC 바르셀로나", TeamColorCategory.Affiliation, 11, [new(1, 3, 1, []), new(4, 11, 4, [])]);

        var plan = Builder(cards).Build(new SquadRequest
        {
            Formation = Formations.Find("4-2-2-2")!, Budget = 4_000_000_000, TeamColors = [new TeamColorTarget(tc, members)],
        })[0];

        Assert.All(plan.Slots, s => Assert.Contains(s.Card.SpId, members));
        Assert.Equal(4, plan.TeamColors[0].Level!.Level);
    }

    [Fact]
    public void Feature_bonus_goes_only_to_its_members()
    {
        var cards = Market();
        var members = cards.Where(c => c.Ovr1 == 123 && c.Group is "ST" or "CAM").Select(c => c.SpId).ToHashSet();
        var feature = new TeamColor(40515, "2024 프랑스", TeamColorCategory.Feature, 4, [new(1, 3, 0, ["속력 +2", "골 결정력 +3"])]);

        var plan = Builder(cards).Build(new SquadRequest
        {
            Formation = Formations.Find("4-2-2-2")!, Budget = 400_000_000, TeamColors = [new TeamColorTarget(feature, members)],
        })[0];

        Assert.True(plan.TeamColors[0].Members >= 3);
        foreach (var s in plan.Slots)
        {
            if (!members.Contains(s.Card.SpId)) Assert.Equal(0, s.TeamColorBonus);
            else if (s.Position == "ST") Assert.Equal(2 * .05 + 3 * .18, s.TeamColorBonus, 3);
            else Assert.True(s.TeamColorBonus > 0);
        }

        // An upgrade may not take the colour below its level: members at the threshold are only swapped for members.
        var pool = cards.Concat([Card("ST", 130, 50_000_000), Card("CAM", 130, 50_000_000)]).ToList();
        var current = Advisors.WithTeamColors(plan.Slots.Select(x => x with { Owned = true }).ToList(), [new TeamColorTarget(feature, members)]);
        var upgrades = Advisors.Upgrades(current, pool, _ => null, 1_000_000_000, [8], teamColors: [new TeamColorTarget(feature, members)]);
        Assert.NotEmpty(upgrades);
        if (plan.TeamColors[0].Members == 3)
            Assert.All(upgrades.SelectMany(u => u.Moves), m => Assert.True(!members.Contains(m.Out.Card.SpId) || members.Contains(m.In.SpId)));
    }

    [Fact]
    public void A_cheap_card_too_weak_to_play_stays_out_while_there_are_others()
    {
        var cards = Market();
        // An old season of a striker: at +8 only OVR 125, almost free — the squad takes it only if nothing else fits.
        var junk = Card("ST", 110, 10_000);
        cards.Add(junk);
        var plan = Builder(cards).Build(new SquadRequest { Formation = Formations.Find("4-2-2-2")!, Budget = 1_000_000_000, Mode = SquadMode.Value })[0];
        Assert.DoesNotContain(plan.Slots, s => s.Card.SpId == junk.SpId);
    }

    [Fact]
    public void Ranker_only_widens_a_position_rankers_do_not_show()
    {
        var cards = Market();
        // The chart knows only one GK: every other slot would be empty with 랭커가 쓰는 카드만.
        var gk = cards.First(c => c.Group == "GK");
        var usage = new RankerUsage([new RankerPick("GK", gk.SpId, gk.Name, 8, 140, 25, 50, 0.5)]);
        var plans = new SquadBuilder(cards, _ => null, usage).Build(new SquadRequest
        {
            Formation = Formations.Find("4-2-2-2")!, Budget = 2_000_000_000, RankerPicksOnly = true,
        });
        Assert.Equal(11, plans[0].Slots.Count); // too few ranker cards per slot: widened to all of them, not "no card"
    }

    [Fact]
    public void The_cards_rankers_of_the_colour_play_win_over_equal_ones()
    {
        var cards = Market();
        var strikers = cards.Where(c => c.Group == "ST" && c.Ovr1 == 123).ToList();
        var twin = Card("ST", 123, 30_000_000);
        cards.Add(twin);
        var squads = Enumerable.Range(1, 6).Select(i => new RankerSquad(i, $"r{i}", 0, [new RankerSquadPlayer(twin.SpId, 8, "ST")])).ToList();
        var plan = Builder(cards).Build(new SquadRequest
        {
            Formation = Formations.Find("4-2-2-2")!, Budget = 700_000_000, Mode = SquadMode.Balanced,
            ColorUsage = new ColorCardUsage("테스트", squads),
        })[0];
        Assert.Contains(plan.Slots, s => s.Card.SpId == twin.SpId);
    }

    [Fact]
    public void A_feature_colour_reaches_its_first_level()
    {
        var cards = Market();
        // Five members, each 2 OVR below the best card of its slot: the colour is met, not skipped.
        var members = cards.Where(c => c.Ovr1 == 123 && c.Group is "CB" or "CDM" or "ST").Take(5).Select(c => c.SpId).ToHashSet();
        var feature = new TeamColor(40190, "테스트 트레블", TeamColorCategory.Feature, 5, [new(1, 5, 0, ["골 결정력 +2"])]);
        var plan = Builder(cards).Build(new SquadRequest
        {
            Formation = Formations.Find("4-2-2-2")!, Budget = 5_000_000_000, TeamColors = [new TeamColorTarget(feature, members)],
        })[0];
        Assert.True(plan.Slots.Count(s => members.Contains(s.Card.SpId)) >= 5);
        Assert.NotNull(plan.TeamColors.Single(t => t.Color.Id == 40190).Level);
    }
    [Fact]
    public void A_card_plays_its_own_position_unless_rankers_use_it_elsewhere()
    {
        var cards = Market();
        // A centre back that also lists CDM, far stronger there than the CDM cards: it still is not a CDM.
        var cb = Card("CB", 128, 30_000_000);
        cb = cb with { Positions = new Dictionary<string, int> { ["CB"] = 128, ["CDM"] = 128 } };
        cards.Add(cb);
        var plan = Builder(cards).Build(new SquadRequest { Formation = Formations.Find("4-2-2-2")!, Budget = 2_000_000_000 })[0];
        Assert.DoesNotContain(plan.Slots, s => s.Card.SpId == cb.SpId && s.Position == "CDM");

        // Rankers field it at CDM: then it may go there.
        var usage = new RankerUsage([new RankerPick("CDM", cb.SpId, cb.Name, 8, 143, 25, 80, 0.4)]);
        var withRankers = new SquadBuilder(cards, _ => null, usage).Build(new SquadRequest { Formation = Formations.Find("4-2-2-2")!, Budget = 2_000_000_000 })[0];
        Assert.Contains(withRankers.Slots, s => s.Card.SpId == cb.SpId);
    }

    [Fact]
    public void An_old_season_keeper_rankers_use_passes_the_ovr_floor()
    {
        var cards = Market();
        cards.AddRange([Card("GK", 125, 30_000_000), Card("GK", 124, 30_000_000)]);
        // OVR 97 at +1 (112 at +8), far under the keeper floor of 140, but rankers play it.
        var oldKeeper = Card("GK", 97, 5_000_000);
        cards.Add(oldKeeper);
        var weak = Card("GK", 110, 1_000_000); // 125 at +8, nobody plays it
        cards.Add(weak);
        var usage = new RankerUsage([new RankerPick("GK", oldKeeper.SpId, oldKeeper.Name, 8, 112, 24, 300, 0.6)]);
        var request = new SquadRequest { Formation = Formations.Find("4-2-2-2")!, Budget = 2_000_000_000 };
        var offered = new SquadBuilder(cards, _ => null, usage).CandidatesAt(request, 0).Select(c => c.Card.SpId).ToHashSet();
        Assert.Contains(oldKeeper.SpId, offered);
        Assert.DoesNotContain(weak.SpId, offered);
    }

    [Fact]
    public void The_main_position_is_the_first_one_listed()
    {
        var card = Card("RW", 120, 1) with { Positions = new Dictionary<string, int> { ["RW"] = 120, ["CAM"] = 120, ["RM"] = 121 } };
        Assert.Equal("RW", card.MainPosition);
        Assert.True(card.PlaysAsMain("LW"));
        Assert.False(card.PlaysAsMain("CAM"));
        Assert.Equal(85, MarketGroups.OvrMinOf("GK"));
        Assert.Equal(105, MarketGroups.OvrMinOf("ST"));
    }
}

public class LiquidityTests
{
    [Fact]
    public void Reads_the_daily_prices_of_the_price_graph()
    {
        const string html = """
            <script> var json1 = {
                "time": [ "9.22", "9.23", "9.24", ],
                "value": [ "117490900", "141000000", "141000000", ],
            }; </script>
            """;
        Assert.Equal([117_490_900L, 141_000_000, 141_000_000], PriceHistoryClient.Parse(html));
    }

    [Fact]
    public void A_price_that_rarely_moves_is_a_listing_nobody_sells()
    {
        var busy = Enumerable.Range(0, 60).Select(i => 100L + i).ToList();
        var stale = Enumerable.Range(0, 60).Select(i => 100L + i / 10).ToList(); // moves every ten days
        Assert.True(PriceHistoryClient.Measure(1, 8, busy).Tradable);
        var s = PriceHistoryClient.Measure(1, 11, stale);
        Assert.Equal(3, s.Changes30);
        Assert.False(s.Tradable);
        Assert.True(PriceHistoryClient.Measure(1, 13, [5, 5, 5]).Tradable); // too new to judge
    }
}

public class RankerAllocationTests
{
    [Fact]
    public void Reads_rank_nickname_and_team_value_from_the_ranking()
    {
        const string html = """
            <div class="tr">
                <span class="td rank_no">21</span>
                <span class="td rank_coach">
                    <span class="coach_wrap"><span class="name profile_pointer" data-sn="434811662">프라임핀트</span></span>
                    <span class="price" alt="69,492,316,970" title="69,492,316,970">694억 9,231만</span>
                </span>
            </div>
            """;
        Assert.Equal([(21, "프라임핀트", 69_492_316_970L)], RankerSquadClient.Parse(html));
    }

    private static MarketCard Card(long spId, string pos, long price8, int pay) => new()
    {
        Group = Formations.GroupOf(pos), SpId = spId, Name = $"c{spId}", Season = "S", Pay = pay, Ovr1 = 120, WeakFoot = 3, RatingCount = 50,
        Prices = new Dictionary<int, long> { [1] = 5000, [8] = price8 }, Positions = new Dictionary<string, int> { [pos] = 120 },
    };

    [Fact]
    public void Splits_price_and_salary_by_role_and_leaves_out_squads_under_10_eok()
    {
        var formation = Formations.Find("4-2-2-2")!;
        var cards = new Dictionary<long, MarketCard>();
        RankerSquad Squad(int n, long strikerPrice)
        {
            var players = formation.Slots.Select((pos, i) =>
            {
                var id = n * 100L + i;
                cards[id] = Card(id, pos, pos == "ST" ? strikerPrice : 100_000_000, pos == "ST" ? 32 : 27);
                return new RankerSquadPlayer(id, 8, pos == "ST" ? (i == 9 ? "LS" : "RS") : pos);
            }).ToList();
            return new RankerSquad(n, $"r{n}", 0, players);
        }
        // Two real squads (9 × 1억 + 2 × 20억 = 49억) and one being rebuilt (under 10억).
        var squads = new[] { Squad(1, 2_000_000_000), Squad(2, 2_000_000_000), Squad(3, 10_000_000) };

        var a = RankerAllocation.Analyse(squads, cards);

        Assert.Equal(2, a.Squads);
        Assert.Equal(1, a.Skipped);
        Assert.Equal(20.0 / 49, a.Roles["ST"].PriceShare, 3);
        Assert.Equal(32, a.Roles["ST"].Pay, 3);
        Assert.Equal("FB", RankerAllocation.RoleOf("LB"));
        Assert.Equal(4, a.Roles["FB"].Slots);
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
