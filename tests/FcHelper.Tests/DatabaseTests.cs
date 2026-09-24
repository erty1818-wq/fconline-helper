using FcHelper.Core.Models;
using FcHelper.Data;
using FcHelper.Tests.Fixtures;

namespace FcHelper.Tests;

public sealed class TempDb : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"fch-test-{Guid.NewGuid():N}.db");
    public FcDatabase Db { get; }

    public TempDb() => Db = new FcDatabase(Path);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try { File.Delete(Path + suffix); } catch (IOException) { }
        }
    }
}

public class DatabaseTests : IDisposable
{
    private readonly TempDb _t = new();
    private FcDatabase Db => _t.Db;
    private static readonly DateTime Now = new(2026, 9, 24, 0, 0, 0, DateTimeKind.Utc);

    public void Dispose() => _t.Dispose();

    [Fact]
    public void Match_round_trips_through_compressed_storage()
    {
        var json = new MatchBuilder("a", "b").A(s => s.Goal(1, assist: 2)).Json();
        var saved = Db.SaveMatch(json);

        var loaded = Db.GetMatch(saved.MatchId)!;
        Assert.Equal(saved.MatchId, loaded.MatchId);
        Assert.Equal(2, loaded.SideOf("a")!.ShootDetail[0].AssistSpId);
        Assert.True(Db.HasMatch(saved.MatchId));
        Assert.Equal(["missing"], Db.FilterMissing([saved.MatchId, "missing"]));
    }

    [Fact]
    public void Cached_match_ids_are_newest_first_and_filtered_by_type()
    {
        var old = Db.SaveMatch(new MatchBuilder("a", "b").At(Now.AddDays(-2)).Json());
        var recent = Db.SaveMatch(new MatchBuilder("a", "c").At(Now.AddDays(-1)).Json());
        Db.SaveMatch(new MatchBuilder("a", "d").At(Now).Type(52).Json());

        Assert.Equal([recent.MatchId, old.MatchId], Db.GetCachedMatchIds("a", 50, 10));
        Assert.Equal([recent.MatchId], Db.GetCachedMatchIds("c", 50, 10));
        Assert.Equal(2, Db.CountMatches(50));
    }

    [Fact]
    public void Head_to_head_lists_only_shared_matches()
    {
        Db.SaveMatch(new MatchBuilder("me", "x").A(s => s.Goal(1)).Json());
        Db.SaveMatch(new MatchBuilder("me", "x").B(s => s.Goal(1)).Json());
        Db.SaveMatch(new MatchBuilder("me", "y").Json());

        var rows = Db.GetHeadToHead("me", "x");
        Assert.Equal(2, rows.Count);
        Assert.Equal(["승", "패"], rows.Select(r => r.Result).Order(StringComparer.Ordinal));
        Assert.All(rows, row => Assert.Equal("me", row.Ouid));
    }

    [Fact]
    public void Users_keep_their_nickname_history()
    {
        Db.UpsertUser("o1", "OldName", 10, Now.AddDays(-30));
        Db.UpsertUser("o1", "NewName", 11, Now);

        Assert.Equal("o1", Db.FindUserByNickname(" newname ")!.Ouid);
        Assert.Null(Db.FindUserByNickname("OldName"));
        Assert.Equal(["OldName", "NewName"], Db.GetNicknameHistory("o1"));
        Assert.Equal(11, Db.FindUser("o1")!.Level);
    }

    [Fact]
    public void Memo_saves_tags_and_empty_memo_deletes()
    {
        Db.SaveMemo("o1", " 스루 남발 ", ["탈주", "탈주", " 강함 "], Now);
        var memo = Db.GetMemo("o1")!;
        Assert.Equal("스루 남발", memo.Text);
        Assert.Equal(["탈주", "강함"], memo.Tags);

        Db.SaveMemo("o1", "", [], Now);
        Assert.Null(Db.GetMemo("o1"));
    }

    [Fact]
    public void Metadata_names_are_looked_up_on_demand()
    {
        Db.ReplacePlayers([new SpIdMeta { Id = 101000001, Name = "호날두" }, new SpIdMeta { Id = 101000002, Name = "굴리트" }]);
        Db.ReplaceDivisions([new DivisionMeta { DivisionId = 800, DivisionName = "챔피언스" }]);

        var names = Db.GetPlayerNames([101000001, 999]);
        Assert.Equal("호날두", names[101000001]);
        Assert.False(names.ContainsKey(999));
        Assert.Equal("챔피언스", Db.GetDivisionName(800));
    }

    [Fact]
    public void Max_divisions_are_replaced()
    {
        Db.ReplaceMaxDivisions("o1", [new MaxDivision { MatchType = 50, Division = 900, AchievementDate = Now }], Now);
        Db.ReplaceMaxDivisions("o1", [new MaxDivision { MatchType = 50, Division = 800, AchievementDate = Now }], Now);
        var (divisions, updated) = Db.GetMaxDivisions("o1");
        Assert.Equal(800, Assert.Single(divisions).Division);
        Assert.Equal(Now, updated);
    }

    [Fact]
    public void Schema_migration_is_idempotent()
    {
        Db.SaveMemo("o1", "keep", [], Now);
        var reopened = new FcDatabase(_t.Path);
        Assert.Equal("keep", reopened.GetMemo("o1")!.Text);
    }
}
