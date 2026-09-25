using System.Windows;
using System.Windows.Controls;
using FcHelper.Market;

namespace FcHelper.App.Studio;

/// <summary>Cards trading below the price model's expectation for their spec, inside a price range.</summary>
public partial class ValuePage : UserControl
{
    private const string AnyTrait = "상관없음";

    public ValuePage()
    {
        InitializeComponent();
        GroupBox.ItemsSource = MarketGroups.All;
        GradeBox.ItemsSource = Enumerable.Range(1, 13).Select(g => $"+{g}").ToList();
        GradeBox.SelectedIndex = 7;
        FootBox.ItemsSource = new[] { "상관없음", "4 이상", "5 (양발)" };
        FootBox.SelectedIndex = 0;
        GroupBox.SelectedIndex = 0;
    }

    private MarketGroup Group => (MarketGroup)GroupBox.SelectedItem;

    private void OnGroupChanged(object sender, SelectionChangedEventArgs e)
    {
        if (GroupBox.SelectedItem is not MarketGroup g) return;
        TraitBox.ItemsSource = new[] { AnyTrait }.Concat(g.Traits).ToList();
        TraitBox.SelectedIndex = 0;
    }

    private async void OnSearch(object sender, RoutedEventArgs e)
    {
        if (StudioKit.Squads?.Market is not { } market) return;
        if (!StudioKit.TryPrice(MinBox, 0, out var min) || !StudioKit.TryPrice(MaxBox, long.MaxValue, out var max)) { Status.Text = "가격은 1억, 5000만처럼 입력하세요."; return; }
        var query = new ValueQuery
        {
            Group = Group.Key, Grade = GradeBox.SelectedIndex + 1, MinPrice = min, MaxPrice = max,
            MinWeakFoot = FootBox.SelectedIndex switch { 1 => 4, 2 => 5, _ => 0 },
            Trait = TraitBox.SelectedItem is string t && t != AnyTrait ? t : null,
            SkillMove = SkillBox.IsChecked == true ? 5 : 0,
        };
        var group = Group;
        await StudioKit.Run(SearchButton, Status, async () =>
        {
            Status.Text = "계산 중…";
            var (picks, model) = await Task.Run(() => (market.FindValue(query), market.Model(query.Group, query.Grade)));
            if (model is null)
            {
                Status.Text = market.Status.Current is null ? "아직 시세 데이터가 없습니다." : "이 포지션·강화 단계는 거래되는 카드가 너무 적습니다.";
                Results.ItemsSource = null;
                return;
            }
            Results.ItemsSource = picks.Take(300).Select(p => new ValueRow(p.Card.Name, p.Card.Season, p.Card.OvrAt(p.Grade), p.Card.WeakFoot, p.Card.Pay,
                string.Join(" · ", group.Stats[0].Where(p.Card.Stats.ContainsKey).Select(s => $"{MarketGroups.StatNames.GetValueOrDefault(s, s)} {p.Card.Stats[s]}")),
                Bp.Format(p.Price), Bp.Format(p.Expected), StudioKit.Pct(p.Discount), p.Discount, StudioKit.Tags(p.Card))).ToList();
            Status.Text = $"{group.Name} +{query.Grade} · {picks.Count}장 · 예상가보다 싼 순서 (모델 설명력 R² {model.R2:0.00}, 카드 {model.Cards}장)";
        });
    }
}
