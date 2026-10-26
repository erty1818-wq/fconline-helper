using System.Net;
using System.Text.Json;
using FcHelper.Market;
using FcHelper.NexonApi;

namespace FcHelper.Tests;

public class RankerRefreshTests
{
    [Fact]
    public async Task Explicit_refresh_replaces_even_a_fresh_cache_and_failed_refresh_preserves_it()
    {
        using var temp = new TempDb();
        var store = new MarketStore(temp.Path);
        var old = new RankerSquad(1, "이전표본", 0, []);
        store.SetValue("ranker.squads", JsonSerializer.Serialize(new[] { old }), DateTime.UtcNow);
        var source = new SquadsSource();
        var lists = new NoLists();
        using var http = new HttpClient();
        var service = new SquadService(new(lists, store, _ => Task.FromResult("[]")), store, new NoCharts(),
            new(store, new(http, new RateLimiter(1000), lists)), rankerSquads: source);
        Assert.Equal("이전표본", (await service.RankerSquadsAsync())[0].Nickname);
        Assert.Equal(0, source.Calls);
        Assert.Equal("새표본", (await service.RankerSquadsAsync(refresh: true))[0].Nickname);
        Assert.Equal(1, source.Calls);
        var saved = store.GetValue("ranker.squads");
        source.Empty = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RankerSquadsAsync(refresh: true));
        Assert.Equal(saved, store.GetValue("ranker.squads"));
        source.Empty = false;
        source.Quota = true;
        await Assert.ThrowsAsync<NexonApiException>(() => service.RankerSquadsAsync(refresh: true));
        Assert.Equal(saved, store.GetValue("ranker.squads"));
    }

    [Fact]
    public async Task Quota_exhaustion_stops_the_fetch_instead_of_skipping_one_hundred_users()
    {
        using var handler = new StubHandler(_ => StubHandler.Json(HttpStatusCode.TooManyRequests,
            """{"error":{"name":"OPENAPI00007","message":"quota"}}"""));
        using var http = new HttpClient(handler);
        var api = new FcOnlineApi(http, "test-key", new RateLimiter(1000), retryBaseDelay: TimeSpan.Zero);
        var client = new RankerSquadClient(http, new RateLimiter(1000), () => api);
        var error = await Assert.ThrowsAsync<NexonApiException>(() => client.FetchAsync(
            [new(1, "표본하나", 0, null, 0, null), new(2, "표본둘", 0, null, 0, null)]));
        Assert.True(error.IsRateLimited);
        Assert.NotEmpty(handler.Requests);
        // The API may retry a short rate limit, but the next user's nickname must never be queried.
        Assert.All(handler.Requests, r => Assert.Contains("nickname=" + Uri.EscapeDataString("표본하나"), r.RequestUri!.Query));
    }

    private sealed class SquadsSource : IRankerSquadSource
    {
        public int Calls { get; private set; }
        public bool Empty { get; set; }
        public bool Quota { get; set; }
        public Task<IReadOnlyList<RankerSquad>> FetchAsync(int count, IProgress<string>? progress = null, CancellationToken ct = default)
        {
            Calls++;
            if (Quota) throw new NexonApiException(HttpStatusCode.TooManyRequests, "OPENAPI00007", "quota");
            return Task.FromResult<IReadOnlyList<RankerSquad>>(Empty ? [] : [new(1, "새표본", 0, [])]);
        }
    }
    private sealed class NoLists : IMarketListSource
    {
        public Task<IReadOnlyList<ListRow>> QueryAsync(ListQuery query, CancellationToken ct = default) => throw new InvalidOperationException("No network in cache tests.");
    }
    private sealed class NoCharts : IRankerChartSource
    {
        public Task<RankerChartData> FetchAsync(int from, int to, CancellationToken ct = default) => throw new InvalidOperationException("No network in cache tests.");
    }
}
