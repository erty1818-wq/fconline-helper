using FcHelper.Market;
using FcHelper.Services;

namespace FcHelper.Tests;

/// <summary>매칭 카드: salary and value split by line (AM-03).</summary>
public class SquadMoneyTests
{
    private static SquadSlot Slot(int i, string pos, int pay, long price) =>
        new(i, pos, new MarketCard { Group = "X", SpId = i, Name = $"p{i}", Season = "S", Pay = pay }, 5, 140, 0, 0, price, 0, pay, 0, 0, false, false);

    [Fact]
    public void Shares_add_up_and_follow_the_positions()
    {
        var slots = new[] { Slot(0, "GK", 20, 1), Slot(1, "LB", 20, 1), Slot(2, "RCB", 20, 2), Slot(3, "CDM", 30, 2), Slot(4, "ST", 60, 14) };

        var lines = SquadMoney.Of(slots);

        Assert.Equal(["GK", "수비", "미드", "공격"], lines.Select(l => l.Line));
        Assert.Equal(1.0, lines.Sum(l => l.PayShare), 9);
        Assert.Equal(1.0, lines.Sum(l => l.PriceShare), 9);
        var attack = lines[3];
        Assert.Equal((1, 60, 0.4), (attack.Players, attack.Pay, attack.PayShare));
        Assert.Equal(0.7, attack.PriceShare, 9);
        Assert.Equal(2, lines[1].Players);
    }

    [Theory]
    [InlineData("LWB", "수비")]
    [InlineData("CAM", "미드")]
    [InlineData("LW", "공격")]
    [InlineData("GK", "GK")]
    public void Positions_map_to_lines(string position, string line) => Assert.Equal(line, SquadMoney.LineOf(position));
}
