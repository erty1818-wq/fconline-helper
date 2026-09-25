using System.IO;
using System.Windows;
using System.Windows.Media;

namespace FcHelper.App;

/// <summary>
/// The app's typeface, chosen once at start: a font the user put in <c>skin\fonts</c> (e.g. Pretendard, OFL), else the
/// first installed of Pretendard · SUIT · Noto Sans KR, else 맑은 고딕. Every window gets it, with crisp small text.
/// </summary>
internal static class AppFont
{
    private static readonly string[] Preferred = ["Pretendard", "Pretendard Variable", "SUIT", "Noto Sans KR"];

    public static FontFamily Resolve()
    {
        foreach (var dir in new[] { Path.Combine(Skin.UserFolder, "fonts"), Path.Combine(AppContext.BaseDirectory, "skin", "fonts") })
        {
            try
            {
                if (!Directory.Exists(dir) || !Directory.EnumerateFiles(dir).Any(f => f.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".otf", StringComparison.OrdinalIgnoreCase)))
                    continue;
                if (Fonts.GetFontFamilies(new Uri(dir.TrimEnd('\\') + "\\")).FirstOrDefault() is { } custom) return custom;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or UriFormatException)
            {
                // An unreadable font folder: fall back to installed fonts.
            }
        }
        var installed = Fonts.SystemFontFamilies.Select(f => f.Source).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var name = Preferred.FirstOrDefault(installed.Contains);
        return new FontFamily(name is null ? "Malgun Gothic, Segoe UI" : $"{name}, Malgun Gothic, Segoe UI");
    }

    /// <summary>Applies the font to every window as it loads (and crisp text rendering for Korean at small sizes).</summary>
    public static void Apply()
    {
        var font = Resolve();
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((sender, _) =>
        {
            if (sender is not Window w) return;
            w.FontFamily = font;
            TextOptions.SetTextFormattingMode(w, TextFormattingMode.Display);
            TextOptions.SetTextRenderingMode(w, TextRenderingMode.ClearType);
        }));
    }
}
