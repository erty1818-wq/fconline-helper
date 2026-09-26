using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FcHelper.App.Studio;
using FcHelper.Market;
using FcHelper.NexonApi;
using FcHelper.Services;

namespace FcHelper.App;

/// <summary>The sections a 매칭 카드 can show besides the basics; the user picks them in the settings (AM-03, AM-05).</summary>
public static class MatchCardSections
{
    public static readonly (string Key, string Label)[] All =
    [
        ("form", "최근 20경기 승무패 칩 · 연승연패"),
        ("value", "구단가치 · 포메이션 · 평균 OVR"),
        ("pay", "급여 배분 (GK · 수비 · 미드 · 공격)"),
        ("price", "시세(돈) 배분 (GK · 수비 · 미드 · 공격)"),
        ("seasons", "선발 11명의 시즌 · 강화 · OVR"),
        ("weak", "약점 (낮은 OVR 자리 · 저급여 GK)"),
        ("danger", "위험 선수"),
    ];

    public static readonly string[] Defaults = ["form", "value", "pay", "price", "seasons", "weak", "danger"];
}

/// <summary>
/// The card that pops up by itself when an opponent is matched (AM-02/03): the basics always, then only the sections the
/// user switched on, and the window grows or shrinks to fit them. The full analysis stays in 구단주 검색 ([자세히]).
/// </summary>
public partial class MatchCard : Window
{
    private readonly App _app;
    private readonly HashSet<string> _on;
    private string? _nickname;

