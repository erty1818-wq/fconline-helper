using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace FcHelper.App.Studio;

/// <summary>
/// One window for every squad and market tool, with a navigation rail. Pages are created on first visit and kept
/// until the window closes (then everything is released).
/// </summary>
public partial class StudioWindow : Window
{
    private sealed record PageInfo(string Key, string Label, string Glyph, Func<FrameworkElement> Create);

    private readonly List<PageInfo> _pages;
    private readonly Dictionary<string, FrameworkElement> _created = [];
    private readonly Dictionary<string, RadioButton> _buttons = [];

    public StudioWindow()
    {
        InitializeComponent();
        _pages =
        [
            new("squad", "스쿼드 짜기", "", () => new SquadPage()),
            new("picks", "숨은 랭커픽", "", () => new PicksPage()),
            new("value", "가성비 찾기", "", () => new ValuePage()),
            new("grade", "강화 효율", "", () => new GradePage()),
            new("salary", "급여 효율", "", () => new SalaryPage()),
            new("trends", "시세 추이", "", () => new TrendsPage()),
            new("mysquad", "내 스쿼드", "", () => new MySquadPage()),
            new("opponent", "상대 맞춤", "", () => new OpponentPage()),
            new("teamcolor", "팀컬러", "", () => new TeamColorPage()),
        ];
        foreach (var p in _pages)
        {
            var button = new RadioButton { Style = (Style)FindResource("NavItem"), GroupName = "nav", Content = NavContent(p) };
            System.Windows.Automation.AutomationProperties.SetName(button, p.Label); // screen readers and UI tests
            button.Checked += (_, _) => Show(p.Key);
            _buttons[p.Key] = button;
            Nav.Children.Add(button);
        }
        if (StudioKit.Squads?.Market is { } market)
        {
            market.Changed += OnMarketChanged;
            Closed += (_, _) => market.Changed -= OnMarketChanged;
        }
        ShowStatus();
        Navigate("squad");
    }

    private static object NavContent(PageInfo p)
    {
        var key = "nav-" + p.Key;
        FrameworkElement icon = Skin.HasCustom(key)
            ? new Image { Source = Skin.Get(key), Width = 20, Height = 20 }
            : new TextBlock { Text = p.Glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 16, Width = 20, VerticalAlignment = VerticalAlignment.Center };
        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children = { icon, new TextBlock { Text = p.Label, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, FontSize = 13 } },
        };
    }

    /// <summary>Opens a page, optionally handing it something to do (e.g. lock a card in the squad builder).</summary>
    public void Navigate(string key, Action<FrameworkElement>? then = null)
    {
        _buttons[key].IsChecked = true;
        Show(key);
        then?.Invoke(_created[key]);
    }

    private void Show(string key)
    {
        if (!_created.TryGetValue(key, out var page)) _created[key] = page = _pages.First(p => p.Key == key).Create();
        Host.Content = page;
    }

    private void OnMarketChanged() => Dispatcher.BeginInvoke(ShowStatus);

    private void ShowStatus()
    {
        if (StudioKit.Squads?.Market is not { } market)
        {
            DataStatus.Text = "준비 중…";
            return;
        }
        var s = market.Status;
        var current = s.Current?.FinishedAt is { } at ? $"시세 {at.ToLocalTime():M/d HH:mm} 기준 · {s.Cards:#,0}장" : "시세 데이터 없음";
        DataStatus.Text = s.Running is { } r
            ? $"{current}\n갱신 중 {r.Done}/{r.Total}" + (s.Current is null ? " (처음은 약 20분)" : "")
            : s.LastError is { } err ? $"{current}\n{err}" : current;
        RefreshButton.IsEnabled = s.Running is null;
    }

    private async void OnRefresh(object sender, RoutedEventArgs e)
    {
        if (StudioKit.Squads?.Market is not { } market) return;
        RefreshButton.IsEnabled = false;
        await market.RefreshIfDueAsync(force: true);
    }
}
