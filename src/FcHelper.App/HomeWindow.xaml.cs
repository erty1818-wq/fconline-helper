using System.Diagnostics;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FcHelper.NexonApi;

namespace FcHelper.App;

/// <summary>
/// The first screen: 구단주 검색 (opponent analysis, needs the API key) or 스쿼드 도우미 (market and squads, public data).
/// On first run it also takes the API key and the user's nickname, and checks both against the API right away.
/// </summary>
public partial class HomeWindow : Window
{
    private readonly App _app;
    /// <summary>Keeps the setup panel open after a key that works with a nickname that was not found.</summary>
    private bool _keepSetup;

    public HomeWindow(App app)
    {
        _app = app;
        InitializeComponent();
        SearchIcon.Content = AppIcons.Make("Icon.Home.Search", 44);
        SquadIcon.Content = AppIcons.Make("Icon.Home.Squad", 44);
        ManagerIcon.Content = AppIcons.Make("Icon.Home.Manager", 44);
        // Replaceable artwork (docs/SKIN_ASSETS.md): card backgrounds and the window background.
        if (Skin.Brush("home-card") is { } card)
            foreach (var entry in new Control[] { SearchEntry, SquadEntry, ManagerEntry }) entry.Background = card;
        if (Skin.Brush("background", Stretch.UniformToFill) is { } background) Background = background;
        Refresh();
        Activated += (_, _) => Refresh();
        // First run without a key (e.g. a friend's PC): the step-by-step guide opens once by itself.
        Loaded += (_, _) =>
        {
            if (_app.Service is not null || _app.Settings.ApiGuideShown) return;
            _app.Settings.ApiGuideShown = true;
            _app.Settings.Save();
            Dispatcher.BeginInvoke(ShowApiGuide, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        };
        if (app.Squads?.Market is { } market)
        {
            market.Changed += OnMarketChanged;
            Closed += (_, _) => market.Changed -= OnMarketChanged;
        }
    }

    private void OnMarketChanged() => Dispatcher.BeginInvoke(Refresh);

    private void OnApiGuide(object sender, RoutedEventArgs e) => ShowApiGuide();

    private void ShowApiGuide()
    {
        new ApiGuideWindow { Owner = this }.ShowDialog();
        KeyBox.Focus();
    }

    /// <summary>Shows who is set up, and the first-run panel while there is no key.</summary>
    public void Refresh()
    {
        var hasKey = _app.Service is not null;
        var nick = _app.Settings.MyNickname;
        Who.Text = hasKey
            ? (string.IsNullOrWhiteSpace(nick) ? "API 연결됨 · 내 닉네임은 설정에서 넣을 수 있습니다" : $"{nick} · API 연결됨")
            : "API 키가 필요합니다 (스쿼드 도우미는 키 없이도 됩니다)";
        Setup.Visibility = hasKey && !_keepSetup ? Visibility.Collapsed : Visibility.Visible;
        if (!hasKey && KeyBox.Password.Length == 0)
        {
            // The key the user set for the command line, if any: they only need to confirm it.
            KeyBox.Password = Environment.GetEnvironmentVariable("FCH_API_KEY") ?? "";
            NicknameBox.Text = nick ?? "";
        }
        SearchEntry.IsEnabled = hasKey;
        var status = _app.Squads?.Market.Status;
        MarketLine.Text = status?.Running is { } r ? $"시세 갱신 중 {r.Done}/{r.Total}"
            : status?.Current is { FinishedAt: { } at } ? $"시세 {at.ToLocalTime():M/d HH:mm} 기준 · 카드 {status.Cards:#,0}장"
            : "시세 데이터 준비 중 (처음에는 40분쯤 걸립니다)";
    }

    private void OnSearch(object sender, RoutedEventArgs e) => _app.ShowSearch();

    private void OnSquad(object sender, RoutedEventArgs e) => _app.ShowStudio();

    private void OnManager(object sender, RoutedEventArgs e) => _app.ShowManager();

    private void OnSettings(object sender, RoutedEventArgs e)
    {
        _app.ShowSettings();
        Refresh();
    }

    private void OnOpenLink(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }

    private async void OnStart(object sender, RoutedEventArgs e)
    {
        var key = KeyBox.Password.Trim();
        var nick = NicknameBox.Text.Trim();
        if (key.Length == 0) { SetupStatus.Text = "API 키를 붙여 넣으세요."; return; }
        StartButton.IsEnabled = false;
        SetupStatus.Text = "키 확인 중…";
        try
        {
            _app.UpdateAccount(key, nick);
            if (nick.Length > 0 && _app.Service is { } service)
            {
                // One id lookup: proves the key works and that the nickname exists.
                if (await service.FindExistingNicknameAsync([nick]) is null)
                {
                    SetupStatus.Text = $"키는 맞지만 '{nick}' 닉네임을 찾지 못했습니다. 철자를 확인하고 다시 [시작하기]를 누르세요.";
                    _keepSetup = true;
                    Refresh();
                    return;
                }
            }
            SetupStatus.Text = "";
            _keepSetup = false;
            Refresh();
        }
        catch (NexonApiException ex) when (ex.IsAuthError)
        {
            _app.UpdateAccount(null, nick);
            SetupStatus.Text = "API 키가 올바르지 않습니다. openapi.nexon.com에서 발급한 키를 다시 확인하세요.";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or NexonApiException)
        {
            SetupStatus.Text = "NEXON Open API에 연결하지 못했습니다. 키는 저장했으니 나중에 다시 시도하세요.";
            Refresh();
        }
        finally
        {
            StartButton.IsEnabled = true;
        }
    }
}
