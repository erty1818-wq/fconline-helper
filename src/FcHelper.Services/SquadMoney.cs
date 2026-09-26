using FcHelper.Market;

namespace FcHelper.Services;

/// <summary>One line of the eleven (GK, 수비, 미드, 공격): how much of the salary and of the market value it takes [계산].</summary>
public sealed record LineShare(string Line, int Players, int Pay, double PayShare, long Price, double PriceShare);

/// <summary>
/// Where the opponent put their money (매칭 카드, AM-03): salary and market value split by line. Managers who pour
/// both into the attack tend to leave the keeper or full-backs weak (see <see cref="SquadWeakSpots"/>).
/// </summary>
public static class SquadMoney
{
    public static readonly string[] Lines = ["GK", "수비", "미드", "공격"];

    public static string LineOf(string position) => position switch
    {
        "GK" => "GK",
        "CB" or "LCB" or "RCB" or "SW" or "LB" or "RB" or "LWB" or "RWB" => "수비",
        "ST" or "LS" or "RS" or "CF" or "LF" or "RF" or "LW" or "RW" => "공격",
        _ => "미드",
    };

    public static IReadOnlyList<LineShare> Of(IReadOnlyList<SquadSlot> slots)
    {
        var pay = Math.Max(1, slots.Sum(s => s.Pay));
        var price = Math.Max(1, slots.Sum(s => s.Price));
        return Lines.Select(line =>
        {
            var inLine = slots.Where(s => LineOf(s.Position) == line).ToList();
            return new LineShare(line, inLine.Count, inLine.Sum(s => s.Pay), (double)inLine.Sum(s => s.Pay) / pay,
                inLine.Sum(s => s.Price), (double)inLine.Sum(s => s.Price) / price);
        }).ToList();
    }
}
