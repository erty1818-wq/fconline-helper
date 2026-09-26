using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using FcHelper.App.Studio;
using FcHelper.Market;
using FcHelper.Services;

namespace FcHelper.App;

/// <summary>스쿼드 tab (OS-04): the opponent's starting eleven of their latest match on the pitch.</summary>
public partial class SearchWindow
{
    /// <summary>The match the squad tab shows; a new report with the same latest match keeps what is drawn.</summary>
    private string? _squadMatch;

    private async Task ShowSquadAsync(OpponentReport report)
    {
        var latest = report.Matches
            .Select(m => (Match: m, Side: m.SideOf(report.Ouid)))
            .FirstOrDefault(x => x.Side is { HasStats: true });
        if (latest.Side is not { } side)
        {
            _squadMatch = null;
            SetTab(SquadContent, TabHint("최근 경기에 선수 기록이 없습니다 (몰수 경기 등)."));
            return;
        }
        var key = $"{report.Ouid}|{latest.Match.MatchId}";
        if (_squadMatch == key) return;
        _squadMatch = key;

        var owned = SquadContext.StartersOf(side);
        var formation = SquadContext.FormationOf(side);
        var title = new TextBlock
        {
            Text = $"최근 경기 선발 {owned.Count}명" + (formation is null ? "" : $" · {formation} [추정]")
                + $" · {latest.Match.MatchDate.ToLocalTime():M월 d일 HH:mm}",
            FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap,
        };
        var value = new TextBlock { Foreground = Res<System.Windows.Media.Brush>("Muted"), FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
        var colors = new TextBlock { Foreground = Res<System.Windows.Media.Brush>("Muted"), FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 6) };
        var pitch = new PitchView { MaxWidth = 520, HorizontalAlignment = HorizontalAlignment.Center };
        SetTab(SquadContent, title, value, colors, pitch);

        if (_app.Squads is not { } squads)
        {
            value.Text = "시세 데이터가 아직 준비되지 않아 카드를 그릴 수 없습니다. 스쿼드 도우미를 한 번 열어 주세요.";
            return;
        }
        bool Stale() => _squadMatch != key;
        try
        {
            // Old-season or untraded cards are read from the data center (2 s each), so the first open can take a moment.
            value.Text = "카드 정보 불러오는 중…";
            var unread = await squads.LoadOffMarketAsync(owned.Select(o => o.SpId));
            if (Stale()) return;
            var slots = squads.CurrentSquad(owned);
            pitch.Show(slots);
            var untraded = slots.Count(s => !s.Card.IsTraded);
            value.Text = $"구단가치 [계산] 선발 시세 합 {Bp.Format(slots.Sum(s => s.Price))} · 평균 OVR {(slots.Count == 0 ? 0 : slots.Average(s => s.Ovr)):0.0}"
                + (untraded > 0 ? $" · {untraded}명은 시세 없음(합에서 빠짐)" : "")
                + (unread > 0 ? $" · {unread}명은 카드 정보를 받지 못함" : "");

            colors.Text = "팀컬러 확인 중… (처음에는 30초쯤 걸립니다)";
            var detected = await squads.DetectTeamColorsAsync(owned);
            if (Stale()) return;
            var active = SquadService.ActiveTeamColors(detected);
            colors.Text = detected.Count == 0
                ? "팀컬러 [계산]: 없음"
                : "팀컬러 [계산]: " + string.Join(" · ", detected.Where(d => active.Contains(d.Color.Id))
                    .Select(d => $"{TeamColor.CategoryLabel(d.Color.Category)} {d.Color.Name} {d.Owned}명 {d.Level.Level}단계"));
            // Draw again with the bonuses the squad plays with.
            pitch.Show(squads.CurrentSquad(owned, await squads.TargetsAsync(active)));
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            if (Stale()) return;
            colors.Text = "데이터센터에 연결하지 못해 일부 정보를 채우지 못했습니다.";
        }
    }

    private static void SetTab(Panel panel, params UIElement[] children)
    {
        panel.Children.Clear();
        foreach (var c in children) panel.Children.Add(c);
    }

    private TextBlock TabHint(string text) => new()
    {
        Text = text, Style = (Style)FindResource("Hint"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0),
    };

    private T Res<T>(string key) where T : class => (T)FindResource(key);
}
