using System.IO;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FcHelper.Services;

namespace FcHelper.App;

/// <summary>
/// Replaceable artwork. Every image the studio shows has a key. The default artwork is built into the exe (skin\*.png in
/// the project); a PNG named after the key in %LOCALAPPDATA%\FcHelper\skin\ (or a "skin" folder next to the exe) replaces
/// it without a rebuild, and without any the drawn placeholder shows. The list of keys, sizes and purposes is in docs/SKIN_ASSETS.md, written for an image-generating AI.
/// </summary>
public static class Skin
{
    /// <summary>Keys the studio uses; see docs/SKIN_ASSETS.md.</summary>
    public static readonly string[] Keys =
    [
        "logo", "pitch", "hero", "empty", "player",
        "nav-squad", "nav-picks", "nav-value", "nav-grade", "nav-salary", "nav-trends", "nav-mysquad", "nav-opponent", "nav-teamcolor",
        "home-search", "home-squad", "home-manager",
        "honey", "lock", "undo", "fullscreen", "image", "close", "chevron-left", "chevron-right", "check", "update", "warning", "target", "note", "rematch", "signature", "sparkle", "guide",
        "card-frame", "card-empty", "home-card", "background",
    ];

    private static readonly Dictionary<string, ImageSource?> Cache = [];

    public static string UserFolder => Path.Combine(AppPaths.DataDirectory, "skin");

