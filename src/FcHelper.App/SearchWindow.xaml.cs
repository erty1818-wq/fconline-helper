using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
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
    private OpponentReport? _report;
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
        RefreshStart();
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
        SuggestBar.Visibility = Visibility.Collapsed;
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

    private void OnSearchClick(object sender, RoutedEventArgs e) => RunSearch(NicknameBox.Text.Trim(), refresh: false);

    private void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        if (_shown is not null) RunSearch(_shown, refresh: true);
    }

    private async void RunSearch(string nickname, bool refresh)
    {
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
        RefreshButton.IsEnabled = false;
        StatusText.Text = refresh ? "새 경기 확인 중…" : "검색 중…";

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
            var report = await service.LookupAsync(nickname, progress, cts.Token, refresh);
            if (cts.IsCancellationRequested) return;
            if (report is null)
            {
                StatusText.Text = $"'{nickname}' 닉네임을 찾지 못했습니다.";
                return;
            }
            Show(report);
            History?.AddRecent(report.Nickname);
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
            if (ReferenceEquals(_lookup, cts)) SearchButton.IsEnabled = RefreshButton.IsEnabled = true;
        }
    }

    private void Show(OpponentReport report)
    {
        _report = report;
        _shown = report.Nickname;
        UpdateFavoriteButton();
        _view = new ReportView(report);
        Card.DataContext = _view;
        StartPanel.Visibility = Visibility.Collapsed;
        TabRow.Visibility = Visibility.Visible;
        CheckedText.Text = report.CheckedAt is { } at ? $"{Ago(at)} 갱신" : "갱신 안 됨";
        CheckedText.ToolTip = report.CheckedAt is { } t ? $"새 경기 확인: {t.ToLocalTime():M월 d일 HH:mm}" : "새 경기 확인을 끝내지 못했습니다. [갱신]을 눌러 다시 확인하세요.";
        TailorButton.Visibility = Visibility.Visible;
        SelectTab(_tab, widen: false);
    }

    // ── start screen (before a search): favourites, recent searches, recent opponents, autocomplete ──

    private string? _shown;
    private SearchHistory? History => _app.Db is { } db ? new SearchHistory(db) : null;

    /// <summary>Back from a report to the start lists.</summary>
    private void ShowStart()
    {
        RefreshStart();
        StartPanel.Visibility = Visibility.Visible;
        TabRow.Visibility = Visibility.Collapsed;
        TailorButton.Visibility = Visibility.Collapsed;
        foreach (var t in _tabs) t.Panel.Visibility = Visibility.Collapsed;
        FocusSearchBox();
    }

    private void RefreshStart()
    {
        StartLists.Children.Clear();
        if (_app.Db is not { } db || History is not { } history) return;

        var favorites = history.Favorites;
        if (favorites.Count > 0)
        {
            StartLists.Children.Add(SectionTitle("즐겨찾기"));
            foreach (var n in favorites) StartLists.Children.Add(NameRow(n, null, favorite: true));
        }

        var recent = history.Recent.Where(n => !history.IsFavorite(n)).ToList();
        if (recent.Count > 0)
        {
            StartLists.Children.Add(SectionTitle("최근 검색"));
            foreach (var n in recent) StartLists.Children.Add(NameRow(n, null, favorite: false, removable: true));
        }

        StartLists.Children.Add(SectionTitle("최근 상대"));
        var me = string.IsNullOrWhiteSpace(_app.Settings.MyNickname) ? null : db.FindUserByNickname(_app.Settings.MyNickname);
        var opponents = me is null ? [] : db.RecentOpponents(me.Ouid, limit: 10);
        if (opponents.Count == 0)
        {
            StartLists.Children.Add(new TextBlock
            {
                Style = (Style)FindResource("Hint"), TextWrapping = TextWrapping.Wrap,
                Text = me is null
                    ? "설정에서 내 닉네임을 넣고 트레이 메뉴의 [내 경기 동기화]를 하면 최근에 만난 상대가 여기에 나옵니다."
                    : "저장된 내 경기가 아직 없습니다. 트레이 메뉴의 [내 경기 동기화]를 해 보세요.",
            });
        }
        foreach (var o in opponents)
        {
            var detail = $"내 전적 {o.Wins}승 {o.Draws}무 {o.Losses}패 · {Ago(o.LastPlayed)}";
            StartLists.Children.Add(NameRow(o.Nickname, detail, favorite: history.IsFavorite(o.Nickname)));
        }
    }

    private TextBlock SectionTitle(string text) => new()
    {
        Text = text, Style = (Style)FindResource("FieldLabel"), Margin = new Thickness(0, 10, 0, 4),
    };

    /// <summary>One nickname: click to search, the star to (un)star it, the cross to drop it from the recent list.</summary>
    private DockPanel NameRow(string nickname, string? detail, bool favorite, bool removable = false)
    {
        var row = new DockPanel { Margin = new Thickness(0, 1, 0, 1) };
        var star = IconButton("Icon.Star", favorite, favorite ? "즐겨찾기에서 빼기" : "즐겨찾기에 넣기");
        star.Click += (_, _) => { History?.ToggleFavorite(nickname); RefreshStart(); };
        DockPanel.SetDock(star, Dock.Left);
        row.Children.Add(star);
        if (removable)
        {
            var remove = IconButton("Icon.Close", false, "최근 검색에서 지우기");
            remove.Click += (_, _) => { History?.RemoveRecent(nickname); RefreshStart(); };
            DockPanel.SetDock(remove, Dock.Right);
            row.Children.Add(remove);
        }
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = nickname, FontWeight = FontWeights.SemiBold });
        if (detail is not null) text.Children.Add(new TextBlock { Text = detail, Foreground = (Brush)FindResource("Muted"), FontSize = 11 });
        var open = new Button
        {
            Content = text, Style = (Style)FindResource("Ghost"), HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(6, 3, 6, 3), ToolTip = $"{nickname} 검색",
        };
        open.Click += (_, _) => Search(nickname);
        row.Children.Add(open);
        return row;
    }

    private Button IconButton(string icon, bool active, string tip) => new()
    {
        Content = AppIcons.Make(icon, 14, active), Style = (Style)FindResource("Ghost"), Padding = new Thickness(4),
        VerticalAlignment = VerticalAlignment.Center, ToolTip = tip,
    };

    private static string Ago(DateTime date)
    {
        var span = DateTime.UtcNow - (date.Kind == DateTimeKind.Local ? date.ToUniversalTime() : date);
        return span.TotalHours < 1 ? $"{Math.Max(1, (int)span.TotalMinutes)}분 전"
            : span.TotalDays < 1 ? $"{(int)span.TotalHours}시간 전"
            : $"{(int)span.TotalDays}일 전";
    }

    private void OnFavoriteClick(object sender, RoutedEventArgs e)
    {
        if (_shown is null || History is not { } history) return;
        history.ToggleFavorite(_shown);
        UpdateFavoriteButton();
    }

    private void UpdateFavoriteButton()
    {
        var on = _shown is not null && History?.IsFavorite(_shown) == true;
        FavoriteButton.Content = AppIcons.Make("Icon.Star", 16, on);
        FavoriteButton.ToolTip = on ? "즐겨찾기에서 빼기" : "즐겨찾기에 넣기";
    }

    /// <summary>Autocomplete from the local DB only (no API call per keystroke).</summary>
    private void OnNicknameChanged(object sender, TextChangedEventArgs e)
    {
        SuggestBar.Children.Clear();
        var typed = NicknameBox.Text.Trim();
        List<string> hits = typed.Length == 0 || _app.Db is not { } db
            ? []
            : db.SuggestNicknames(typed, 6).Where(n => !string.Equals(n, typed, StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var n in hits)
        {
            var b = new Button { Content = n, Style = (Style)FindResource("Ghost"), Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 4, 2), ToolTip = $"{n} 검색" };
            b.Click += (_, _) => Search(n);
            SuggestBar.Children.Add(b);
        }
        SuggestBar.Visibility = hits.Count > 0 && NicknameBox.IsKeyboardFocusWithin ? Visibility.Visible : Visibility.Collapsed;
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
        var back = new Button { Content = "처음 화면", Style = (Style)FindResource("Ghost"), Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 0, 4), ToolTip = "즐겨찾기 · 최근 검색 · 최근 상대" };
        back.Click += (_, _) => ShowStart();
        TabBar.Children.Add(back);
    }

    /// <summary>The deeper tabs are filled only when opened (and again when a newer report arrives while open).</summary>
    private async void OnTabShown(string key)
    {
        if (_report is not { } r || StartPanel.Visibility == Visibility.Visible) return;
        switch (key)
        {
            case "squad": await ShowSquadAsync(r); break;
            case "shots": ShowShots(r); break;
        }
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
        OnTabShown(key);
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
