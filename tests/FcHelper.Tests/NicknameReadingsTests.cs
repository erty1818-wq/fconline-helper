using FcHelper.Core;
using FcHelper.Services;

namespace FcHelper.Tests;

public class NicknameReadingsTests
{
    private static IReadOnlyList<OcrLine> Read(string text) => [new(text, 0, 0, 1, 1)];

    [Fact]
    public void Focused_original_pixel_reads_correct_the_observed_ryong_to_tong_error()
    {
        var guesses = NicknameReadings.Rank(["염통"], [Read("염룡"), Read("염룡")]);
        Assert.Equal("염룡", guesses[0].Text);
        Assert.Equal(2, guesses[0].FocusedReadings);
        Assert.True(NicknameReadings.Confident(guesses));
    }

    [Fact]
    public void Conflicting_or_missing_reads_never_confirm_an_initial_candidate()
    {
        Assert.False(NicknameReadings.Confident(NicknameReadings.Rank(["염통"], [])));
        Assert.False(NicknameReadings.Confident(NicknameReadings.Rank(["염통"], [Read("염룡")])));
        Assert.False(NicknameReadings.Confident(NicknameReadings.Rank(["염통"], [Read("염룡"), Read("염통")])));
        Assert.False(NicknameReadings.Confident(NicknameReadings.Rank(["염통"], [Read("염룡"), Read("염룡"), Read("염통")])));
        Assert.False(NicknameReadings.Confident(NicknameReadings.Rank(["염통"], [Read("염룡"), Read("염룡"), Read("염통"), Read("염통")])));
    }

    [Fact]
    public void Votes_are_per_pass_and_only_observed_nearby_names_count()
    {
        var guesses = NicknameReadings.Rank(["염통", "염통"], [[new("염룡 염룡", 0, 0, 1, 1)], Read("염룡"), Read("ONLINE"), Read("내닉네임")], "내닉네임");
        Assert.Equal(["염룡", "염통"], guesses.Select(g => g.Text));
        Assert.Equal(2, guesses[0].FocusedReadings);
        Assert.DoesNotContain(guesses, g => g.Text == "염롱");
    }

    [Fact]
    public async Task Two_existing_accounts_do_not_turn_ambiguous_spelling_into_a_match()
    {
        using var temp = new TempDb();
        var api = new FakeApi();
        api.Ouids["염통"] = "wrong";
        api.Ouids["염룡"] = "right";
        var service = new FcHelperService(api, temp.Db, new());
        Assert.Null(await service.FindReadNicknameAsync([new("염통", 2, 0), new("염룡", 2, 1)]));
        Assert.Equal(0, api.CallsTo("id"));
        Assert.Equal("염룡", await service.FindReadNicknameAsync([new("염룡", 2, 1), new("염통", 0, 0)]));
        Assert.Equal(1, api.CallsTo("id"));
    }
}
