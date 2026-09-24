using FcHelper.Analysis;
using FcHelper.Core;
using FcHelper.Services;

namespace FcHelper.App;

public sealed record InsightItem(string Text, string Badge);

public sealed record ShareItem(string Label, double Percent, string Display);

public sealed class TagOption(string name, bool isChecked)
{
    public string Name { get; } = name;
    public bool IsChecked { get; set; } = isChecked;
}

/// <summary>Display-ready strings for the card, built once per report so the XAML stays declarative.</summary>
public sealed class ReportView
{
    public static readonly string[] QuickTags = ["스루 많음", "헤더 많음", "중거리 많음", "탈주", "매너 좋음", "매너 나쁨", "강함", "다시 만나기 싫음"];

    public ReportView(OpponentReport r)
    {
        Report = r;
        var a = r.Analysis;

        Header = r.Level > 0 ? $"{r.Nickname}  Lv.{r.Level}" : r.Nickname;
        Controller = a.Controller is { } c ? ReportText.ControllerLabel(c.Label) : "";
        PreviousNicknames = r.PreviousNicknames.Count > 0 ? $"이전 닉네임: {string.Join(", ", r.PreviousNicknames)}" : "";
        HeadToHead = r.HeadToHead is { } h
            ? $"🔁 재대결 {h.Matches}전 {h.Wins}승 {h.Draws}무 {h.Losses}패" + (h.LastScore is null ? "" : $" · 지난 경기 {h.LastScore}")
            : "";

        var rec = a.Record;
        Record = ReportText.DivisionPrefix(r)
            + $"최근 {rec.Matches}경기 {rec.Wins}승 {rec.Draws}무 {rec.Losses}패 ({rec.WinRate * 100:0}%)";
        Averages = $"평균 득점 {a.AvgGoalsFor:0.00} · 실점 {a.AvgGoalsAgainst:0.00} · 점유율 {a.AvgPossession:0.#}%";
        OneLine = r.OneLine;

        Threats = a.Threats.Take(ReportText.CardThreats).Select(Item).ToList();
        Weakness = a.Weaknesses.Take(1).Select(Item).ToList();
        Matchups = r.MatchupAlerts.Take(2).Select(m => new InsightItem(m.Text, "상성")).ToList();
        Signature = a.Signature is { } s ? $"✦ 시그니처 골: {ReportText.SignatureText(r, s)}" : "";
        DangerPlayers = a.Players.Take(2).Select(p => $"{r.PlayerName(p.SpId)}  {p.Goals}골 {p.Assists}도움 · 강화 {p.TopGrade}").ToList();
        Traits = a.Traits.Where(t => t.Key != "controller").Take(3).Select(Item).ToList();

        MemoText = r.Memo?.Text ?? "";
        Tags = QuickTags.Concat(r.Memo?.Tags.Except(QuickTags) ?? [])
            .Select(t => new TagOption(t, r.Memo?.Tags.Contains(t) == true)).ToList();

        GoalTypes = Shares(a.GoalTypes);
        GoalZones = Shares(a.GoalZones);
        ConcededTypes = Shares(a.ConcededTypes);
        ConcededZones = Shares(a.ConcededZones);
        PassMix = Shares(a.PassMix);
        Players = a.Players.Take(8)
            .Select(p => $"{r.PlayerName(p.SpId)}  {p.Goals}골 {p.Assists}도움 · 슈팅 {p.Shots} · {p.Appearances}경기").ToList();
        Combos = a.Combos.Select(c => $"{r.PlayerName(c.AssistSpId)} → {r.PlayerName(c.ScorerSpId)}  {c.Count}회").ToList();
        Numbers =
        [
            $"경기당 슈팅 {a.AvgShots:0.0} · 유효슈팅 {a.AvgShotsOnTarget:0.0} · 정확도 {a.ShotAccuracy * 100:0}%",
            $"결정력(골/슈팅) {a.Conversion * 100:0}% · 패스 성공률 {a.PassSuccess * 100:0}%",
            $"경기당 태클 시도 {a.AvgTackleTry:0.0} · 태클 성공률 {a.TackleSuccess * 100:0}% · 인터셉트 {a.AvgIntercept:0.0}",
            a.FirstGoal is { } f
                ? $"선제골 시 승률 {Rate(f.WinsWhenScoredFirst, f.MatchesScoredFirst)} · 선제 실점 시 승률 {Rate(f.WinsWhenConcededFirst, f.MatchesConcededFirst)}"
                : "선제골 통계: 데이터 부족",
            $"몰수패 {rec.Forfeits}회 · 경기당 일시정지 {a.AvgPause:0.0}회",
        ];

        SampleNote = (a.ComparedToBaseline
                ? $"표본: 득점 {a.GoalCount}골 · 실점 {a.ConcededCount}골 · 평균은 내가 캐시한 유저들 기준"
                : $"표본: 득점 {a.GoalCount}골 · 실점 {a.ConcededCount}골 · 비교 기준 데이터가 아직 적어 평균 비교 생략")
            + (r.IsComplete ? "" : $" · 불러오는 중 {r.LoadedMatches}/{r.RequestedMatches}");
    }

    public OpponentReport Report { get; }
    public string Header { get; }
    public string Controller { get; }
    public string PreviousNicknames { get; }
    public string HeadToHead { get; }
    public string Record { get; }
    public string Averages { get; }
    public string OneLine { get; }
    public IReadOnlyList<InsightItem> Threats { get; }
    public IReadOnlyList<InsightItem> Weakness { get; }
    public IReadOnlyList<InsightItem> Matchups { get; }
    public string Signature { get; }
    public IReadOnlyList<string> DangerPlayers { get; }
    public IReadOnlyList<InsightItem> Traits { get; }
    public string MemoText { get; set; }
    public IReadOnlyList<TagOption> Tags { get; }
    public IReadOnlyList<ShareItem> GoalTypes { get; }
    public IReadOnlyList<ShareItem> GoalZones { get; }
    public IReadOnlyList<ShareItem> ConcededTypes { get; }
    public IReadOnlyList<ShareItem> ConcededZones { get; }
    public IReadOnlyList<ShareItem> PassMix { get; }
    public IReadOnlyList<string> Players { get; }
    public IReadOnlyList<string> Combos { get; }
    public IReadOnlyList<string> Numbers { get; }
    public string SampleNote { get; }

    private static InsightItem Item(Insight i) => new(i.Text, i.Evidence.Label());

    private static List<ShareItem> Shares(IEnumerable<Share> shares) =>
        shares.Take(8).Select(s => new ShareItem(s.Label, s.Ratio * 100, $"{s.Ratio * 100:0}% ({s.Count})")).ToList();

    private static string Rate(int wins, int matches) => matches == 0 ? "-" : $"{wins * 100 / matches}% ({matches}경기)";
}
