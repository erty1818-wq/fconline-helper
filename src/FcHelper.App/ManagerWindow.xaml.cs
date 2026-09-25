using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using FcHelper.Market;
using FcHelper.NexonApi;

namespace FcHelper.App;

public sealed record PickRateRow(int Rank, string Name, int Rankers, string Share, string Average, string Richest, string Poorest);

public sealed record CoachPlayerRow(string Position, string Name, string Grade, int Games, string Win, string Attack, double AttackValue,
    string Defence, double DefenceValue, string Goals, string Assists, string Shots, string Pass, string Tackles, string Blocks, string Intercepts, string Rating);

public sealed record FormationRow(string Lines, string Win, string Record);

public sealed record HoneyRow(string Honey, string Name, string Season, int Ovr, string Price, int Matches, string Goals, string Assists, string Shots,
    string Passes, string Tackles, string Blocks, string Score, string Expected, string Ratio, double RatioValue);

/// <summary>
/// 감독모드: which team colours top manager-mode rankers run and whom they field per position, and one coach's last
/// manager-mode matches (record, per-card numbers, formations). Official ranking and Open API only.
/// </summary>
public partial class ManagerWindow : Window
{
    private readonly App _app;

    public ManagerWindow(App app)
    {
        _app = app;
        InitializeComponent();
        if (Skin.Brush("background", System.Windows.Media.Stretch.UniformToFill) is { } background) Background = background;
        NicknameBox.Text = app.Settings.MyNickname ?? "";
        HoneyPosition.ItemsSource = new[] { "ST", "CF", "LW", "CAM", "LM", "CM", "CDM", "LB", "CB" };
        HoneyPosition.SelectedIndex = 0;
        HoneyGrade.ItemsSource = Grades.Tradable.Select(g => $"+{g}").ToList();
        HoneyGrade.SelectedIndex = 7;
        Loaded += (_, _) => OnLoadPicks(this, new RoutedEventArgs());
    }

    private void OnTab(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized || PicksTab is null || CoachTab is null || HoneyTab is null) return;
        PicksTab.IsChecked = ReferenceEquals(sender, PicksTab);
        HoneyTab.IsChecked = ReferenceEquals(sender, HoneyTab);
        CoachTab.IsChecked = ReferenceEquals(sender, CoachTab);
        PicksView.Visibility = PicksTab.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        HoneyView.Visibility = HoneyTab.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        CoachView.Visibility = CoachTab.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Shows the coach tab for a nickname (from the home screen or search).</summary>
    public void Analyse(string nickname)
    {
        NicknameBox.Text = nickname;
        OnTab(CoachTab, new RoutedEventArgs());
        OnAnalyse(this, new RoutedEventArgs());
    }

    private async void OnLoadPicks(object sender, RoutedEventArgs e)
    {
        if (_app.Squads is not { } squads) return;
        PicksButton.IsEnabled = false;
        try
        {
            var rates = await squads.ManagerPickRatesAsync(new Progress<string>(m => Status.Text = m));
            PickRates.ItemsSource = rates.Select((r, i) => new PickRateRow(i + 1, r.TeamColor, r.Rankers, $"{r.Share:P1}", Bp.Format(r.AverageValue),
                $"{r.Richest.Nickname} ({Bp.Format(r.Richest.TeamValue)})", $"{r.Poorest.Nickname} ({Bp.Format(r.Poorest.TeamValue)})")).ToList();
            Status.Text = rates.Count == 0 ? "랭킹을 받지 못했습니다." : $"감독모드 랭킹 상위 {rates.Sum(r => r.Rankers):#,0}명 기준 · 팀컬러를 누르면 포지션별 인기 선수가 나옵니다.";
            if (rates.Count > 0) PickRates.SelectedIndex = 0;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Status.Text = "데이터센터에 연결하지 못했습니다.";
        }
        finally
        {
            PicksButton.IsEnabled = true;
        }
    }

