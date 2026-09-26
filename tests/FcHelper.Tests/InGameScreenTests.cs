using FcHelper.Core;

namespace FcHelper.Tests;

/// <summary>
/// In-game capture (AM-01). The lines are what the app's OCR really read from the user's screenshot
/// (2026-09-26, 킹마카이 0:5 AUD콜대원 at 86:00), region by region.
/// </summary>
public class InGameScreenTests
{
    private static readonly IReadOnlyList<OcrLine> Scoreboard3x = [new("킹마카이", 0.16, 0.41, 0.09, 0.23), new("AUD콜대원", 0.56, 0.41, 0.11, 0.23)];
    private static readonly IReadOnlyList<OcrLine> Scoreboard4x =
        [new("킹마카이", 0.16, 0.40, 0.09, 0.23), new("AUD*대원", 0.56, 0.40, 0.11, 0.23), new("86:00", 0.81, 0.37, 0.08, 0.28)];
    private static readonly IReadOnlyList<OcrLine> Bottom = [new("AUDS대원", 0.05, 0.30, 0.03, 0.12), new("0", 0.51, 0.03, 0.01, 0.1), new("킹마카이", 0.92, 0.30, 0.02, 0.12)];

    [Fact]
    public void The_opponent_comes_first_and_misreads_follow()
    {
        var candidates = InGameScreen.OpponentCandidates([Scoreboard3x, Scoreboard4x, Bottom], "킹마카이");

        Assert.Equal(["AUD콜대원", "AUDS대원"], candidates); // "AUD*대원" has a symbol and is dropped; the API rejects AUDS대원
    }

    [Fact]
    public void Without_my_nickname_both_names_are_candidates()
    {
        var candidates = InGameScreen.OpponentCandidates([Scoreboard3x], null);
        Assert.Equal(["킹마카이", "AUD콜대원"], candidates);
    }

    [Fact]
    public void The_clock_tells_a_match_is_on()
    {
        Assert.Equal(86, InGameScreen.ClockMinutes(Scoreboard4x));
        Assert.True(InGameScreen.LooksLikeInGame(Scoreboard4x));
        Assert.False(InGameScreen.LooksLikeInGame(Scoreboard3x));
        Assert.Equal(90, InGameScreen.ClockMinutes([new("90:00", 0, 0, 0.1, 0.1)]));
        Assert.Equal(120, InGameScreen.ClockMinutes([new("120:00", 0, 0, 0.1, 0.1)]));
    }
}
