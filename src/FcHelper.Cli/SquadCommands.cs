using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using FcHelper.Data;
using FcHelper.Market;
using FcHelper.NexonApi;
using FcHelper.Services;

/// <summary>Squad and market commands: they read the market data the app keeps fresh; only ranker stats and opponent lookups use the API key.</summary>
internal static class SquadCommands
{
    public static readonly string[] Names = ["squad", "picks", "grade", "salary", "movers", "formation", "teamcolor", "upgrade", "tailor", "value", "factors", "traits", "valuation-audit", "market-validate", "market-refresh", "market-backfill", "allocation", "mteam", "mhoney"];

    public static async Task<int> RunAsync(string command, List<string> positional, Func<string, string?> option, string? apiKey)
    {
        var dbPath = option("db") ?? AppPaths.DatabasePath;
        var http = new HttpClient();
        var dataCenter = new RateLimiter(0.5);
        var store = new MarketStore(dbPath);
        var lists = new DataCenterListClient(http, dataCenter);
        var market = new MarketService(lists, store, _ => Task.FromResult("[]"));
        if (Flag(option("offline"))) market.SetValueMemberships(CachedMemberships(store));
        FcOnlineApi? api = string.IsNullOrWhiteSpace(apiKey) ? null : new FcOnlineApi(http, apiKey, new RateLimiter(5));
        var squads = new SquadService(market, store, new DataCenterChartClient(http, dataCenter),
            new TeamColorCache(store, new DataCenterTeamColorClient(http, dataCenter, lists)), api,
            salaryCap: new SalaryCapCache(store, new SalaryCapSource(http, dataCenter)),
            rankerSquads: new RankerSquadClient(http, dataCenter, () => api),
            liquidity: new LiquidityCache(store, new PriceHistoryClient(http, dataCenter)),
            managerRankers: new RankerSquadClient(http, dataCenter, () => api, "manager", 52),
            abilities: new AbilityCache(store, new AbilityClient(http, dataCenter)),
            faces: new FaceClient(http, dataCenter),
            playerSearch: new PlayerSearchClient(http, dataCenter));
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
                case "market-validate": return await ValidateMarket(store, option);
                case "squad": return await Squad(squads, option, rankFrom, rankTo);
                case "value": return await Value(squads, option);
                case "mhoney":
                    var honey = await squads.ManagerHoneyAsync(option("pos") ?? "ST", Int(option("grade"), MarketDefaults.Grade), Price(option("min"), 0), Price(option("max"), long.MaxValue),
                        Int(option("minovr"), MarketDefaults.MinOvr(Int(option("grade"), MarketDefaults.Grade))), Int(option("matches"), 20));
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
                    if (Flag(option("refresh")))
                    {
                        var fresh = await squads.RankerSquadsAsync(refresh: true, progress: new Progress<string>(m => Console.Error.Write($"\r{m}          ")));
                        Console.Error.WriteLine();
                        Console.WriteLine($"랭커 표본 새로 수집: {fresh.Count}팀 · {store.GetValue("ranker.squads")?.UpdatedAt.ToLocalTime():yyyy-MM-dd HH:mm} 기준");
                    }
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
                case "market-backfill":
                    return await Backfill(store, new PriceHistoryClient(http, dataCenter), option);
                case "market-refresh":
                    // Two refreshers would write the same unfinished snapshot; the app's own refresh runs while it is open.
                    if (System.Diagnostics.Process.GetProcessesByName("FcHelper").Length > 0 && option("force") != "yes")
                    {
                        Console.Error.WriteLine("FC Online Helper 앱이 켜져 있습니다. 앱을 트레이에서 종료한 뒤 다시 실행하세요. (무시하려면 --force yes)");
                        return 4;
                    }
                    var before = market.Status;
                    Console.WriteLine($"현재 시세: {before.Current?.FinishedAt?.ToLocalTime():yyyy-MM-dd HH:mm} 기준 · {before.Cards:N0}장"
                        + (store.Unfinished() is { } open ? $" · 끝나지 않은 {open.Kind} 갱신 이어받음 (시작 {open.StartedAt.ToLocalTime():MM-dd HH:mm})" : ""));
                    var lastPrinted = DateTime.MinValue;
                    market.Changed += () =>
                    {
                        if (market.Status.Running is not { } run || DateTime.UtcNow - lastPrinted < TimeSpan.FromSeconds(2)) return;
                        lastPrinted = DateTime.UtcNow;
                        Console.Error.Write($"\r갱신 중 {run.Done}/{run.Total} ({(run.Total > 0 ? run.Done * 100 / run.Total : 0)}%) {run.Step}          ");
                    };
                    var refreshed = await market.RefreshIfDueAsync(force: true);
                    Console.Error.WriteLine();
                    var after = market.Status;
                    if (!refreshed)
                    {
                        Console.Error.WriteLine(after.LastError ?? "갱신하지 못했습니다.");
                        return 5;
                    }
                    Console.WriteLine($"완료: {after.Current?.FinishedAt?.ToLocalTime():yyyy-MM-dd HH:mm} 기준 · {after.Cards:N0}장 · 일별 시세 {store.PriceHistoryDays().Count}일");
                    return 0;
                case "valuation-audit":
                    if (!Flag(option("offline"))) await squads.RankerTeamColorMembersAsync();
                    var auditGrade = Math.Clamp(Int(option("grade"), MarketDefaults.Grade), 1, Grades.MaxTradable);
                    int? auditFloor = option("minovr") is { } af ? Int(af, MarketDefaults.MinOvr(auditGrade)) : null;
                    var report = new
                    {
                        Snapshot = store.LatestFinished(),
                        Snapshots = store.Snapshots().Select(s => new { Snapshot = s, Cards = store.CountCards(s.Id) }),
                        HistoryDays = store.PriceHistoryDays(), PatchBaselines = store.PatchBaselines(), Grade = auditGrade, MinOvr = auditFloor,
                        PatchChanges = store.PatchBaselines().Where(p => p.Snapshot is not null).Select(p => new { Baseline = p,
                            After = store.LatestFinished(), Values = PatchValueModel.Compare(store.LoadCards(p.Snapshot!.Value), store.LoadCards(store.LatestFinished()!.Id), auditGrade) }),
                        Models = MarketGroups.All.Select(g => auditFloor is { } lo ? market.ModelAbove(g.Key, auditGrade, lo) : market.Model(g.Key, auditGrade))
                            .Where(m => m is not null).Select(m => new { m!.Group, m.Cards, m.Players, m.R2, m.ValidationError, m.ValidationLogScale, m.MedianPrice, Factors = m.Factors() }),
                        Traits = market.TraitValues(auditGrade, auditFloor).Select(t => new { t.Trait, t.Scope, t.Percent, t.Low, t.High, t.OvrEquivalent, t.Cards, t.Players, t.Estimated, t.Clear,
                            ByGroup = t.ByGroup.Select(g => new { g.Group, g.Factor }) }),
                    };
                    var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true, NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals });
                    if (option("out") is { } output) await File.WriteAllTextAsync(output, json);
                    else Console.WriteLine(json);
                    return 0;
                case "traits":
                    if (!Flag(option("offline"))) await squads.RankerTeamColorMembersAsync();
                    var tgrade = Math.Clamp(Int(option("grade"), MarketDefaults.Grade), 1, Grades.MaxTradable);
                    foreach (var minOvr in new int?[] { null, option("minovr") is { } mo ? Int(mo, MarketDefaults.MinOvr(tgrade)) : MarketDefaults.MinOvr(tgrade) })
                    {
                        Console.WriteLine($"\n■ 신특 평균 값어치 · +{tgrade} · {(minOvr is { } m ? $"OVR {m}+ 카드만" : "전체 카드")} [추정]");
                        foreach (var t in squads.Market.TraitValues(tgrade, minOvr))
                            Console.WriteLine($"  {t.Scope,-22} {t.Trait,-10} " + (t.Estimated ? $"{t.Percent:+0;-0}% ({t.Low:+0;-0}~{t.High:+0;-0}%) OVR {t.OvrEquivalent:+0.0;-0.0}" : "표본 부족")
                                + $" · {t.Cards}장 · 선수 {t.Players}명{(t.Clear ? "" : " (불확실)")}"
                                + "  [" + string.Join(", ", t.ByGroup.Select(g => g.Factor.Estimated ? $"{g.Group} {g.Factor.Percent:+0;-0}%" : $"{g.Group} 표본 부족")) + "]");
                    }
                    return 0;
                case "picks":
                    foreach (var h in (await squads.HiddenRankerPicksAsync(option("pos"), await Filter(squads, option, MarketDefaults.MinOvr(Math.Clamp(Int(option("grade"), MarketDefaults.Grade), 1, Grades.MaxTradable))),
                                 Int(option("users"), 10), rankFrom, rankTo, grade: Math.Clamp(Int(option("grade"), MarketDefaults.Grade), 1, Grades.MaxTradable))).Take(Int(option("top"), 15)))
                        Console.WriteLine($"{h.Position,-4} {h.Card.Name,-10} {h.Card.Season,-8} +{h.Grade,-2} OVR {h.Ovr} · 랭커 {h.Users}명({h.Share:P1}) · 시세 {Bp.Format(h.Price),7} · 예상 {Bp.Format(h.Expected),7} · {h.Discount:+0%;-0%} · 중앙오차 {PriceEvidence.ErrorLabel(h.ModelError)} · {PriceEvidence.Label(h.EvidenceScore)}");
                    Console.WriteLine("랭커 사용 = 데일리 차트(전날 공식경기). 예상가 = 같은 스펙 카드의 오늘 시세 [추정].");
                    return 0;
                case "grade": return Grade(squads, option);
                case "salary":
                    foreach (var s in squads.SalaryEfficiency(option("pos") ?? "ST", Int(option("grade"), MarketDefaults.Grade), Price(option("min"), 0), Price(option("max"), long.MaxValue),
                                 Int(option("minovr"), 0)).Take(Int(option("top"), 15)))
                        Console.WriteLine($"{s.Card.Name,-10} {s.Card.Season,-8} OVR {s.Ovr} (환산 {s.EffectiveOvr:0.0}) · 급여 {s.Pay} · 급여당 {s.OvrPerPay:0.00} · 시세 {Bp.Format(s.Price)}");
                    return 0;
                case "movers":
                    var moves = squads.PriceMoves(Int(option("grade"), MarketDefaults.Grade), Int(option("days"), 7), Price(option("min"), 10_000_000));
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
        catch (NexonApiException e)
        {
            Console.Error.WriteLine(e.IsRateLimited ? "API 조회 한도에 도달했습니다. 요청을 완료하지 못했습니다."
                : e.IsAuthError ? "API 인증을 확인하지 못했습니다. 요청을 완료하지 못했습니다." : $"API 조회 실패: {e.ErrorName}");
            return 5;
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
            Grades = (option("grades") ?? MarketDefaults.Grade.ToString(CultureInfo.InvariantCulture)).Split(',').Select(g => Int(g, MarketDefaults.Grade)).ToList(),
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
            Console.WriteLine($"\n■ {p.Label} · {p.Formation.Name} · 총 {Bp.Format(p.TotalPrice)} · 급여 {p.TotalPay}/{cap} · 평균 인게임 OVR {p.AveragePlayingOvr:0.0} · 환산 {p.AverageEffectiveOvr:0.0} [추정]"
                + string.Concat(p.TeamColors.Select(t => $" · {TeamColorText(t)}")));
            if (p.SelectionNote is { Length: > 0 } note) Console.WriteLine(note);
            foreach (var s in p.Slots)
                Console.WriteLine($"  {s.Position,-4} {s.Card.Name,-10} {s.Card.Season,-8} +{s.Grade,-2} OVR {s.ShownOvr,5:0.#}{(s.Final?.Exact == false ? " [추정]" : "")}{(s.TeamColorBonus > 0 ? "*" : " ")} 환산 {s.EffectiveOvr,5:0.0} · {Bp.Format(s.Price),7} · 급여 {s.Pay,2}"
                    + (s.RankerUsers > 0 ? $" · 랭커 {s.RankerUsers}명" : "") + (s.Discount < -0.15 ? $" · 스펙 대비 {s.Discount:P0}" : ""));
        }
        Console.WriteLine("\n환산 OVR = OVR + 시장이 약발·특성·개인기·능력치에 매기는 값(OVR 단위) [추정]. * = 팀컬러 보너스 포함.");
        Console.WriteLine("인게임 OVR은 강화·적응도 5·팀컬러를 합산한 값이며, 세부 스탯으로 확인하지 못한 값은 [추정]입니다.");
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

    /// <summary>Match the app's saved top affiliation colours without refreshing any network cache.</summary>
    private static IReadOnlyList<MarketMembership> CachedMemberships(MarketStore store)
    {
        var catalog = store.LoadTeamColors();
        var chart = store.LatestChart(1, 10000)?.Chart;
        if (chart is null) return [];
        var colors = chart.TeamColors.Select(u => catalog.FirstOrDefault(c => c.Category == TeamColorCategory.Affiliation
            && (u.Id > 0 ? c.Id == u.Id : c.Name == u.Name))).Where(c => c is not null).Take(SquadService.RankerTeamColors);
        return colors.Select(c => new MarketMembership($"tc:aff:{c!.Id}", $"소속 팀컬러: {c.Name}", FactorKind.TeamColor,
            store.LoadTeamColorMembers(c.Id))).ToList();
    }

    private static async Task<int> ValidateMarket(MarketStore store, Func<string, string?> option)
    {
        var snapshot = store.LatestFinished()!;
        var cards = store.LoadCards(snapshot.Id);
        var memberships = option("tc") is "no" or "0" ? [] : CachedMemberships(store);
        var groups = (option("positions") ?? "ST,CF,W,CAM,CM,CDM,FB,CB,GK").Split(',');
        var grades = (option("grades") ?? "8,10,11").Split(',').Select(g => Math.Clamp(Int(g, 11), 1, Grades.MaxTradable));
        var cuts = (option("cuts") ?? "110,115,120").Split(',').Select(c => Int(c, 115)).ToList();
        var auditHinge = Int(option("hinge"), PriceModel.TraitHinge);
        var reports = new List<object>();
        object Summarize(IEnumerable<PriceValidationRow> input)
        {
            var rows = input.ToList();
            double? Median(IEnumerable<double> inputValues)
            {
                var sorted = inputValues.Order().ToArray();
                return sorted.Length == 0 ? null : (sorted[(sorted.Length - 1) / 2] + sorted[sorted.Length / 2]) / 2;
            }
            return new { Cards = rows.Count, MedianError = Median(rows.Select(r => r.Error)),
                MedianLogActualExpected = Median(rows.Select(r => r.LogResidual)) };
        }
        foreach (var group in groups)
        foreach (var grade in grades)
        foreach (var cut in cuts)
        {
            var data = cards.Where(c => c.Group == group && c.Ovr1 >= cut).ToList();
            var model = PriceModel.Fit(group, grade, data, memberships, traitHinge: auditHinge);
            if (model is null) continue;
            var rows = PriceModel.CrossValidate(group, grade, data, memberships, traitHinge: auditHinge);
            var top = rows.Where(r => r.Card.Ovr1 >= 120 && !r.AtFloor).ToList();
            var focus = new[] { "크로스 포쳐", "라인 브레이커", "트릭스터", "스피드스터" };
            object Split(IEnumerable<PriceValidationRow> input)
            {
                var free = input.Where(r => r.Card.Ovr1 >= 120 && !r.AtFloor).ToList();
                return new { All = Summarize(input),
                    TopWithTrait = Summarize(free.Where(r => r.Card.Tags.Any(t => t.StartsWith("trait:")))),
                    TopWithoutTrait = Summarize(free.Where(r => !r.Card.Tags.Any(t => t.StartsWith("trait:")))) };
            }
            var shapes = new Dictionary<string, object>();
            if (Flag(option("shapes")))
            {
                foreach (var (name, hinge, linear) in new (string, int?, bool)[] {
                    ("constant", null, false), ("linear", null, true), ("hinge115", 115, false),
                    ("hinge118", 118, false), ("hinge120", 120, false), ("hinge122", 122, false) })
                    shapes[name] = Split(PriceModel.CrossValidate(group, grade, data, memberships, hinge, linear));
            }
            var placebo = new Dictionary<string, object>();
            if (option("draws") is { } drawsText)
            {
                var draws = Math.Max(40, Int(drawsText, 60));
                var seed = 20261026 + grade * 1000 + cut * 10 + Array.IndexOf(groups, group);
                var random = new Random(seed);
                var nullT = new Dictionary<string, List<double>>();
                var eligible = data.Where(c => c.IsTraded && c.PriceAt(grade) > Grades.FloorPrice && !MarketGroups.IsPriceOutlier(c)).ToList();
                for (var draw = 0; draw < draws; draw++)
                {
                    var shuffled = new List<MarketCard>();
                    foreach (var season in eligible.GroupBy(c => c.Season))
                    {
                        var source = season.ToArray();
                        var bundles = source.Select(c => c.Tags.Where(t => t.StartsWith("trait:")).ToArray()).ToArray();
                        random.Shuffle(bundles);
                        shuffled.AddRange(source.Select((c, i) => c with { Tags = c.Tags.Where(t => !t.StartsWith("trait:")).Concat(bundles[i]).ToHashSet() }));
                    }
                    var fake = PriceModel.Fit(group, grade, shuffled, memberships, validate: false, traitHinge: auditHinge)!;
                    foreach (var f in fake.Factors().Where(f => f.Kind == FactorKind.Trait && f.Estimated))
                    {
                        var b = Math.Log(1 + f.Percent / 100);
                        var se = (Math.Log(1 + f.High / 100) - Math.Log(1 + f.Low / 100)) / (2 * f.Critical);
                        if (se <= 1e-10 || !double.IsFinite(se)) continue;
                        if (!nullT.TryGetValue(f.Name, out var values)) nullT[f.Name] = values = [];
                        values.Add(Math.Abs(b / se));
                    }
                }
                object NullSummary(IEnumerable<double> values)
                {
                    var v = values.Order().ToArray();
                    var index = .95 * (v.Length - 1);
                    var p95 = v[(int)index] + (v[(int)Math.Ceiling(index)] - v[(int)index]) * (index - (int)index);
                    return new { Draws = draws, Seed = seed, Tests = v.Length, AbsT95 = p95,
                        FalsePositive196 = v.Count(t => t > 1.96) / (double)v.Length, FalsePositive3 = v.Count(t => t > 3) / (double)v.Length };
                }
                foreach (var (trait, values) in nullT) placebo[trait] = NullSummary(values);
                if (nullT.Count > 0) placebo["pooled"] = NullSummary(nullT.Values.SelectMany(v => v));
            }
            reports.Add(new { Group = group, Grade = grade, MinOvr1 = cut, model.Cards, model.Players, model.FloorCards,
                ValidatedCards = rows.Count, CompleteValidation = rows.Count == model.Cards, FoldCount = rows.Select(r => r.Fold).Distinct().Count(),
                SingleHoldoutError = model.ValidationError, All = Summarize(rows),
                Playable = Summarize(rows.Where(r => r.Card.Ovr1 >= MarketDefaults.MinOvr1)),
                TopWithTrait = Summarize(top.Where(r => r.Card.Tags.Any(t => t.StartsWith("trait:")))),
                TopWithoutTrait = Summarize(top.Where(r => !r.Card.Tags.Any(t => t.StartsWith("trait:")))),
                TopByTrait = focus.ToDictionary(t => t, t => Summarize(top.Where(r => r.Card.Tags.Contains($"trait:{t}")))),
                Factors = model.Factors().Where(f => f.Kind == FactorKind.Trait), Shapes = shapes, Placebo = placebo });
            Console.Error.WriteLine($"{group} +{grade} · +1 OVR {cut}+ · {rows.Count}장 5묶음 검증 완료");
        }
        var report = new { Snapshot = snapshot, Memberships = memberships.Select(m => m.Key),
            TraitHinge = auditHinge, PriceModel.TraitTopOvr, PriceModel.TraitCritical, MarketService.PlayableMargin, Models = reports };
        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true, NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals });
        if (option("out") is { } output) await File.WriteAllTextAsync(output, json);
        else Console.WriteLine(json);
        return 0;
    }

    private static async Task<int> Value(SquadService squads, Func<string, string?> option)
    {
        var group = MarketGroups.Get(GroupKey(option));
        var grade = Math.Clamp(Int(option("grade"), MarketDefaults.Grade), 1, Grades.MaxTradable);
        var filter = await Filter(squads, option, MarketDefaults.MinOvr(grade));
        if (squads.Market.ModelFor(new ValueQuery { Group = group.Key, Grade = grade, Filter = filter }) is not { } model) { Console.Error.WriteLine("이 포지션·강화의 시세 데이터가 부족합니다."); return 3; }
        var picks = squads.Market.FindValue(new ValueQuery { Group = group.Key, Grade = grade, Filter = filter });
        Console.WriteLine($"{group.Name} +{grade} · {picks.Count}장 · OVR {filter.MinOvr}+{(filter.Members is null ? "" : " · 랭커 팀컬러 20")} · R² {model.R2:0.00} (카드 {model.Cards}장)");
        foreach (var p in picks.Take(Int(option("top"), 15)))
        {
            var c = p.Card;
            var core = group.CoreGap(c) is { } g ? $"코어 {g:+0.0;-0.0}" : "";
            Console.WriteLine($"  {c.Name,-10} {c.Season,-8} OVR {c.OvrAt(grade)} {core} 약발{c.WeakFoot} 급여{c.Pay,2}{(c.Stats.TryGetValue("height", out var h) ? $" {h}cm" : "")}"
                + $" · 시세 {Bp.Format(p.Price),7} · 예상 {Bp.Format(p.Expected),7} · {p.Discount * 100:+0;-0}% · 중앙오차 {PriceEvidence.ErrorLabel(p.ModelError)} · {PriceEvidence.Label(p.EvidenceScore)}  " + string.Join(" ", c.Tags.Order().Select(MarketGroups.TagLabel)));
        }
        Console.WriteLine("예상가 = 같은 스펙 카드들의 오늘 시세로 계산한 값 [추정]. 코어 = 포지션 핵심 능력치 가중 평균 − OVR.");
        return 0;
    }

    private static async Task<int> Factors(SquadService squads, Func<string, string?> option)
    {
        var group = MarketGroups.Get(GroupKey(option));
        var grade = Math.Clamp(Int(option("grade"), MarketDefaults.Grade), 1, Grades.MaxTradable);
        // Membership of the rankers' 20 team colours is priced too (members fetched once a week).
        if (!Flag(option("offline")) && option("tc") is not ("no" or "0")) await squads.RankerTeamColorMembersAsync();
        // --minovr is the training OVR at this grade; default matches the value finder's playable model.
        var minOvr = option("minovr") is { } m ? Int(m, 0) : MarketDefaults.MinOvr(grade) - MarketService.PlayableMargin;
        var model = minOvr > 0 ? squads.Market.ModelAbove(group.Key, grade, minOvr) : squads.Market.Model(group.Key, grade);
        if (model is null) { Console.Error.WriteLine("이 포지션·강화의 시세 데이터가 부족합니다."); return 3; }
        Console.WriteLine($"{group.Name} +{grade}{(minOvr > 0 ? $" · OVR {minOvr}+" : "")} · 카드 {model.Cards}장 · 선수 {model.Players}명 (호날두·호나우두·굴리트 제외) · 중간 가격 {Bp.Format(model.MedianPrice)} · R² {model.R2:0.00}"
            + $" · 검증 중앙오차 {(double.IsNaN(model.ValidationError) ? "표본 부족" : model.ValidationError.ToString("P0"))}");
        var factors = model.Factors();
        void Print(string title, IEnumerable<PriceFactor> list)
        {
            Console.WriteLine($"\n[{title}]");
            foreach (var f in list)
                Console.WriteLine($"  {f.Name,-22} " + (f.Estimated ? $"{f.Percent:+0.0;-0.0}% ({f.Low:+0;-0}~{f.High:+0;-0}%) OVR {f.OvrEquivalent:+0.0;-0.0}" : "표본 부족")
                    + $"{(double.IsFinite(f.PerOvr) ? $" · OVR {PriceModel.TraitTopOvr} 카드 기준 (OVR {PriceModel.TraitHinge} 이하 {f.PercentBelowHinge:+0;-0}%, 그 위 +1마다 {f.PerOvr * 100:+0;-0}%p)" : "")}{(f.Comparisons is { } cmp ? $" · 같은 선수 비교 {cmp}명" : "")}"
                    + $"{(f.Cards is { } n ? $"  {n}장" : "")} · 선수 {f.Players}명{(f.Clear ? "" : "  (불확실)")}{(f.IsInflating ? "  뻥스탯" : "")}");
        }
        Print("핵심 신특", factors.Where(f => f.Kind == FactorKind.Trait && f.IsCore));
        Print("코어 능력치·피지컬", factors.Where(f => (f.Kind == FactorKind.Stat && f.IsCore) || f.Kind is FactorKind.Height or FactorKind.Weight).OrderByDescending(f => f.Percent));
        Print("그 밖의 능력치", factors.Where(f => f.Kind == FactorKind.Stat && !f.IsCore).OrderByDescending(f => f.Percent));
        Print("그 밖의 특성·개인기·체형·약발·팀컬러", factors.Where(f => f.Kind is FactorKind.Trait or FactorKind.Skill or FactorKind.Body or FactorKind.Foot or FactorKind.TeamColor or FactorKind.FeatureTeamColor
            && !(f.Kind == FactorKind.Trait && f.IsCore)).OrderByDescending(f => f.Percent));
        Console.WriteLine("\n%·OVR은 다른 조건을 통제한 시세 연관성 [추정, 인과 아님]. 스탯은 같은 OVR에서 능력치 배분을 바꾼 효과. 범위는 선수 군집·t 보정한 근사치이며 릿지 편향은 포함하지 않습니다.");
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
        foreach (var g in squads.Tailored(needs, Int(option("grade"), MarketDefaults.Grade), Price(option("max"), long.MaxValue), 4).GroupBy(t => t.Need))
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

    /// <summary>
    /// Daily price graphs from the data center for the cards an event study needs (trait cards, the watched seasons,
    /// then the most rated others), appended line by line to a JSON-lines file so an interrupted run loses nothing;
    /// re-running skips cards already in the file. One request per card through the shared 2-second limiter.
    /// </summary>
    private static async Task<int> Backfill(MarketStore store, PriceHistoryClient graphs, Func<string, string?> option)
    {
        var grade = Math.Clamp(Int(option("grade"), 8), 1, Grades.MaxTradable);
        var max = Math.Max(1, Int(option("max"), 2500));
        var outPath = option("out") ?? "docs/market-value/data/price-graphs.jsonl";
        var watch = (option("watch") ?? "24EP,CU").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var snapshot = store.LatestFinished()!;
        var ordered = store.LoadCards(snapshot.Id).Where(c => c.IsTraded && c.PriceAt(grade) > Grades.FloorPrice)
            .GroupBy(c => c.SpId).Select(g => g.First())
            .OrderByDescending(c => c.Tags.Any(t => t.StartsWith("trait:", StringComparison.Ordinal)))
            .ThenByDescending(c => watch.Any(w => c.Season.Contains(w, StringComparison.Ordinal)))
            .ThenByDescending(c => c.RatingCount)
            .Take(max).ToList();

        var done = new HashSet<long>();
        if (File.Exists(outPath))
        {
            foreach (var line in await File.ReadAllLinesAsync(outPath))
            {
                if (line.Length == 0) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    if (doc.RootElement.TryGetProperty("spId", out var id) && doc.RootElement.GetProperty("grade").GetInt32() == grade) done.Add(id.GetInt64());
                }
                catch (JsonException) { } // a line cut off by an interrupted run
            }
        }
        var todo = ordered.Where(c => !done.Contains(c.SpId)).ToList();
        Console.WriteLine($"시세 그래프 +{grade}: {todo.Count}장 받기 (이미 {done.Count}장) · 2초에 1장이라 약 {todo.Count * 2 / 60}분. 끊겨도 다시 실행하면 이어받습니다.");
        if (Path.GetDirectoryName(Path.GetFullPath(outPath)) is { } dir) Directory.CreateDirectory(dir);

        var fetched = 0;
        var failed = 0;
        await using var w = new StreamWriter(outPath, append: true);
        foreach (var c in todo)
        {
            IReadOnlyList<long> values;
            try { values = await graphs.DailyPricesAsync(c.SpId, grade); }
            catch (HttpRequestException) { failed++; continue; }
            await w.WriteLineAsync(JsonSerializer.Serialize(new
            {
                spId = c.SpId, grade, fetchedAt = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture), values,
            }));
            await w.FlushAsync();
            if (++fetched % 25 == 0) Console.Error.Write($"\r{fetched}/{todo.Count} (실패 {failed})          ");
        }
        Console.Error.WriteLine();
        Console.WriteLine($"{outPath}: 새로 {fetched}장, 실패 {failed}장, 합계 {done.Count + fetched}장");
        return 0;
    }
}
