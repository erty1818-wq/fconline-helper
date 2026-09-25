using System.Globalization;
using FcHelper.Data;
using FcHelper.Market;
using FcHelper.NexonApi;
using FcHelper.Services;

/// <summary>Squad and market commands: they read the market data the app keeps fresh; only ranker stats and opponent lookups use the API key.</summary>
internal static class SquadCommands
{
    public static readonly string[] Names = ["squad", "picks", "grade", "salary", "movers", "formation", "teamcolor", "upgrade", "tailor", "value", "factors", "traits", "allocation", "mteam", "mhoney"];

    public static async Task<int> RunAsync(string command, List<string> positional, Func<string, string?> option, string? apiKey)
    {
        var dbPath = option("db") ?? AppPaths.DatabasePath;
        var http = new HttpClient();
        var dataCenter = new RateLimiter(0.5);
        var store = new MarketStore(dbPath);
        var lists = new DataCenterListClient(http, dataCenter);
        var market = new MarketService(lists, store, _ => Task.FromResult("[]"));
        FcOnlineApi? api = string.IsNullOrWhiteSpace(apiKey) ? null : new FcOnlineApi(http, apiKey, new RateLimiter(5));
        var squads = new SquadService(market, store, new DataCenterChartClient(http, dataCenter),
            new TeamColorCache(store, new DataCenterTeamColorClient(http, dataCenter, lists)), api,
            salaryCap: new SalaryCapCache(store, new SalaryCapSource(http, dataCenter)),
            rankerSquads: new RankerSquadClient(http, dataCenter, () => api),
            liquidity: new LiquidityCache(store, new PriceHistoryClient(http, dataCenter)),
            managerRankers: new RankerSquadClient(http, dataCenter, () => api, "manager", 52));
        if (store.LatestFinished() is null)
        {
            Console.Error.WriteLine("시세 데이터가 없습니다. 앱을 켜 두면 자동으로 받습니다.");
            return 3;
        }
        var (rankFrom, rankTo) = Range(option("rankers"));

        try
        {
            switch (command)
            {
                case "squad": return await Squad(squads, option, rankFrom, rankTo);
                case "value": return await Value(squads, option);
                case "mhoney":
                    var honey = await squads.ManagerHoneyAsync(option("pos") ?? "ST", Int(option("grade"), 8), Price(option("min"), 0), Price(option("max"), long.MaxValue),
                        Int(option("minovr"), 135), Int(option("matches"), 20));
                    foreach (var h in honey.Take(Int(option("top"), 15)))
                        Console.WriteLine($"  {(h.IsHoney ? "🐝" : "  ")} {h.Card.Name,-10} {h.Card.Season,-8} OVR {h.Ovr} · {Bp.Format(h.Price),7} · {h.Stats.MatchCount}경기 골 {h.Stats.Goal:0.00} 도움 {h.Stats.Assist:0.00} · 활약 {h.Score:0.0} (가격대 {h.Expected:0.0}) · {h.Ratio:0.00}배");
                    Console.WriteLine($"{honey.Count}장 · 감독모드 ranker-stats [계산]");
                    return 0;
                case "mteam":
                    var mprogress = new Progress<string>(m => Console.Error.Write($"\r{m}          "));
                    if (option("tc") is { } tcName)
                    {
                        var team = await squads.ManagerTeamPlayersAsync(tcName, mprogress);
                        Console.Error.WriteLine();
                        Console.WriteLine($"감독모드 {tcName} · 랭커 스쿼드 {team.Squads}팀 기준");
                        foreach (var (role, list) in team.ByRole.OrderBy(kv => kv.Key))
                            Console.WriteLine($"  [{RankerAllocation.RoleName(role)}] " + string.Join(", ", list.Select(p => $"{p.Name} {p.Season} +{p.Grade} {p.Users}명({p.Share:P0})")));
                        return 0;
                    }
                    var rates = await squads.ManagerPickRatesAsync(mprogress);
                    Console.Error.WriteLine();
                    foreach (var (r, i) in rates.Take(Int(option("top"), 20)).Select((r, i) => (r, i)))
                        Console.WriteLine($"  #{i + 1,-2} {r.TeamColor,-14} 랭커 {r.Rankers,4}명 ({r.Share:P1}) · 평균 {Bp.Format(r.AverageValue),8} · 최고 {r.Richest.Nickname}({Bp.Format(r.Richest.TeamValue)}) · 최저 {r.Poorest.Nickname}({Bp.Format(r.Poorest.TeamValue)})");
                    return 0;
                case "allocation":
                    var alloc = await squads.RankerAllocationAsync(progress: new Progress<string>(m => Console.Error.Write($"\r{m}          ")));
                    Console.Error.WriteLine();
                    if (alloc is null) { Console.Error.WriteLine("랭커 스쿼드를 받지 못했습니다 (API 키 필요)."); return 3; }
                    Console.WriteLine($"랭커 {alloc.Squads}팀 (10억 미만·카드 불명 {alloc.Skipped}팀 제외) · 스쿼드 시세 중간값 {Bp.Format(alloc.MedianValue)} · 급여 중간값 {alloc.MedianPay:0}");
                    Console.WriteLine("  역할          칸수  가격 비중 평균 (10~90%)      급여 평균 (10~90%)  OVR 평균");
                    foreach (var r in alloc.Roles.Values.OrderByDescending(r => r.PriceShare))
                        Console.WriteLine($"  {RankerAllocation.RoleName(r.Role),-8} {r.Slots,5}  {r.PriceShare,6:P1} ({r.PriceShareLow:P1}~{r.PriceShareHigh:P1})   {r.Pay,5:0.0} ({r.PayLow}~{r.PayHigh})   {r.Ovr:0.0}   평균 +{alloc.RoleGrade.GetValueOrDefault(r.Role):0.0}");
                    Console.WriteLine("  +11 카드 수별 팀 수: " + string.Join("  ", alloc.PlusElevenCounts.OrderByDescending(kv => kv.Key).Select(kv => $"{kv.Key}명 {kv.Value}팀")));
                    return 0;
                case "factors": return await Factors(squads, option);
                case "traits":
                    await squads.RankerTeamColorMembersAsync();
                    var tgrade = Math.Clamp(Int(option("grade"), 8), 1, Grades.MaxTradable);
                    foreach (var minOvr in new int?[] { null, option("minovr") is { } mo ? Int(mo, 135) : 135 })
                    {
                        Console.WriteLine($"\n■ 신특 평균 값어치 · +{tgrade} · {(minOvr is { } m ? $"OVR {m}+ 카드만" : "전체 카드")} [추정]");
                        foreach (var t in squads.Market.TraitValues(tgrade, minOvr))
                            Console.WriteLine($"  {t.Scope,-22} {t.Trait,-10} {t.Percent,7:+0;-0}% ({t.Low:+0;-0}~{t.High:+0;-0}%) OVR {t.OvrEquivalent:+0.0;-0.0} · {t.Cards}장{(t.Clear ? "" : " (불확실)")}"
                                + "  [" + string.Join(", ", t.ByGroup.Select(g => $"{g.Group} {g.Factor.Percent:+0;-0}%")) + "]");
                    }
                    return 0;
                case "picks":
                    foreach (var h in (await squads.HiddenRankerPicksAsync(option("pos"), await Filter(squads, option, 0),
                                 Int(option("users"), 10), rankFrom, rankTo)).Take(Int(option("top"), 15)))
                        Console.WriteLine($"{h.Position,-4} {h.Card.Name,-10} {h.Card.Season,-8} +{h.Grade,-2} OVR {h.Ovr} · 랭커 {h.Users}명({h.Share:P1}) · 시세 {Bp.Format(h.Price),7} · 예상 {Bp.Format(h.Expected),7} · {h.Discount:+0%;-0%}");
                    Console.WriteLine("랭커 사용 = 데일리 차트(전날 공식경기). 예상가 = 같은 스펙 카드의 오늘 시세 [추정].");
                    return 0;
                case "grade": return Grade(squads, option);
                case "salary":
                    foreach (var s in squads.SalaryEfficiency(option("pos") ?? "ST", Int(option("grade"), 8), Price(option("min"), 0), Price(option("max"), long.MaxValue),
                                 Int(option("minovr"), 0)).Take(Int(option("top"), 15)))
                        Console.WriteLine($"{s.Card.Name,-10} {s.Card.Season,-8} OVR {s.Ovr} (환산 {s.EffectiveOvr:0.0}) · 급여 {s.Pay} · 급여당 {s.OvrPerPay:0.00} · 시세 {Bp.Format(s.Price)}");
                    return 0;
                case "movers":
                    var moves = squads.PriceMoves(Int(option("grade"), 8), Int(option("days"), 7), Price(option("min"), 10_000_000));
                    if (moves.Count == 0) Console.WriteLine("비교할 과거 시세가 아직 없습니다. 하루 이상 자동 갱신이 쌓이면 보입니다.");
                    foreach (var m in moves.Take(10).Concat(moves.TakeLast(5)))
                        Console.WriteLine($"{m.Card.Name,-10} {m.Card.Season,-8} +{m.Grade} {Bp.Format(m.Before),7} → {Bp.Format(m.Now),7} ({m.Change:+0%;-0%}, {m.Since:M/d} 대비) · 스펙 대비 {m.Discount:+0%;-0%}");
                    return 0;
                case "formation":
                    var vs = option("vs") ?? "4-2-2-2";
                    if (await squads.FormationAdviceAsync(vs, rankFrom, rankTo) is not { } advice) { Console.WriteLine("데일리 차트를 받지 못했습니다."); return 5; }
                    Console.WriteLine($"상대 {vs} 상대로 랭커 전적 (전날 공식경기, 30경기 이상):");
                    foreach (var m in advice.Best) Console.WriteLine($"  강함 {m.Formation,-10} {m.Wins}승 {m.Draws}무 {m.Losses}패 (승률 {m.WinRate:P0})");
                    foreach (var m in advice.Worst) Console.WriteLine($"  약함 {m.Formation,-10} {m.Wins}승 {m.Draws}무 {m.Losses}패 (승률 {m.WinRate:P0})");
                    return 0;
                case "teamcolor": return await TeamColor(squads, option);
                case "upgrade": return await Upgrade(squads, option, dbPath);
                case "tailor": return await Tailor(squads, positional, option, api, dbPath);
            }
        }
        catch (InvalidOperationException e)
        {
            Console.Error.WriteLine(e.Message);
            return 4;
        }
        return 1;
    }

