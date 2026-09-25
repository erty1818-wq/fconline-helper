using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FcHelper.Market;
using FcHelper.Services;

namespace FcHelper.App.Studio;

/// <summary>What to field against one opponent: their weaknesses and threats turned into cards, and the formation to use.</summary>
public partial class OpponentPage : UserControl
{
    public OpponentPage()
    {
        InitializeComponent();
        GradeBox.ItemsSource = Enumerable.Range(1, 13).Select(g => $"+{g}").ToList();
        GradeBox.SelectedIndex = 7;
    }

    /// <summary>Opens the page for an opponent (e.g. from the search card).</summary>
    public void Analyse(string nickname)
    {
        NickBox.Text = nickname;
        OnGo(this, new RoutedEventArgs());
    }

    private async void OnGo(object sender, RoutedEventArgs e)
    {
        var app = StudioKit.App;
        if (StudioKit.Squads is not { } squads || app.Db is not { } db) return;
        if (app.Service is not { } service) { Status.Text = "설정에서 API 키를 입력하세요."; return; }
        if (!StudioKit.TryPrice(MaxBox, long.MaxValue, out var max)) { Status.Text = "가격은 10억처럼 입력하세요."; return; }
        var nick = NickBox.Text.Trim();
        if (nick.Length == 0) return;
        var grade = GradeBox.SelectedIndex + 1;
        await StudioKit.Run(GoButton, Status, async () =>
        {
            Status.Text = "상대 경기를 불러오는 중…";
            Result.Children.Clear();
            var report = await service.LookupAsync(nick);
            if (report is null) { Status.Text = "닉네임을 찾지 못했습니다."; return; }
            Status.Text = "";
            Section($"{report.Nickname} · {report.OneLine}", null);

            var formation = SquadContext.OpponentFormation(db, report.Ouid);
            var advice = formation is null ? null : await squads.FormationAdviceAsync(formation);
            var box = Card();
            box.Children.Add(Text($"상대 추정 포메이션: {formation ?? "알 수 없음"}  [추정]", bold: true));
            if (advice is { Best.Count: > 0 })
            {
                box.Children.Add(Text("랭커 전적상 유리한 포메이션 (전날 공식경기, 30경기 이상)", hint: true));
                foreach (var m in advice.Best) box.Children.Add(Text($"  {m.Formation}  {m.Wins}승 {m.Draws}무 {m.Losses}패 · 승률 {m.WinRate:P0}", color: "Accent"));
                foreach (var m in advice.Worst.Take(2)) box.Children.Add(Text($"  피할 것: {m.Formation}  {m.Wins}승 {m.Draws}무 {m.Losses}패 · 승률 {m.WinRate:P0}", color: "Warn"));
            }
            Result.Children.Add((Border)box.Parent);

            var needs = SquadContext.NeedsAgainst(report.Analysis);
            if (needs.Count == 0) { Section("뚜렷한 맞춤 포인트가 없습니다. 평범한 상대로 보입니다.", null); return; }
            foreach (var group in squads.Tailored(needs, grade, max, 5).GroupBy(t => t.Need))
            {
                var card = Card();
                card.Children.Add(Text(group.First().Reason, bold: true));
                foreach (var t in group)
                    card.Children.Add(Text($"  {t.Position,-4} {t.Card.Name} {t.Card.Season} +{t.Grade} · OVR {t.Ovr} · {Bp.Format(t.Price)}  {StudioKit.Tags(t.Card)}"));
                Result.Children.Add((Border)card.Parent);
            }
        });
    }

    private void Section(string text, string? hint)
    {
        var p = Card();
        p.Children.Add(Text(text, bold: true));
        if (hint is not null) p.Children.Add(Text(hint, hint: true));
        Result.Children.Add((Border)p.Parent);
    }

    private StackPanel Card()
    {
        var panel = new StackPanel();
        _ = new Border { Style = (Style)FindResource("CardPanel"), Margin = new Thickness(0, 0, 0, 10), Child = panel };
        return panel;
    }

    private TextBlock Text(string s, bool bold = false, bool hint = false, string? color = null)
    {
        var t = new TextBlock { Text = s, Margin = new Thickness(0, 2, 0, 2) };
        if (bold) t.FontWeight = FontWeights.SemiBold;
        if (hint) t.Style = (Style)FindResource("Hint");
        if (color is not null) t.Foreground = (Brush)FindResource(color);
        return t;
    }
}
