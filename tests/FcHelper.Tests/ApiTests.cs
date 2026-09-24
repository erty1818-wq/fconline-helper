using System.Net;
using System.Text.Json;
using FcHelper.Core.Models;
using FcHelper.NexonApi;
using Microsoft.Extensions.Time.Testing;

namespace FcHelper.Tests;

internal sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<HttpRequestMessage> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Requests.Add(request);
        return Task.FromResult(respond(request));
    }

    public static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
}

public class FcOnlineApiTests
{
    private static (FcOnlineApi Api, StubHandler Handler) Create(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var handler = new StubHandler(respond);
        var api = new FcOnlineApi(new HttpClient(handler), "test-key", new RateLimiter(1000), retryBaseDelay: TimeSpan.Zero);
        return (api, handler);
    }

    [Fact]
    public async Task Sends_key_header_and_escaped_nickname()
    {
        var (api, handler) = Create(_ => StubHandler.Json(HttpStatusCode.OK, """{"ouid":"abc"}"""));

        Assert.Equal("abc", await api.GetOuidAsync(" FC고인물 123 "));

        var req = Assert.Single(handler.Requests);
        Assert.Equal("test-key", req.Headers.GetValues("x-nxopen-api-key").Single());
        Assert.Equal("https://open.api.nexon.com/fconline/v1/id?nickname=FC%EA%B3%A0%EC%9D%B8%EB%AC%BC%20123", req.RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task Unknown_nickname_returns_null()
    {
        var (api, _) = Create(_ => StubHandler.Json(HttpStatusCode.BadRequest,
            """{"error":{"name":"OPENAPI00004","message":"Please input valid parameter"}}"""));
        Assert.Null(await api.GetOuidAsync("nobody"));
    }

    // The real API answers an invalid key with 400, not 401 (checked against open.api.nexon.com).
    private static HttpResponseMessage InvalidKey() => StubHandler.Json(HttpStatusCode.BadRequest,
        """{"error":{"name":"OPENAPI00005","message":"The apikey is not valid."}}""");

    [Fact]
    public async Task Auth_error_is_raised_with_error_name()
    {
        var (api, _) = Create(_ => InvalidKey());
        var e = await Assert.ThrowsAsync<NexonApiException>(() => api.GetUserBasicAsync("x"));
        Assert.True(e.IsAuthError);
        Assert.Equal("OPENAPI00005", e.ErrorName);
    }

    [Fact]
    public async Task Invalid_key_on_nickname_lookup_is_not_reported_as_unknown_user()
    {
        var (api, _) = Create(_ => InvalidKey());
        var e = await Assert.ThrowsAsync<NexonApiException>(() => api.GetOuidAsync("킹마카이"));
        Assert.True(e.IsAuthError);
    }

    [Fact]
    public async Task Retries_rate_limited_calls_then_succeeds()
    {
        var calls = 0;
        var (api, handler) = Create(_ => ++calls < 3
            ? StubHandler.Json(HttpStatusCode.TooManyRequests, """{"error":{"name":"OPENAPI00007","message":"limit"}}""")
            : StubHandler.Json(HttpStatusCode.OK, """["m1","m2"]"""));

        var ids = await api.GetUserMatchIdsAsync("o", 50, 0, 2);

        Assert.Equal(["m1", "m2"], ids);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Contains("matchtype=50&offset=0&limit=2", handler.Requests[0].RequestUri!.Query);
    }

    [Fact]
    public async Task Gives_up_after_max_attempts()
    {
        var (api, handler) = Create(_ => StubHandler.Json(HttpStatusCode.TooManyRequests, "{}"));
        var e = await Assert.ThrowsAsync<NexonApiException>(() => api.GetMatchDetailJsonAsync("m"));
        Assert.True(e.IsRateLimited);
        Assert.Equal(4, handler.Requests.Count);
    }

    [Fact]
    public async Task Metadata_is_fetched_without_key()
    {
        var (api, handler) = Create(_ => StubHandler.Json(HttpStatusCode.OK, "[]"));
        await api.GetMetadataJsonAsync("spid");
        var req = Assert.Single(handler.Requests);
        Assert.False(req.Headers.Contains("x-nxopen-api-key"));
        Assert.Equal("/static/fconline/meta/spid.json", req.RequestUri!.AbsolutePath);
        await Assert.ThrowsAsync<ArgumentException>(() => api.GetMetadataJsonAsync("../secret"));
    }

    [Fact]
    public async Task Rejects_limits_outside_the_api_range()
    {
        var (api, _) = Create(_ => StubHandler.Json(HttpStatusCode.OK, "[]"));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => api.GetUserMatchIdsAsync("o", 50, 0, 101));
    }
}

public class RateLimiterTests
{
    [Fact]
    public async Task Allows_a_burst_then_spaces_requests()
    {
        var time = new FakeTimeProvider();
        var limiter = new RateLimiter(2, time);

        await limiter.WaitAsync();
        await limiter.WaitAsync();
        var third = limiter.WaitAsync();
        Assert.False(third.IsCompleted);

        time.Advance(TimeSpan.FromMilliseconds(600));
        await third.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(3, limiter.IssuedCount);
    }
}

public class DeserializationTests
{
    [Fact]
    public void Reads_the_documented_match_detail_shape()
    {
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "samples", "synthetic-match-detail.json"));
        var match = JsonSerializer.Deserialize(json, FcJsonContext.Default.MatchDetail)!;

