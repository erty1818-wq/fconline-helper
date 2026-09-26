using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using FcHelper.NexonApi;
using FcHelper.Services;

namespace FcHelper.App;

/// <summary>Nickname search and the opponent card. Created on demand and closed (not hidden) to free memory.</summary>
public partial class SearchWindow : Window
{
    private readonly App _app;
    private CancellationTokenSource? _lookup;
    private ReportView? _view;

    /// <summary>The tabs after a search: key, label, panel. 내 전적 only shows for the user's own account (OS-18).</summary>
    private readonly List<(string Key, string Label, ScrollViewer Panel, ToggleButton Chip)> _tabs = [];
    private string _tab = "summary";
    /// <summary>Width the deeper tabs open at, so the pitch and charts have room (the 요약 card stays narrow).</summary>
    private const double WideWidth = 760;

    public SearchWindow(App app)
    {
        _app = app;
        InitializeComponent();
        // Never always-on-top: it would cover the game (the user's rule). It opens beside the game window instead.
        Topmost = false;
        if (app.Settings.SearchWidth is { } w && w >= MinWidth) Width = w;
        if (app.Settings.SearchHeight is { } h && h >= MinHeight) Height = h;
        PlaceNearRightEdge();
        BuildTabs();
        // The size the user leaves the window at is the size it opens with next time.
        Closing += (_, _) =>
        {
            if (WindowState != WindowState.Normal) return;
            app.Settings.SearchWidth = Math.Round(ActualWidth);
            app.Settings.SearchHeight = Math.Round(ActualHeight);
            app.Settings.Save();
        };
        // Opened by the capture hotkey, the card must not pull keyboard focus away from the game.
        Loaded += (_, _) => { if (ShowActivated) FocusSearchBox(); };
        Closed += (_, _) => _lookup?.Cancel();
        // Esc gets the card out of the way at once; Ctrl+Alt+S brings it back.
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != System.Windows.Input.Key.Escape) return;
            e.Handled = true;
            Close();
        };
    }

    public void FocusSearchBox()
    {
        NicknameBox.Focus();
        NicknameBox.SelectAll();
    }

    public void Search(string nickname)
    {
        NicknameBox.Text = nickname;
        OnSearchClick(this, new RoutedEventArgs());
    }

    /// <summary>Shows a message in the status line and puts a best guess in the search box, e.g. after OCR failed.</summary>
    public void Prompt(string status, string? nickname = null)
    {
        if (nickname is not null) NicknameBox.Text = nickname;
        StatusText.Text = status;
    }

    private void PlaceNearRightEdge()
    {
        var area = SystemParameters.WorkArea;
        Height = Math.Min(Height, area.Height - 40);
        Left = area.Right - Width - 20;
        Top = area.Top + 20;
    }

    /// <summary>
    /// Moves the card next to the game (<paramref name="game"/> in device-independent units) when there is room.
    /// Beside the game it covers nothing, so it never needs to be on top.
    /// </summary>
    /// <returns>True when the card fits beside the game without overlapping it.</returns>
    public bool PlaceBeside(Rect game)
    {
        const double gap = 8;
        var area = SystemParameters.WorkArea;
        var right = area.Right - game.Right - gap;
        var left = game.Left - area.Left - gap;
        Height = Math.Min(_app.Settings.SearchHeight ?? 720, area.Height - 20);
        Top = Math.Max(area.Top + 10, Math.Min(game.Top, area.Bottom - Height - 10));

        if (right >= MinWidth || left >= MinWidth)
        {
            var useRight = right >= left;
            Width = Math.Min(_app.Settings.SearchWidth ?? 440, useRight ? right : left);
            Left = useRight ? game.Right + gap : game.Left - gap - Width;
            return true;
        }
        PlaceNearRightEdge();
        return false;
    }

    private async void OnSearchClick(object sender, RoutedEventArgs e)
    {
        var nickname = NicknameBox.Text.Trim();
        if (nickname.Length == 0) return;

        var service = _app.Service;
        if (service is null)
        {
            StatusText.Text = "먼저 홈 화면에서 NEXON Open API 키를 입력하세요.";
            _app.ShowHome();
            return;
        }

        _lookup?.Cancel();
        var cts = _lookup = new CancellationTokenSource();
        SearchButton.IsEnabled = false;
        StatusText.Text = "검색 중…";

        // Progress<T> captures the UI thread, so the card can be updated directly from the callback.
        var progress = new Progress<LookupProgress>(p =>
        {
            if (cts.IsCancellationRequested) return;
            StatusText.Text = p.Stage switch
            {
                LookupStage.ResolvingUser => "닉네임 확인 중…",
                LookupStage.ShowingCache => "저장된 기록 표시 중 · 새 경기 확인 중…",
                LookupStage.FetchingMatches => $"경기 불러오는 중 {p.Fetched}/{p.ToFetch}",
                _ => "",
            };
            if (p.Report is not null) Show(p.Report);
        });

        try
        {
            var report = await service.LookupAsync(nickname, progress, cts.Token);
            if (cts.IsCancellationRequested) return;
            if (report is null)
            {
                StatusText.Text = $"'{nickname}' 닉네임을 찾지 못했습니다.";
                return;
            }
            Show(report);
            StatusText.Text = report.IsComplete
                ? $"완료 · API 호출 누적 {_app.ApiCallCount}회"
                : $"일부만 불러왔습니다 ({report.LoadedMatches}/{report.RequestedMatches}). 호출 한도나 연결 상태를 확인하세요.";
            _app.Brief(report);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
        }
        catch (NexonApiException ex) when (ex.IsAuthError)
        {
            StatusText.Text = "API 키가 올바르지 않습니다. 설정에서 다시 입력하세요.";
        }
        catch (NexonApiException ex)
        {
            StatusText.Text = $"NEXON Open API 오류: {ex.ErrorName}";
        }
        catch (HttpRequestException)
        {
            StatusText.Text = "NEXON Open API에 연결하지 못했습니다. 인터넷 연결을 확인하세요.";
        }
        finally
        {
            if (ReferenceEquals(_lookup, cts)) SearchButton.IsEnabled = true;
        }
    }

    private void Show(OpponentReport report)
    {
        _view = new ReportView(report);
        Card.DataContext = _view;
        StartPanel.Visibility = Visibility.Collapsed;
        TabBar.Visibility = Visibility.Visible;
        TailorButton.Visibility = Visibility.Visible;
        SelectTab(_tab, widen: false);
    }

    // ── tabs ──

    private void BuildTabs()
    {
        foreach (var (key, label, panel) in new (string, string, ScrollViewer)[]
                 {
                     ("summary", "요약", SummaryTab), ("squad", "스쿼드", SquadTab), ("shots", "슈팅", ShotsTab), ("flow", "흐름", FlowTab),
                     ("compare", "비교", CompareTab), ("players", "선수", PlayersTab), ("mine", "내 전적", MineTab),
                 })
        {
            var chip = new ToggleButton { Content = label, Style = (Style)FindResource("Chip"), Margin = new Thickness(0, 0, 6, 4) };
            chip.Click += (_, _) => SelectTab(key, widen: true);
            _tabs.Add((key, label, panel, chip));
            TabBar.Children.Add(chip);
            if (key != "summary") (panel.Content as StackPanel)?.Children.Add(Placeholder(label));
        }
        // 내 전적 is for the user's own account; it appears when OS-18 fills it.
        _tabs.First(t => t.Key == "mine").Chip.Visibility = Visibility.Collapsed;
    }

    private TextBlock Placeholder(string label) => new()
    {
        Text = $"{label} 탭은 준비 중입니다 (docs/opponent-search/PLAN.md).", Style = (Style)FindResource("Hint"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0),
    };

    /// <summary>Shows one tab; the deeper ones widen a narrow window once (moving it left if the screen edge is near).</summary>
    private void SelectTab(string key, bool widen)
    {
        _tab = key;
        foreach (var t in _tabs)
        {
            t.Panel.Visibility = t.Key == key ? Visibility.Visible : Visibility.Collapsed;
            t.Chip.IsChecked = t.Key == key;
        }
        if (!widen || key == "summary" || WindowState != WindowState.Normal || ActualWidth >= WideWidth - 1) return;
        var area = SystemParameters.WorkArea;
        var width = Math.Min(WideWidth, area.Width - 20);
        var right = Left + ActualWidth;
        Width = width;
        // Grow towards the free side: keep the right edge when the window sits at the right of the screen.
        Left = right > area.Right - 40 ? Math.Max(area.Left + 10, right - width) : Math.Min(Left, area.Right - width - 10);
    }

    private void OnTailorClick(object sender, RoutedEventArgs e)
    {
        if (_view is null) return;
        var nickname = _view.Report.Nickname;
        _app.ShowStudio("opponent", page => ((Studio.OpponentPage)page).Analyse(nickname));
    }

    private void OnSaveMemoClick(object sender, RoutedEventArgs e)
    {
        if (_view is null || _app.Service is null) return;
        _app.Service.SaveMemo(_view.Report.Ouid, _view.MemoText, _view.Tags.Where(t => t.IsChecked).Select(t => t.Name));
        StatusText.Text = "메모를 저장했습니다.";
    }
}
