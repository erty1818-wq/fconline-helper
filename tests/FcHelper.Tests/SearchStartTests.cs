using FcHelper.Data;
using FcHelper.Services;
using FcHelper.Tests.Fixtures;

namespace FcHelper.Tests;

/// <summary>The 구단주 검색 start screen: recent searches, favourites, recent opponents, autocomplete (OS-01).</summary>
public class SearchStartTests : IDisposable
{
    private readonly TempDb _t = new();
    private FcDatabase Db => _t.Db;
    private static readonly DateTime Now = new(2026, 9, 24, 0, 0, 0, DateTimeKind.Utc);

    public void Dispose() => _t.Dispose();

    [Fact]
    public void Recent_searches_are_newest_first_unique_and_capped()
    {
        var h = new SearchHistory(Db);
        for (var i = 0; i < SearchHistory.MaxRecent + 3; i++) h.AddRecent($"user{i}");
        h.AddRecent("USER5");

        var recent = h.Recent;
        Assert.Equal(SearchHistory.MaxRecent, recent.Count);
        Assert.Equal("USER5", recent[0]);
        Assert.DoesNotContain("user5", recent);
        Assert.DoesNotContain("user0", recent);

        h.RemoveRecent("user12");
        Assert.DoesNotContain("user12", h.Recent);
    }

    [Fact]
    public void Favourites_toggle_on_and_off_case_insensitively()
    {
        var h = new SearchHistory(Db);
        Assert.True(h.ToggleFavorite("Kane"));
        Assert.True(h.IsFavorite("kane"));
        Assert.False(h.ToggleFavorite("KANE"));
        Assert.Empty(h.Favorites);
        Assert.False(h.ToggleFavorite("  "));
    }

    [Fact]
    public void Broken_stored_list_reads_as_empty()
    {
        Db.SetValue(SearchHistory.RecentKey, "not json");
        Assert.Empty(new SearchHistory(Db).Recent);
    }

    [Fact]
    public void Recent_opponents_count_my_results_and_use_their_latest_nickname()
    {
        Db.SaveMatch(new MatchBuilder("me", "a").At(Now.AddDays(-3)).A(s => s.Goal(1)).B(s => s.Nick("old_a")).Json());
        Db.SaveMatch(new MatchBuilder("me", "a").At(Now.AddDays(-1)).B(s => s.Nick("new_a").Goal(2)).Json());
        Db.SaveMatch(new MatchBuilder("me", "b").At(Now.AddDays(-2)).Json());
        // Someone else's match must not show up.
        Db.SaveMatch(new MatchBuilder("x", "c").At(Now).Json());

        var list = Db.RecentOpponents("me");

        Assert.Equal(["a", "b"], list.Select(o => o.Ouid));
        var a = list[0];
        Assert.Equal("new_a", a.Nickname);
        Assert.Equal((1, 0, 1), (a.Wins, a.Draws, a.Losses));
        Assert.Equal(Now.AddDays(-1), a.LastPlayed);
        Assert.Equal((0, 1, 0), (list[1].Wins, list[1].Draws, list[1].Losses));
    }

    [Fact]
    public void Nickname_suggestions_match_the_start_and_treat_wildcards_literally()
    {
        Db.UpsertUser("u1", "Sonny7", 10, Now.AddDays(-1));
        Db.UpsertUser("u2", "son_kick", 10, Now);
        Db.SaveMatch(new MatchBuilder("me", "u3").At(Now.AddDays(-5)).B(s => s.Nick("SonMatch")).Json());
        Db.UpsertUser("u4", "Kane", 10, Now);

        Assert.Equal(["son_kick", "Sonny7", "SonMatch"], Db.SuggestNicknames("son"));
        Assert.Equal(["son_kick"], Db.SuggestNicknames("son_"));
        Assert.Empty(Db.SuggestNicknames("%"));
        Assert.Empty(Db.SuggestNicknames(" "));
    }
}
