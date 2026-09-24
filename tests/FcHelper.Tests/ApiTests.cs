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

    [Fact]
    public async Task Auth_error_is_raised_with_error_name()
    {
        var (api, _) = Create(_ => StubHandler.Json(HttpStatusCode.Unauthorized,
            """{"error":{"name":"OPENAPI00005","message":"Please input valid API key"}}"""));
        var e = await Assert.ThrowsAsync<NexonApiException>(() => api.GetUserBasicAsync("x"));
        Assert.True(e.IsAuthError);
        Assert.Equal("OPENAPI00005", e.ErrorName);
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
}
