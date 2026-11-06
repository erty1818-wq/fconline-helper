using System.IO;
using System.Windows;
using System.Windows.Media;

namespace FcHelper.App;

/// <summary>
/// The app's typeface, chosen once at start: a font the user put in <c>skin\fonts</c> (e.g. Pretendard, OFL), else the
/// first installed of Pretendard · SUIT · Noto Sans KR, else 맑은 고딕. Every window gets it, with crisp small text.
/// Variable fonts are skipped: WPF draws only parts of their Hangul glyphs (드 → ㄷ, 스 → ㅅ with NotoSansKR-VF.ttf,
/// which Windows 11 and other apps install), so a family is used only when it has a static file.
/// </summary>
internal static class AppFont
{
    private static readonly string[] Preferred = ["Pretendard", "SUIT", "Noto Sans KR"];

    public static FontFamily Resolve()
    {
        foreach (var dir in new[] { Path.Combine(Skin.UserFolder, "fonts"), Path.Combine(AppContext.BaseDirectory, "skin", "fonts") })
        {
            try
            {
                if (!Directory.Exists(dir) || !Directory.EnumerateFiles(dir).Any(f => IsFontFile(f) && !IsVariableFont(f)))
                    continue;
                if (Fonts.GetFontFamilies(new Uri(dir.TrimEnd('\\') + "\\")).FirstOrDefault(Static) is { } custom) return custom;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or UriFormatException)
            {
                // An unreadable font folder: fall back to installed fonts.
            }
        }
        var installed = Fonts.SystemFontFamilies.Where(f => Preferred.Contains(f.Source, StringComparer.OrdinalIgnoreCase)).ToList();
        var name = Preferred.FirstOrDefault(p => installed.FirstOrDefault(f => f.Source.Equals(p, StringComparison.OrdinalIgnoreCase)) is { } family && Static(family));
        return new FontFamily(name is null ? "Malgun Gothic, Segoe UI" : $"{name}, Malgun Gothic, Segoe UI");
    }

    private static bool IsFontFile(string path) =>
        path.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".otf", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether the family's regular face comes from a static font file (not a variable one).</summary>
    private static bool Static(FontFamily family)
    {
        try
        {
            var face = family.GetTypefaces().FirstOrDefault(t => t.Weight == FontWeights.Normal && t.Style == FontStyles.Normal) ?? family.GetTypefaces().FirstOrDefault();
            if (face is null || !face.TryGetGlyphTypeface(out var glyphs)) return false;
            return !glyphs.FontUri.IsFile || !IsVariableFont(glyphs.FontUri.LocalPath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>An OpenType font with an <c>fvar</c> table (variation axes). Collections (.ttc) are treated as static.</summary>
    internal static bool IsVariableFont(string path)
    {
        try
        {
            using var file = File.OpenRead(path);
            Span<byte> header = stackalloc byte[12];
            if (file.Read(header) < 12 || header[..4].SequenceEqual("ttcf"u8)) return false;
            var tables = header[4] << 8 | header[5];
            Span<byte> record = stackalloc byte[16];
            for (var i = 0; i < tables; i++)
            {
                if (file.Read(record) < 16) return false;
                if (record[..4].SequenceEqual("fvar"u8)) return true;
            }
            return false;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
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