    private async void OnPickSelected(object sender, SelectionChangedEventArgs e)
    {
        if (PickRates.SelectedItem is not PickRateRow row || _app.Squads is not { } squads) return;
        TeamTitle.Text = row.Name;
        TeamPlayers.Children.Clear();
        TeamPlayers.Children.Add(new TextBlock { Text = "랭커 스쿼드 불러오는 중… (처음 한 번은 몇 분 걸립니다)", Style = (Style)FindResource("Hint") });
        try
        {
            var team = await squads.ManagerTeamPlayersAsync(row.Name, new Progress<string>(m => Status.Text = m),
                nameOf: id => _app.Db?.GetPlayerNames([(int)id]).GetValueOrDefault((int)id));
            if ((PickRates.SelectedItem as PickRateRow)?.Name != row.Name) return;
            TeamPlayers.Children.Clear();
            TeamHint.Text = team.Squads == 0
                ? "표본의 랭커 스쿼드 중 이 팀컬러가 없습니다 (상위 150명 기준)."
                : $"이 팀컬러 랭커 {team.Squads}명의 최근 감독모드 경기 기준 · 비율 = 그중 이 선수를 쓴 랭커";
            foreach (var role in new[] { "ST", "CF", "W", "CAM", "SM", "CM", "CDM", "FB", "CB", "GK" })
            {
                if (!team.ByRole.TryGetValue(role, out var picks) || picks.Count == 0) continue;
                var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
                panel.Children.Add(new TextBlock { Text = RankerAllocation.RoleName(role), Style = (Style)FindResource("FieldLabel"), Foreground = (System.Windows.Media.Brush)FindResource("Accent") });
                foreach (var p in picks)
                    panel.Children.Add(new DockPanel
                    {
                        Margin = new Thickness(0, 3, 0, 0),
                        Children =
                        {
                            new TextBlock { Text = $"{p.Users}명 · {p.Share:P1}", Style = (Style)FindResource("Hint") }.Also(t => DockPanel.SetDock(t, Dock.Right)),
                            new TextBlock { Text = $"{p.Season} {p.Name} +{p.Grade}", TextTrimming = TextTrimming.CharacterEllipsis },
                        },
                    });
                TeamPlayers.Children.Add(panel);
            }
            Status.Text = "";
        }
        catch (InvalidOperationException ex)
        {
            TeamPlayers.Children.Clear();
            TeamPlayers.Children.Add(new TextBlock { Text = ex.Message, Style = (Style)FindResource("Hint"), TextWrapping = TextWrapping.Wrap });
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or NexonApiException)
        {
            TeamPlayers.Children.Clear();
            TeamPlayers.Children.Add(new TextBlock { Text = "랭커 스쿼드를 받지 못했습니다 (API 호출 한도나 연결을 확인하세요).", Style = (Style)FindResource("Hint"), TextWrapping = TextWrapping.Wrap });
        }
    }

