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
    private sealed record PageInfo(string Key, string Label, Func<FrameworkElement> Create);

    private readonly List<PageInfo> _pages;
    private readonly Dictionary<string, FrameworkElement> _created = [];
    private readonly Dictionary<string, RadioButton> _buttons = [];
    private readonly Dictionary<string, Image> _navIcons = [];

    public StudioWindow()
    {
        InitializeComponent();
        if (Skin.Brush("background", System.Windows.Media.Stretch.UniformToFill) is { } background) Background = background;
        _pages =
        [
            new("squad", "스쿼드 짜기", () => new SquadPage()),
            new("players", "선수 검색", () => new PlayerSearchPage()),
            new("picks", "숨은 랭커픽", () => new PicksPage()),
            new("value", "가성비 찾기", () => new ValuePage()),
            new("grade", "강화 효율", () => new GradePage()),
            new("salary", "급여 효율", () => new SalaryPage()),
            new("trends", "시세 추이", () => new TrendsPage()),
            new("mysquad", "내 스쿼드", () => new MySquadPage()),
            new("opponent", "상대 맞춤", () => new OpponentPage()),
            new("teamcolor", "팀컬러", () => new TeamColorPage()),
        ];
        foreach (var p in _pages)
        {
            var button = new RadioButton { Style = (Style)FindResource("NavItem"), GroupName = "nav", Content = NavContent(p) };
            System.Windows.Automation.AutomationProperties.SetName(button, p.Label); // screen readers and UI tests
            button.Checked += (_, _) =>
            {
                Show(p.Key);
                UpdateNavIcons(p.Key);
            };
            _buttons[p.Key] = button;
            Nav.Children.Add(button);
        }
        if (StudioKit.Squads?.Market is { } market)
        {
            market.Changed += OnMarketChanged;
            Closed += (_, _) => market.Changed -= OnMarketChanged;
        }
        PreviewKeyDown += (s, e) =>
        {
            if (e.Key == System.Windows.Input.Key.F11)
            {
                if (_created.TryGetValue("squad", out var page) && page is SquadPage sp && _buttons.TryGetValue("squad", out var b) && b.IsChecked == true)
                {
                    sp.ToggleFocus();
                    e.Handled = true;
                }
            }
        };
        ShowStatus();
        Navigate("squad");
    }

    private object NavContent(PageInfo p)
    {
        var icon = AppIcons.Make("nav-" + p.Key, 20);
        _navIcons[p.Key] = icon;
        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children = { icon, new TextBlock { Text = p.Label, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, FontSize = 13 } },
        };
    }

    private void UpdateNavIcons(string selectedKey)
    {
        foreach (var (k, icon) in _navIcons)
        {
            icon.Source = AppIcons.Get("nav-" + k, active: k == selectedKey);
        }
    }

    private WindowState _prevWindowState = WindowState.Normal;
    private bool _inFocusMode;

    public void SetFocusMode(bool on)
    {
        if (_inFocusMode == on) return;
        _inFocusMode = on;
        if (on)
        {
            _prevWindowState = WindowState;
            NavColumn.Width = new GridLength(0);
            NavBorder.Visibility = Visibility.Collapsed;
            FooterText.Visibility = Visibility.Collapsed;
            Host.Margin = new Thickness(0);
            WindowState = WindowState.Maximized;
        }
        else
        {
            NavColumn.Width = new GridLength(208);
            NavBorder.Visibility = Visibility.Visible;
            FooterText.Visibility = Visibility.Visible;
            Host.Margin = new Thickness(20, 16, 20, 8);
            WindowState = _prevWindowState;
        }
        if (_created.TryGetValue("squad", out var p) && p is SquadPage sp)
        {
            sp.SetFocusMode(on);
        }
    }

    /// <summary>Opens a page, optionally handing it something to do (e.g. lock a card in the squad builder).</summary>
    public void Navigate(string key, Action<FrameworkElement>? then = null)
    {
        if (_inFocusMode && key != "squad") SetFocusMode(false);
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
