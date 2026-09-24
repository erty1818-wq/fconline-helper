using System.Text;
using FcHelper.Analysis;
using FcHelper.Core;

namespace FcHelper.Services;

/// <summary>Plain-text renderings of a report: the card layout (CLI, clipboard) and a short sentence for voice.</summary>
public static class ReportText
{
    public const int CardThreats = 3;

    public static string Card(OpponentReport r)
    {
        var a = r.Analysis;
        var sb = new StringBuilder();
        sb.Append(r.Nickname);
        if (r.Level > 0) sb.Append($"  Lv.{r.Level}");
        if (a.Controller is { } c) sb.Append($"  {ControllerLabel(c.Label)}");
        sb.AppendLine();
        if (r.PreviousNicknames.Count > 0) sb.AppendLine($"이전 닉네임: {string.Join(", ", r.PreviousNicknames)}");

        if (r.HeadToHead is { } h)
        {
            sb.AppendLine($"재대결 {h.Matches}전 {h.Wins}승 {h.Draws}무 {h.Losses}패" + (h.LastScore is null ? "" : $" · 지난 경기 {h.LastScore}"));
        }

        var rec = a.Record;
        sb.Append(DivisionPrefix(r));
        sb.AppendLine($"최근 {rec.Matches}경기 {rec.Wins}승 {rec.Draws}무 {rec.Losses}패 ({rec.WinRate * 100:0}%)"
            + (r.IsComplete ? "" : $"  [불러오는 중 {r.LoadedMatches}/{r.RequestedMatches}]"));
        sb.AppendLine($"평균 득점 {a.AvgGoalsFor:0.00} · 실점 {a.AvgGoalsAgainst:0.00} · 점유율 {a.AvgPossession:0.#}%");

        sb.AppendLine();
        sb.AppendLine(r.OneLine);

        var threats = a.Threats.Take(CardThreats).ToList();
        if (threats.Count > 0)
        {
            sb.AppendLine("⚠ 주의");
            for (var i = 0; i < threats.Count; i++) sb.AppendLine($" {i + 1} {threats[i].Text}  [{threats[i].Evidence.Label()}]");
        }
        if (a.Weaknesses.FirstOrDefault() is { } w) sb.AppendLine($"🎯 약점: {w.Text}  [{w.Evidence.Label()}]");
        foreach (var m in r.MatchupAlerts.Take(2)) sb.AppendLine($"⚡ 상성: {m.Text}");

        if (a.Signature is { } s) sb.AppendLine($"✦ 시그니처 골: {SignatureText(r, s)}");

        var danger = a.Players.Take(2).ToList();
        if (danger.Count > 0)
        {
            sb.AppendLine("위험 선수: " + string.Join(" · ", danger.Select(p => $"{r.PlayerName(p.SpId)} {p.Goals}골 {p.Assists}도움")));
        }

        foreach (var t in a.Traits.Where(t => t.Key != "controller").Take(3)) sb.AppendLine($"· {t.Text}  [{t.Evidence.Label()}]");

        if (r.Memo is { } memo)
        {
            var tags = memo.Tags.Count > 0 ? $"[{string.Join("] [", memo.Tags)}] " : "";
            sb.AppendLine($"📝 {tags}{memo.Text}");
        }

        sb.AppendLine();
        sb.Append(a.ComparedToBaseline
            ? $"표본: 득점 {a.GoalCount}골 · 실점 {a.ConcededCount}골. 평균은 내 캐시의 상대들 기준."
            : $"표본: 득점 {a.GoalCount}골 · 실점 {a.ConcededCount}골. 비교 기준 데이터가 아직 적어 평균 비교는 생략.");
        return sb.ToString();
    }

    /// <summary>"최근경기 등급 X · 최고 Y · ", with either part left out when unknown.</summary>
    public static string DivisionPrefix(OpponentReport r) =>
        (r.RecentDivisionName is null ? "" : $"최근경기 등급 {r.RecentDivisionName} · ")
        + (r.MaxDivisionName is null ? "" : $"최고 {r.MaxDivisionName} · ");

    public static string SignatureText(OpponentReport r, SignatureGoal s)
    {
        var route = s.AssistSpId is { } assist ? $"{r.PlayerName(assist)} → {r.PlayerName(s.ScorerSpId)}" : $"{r.PlayerName(s.ScorerSpId)} 단독";
        return $"{route}, {Pitch.Label(s.Zone)} {ShotTypes.Label(s.ShotType)} {s.Count}회 ({s.Count}/{s.TotalGoals}골)";
    }

    /// <summary>One or two sentences meant to be read aloud before kickoff.</summary>
    public static string Voice(OpponentReport r)
    {
        var parts = new List<string>();
        if (r.HeadToHead is { } h) parts.Add($"재대결입니다. {h.Wins}승 {h.Losses}패.");
        parts.Add(r.OneLine);
        if (r.MatchupAlerts.FirstOrDefault() is { } m) parts.Add($"상성 주의, {m.OpponentThreat.Short}.");
        if (r.Memo?.Tags.Count > 0) parts.Add($"메모, {string.Join(", ", r.Memo.Tags)}.");
        return string.Join(" ", parts);
    }

    public static string ControllerLabel(string raw) => raw switch
    {
        "keyboard" => "⌨ 키보드",
        "gamepad" or "pad" => "🎮 패드",
        _ => raw,
    };
}
