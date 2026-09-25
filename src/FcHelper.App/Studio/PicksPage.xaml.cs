using System.Windows;
using System.Windows.Controls;
using FcHelper.Market;

namespace FcHelper.App.Studio;

public partial class PicksPage : UserControl
{
    public PicksPage()
    {
        InitializeComponent();
        PositionBox.ItemsSource = new[] { "전체" }.Concat(StudioKit.RatedPositions).ToList();
        PositionBox.SelectedIndex = 0;
    }

    private async void OnFind(object sender, RoutedEventArgs e)
    {
        if (StudioKit.Squads is not { } squads) return;
        if (!StudioKit.TryPrice(MinBox, 0, out var min) || !StudioKit.TryPrice(MaxBox, long.MaxValue, out var max)) { Status.Text = "가격은 1억, 5000만처럼 입력하세요."; return; }
        await StudioKit.Run(FindButton, Status, async () =>
        {
            Status.Text = "찾는 중… (처음에는 랭커 데이터를 받느라 1분쯤 걸립니다)";
            var position = PositionBox.SelectedIndex > 0 ? (string)PositionBox.SelectedItem : null;
            var picks = await squads.HiddenRankerPicksAsync(position, min, max, StudioKit.IntOr(UsersBox, 10));
            Results.ItemsSource = picks.Take(300).Select(p => new PickRow(p.Position, p.Card.Name, p.Card.Season, $"+{p.Grade}", p.Ovr, p.Users,
                $"{p.Share:P1}", Bp.Format(p.Price), Bp.Format(p.Expected), StudioKit.Pct(p.Discount), p.Card.SpId, p.Grade)).ToList();
            Status.Text = picks.Count == 0 ? "조건에 맞는 카드가 없습니다. 최소 랭커 수를 낮추거나 가격대를 넓혀 보세요." : $"{picks.Count}장 · 랭커 사용이 많고 싼 순서";
        });
    }

    private void OnLock(object sender, RoutedEventArgs e)
    {
        if (Results.SelectedItem is not PickRow row) { Status.Text = "표에서 선수를 먼저 고르세요."; return; }
        (Window.GetWindow(this) as StudioWindow)?.Navigate("squad", p => ((SquadPage)p).LockCard(row.SpId, row.GradeValue, row.Position));
    }
}
