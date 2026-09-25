using System.Text.Json;
using FcHelper.Core;
using FcHelper.Market;
using FcHelper.NexonApi;

namespace FcHelper.Tests;

public class FormationLayoutTests
{
    [Theory]
    [InlineData("4-2-2-2")]
    [InlineData("4-2-2-1-1")]
    [InlineData("4-2-3-1")]
    [InlineData("4-1-2-3")]
    [InlineData("4-2-4")]
    [InlineData("4-1-4-1")]
    [InlineData("4-4-2")]
    [InlineData("4-2-1-3")]
    [InlineData("4-1-2-1-2")]
    [InlineData("4-1-3-2")]
    [InlineData("4-3-3")]
    [InlineData("3-4-3")]
    [InlineData("5-2-1-2")]
    public void Presets_have_11_distinct_spots_single_gk_and_match_legacy_slots_and_detect(string name)
    {
        var formation = Formations.Find(name);
        Assert.NotNull(formation);
        var spots = Formations.LayoutOf(name);
        Assert.Equal(11, spots.Count);

        // 1. 11 spots are distinct
        Assert.Equal(11, spots.Distinct().Count());

        // 2. GK is exactly one and at (6, 2)
        var gkSpots = spots.Where(s => s.Row == 6).ToList();
        Assert.Single(gkSpots);
        Assert.Equal(new Spot(6, 2), gkSpots[0]);
        Assert.Equal(new Spot(6, 2), spots[0]);

        // 3. Normalize(PositionAt(spot)) matches Slots[i]
        for (var i = 0; i < 11; i++)
        {
            var pos = Formations.Normalize(Formations.PositionAt(spots[i]));
            Assert.Equal(formation.Slots[i], pos);
        }

        // 4. Detect(spots) returns preset name
        var detected = Formations.Detect(spots);
        Assert.Equal(name, detected);
    }

    [Fact]
    public void Moving_midfielder_to_st_line_in_442_detects_433()
    {
        var spots = Formations.LayoutOf("4-4-2").ToList();
        var lcmIndex = spots.FindIndex(s => s == new Spot(3, 1));
        Assert.True(lcmIndex >= 0);

        var move = Formations.Move(spots, lcmIndex, new Spot(0, 2)); // ST center
        Assert.True(move.Success);
        Assert.Equal("4-3-3", Formations.Detect(move.Spots));
    }

    [Fact]
    public void Moving_striker_to_am_line_in_442_detects_4411()
    {
        var spots = Formations.LayoutOf("4-4-2").ToList();
        var rsIndex = spots.FindIndex(s => s == new Spot(0, 3));
        Assert.True(rsIndex >= 0);

        var move = Formations.Move(spots, rsIndex, new Spot(2, 2));
        Assert.True(move.Success);
        Assert.Equal("4-4-1-1", Formations.Detect(move.Spots));
    }

    [Fact]
    public void Moving_wingbacks_to_def_line_in_5212_still_detects_5212()
    {
        var spots = Formations.LayoutOf("5-2-1-2").ToList();
        var lwbIndex = spots.FindIndex(s => s == new Spot(4, 0));
        var rwbIndex = spots.FindIndex(s => s == new Spot(4, 4));

        var m1 = Formations.Move(spots, lwbIndex, new Spot(5, 0)); // LB
        Assert.True(m1.Success);
        var m2 = Formations.Move(m1.Spots, rwbIndex, new Spot(5, 4)); // RB
        Assert.True(m2.Success);

        Assert.Equal("5-2-1-2", Formations.Detect(m2.Spots));
    }

    [Fact]
    public void Formations_Move_handles_empty_spot_swap_and_gk_constraints()
    {
        var spots = Formations.LayoutOf("4-4-2");

        // 1. Move to empty spot succeeds
        var mEmpty = Formations.Move(spots, 5, new Spot(2, 2));
        Assert.True(mEmpty.Success);
        Assert.Equal(new Spot(2, 2), mEmpty.Spots[5]);

        // 2. Move to occupied spot swaps players
        var spot5 = spots[5]; // LM (3,0)
        var spot6 = spots[6]; // LCM (3,1)
        var mSwap = Formations.Move(spots, 5, spot6);
        Assert.True(mSwap.Success);
        Assert.Equal(spot6, mSwap.Spots[5]);
        Assert.Equal(spot5, mSwap.Spots[6]);

        // 3. GK rule violation rejected
        var mGkToField = Formations.Move(spots, 0, new Spot(0, 2));
        Assert.False(mGkToField.Success);
        Assert.Contains("골키퍼", mGkToField.Reason);

        var mFieldToGk = Formations.Move(spots, 1, new Spot(6, 2));
        Assert.False(mFieldToGk.Success);
        Assert.Contains("골문", mFieldToGk.Reason);

        var mSwapGk = Formations.Move(spots, 5, spots[0]);
        Assert.False(mSwapGk.Success);
        Assert.Contains("골문", mSwapGk.Reason);
    }

    [Fact]
    public void SquadMaker_Slot_computes_ovr_with_CardAbility_for_unlisted_positions_or_falls_back()
    {
        using var temp = new TempDb();
        var store = new MarketStore(temp.Path);
        using var http = new HttpClient();
        var limiter = new RateLimiter(10);
        var abilityClient = new AbilityClient(http, limiter);
        var abilities = new AbilityCache(store, abilityClient);
        var listSource = new DummyMarketListSource();
        var marketService = new MarketService(listSource, store, _ => Task.FromResult("[]"));
        var teamColorClient = new DataCenterTeamColorClient(http, limiter, listSource);
        var teamColors = new TeamColorCache(store, teamColorClient);
        var squads = new SquadService(marketService, store, new DummyChartSource(), teamColors, abilities: abilities);
        var maker = new SquadMaker(squads);

        const long spId = 250123456L;
        var card = new MarketCard
        {
            SpId = spId,
            Name = "선수A",
            Group = "CAM",
            Season = "24TY",
            Ovr1 = 110,
            Pay = 24,
            Positions = new Dictionary<string, int> { ["CAM"] = 110 },
            Prices = new Dictionary<int, long> { [1] = 1000, [8] = 50000 },
        };

        // Case 1: Unlisted position (ST), but CardAbility is available
        // At +1, ST ability is 105. Grade bonus for grade 8 is Bonus[8] - Bonus[1] = 18 - 3 = 15.
        // Expected OVR = 105 + 15 = 120.
        store.SetValue($"ability.{spId}", JsonSerializer.Serialize(new
        {
            Stats = new Dictionary<string, int> { ["sprintspeed"] = 110 },
            Positions = new Dictionary<string, int> { ["ST"] = 105, ["CAM"] = 110 },
        }), DateTime.UtcNow);

        var slotWithAbility = maker.Slot(0, "ST", card, grade: 8);
        Assert.Equal(120, slotWithAbility.Ovr);

        // Case 2: Unlisted position (CB), CardAbility is NOT available for CB -> Fallback to card.OvrAt(grade)
        // card.OvrAt(8) = 110 - 3 + 18 = 125.
        var slotFallback = maker.Slot(1, "CB", card, grade: 8);
        Assert.Equal(125, slotFallback.Ovr);
    }
}

file sealed class DummyMarketListSource : IMarketListSource
{
    public Task<IReadOnlyList<ListRow>> QueryAsync(ListQuery query, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ListRow>>([]);
}

file sealed class DummyChartSource : IRankerChartSource
{
    public Task<RankerChartData> FetchAsync(int rankFrom, int rankTo, CancellationToken ct = default) =>
        Task.FromResult(new RankerChartData("", rankFrom, rankTo, [], [], [], []));
}
