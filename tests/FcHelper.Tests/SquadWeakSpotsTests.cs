using FcHelper.Market;
using FcHelper.Services;

namespace FcHelper.Tests;

/// <summary>스쿼드 tab: the slots clearly weaker than the rest, and a cheap keeper (user request 2026-09-26).</summary>
public class SquadWeakSpotsTests
{
    private static SquadSlot Slot(int i, string pos, int ovr, int pay = 20, string group = "X") =>
        new(i, pos, new MarketCard { Group = group, SpId = 100 + i, Name = $"p{i}", Season = "S", Pay = pay }, 5, ovr, 0, 0, 0, 0, pay, 0, 0, false, false);

    [Fact]
    public void Slots_well_below_the_average_and_a_cheap_keeper_are_weak()
    {
        // Ten at 140 and a keeper at 136 on a cheap wage; one full-back at 133.
        var slots = Enumerable.Range(1, 9).Select(i => Slot(i, "ST", 140)).ToList();
        slots.Add(Slot(10, "LB", 133));
        slots.Add(Slot(0, "GK", 138, pay: 18, group: "GK"));

        var (average, spots) = SquadWeakSpots.Of(slots, c => c.Group == "GK" ? 0.1 : 0.5);

        Assert.Equal((9 * 140 + 133 + 138) / 11.0, average, 6);
        Assert.Equal(["LB", "GK"], spots.Select(s => s.Position));
        Assert.Contains("OVR", spots[0].Reason);
        Assert.Contains("저급여 GK", spots[1].Reason);
    }

    [Fact]
    public void An_even_eleven_has_no_weak_spots()
    {
        var slots = Enumerable.Range(0, 11).Select(i => Slot(i, i == 0 ? "GK" : "CB", 140 - i % 2)).ToList();
        Assert.Empty(SquadWeakSpots.Of(slots, _ => 0.5).Spots);
    }

    [Theory]
    [InlineData(0.1, "저급여")]
    [InlineData(0.5, "보통 급여")]
    [InlineData(0.9, "고급여")]
    public void Pay_label_follows_the_quarters(double rank, string label) => Assert.Equal(label, SquadWeakSpots.PayLabel(rank));
}
