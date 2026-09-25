using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace FcHelper.App;

/// <summary>
/// The "새 버전" notice: a small window above the clock, on top of every app window whichever page is open, with
/// [지금 업데이트] (download, check, restart in one click) and [나중에]. It does not take the focus from the game.
/// </summary>
public sealed class UpdateToast : Window
{
    private readonly Updater _updater;
    private readonly UpdateInfo _info;
    private readonly TextBlock _status;
    private readonly Button _update;

    public UpdateToast(Updater updater, UpdateInfo info, bool downloaded)
    {
        _updater = updater;
        _info = info;
        var res = Application.Current.Resources;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        SizeToContent = SizeToContent.Height;
        Width = 380;

        var notes = info.Notes.Split('\n').Select(l => l.Trim().TrimStart('-', '*', ' ')).Where(l => l.Length > 0).Take(3).ToList();
        _status = new TextBlock { Style = (Style)res["Hint"], TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0),
            Text = downloaded ? "새 버전을 받아 두었습니다. 누르면 바로 바뀌고 다시 켜집니다 (몇 초)." : "누르면 받아서 바꾸고 다시 켭니다 (1분 안쪽)." };
        _update = new Button { Content = downloaded ? "지금 다시 시작" : "지금 업데이트", Style = (Style)res["Primary"], Margin = new Thickness(0, 0, 8, 0) };
        _update.Click += async (_, _) => await UpdateAsync();
        var later = new Button { Content = "나중에", Style = (Style)res["Ghost"], Padding = new Thickness(12, 4, 12, 4) };
        later.Click += (_, _) => Close();
        var titlePanel = new StackPanel { Orientation = Orientation.Horizontal };
        var updateIcon = AppIcons.Make("Icon.Update", 20);
        updateIcon.Margin = new Thickness(0, 0, 8, 0);
        updateIcon.VerticalAlignment = VerticalAlignment.Center;
        titlePanel.Children.Add(updateIcon);
        titlePanel.Children.Add(new TextBlock
        {
            Text = $"새 버전 {info.Tag}이 나왔습니다",
            FontSize = 16,
            FontWeight = FontWeights.Bold,
            Foreground = (Brush)res["Accent"],
            VerticalAlignment = VerticalAlignment.Center,
        });

        var body = new StackPanel
        {
            Children =
            {
                titlePanel,
                new TextBlock { Text = $"지금 버전 v{Updater.Current}", Style = (Style)res["Hint"], Margin = new Thickness(28, 2, 0, 0) },
            },
        };
        foreach (var n in notes) body.Children.Add(new TextBlock { Text = "· " + n, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) });
        body.Children.Add(_status);
        body.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0), Children = { _update, later } });
        Content = new Border
        {
            Background = (Brush)res["Panel"], BorderBrush = (Brush)res["Accent"], BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(12),
            Padding = new Thickness(16, 12, 16, 14), Child = body,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 2, Opacity = 0.5 },
        };
        Loaded += (_, _) =>
        {
            var area = SystemParameters.WorkArea;
            Left = area.Right - ActualWidth - 16;
            Top = area.Bottom - ActualHeight - 16;
        };
    }

    private async Task UpdateAsync()
    {
        _update.IsEnabled = false;
        try
        {
            _status.Text = "받는 중…";
            var ok = await _updater.DownloadAsync(_info, new Progress<double>(p => _status.Text = $"받는 중… {p:P0}"));
            if (!ok)
            {
                _status.Text = "받은 파일이 맞지 않아 멈췄습니다 (크기·해시 불일치). 잠시 뒤 다시 눌러 주세요.";
                _update.IsEnabled = true;
                return;
            }
            _status.Text = "바꾸는 중… 곧 다시 켜집니다.";
            if (Updater.ApplyAndRestart()) Application.Current.Shutdown();
            else
            {
                _status.Text = "프로그램 폴더에 쓸 수 없어 바꾸지 못했습니다. 프로그램을 끄고 다시 켜면 적용됩니다.";
                _update.IsEnabled = true;
            }
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.IO.IOException)
        {
            _status.Text = "받지 못했습니다. 인터넷 연결을 확인하고 다시 눌러 주세요.";
            _update.IsEnabled = true;
        }
    }
}
