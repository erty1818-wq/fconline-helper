using System.IO.Compression;
using System.Text.Json;
using FcHelper.Market;

namespace FcHelper.Tests;

public class MarketExportTests
{
    private static ListRow Row(long spId, int ovr, long price8) =>
        new(spId, $"p{spId}", "S", 25, ovr, 4, 7, 30, new Dictionary<int, long> { [1] = 1000, [5] = price8 / 4, [8] = price8, [11] = price8 * 3 },
            new Dictionary<string, int> { ["sprintspeed"] = ovr + 3 }) { Positions = new Dictionary<string, int> { ["RW"] = ovr } };

    private static List<JsonElement> ReadLines(byte[] gz)
    {
        using var reader = new StreamReader(new GZipStream(new MemoryStream(gz), CompressionMode.Decompress));
        return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => JsonDocument.Parse(l).RootElement.Clone()).ToList();
    }

    [Fact]
    public void Exports_snapshots_history_colours_and_ranker_usage_without_nicknames()
    {
        using var temp = new TempDb();
        var store = new MarketStore(temp.Path);
        var day0 = new DateTime(2026, 9, 28, 0, 0, 0, DateTimeKind.Utc);
        for (var d = 0; d < 3; d++)
        {
            var at = day0.AddDays(d * 3);
            var id = store.StartSnapshot(d == 0 ? "Full" : "Prices", at);
            // The same card is listed under two position groups: it must come out once, with both groups.
            store.SaveRows(id, "W", [Row(100000001, 120, 100_000_000 + d * 10_000_000), Row(100000002, 118, 50_000_000)]);
            store.SaveRows(id, "SM", [Row(100000001, 120, 100_000_000 + d * 10_000_000)]);
            store.SaveTag(id, "W", [100000001], "trait:스피드스터");
            store.FinishSnapshot(id, at);
            store.RecordPriceHistory(id, at);
        }
        store.SaveTeamColors([new TeamColor(1016, "FC 바르셀로나", TeamColorCategory.Affiliation, 11, [])], day0);
        store.SaveTeamColorMembers(1016, [100000001]);
        store.SaveChart(new RankerChartData("2026-10-01", 1, 1000, [new RankerPick("RW", 100000001, "p100000001", 8, 140, 25, 321, 0.32)],
            [new FormationUsage("4-2-3-1", 400, 0.4)], [], [new TeamColorUsage("FC 바르셀로나", 120, 0.12) { Id = 1016 }]), day0);

        using var output = new MemoryStream();
        var summary = MarketExport.Write(temp.Path, output);
        var lines = ReadLines(output.ToArray());

        var meta = lines.Single(l => l.GetProperty("t").GetString() == "meta");
        var latest = meta.GetProperty("latestSnapshot").GetInt64();
        Assert.Equal(3, meta.GetProperty("snapshots").GetArrayLength());
        Assert.Equal(["2026-09-28", "2026-10-01", "2026-10-04"], meta.GetProperty("historyDays").EnumerateArray().Select(d => d.GetString()));
        Assert.Contains(meta.GetProperty("patches").EnumerateArray(), p => p.GetProperty("historyDay").GetString() == "2026-09-28");

        var cards = lines.Where(l => l.GetProperty("t").GetString() == "card").ToList();
        Assert.Equal(6, cards.Count); // 2 cards x 3 snapshots, not one per group
        var latestCard = cards.Single(c => c.GetProperty("snapshot").GetInt64() == latest && c.GetProperty("spId").GetInt64() == 100000001);
        Assert.Equal(["SM", "W"], latestCard.GetProperty("groups").EnumerateArray().Select(g => g.GetString()!).Order());
        Assert.Equal(["trait:스피드스터"], latestCard.GetProperty("tags").EnumerateArray().Select(t => t.GetString()));
        Assert.Equal(120_000_000, latestCard.GetProperty("prices").GetProperty("8").GetInt64());
        Assert.Equal(123, latestCard.GetProperty("stats").GetProperty("sprintspeed").GetInt32());
        Assert.Equal(120, latestCard.GetProperty("positions").GetProperty("RW").GetInt32());
        var olderCard = cards.First(c => c.GetProperty("snapshot").GetInt64() != latest);
        Assert.False(olderCard.TryGetProperty("stats", out _)); // older snapshots carry only what a before/after needs

        var series = lines.Where(l => l.GetProperty("t").GetString() == "series").ToList();
        Assert.Equal(2, series.Count);
        var s1 = series.Single(s => s.GetProperty("spId").GetInt64() == 100000001);
        Assert.Equal([100_000_000L, 110_000_000L, 120_000_000L], s1.GetProperty("p").GetProperty("8").EnumerateArray().Select(p => p.GetInt64()));
        Assert.False(s1.GetProperty("p").TryGetProperty("11", out _)); // history keeps only MarketStore.HistoryGrades

        var colour = lines.Single(l => l.GetProperty("t").GetString() == "teamColor");
        Assert.Equal([100000001L], colour.GetProperty("members").EnumerateArray().Select(m => m.GetInt64()));
        var ranker = lines.Single(l => l.GetProperty("t").GetString() == "ranker");
        Assert.Equal(321, ranker.GetProperty("picks")[0].GetProperty("users").GetInt32());
        Assert.Equal(1016, ranker.GetProperty("teamColors")[0].GetProperty("id").GetInt32());

        Assert.Equal(new MarketExportSummary(3, 6, 2, 3, 1, 1, "2026-09-28", "2026-10-04"), summary);
        var text = string.Join("\n", lines.Select(l => l.GetRawText()));
        Assert.DoesNotContain("nickname", text, StringComparison.OrdinalIgnoreCase);
    }
}
