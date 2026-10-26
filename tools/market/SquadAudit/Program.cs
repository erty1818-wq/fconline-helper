using System.Diagnostics;
using System.Text.Json;
using FcHelper.Market;
using FcHelper.NexonApi;
using Microsoft.Data.Sqlite;

if (args.Length < 2 || !Bp.TryParse(args.ElementAtOrDefault(2) ?? "100", out var budget))
{
    Console.Error.WriteLine("Usage: SquadAudit <source.db> <new-working-copy.db> [budget-in-eok] [formation]");
    return 2;
}
if (File.Exists(args[1])) throw new IOException("The working copy must be a new file.");
using (var source = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = args[0], Mode = SqliteOpenMode.ReadOnly }.ToString()))
using (var copy = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = args[1], Mode = SqliteOpenMode.ReadWriteCreate }.ToString()))
{
    source.Open();
    copy.Open();
    source.BackupDatabase(copy);
}
// All data must come from the working copy. An earlier clock makes cached entries usable without refreshing them.
var time = new CacheClock();
var store = new MarketStore(args[1]);
var lists = new NoLists();
using var http = new HttpClient(new NoNetwork());
var market = new MarketService(lists, store, _ => throw new InvalidOperationException("No metadata requests."), time: time);
var colors = new TeamColorCache(store, new(http, new RateLimiter(.5), lists), time);
var squads = new SquadService(market, store, new NoCharts(), colors, time: time);
var cards = squads.Pool().ToDictionary(c => c.SpId);
var chart = store.LatestChart(1, 10000) ?? throw new InvalidOperationException("A cached daily chart is required.");
var targets = new List<TeamColorTarget>();
foreach (var (color, _) in await squads.PopularTeamColorsAsync())
    if (await squads.TeamColorAsync(color.Id) is { } t) targets.Add(new(t.Color, t.Members));
var request = new SquadRequest { Formation = Formations.Find(args.ElementAtOrDefault(3) ?? "4-2-2-2")!,
    Budget = budget, SalaryCap = 310, Grades = [11], Allocation = args.Contains("--allocation") ? await squads.RankerAllocationAsync(allowFetch: false) : null };
var samples = await squads.RankerSquadsAsync(allowFetch: false);
var ranking = SquadAffiliations.Rank(request, targets, samples, cards, chart.Chart.TeamColors);
var clock = Stopwatch.StartNew();
var plans = await squads.CompareModesAsync(request);
Console.OutputEncoding = System.Text.Encoding.UTF8;
Console.WriteLine(JsonSerializer.Serialize(new
{
    Snapshot = store.LatestFinished(), ChartDate = chart.Chart.ChartDate, ChartFetched = chart.FetchedAt,
    RankerCacheUpdated = store.GetValue("ranker.squads")?.UpdatedAt, RankerSamples = samples.Count,
    Budget = Bp.Format(budget), request.Formation, AllocationSamples = request.Allocation?.Squads, ElapsedSeconds = clock.Elapsed.TotalSeconds,
    Popular = ranking.Take(6).Select(c => new { c.Target.Color.Name, c.NearbyUsers, c.NearbySquads, c.RankerUsers, c.BudgetEvidence }),
    Plans = plans.Select(p => new { p.Label, Price = Bp.Format(p.TotalPrice), p.TotalPay, p.AveragePlayingOvr, p.SelectionNote,
        HasEstimatedOvr = p.Slots.Any(s => s.Final?.Exact != true),
        Affiliation = p.TeamColors.Where(t => t.Color.Category == TeamColorCategory.Affiliation).Select(t => new { t.Color.Name, t.Members }),
        Slots = p.Slots.Select(s => new { s.Position, s.Card.Name, s.Card.Season, Main = s.Card.MainPosition, s.Ovr, s.ShownOvr, Price = Bp.Format(s.Price) }) })
}, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
foreach (var plan in plans)
{
    var affiliation = plan.TeamColors.Single(t => t.Color.Category == TeamColorCategory.Affiliation);
    var members = targets.Single(t => t.Color.Id == affiliation.Color.Id).Members;
    if (plan.Slots.Count != 11 || plan.Slots.Any(s => !members.Contains(s.Card.SpId)) || plan.TotalPrice > budget || plan.TotalPay > 310
        || plan.Slots.Select(s => s.Card.PlayerId).Distinct().Count() != 11) throw new InvalidOperationException("Invalid squad.");
}
return 0;

file sealed class CacheClock : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => new(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
}
file sealed class NoNetwork : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
        throw new InvalidOperationException("Offline audit: a required team-color entry is missing from the cache.");
}
file sealed class NoLists : IMarketListSource
{
    public Task<IReadOnlyList<ListRow>> QueryAsync(ListQuery query, CancellationToken ct = default) => throw new InvalidOperationException("Offline audit: no list requests.");
}
file sealed class NoCharts : IRankerChartSource
{
    public Task<RankerChartData> FetchAsync(int from, int to, CancellationToken ct = default) => throw new InvalidOperationException("Offline audit: no chart requests.");
}
