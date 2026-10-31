using System.Drawing;
using FcHelper.Core;
using FcHelper.Vision;

Console.OutputEncoding = System.Text.Encoding.UTF8;
if (args is ["--lines", var path, ..])
{
    // Every line of a screen as OCR sees it, optionally from a crop (x y width height), and what the app would make of it.
    var ocr = ScreenReader.Create() ?? throw new InvalidOperationException("Korean Windows OCR is unavailable.");
    using var whole = new Bitmap(path);
    var crop = args.Length == 6 ? args[2..].Select(int.Parse).ToArray() : [0, 0, whole.Width, whole.Height];
    using var image = whole.Clone(new Rectangle(crop[0], crop[1], crop[2], crop[3]), whole.PixelFormat);
    var lines = await ocr.ReadAsync(image);
    foreach (var l in lines) Console.WriteLine(FormattableString.Invariant($"{l.X:0.000} {l.Y:0.000} {l.Width:0.000} {l.Height:0.000}  {l.Text}"));
    var found = MatchScreen.OpponentCandidates(lines, null);
    Console.WriteLine($"match screen={MatchScreen.LooksLikeMatchScreen(lines)} record screen={MatchScreen.LooksLikeRecordScreen(lines)} candidates={string.Join(" | ", found)}");
    if (MatchScreen.ShowsOpponent(lines))
    {
        var read = await ocr.ReadNicknamesAsync(image, MatchScreen.OpponentLines(lines, null), found);
        foreach (var guess in read) Console.WriteLine($"{guess.Text}: focused={guess.FocusedReadings}");
        Console.WriteLine($"automatic={NicknameReadings.Confident(read)}");
    }
    return 0;
}
if (args.Length != 7)
{
    Console.Error.WriteLine("Usage: OcrProbe <image> <x> <y> <width> <height> <initial-name> <expected-name>\n       OcrProbe --lines <image> [x y width height]");
    return 2;
}
var reader = ScreenReader.Create() ?? throw new InvalidOperationException("Korean Windows OCR is unavailable.");
using var frame = new Bitmap(args[0]);
var box = args.Skip(1).Take(4).Select(int.Parse).ToArray();
var source = new[] { new OcrLine(args[5], box[0] / (double)frame.Width, box[1] / (double)frame.Height,
    box[2] / (double)frame.Width, box[3] / (double)frame.Height) };
var guesses = await reader.ReadNicknamesAsync(frame, source, [args[5]]);
foreach (var guess in guesses) Console.WriteLine($"{guess.Text}: focused={guess.FocusedReadings}");
Console.WriteLine($"automatic={NicknameReadings.Confident(guesses)}");
return guesses.FirstOrDefault()?.Text == args[6] && NicknameReadings.Confident(guesses) ? 0 : 1;
