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

    private void OnPositionChanged(object sender, SelectionChangedEventArgs e) =>
        Filters.SetGroup(PositionBox.SelectedIndex > 0 ? MarketGroups.Get(Formations.GroupOf(Formations.Normalize((string)PositionBox.SelectedItem))) : null);

    private async void OnFind(object sender, RoutedEventArgs e)
    {
        if (StudioKit.Squads is not { } squads) return;
        await StudioKit.Run(FindButton, Status, async () =>
        {
            if (await Filters.BuildAsync(squads, 0, s => Status.Text = s) is not { } filter) return;
            Status.Text = "찾는 중… (처음에는 랭커 데이터를 받느라 1분쯤 걸립니다)";
            var position = PositionBox.SelectedIndex > 0 ? (string)PositionBox.SelectedItem : null;
            var found = await squads.HiddenRankerPicksAsync(position, filter, StudioKit.IntOr(UsersBox, 10));
            var bad = await squads.IlliquidAsync(found.Take(30).Select(p => (p.Card.SpId, p.Grade)), new Progress<string>(m => Status.Text = $"상위 30장 {m}"));
            var picks = found.Where(p => !bad.Contains((p.Card.SpId, p.Grade))).ToList();
            Results.ItemsSource = picks.Take(300).Select(p => new PickRow(p.Position, p.Card.Name, p.Card.Season, $"+{p.Grade}", p.Ovr, p.Users,
                $"{p.Share:P1}", Bp.Format(p.Price), Bp.Format(p.Expected), StudioKit.Pct(p.Discount), p.Card.SpId, p.Grade)
            {
                Core = MarketGroups.Get(p.Card.Group).CoreGap(p.Card) is { } gap ? $"{gap:+0.0;-0.0}" : "",
                Height = p.Card.Stats.TryGetValue("height", out var h) ? h.ToString() : "",
                Tags = StudioKit.Tags(p.Card),
            }).ToList();
            Status.Text = picks.Count == 0 ? "조건에 맞는 카드가 없습니다. 최소 랭커 수를 낮추거나 조건을 넓혀 보세요." : $"{picks.Count}장 · 랭커 사용이 많고 싼 순서 · {Filters.Summary(filter)}" + (bad.Count > 0 ? $" · 거래가 거의 없는 {bad.Count}장 제외" : "");
        });
    }

    private void OnLock(object sender, RoutedEventArgs e)
    {
        if (Results.SelectedItem is not PickRow row) { Status.Text = "표에서 선수를 먼저 고르세요."; return; }
        (Window.GetWindow(this) as StudioWindow)?.Navigate("squad", p => ((SquadPage)p).LockCard(row.SpId, row.GradeValue, row.Position));
    }
}