        Assert.Equal("synthetic0001", match.MatchId);
        var a = match.SideOf("ouid-a")!;
        Assert.Equal(MatchOutcome.Win, a.MatchDetail.Outcome);
        Assert.Equal("keyboard", a.MatchDetail.Controller);
        Assert.Equal(1, a.MatchDetail.OffsideCount);
        Assert.Equal(1100, a.Division);
        Assert.Equal(4, a.Player[0].Status.BallPossesionSuccess);
        Assert.Equal((2, 3), (a.Shoot.GoalTotal, a.Shoot.GoalTotalDisplay)); // opponent's own goal counts on the scoreboard
        Assert.Equal(3, a.ShootDetail.Count);
        Assert.Equal(101000002, a.ShootDetail[0].AssistSpId);
        Assert.Null(a.ShootDetail[2].AssistSpId);
        Assert.Equal(75 * 60, a.ShootDetail[1].Seconds); // 2^24 + 1800: second half, 30 minutes in
        Assert.True(a.Player[1].IsSubstitute);
        Assert.Equal("ouid-b", match.OpponentOf("ouid-a")!.Ouid);
    }

    [Fact]
    public void Accepts_the_alternative_assist_field_name()
    {
        var detail = JsonSerializer.Deserialize<ShootDetail>("""{"assist":true,"assistSpI":7,"result":3}""",
            new JsonSerializerOptions { TypeInfoResolver = FcJsonContext.Default });
        Assert.Equal(7, detail!.AssistSpId);
    }

    [Fact]
    public void Reads_a_side_that_quit_before_any_stats_existed()
    {
        // Shape copied from a real forfeit: every stat is null, the squad list is empty (identifiers replaced).
        const string json = """
            {"matchId":"m1","matchDate":"2026-09-20T01:02:03","matchType":50,"matchInfo":[
              {"ouid":"a","nickname":"quitter","division":2600,
               "matchDetail":{"seasonId":202605,"matchResult":"패","matchEndType":2,"systemPause":null,"foul":null,"injury":null,
                 "redCards":null,"yellowCards":null,"dribble":null,"cornerKick":null,"possession":null,"offsideCount":null,
                 "averageRating":null,"controller":null},
               "shoot":{"shootTotal":null,"effectiveShootTotal":null,"shootOutScore":null,"goalTotal":null,"goalTotalDisplay":0,
                 "ownGoal":null,"shootHeading":null,"goalHeading":null},
               "shootDetail":[],"pass":{"passTry":null,"passSuccess":null},"defence":{"blockTry":null,"tackleTry":null},"player":[]},
              {"ouid":"b","nickname":"winner","division":2500,
               "matchDetail":{"matchResult":"승","matchEndType":1,"possession":57,"controller":"gamepad"},
               "shoot":{"goalTotal":1,"goalTotalDisplay":3},"shootDetail":[],"player":[{"spId":1,"spPosition":0}]}]}
            """;

        var match = JsonSerializer.Deserialize(json, FcJsonContext.Default.MatchDetail)!;

        var quitter = match.SideOf("a")!;
        Assert.False(quitter.HasStats);
        Assert.Equal("", quitter.MatchDetail.Controller);
        Assert.Equal(0, quitter.MatchDetail.Possession);
        Assert.Equal(2, quitter.MatchDetail.MatchEndType);
        Assert.True(match.SideOf("b")!.HasStats);
    }

    /// <summary>
    /// Runs over real responses saved by `fch dump` (git-ignored, so only on a developer machine) and checks the
    /// field semantics verified in docs/PLANNING.md 3.4. Without local samples there is nothing to check.
    /// </summary>
    [Fact]
    public void Local_real_samples_parse_and_match_the_verified_semantics()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "FcHelper.sln"))) dir = dir.Parent;
        var files = dir is null ? [] : Directory.GetFiles(Path.Combine(dir.FullName, "docs", "samples"), "*.local.json");

        foreach (var file in files)
        {
            var match = JsonSerializer.Deserialize(File.ReadAllText(file), FcJsonContext.Default.MatchDetail)!;
            Assert.Equal(2, match.MatchInfo.Count);
            foreach (var side in match.MatchInfo)
            {
                var opp = match.MatchInfo.First(m => m != side);
                Assert.Equal(side.Shoot.GoalTotal, side.ShootDetail.Count(s => s.IsGoal));
                if (side.MatchDetail.MatchEndType == 0)
                    Assert.Equal(side.Shoot.GoalTotal + opp.Shoot.OwnGoal, side.Shoot.GoalTotalDisplay);
                // Coordinates are per side: every goal is near the goal at x = 1.
                Assert.All(side.ShootDetail.Where(s => s.IsGoal), s => Assert.True(s.X > 0.7, $"{file}: goal at x={s.X}"));
                Assert.All(side.ShootDetail, s => Assert.Equal(s.InPenalty, Core.Pitch.IsInBox(s.X, s.Y)));
            }
            _ = Analysis.UserAnalyzer.Analyze([match], match.MatchInfo[0].Ouid);
        }
    }

    [Fact]
    public void Accepts_the_documented_ball_possession_field_name()
    {
        var status = JsonSerializer.Deserialize<PlayerStatus>("""{"ballPossesionSuc":5}""",
            new JsonSerializerOptions { TypeInfoResolver = FcJsonContext.Default });
        Assert.Equal(5, status!.BallPossesionSuccess);
    }
}
