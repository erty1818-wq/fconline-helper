using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace FcHelper.App;

/// <summary>OS-19: the open tab as a picture, saved to Pictures\FcHelper or put on the clipboard.</summary>
public partial class SearchWindow
{
    /// <summary>What the open tab draws, whole (also the part scrolled out of view).</summary>
    private FrameworkElement? TabContent() => _tab switch
    {
        "summary" => Card,
        "squad" => SquadContent,
        "shots" => ShotsContent,
        "flow" => FlowContent,
        "compare" => CompareContent,
        "players" => PlayersTab, // the grid shows the rows that are on screen
        "mine" => MineContent,
        _ => null,
    };

    /// <summary>A sheet with a title line above the tab, rendered at twice the screen size so text stays sharp.</summary>
    private BitmapSource? RenderTab()
    {
        if (_report is null || TabContent() is not { ActualWidth: > 0, ActualHeight: > 0 } content) return null;
        var label = _tabs.FirstOrDefault(t => t.Key == _tab).Label;
        var header = new StackPanel { Margin = new Thickness(16, 12, 16, 8) };
        header.Children.Add(new TextBlock { Text = $"{_report.Nickname} · {label}", FontSize = 16, FontWeight = FontWeights.SemiBold, Foreground = Res<Brush>("Text") });
        header.Children.Add(new TextBlock
        {
            Text = $"{DateTime.Now:yyyy-MM-dd HH:mm} · FC Online Helper · Data based on NEXON Open API", FontSize = 11, Foreground = Res<Brush>("Muted"),
        });
        var picture = new Rectangle
        {
            Width = content.ActualWidth, Height = content.ActualHeight, Margin = new Thickness(16, 0, 16, 16),
            Fill = new VisualBrush(content) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top },
        };
        var sheet = new StackPanel { Background = Res<Brush>("Bg"), Children = { header, picture } };
        sheet.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        sheet.Arrange(new Rect(sheet.DesiredSize));
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(sheet.ActualWidth * 2), (int)Math.Ceiling(sheet.ActualHeight * 2), 192, 192, PixelFormats.Pbgra32);
        bitmap.Render(sheet);
        bitmap.Freeze();
        return bitmap;
    }

    private void OnSaveImageClick(object sender, RoutedEventArgs e)
    {
        if (RenderTab() is not { } bitmap || _report is null) { StatusText.Text = "저장할 화면이 없습니다."; return; }
        try
        {
            var folder = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "FcHelper");
            Directory.CreateDirectory(folder);
            var safe = string.Concat(_report.Nickname.Select(c => System.IO.Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
            var file = System.IO.Path.Combine(folder, $"opponent-{safe}-{_tab}-{DateTime.Now:yyyyMMdd-HHmmss}.png");
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(file)) encoder.Save(stream);
            StatusText.Text = $"이미지로 저장했습니다: {file}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText.Text = "이미지를 저장하지 못했습니다 (사진 폴더에 쓸 수 없습니다).";
        }
    }

    private void OnCopyImageClick(object sender, RoutedEventArgs e)
    {
        if (RenderTab() is not { } bitmap) { StatusText.Text = "복사할 화면이 없습니다."; return; }
        try
        {
            Clipboard.SetImage(bitmap);
            StatusText.Text = "이미지를 클립보드에 복사했습니다. 메신저에 붙여 넣으세요.";
        }
        catch (COMException)
        {
            // Another program holds the clipboard for a moment.
            StatusText.Text = "클립보드를 쓰지 못했습니다. 잠시 뒤 다시 눌러 주세요.";
        }
    }
}
