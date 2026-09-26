using System.Net;
using System.Text.Json;
using FcHelper.Core;
using FcHelper.Core.Models;
using FcHelper.NexonApi;
using FcHelper.Services;
using FcHelper.Tests.Fixtures;
using Microsoft.Extensions.Time.Testing;

namespace FcHelper.Tests;

/// <summary>In-memory stand-in for the NEXON API that records how often each endpoint is hit.</summary>
internal sealed class FakeApi : IFcOnlineApi
{
    public Dictionary<string, string> Ouids { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, List<MatchDetail>> MatchesByOuid { get; } = [];
    public Dictionary<string, int> Calls { get; } = [];
    public int FailDetailsAfter { get; set; } = int.MaxValue;

    private void Hit(string name) => Calls[name] = Calls.GetValueOrDefault(name) + 1;
    public int CallsTo(string name) => Calls.GetValueOrDefault(name);

    public void Add(MatchDetail match)
    {
        foreach (var side in match.MatchInfo)
        {
            Ouids.TryAdd(side.Nickname, side.Ouid);
            if (!MatchesByOuid.TryGetValue(side.Ouid, out var list)) MatchesByOuid[side.Ouid] = list = [];
            list.Add(match);
        }
    }

    public Task<string?> GetOuidAsync(string nickname, CancellationToken ct = default)
    {
        Hit("id");
        return Task.FromResult(Ouids.TryGetValue(nickname, out var o) ? o : null);
    }

    public Task<UserBasic> GetUserBasicAsync(string ouid, CancellationToken ct = default)
    {
        Hit("basic");
        var nick = Ouids.First(kv => kv.Value == ouid).Key;
        return Task.FromResult(new UserBasic { Ouid = ouid, Nickname = nick, Level = 42 });
    }

    public Task<IReadOnlyList<MaxDivision>> GetMaxDivisionAsync(string ouid, CancellationToken ct = default)
    {
        Hit("maxdivision");
        return Task.FromResult<IReadOnlyList<MaxDivision>>([new MaxDivision { MatchType = 50, Division = 800 }]);
    }

    public Task<IReadOnlyList<string>> GetUserMatchIdsAsync(string ouid, int matchType, int offset, int limit, CancellationToken ct = default)
    {
        Hit("match");
        var ids = MatchesByOuid.GetValueOrDefault(ouid, [])
            .Where(m => m.MatchType == matchType)
            .OrderByDescending(m => m.MatchDate)
            .Skip(offset).Take(limit).Select(m => m.MatchId).ToList();
        return Task.FromResult<IReadOnlyList<string>>(ids);
    }

    public Task<string> GetMatchDetailJsonAsync(string matchId, CancellationToken ct = default)
    {
        Hit("match-detail");
        if (CallsTo("match-detail") > FailDetailsAfter)
            throw new NexonApiException(HttpStatusCode.TooManyRequests, "OPENAPI00007", "limit");
        var match = MatchesByOuid.Values.SelectMany(l => l).First(m => m.MatchId == matchId);
        return Task.FromResult(JsonSerializer.Serialize(match, FcJsonContext.Default.MatchDetail));
    }

    public Task<string> GetMetadataJsonAsync(string name, CancellationToken ct = default)
    {
        Hit("meta." + name);
        return Task.FromResult(name switch
        {
            "spid" => """[{"id":101000001,"name":"호날두"},{"id":101000002,"name":"굴리트"}]""",
            "division" => """[{"divisionId":800,"divisionName":"슈퍼 챔피언스"},{"divisionId":900,"divisionName":"챔피언스"},{"divisionId":1000,"divisionName":"슈퍼 챌린지"}]""",
            _ => "[]",
        });
    }
}

public class FcHelperServiceTests : IDisposable
{
    private const int Ronaldo = 101000001;
    private const int Gullit = 101000002;
    private readonly TempDb _t = new();
    private readonly FakeApi _api = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 24, 0, 0, 0, TimeSpan.Zero));

    public void Dispose() => _t.Dispose();

    private FcHelperService Service(string? me = null, int window = 30) =>
        new(_api, _t.Db, new FcHelperOptions { MyNickname = me, MatchWindow = window, ProgressBatch = 10 }, _time);

    private void SeedOpponent(int matches, string opponentNick = "FC고인물123", string? meNick = null)
    {
        for (var i = 0; i < matches; i++)
        {
            var rival = meNick is not null && i < 2 ? "me" : $"r{i}";
            var rivalNick = meNick is not null && i < 2 ? meNick : $"rival{i}";
            _api.Add(new MatchBuilder("opp", rival)
                .At(new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc).AddHours(i))
                .A(s => s.Nick(opponentNick).Division(i == matches - 1 ? 900 : 1000).Goal(Ronaldo, ShotTypes.Finesse, y: 0.75, assist: Gullit).Goal(Ronaldo, ShotTypes.Finesse, y: 0.75, assist: Gullit))
                .B(s => s.Nick(rivalNick).Goal(5, ShotTypes.Header))
                .Build());
        }
    }

    [Fact]
    public async Task Lookup_fetches_matches_and_builds_a_named_report()
    {
        SeedOpponent(12);
        var svc = Service();
        await svc.EnsureMetadataAsync();

        var report = await svc.LookupAsync("fc고인물123");

        Assert.NotNull(report);
        Assert.Equal("FC고인물123", report.Nickname);
        Assert.Equal(42, report.Level);
        Assert.Equal("슈퍼 챔피언스", report.MaxDivisionName);
        // The newest match (i = 11) was played at 챔피언스; older ones at 슈퍼 챌린지.
        Assert.Equal("챔피언스", report.RecentDivisionName);
        Assert.Contains("최근경기 등급 챔피언스 · 최고 슈퍼 챔피언스", ReportText.Card(report));
        Assert.Equal(12, report.Analysis.Record.Matches);
        Assert.True(report.IsComplete);
        Assert.Equal("호날두", report.PlayerName(Ronaldo));
        Assert.Contains("굴리트 → 호날두", ReportText.Card(report));
        Assert.Equal(12, _api.CallsTo("match-detail"));
    }

    [Fact]
    public async Task Second_lookup_uses_the_cache()
    {
        SeedOpponent(5);
        var svc = Service();
        await svc.LookupAsync("FC고인물123");
        _api.Calls.Clear();
        _time.Advance(TimeSpan.FromMinutes(11));

        var progress = new List<LookupProgress>();
        await svc.LookupAsync("FC고인물123", new SyncProgress<LookupProgress>(progress.Add));

        Assert.Equal(0, _api.CallsTo("match-detail"));
        Assert.Equal(0, _api.CallsTo("id"));
        Assert.Equal(1, _api.CallsTo("match"));
        Assert.Contains(progress, p => p.Stage == LookupStage.ShowingCache && p.Report!.Analysis.Record.Matches == 5);
    }

    [Fact]
    public async Task Only_new_matches_are_fetched_after_the_user_played_more()
    {
        SeedOpponent(5);
        var svc = Service();
        await svc.LookupAsync("FC고인물123");
        _api.Add(new MatchBuilder("opp", "late").At(new DateTime(2026, 9, 10, 0, 0, 0, DateTimeKind.Utc)).A(s => s.Nick("FC고인물123")).Build());
        _api.Calls.Clear();
        _time.Advance(TimeSpan.FromMinutes(11));

        var report = await svc.LookupAsync("FC고인물123");

        Assert.Equal(1, _api.CallsTo("match-detail"));
        Assert.Equal(6, report!.Analysis.Record.Matches);
    }

    [Fact]
    public async Task Reopening_within_ten_minutes_makes_no_api_call_unless_refreshed()
    {
        SeedOpponent(5);
        var svc = Service();
        var first = await svc.LookupAsync("FC고인물123");
        Assert.Equal(_time.GetUtcNow().UtcDateTime, first!.CheckedAt);
        _api.Add(new MatchBuilder("opp", "late").At(new DateTime(2026, 9, 10, 0, 0, 0, DateTimeKind.Utc)).A(s => s.Nick("FC고인물123")).Build());
        _api.Calls.Clear();
        _time.Advance(TimeSpan.FromMinutes(5));

        var again = await svc.LookupAsync("FC고인물123");
        Assert.Empty(_api.Calls);
        Assert.Equal(5, again!.Analysis.Record.Matches);
        Assert.Equal(first.CheckedAt, again.CheckedAt);
        Assert.True(again.IsComplete);

        var refreshed = await svc.LookupAsync("FC고인물123", refresh: true);
        Assert.Equal(1, _api.CallsTo("match-detail"));
        Assert.Equal(6, refreshed!.Analysis.Record.Matches);
        Assert.Equal(_time.GetUtcNow().UtcDateTime, refreshed.CheckedAt);
    }

    [Fact]
    public async Task A_recent_check_is_reused_only_for_as_many_matches_or_fewer()
    {
        SeedOpponent(12);
        var svc = Service();
        var few = await svc.LookupAsync("FC고인물123", matches: 5);
        Assert.Equal(5, few!.Analysis.Record.Matches);
        _api.Calls.Clear();

        var more = await svc.LookupAsync("FC고인물123");   // 30: more than the last check looked at
        Assert.Equal(1, _api.CallsTo("match"));
        Assert.Equal(12, more!.Analysis.Record.Matches);
        _api.Calls.Clear();

        var again = await svc.LookupAsync("FC고인물123", matches: 5);
        Assert.Empty(_api.Calls);
        Assert.Equal(5, again!.Analysis.Record.Matches);
    }

    [Fact]
    public async Task A_stopped_fetch_is_not_remembered_as_checked()
    {
        SeedOpponent(8);
        _api.FailDetailsAfter = 3;
        var svc = Service();
        var report = await svc.LookupAsync("FC고인물123");
        Assert.Null(report!.CheckedAt);

        _api.FailDetailsAfter = int.MaxValue;
        _api.Calls.Clear();
        await svc.LookupAsync("FC고인물123");
        Assert.Equal(5, _api.CallsTo("match-detail"));
    }

    [Fact]
    public async Task Publishes_partial_results_while_fetching()
    {
        SeedOpponent(25);
        var progress = new List<LookupProgress>();

        await Service().LookupAsync("FC고인물123", new SyncProgress<LookupProgress>(progress.Add));

        var partial = progress.Where(p => p.Stage == LookupStage.FetchingMatches).ToList();
        Assert.Equal([10, 20], partial.Select(p => p.Fetched));
        Assert.All(partial, p => Assert.False(p.Report!.IsComplete));
        Assert.Equal(LookupStage.Done, progress[^1].Stage);
    }

    [Fact]
    public async Task Stops_fetching_when_rate_limited_and_returns_what_it_has()
    {
        SeedOpponent(8);
        _api.FailDetailsAfter = 3;

        var report = await Service().LookupAsync("FC고인물123");

        Assert.Equal(3, report!.Analysis.Record.Matches);
        Assert.False(report.IsComplete);
    }

    [Fact]
    public async Task Unknown_nickname_returns_null() => Assert.Null(await Service().LookupAsync("없는닉네임"));

    [Fact]
    public async Task Head_to_head_uses_my_synced_matches()
    {
        SeedOpponent(6, meNick: "나야나");
        var svc = Service(me: "나야나");

        Assert.Equal(2, await svc.SyncMyMatchesAsync());
        var report = await svc.LookupAsync("FC고인물123");

        var h2h = report!.HeadToHead!;
        Assert.Equal(2, h2h.Matches);
        Assert.Equal(2, h2h.Losses);
        Assert.Equal("1:2", h2h.LastScore);
        Assert.Contains("재대결 2전 0승 0무 2패", ReportText.Card(report));
    }

    [Fact]
    public async Task User_info_is_refreshed_only_after_its_ttl()
    {
        SeedOpponent(1);
        var svc = Service();
        await svc.LookupAsync("FC고인물123");
        await svc.LookupAsync("FC고인물123");
        Assert.Equal(1, _api.CallsTo("id"));

        _time.Advance(TimeSpan.FromDays(2));
        await svc.LookupAsync("FC고인물123");
        Assert.Equal(2, _api.CallsTo("id"));
        Assert.Equal(2, _api.CallsTo("maxdivision"));
    }

    [Fact]
    public async Task Metadata_is_downloaded_once_per_ttl()
    {
        var svc = Service();
        await svc.EnsureMetadataAsync();
        await svc.EnsureMetadataAsync();
        Assert.Equal(1, _api.CallsTo("meta.spid"));
    }

    [Fact]
    public async Task Memo_appears_in_the_next_report()
    {
        SeedOpponent(1);
        var svc = Service();
        var first = await svc.LookupAsync("FC고인물123");
        svc.SaveMemo(first!.Ouid, "스루 남발", ["탈주"]);

        var second = await svc.LookupAsync("FC고인물123");

        Assert.Equal(["탈주"], second!.Memo!.Tags);
        Assert.Contains("📝 [탈주] 스루 남발", ReportText.Card(second));
        Assert.Contains("메모, 탈주.", ReportText.Voice(second));
    }
}

