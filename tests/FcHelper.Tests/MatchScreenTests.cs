using FcHelper.Core;

namespace FcHelper.Tests;

public class MatchScreenTests
{
    /// <summary>
    /// OCR lines laid out like the real "팀 정보" screen (positions measured from a 1380x717 screenshot): tabs on top,
    /// both nicknames on one row with a division badge after the opponent's, points and divisions below, and the
    /// game chat in the bottom-right corner.
    /// </summary>
    private static List<OcrLine> TeamInfoScreen(double shiftY = 0) =>
    [
        new("팀 정보", 0.33, 0.06, 0.07, 0.03),
        new("유니폼 선택", 0.51, 0.06, 0.09, 0.03),
        new("VS", 0.44, 0.31, 0.05, 0.07),
        new("킹마카이", 0.33, 0.41 + shiftY, 0.07, 0.035),
        new("류춘 3", 0.53, 0.41 + shiftY, 0.06, 0.035),
        new("무 패", 0.29, 0.47, 0.03, 0.02),
        new("2392점", 0.33, 0.51, 0.06, 0.03),
        new("2431점", 0.53, 0.51, 0.06, 0.03),
        new("챌린저 2부 감독", 0.31, 0.56, 0.1, 0.025),
        new("챌린저 2부 감독", 0.51, 0.56, 0.1, 0.025),
        new("상대가 매칭되었습니다. = 키로 채팅을 수신할지 설정", 0.76, 0.75, 0.2, 0.02),
    ];

    [Fact]
    public void Finds_the_opponent_on_the_same_row_as_my_nickname()
    {
        var candidates = MatchScreen.OpponentCandidates(TeamInfoScreen(), "킹마카이");

        // The division badge read as "3" is dropped first; the id API rejects whichever candidate is not a user.
        Assert.Equal(["류춘", "류춘3"], candidates);
    }

    [Fact]
    public void Tolerates_an_ocr_slip_in_my_own_nickname()
    {
        var lines = TeamInfoScreen().Select(l => l.Text == "킹마카이" ? l with { Text = "킹마키이" } : l).ToList();

        Assert.Equal("류춘", MatchScreen.OpponentCandidates(lines, "킹마카이")[0]);
    }

    [Fact]
    public void Without_my_nickname_falls_back_to_the_largest_text_on_the_right()
    {
        var candidates = MatchScreen.OpponentCandidates(TeamInfoScreen(), myNickname: null);

        Assert.Equal("류춘", candidates[0]);
        Assert.DoesNotContain(candidates, c => c.EndsWith('점') || c.Contains("감독") || c.Contains("유니폼"));
    }

    [Fact]
    public void Positions_are_relative_so_layout_shifts_do_not_matter()
    {
        Assert.Equal("류춘", MatchScreen.OpponentCandidates(TeamInfoScreen(shiftY: 0.05), "킹마카이")[0]);
    }

    [Fact]
    public void Recognises_the_matchmaking_screen()
    {
        Assert.True(MatchScreen.LooksLikeMatchScreen(TeamInfoScreen()));
        Assert.False(MatchScreen.LooksLikeMatchScreen([new("메시지를 입력하세요.", 0.7, 0.9, 0.2, 0.02)]));
    }

    /// <summary>Other text the user saw picked up as nicknames: buttons, extra time, the manager card and division labels.</summary>
    private static List<OcrLine> NoisyScreen() =>
    [
        .. TeamInfoScreen(),
        new("준비", 0.80, 0.40, 0.08, 0.05),
        new("연장전", 0.62, 0.30, 0.06, 0.04),
        new("감독", 0.70, 0.62, 0.04, 0.025),
        new("셰틸 크누첸", 0.68, 0.655, 0.09, 0.03),
        new("챌린저 2부", 0.60, 0.20, 0.08, 0.045),
        new("월드클래스1", 0.85, 0.25, 0.09, 0.05),
    ];

    [Fact]
    public void Screen_labels_manager_names_and_divisions_are_not_candidates()
    {
        foreach (var me in new[] { "킹마카이", null })
        {
            var candidates = MatchScreen.OpponentCandidates(NoisyScreen(), me);
            Assert.Equal("류춘", candidates[0]);
            Assert.DoesNotContain(candidates, c => c is "준비" or "연장전" or "셰틸크누첸" or "셰틸" or "크누첸" or "챌린저2부" or "월드클래스1");
        }
    }

    [Theory]
    [InlineData("준비된자")]
    [InlineData("프로류춘")]
    [InlineData("전반전킹")]
    public void Nicknames_that_merely_contain_a_label_word_still_count(string nickname)
    {
        var lines = TeamInfoScreen().Select(l => l.Text == "류춘 3" ? l with { Text = nickname } : l).ToList();
        Assert.Equal(nickname, MatchScreen.OpponentCandidates(lines, "킹마카이")[0]);
    }

    [Theory]
    [InlineData("폭탄먼지벌레", new[] { "폭탄먼지벌레" })]
    [InlineData("폭탄 먼지벌레", new[] { "폭탄먼지벌레", "먼지벌레", "폭탄" })]
    [InlineData("FC고인물123", new[] { "FC고인물123" })]
    public void Builds_candidates_from_split_ocr_words(string line, string[] expected) =>
        Assert.Equal(expected, MatchScreen.CandidatesFrom(line));
}
