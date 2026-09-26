using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using FcHelper.Market;

namespace FcHelper.App.Studio;

/// <summary>Small helpers shared by the studio pages.</summary>
internal static class StudioKit
{
    public static App App => (App)Application.Current;
    public static SquadService? Squads => App.Squads;

    /// <summary>Empty text → <paramref name="empty"/>; otherwise BP like "1억", "2억 5000만".</summary>
    public static bool TryPrice(TextBox box, long empty, out long value)
    {
        value = empty;
        return string.IsNullOrWhiteSpace(box.Text) || Bp.TryParse(box.Text, out value);
    }

    /// <summary>"소속 프랑스 4단계 · 11명 (전체 능력치 +4/드리블 +3) · 선발 전원" for a plan or my squad.</summary>
    public static string TeamColorLine(AppliedTeamColor t) =>
        $"{TeamColor.CategoryLabel(t.Color.Category)} {t.Color.Name} " + (t.Level is { } l
            ? $"{l.Level}단계 · {t.Members}명 ({string.Join("/", l.Effects)}) · {(t.Color.AppliesToSquad ? "선발 전원" : "해당 카드만")}"
            : $"{t.Members}명 (단계 미달)");

    public static int IntOr(TextBox box, int fallback) => int.TryParse(box.Text.Trim(), out var v) ? v : fallback;

    public static readonly string[] RatedPositions = ["ST", "CF", "LW", "RW", "CAM", "LM", "RM", "CM", "CDM", "LB", "RB", "LWB", "RWB", "CB", "GK"];

    public static string Tags(MarketCard c) => string.Join(" · ", c.Tags.Order().Select(MarketGroups.TagLabel));

    public static string Pct(double v) => $"{v * 100:+0;-0}%";

    /// <summary>Runs a page action with the button disabled and errors shown in the status line instead of closing the app.</summary>
    public static async Task Run(Button? button, TextBlock status, Func<Task> action)
    {
        if (button is not null) button.IsEnabled = false;
        try
        {
            await action();
        }
        catch (InvalidOperationException e)
        {
            status.Text = e.Message;
        }
        catch (HttpRequestException)
        {
            status.Text = "데이터센터에 연결하지 못했습니다. 잠시 뒤 다시 시도하세요.";
        }
        catch (TaskCanceledException)
        {
            status.Text = "시간이 초과되었습니다. 다시 시도하세요.";
        }
        finally
        {
            if (button is not null) button.IsEnabled = true;
        }
    }
}

// Display rows: formatted once so the XAML stays plain bindings.

public sealed record PickRow(string Position, string Name, string Season, string Grade, int Ovr, int Users, string Share, string Price, string Expected, string Diff, long SpId, int GradeValue)
{
    public string Core { get; init; } = "";
    public string Height { get; init; } = "";
    public string Tags { get; init; } = "";
    public string Honey { get; init; } = "";
}

public sealed record ValueRow(string Name, string Season, int Ovr, string Core, double CoreValue, int WeakFoot, int Pay, string Height, string Stats,
    string Price, string Expected, string Diff, double Discount, string Tags)
{
    /// <summary>🐝 when the card is a 꿀선수 and is known to trade.</summary>
    public string Honey { get; init; } = "";
    /// <summary>The card, for its mini face in the grid.</summary>
    public long SpId { get; init; }

    public static ValueRow From(ValuePick p, MarketGroup g, CardLiquidity? liquidity = null)
    {
        var c = p.Card;
        var gap = g.CoreGap(c);
        // The position's most important stats first (weight 3, then 2).
        var stats = g.CoreStats.OrderByDescending(s => s.Weight).Where(s => s.Stat is not ("height" or "weight") && c.Stats.ContainsKey(s.Stat)).Take(7)
            .Select(s => $"{MarketGroups.StatNames.GetValueOrDefault(s.Stat, s.Stat)} {c.Stats[s.Stat]}");
        return new ValueRow(c.Name, c.Season, c.OvrAt(p.Grade), gap is { } v ? $"{v:+0.0;-0.0}" : "", gap ?? -99, c.WeakFoot, c.Pay,
            c.Stats.TryGetValue("height", out var h) ? h.ToString() : "", string.Join(" · ", stats),
            Bp.Format(p.Price), Bp.Format(p.Expected), StudioKit.Pct(p.Discount), p.Discount, StudioKit.Tags(c))
        {
            Honey = liquidity is { Tradable: true } && FcHelper.Market.Honey.IsHoney(p.Discount) ? FcHelper.Market.Honey.Mark : "",
            SpId = c.SpId,
        };
    }
}

/// <summary>One price factor of a position (stat, trait, body…) for the "가격 요인" table.</summary>
public sealed record FactorRow(string Name, string Kind, string Percent, double PercentValue, string Range, string Ovr, double OvrValue, string Bp, string Cards, string Verdict)
{
    public static IReadOnlyList<FactorRow> For(PriceModel model, MarketGroup g) =>
        model.Factors()
            .OrderByDescending(f => f.Kind == FactorKind.Trait && f.IsCore)
            .ThenByDescending(f => f.Kind is FactorKind.Stat or FactorKind.Height && f.IsCore)
            .ThenByDescending(f => Math.Abs(f.Percent))
            .Select(f => new FactorRow(f.Name, KindLabel(f), $"{f.Percent:+0.0;-0.0}%", f.Percent, $"{f.Low:+0;-0} ~ {f.High:+0;-0}%",
                $"{f.OvrEquivalent:+0.0;-0.0}", f.OvrEquivalent, (f.BpAtMedian >= 0 ? "+" : "−") + FcHelper.Market.Bp.Format(Math.Abs(f.BpAtMedian)),
                f.Cards?.ToString() ?? "", VerdictOf(f)))
            .ToList();

    private static string KindLabel(PriceFactor f) => f.Kind switch
    {
        FactorKind.Trait => f.IsCore ? "핵심 신특" : "특성",
        FactorKind.Stat => f.IsInflating ? "뻥스탯" : f.IsCore ? "코어 능력치" : "능력치",
        FactorKind.Height => "키·체중",
        FactorKind.Skill => "개인기",
        FactorKind.Body => "체형",
        FactorKind.Foot => "약발",
        FactorKind.TeamColor => "팀컬러",
        _ => "급여",
    };

    private static string VerdictOf(PriceFactor f) =>
        !f.Clear ? "불확실 (범위가 0을 포함)"
        : f.IsInflating && f.Percent < 0 ? "OVR만 올리는 능력치: 같은 OVR이면 싸게 거래"
        : f.Percent > 0 ? "시장이 값을 더 쳐줌" : "시장이 값을 덜 쳐줌";
}

public sealed record SalaryRow(string Name, string Season, int Ovr, string Effective, int Pay, string PerPay, string Price, string Tags);

public sealed record MoveRow(string Name, string Season, string Grade, string Before, string Now, string Change, string SpecDiff);

public sealed record GradeRow(string Grade, int Ovr, string Price, string PerOvr, string Alternative, string Premium, double BarWidth, bool Competitive);

public sealed record ModeCard(SquadPlan Plan, string Title, string Total, string Ovr, string Effective, string Pay, string TeamColor, string Badge)
{
    /// <summary>The full figures (the card shows two lines), as its tooltip.</summary>
    public string Details => $"{Title} · {Total}\n{Ovr}\n{Effective} · {Pay}" + (TeamColor.Length > 0 ? $"\n{TeamColor}" : "");
}
