using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Navigation;

namespace FcHelper.App;

/// <summary>
/// How to get a NEXON Open API key, step by step with pictures of the official pages, shown on first run (no key yet)
/// and from the home screen's [발급 방법 보기]. The service name and description can be copied in one click.
/// </summary>
public partial class ApiGuideWindow : Window
{
    public ApiGuideWindow() => InitializeComponent();

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not string text) return;
        try
        {
            Clipboard.SetText(text);
            Copied.Text = "복사했습니다. 신청서 칸에 Ctrl + V로 붙여 넣으세요.";
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            Copied.Text = "클립보드를 쓰지 못했습니다. 글자를 직접 선택해 복사하세요.";
        }
    }

    private void OnOpenLink(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }

    private void OnDone(object sender, RoutedEventArgs e) => Close();
}
