using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices.WindowsRuntime;
using FcHelper.Core;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using OcrLine = FcHelper.Core.OcrLine;

namespace FcHelper.Vision;

/// <summary>
/// Copies the game area off the screen and reads it with the Korean OCR built into Windows.
/// A screen copy rather than Windows.Graphics.Capture: on Windows 10 the latter draws a yellow border around the
/// game while capturing. The copy shows what is on screen, so it is only taken while the game is in front.
/// Frames stay in memory unless the caller saves one.
/// </summary>
public sealed class ScreenReader
{
    private readonly OcrEngine _engine;

    private ScreenReader(OcrEngine engine) => _engine = engine;

    /// <returns>Null when the Korean OCR language is not installed (Windows Settings → Language → 한국어).</returns>
    public static ScreenReader? Create()
    {
        var engine = OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("ko"));
        return engine is null ? null : new ScreenReader(engine);
    }

    public static Bitmap Capture(Rectangle screenArea)
    {
        var bmp = new Bitmap(screenArea.Width, screenArea.Height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.CopyFromScreen(screenArea.Location, Point.Empty, screenArea.Size);
        return bmp;
    }

    /// <summary>OCR lines with positions relative to the image, so callers never deal with the resolution.</summary>
    public async Task<IReadOnlyList<OcrLine>> ReadAsync(Bitmap image)
    {
        // The engine has a size cap; large game windows are scaled down, which keeps nicknames readable.
        var scale = Math.Min(1.0, (double)OcrEngine.MaxImageDimension / Math.Max(image.Width, image.Height));
        using var scaled = scale < 1 ? new Bitmap(image, (int)(image.Width * scale), (int)(image.Height * scale)) : null;
        var source = scaled ?? image;

        using var bitmap = ToSoftwareBitmap(source);
        var result = await _engine.RecognizeAsync(bitmap);
        double w = source.Width, h = source.Height;
        return result.Lines.Select(line =>
        {
            var rects = line.Words.Select(word => word.BoundingRect).ToList();
            var left = rects.Min(r => r.X);
            var top = rects.Min(r => r.Y);
            var right = rects.Max(r => r.X + r.Width);
            var bottom = rects.Max(r => r.Y + r.Height);
            return new OcrLine(line.Text, left / w, top / h, (right - left) / w, (bottom - top) / h);
        }).ToList();
    }

    /// <summary>
    /// OCR of one part of a captured frame, enlarged first: small game text (the in-game scoreboard) is read right only
    /// at two to four times its size. Positions are relative to the region.
    /// </summary>
    public async Task<IReadOnlyList<OcrLine>> ReadRegionAsync(Bitmap frame, ScreenRegion region)
    {
        var crop = Rectangle.Intersect(new Rectangle(0, 0, frame.Width, frame.Height), new Rectangle(
            (int)(region.X * frame.Width), (int)(region.Y * frame.Height), (int)(region.Width * frame.Width), (int)(region.Height * frame.Height)));
        if (crop.Width < 4 || crop.Height < 4) return [];
        using var big = new Bitmap((int)(crop.Width * region.Scale), (int)(crop.Height * region.Scale), PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(big))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.DrawImage(frame, new Rectangle(0, 0, big.Width, big.Height), crop, GraphicsUnit.Pixel);
        }
        return await ReadAsync(big);
    }

    /// <summary>
    /// Reread only nickname boxes from the original pixels, at 2x and 3x, with modest padding. Larger magnification,
    /// thresholds and contrast stretching lost small Hangul strokes in the AM-06 supplied crop (룡 became 통).
    /// Eight small reads at most, only when an opponent still needs identifying; no extra capture or polling.
    /// </summary>
    public async Task<IReadOnlyList<NicknameGuess>> ReadNicknamesAsync(Bitmap frame, IReadOnlyList<OcrLine> source,
        IReadOnlyList<string> initial, string? myNickname = null)
    {
        if (initial.Count == 0) return [];
        var boxes = source.Where(l => initial.Any(n => string.Concat(l.Text.Where(c => !char.IsWhiteSpace(c))).Contains(n, StringComparison.OrdinalIgnoreCase)))
            .DistinctBy(l => (Math.Round(l.X, 3), Math.Round(l.Y, 3), Math.Round(l.Width, 3), Math.Round(l.Height, 3)))
            .Take(4).ToList();
        var passes = new List<IReadOnlyList<OcrLine>>();
        foreach (var line in boxes)
        {
            var paddingX = Math.Max(4.0 / frame.Width, line.Height * frame.Height / frame.Width * .7);
            var paddingY = Math.Max(3.0 / frame.Height, line.Height * .5);
            var x = Math.Max(0, line.X - paddingX);
            var y = Math.Max(0, line.Y - paddingY);
            var width = Math.Min(1 - x, line.Width + 2 * paddingX);
            var height = Math.Min(1 - y, line.Height + 2 * paddingY);
            foreach (var scale in new[] { 2.0, 3.0 })
                passes.Add(await ReadRegionAsync(frame, new ScreenRegion(x, y, width, height, scale)));
        }
        return NicknameReadings.Rank(initial, passes, myNickname);
    }

    public static IReadOnlyList<OcrLine> InFrame(IReadOnlyList<OcrLine> lines, ScreenRegion region) =>
        lines.Select(l => l with { X = region.X + l.X * region.Width, Y = region.Y + l.Y * region.Height,
            Width = l.Width * region.Width, Height = l.Height * region.Height }).ToList();

    private static SoftwareBitmap ToSoftwareBitmap(Bitmap bmp)
    {
        var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var bytes = new byte[data.Stride * bmp.Height];
            System.Runtime.InteropServices.Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
            return SoftwareBitmap.CreateCopyFromBuffer(bytes.AsBuffer(), BitmapPixelFormat.Bgra8, bmp.Width, bmp.Height, BitmapAlphaMode.Premultiplied);
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }
}