    public MatchCard(App app)
    {
        _app = app;
        _on = [.. app.Settings.CardSections];
        InitializeComponent();
        Topmost = false;
        Scroller.MaxHeight = SystemParameters.WorkArea.Height - 60;
        // Wider only when a section needs the room.
        Body.Width = _on.Overlaps(["pay", "price", "seasons"]) ? 380 : 320;
        PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) Close(); };
    }

    private Brush Res(string key) => (Brush)FindResource(key);

    private TextBlock Line(string text, string brush = "Text", double size = 13, bool bold = false) => new()
    {
        Text = text, Foreground = Res(brush), FontSize = size, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
        TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 1),
    };

    private TextBlock Heading(string text) => new()
    {
        Text = text, Style = (Style)FindResource("FieldLabel"), Margin = new Thickness(0, 10, 0, 3),
    };

    /// <summary>Beside the game window (right if there is room, else left), never over it.</summary>
    public void PlaceBeside(Rect game)
    {
        void Place()
        {
            var area = SystemParameters.WorkArea;
            var w = ActualWidth > 0 ? ActualWidth : Body.Width + 40;
            Left = game.Right + 8 + w <= area.Right ? game.Right + 8 : game.Left - 8 - w >= area.Left ? game.Left - 8 - w : area.Right - w - 10;
            Top = Math.Max(area.Top + 10, Math.Min(game.Top, area.Bottom - ActualHeight - 10));
        }
        Place();
        SizeChanged += (_, _) => Place();
    }

    /// <summary>Looks the opponent up (cache first, see OS-03) and fills the card; the squad sections follow.</summary>
    /// <param name="endMinute">Set for the 종료 카드: the last match minute the watcher saw.</param>
    public async Task ShowForAsync(string nickname, int? endMinute = null)
    {
        _nickname = nickname;
        Title = endMinute is null ? $"매칭 카드 · {nickname}" : $"경기 종료 · {nickname}";
        Body.Children.Clear();
        _end = endMinute;
        Body.Children.Add(Line($"{nickname} 불러오는 중…", "Muted"));
        if (_app.Service is not { } service) { Body.Children.Clear(); Body.Children.Add(Line("NEXON Open API 키를 먼저 입력하세요.", "Warn")); return; }
        OpponentReport? report;
        try
        {
            report = await service.LookupAsync(nickname);
        }
        catch (Exception e) when (e is NexonApiException or HttpRequestException or TaskCanceledException)
        {
            Body.Children.Clear();
            Body.Children.Add(Line("상대 정보를 불러오지 못했습니다. 연결 상태나 API 한도를 확인하세요.", "Warn"));
            return;
        }
        if (_nickname != nickname) return;
        Body.Children.Clear();
        if (report is null) { Body.Children.Add(Line($"'{nickname}' 닉네임을 찾지 못했습니다.", "Warn")); return; }
        if (_end is { } minute) AddEndBanner(nickname, minute);
        AddBasics(report);
        var squad = new StackPanel();
        Body.Children.Add(squad);
        AddFooter();
        if (_on.Overlaps(["value", "pay", "price", "seasons", "weak"])) await AddSquadAsync(report, squad);
    }

    private int? _end;

    /// <summary>
    /// The 종료 카드 banner. The official record of this match (score, shots, ratings) reaches the Open API only an hour or
    /// two later (checked 2026-09-26), and the score digits cannot be read off the screen, so the card says so plainly.
    /// </summary>
    private void AddEndBanner(string nickname, int minute)
    {
        Body.Children.Add(new Border
        {
            Background = Res("WarnSoft"), CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 0, 0, 8),
            Child = new StackPanel
            {
                Children =
                {
                    Line($"경기 종료 · vs {nickname}", bold: true, size: 15),
                    Line(minute > 0 ? $"마지막으로 본 경기 시간 {minute}분" : "경기 화면이 사라졌습니다", "Muted", 11),
                    Line("공식 기록(점수·슛·평점)은 NEXON Open API에 보통 1~2시간 뒤 올라옵니다. 그 뒤 구단주 검색의 재대결 전적과 [내 전적]에 반영됩니다.", "Muted", 11),
                },
            },
        });
    }

    private void AddBasics(OpponentReport r)
    {
        var view = new ReportView(r);
        Body.Children.Add(Line(view.Header, bold: true, size: 17));
        var division = r.MaxDivisionName is null ? "" : $"최고 {r.MaxDivisionName} · ";
        Body.Children.Add(Line(division + view.Record, "Muted", 12));
        if (_on.Contains("form") && r.Form.Results.Count > 0)
        {
            var chips = new WrapPanel { Margin = new Thickness(0, 3, 0, 1), ToolTip = view.FormTip };
            foreach (var c in view.Form)
                chips.Children.Add(new Border
                {
                    Background = c.Fill, CornerRadius = new CornerRadius(3), Width = 14, Height = 15, Margin = new Thickness(0, 0, 1, 0),
                    Child = new TextBlock { Text = c.Letter, FontSize = 9, FontWeight = FontWeights.Bold, Foreground = Res("Bg"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
                });
            if (view.Streak.Length > 0) chips.Children.Add(Line("  " + view.Streak, "Warn", 12, bold: true));
            Body.Children.Add(chips);
        }
        if (view.HeadToHead.Length > 0) Body.Children.Add(Line(view.HeadToHead, "Warn", 12, bold: true));
        Body.Children.Add(new Border
        {
            Style = (Style)FindResource("CardPanel"), Padding = new Thickness(8, 5, 8, 5), Margin = new Thickness(0, 5, 0, 0),
            Child = Line(r.OneLine, bold: true),
        });
        // 위험 / 약점 comments against the average: always shown, they are the point of the card.
        foreach (var (kind, title, brush) in new[] { (NoteKind.Danger, "위험", "Danger"), (NoteKind.Weakness, "약점", "Accent") })
        {
            var notes = view.Notes.Where(n => n.Kind == kind).Take(3).ToList();
            if (notes.Count == 0) continue;
            var heading = Heading(title);
            heading.Foreground = Res(brush);
            Body.Children.Add(heading);
            foreach (var n in notes) Body.Children.Add(Line($"• {n.Text}", size: 12));
        }
        if (_on.Contains("danger") && view.DangerPlayers.Count > 0)
        {
            Body.Children.Add(Heading("위험 선수 [직접]"));
            foreach (var d in view.DangerPlayers) Body.Children.Add(Line(d, size: 12));
        }
    }

    /// <summary>The squad sections, from the opponent's latest eleven (cards the market data lacks are read from the data center).</summary>
    private async Task AddSquadAsync(OpponentReport r, StackPanel host)
    {
        var side = r.Matches.Select(m => m.SideOf(r.Ouid)).FirstOrDefault(s => s is { HasStats: true });
        if (side is null || _app.Squads is not { } squads) return;
        var owned = SquadContext.StartersOf(side);
        host.Children.Add(Line("선발 카드 불러오는 중…", "Muted", 11));
        try { await squads.LoadOffMarketAsync(owned.Select(o => o.SpId)); }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { }
        if (_nickname is null || !IsLoaded && !IsVisible) return;
        host.Children.Clear();
        var slots = squads.WithInGameOvr(squads.CurrentSquad(owned));
        if (slots.Count == 0) return;

        if (_on.Contains("value"))
        {
            host.Children.Add(Heading("구단가치 [계산]"));
            var formation = SquadContext.FormationOf(side);
            host.Children.Add(Line($"선발 시세 합 {Bp.Format(slots.Sum(s => s.Price))} · 평균 OVR {slots.Average(s => s.ShownOvr):0.0}" + (formation is null ? "" : $" · {formation} [추정]"), bold: true));
            host.Children.Add(Line($"급여 합 {slots.Sum(s => s.Pay)} · OVR은 인게임 추정 [추정] · 팀컬러는 빼고 계산", "Muted", 11));
        }
        var money = SquadMoney.Of(slots);
        if (_on.Contains("pay")) AddShareBars(host, "급여 배분 [계산]", money, l => l.PayShare, l => $"{l.Pay}");
        if (_on.Contains("price")) AddShareBars(host, "시세 배분 [계산]", money, l => l.PriceShare, l => Bp.Format(l.Price));
        if (_on.Contains("weak"))
        {
            var (_, spots) = SquadWeakSpots.Of(slots, squads.PayRank);
            host.Children.Add(Heading("약점 [계산]"));
            if (spots.Count == 0) host.Children.Add(Line("눈에 띄게 약한 자리가 없습니다.", "Muted", 12));
            foreach (var s in spots) host.Children.Add(Line($"{s.Position} {s.Name} OVR {s.Ovr:0} · {s.Reason}", "Warn", 12));
        }
        if (_on.Contains("seasons")) AddSeasons(host, slots);
    }

    private void AddShareBars(Panel host, string title, IReadOnlyList<LineShare> lines, Func<LineShare, double> share, Func<LineShare, string> amount)
    {
        host.Children.Add(Heading(title));
        foreach (var l in lines)
        {
            var row = new DockPanel { Margin = new Thickness(0, 1, 0, 1) };
            var name = Line($"{l.Line} ({l.Players})", "Muted", 12);
            name.Width = 64;
            var value = Line($"{share(l) * 100:0}% · {amount(l)}", size: 12);
            value.Width = 110;
            value.TextAlignment = TextAlignment.Right;
            DockPanel.SetDock(name, Dock.Left);
            DockPanel.SetDock(value, Dock.Right);
            row.Children.Add(name);
            row.Children.Add(value);
            row.Children.Add(new ProgressBar
            {
                Maximum = 1, Value = share(l), Height = 7, VerticalAlignment = VerticalAlignment.Center, BorderThickness = new Thickness(0),
                Background = Res("Panel"), Foreground = Res(l.Line == "공격" ? "Warn" : "Info"),
            });
            host.Children.Add(row);
        }
    }

    private void AddSeasons(Panel host, IReadOnlyList<SquadSlot> slots)
    {
        host.Children.Add(Heading("선발 11명 [직접]"));
        var order = new[] { "공격", "미드", "수비", "GK" };
        foreach (var s in slots.OrderBy(s => Array.IndexOf(order, SquadMoney.LineOf(s.Position))))
        {
            var row = new DockPanel { Margin = new Thickness(0, 1, 0, 1) };
            var pos = Line(s.Position, "Muted", 12);
            pos.Width = 40;
            var season = new Image { Width = 20, Height = 20, Margin = new Thickness(0, 0, 6, 0), ToolTip = s.Card.Season };
            Pictures.SetSeasonOf(season, s.Card.Season);
            var ovr = Line($"+{s.Grade} · {s.ShownOvr:0}", size: 12);
            ovr.Width = 70;
            ovr.TextAlignment = TextAlignment.Right;
            foreach (var e in new FrameworkElement[] { pos, season }) { DockPanel.SetDock(e, Dock.Left); row.Children.Add(e); }
            DockPanel.SetDock(ovr, Dock.Right);
            row.Children.Add(ovr);
            row.Children.Add(Line($"{s.Card.Name} {s.Card.Season}", size: 12));
            host.Children.Add(row);
        }
    }

    private void AddFooter()
    {
        var more = new Button { Content = "자세히 (구단주 검색)", Style = (Style)FindResource("Ghost"), Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 10, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        more.Click += (_, _) => { if (_nickname is not null) _app.OpenSearch(_nickname); };
        Body.Children.Add(more);
        Body.Children.Add(Line("보이는 항목은 설정 → 매칭 카드에서 고릅니다. Data based on NEXON Open API.", "Muted", 10));
    }
}
