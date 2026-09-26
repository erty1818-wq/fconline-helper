using FcHelper.Analysis;
using FcHelper.Core;
using FcHelper.Core.Models;

namespace FcHelper.Services;

public enum NoteKind { Danger, Weakness, Trait }

/// <summary>One sentence about the opponent with its numbers, e.g. "감아차기에 자주 실점하는 유저입니다 (실점의 38% · 평균 22%)".</summary>
public sealed record ScoutNote(NoteKind Kind, string Text, string Badge, double Score);

/// <summary>
/// Plain-language 위험 / 약점 / 특징 lines from the opponent's recent matches against "평균" (every match side in the local
/// cache, <see cref="Baseline"/>), asked for by the user on 2026-09-26. Every line carries the two numbers it compares;
/// xG-based lines are [추정]. Thresholds: at least <see cref="MinMatches"/> matches with records, and for goal shares at
/// least 3 goals of that kind, 1.3× the average and 7 points above it.
/// </summary>
public static class ScoutNotes
{
    public const int MinMatches = 5;
    private const double MinLift = 1.3, MinGap = 0.07;

    public static IReadOnlyList<ScoutNote> Of(OpponentReport r)
    {
        var notes = new List<ScoutNote>();
        var games = r.Matches.Where(m => m.SideOf(r.Ouid) is { HasStats: true }).ToList();
        if (games.Count < MinMatches) return notes;
        var b = r.Baseline;
        var a = r.Analysis;

        // Match-level averages.
        double Avg(Func<MatchInfo, MatchInfo?, double> f) => games.Average(m => f(m.SideOf(r.Ouid)!, m.OpponentOf(r.Ouid)));
        var possession = Avg((me, _) => me.MatchDetail.Possession);
        var shots = Avg((me, _) => me.Shoot.ShootTotal);
        var goalsFor = Avg((me, them) => me.Shoot.GoalTotal + (them?.Shoot.OwnGoal ?? 0));
        var goalsAgainst = Avg((me, them) => (them?.Shoot.GoalTotal ?? 0) + me.Shoot.OwnGoal);
        double Rate(Func<MatchInfo, int> made, Func<MatchInfo, int> tried)
        {
            var t = games.Sum(m => tried(m.SideOf(r.Ouid)!));
            return t == 0 ? double.NaN : (double)games.Sum(m => made(m.SideOf(r.Ouid)!)) / t;
        }
        var conversion = Rate(s => s.Shoot.GoalTotal, s => s.Shoot.ShootTotal);
        var passing = Rate(s => s.Pass.PassSuccess, s => s.Pass.PassTry);
        var tackling = Rate(s => s.Defence.TackleSuccess, s => s.Defence.TackleTry);

        var avgPossession = b?.AvgPossession is > 0 ? b.AvgPossession : 50;
        if (possession >= avgPossession + 5)
            notes.Add(new(NoteKind.Danger, $"점유율이 평균보다 높은 유저입니다 ({possession:0}% · 평균 {avgPossession:0}%). 공을 오래 돌립니다.", "계산", 2 + (possession - avgPossession) / 5));
        else if (possession <= avgPossession - 5)
            notes.Add(new(NoteKind.Trait, $"점유율이 낮은 역습형입니다 ({possession:0}% · 평균 {avgPossession:0}%). 공을 뺏기면 빠르게 넘어옵니다.", "추정", 1.5 + (avgPossession - possession) / 5));

        if (b is not null)
        {
            if (b.AvgGoals > 0 && goalsFor >= b.AvgGoals * 1.25)
                notes.Add(new(NoteKind.Danger, $"득점력이 높은 유저입니다 (경기당 {goalsFor:0.0}골 · 평균 {b.AvgGoals:0.0}골).", "계산", 2 * goalsFor / b.AvgGoals));
            if (b.AvgGoals > 0 && goalsAgainst >= b.AvgGoals * 1.25)
                notes.Add(new(NoteKind.Weakness, $"실점이 많은 유저입니다 (경기당 {goalsAgainst:0.0}골 · 평균 {b.AvgGoals:0.0}골).", "계산", 2 * goalsAgainst / b.AvgGoals));
            else if (b.AvgGoals > 0 && goalsAgainst <= b.AvgGoals * 0.75)
                notes.Add(new(NoteKind.Danger, $"수비가 단단한 유저입니다 (경기당 실점 {goalsAgainst:0.0} · 평균 {b.AvgGoals:0.0}).", "계산", 2 * b.AvgGoals / Math.Max(0.3, goalsAgainst)));
            if (b.AvgShots > 0 && shots >= b.AvgShots * 1.25)
                notes.Add(new(NoteKind.Danger, $"슈팅을 많이 하는 유저입니다 (경기당 {shots:0.0}개 · 평균 {b.AvgShots:0.0}개).", "계산", 1.5 * shots / b.AvgShots));
            if (!double.IsNaN(conversion) && b.Conversion > 0 && conversion >= b.Conversion + 0.08)
                notes.Add(new(NoteKind.Danger, $"결정력이 좋은 유저입니다 (골 전환 {conversion * 100:0}% · 평균 {b.Conversion * 100:0}%).", "계산", 2 + (conversion - b.Conversion) * 10));
            if (!double.IsNaN(passing) && b.PassAccuracy > 0 && passing <= b.PassAccuracy - 0.05)
                notes.Add(new(NoteKind.Weakness, $"패스 성공률이 낮은 유저입니다 ({passing * 100:0}% · 평균 {b.PassAccuracy * 100:0}%). 압박하면 실수가 나옵니다.", "계산", 1.5 + (b.PassAccuracy - passing) * 10));
            if (!double.IsNaN(tackling) && b.TackleRate > 0 && tackling <= b.TackleRate - 0.07)
                notes.Add(new(NoteKind.Weakness, $"태클 성공률이 낮은 유저입니다 ({tackling * 100:0}% · 평균 {b.TackleRate * 100:0}%). 드리블 돌파가 잘 통합니다.", "계산", 1.5 + (b.TackleRate - tackling) * 10));
            notes.AddRange(ShareNotes(a, b));
        }

        // Finishing against the xG model, both ways [추정].
        var taken = ShotMap.Of(r.Matches, r.Ouid, conceded: false);
        var allowed = ShotMap.Of(r.Matches, r.Ouid, conceded: true);
        if (taken.Goals - taken.Xg >= 5)
            notes.Add(new(NoteKind.Danger, $"기대득점보다 {taken.Goals - taken.Xg:0.0}골 더 넣었습니다. 어려운 슛도 잘 넣는 유저입니다.", "추정", 2 + (taken.Goals - taken.Xg) / 5));
        if (allowed.Goals - allowed.Xg >= 4)
            notes.Add(new(NoteKind.Weakness, $"기대 실점보다 {allowed.Goals - allowed.Xg:0.0}골 더 먹혔습니다. 골키퍼나 수비가 약할 수 있습니다.", "추정", 2 + (allowed.Goals - allowed.Xg) / 4));

        // When the goals go in.
        var buckets = TimeBuckets.Of(taken, allowed);
        var totalAgainst = buckets.Sum(x => x.Against);
        if (buckets.MaxBy(x => x.Against) is { } worst && worst.Against >= 4 && totalAgainst > 0 && (double)worst.Against / totalAgainst >= 0.3)
            notes.Add(new(NoteKind.Weakness, $"{worst.Label}분에 실점이 몰립니다 ({worst.Against}골 · 실점의 {100.0 * worst.Against / totalAgainst:0}%).", "계산", 1.5));
        var totalFor = buckets.Sum(x => x.For);
        if (buckets.MaxBy(x => x.For) is { } best && best.For >= 5 && totalFor > 0 && (double)best.For / totalFor >= 0.3)
            notes.Add(new(NoteKind.Danger, $"{best.Label}분에 득점이 몰립니다 ({best.For}골 · 득점의 {100.0 * best.For / totalFor:0}%).", "계산", 1.5));

        // Form and habits.
        if (r.Form.StreakLength >= 3 && r.Form.Streak == MatchOutcome.Win)
            notes.Add(new(NoteKind.Danger, $"현재 {r.Form.StreakLength}연승 중입니다.", "직접", 1 + r.Form.StreakLength / 2.0));
        if (r.Form.StreakLength >= 3 && r.Form.Streak == MatchOutcome.Loss)
            notes.Add(new(NoteKind.Weakness, $"현재 {r.Form.StreakLength}연패 중입니다. 흔들리고 있을 수 있습니다.", "직접", 1 + r.Form.StreakLength / 2.0));
        var forfeits = r.Matches.Count(m => m.SideOf(r.Ouid)?.MatchDetail.MatchEndType == 2);
        if (r.Matches.Count > 0 && forfeits >= 2 && (double)forfeits / r.Matches.Count >= 0.1)
            notes.Add(new(NoteKind.Trait, $"몰수패(나가기)가 잦습니다 ({forfeits}번 · {100.0 * forfeits / r.Matches.Count:0}%). 지고 있으면 나갈 수 있습니다.", "직접", 1.2));
        if (a.Insights.FirstOrDefault(i => i.Key == "player.dependency") is { } dependency)
            notes.Add(new(NoteKind.Danger, $"한 선수에게 골이 몰립니다: {dependency.Text}. 이 선수를 막으면 득점이 줄어듭니다.", "계산", dependency.Score));

        return notes.OrderByDescending(n => n.Score).ToList();
    }

