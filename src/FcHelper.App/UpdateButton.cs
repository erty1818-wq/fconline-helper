using System.Net.Http;
using System.Windows;
using System.Windows.Controls;

namespace FcHelper.App;

/// <summary>
/// The one-touch update button, always visible (home screen, studio sidebar). Normally "v0.6.0 · 업데이트 확인": a click
/// asks the release page right away. When a newer version is out it turns green ("새 버전 v0.6.1 업데이트") and one
/// click downloads it (progress on the button), swaps the exe and restarts.
/// </summary>
public sealed class UpdateButton : Button
{
    private static App App => (App)Application.Current;
    private bool _busy;

    public UpdateButton()
    {
        Style = (Style)Application.Current.Resources["Ghost"];
        Padding = new Thickness(10, 4, 10, 4);
        Idle();
        Click += async (_, _) => await OnClickAsync();
        Loaded += (_, _) =>
        {
            App.UpdateAvailable += OnAvailable;
            if (App.AvailableUpdate is { } info) Available(info);
        };
        Unloaded += (_, _) => App.UpdateAvailable -= OnAvailable;
    }

    private void OnAvailable(UpdateInfo info) => Dispatcher.BeginInvoke(() => Available(info));

    private void Idle(string? note = null)
    {
        Style = (Style)Application.Current.Resources["Ghost"];
        Content = Label("Icon.Update", note ?? $"v{Updater.Current} · 업데이트 확인");
        ToolTip = "눌러서 새 버전이 있는지 바로 확인합니다 (켠 뒤 1분, 그 뒤 6시간마다 저절로도 확인)";
    }

    private void Available(UpdateInfo info)
    {
        if (_busy) return;
        Style = (Style)Application.Current.Resources["Primary"];
        Content = Label("Icon.Update", $"새 버전 {info.Tag} 업데이트");
        ToolTip = $"지금 v{Updater.Current} → {info.Tag}. 누르면 받아서 바꾸고 다시 켭니다.";
    }

    private async Task OnClickAsync()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            if (App.AvailableUpdate is not { } info)
            {
                Content = Label("Icon.Update", "확인 중…");
                info = await App.CheckUpdateNowAsync() ?? null!;
                if (info is null)
                {
                    Idle($"v{Updater.Current} · 최신 버전입니다");
                    return;
                }
                _busy = false;
                Available(info);
                return;
            }
            var ok = await App.UpdateNowAsync(new Progress<double>(p => Content = Label("Icon.Update", $"받는 중… {p:P0}")));
            if (!ok) Idle("업데이트 실패 · 다시 누르기");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.IO.IOException)
        {
            Idle("연결 실패 · 다시 누르기");
        }
        finally
        {
            _busy = false;
        }
    }

    private static StackPanel Label(string icon, string text)
    {
        var image = AppIcons.Make(icon, 16);
        image.Margin = new Thickness(0, 0, 6, 0);
        image.VerticalAlignment = VerticalAlignment.Center;
        return new StackPanel { Orientation = Orientation.Horizontal, Children = { image, new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center } } };
    }
}