/// <summary>Progress&lt;T&gt; posts asynchronously; tests need the callbacks in order before asserting.</summary>
internal sealed class SyncProgress<T>(Action<T> handler) : IProgress<T>
{
    public void Report(T value) => handler(value);
}

public class TransientFailureTests : IDisposable
{
    private readonly TempDb _t = new();
    public void Dispose() => _t.Dispose();

    private sealed class FlakyApi(FakeApi inner, int okDetails) : IFcOnlineApi
    {
        private int _details;
        public Task<string?> GetOuidAsync(string n, CancellationToken ct = default) => inner.GetOuidAsync(n, ct);
        public Task<UserBasic> GetUserBasicAsync(string o, CancellationToken ct = default) => inner.GetUserBasicAsync(o, ct);
        public Task<IReadOnlyList<MaxDivision>> GetMaxDivisionAsync(string o, CancellationToken ct = default) => inner.GetMaxDivisionAsync(o, ct);
        public Task<IReadOnlyList<string>> GetUserMatchIdsAsync(string o, int t, int off, int l, CancellationToken ct = default) => inner.GetUserMatchIdsAsync(o, t, off, l, ct);
        public Task<string> GetMetadataJsonAsync(string n, CancellationToken ct = default) => inner.GetMetadataJsonAsync(n, ct);
        public Task<string> GetMatchDetailJsonAsync(string id, CancellationToken ct = default) =>
            ++_details > okDetails ? throw new HttpRequestException("network down") : inner.GetMatchDetailJsonAsync(id, ct);
    }

    [Fact]
    public async Task Network_errors_mid_fetch_return_a_partial_report()
    {
        var fake = new FakeApi();
        for (var i = 0; i < 5; i++) fake.Add(new MatchBuilder("opp", $"r{i}").A(s => s.Nick("상대")).Build());
        var svc = new FcHelperService(new FlakyApi(fake, okDetails: 2), _t.Db, new FcHelperOptions());

        var report = await svc.LookupAsync("상대");

        Assert.Equal(2, report!.LoadedMatches);
        Assert.False(report.IsComplete);
    }
}
