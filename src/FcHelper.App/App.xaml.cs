using System.Net.Http;
using System.Windows;
using FcHelper.Data;
using FcHelper.NexonApi;
using FcHelper.Services;
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

    private Mutex? _singleInstance;
    private Forms.NotifyIcon? _tray;
    private GlobalHotkey? _hotkey;
    private SearchWindow? _search;
    private VoiceBriefing? _voice;
    private RateLimiter? _limiter;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private FcDatabase? _db;

    public AppSettings Settings { get; private set; } = new();
    public FcHelperService? Service { get; private set; }
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

        Settings = AppSettings.Load();
        _db = new FcDatabase(AppPaths.DatabasePath);
        CreateTray();
        _hotkey = new GlobalHotkey(GlobalHotkey.ModControl | GlobalHotkey.ModAlt, VkS, ShowSearch);
        if (!_hotkey.IsRegistered) Notify("단축키 Ctrl+Alt+S를 등록하지 못했습니다. 다른 프로그램이 쓰고 있을 수 있습니다.");

        ApplySettings();
        if (Service is null)
        {
            // First run: ask for the API key, then go straight to the search window.
            ShowSettings();
            if (Service is not null) ShowSearch();
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

        var key = Settings.ApiKey;
        if (string.IsNullOrWhiteSpace(key))
        {
            Service = null;
            return;
        }
        _limiter = new RateLimiter(Settings.RequestsPerSecond);
        var api = new FcOnlineApi(_http, key, _limiter);
        Service = new FcHelperService(api, _db!, Settings.ToOptions());
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
        _hotkey?.Dispose();
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
