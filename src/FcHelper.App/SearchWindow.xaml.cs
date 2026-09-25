using System.Net.Http;
using System.Windows;
using FcHelper.NexonApi;
using FcHelper.Services;

namespace FcHelper.App;

/// <summary>Nickname search and the opponent card. Created on demand and closed (not hidden) to free memory.</summary>
public partial class SearchWindow : Window
{
    private readonly App _app;
    private CancellationTokenSource? _lookup;
    private ReportView? _view;

    public SearchWindow(App app)
    {
        _app = app;
        InitializeComponent();
        Topmost = app.Settings.KeepCardOnTop;
        PlaceNearRightEdge();
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
    /// Only then may it stay on top: next to the game it covers nothing.
    /// </summary>
    /// <returns>True when the card fits beside the game without overlapping it.</returns>
    public bool PlaceBeside(Rect game)
    {
        const double gap = 8;
        var area = SystemParameters.WorkArea;
        var right = area.Right - game.Right - gap;
        var left = game.Left - area.Left - gap;
        Height = Math.Min(720, area.Height - 20);
        Top = Math.Max(area.Top + 10, Math.Min(game.Top, area.Bottom - Height - 10));

        if (right >= MinWidth || left >= MinWidth)
        {
            var useRight = right >= left;
            Width = Math.Min(440, useRight ? right : left);
            Left = useRight ? game.Right + gap : game.Left - gap - Width;
            Topmost = true;
            return true;
        }
        PlaceNearRightEdge();
        Topmost = _app.Settings.KeepCardOnTop;
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
        Card.Visibility = Visibility.Visible;
        TailorButton.Visibility = Visibility.Visible;
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
