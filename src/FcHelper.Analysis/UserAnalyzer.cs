using FcHelper.Core;
using FcHelper.Core.Models;

namespace FcHelper.Analysis;

/// <summary>Turns one user's cached matches into the numbers and insights shown on the card. Pure: no I/O.</summary>
public static class UserAnalyzer
{
    /// <summary>
    /// Pseudo-counts pulling small samples toward the baseline (Bayesian shrinkage). With 10, a user needs
    /// roughly 10+ goals before their own rate dominates.
    /// </summary>
    private const double GoalPrior = 10;
    private const double PassPrior = 300;
    private const double MatchPrior = 20;

    /// <summary>Minimum lift over the baseline, and minimum absolute gap, before a rate is called out.</summary>
    private const double MinLift = 1.35;
    private const double MinGap = 0.05;
    private const int MinCount = 3;

    public static UserAnalysis Analyze(
        IReadOnlyList<MatchDetail> matches,
        string ouid,
        Baseline? baseline = null,
        Func<int, string>? playerName = null)
    {
        playerName ??= id => id.ToString();
        var sides = matches
            .OrderByDescending(m => m.MatchDate)
            .Select(m => (Match: m, Me: m.SideOf(ouid), Opp: m.OpponentOf(ouid)))
            .Where(t => t.Me is not null && t.Opp is not null)
            .Select(t => (t.Match, Me: t.Me!, Opp: t.Opp!))
            .ToList();

        var n = sides.Count;
        var metrics = new Dictionary<string, Proportion>();
        foreach (var (_, me, opp) in sides)
        {
            foreach (var (key, p) in SideMetrics.From(me, opp).Rates) metrics[key] = metrics.GetValueOrDefault(key) + p;
        }

        var goals = sides.SelectMany(s => s.Me.ShootDetail.Where(d => d.IsGoal)).ToList();
        var conceded = sides.SelectMany(s => s.Opp.ShootDetail.Where(d => d.IsGoal)).ToList();
        var shots = sides.Sum(s => s.Me.Shoot.ShootTotal);
        var onTarget = sides.Sum(s => s.Me.Shoot.EffectiveShootTotal);
        var passTry = sides.Sum(s => s.Me.Pass.PassTry);
        var tackleTry = sides.Sum(s => s.Me.Defence.TackleTry);

        var record = new RecordSummary(
            n,
            sides.Count(s => s.Me.MatchDetail.Outcome == MatchOutcome.Win),
            sides.Count(s => s.Me.MatchDetail.Outcome == MatchOutcome.Draw),
            sides.Count(s => s.Me.MatchDetail.Outcome == MatchOutcome.Loss),
            sides.Count(s => s.Me.MatchDetail.MatchEndType == 2));

        var players = PlayerThreats(sides.Select(s => s.Me).ToList(), goals);
        var compared = baseline?.IsUsable == true;
        var controller = sides.Where(s => s.Me.MatchDetail.Controller.Length > 0)
            .GroupBy(s => s.Me.MatchDetail.Controller)
            .OrderByDescending(g => g.Count())
            .Select(g => new Share(g.Key, g.Count(), n))
            .FirstOrDefault();

        var analysis = new UserAnalysis
        {
            Ouid = ouid,
            Record = record,
            AvgGoalsFor = Avg(sides, s => GoalsShown(s.Me)),
            AvgGoalsAgainst = Avg(sides, s => GoalsShown(s.Opp)),
            AvgPossession = Avg(sides, s => s.Me.MatchDetail.Possession),
            AvgShots = Avg(sides, s => s.Me.Shoot.ShootTotal),
            AvgShotsOnTarget = Avg(sides, s => s.Me.Shoot.EffectiveShootTotal),
            ShotAccuracy = Ratio(onTarget, shots),
            Conversion = Ratio(goals.Count, shots),
            PassSuccess = Ratio(sides.Sum(s => s.Me.Pass.PassSuccess), passTry),
            AvgTackleTry = Avg(sides, s => s.Me.Defence.TackleTry),
            TackleSuccess = Ratio(sides.Sum(s => s.Me.Defence.TackleSuccess), tackleTry),
            AvgIntercept = Avg(sides, s => s.Me.Player.Sum(p => p.Status.Intercept)),
            AvgPause = Avg(sides, s => s.Me.MatchDetail.SystemPause),
            Controller = controller,
            GoalCount = goals.Count,
            ConcededCount = conceded.Count,
            GoalTypes = Shares(goals, g => ShotTypes.Label(g.Type)),
            GoalZones = Shares(goals, g => Pitch.Label(Pitch.ZoneOf(g.X, g.Y, g.InPenalty))),
            ConcededTypes = Shares(conceded, g => ShotTypes.Label(g.Type)),
            ConcededZones = Shares(conceded, g => Pitch.Label(Pitch.ZoneOf(g.X, g.Y, g.InPenalty))),
            PassMix = PassMix(sides.Select(s => s.Me.Pass).ToList()),
            Players = players,
            Combos = goals.Where(g => g.AssistSpId is not null)
                .GroupBy(g => (Assist: g.AssistSpId!.Value, g.SpId))
                .Select(g => new GoalCombo(g.Key.Assist, g.Key.SpId, g.Count()))
                .OrderByDescending(c => c.Count).Take(5).ToList(),
            Signature = Signature(goals),
            FirstGoal = FirstGoal(sides.Select(s => (s.Me, s.Opp)).ToList()),
            ComparedToBaseline = compared,
            Rates = metrics,
        };

        var insights = new List<Insight>();
        insights.AddRange(RateInsights(metrics, compared ? baseline : null));
        insights.AddRange(PlayerInsights(players, goals.Count, playerName));
        insights.AddRange(TraitInsights(analysis, compared ? baseline : null));
        return analysis with { Insights = insights.OrderByDescending(i => i.Score).ToList() };
    }

