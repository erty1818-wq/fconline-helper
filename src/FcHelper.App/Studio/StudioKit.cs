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

public sealed record PickRow(string Position, string Name, string Season, string Grade, int Ovr, int Users, string Share, string Price, string Expected, string Diff, long SpId, int GradeValue);

public sealed record ValueRow(string Name, string Season, int Ovr, int WeakFoot, int Pay, string Stats, string Price, string Expected, string Diff, double Discount, string Tags);

public sealed record SalaryRow(string Name, string Season, int Ovr, string Effective, int Pay, string PerPay, string Price, string Tags);

public sealed record MoveRow(string Name, string Season, string Grade, string Before, string Now, string Change, string SpecDiff);

public sealed record GradeRow(string Grade, int Ovr, string Price, string PerOvr, string Alternative, string Premium, double BarWidth, bool Competitive);

public sealed record ModeCard(SquadPlan Plan, string Title, string Total, string Ovr, string Effective, string Pay, string TeamColor, string Badge);
