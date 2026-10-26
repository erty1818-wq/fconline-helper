using System.Drawing;
using FcHelper.Core;
using FcHelper.Vision;

if (args.Length != 7)
{
    Console.Error.WriteLine("Usage: OcrProbe <image> <x> <y> <width> <height> <initial-name> <expected-name>");
    return 2;
}
Console.OutputEncoding = System.Text.Encoding.UTF8;
var reader = ScreenReader.Create() ?? throw new InvalidOperationException("Korean Windows OCR is unavailable.");
using var frame = new Bitmap(args[0]);
var box = args.Skip(1).Take(4).Select(int.Parse).ToArray();
var source = new[] { new OcrLine(args[5], box[0] / (double)frame.Width, box[1] / (double)frame.Height,
    box[2] / (double)frame.Width, box[3] / (double)frame.Height) };
var guesses = await reader.ReadNicknamesAsync(frame, source, [args[5]]);
foreach (var guess in guesses) Console.WriteLine($"{guess.Text}: focused={guess.FocusedReadings}");
Console.WriteLine($"automatic={NicknameReadings.Confident(guesses)}");
return guesses.FirstOrDefault()?.Text == args[6] && NicknameReadings.Confident(guesses) ? 0 : 1;