    // ── rate comparisons ───────────────────────────────────────────────────

    private sealed record RateRule(string Key, InsightKind Kind, Evidence Evidence, double Prior, double AbsoluteThreshold, Func<string> Text);

    private static IEnumerable<RateRule> RateRules()
    {
        // Normal shots are the default finish, so a high share says nothing on its own; compare only with a baseline.
        for (var t = 1; t <= 12; t++)
        {
            var type = t;
            yield return new(SideMetrics.GoalType + type, InsightKind.Threat, Evidence.Computed, GoalPrior,
                type == ShotTypes.Normal ? double.PositiveInfinity : 0.3, () => $"{ShotTypes.Label(type)} 득점");
            yield return new(SideMetrics.ConcededType + type, InsightKind.Weakness, Evidence.Computed, GoalPrior,
                type == ShotTypes.Normal ? double.PositiveInfinity : 0.3, () => $"{ShotTypes.Label(type)} 실점");
        }
        foreach (var zone in Enum.GetValues<GoalZone>())
        {
            // Most goals come from the centre of the box anyway; those zones are only notable relative to a baseline.
            var absolute = zone is GoalZone.BoxCenter or GoalZone.SixYardBox ? double.PositiveInfinity : 0.4;
            yield return new(SideMetrics.GoalZone + zone, InsightKind.Threat, Evidence.Computed, GoalPrior, absolute, () => $"{Pitch.Label(zone)} 득점");
            yield return new(SideMetrics.ConcededZone + zone, InsightKind.Weakness, Evidence.Computed, GoalPrior, absolute, () => $"{Pitch.Label(zone)} 실점");
        }
        yield return new(SideMetrics.GoalLongRange, InsightKind.Threat, Evidence.Computed, GoalPrior, 0.3, () => "중거리 득점");
        yield return new(SideMetrics.ConcededLongRange, InsightKind.Weakness, Evidence.Computed, GoalPrior, 0.3, () => "중거리 실점");
        yield return new(SideMetrics.GoalLate, InsightKind.Threat, Evidence.Computed, GoalPrior, 0.35, () => $"{SideMetrics.LateMinute}분 이후 득점");
        yield return new(SideMetrics.ConcededLate, InsightKind.Weakness, Evidence.Computed, GoalPrior, 0.35, () => $"{SideMetrics.LateMinute}분 이후 실점");
        yield return new(SideMetrics.RouteCutback, InsightKind.Threat, Evidence.Inferred, GoalPrior, 0.3, () => "컷백형 도움골 (엔드라인 부근 → 박스 중앙)");
        yield return new(SideMetrics.RouteCross, InsightKind.Threat, Evidence.Inferred, GoalPrior, 0.3, () => "크로스 → 헤더 도움골");
        yield return new(SideMetrics.RouteThrough, InsightKind.Threat, Evidence.Inferred, GoalPrior, 0.35, () => "중앙 스루 침투 도움골");
        yield return new(SideMetrics.PassThrough, InsightKind.Trait, Evidence.Computed, PassPrior, double.PositiveInfinity, () => "스루패스 비중");
        yield return new(SideMetrics.PassLobbedThrough, InsightKind.Trait, Evidence.Computed, PassPrior, double.PositiveInfinity, () => "로빙 스루패스 비중");
        yield return new(SideMetrics.PassLong, InsightKind.Trait, Evidence.Computed, PassPrior, double.PositiveInfinity, () => "롱패스 비중");
        yield return new(SideMetrics.Forfeit, InsightKind.Trait, Evidence.Direct, MatchPrior, 0.1, () => "몰수패(탈주)");
    }

