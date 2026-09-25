using System.IO;
using System.Net.Http;
using System.Windows;
using FcHelper.Core;
using FcHelper.Data;
using FcHelper.Market;
using FcHelper.NexonApi;
using FcHelper.Services;
using FcHelper.Vision;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace FcHelper.App;

/// <summary>
/// Lives in the system tray. Nothing polls: the app only does work when the hotkey, the tray menu or a
/// search asks for it, so it costs no CPU while you play.
/// </summary>
public partial class App : Application
{
    private const uint VkS = 0x53;
    private const uint VkF = 0x46;

    private Mutex? _singleInstance;
    private Forms.NotifyIcon? _tray;
    private GlobalHotkey? _hotkey;
    private GlobalHotkey? _captureHotkey;
    private ScreenReader? _reader;
    private bool _recognizing;
    private MarketService? _market;
    private IRankerStatsSource? _rankerStats;
    private Studio.StudioWindow? _studio;
    private readonly CancellationTokenSource _exit = new();
    private static readonly Uri SeasonListUrl = new("https://open.api.nexon.com/static/fconline/meta/seasonid.json");
    private SearchWindow? _search;
    private VoiceBriefing? _voice;
    private RateLimiter? _limiter;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private FcDatabase? _db;

    public AppSettings Settings { get; private set; } = new();
    public FcHelperService? Service { get; private set; }
    /// <summary>Squad builder, ranker picks, team colours, grade/salary/price analyses: the engine the squad screens bind to.</summary>
    public SquadService? Squads { get; private set; }
    /// <summary>The match cache (my matches, opponents) for the squad pages.</summary>
    public FcDatabase? Db => _db;
    public int ApiCallCount => _limiter?.IssuedCount ?? 0;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstance = new Mutex(true, @"Local\FcHelper.SingleInstance", out var isFirst);
        if (!isFirst)
        {
            MessageBox.Show("FC Online Helper가 이미 실행 중입니다. 트레이 아이콘을 확인하세요.", "FC Online Helper");
            Shutdown();
            return;
        }

        // A tray app should not vanish on one bad response: log it, tell the user, keep running.
        DispatcherUnhandledException += (_, args) =>
        {
            LogError(args.Exception);
            Notify("오류가 생겨 작업을 중단했습니다. 앱은 계속 실행됩니다. (기록: error.log)");
            args.Handled = true;
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            LogError(args.Exception);
            args.SetObserved();
        };

        Settings = AppSettings.Load();
        _db = new FcDatabase(AppPaths.DatabasePath);
        CreateTray();
        _hotkey = new GlobalHotkey(GlobalHotkey.ModControl | GlobalHotkey.ModAlt, VkS, ShowSearch);
        if (!_hotkey.IsRegistered) Notify("단축키 Ctrl+Alt+S를 등록하지 못했습니다. 다른 프로그램이 쓰고 있을 수 있습니다.");

