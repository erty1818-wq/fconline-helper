using System.Globalization;
using System.Text.RegularExpressions;

namespace FcHelper.Core;

/// <summary>A part of the game window to read on its own, as fractions (0..1) of the window's client area.</summary>
public sealed record ScreenRegion(double X, double Y, double Width, double Height, double Scale);

/// <summary>
/// The in-game screen: the scoreboard top-left reads "home 0 5 away 86:00", and the panels at the bottom corners carry
/// both nicknames again. For when the matchmaking screen was missed (docs/auto-mode/PLAN.md has the measurements).
/// Windows OCR misreads the small scoreboard text at screen size ("AU唱대원"), but reads it right enlarged, so the
/// scoreboard is read on its own at two scales and the id API picks the candidate that is a real user.
/// </summary>
public static class InGameScreen
{
    /// <summary>Scoreboard band: both nicknames and the clock. Read at 3× (nicknames) and 4× (clock).</summary>
    public static readonly ScreenRegion Scoreboard = new(0.03, 0.05, 0.32, 0.07, 3);
    public static readonly ScreenRegion ScoreboardLarge = Scoreboard with { Scale = 4 };
    /// <summary>The nickname line under the bottom-corner player panels (y ≈ 0.955), without the player names above.</summary>
    public static readonly ScreenRegion BottomNames = new(0, 0.935, 1, 0.065, 3);

    public const int MaxCandidates = 6;

    private static readonly Regex ClockText = new(@"^(\d{1,3})[:;.](\d{2})$");

    /// <summary>The match clock in minutes when a region's text shows one ("86:00" → 86).</summary>
    public static int? ClockMinutes(IReadOnlyList<OcrLine> lines) =>
        lines.Select(l => ClockText.Match(MatchScreen.Compact(l.Text)))
            .Where(m => m.Success)
            .Select(m => (int?)int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture))
            .FirstOrDefault();

    /// <summary>
    /// Nicknames read from one or more passes over the scoreboard and the bottom panels, without mine and without the
    /// clock, digits and screen labels; the first pass is trusted most.
    /// </summary>
    public static IReadOnlyList<string> OpponentCandidates(IEnumerable<IReadOnlyList<OcrLine>> passes, string? myNickname)
    {
        var me = string.IsNullOrWhiteSpace(myNickname) ? null : MatchScreen.Compact(myNickname);
        return passes
            .SelectMany(lines => lines.OrderBy(l => l.X))
            .Where(l => !ClockText.IsMatch(MatchScreen.Compact(l.Text)) && !MatchScreen.IsUiText(l.Text))
            .Where(l => me is null || !IsMe(MatchScreen.Compact(l.Text), me))
            .SelectMany(l => MatchScreen.CandidatesFrom(l.Text))
            // OCR marks a letter it could not read with a symbol ("AUD*대원"); such a reading is never a nickname.
            .Where(c => c.All(ch => char.IsLetterOrDigit(ch) || ch is '_' or '-'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxCandidates)
            .ToList();
    }

    /// <summary>True when the scoreboard pass shows the clock, i.e. a match is being played.</summary>
    public static bool LooksLikeInGame(IReadOnlyList<OcrLine> scoreboard) => ClockMinutes(scoreboard) is not null;

    private static bool IsMe(string text, string me) =>
        text.Contains(me, StringComparison.OrdinalIgnoreCase) || MatchScreen.EditDistance(text, me) <= Math.Max(1, me.Length / 4);
}