    private static IEnumerable<Insight> RateInsights(Dictionary<string, Proportion> metrics, Baseline? baseline)
    {
        foreach (var rule in RateRules())
        {
            if (!metrics.TryGetValue(rule.Key, out var p) || p.Total == 0) continue;
            var minCount = rule.Key == SideMetrics.Forfeit ? 2 : MinCount;
            if (p.Count < minCount) continue;

            var p0 = baseline?.RateOf(rule.Key);
            if (p0 is > 0)
            {
                var shrunk = (p.Count + rule.Prior * p0.Value) / (p.Total + rule.Prior);
                var lift = shrunk / p0.Value;
                if (lift < MinLift || shrunk - p0.Value < MinGap) continue;
                yield return new Insight(rule.Key, rule.Kind, $"{rule.Text()} {Pct(p.Value)} (평균 {Pct(p0.Value)})",
                    rule.Evidence, p.Total, (lift - 1) * Math.Sqrt(p.Count))
                { Rate = shrunk, BaselineRate = p0, Short = rule.Text() };
            }
            else if (p.Value >= rule.AbsoluteThreshold)
            {
                // No usable baseline: absolute threshold only, and the text makes no comparison claim.
                yield return new Insight(rule.Key, rule.Kind, $"{rule.Text()} {Pct(p.Value)}", rule.Evidence, p.Total,
                    p.Value * Math.Sqrt(p.Count)) { Rate = p.Value, Short = rule.Text() };
            }
        }
    }

    private static IEnumerable<Insight> PlayerInsights(IReadOnlyList<PlayerThreat> players, int totalGoals, Func<int, string> name)
    {
        var top = players.FirstOrDefault();
        if (top is null || top.Goals < 4 || top.GoalShare < 0.35) yield break;
        yield return new Insight("player.dependency", InsightKind.Threat,
            $"{name(top.SpId)} 득점 의존 {Pct(top.GoalShare)} ({top.Goals}/{totalGoals}골)",
            Evidence.Computed, totalGoals, top.GoalShare * Math.Sqrt(top.Goals) * 1.2) { Short = $"{name(top.SpId)} 의존" };
    }

    private static IEnumerable<Insight> TraitInsights(UserAnalysis a, Baseline? baseline)
    {
        if (a.Controller is { } c)
        {
            var label = c.Label switch { "keyboard" => "키보드", "pad" => "패드", var other => other };
            var text = c.Ratio >= 0.8 ? $"{label} 유저" : $"{label} {Pct(c.Ratio)} (혼용)";
            yield return new Insight("controller", InsightKind.Trait, text, Evidence.Direct, c.Total, 0.1);
        }

        if (a.Record.Matches >= 5)
        {
            var avgPossession = baseline?.AvgPossession is > 0 ? baseline.AvgPossession : 50;
            if (a.AvgPossession >= avgPossession + 5)
                yield return new Insight("style.possession", InsightKind.Trait, $"점유형 추정 (평균 점유율 {a.AvgPossession:0.#}%)",
                    Evidence.Inferred, a.Record.Matches, 0.5);
            else if (a.AvgPossession <= avgPossession - 5)
                yield return new Insight("style.counter", InsightKind.Trait, $"낮은 점유율 · 역습형 추정 (평균 점유율 {a.AvgPossession:0.#}%)",
                    Evidence.Inferred, a.Record.Matches, 0.5);
        }

        if (a.AvgPause >= 1.5)
            yield return new Insight("pause", InsightKind.Trait, $"일시정지 경기당 {a.AvgPause:0.#}회", Evidence.Direct, a.Record.Matches, 0.3);
    }

    // ── building blocks ────────────────────────────────────────────────────

    private static IReadOnlyList<PlayerThreat> PlayerThreats(List<MatchInfo> mySides, List<ShootDetail> goals)
    {
        var shotsByPlayer = mySides.SelectMany(s => s.ShootDetail).GroupBy(s => s.SpId).ToDictionary(g => g.Key, g => g.Count());
        var goalsByPlayer = goals.GroupBy(g => g.SpId).ToDictionary(g => g.Key, g => g.Count());
        var assistsByPlayer = goals.Where(g => g.AssistSpId is not null).GroupBy(g => g.AssistSpId!.Value).ToDictionary(g => g.Key, g => g.Count());

        var appearances = new Dictionary<int, int>();
        var grades = new Dictionary<int, int>();
        foreach (var side in mySides)
        {
            foreach (var p in side.Player)
            {
                var played = !p.IsSubstitute || p.Status.PassTry + p.Status.Shoot + p.Status.TackleTry + p.Status.DribbleTry > 0;
                if (played) appearances[p.SpId] = appearances.GetValueOrDefault(p.SpId) + 1;
                grades[p.SpId] = Math.Max(grades.GetValueOrDefault(p.SpId), p.SpGrade);
            }
        }

        var ids = goalsByPlayer.Keys.Concat(assistsByPlayer.Keys).Distinct();
        return ids
            .Select(id => new PlayerThreat(
                id,
                goalsByPlayer.GetValueOrDefault(id),
                assistsByPlayer.GetValueOrDefault(id),
                shotsByPlayer.GetValueOrDefault(id),
                appearances.GetValueOrDefault(id),
                goals.Count == 0 ? 0 : (double)goalsByPlayer.GetValueOrDefault(id) / goals.Count,
                grades.GetValueOrDefault(id)))
            .OrderByDescending(p => p.Goals + 0.5 * p.Assists)
            .ThenByDescending(p => p.Shots)
            .ToList();
    }

