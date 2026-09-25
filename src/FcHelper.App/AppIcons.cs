using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FcHelper.App;

/// <summary>
/// Unified duotone vector icon resolver and factory for the entire application.
/// Fallback order:
/// 1. Custom user skin PNG in %LOCALAPPDATA%\FcHelper\skin\&lt;key&gt;.png (via Skin.cs)
/// 2. Vector DrawingImage defined in Icons.xaml
/// 3. null
/// </summary>
public static class AppIcons
{
    private static readonly Dictionary<string, string> SkinKeyMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Icon.Nav.Squad"] = "nav-squad",
        ["Icon.Nav.Picks"] = "nav-picks",
        ["Icon.Nav.Value"] = "nav-value",
        ["Icon.Nav.Grade"] = "nav-grade",
        ["Icon.Nav.Salary"] = "nav-salary",
        ["Icon.Nav.Trends"] = "nav-trends",
        ["Icon.Nav.MySquad"] = "nav-mysquad",
        ["Icon.Nav.Opponent"] = "nav-opponent",
        ["Icon.Nav.TeamColor"] = "nav-teamcolor",
        ["Icon.Home.Search"] = "home-search",
        ["Icon.Home.Squad"] = "home-squad",
        ["Icon.Home.Manager"] = "home-manager",
    };

    /// <summary>
    /// Normalizes any input key into the standard "Icon.&lt;Name&gt;" format.
    /// </summary>
    public static string NormalizeKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return "";
        var k = key.Trim();
        if (k.StartsWith("Icon.", StringComparison.OrdinalIgnoreCase)) return k;
        if (k.StartsWith("nav-", StringComparison.OrdinalIgnoreCase))
        {
            var suffix = k[4..];
            return suffix.ToLowerInvariant() switch
            {
                "squad" => "Icon.Nav.Squad",
                "picks" => "Icon.Nav.Picks",
                "value" => "Icon.Nav.Value",
                "grade" => "Icon.Nav.Grade",
                "salary" => "Icon.Nav.Salary",
                "trends" => "Icon.Nav.Trends",
                "mysquad" => "Icon.Nav.MySquad",
                "opponent" => "Icon.Nav.Opponent",
                "teamcolor" => "Icon.Nav.TeamColor",
                "players" => "Icon.Home.Search",
                _ => "Icon.Nav." + char.ToUpperInvariant(suffix[0]) + suffix[1..],
            };
        }
        if (k.StartsWith("home-", StringComparison.OrdinalIgnoreCase))
        {
            var suffix = k[5..];
            return suffix.ToLowerInvariant() switch
            {
                "search" => "Icon.Home.Search",
                "squad" => "Icon.Home.Squad",
                "manager" => "Icon.Home.Manager",
                _ => "Icon.Home." + char.ToUpperInvariant(suffix[0]) + suffix[1..],
            };
        }
        return "Icon." + k;
    }

    /// <summary>
    /// Maps an icon key to its corresponding Skin PNG key.
    /// </summary>
    public static string ToSkinKey(string key)
    {
        var norm = NormalizeKey(key);
        if (SkinKeyMap.TryGetValue(norm, out var skinKey)) return skinKey;
        var sub = norm.StartsWith("Icon.", StringComparison.OrdinalIgnoreCase) ? norm[5..] : norm;
        return sub.Replace('.', '-').ToLowerInvariant();
    }

    /// <summary>
    /// Gets an ImageSource for the given icon key. Custom user skin PNGs take precedence over vector icons.
    /// </summary>
    public static ImageSource? Get(string key, bool active = false)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;

        var skinKey = ToSkinKey(key);
        if (active)
        {
            var activeSkin = skinKey + "-active";
            if (Skin.HasCustom(activeSkin)) return Skin.Get(activeSkin);
        }
        if (Skin.HasCustom(skinKey)) return Skin.Get(skinKey);

        var norm = NormalizeKey(key);
        if (active)
        {
            var activeKey = norm + ".Active";
            if (Application.Current?.TryFindResource(activeKey) is ImageSource activeSource)
                return activeSource;
        }

        if (Application.Current?.TryFindResource(norm) is ImageSource source)
            return source;

        // Fallback: check Skin built-in placeholder
        return Skin.Get(skinKey);
    }

    /// <summary>
    /// Creates a high-quality WPF Image control for the given icon key and dimensions.
    /// </summary>
    public static Image Make(string key, double size = 16, bool active = false)
    {
        var img = new Image
        {
            Source = Get(key, active),
            Width = size,
            Height = size,
            SnapsToDevicePixels = true,
        };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
        return img;
    }

    /// <summary>
    /// Renders an icon DrawingImage into a WinForms System.Drawing.Bitmap (e.g. for NotifyIcon / ContextMenuStrip).
    /// </summary>
    public static System.Drawing.Bitmap? ToWinFormsBitmap(string key, int size = 16, bool active = false)
    {
        var source = Get(key, active);
        if (source is null) return null;

        try
        {
            var image = new Image
            {
                Source = source,
                Width = size,
                Height = size,
            };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            image.Measure(new Size(size, size));
            image.Arrange(new Rect(0, 0, size, size));

            var rtb = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(image);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(rtb));
            using var ms = new MemoryStream();
            encoder.Save(ms);
            ms.Position = 0;
            return new System.Drawing.Bitmap(ms);
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>
/// Markup extension for XAML: <c>Source="{app:Icon Undo}"</c> or <c>Source="{app:Icon Nav.Squad, Active=True}"</c>.
/// </summary>
[MarkupExtensionReturnType(typeof(ImageSource))]
public sealed class IconExtension : MarkupExtension
{
    public IconExtension() : this("") { }

    public IconExtension(string key) => Key = key;

    [ConstructorArgument("key")]
    public string Key { get; set; } = "";

    public bool Active { get; set; }

    public override object? ProvideValue(IServiceProvider serviceProvider) => AppIcons.Get(Key, Active);
}
