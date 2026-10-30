using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FcHelper.Market;

namespace FcHelper.App.Studio;

/// <summary>The cards the user watches, at today's prices. Target prices raise a tray notice after a price refresh.</summary>
public partial class FavoritesPage : UserControl
{
    private sealed record Row(long SpId, int GradeValue, string Name, string Season, string Grade, int Ovr, int Pay, string Price, string Target, string Gap, string State, string Traits);

    public FavoritesPage()
    {
        InitializeComponent();
        Loaded += (_, _) => Show();
    }

    private void Show()
    {
        if (StudioKit.Squads is not { } squads) return;
        var favorites = squads.Favorites();
        var rows = new List<Row>();
        foreach (var f in favorites)
        {
            if (squads.Card(f.SpId) is not { } c) continue;
            var price = c.PriceAt(f.Grade);
            var reached = f.TargetPrice > 0 && price > 0 && price <= f.TargetPrice;
            rows.Add(new Row(c.SpId, f.Grade, c.Name, c.Season, $"+{f.Grade}", c.OvrAt(f.Grade), c.Pay, Bp.Format(price),
                f.TargetPrice > 0 ? Bp.Format(f.TargetPrice) : "", f.TargetPrice > 0 ? StudioKit.Pct(f.TargetPrice / (double)Math.Max(price, 1) - 1) : "",
                reached ? "목표가 도달" : f.TargetPrice > 0 ? "대기" : "", string.Join(", ", c.Tags.Where(t => t.StartsWith("trait:")).Select(t => t[6..]))));
        }
        Results.ItemsSource = rows;
        Status.Text = rows.Count == 0 ? "아직 없습니다. 가성비 찾기에서 선수를 두 번 눌러 [즐겨찾기 추가]를 누르세요." : $"{rows.Count}장 · 시세는 마지막 갱신 기준입니다.";
    }

    private void OnOpen(object sender, MouseButtonEventArgs e)
    {
        if (Results.SelectedItem is Row row) CardDetailWindow.Open(row.SpId, row.GradeValue, Window.GetWindow(this));
    }

    private void OnRemove(object sender, RoutedEventArgs e)
    {
        if (Results.SelectedItem is not Row row) { Status.Text = "표에서 카드를 먼저 고르세요."; return; }
        StudioKit.Squads?.SetFavorite(row.SpId, row.GradeValue, null);
        Show();
    }
}
