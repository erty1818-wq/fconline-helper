using System.Windows;
using System.Windows.Controls;
using FcHelper.Market;

namespace FcHelper.App.Studio;

public partial class SalaryPage : UserControl
{
    public SalaryPage()
    {
        InitializeComponent();
        PositionBox.ItemsSource = StudioKit.RatedPositions;
        PositionBox.SelectedIndex = 0;
        GradeBox.ItemsSource = Grades.Tradable.Select(g => $"+{g}").ToList();
        GradeBox.SelectedIndex = 7;
    }

    private async void OnFind(object sender, RoutedEventArgs e)
    {
        if (StudioKit.Squads is not { } squads) return;
        if (!StudioKit.TryPrice(MinBox, 0, out var min) || !StudioKit.TryPrice(MaxBox, long.MaxValue, out var max)) { Status.Text = "가격은 억 단위 숫자로 입력하세요 (예: 1 = 1억, 0.5 = 0.5억)."; return; }
        var position = (string)PositionBox.SelectedItem;
        var grade = GradeBox.SelectedIndex + 1;
        var minOvr = StudioKit.IntOr(MinOvrBox, 0);
        await StudioKit.Run(FindButton, Status, async () =>
        {
            var list = await Task.Run(() => squads.SalaryEfficiency(position, grade, min, max, minOvr));
            Results.ItemsSource = list.Take(300).Select(s => new SalaryRow(s.Card.Name, s.Card.Season, s.Ovr, $"{s.EffectiveOvr:0.0}", s.Pay, $"{s.OvrPerPay:0.00}",
                Bp.Format(s.Price), StudioKit.Tags(s.Card))).ToList();
            Status.Text = $"{position} +{grade} · {list.Count}장 · 급여 1당 환산 OVR 높은 순";
        });
    }
}
