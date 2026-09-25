using System.Windows;
using System.Windows.Controls;
using FcHelper.Market;

namespace FcHelper.App.Studio;

public partial class TrendsPage : UserControl
{
    private static readonly int[] Days = [1, 7, 30];

    public TrendsPage()
    {
        InitializeComponent();
        GradeBox.ItemsSource = MarketStore.HistoryGrades.Select(g => $"+{g}").ToList();
        GradeBox.SelectedIndex = 2;
        DaysBox.ItemsSource = new[] { "어제 대비", "7일 전 대비", "30일 전 대비" };
        DaysBox.SelectedIndex = 1;
        Loaded += (_, _) => OnShow(this, new RoutedEventArgs());
    }

    private void OnShow(object sender, RoutedEventArgs e)
    {
        if (StudioKit.Squads is not { } squads) return;
        if (!StudioKit.TryPrice(MinBox, 0, out var min)) { Status.Text = "가격은 억 단위 숫자로 입력하세요 (예: 0.1 = 0.1억)."; return; }
        try
        {
            var moves = squads.PriceMoves(MarketStore.HistoryGrades[GradeBox.SelectedIndex], Days[DaysBox.SelectedIndex], min);
            if (moves.Count == 0)
            {
                Status.Text = "비교할 과거 시세가 아직 없습니다. 자동 갱신이 하루 이상 쌓이면 여기에 보입니다.";
                Down.ItemsSource = Up.ItemsSource = null;
                return;
            }
            MoveRow Row(PriceMove m) => new(m.Card.Name, m.Card.Season, $"+{m.Grade}", Bp.Format(m.Before), Bp.Format(m.Now), StudioKit.Pct(m.Change), StudioKit.Pct(m.Discount));
            Down.ItemsSource = moves.Where(m => m.Change < 0).Take(100).Select(Row).ToList();
            Up.ItemsSource = moves.Where(m => m.Change > 0).Reverse().Take(100).Select(Row).ToList();
            var alerts = Advisors.Alerts(moves);
            Status.Text = $"{moves[0].Since:M/d} 대비 {moves.Count}장" + (alerts.Count > 0 ? $" · 급락 + 스펙 대비 쌈: {string.Join(", ", alerts.Select(a => a.Card.Name))}" : "");
        }
        catch (InvalidOperationException ex)
        {
            Status.Text = ex.Message;
        }
    }
}