    private async void OnHoney(object sender, RoutedEventArgs e)
    {
        if (_app.Squads is not { } squads) return;
        if (!Bp.TryParse(HoneyMin.Text, out var min) && HoneyMin.Text.Trim().Length > 0 || !Bp.TryParse(HoneyMax.Text, out var max) && HoneyMax.Text.Trim().Length > 0)
        {
            Status.Text = "가격은 1억, 5000만처럼 입력하세요.";
            return;
        }
        if (HoneyMax.Text.Trim().Length == 0) max = long.MaxValue;
        if (HoneyMin.Text.Trim().Length == 0) min = 0;
        var position = (string)HoneyPosition.SelectedItem;
        var grade = HoneyGrade.SelectedIndex + 1;
        HoneyButton.IsEnabled = false;
        try
        {
            var found = await squads.ManagerHoneyAsync(position, grade, min, max, int.TryParse(HoneyOvr.Text, out var o) ? o : 0,
                int.TryParse(HoneyMatches.Text, out var m) ? m : 20, new Progress<string>(t => Status.Text = t));
            HoneyResults.ItemsSource = found.Select(h => new HoneyRow(h.IsHoney ? Honey.Mark : "", h.Card.Name, h.Card.Season, h.Ovr, Bp.Format(h.Price),
                h.Stats.MatchCount, $"{h.Stats.Goal:0.00}", $"{h.Stats.Assist:0.00}", $"{h.Stats.EffectiveShoot:0.0}", $"{h.Stats.PassSuccess:0.0}",
                $"{h.Stats.Tackle:0.0}", $"{h.Stats.Block:0.0}", $"{h.Score:0.0}", $"{h.Expected:0.0}", $"{h.Ratio:0.00}배", h.Ratio)).ToList();
            Status.Text = found.Count == 0
                ? "조건에 맞고 감독모드 랭커 기록이 있는 카드가 없습니다. 가격대를 넓히거나 최소 경기 수를 낮춰 보세요."
                : $"{position} +{grade} · {found.Count}장 · 꿀선수 {found.Count(h => h.IsHoney)}장 · 활약 점수 = 경기당 골×10 + 도움×7 + 유효 슈팅×2 + … [계산], 가격 대비 = 같은 가격대 평균과 비교";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or NexonApiException)
        {
            Status.Text = "감독모드 랭커 기록을 받지 못했습니다 (API 호출 한도나 연결을 확인하세요).";
        }
        finally
        {
            HoneyButton.IsEnabled = true;
        }
    }

    private void OnNicknameKey(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter) OnAnalyse(sender, e);
    }

    private async void OnAnalyse(object sender, RoutedEventArgs e)
    {
        var nick = NicknameBox.Text.Trim();
        if (nick.Length == 0) { Status.Text = "구단주 닉네임을 입력하세요."; return; }
        if (_app.Service is not { } service) { Status.Text = "먼저 홈 화면에서 NEXON Open API 키를 입력하세요."; return; }
        var count = int.TryParse(CountBox.Text, out var c) ? Math.Clamp(c, 1, 100) : 100;
        AnalyseButton.IsEnabled = false;
        try
        {
            var report = await service.ManagerAnalysisAsync(nick, count, new Progress<string>(m => Status.Text = m));
            if (report is null) { Status.Text = $"'{nick}' 닉네임을 찾지 못했습니다."; return; }
            RecordText.Text = $"{report.Wins}승 {report.Draws}무 {report.Losses}패";
            WinText.Text = $"{report.WinRate:P1}";
            ForText.Text = $"{report.GoalsFor:0.00}";
            AgainstText.Text = $"{report.GoalsAgainst:0.00}";
            Players.ItemsSource = report.Players.Select(p => new CoachPlayerRow(p.Position, p.Name, $"+{p.Grade}", p.Games, $"{p.WinRate:P1}",
                $"{p.Attack:0.0}", p.Attack, $"{p.Defence:0.0}", p.Defence, $"{p.Goals:0.00}", $"{p.Assists:0.00}", $"{p.ShotsOnTarget:0.0}",
                $"{p.PassRate:P0}", $"{p.Tackles:0.0}", $"{p.Blocks:0.0}", $"{p.Interceptions:0.0}", $"{p.Rating:0.0}")).ToList();
            Formations.ItemsSource = report.Formations.Select(f => new FormationRow(f.Lines, $"승률 {f.WinRate:P1}", $"{f.Games}경기 · {f.Wins}승 {f.Draws}무 {f.Losses}패")).ToList();
            Status.Text = $"{report.Nickname} · 감독모드 최근 {report.Games}경기 (Data based on NEXON Open API)";
        }
        catch (NexonApiException ex) when (ex.IsAuthError)
        {
            Status.Text = "API 키가 올바르지 않습니다.";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or NexonApiException)
        {
            Status.Text = "NEXON Open API에서 경기를 받지 못했습니다. 호출 한도나 연결을 확인하세요.";
        }
        finally
        {
            AnalyseButton.IsEnabled = true;
        }
    }
}

internal static class ElementExtensions
{
    public static T Also<T>(this T element, Action<T> setup) { setup(element); return element; }
}