    private static async Task<int> Squad(SquadService squads, Func<string, string?> option, int rankFrom, int rankTo)
    {
        var formation = option("formation") is { } f && f.Contains(',')
            ? Formations.Custom("사용자", f.Split(','))
            : Formations.Find(option("formation") ?? "4-2-2-2") ?? throw new InvalidOperationException(
                $"포메이션은 {string.Join(", ", Formations.All.Select(x => x.Name))} 또는 GK,LB,CB,... 11개입니다.");
        // --teamcolor 2002,40515 = 소속 프랑스 + 특성 2024 프랑스.
        var targets = await squads.TargetsAsync((option("teamcolor") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(id => Int(id, 0)));
        var cap = option("cap") is { } c ? Int(c, int.MaxValue) : await squads.SalaryCapAsync();
        var request = new SquadRequest
        {
            Formation = formation,
            Budget = Price(option("budget"), long.MaxValue),
            SalaryCap = cap,
            Grades = (option("grades") ?? "8").Split(',').Select(g => Int(g, 8)).ToList(),
            TeamColors = targets,
            Allocation = option("alloc") is "no" or "0" ? null : await squads.RankerAllocationAsync(),
            RankerPicksOnly = option("ranker-only") is "yes" or "y" or "1",
        };
        var mode = option("mode") ?? "all";
        var plans = mode == "all"
            ? await squads.CompareModesAsync(request, rankFrom, rankTo)
            : await squads.BuildAsync(request with { Mode = mode switch { "balanced" => SquadMode.Balanced, "value" => SquadMode.Value, "ranker" => SquadMode.RankerPicks, _ => SquadMode.Strongest } }, rankFrom, rankTo);
        foreach (var p in plans)
        {
            Console.WriteLine($"\n■ {p.Label} · {p.Formation.Name} · 총 {Bp.Format(p.TotalPrice)} · 급여 {p.TotalPay}/{cap} · 평균 OVR {p.AverageOvr:0.0} · 환산 {p.AverageEffectiveOvr:0.0}"
                + string.Concat(p.TeamColors.Select(t => $" · {TeamColorText(t)}")));
            foreach (var s in p.Slots)
                Console.WriteLine($"  {s.Position,-4} {s.Card.Name,-10} {s.Card.Season,-8} +{s.Grade,-2} OVR {s.Ovr + s.TeamColorBonus,5:0.#}{(s.TeamColorBonus > 0 ? "*" : " ")} 환산 {s.EffectiveOvr,5:0.0} · {Bp.Format(s.Price),7} · 급여 {s.Pay,2}"
                    + (s.RankerUsers > 0 ? $" · 랭커 {s.RankerUsers}명" : "") + (s.Discount < -0.15 ? $" · 스펙 대비 {s.Discount:P0}" : ""));
        }
        Console.WriteLine("\n환산 OVR = OVR + 시장이 약발·특성·개인기·능력치에 매기는 값(OVR 단위) [추정]. * = 팀컬러 보너스 포함.");
        Console.WriteLine("팀컬러: 소속 보너스는 선발 전원, 특성 보너스는 해당 카드만. 개별 능력치 보너스의 OVR 환산은 [추정].");
        return 0;
    }

    private static string TeamColorText(AppliedTeamColor t) =>
        $"{FcHelper.Market.TeamColor.CategoryLabel(t.Color.Category)} {t.Color.Name} " + (t.Level is { } l
            ? $"{l.Level}단계({t.Members}명, {string.Join("/", l.Effects)}{(t.Color.AppliesToSquad ? ", 전원" : ", 해당 카드만")})"
            : $"{t.Members}명(단계 미달)");

    private static string GroupKey(Func<string, string?> option)
    {
        var key = (option("pos") ?? "ST").ToUpperInvariant();
        if (MarketGroups.All.Any(g => g.Key == key)) return key;
        return Formations.GroupOf(Formations.Normalize(key));
    }

    private static async Task<int> Value(SquadService squads, Func<string, string?> option)
    {
        var group = MarketGroups.Get(GroupKey(option));
        var grade = Math.Clamp(Int(option("grade"), 8), 1, Grades.MaxTradable);
        var filter = await Filter(squads, option, group.Key == "GK" ? 140 : 135);
        if (squads.Market.ModelFor(new ValueQuery { Group = group.Key, Grade = grade, Filter = filter }) is not { } model) { Console.Error.WriteLine("이 포지션·강화의 시세 데이터가 부족합니다."); return 3; }
        var picks = squads.Market.FindValue(new ValueQuery { Group = group.Key, Grade = grade, Filter = filter });
        Console.WriteLine($"{group.Name} +{grade} · {picks.Count}장 · OVR {filter.MinOvr}+{(filter.Members is null ? "" : " · 랭커 팀컬러 20")} · R² {model.R2:0.00} (카드 {model.Cards}장)");
        foreach (var p in picks.Take(Int(option("top"), 15)))
        {
            var c = p.Card;
            var core = group.CoreGap(c) is { } g ? $"코어 {g:+0.0;-0.0}" : "";
            Console.WriteLine($"  {c.Name,-10} {c.Season,-8} OVR {c.OvrAt(grade)} {core} 약발{c.WeakFoot} 급여{c.Pay,2}{(c.Stats.TryGetValue("height", out var h) ? $" {h}cm" : "")}"
                + $" · 시세 {Bp.Format(p.Price),7} · 예상 {Bp.Format(p.Expected),7} · {p.Discount * 100:+0;-0}%  " + string.Join(" ", c.Tags.Order().Select(MarketGroups.TagLabel)));
        }
        Console.WriteLine("예상가 = 같은 스펙 카드들의 오늘 시세로 계산한 값 [추정]. 코어 = 포지션 핵심 능력치 가중 평균 − OVR.");
        return 0;
    }

    private static async Task<int> Factors(SquadService squads, Func<string, string?> option)
    {
        var group = MarketGroups.Get(GroupKey(option));
        var grade = Math.Clamp(Int(option("grade"), 8), 1, Grades.MaxTradable);
        // Membership of the rankers' 20 team colours is priced too (members fetched once a week).
        if (option("tc") is not ("no" or "0")) await squads.RankerTeamColorMembersAsync();
        // --minovr 135: only the playable market (OVR at the grade), where the video's rules are about.
        var minOvr = option("minovr") is { } m ? Int(m, 0) : 0;
        var model = minOvr > 0 ? squads.Market.ModelAbove(group.Key, grade, minOvr) : squads.Market.Model(group.Key, grade);
        if (model is null) { Console.Error.WriteLine("이 포지션·강화의 시세 데이터가 부족합니다."); return 3; }
        Console.WriteLine($"{group.Name} +{grade}{(minOvr > 0 ? $" · OVR {minOvr}+" : "")} · 카드 {model.Cards}장 (호날두·호나우두·굴리트 제외) · 중간 가격 {Bp.Format(model.MedianPrice)} · R² {model.R2:0.00}");
        var factors = model.Factors();
        void Print(string title, IEnumerable<PriceFactor> list)
        {
            Console.WriteLine($"\n[{title}]");
            foreach (var f in list)
                Console.WriteLine($"  {f.Name,-22} {f.Percent,6:+0.0;-0.0}%  ({f.Low:+0;-0}~{f.High:+0;-0}%)  OVR {f.OvrEquivalent,5:+0.0;-0.0}  "
                    + $"{(f.BpAtMedian >= 0 ? "+" : "-")}{Bp.Format(Math.Abs(f.BpAtMedian)),7}{(f.Cards is { } n ? $"  {n}장" : "")}{(f.Clear ? "" : "  (불확실)")}{(f.IsInflating ? "  뻥스탯" : "")}");
        }
        Print("핵심 신특", factors.Where(f => f.Kind == FactorKind.Trait && f.IsCore));
        Print("코어 능력치 (같은 OVR에서 +1)", factors.Where(f => f.Kind == FactorKind.Stat && f.IsCore || f.Kind == FactorKind.Height).OrderByDescending(f => f.Percent));
        Print("그 밖의 능력치", factors.Where(f => f.Kind == FactorKind.Stat && !f.IsCore).OrderByDescending(f => f.Percent));
        Print("그 밖의 특성·개인기·체형·약발·팀컬러", factors.Where(f => f.Kind is FactorKind.Trait or FactorKind.Skill or FactorKind.Body or FactorKind.Foot or FactorKind.TeamColor && !(f.Kind == FactorKind.Trait && f.IsCore)).OrderByDescending(f => f.Percent));
        Console.WriteLine("\n%·OVR·BP는 다른 조건이 같을 때의 시세 차이 [추정: 시장 회귀, 인과 아님]. 범위가 0을 포함하면 불확실.");
        return 0;
    }

    private static int Grade(SquadService squads, Func<string, string?> option)
    {
        var card = option("spid") is { } id ? squads.Card(long.Parse(id, CultureInfo.InvariantCulture))
            : squads.Pool().Where(c => option("name") is { } n && c.Name.Contains(n)).OrderByDescending(c => c.Ovr1).FirstOrDefault();
        if (card is null) { Console.Error.WriteLine("선수를 찾지 못했습니다 (--name 또는 --spid)."); return 3; }
        var pos = option("pos") ?? card.Positions.MaxBy(kv => kv.Value).Key;
        var a = squads.Grade(card.SpId, pos, option("from") is { } f ? Int(f, 1) : null, option("to") is { } t ? Int(t, 1) : null)!;
        Console.WriteLine($"{card.Name} ({card.Season}) · {pos}");
        foreach (var s in a.Steps)
            Console.WriteLine($"  +{s.Grade,-2} OVR {s.Ovr} · {Bp.Format(s.Price),8}" + (s.CostPerOvrFromPrevious is { } c ? $" · OVR 1당 {Bp.Format(c),7}" : "".PadRight(15))
                + (s.Alternative is { } alt ? $" · 같은 OVR 최저: {alt.Name} {alt.Season} +{s.AlternativeGrade} {Bp.Format(s.AlternativePrice!.Value)} ({s.PremiumOverAlternative:+0%;-0%})" : ""));
        Console.WriteLine(a.CompetitiveUpTo is { } up
            ? $"  → +{up}까지는 같은 OVR 대안과 비슷한 값. 그 위로는 다른 카드가 더 싸게 같은 OVR을 줍니다."
            : "  → 모든 단계에서 같은 OVR 대안보다 20% 넘게 비쌉니다: OVR 말고 특성·팀컬러·체감에 값을 치르는 카드입니다.");
        if (a.UpgradeCost is { } cost)
            Console.WriteLine($"  +{a.FromGrade} → +{a.ToGrade}: 시세 차이 {Bp.Format(cost)}"
                + (a.Alternative is { } alt ? $" · 같은 OVR 이상 다른 카드: {alt.Name} {alt.Season} +{a.AlternativeGrade} {Bp.Format(a.AlternativePrice!.Value)}" : ""));
        return 0;
    }

    private static async Task<int> TeamColor(SquadService squads, Func<string, string?> option)
    {
        if (option("id") is { } id)
        {
            if (await squads.TeamColorAsync(Int(id, 0)) is not { } tc) { Console.Error.WriteLine("팀컬러를 찾지 못했습니다."); return 3; }
            Console.WriteLine($"{tc.Color.Name} ({FcHelper.Market.TeamColor.CategoryLabel(tc.Color.Category)} · {(tc.Color.AppliesToSquad ? "보너스는 선발 전원" : "보너스는 해당 카드만")}) · 적용 선수 {tc.Members.Count}장");
            foreach (var l in tc.Color.Levels) Console.WriteLine($"  {l.Level}단계 {l.Members}명: {string.Join(", ", l.Effects)}");
            if (tc.Color.Category == TeamColorCategory.Affiliation)
            {
                var related = await squads.RelatedFeatureColorsAsync(tc.Color.Id);
                Console.WriteLine($"관련 특성 팀컬러 {related.Count}개: " + string.Join(", ", related.Select(r => $"{r.Color.Name}({r.Color.Id}, 멤버 {r.Overlap:P0} 겹침)")));
            }
            return 0;
        }
        var popular = await squads.PopularTeamColorsAsync();
        foreach (var (color, usage) in popular) Console.WriteLine($"  {color.Id,6} {color.Name,-14} 랭커 {usage.Users}명 ({usage.Share:P1}) · 최대 {color.MaxMembers}명");
        if (popular.Count == 0)
            foreach (var t in (await squads.TeamColorsAsync()).Where(t => t.Kind == TeamColorKind.Club).Take(20)) Console.WriteLine($"  {t.Id,6} {t.Name}");
        Console.WriteLine("상세와 적용 선수: fch teamcolor --id <번호>. 스쿼드에 적용: fch squad --teamcolor <소속번호>,<특성번호>");
        return 0;
    }

    private static async Task<int> Upgrade(SquadService squads, Func<string, string?> option, string dbPath)
    {
        var db = new FcDatabase(dbPath);
        var me = option("me") is { } nick ? db.FindUserByNickname(nick) : null;
        if (me is null) { Console.Error.WriteLine("--me <내 닉네임>이 필요합니다 (먼저 fch sync --me 로 내 경기를 받아 두세요)."); return 2; }
        var owned = SquadContext.MyCurrentCards(db, me.Ouid);
        if (owned.Count == 0) { Console.Error.WriteLine("캐시에 내 공식경기가 없습니다."); return 3; }
        var current = squads.CurrentSquad(owned);
        Console.WriteLine($"지금 스쿼드 (최근 공식경기): 시세 합 {Bp.Format(current.Sum(s => s.Price))} · 평균 OVR {current.Average(s => s.Ovr):0.0} · 환산 {current.Average(s => s.EffectiveOvr):0.0}");
        foreach (var s in current) Console.WriteLine($"  {s.Position,-4} {s.Card.Name,-10} {s.Card.Season,-8} +{s.Grade,-2} OVR {s.Ovr} · {Bp.Format(s.Price)}");
        var teamColors = (option("teamcolor") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(id => Int(id, 0)).ToList();
        if (option("teamcolor") is null)
        {
            var detected = await squads.DetectTeamColorsAsync(owned);
            if (detected.Count > 0)
            {
                Console.WriteLine($"팀컬러 감지: {string.Join(", ", detected.Select(d => $"{FcHelper.Market.TeamColor.CategoryLabel(d.Color.Category)} {d.Color.Name}({d.Color.Id}) {d.Owned}명 {d.Level.Level}단계 {string.Join("/", d.Level.Effects)}"))}");
                teamColors = SquadService.ActiveTeamColors(detected).ToList();
                var active = detected.Where(d => teamColors.Contains(d.Color.Id)).Select(d => d.Color.Name);
                Console.WriteLine($"→ 게임은 종류별로 하나만 적용하므로 {string.Join(", ", active)} 기준으로, 이 단계를 지키는 교체만 봅니다 (바꾸려면 --teamcolor <번호>, 끄려면 0).");
            }
        }
        // Official fee rule: --pcroom, --topclass, --coupon <%>, --coupon-max <최대 할인 BP>.
        var fee = new SaleFee(Flag(option("pcroom")), Flag(option("topclass")), Int(option("coupon"), 0), Price(option("coupon-max"), 0));
        Console.WriteLine($"판매 수수료 {fee.Rate:P0} (기본 40%{(fee.BenefitPercent > 0 ? $", 혜택 -{fee.BenefitPercent}%" : "")}{(fee.CouponPercent > 0 ? $", 쿠폰 -{fee.CouponPercent}%" : "")})");
        // By default look at the grades the user already plays with.
        var grades = option("grades") is { } gs ? gs.Split(',').Select(g => Int(g, 8)).ToList() : owned.Select(o => o.Grade).Distinct().Order().ToList();
        var plans = await squads.UpgradesAsync(owned, Price(option("budget"), 500_000_000), grades, fee, teamColorIds: teamColors, checkLiquidity: option("liq") is not ("no" or "0"));
        Console.WriteLine($"\n예산 {Bp.Format(Price(option("budget"), 500_000_000))} 안에서 효과 큰 교체:");
        foreach (var p in plans)
            Console.WriteLine("  " + string.Join(" + ", p.Moves.Select(m => $"{m.Out.Card.Name}→{m.In.Name} {m.In.Season} +{m.Grade} (OVR {m.Ovr}, +{m.EffectiveGain:0.0})"))
                + $" · 순비용 {Bp.Format(Math.Max(0, p.NetCost))} · 환산 +{p.TotalGain:0.0}");
        return 0;
    }

    private static async Task<int> Tailor(SquadService squads, List<string> positional, Func<string, string?> option, FcOnlineApi? api, string dbPath)
    {
        if (api is null || positional.Count == 0) { Console.Error.WriteLine("fch tailor <상대 닉네임> (API 키 필요)"); return 2; }
        var db = new FcDatabase(dbPath);
        var service = new FcHelperService(api, db, new FcHelperOptions());
        var report = await service.LookupAsync(positional[0]);
        if (report is null) { Console.Error.WriteLine("닉네임을 찾지 못했습니다."); return 3; }
        var needs = SquadContext.NeedsAgainst(report.Analysis);
        var formation = SquadContext.OpponentFormation(db, report.Ouid);
        Console.WriteLine($"{report.Nickname}: {report.OneLine}");
        Console.WriteLine($"추정 포메이션: {formation ?? "알 수 없음"} [추정]");
        if (formation is not null && await squads.FormationAdviceAsync(formation) is { Best.Count: > 0 } adv)
            Console.WriteLine("  랭커 전적상 유리한 포메이션: " + string.Join(", ", adv.Best.Select(m => $"{m.Formation} ({m.WinRate:P0}, {m.Games}경기)")));
        if (needs.Count == 0) { Console.WriteLine("뚜렷한 맞춤 포인트가 없습니다."); return 0; }
        foreach (var g in squads.Tailored(needs, Int(option("grade"), 8), Price(option("max"), long.MaxValue), 4).GroupBy(t => t.Need))
        {
            Console.WriteLine($"\n{g.First().Reason}");
            foreach (var t in g) Console.WriteLine($"  {t.Position,-4} {t.Card.Name,-10} {t.Card.Season,-8} +{t.Grade} OVR {t.Ovr} · {Bp.Format(t.Price)} · {string.Join(" ", t.Card.Tags.Select(MarketGroups.TagLabel))}");
        }
        return 0;
    }

    private static (int, int) Range(string? text)
    {
        var parts = (text ?? "1-10000").Split('-');
        return (Int(parts[0], 1), Int(parts.Length > 1 ? parts[1] : "10000", 10000));
    }

    private static int Int(string? s, int fallback) => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;
    private static long Price(string? s, long fallback) => s is not null && Bp.TryParse(s, out var v) ? v : fallback;
    /// <summary>
    /// Detailed search options shared by picks and value: --min/--max price, --minovr/--maxovr, --foot, --trait a,b,
    /// --body thin|normal|heavy, --height 183-192, --maxpay, --stat 속력=130,밸런스=120, --name, --tc-only yes (only
    /// cards of the 20 team colours rankers use most).
    /// </summary>
    internal static async Task<CardFilter> Filter(SquadService? squads, Func<string, string?> option, int defaultMinOvr)
    {
        var height = (option("height") ?? "").Split('-', StringSplitOptions.RemoveEmptyEntries);
        var stats = new Dictionary<string, int>();
        foreach (var part in (option("stat") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split('=');
            var key = MarketGroups.StatNames.FirstOrDefault(s => s.Value == kv[0].Trim() || s.Key == kv[0].Trim()).Key;
            if (key is not null && kv.Length == 2) stats[key] = Int(kv[1], 0);
        }
        return new CardFilter
        {
            MinPrice = Price(option("min"), 0), MaxPrice = Price(option("max"), long.MaxValue),
            MinOvr = option("minovr") is { } lo ? Int(lo, 0) : defaultMinOvr > 0 ? defaultMinOvr : null,
            MaxOvr = option("maxovr") is { } hi ? Int(hi, 999) : null,
            MinWeakFoot = Int(option("foot"), 0),
            Traits = (option("trait") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            Body = option("body"),
            MinHeight = height.Length > 0 ? Int(height[0], 0) : null,
            MaxHeight = height.Length > 1 ? Int(height[1], 999) : null,
            MaxPay = option("maxpay") is { } p ? Int(p, 99) : null,
            MinStats = stats,
            MinCoreGap = option("core") is { } core && double.TryParse(core, NumberStyles.Float, CultureInfo.InvariantCulture, out var gap) ? gap : null,
            Name = option("name"),
            Members = squads is not null && Flag(option("tc-only")) ? await squads.RankerTeamColorMembersAsync() : null,
        };
    }

    private static bool Flag(string? s) => s is "yes" or "y" or "1" or "true" or "on";
}