        ApplySettings();
        // Market data needs no API key: the data center and the season list are public.
        // One limiter for every data center request (market lists, daily chart, team colours): one per two seconds.
        var dataCenter = new RateLimiter(0.5);
        var marketStore = new MarketStore(AppPaths.DatabasePath);
        var lists = new DataCenterListClient(_http, dataCenter);
        _market = new MarketService(lists, marketStore, ct => _http.GetStringAsync(SeasonListUrl, ct));
        Squads = new SquadService(_market, marketStore, new DataCenterChartClient(_http, dataCenter),
            new TeamColorCache(marketStore, new DataCenterTeamColorClient(_http, dataCenter, lists)), _rankerStats);
        _market.Changed += () => Dispatcher.BeginInvoke(UpdateTrayText);
        _ = KeepMarketFreshAsync(_exit.Token);
        if (Service is null)
        {
            // First run: ask for the API key, then go straight to the search window.
            ShowSettings();
            if (Service is not null) ShowSearch();
        }
        else if (e.Args.Contains("--studio"))
        {
            ShowStudio();
        }
        else if (!e.Args.Contains("--tray"))
        {
            ShowSearch();
        }
    }

    /// <summary>Rebuilds the API client and service after the settings change.</summary>
    private void ApplySettings()
    {
        _voice?.Dispose();
        _voice = Settings.VoiceBriefing ? new VoiceBriefing() : null;
        if (_voice is { HasKoreanVoice: false }) Notify("한국어 음성이 설치되어 있지 않아 기본 음성으로 읽습니다.");

        // The capture shortcut exists only in hotkey mode, so manual mode leaves Ctrl+Alt+F to other programs.
        _captureHotkey?.Dispose();
        _captureHotkey = null;
        if (Settings.Detection != DetectionMode.Manual)
        {
            _captureHotkey = new GlobalHotkey(GlobalHotkey.ModControl | GlobalHotkey.ModAlt, VkF, RecognizeOpponent);
            if (!_captureHotkey.IsRegistered) Notify("단축키 Ctrl+Alt+F를 등록하지 못했습니다. 다른 프로그램이 쓰고 있을 수 있습니다.");
        }

        var key = Settings.ApiKey;
        if (string.IsNullOrWhiteSpace(key))
        {
            Service = null;
            return;
        }
        _limiter = new RateLimiter(Settings.RequestsPerSecond);
        var api = new FcOnlineApi(_http, key, _limiter);
        _rankerStats = api;
        // The data center is a website, not the Open API: one request per second at most.
        var market = Settings.ShowMarket ? new DataCenterClient(_http, new RateLimiter(1)) : null;
        Service = new FcHelperService(api, _db!, Settings.ToOptions(), market: market);
        _ = WarmUpAsync(Service);
    }

    /// <summary>Loads player names and my recent matches in the background, once per start.</summary>
    private async Task WarmUpAsync(FcHelperService service)
    {
        try
        {
            await service.EnsureMetadataAsync();
            await service.SyncMyMatchesAsync();
        }
        catch (NexonApiException ex) when (ex.IsAuthError)
        {
            Notify("API 키가 올바르지 않습니다. 설정에서 다시 입력하세요.");
        }
        catch (Exception ex) when (ex is NexonApiException or HttpRequestException or TaskCanceledException)
        {
            // Offline or under maintenance: searches will report it; the cache still works.
        }
    }

    public void ShowSearch()
    {
        if (_search is null)
        {
            _search = new SearchWindow(this);
            _search.Closed += (_, _) => _search = null;
            _search.Show();
        }
        if (_search.WindowState == WindowState.Minimized) _search.WindowState = WindowState.Normal;
        _search.Activate();
        _search.FocusSearchBox();
    }

    /// <summary>
    /// Ctrl+Alt+F: one screenshot of the game area, OCR, then the id API picks the real nickname among the
    /// candidates. The card opens beside the game without taking focus, so the game keeps its input.
    /// </summary>
    private async void RecognizeOpponent()
    {
        if (_recognizing) return;
        _recognizing = true;
        try
        {
            if (Service is null)
            {
                Notify("설정에서 API 키를 먼저 입력하세요.");
                return;
            }
            var game = GameWindow.Find();
            if (game is null || game.IsMinimized || game.ClientBounds is not { } area)
            {
                Notify("FC온라인 창을 찾지 못했습니다.");
                return;
            }
            if (!game.IsForeground)
            {
                Notify("게임 화면이 앞에 있을 때 Ctrl+Alt+F를 눌러 주세요.");
                return;
            }
            _reader ??= ScreenReader.Create();
            if (_reader is null)
            {
                Notify("한국어 OCR이 없습니다. Windows 설정 → 시간 및 언어 → 언어에서 한국어를 추가하세요.");
                return;
            }

            // Capture first: the matchmaking screen can be gone within about three seconds.
            using var frame = ScreenReader.Capture(area);
            var lines = await _reader.ReadAsync(frame);
            if (Settings.SaveCaptures) SaveCapture(frame, lines);

            var candidates = MatchScreen.OpponentCandidates(lines, Settings.MyNickname);
            var nickname = candidates.Count == 0 ? null : await Service.FindExistingNicknameAsync(candidates);

            var search = ShowSearchBeside(area);
            if (nickname is not null)
            {
                search.Search(nickname);
            }
            else
            {
                search.Prompt(MatchScreen.LooksLikeMatchScreen(lines)
                    ? "화면에서 상대 닉네임을 읽지 못했습니다. 직접 입력해 주세요."
                    : "매칭 화면(팀 정보)이 아닌 것 같습니다. 팀 정보 화면에서 눌러 주세요.", candidates.FirstOrDefault());
            }
        }
        catch (Exception ex) when (ex is NexonApiException or HttpRequestException or TaskCanceledException)
        {
            Notify("상대를 확인하지 못했습니다. 연결 상태나 API 키를 확인하세요.");
        }
        finally
        {
            _recognizing = false;
        }
    }

    private static void SaveCapture(Drawing.Bitmap frame, IReadOnlyList<OcrLine> lines)
    {
        var path = Path.Combine(AppPaths.CapturesDirectory, $"{DateTime.Now:yyyyMMdd-HHmmss}");
        frame.Save(path + ".png", Drawing.Imaging.ImageFormat.Png);
        File.WriteAllLines(path + ".txt", lines.Select(l => FormattableString.Invariant(
            $"{l.X:0.000} {l.Y:0.000} {l.Width:0.000} {l.Height:0.000}  {l.Text}")));
    }

    // ── market data ────────────────────────────────────────────────────────

    /// <summary>How often the season list is checked, so new cards are picked up within hours, not a day.</summary>
    private static readonly TimeSpan SeasonCheck = TimeSpan.FromHours(3);

    /// <summary>
    /// Refreshes market prices when they are a day old and at once for a new season. No polling: it sleeps until the
    /// next due time (at most <see cref="SeasonCheck"/>), and while the game runs it waits for the game to exit.
    /// </summary>
    private async Task KeepMarketFreshAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(1), ct); // let start-up work finish first
            while (!ct.IsCancellationRequested)
            {
                if (Settings.MarketAutoRefresh)
                {
                    await WaitForGameToCloseAsync(ct);
                    if (await _market!.RefreshIfDueAsync(ct: ct)) NotifyPriceAlerts();
                    // The daily chart (ranker picks, formations) changes once a day; the service re-fetches when it is 12 h old.
                    try { await Squads!.ChartAsync(ct: ct); } catch (InvalidOperationException) { }
                }
                var due = _market!.NextDue ?? DateTime.UtcNow;
                var wait = due - DateTime.UtcNow;
                await Task.Delay(wait < TimeSpan.FromMinutes(5) ? TimeSpan.FromMinutes(5) : wait > SeasonCheck ? SeasonCheck : wait, ct);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>After a price refresh: a few cards that just dropped and are cheap for their spec, as one tray notice.</summary>
    private void NotifyPriceAlerts()
    {
        try
        {
            var alerts = Squads?.Alerts() ?? [];
            if (alerts.Count == 0) return;
            Dispatcher.BeginInvoke(() => Notify("시세 급락 가성비: " + string.Join(", ",
                alerts.Take(3).Select(a => $"{a.Card.Name} {a.Card.Season} +{a.Grade} {a.Change:+0%;-0%}"))));
        }
        catch (InvalidOperationException)
        {
            // No market data yet.
        }
    }

    /// <summary>Waits on the process exit event: no checking in a loop, nothing sent to the game.</summary>
    private static async Task WaitForGameToCloseAsync(CancellationToken ct)
    {
        foreach (var p in System.Diagnostics.Process.GetProcessesByName(GameWindow.DefaultProcessName))
        {
            using (p)
            {
                await p.WaitForExitAsync(ct);
            }
        }
    }

    /// <summary>Opens the squad and market studio on a page ("squad", "value", "opponent", …).</summary>
    public void ShowStudio(string page = "squad", Action<FrameworkElement>? then = null)
    {
        if (_studio is null)
        {
            _studio = new Studio.StudioWindow();
            _studio.Closed += (_, _) => _studio = null;
            _studio.Show();
        }
        if (_studio.WindowState == WindowState.Minimized) _studio.WindowState = WindowState.Normal;
        _studio.Activate();
        _studio.Navigate(page, then);
    }

    private void UpdateTrayText()
    {
        if (_tray is null || _market is null) return;
        // NotifyIcon text is limited to 63 characters.
        _tray.Text = _market.Status.Running is { } r ? $"FC Online Helper · 시세 갱신 중 {r.Done}/{r.Total}" : "FC Online Helper";
    }

    /// <summary>Opens (or reuses) the card next to the game window without activating it.</summary>
    private SearchWindow ShowSearchBeside(Drawing.Rectangle gamePixels)
    {
        var isNew = _search is null;
        if (_search is null)
        {
            _search = new SearchWindow(this) { ShowActivated = false };
            _search.Closed += (_, _) => _search = null;
        }
        // Screen pixels → WPF units on the primary monitor.
        var scale = Forms.Screen.PrimaryScreen!.Bounds.Width / SystemParameters.PrimaryScreenWidth;
        _search.PlaceBeside(new Rect(gamePixels.X / scale, gamePixels.Y / scale, gamePixels.Width / scale, gamePixels.Height / scale));
        if (isNew) _search.Show();
        else if (_search.WindowState == WindowState.Minimized) _search.WindowState = WindowState.Normal;
        return _search;
    }

    public void ShowSettings()
    {
        var window = new SettingsWindow(Settings);
        if (window.ShowDialog() == true)
        {
            ApplySettings();
            if (_search is not null) _search.Topmost = Settings.KeepCardOnTop;
        }
    }

    public void Brief(OpponentReport report) => _voice?.Speak(ReportText.Voice(report));

    private async void SyncMine()
    {
        if (Service is null || string.IsNullOrWhiteSpace(Settings.MyNickname))
        {
            Notify("설정에서 내 닉네임을 입력하면 내 경기를 동기화할 수 있습니다.");
            return;
        }
        try
        {
            Notify($"내 경기 동기화 완료: 새 경기 {await Service.SyncMyMatchesAsync()}건");
        }
        catch (Exception ex) when (ex is NexonApiException or HttpRequestException or TaskCanceledException)
        {
            Notify("동기화에 실패했습니다. 연결 상태나 API 키를 확인하세요.");
        }
    }

    // ── tray ───────────────────────────────────────────────────────────────

    private void CreateTray()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("상대 검색  (Ctrl+Alt+S)", null, (_, _) => ShowSearch());
        menu.Items.Add("스쿼드·시세", null, (_, _) => ShowStudio("squad"));
        menu.Items.Add("가성비 찾기", null, (_, _) => ShowStudio("value"));
        menu.Items.Add("내 경기 동기화", null, (_, _) => SyncMine());
        menu.Items.Add("설정", null, (_, _) => ShowSettings());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("종료", null, (_, _) => Shutdown());

        _tray = new Forms.NotifyIcon
        {
            Icon = CreateTrayIcon(),
            Text = "FC Online Helper",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _tray.MouseClick += (_, args) =>
        {
            if (args.Button == Forms.MouseButtons.Left) ShowSearch();
        };
    }

    private static void LogError(Exception e)
    {
        try
        {
            File.AppendAllText(Path.Combine(AppPaths.DataDirectory, "error.log"), $"[{DateTime.Now:O}] {e}\n\n");
        }
        catch (IOException)
        {
        }
    }

    private void Notify(string message) => _tray?.ShowBalloonTip(3000, "FC Online Helper", message, Forms.ToolTipIcon.Info);

    /// <summary>Draws the tray icon at runtime so the repo needs no binary .ico file.</summary>
    private static Drawing.Icon CreateTrayIcon()
    {
        using var bmp = new Drawing.Bitmap(32, 32);
        using (var g = Drawing.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.TextRenderingHint = Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            using var fill = new Drawing.SolidBrush(Drawing.Color.FromArgb(0x3D, 0xDC, 0x97));
            g.FillEllipse(fill, 1, 1, 30, 30);
            using var font = new Drawing.Font("Segoe UI", 11, Drawing.FontStyle.Bold, Drawing.GraphicsUnit.Pixel);
            using var text = new Drawing.SolidBrush(Drawing.Color.FromArgb(0x15, 0x18, 0x1D));
            var size = g.MeasureString("FC", font);
            g.DrawString("FC", font, text, (32 - size.Width) / 2, (32 - size.Height) / 2);
        }
        return Drawing.Icon.FromHandle(bmp.GetHicon());
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Stops a refresh in progress; it resumes from the same step next time.
        _exit.Cancel();
        _hotkey?.Dispose();
        _captureHotkey?.Dispose();
        _voice?.Dispose();
        if (_tray is not null)
        {
            _tray.Visible = false;
            _tray.Dispose();
        }
        _http.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