    /// <summary>The custom PNG for a key, or null when there is none.</summary>
    public static ImageSource? Custom(string key)
    {
        if (Cache.TryGetValue(key, out var cached)) return cached;
        ImageSource? image = null;
        foreach (var dir in new[] { UserFolder, Path.Combine(AppContext.BaseDirectory, "skin") })
        {
            var path = Path.Combine(dir, key + ".png");
            if (!File.Exists(path)) continue;
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad; // do not keep the file locked
                bmp.UriSource = new Uri(path);
                bmp.EndInit();
                bmp.Freeze();
                image = bmp;
                break;
            }
            catch (Exception e) when (e is IOException or NotSupportedException or FileFormatException)
            {
                // A broken file falls back to the placeholder.
            }
        }
        return Cache[key] = image ?? BuiltIn(key);
    }

    /// <summary>The artwork compiled into the exe for a key, or null.</summary>
    private static ImageSource? BuiltIn(string key)
    {
        var uri = new Uri($"pack://application:,,,/skin/{key}.png", UriKind.Absolute);
        try
        {
            if (Application.GetResourceStream(uri) is null) return null;
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = uri;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch (Exception e) when (e is IOException or NotSupportedException or FileFormatException)
        {
            return null;
        }
    }

    /// <summary>The custom PNG, else the built-in vector placeholder.</summary>
    public static ImageSource Get(string key) => Custom(key) ?? Placeholder(key);

    public static bool HasCustom(string key) => Custom(key) is not null;

    /// <summary>A custom image as a stretched brush (card frames, window backgrounds), or null to keep the drawn look.</summary>
    public static System.Windows.Media.ImageBrush? Brush(string key, System.Windows.Media.Stretch stretch = System.Windows.Media.Stretch.Fill) =>
        Custom(key) is { } image ? new System.Windows.Media.ImageBrush(image) { Stretch = stretch } : null;

    /// <summary>Forget loaded images, e.g. after new files were dropped into the skin folder.</summary>
    public static void Reload() => Cache.Clear();

    private static readonly Brush Accent = Frozen(Color.FromRgb(0x3D, 0xDC, 0x97));
    private static readonly Brush Muted = Frozen(Color.FromRgb(0x9A, 0xA3, 0xAE));
    private static readonly Brush PitchFill = Frozen(Color.FromRgb(0x17, 0x23, 0x1D));
    private static readonly Brush PitchLine = Frozen(Color.FromArgb(0x55, 0xE8, 0xEA, 0xED));

    private static ImageSource Placeholder(string key)
    {
        var drawing = key switch
        {
            "pitch" => PitchDrawing(),
            "player" => PlayerDrawing(),
            "logo" => LogoDrawing(),
            "empty" => EmptyDrawing(),
            _ => BlankDrawing(),
        };
        var image = new DrawingImage(drawing);
        image.Freeze();
        return image;
    }

    /// <summary>Vertical pitch, attack at the top, 600 × 800.</summary>
    private static Drawing PitchDrawing()
    {
        var pen = new Pen(PitchLine, 2);
        var g = new DrawingGroup();
        g.Children.Add(new GeometryDrawing(PitchFill, null, new RectangleGeometry(new Rect(0, 0, 600, 800), 12, 12)));
        var lines = new GeometryGroup();
        lines.Children.Add(new RectangleGeometry(new Rect(20, 20, 560, 760)));
        lines.Children.Add(new LineGeometry(new Point(20, 400), new Point(580, 400)));
        lines.Children.Add(new EllipseGeometry(new Point(300, 400), 70, 70));
        lines.Children.Add(new RectangleGeometry(new Rect(150, 20, 300, 120)));
        lines.Children.Add(new RectangleGeometry(new Rect(230, 20, 140, 45)));
        lines.Children.Add(new RectangleGeometry(new Rect(150, 660, 300, 120)));
        lines.Children.Add(new RectangleGeometry(new Rect(230, 735, 140, 45)));
        g.Children.Add(new GeometryDrawing(null, pen, lines));
        return g;
    }

    private static Drawing PlayerDrawing()
    {
        var g = new DrawingGroup();
        g.Children.Add(new GeometryDrawing(Frozen(Color.FromRgb(0x25, 0x2A, 0x33)), null, new EllipseGeometry(new Point(64, 64), 64, 64)));
        g.Children.Add(new GeometryDrawing(Muted, null, new EllipseGeometry(new Point(64, 50), 22, 22)));
        g.Children.Add(new GeometryDrawing(Muted, null, Geometry.Parse("M24,112 C28,84 44,76 64,76 C84,76 100,84 104,112 Z")));
        return g;
    }

    private static Drawing LogoDrawing()
    {
        var g = new DrawingGroup();
        // 5-sided pentagon shield fallback with gold accent
        var gold = Frozen(Color.FromRgb(0xD4, 0xAF, 0x37));
        var dark = Frozen(Color.FromRgb(0x1E, 0x22, 0x29));
        g.Children.Add(new GeometryDrawing(gold, null, Geometry.Parse("M32,4 L58,22 L48,58 L16,58 L6,22 Z")));
        g.Children.Add(new GeometryDrawing(dark, null, Geometry.Parse("M32,8 L54,24 L45,54 L19,54 L10,24 Z")));
        g.Children.Add(new GeometryDrawing(gold, null, Geometry.Parse("M20,38 L24,26 L32,32 L40,26 L44,38 Z")));
        return g;
    }

    /// <summary>A ball on a line: the placeholder for empty states, 160 × 100.</summary>
    private static Drawing EmptyDrawing()
    {
        var g = new DrawingGroup();
        g.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, 160, 100))));
        g.Children.Add(new GeometryDrawing(null, new Pen(PitchLine, 2), new LineGeometry(new Point(10, 88), new Point(150, 88))));
        g.Children.Add(new GeometryDrawing(Frozen(Color.FromRgb(0x25, 0x2A, 0x33)), new Pen(Muted, 2), new EllipseGeometry(new Point(80, 50), 34, 34)));
        g.Children.Add(new GeometryDrawing(Accent, null, Geometry.Parse("M80,36 L93,45 L88,60 L72,60 L67,45 Z")));
        return g;
    }

    private static Drawing BlankDrawing() => new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, 1, 1)));

    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}

/// <summary>XAML: <c>Source="{app:Skin pitch}"</c>.</summary>
public sealed class SkinExtension(string key) : MarkupExtension
{
    public SkinExtension() : this("") { }

    [ConstructorArgument("key")]
    public string Key { get; set; } = key;

    public override object ProvideValue(IServiceProvider serviceProvider) => Skin.Get(Key);
}
