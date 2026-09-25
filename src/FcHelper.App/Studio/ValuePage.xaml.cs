using System.Windows;
using System.Windows.Controls;
using FcHelper.Market;

namespace FcHelper.App.Studio;

/// <summary>Cards trading below the price model's expectation for their spec, with a detailed search, and what the market pays for each stat and trait.</summary>
public partial class ValuePage : UserControl
{
    public ValuePage()
    {
        InitializeComponent();
        GroupBox.ItemsSource = MarketGroups.All;
        GradeBox.ItemsSource = Enumerable.Range(1, 13).Select(g => $"+{g}").ToList();
        GradeBox.SelectedIndex = 7;
        GroupBox.SelectedIndex = 0;
    }

    private MarketGroup Group => (MarketGroup)GroupBox.SelectedItem;

    private void OnGroupChanged(object sender, SelectionChangedEventArgs e)
    {
        if (GroupBox.SelectedItem is MarketGroup g) Filters.SetGroup(g);
    }

    private void OnView(object sender, RoutedEventArgs e)
    {
        CardsView.IsChecked = ReferenceEquals(sender, CardsView);
        FactorsView.IsChecked = ReferenceEquals(sender, FactorsView);
        TraitsView.IsChecked = ReferenceEquals(sender, TraitsView);
        var table = CardsView.IsChecked != true;
        Results.Visibility = table ? Visibility.Collapsed : Visibility.Visible;
        Factors.Visibility = table ? Visibility.Visible : Visibility.Collapsed;
        if (FactorsView.IsChecked == true) _ = ShowFactorsAsync();
        if (TraitsView.IsChecked == true) _ = ShowTraitsAsync();
    }

    /// <summary>Key new traits averaged over position families (playable market when an OVR floor is set).</summary>
    private async Task ShowTraitsAsync()
    {
        if (StudioKit.Squads is not { } squads) return;
        var grade = GradeBox.SelectedIndex + 1;
        int? floor = int.TryParse(Filters.MinOvrText, out var lo) ? lo : null;
        Status.Text = "계산 중… (처음에는 랭커 팀컬러 멤버를 받느라 2분쯤 걸립니다)";
        try { await squads.RankerTeamColorMembersAsync(); }
        catch (Exception e) when (e is System.Net.Http.HttpRequestException or TaskCanceledException or InvalidOperationException) { }
        var values = await Task.Run(() => squads.Market.TraitValues(grade, floor));
        Factors.ItemsSource = values.Select(t => new FactorRow(t.Trait, t.Scope, $"{t.Percent:+0;-0}%", t.Percent, $"{t.Low:+0;-0} ~ {t.High:+0;-0}%",
            $"{t.OvrEquivalent:+0.0;-0.0}", t.OvrEquivalent, "", t.Cards.ToString(),
            (t.Clear ? "" : "불확실 · ") + string.Join(", ", t.ByGroup.Select(g => $"{MarketGroups.Get(g.Group).Name} {g.Factor.Percent:+0;-0}%")))).ToList();
        Status.Text = $"+{grade}{(floor is { } f ? $" · OVR {f}+ 카드" : " · 전체 카드")} · 신특이 있는 카드가 없는 카드보다 비싼 정도 (포지션별 추정을 정밀도로 가중 평균) [추정: 시장 회귀]";
    }

    private async Task ShowFactorsAsync()
    {
        if (StudioKit.Squads is not { } squads) return;
        var market = squads.Market;
        var grade = GradeBox.SelectedIndex + 1;
        var group = Group;
        Status.Text = "랭커 주요 팀컬러 멤버 확인 중… (처음에는 2분쯤 걸립니다)";
        try { await squads.RankerTeamColorMembersAsync(); }
        catch (Exception e) when (e is System.Net.Http.HttpRequestException or TaskCanceledException or InvalidOperationException) { }
        // The playable market (OVR floor of the filter), else the whole group.
        var floor = int.TryParse(Filters.MinOvrText, out var lo) ? lo : 0;
        if (((floor > 0 ? market.ModelAbove(group.Key, grade, floor) : null) ?? market.Model(group.Key, grade)) is not { } model)
        {
            Status.Text = "이 포지션·강화 단계는 거래되는 카드가 너무 적습니다.";
            Factors.ItemsSource = null;
            return;
        }
        Factors.ItemsSource = FactorRow.For(model, group);
        Status.Text = $"{group.Name} +{grade}{(floor > 0 ? $" · OVR {floor}+" : "")} · 카드 {model.Cards}장 · 중간 가격 {Bp.Format(model.MedianPrice)} · R² {model.R2:0.00}. "
            + "능력치는 같은 OVR에서 +1일 때, 특성·체형은 없는 카드 대비. OVR 환산 = 그만큼 OVR이 높은 카드의 가격 [추정: 시장 회귀, 인과 아님].";
    }

    private async void OnSearch(object sender, RoutedEventArgs e)
    {
        if (StudioKit.Squads is not { } squads) return;
        if (FactorsView.IsChecked == true) { await ShowFactorsAsync(); return; }
        if (TraitsView.IsChecked == true) { await ShowTraitsAsync(); return; }
        var group = Group;
        var grade = GradeBox.SelectedIndex + 1;
        await StudioKit.Run(SearchButton, Status, async () =>
        {
            if (await Filters.BuildAsync(squads, 10, s => Status.Text = s) is not { } filter) return;
            Status.Text = "계산 중…";
            var query = new ValueQuery { Group = group.Key, Grade = grade, Filter = filter };
            var (picks, model) = await Task.Run(() => (squads.Market.FindValue(query), squads.Market.ModelFor(query)));
            if (model is null)
            {
                Status.Text = squads.Market.Status.Current is null ? "아직 시세 데이터가 없습니다." : "이 포지션·강화 단계는 거래되는 카드가 너무 적습니다.";
                Results.ItemsSource = null;
                return;
            }
            Results.ItemsSource = picks.Take(300).Select(p => ValueRow.From(p, group)).ToList();
            Status.Text = $"{group.Name} +{grade} · {picks.Count}장 · 예상가보다 싼 순서 · {Filters.Summary(filter)} (R² {model.R2:0.00}, 카드 {model.Cards}장)";
        });
    }
}
