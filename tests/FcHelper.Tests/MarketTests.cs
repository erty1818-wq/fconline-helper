using System.Net;
using FcHelper.Core;
using FcHelper.Core.Models;
using FcHelper.NexonApi;
using FcHelper.Services;
using FcHelper.Tests.Fixtures;
using Microsoft.Extensions.Time.Testing;

namespace FcHelper.Tests;

public class DataCenterParserTests
{
    // Trimmed from the structure of the real PlayerAbility / PlayerPriceGraph fragments (2026-09-25).
    private const string Ability = """
        <div class="playerCardInfoSide">
            <div class="liveInfo"></div>
            <div class="ovr value">105</div>
            <div class="position">
                CM
            </div>
        </div>
        <div class="nameWrap"><div class="season"><img src="x.png" alt=""></div>
            <div class="name">황인범</div>
        </div>
        """;

    private const string Price = """
        <div class="txt">
            <span>현재가</span>
            <strong alt="4,820" title="4,820">

                4,820

            </strong>
        </div>
        """;

    [Fact]
    public void Reads_overall_position_and_name() =>
        Assert.Equal(("황인범", 105, "CM"), DataCenterParser.ParseAbility(Ability));

    [Fact]
    public void Reads_the_price_as_displayed() => Assert.Equal("4,820", DataCenterParser.ParsePrice(Price));

    [Fact]
    public void Unknown_markup_gives_nothing_instead_of_throwing()
    {
        Assert.Null(DataCenterParser.ParseAbility("<html>점검 중</html>"));
        Assert.Null(DataCenterParser.ParsePrice("<html></html>"));
    }

    [Fact]
    public async Task Client_posts_both_requests_and_clamps_the_grade()
    {
        var bodies = new List<string>();
        var handler = new StubHandler(req =>
        {
            bodies.Add(req.Content!.ReadAsStringAsync().Result);
            return StubHandler.Json(HttpStatusCode.OK, req.RequestUri!.AbsolutePath.EndsWith("PlayerAbility") ? Ability : Price);
        });
        var client = new DataCenterClient(new HttpClient(handler), new RateLimiter(1000));

        var m = await client.GetPlayerAsync(293228010, strong: 0);

        Assert.Equal(("황인범", 105, "4,820", 1), (m!.Name, m.Ovr, m.Price, m.Strong));
        Assert.All(handler.Requests, r => Assert.Equal(HttpMethod.Post, r.Method));
        Assert.StartsWith("spid=293228010&n1Strong=1&", bodies[0]);
        Assert.Equal("spid=293228010&n1strong=1", bodies[1]);
    }
}

public class MarketLookupTests : IDisposable
{
    private const int Ronaldo = 101000001;
    private readonly TempDb _t = new();
    private readonly FakeApi _api = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 24, 0, 0, 0, TimeSpan.Zero));

    public void Dispose() => _t.Dispose();

    private sealed class FakeMarket(TimeProvider time) : IPlayerMarketSource
    {
        public int Calls;
        public bool Fail;
        public Task<PlayerMarket?> GetPlayerAsync(int spId, int strong, CancellationToken ct = default)
        {
            Calls++;
            if (Fail) throw new HttpRequestException("down");
            return Task.FromResult<PlayerMarket?>(new PlayerMarket(spId, strong, "호날두", 132, "ST", "4,820", time.GetUtcNow().UtcDateTime));
        }
    }

    private FcHelperService Service(FakeMarket market) =>
        new(_api, _t.Db, new FcHelperOptions(), _time, market);

    private void Seed() =>
        _api.Add(new MatchBuilder("opp", "r").A(s => s.Nick("상대").Goal(Ronaldo, ShotTypes.Finesse)).Build());

    [Fact]
    public async Task Dangerous_players_get_overall_and_price_on_the_card()
    {
        Seed();
        var market = new FakeMarket(_time);

        var report = await Service(market).LookupAsync("상대");

        Assert.Equal(132, report!.Market[Ronaldo].Ovr);
        Assert.Contains("· +5 ST 132 · 시세 0.01억 미만", ReportText.Card(report)); // grade 5 from the match data; 4,820 BP in 억
    }

    [Fact]
    public async Task Market_values_are_cached_for_their_ttl()
    {
        Seed();
        var market = new FakeMarket(_time);
        var svc = Service(market);

        await svc.LookupAsync("상대");
        await svc.LookupAsync("상대");
        Assert.Equal(1, market.Calls);

        _time.Advance(TimeSpan.FromHours(13));
        await svc.LookupAsync("상대");
        Assert.Equal(2, market.Calls);
    }

    [Fact]
    public async Task A_data_center_failure_keeps_the_card_and_the_old_value()
    {
        Seed();
        var market = new FakeMarket(_time);
        var svc = Service(market);
        await svc.LookupAsync("상대");

        _time.Advance(TimeSpan.FromDays(1));
        market.Fail = true;
        var report = await svc.LookupAsync("상대");

        Assert.NotNull(report);
        Assert.Equal("4,820", report.Market[Ronaldo].Price);
    }
}
