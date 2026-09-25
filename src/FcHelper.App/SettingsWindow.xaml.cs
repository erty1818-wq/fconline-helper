using System.Globalization;
using System.Windows;

namespace FcHelper.App;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;

    public SettingsWindow(AppSettings settings)
    {
        _settings = settings;
        InitializeComponent();
        ApiKeyBox.Password = settings.ApiKey ?? "";
        MyNicknameBox.Text = settings.MyNickname ?? "";
        WindowBox.Text = settings.MatchWindow.ToString(CultureInfo.InvariantCulture);
        RateBox.Text = settings.RequestsPerSecond.ToString(CultureInfo.InvariantCulture);
        VoiceBox.IsChecked = settings.VoiceBriefing;
        TopmostBox.IsChecked = settings.KeepCardOnTop;
        StartupBox.IsChecked = settings.StartWithWindows;
        (settings.Detection == DetectionMode.Manual ? ManualModeBox : HotkeyModeBox).IsChecked = true;
        SaveCapturesBox.IsChecked = settings.SaveCaptures;
        MarketBox.IsChecked = settings.ShowMarket;
        MarketRefreshBox.IsChecked = settings.MarketAutoRefresh;
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(WindowBox.Text, out var window) || window is < 10 or > 100)
        {
            MessageBox.Show(this, "분석 경기 수는 10~100 사이로 입력하세요.", Title);
            return;
        }
        if (!double.TryParse(RateBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var rate) || rate is <= 0 or > 1000)
        {
            MessageBox.Show(this, "초당 호출 한도는 0보다 큰 숫자로 입력하세요.", Title);
            return;
        }

        _settings.ApiKey = ApiKeyBox.Password;
        _settings.MyNickname = MyNicknameBox.Text.Trim();
        _settings.MatchWindow = window;
        _settings.RequestsPerSecond = rate;
        _settings.VoiceBriefing = VoiceBox.IsChecked == true;
        _settings.KeepCardOnTop = TopmostBox.IsChecked == true;
        _settings.StartWithWindows = StartupBox.IsChecked == true;
        _settings.Detection = ManualModeBox.IsChecked == true ? DetectionMode.Manual : DetectionMode.Hotkey;
        _settings.SaveCaptures = SaveCapturesBox.IsChecked == true;
        _settings.ShowMarket = MarketBox.IsChecked == true;
        _settings.MarketAutoRefresh = MarketRefreshBox.IsChecked == true;
        _settings.Save();
        DialogResult = true;
    }
}
