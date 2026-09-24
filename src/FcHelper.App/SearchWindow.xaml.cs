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
        Loaded += (_, _) => FocusSearchBox();
        Closed += (_, _) => _lookup?.Cancel();
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

    private void PlaceNearRightEdge()
    {
        var area = SystemParameters.WorkArea;
        Height = Math.Min(Height, area.Height - 40);
        Left = area.Right - Width - 20;
        Top = area.Top + 20;
    }

    private async void OnSearchClick(object sender, RoutedEventArgs e)
    {
        var nickname = NicknameBox.Text.Trim();
        if (nickname.Length == 0) return;

        var service = _app.Service;
        if (service is null)
        {
            StatusText.Text = "먼저 설정에서 NEXON Open API 키를 입력하세요.";
            _app.ShowSettings();
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
        CopyButton.IsEnabled = true;
    }

    private void OnSaveMemoClick(object sender, RoutedEventArgs e)
    {
        if (_view is null || _app.Service is null) return;
        _app.Service.SaveMemo(_view.Report.Ouid, _view.MemoText, _view.Tags.Where(t => t.IsChecked).Select(t => t.Name));
        StatusText.Text = "메모를 저장했습니다.";
    }

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        if (_view is null) return;
        try
        {
            Clipboard.SetText(ReportText.Card(_view.Report));
            StatusText.Text = "카드를 클립보드에 복사했습니다.";
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // Another program holds the clipboard open.
            StatusText.Text = "클립보드를 쓸 수 없습니다. 잠시 후 다시 시도하세요.";
        }
    }
}
