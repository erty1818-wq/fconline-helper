using FcHelper.Market;

namespace FcHelper.Tests;

public class PatchBaselineTests
{
    [Fact]
    public void Registered_baseline_keeps_cards_tags_and_history_past_normal_retention()
    {
        using var temp = new TempDb();
        var store = new MarketStore(temp.Path);
        var before = new DateTime(2026, 9, 28, 0, 0, 0, DateTimeKind.Utc);
        var baseline = store.StartSnapshot("Full", before);
        var row = new ListRow(100000001, "p1", "S", 25, 120, 4, 7, 30,
            new Dictionary<int, long> { [1] = 1000, [8] = 100000000 }, new Dictionary<string, int>());
        store.SaveRows(baseline, "W", [row]);
        store.SaveTag(baseline, "W", [row.SpId], "trait:스피드스터");
        store.FinishSnapshot(baseline, before);
        store.RecordPriceHistory(baseline, before);
        for (var day = 0; day < 6; day++)
        {
            var after = before.AddDays(40 + day);
            var id = store.StartSnapshot("Prices", after);
            store.SaveRows(id, "W", [row]);
            store.FinishSnapshot(id, after);
            store.RecordPriceHistory(id, after);
        }
        var pin = Assert.Single(store.PatchBaselines());
        Assert.Equal(baseline, pin.Snapshot);
        Assert.Equal(DateOnly.FromDateTime(before), pin.HistoryDay);
        Assert.Equal(MarketStore.KeepSnapshots + 1, store.Snapshots().Count);
        Assert.Contains("trait:스피드스터", Assert.Single(store.LoadCards(baseline)).Tags);
        Assert.Equal(100000000, store.PriceHistory(DateOnly.FromDateTime(before))[("W", row.SpId)][8]);
    }

    [Fact]
    public void Registration_uses_finished_prices_before_patch_and_does_not_invent_missing_data()
    {
        using var temp = new TempDb();
        var store = new MarketStore(temp.Path);
        var patch = new DateTime(2026, 10, 20, 15, 0, 0, DateTimeKind.Utc);
        var unfinished = store.StartSnapshot("Full", patch.AddDays(-1));
        store.RegisterPatch(patch, "future patch");
        Assert.Null(store.PatchBaselines().Single(p => p.PatchAt == patch).Snapshot);
        store.FinishSnapshot(unfinished, patch.AddHours(1));
        Assert.Null(store.PatchBaselines().Single(p => p.PatchAt == patch).Snapshot);
        var before = store.StartSnapshot("Full", patch.AddDays(-2));
        store.FinishSnapshot(before, patch.AddDays(-2));
        Assert.Equal(before, store.PatchBaselines().Single(p => p.PatchAt == patch).Snapshot);
    }
}
