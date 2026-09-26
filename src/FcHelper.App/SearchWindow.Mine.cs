using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FcHelper.Services;

namespace FcHelper.App;

/// <summary>내 전적 tab (OS-18): only for the user's own account, which stats go with winning [추정].</summary>
public partial class SearchWindow
{
    private const int MineMatches = 100;
    private string? _mineFor;

    /// <summary>The user's own ouid from the cache (no API call), or null when no nickname is set or it was never looked up.</summary>
    private string? MyOuid() =>
        _app.Db is { } db && !string.IsNullOrWhiteSpace(_app.Settings.MyNickname) ? db.FindUserByNickname(_app.Settings.MyNickname)?.Ouid : null;

    private void ShowMine(OpponentReport report, bool force = false)
    {
        if (_app.Db is not { } db) return;
        var key = $"{report.Ouid}|{report.Matches.FirstOrDefault()?.MatchId}";
        if (!force && _mineFor == key) return;
        _mineFor = key;

        var matches = db.GetMatches(db.GetCachedMatchIds(report.Ouid, _app.Service?.Options.MatchType ?? 50, MineMatches));
        var (count, factors) = WinFactors.Of(matches, report.Ouid);
        SetTab(MineContent, SectionTitle($"승률 개선 분석 [추정] · 내 공식경기 {count}경기"));

        var more = new Button { Content = $"내 경기 더 불러오기 (최근 {MineMatches}경기)", Style = (Style)FindResource("Ghost"), Padding = new Thickness(10, 4, 10, 4), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 6),
            ToolTip = "저장 안 된 경기만 NEXON Open API로 받습니다 (경기마다 1회)." };
        more.Click += async (_, _) =>
        {
            if (_app.Service is not { } service) return;
            more.IsEnabled = false;
            more.Content = "불러오는 중…";
            try { await service.SyncMyMatchesAsync(MineMatches); }
            catch (Exception e) when (e is NexonApi.NexonApiException or System.Net.Http.HttpRequestException or TaskCanceledException) { }
            ShowMine(report, force: true);
        };

        if (count < WinFactors.MinMatches)
        {
            MineContent.Children.Add(TabHint($"표본이 부족합니다. 기록 있는 경기가 {WinFactors.MinMatches}경기 이상 필요합니다 (지금 {count}경기)."));
            MineContent.Children.Add(more);
            return;
        }
        MineContent.Children.Add(new TextBlock
        {
            Text = "지표마다 경기를 중앙값으로 둘로 나눠, 높은 쪽과 낮은 쪽의 승률을 비교했습니다. 차이가 큰 순서입니다. "
                + "같이 나타나는 경향이지 원인이라는 뜻은 아닙니다. 몰수 경기는 뺐습니다.",
            Style = (Style)FindResource("Hint"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6),
        });

        foreach (var f in factors)
        {
            var box = new StackPanel { Margin = new Thickness(0, 4, 0, 6) };
            var gap = f.Gap * 100;
            var better = gap >= 0 ? "높을 때" : "낮을 때";
            box.Children.Add(new TextBlock
            {
                Text = $"{f.Label}: {better} 승률 {Math.Abs(gap):0}%p 높음", FontWeight = FontWeights.SemiBold,
                Foreground = Math.Abs(gap) >= 15 ? Res<Brush>("Accent") : Res<Brush>("Text"),
            });
            box.Children.Add(Half($"{f.Median:0.#}{f.Unit} 넘음", f.HighWinRate, f.HighMatches));
            box.Children.Add(Half($"{f.Median:0.#}{f.Unit} 이하", f.LowWinRate, f.LowMatches));
            MineContent.Children.Add(box);
        }
        if (factors.Count == 0) MineContent.Children.Add(TabHint("나눠 볼 수 있는 지표가 없습니다 (값이 거의 같음)."));
        MineContent.Children.Add(more);
    }

    private DockPanel Half(string label, double winRate, int matches)
    {
        var row = new DockPanel { MaxWidth = 560, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 1, 0, 1) };
        var name = new TextBlock { Text = label, Width = 110, Foreground = Res<Brush>("Muted"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        var value = new TextBlock { Text = $"승률 {winRate * 100:0}% ({matches}경기)", Width = 130, FontSize = 12, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(name, Dock.Left);
        DockPanel.SetDock(value, Dock.Right);
        row.Children.Add(name);
        row.Children.Add(value);
        row.Children.Add(new ProgressBar
        {
            Maximum = 1, Value = winRate, Height = 7, Width = 300, VerticalAlignment = VerticalAlignment.Center, BorderThickness = new Thickness(0),
            Background = Res<Brush>("Panel"), Foreground = Res<Brush>("Info"),
        });
        return row;
    }
}