    private static SignatureGoal? Signature(List<ShootDetail> goals)
    {
        var best = goals
            .GroupBy(g => (g.SpId, g.AssistSpId, Zone: Pitch.ZoneOf(g.X, g.Y, g.InPenalty), g.Type))
            .OrderByDescending(g => g.Count())
            .FirstOrDefault();
        if (best is null || best.Count() < 3 || best.Count() < goals.Count * 0.12) return null;
        return new SignatureGoal(best.Key.SpId, best.Key.AssistSpId, best.Key.Zone, best.Key.Type, best.Count(), goals.Count);
    }

    /// <summary>
    /// Rebuilds who scored first from both sides' goal times. Matches whose shot list does not account for every
    /// goal (own goals carry no shot entry) are skipped rather than guessed.
    /// </summary>
    private static FirstGoalStats? FirstGoal(List<(MatchInfo Me, MatchInfo Opp)> sides)
    {
        int scoredFirst = 0, winsScoredFirst = 0, concededFirst = 0, winsConcededFirst = 0, goalless = 0, usable = 0;
        foreach (var (me, opp) in sides)
        {
            var myGoals = me.ShootDetail.Where(s => s.IsGoal).ToList();
            var oppGoals = opp.ShootDetail.Where(s => s.IsGoal).ToList();
            if (myGoals.Count != GoalsShown(me) || oppGoals.Count != GoalsShown(opp)) continue;
            usable++;

            var myFirst = myGoals.Count == 0 ? int.MaxValue : myGoals.Min(g => g.Seconds);
            var oppFirst = oppGoals.Count == 0 ? int.MaxValue : oppGoals.Min(g => g.Seconds);
            var won = me.MatchDetail.Outcome == MatchOutcome.Win;
            if (myFirst == int.MaxValue && oppFirst == int.MaxValue) goalless++;
            else if (myFirst < oppFirst) { scoredFirst++; if (won) winsScoredFirst++; }
            else if (oppFirst < myFirst) { concededFirst++; if (won) winsConcededFirst++; }
        }
        return usable == 0 ? null : new FirstGoalStats(scoredFirst, winsScoredFirst, concededFirst, winsConcededFirst, goalless);
    }

    private static IReadOnlyList<Share> PassMix(List<PassSummary> passes)
    {
        var total = passes.Sum(p => p.PassTry);
        if (total == 0) return [];
        return new[]
        {
            new Share("숏패스", passes.Sum(p => p.ShortPassTry), total),
            new Share("롱패스", passes.Sum(p => p.LongPassTry), total),
            new Share("스루패스", passes.Sum(p => p.ThroughPassTry), total),
            new Share("로빙 스루패스", passes.Sum(p => p.LobbedThroughPassTry), total),
            new Share("바운싱 로브", passes.Sum(p => p.BouncingLobPassTry), total),
            new Share("드리븐 땅볼", passes.Sum(p => p.DrivenGroundPassTry), total),
        }.Where(s => s.Count > 0).OrderByDescending(s => s.Count).ToList();
    }

    private static IReadOnlyList<Share> Shares(List<ShootDetail> goals, Func<ShootDetail, string> label) =>
        goals.GroupBy(label)
            .Select(g => new Share(g.Key, g.Count(), goals.Count))
            .OrderByDescending(s => s.Count)
            .ToList();

    /// <summary>The score shown to players after the match; falls back to the counted total.</summary>
    internal static int GoalsShown(MatchInfo side) => Math.Max(side.Shoot.GoalTotalDisplay, side.Shoot.GoalTotal);

    private static double Avg<T>(List<T> items, Func<T, double> f) => items.Count == 0 ? 0 : items.Average(f);
    private static double Ratio(double a, double b) => b == 0 ? 0 : a / b;
    internal static string Pct(double v) => $"{v * 100:0}%";
}
