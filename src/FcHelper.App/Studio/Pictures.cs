using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace FcHelper.App.Studio;

/// <summary>
/// Pictures in grid cells without touching the row records: <c>studio:Pictures.FaceOf="{Binding SpId}"</c> shows the
/// card's mini face, <c>studio:Pictures.SeasonOf="{Binding Season}"</c> the official season icon. Both load in the
/// background (cached on disk) and survive row recycling: a picture only lands if the cell still shows that card.
/// </summary>
public static class Pictures
{
    public static readonly DependencyProperty FaceOfProperty =
        DependencyProperty.RegisterAttached("FaceOf", typeof(long), typeof(Pictures), new PropertyMetadata(0L, OnFaceOf));

    public static readonly DependencyProperty SeasonOfProperty =
        DependencyProperty.RegisterAttached("SeasonOf", typeof(string), typeof(Pictures), new PropertyMetadata(null, OnSeasonOf));

    public static long GetFaceOf(DependencyObject d) => (long)d.GetValue(FaceOfProperty);
    public static void SetFaceOf(DependencyObject d, long value) => d.SetValue(FaceOfProperty, value);
    public static string? GetSeasonOf(DependencyObject d) => (string?)d.GetValue(SeasonOfProperty);
    public static void SetSeasonOf(DependencyObject d, string? value) => d.SetValue(SeasonOfProperty, value);

    /// <summary>The official icon of a season code as the market data writes it (26TOTS, ICONTM, WG …).</summary>
    public static string SeasonIconUrl(string code) =>
        $"https://ssl.nexon.com/s2/game/fc/online/obt/externalAssets/new/season/{code.ToLowerInvariant()}.png";

    private static async void OnFaceOf(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Image image) return;
        var spId = (long)e.NewValue;
        image.Source = spId == 0 ? null : Faces.Cached(spId);
        if (spId == 0 || image.Source is not null || !Faces.Enabled) return;
        var source = await Faces.GetAsync(spId);
        if (GetFaceOf(image) == spId) image.Source = source;
    }

    private static async void OnSeasonOf(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Image image) return;
        image.Source = null;
        if (e.NewValue is not string { Length: > 0 } code) return;
        ImageSource? source = await Faces.LoadAsync(SeasonIconUrl(code));
        if (GetSeasonOf(image) == code) image.Source = source;
    }
}