    /// <summary>How their goals are scored and conceded, by shot type, zone, time and route, compared with the average share.</summary>
    private static IEnumerable<ScoutNote> ShareNotes(UserAnalysis a, Baseline b)
    {
        foreach (var (key, p) in a.Rates)
        {
            // An average of 0% still counts: "none of the others, most of theirs" is worth saying.
            if (p.Count < 3 || p.Total == 0 || b.RateOf(key) is not { } avg) continue;
            // Undocumented shot codes (13, 14) have no name and a doubtful base; say nothing about them.
            if ((key.StartsWith(SideMetrics.GoalType) || key.StartsWith(SideMetrics.ConcededType)) && TypeOf(key) is < 1 or > 12) continue;
            var rate = p.Value;
            // Plain shots are the default finish; they need a clearer gap to mean anything.
            var gap = key.EndsWith("." + ShotTypes.Normal) ? MinGap * 1.5 : MinGap;
            if (rate < avg * MinLift || rate - avg < gap) continue;
            var numbers = $"({Pct(rate)} · 평균 {Pct(avg)})";
            var score = rate / Math.Max(avg, 0.02) * Math.Sqrt(p.Count);
            var note = key switch
            {
                _ when key.StartsWith(SideMetrics.GoalType) =>
                    Note(NoteKind.Danger, $"{ShotTypes.Label(TypeOf(key))} 득점이 많은 유저입니다 (득점의 {Pct(rate)} · 평균 {Pct(avg)})."),
                _ when key.StartsWith(SideMetrics.ConcededType) =>
                    Note(NoteKind.Weakness, $"{ShotTypes.Label(TypeOf(key))}에 자주 실점하는 유저입니다 (실점의 {Pct(rate)} · 평균 {Pct(avg)})."),
                _ when key.StartsWith(SideMetrics.GoalZone) && Enum.TryParse<GoalZone>(key[SideMetrics.GoalZone.Length..], out var z) =>
                    Note(NoteKind.Danger, $"{Pitch.Label(z)}에서 넣는 골이 많습니다 (득점의 {Pct(rate)} · 평균 {Pct(avg)})."),
                _ when key.StartsWith(SideMetrics.ConcededZone) && Enum.TryParse<GoalZone>(key[SideMetrics.ConcededZone.Length..], out var z) =>
                    Note(NoteKind.Weakness, $"{Pitch.Label(z)}에서 먹히는 골이 많습니다 (실점의 {Pct(rate)} · 평균 {Pct(avg)})."),
                SideMetrics.GoalLongRange => Note(NoteKind.Danger, $"중거리 득점이 많은 유저입니다 {numbers}. 박스 밖에서도 슛을 막아야 합니다."),
                SideMetrics.ConcededLongRange => Note(NoteKind.Weakness, $"중거리 슛에 자주 실점하는 유저입니다 {numbers}."),
                SideMetrics.GoalLate => Note(NoteKind.Danger, $"{SideMetrics.LateMinute}분 이후 득점이 많은 유저입니다 {numbers}. 막판까지 조심하세요."),
                SideMetrics.ConcededLate => Note(NoteKind.Weakness, $"{SideMetrics.LateMinute}분 이후 실점이 많은 유저입니다 {numbers}. 막판에 흔들립니다."),
                SideMetrics.RouteCutback => Note(NoteKind.Danger, $"컷백(엔드라인에서 뒤로 내주기)으로 만드는 골이 많습니다 {numbers}."),
                SideMetrics.RouteCross => Note(NoteKind.Danger, $"크로스 → 헤더 골이 많습니다 {numbers}. 측면 크로스를 막으세요."),
                SideMetrics.RouteThrough => Note(NoteKind.Danger, $"중앙 스루패스 침투 골이 많습니다 {numbers}. 수비 라인을 너무 올리지 마세요."),
                SideMetrics.PassThrough => Note(NoteKind.Trait, $"스루패스를 자주 씁니다 (패스의 {Pct(rate)} · 평균 {Pct(avg)})."),
                SideMetrics.PassLong => Note(NoteKind.Trait, $"롱패스를 자주 씁니다 (패스의 {Pct(rate)} · 평균 {Pct(avg)})."),
                _ => null,
            };
            if (note is not null) yield return note with { Score = score };
        }
    }

    private static ScoutNote Note(NoteKind kind, string text) => new(kind, text, "계산", 0);

    private static int TypeOf(string key) => int.TryParse(key[(key.LastIndexOf('.') + 1)..], out var t) ? t : 0;

    private static string Pct(double v) => $"{v * 100:0}%";
}
