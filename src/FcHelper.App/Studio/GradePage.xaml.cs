using System.Windows;
using System.Windows.Controls;
using FcHelper.Market;

namespace FcHelper.App.Studio;

public sealed record CardSuggestion(string Label, MarketCard Card);

/// <summary>Price and OVR per enhancement grade against the cheapest equal-OVR alternatives.</summary>
public partial class GradePage : UserControl
{
    private MarketCard? _card;
    private bool _picking;

    public GradePage()
    {
        InitializeComponent();
        var grades = new[] { "-" }.Concat(Enumerable.Range(1, 13).Select(g => $"+{g}")).ToList();
        FromBox.ItemsSource = grades;
        ToBox.ItemsSource = grades;
        FromBox.SelectedIndex = 0;
        ToBox.SelectedIndex = 0;
    }

    /// <summary>Opens the page for a card (from the squad builder).</summary>
    public void Load(long spId, string position, int grade)
    {
        if (StudioKit.Squads?.Card(spId) is not { } card) return;
        Pick(card);
        PositionBox.SelectedItem = Formations.Normalize(position);
        FromBox.SelectedIndex = grade;
        ToBox.SelectedIndex = Math.Min(13, grade + 3);
        OnShow(this, new RoutedEventArgs());
    }

    private void OnNameChanged(object sender, TextChangedEventArgs e)
    {
        if (_picking || StudioKit.Squads is not { } squads || NameBox.Text.Trim().Length < 1) { Suggest.IsOpen = false; return; }
        var text = NameBox.Text.Trim();
        try
        {
            SuggestList.ItemsSource = squads.Pool().Where(c => c.Name.Contains(text, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(c => c.Ovr1).Take(20)
                .Select(c => new CardSuggestion($"{c.Name}  {c.Season}  OVR {c.Ovr1}  {string.Join("/", c.Positions.Keys)}", c)).ToList();
            Suggest.IsOpen = SuggestList.Items.Count > 0;
        }
        catch (InvalidOperationException)
        {
            Status.Text = "시세 데이터가 아직 없습니다.";
        }
    }

    private void OnSuggestPicked(object sender, SelectionChangedEventArgs e)
    {
        if (SuggestList.SelectedItem is CardSuggestion s) Pick(s.Card);
        Suggest.IsOpen = false;
    }

    private void Pick(MarketCard card)
    {
        _card = card;
        _picking = true;
        NameBox.Text = $"{card.Name} {card.Season}";
        _picking = false;
        PositionBox.ItemsSource = card.Positions.OrderByDescending(kv => kv.Value).Select(kv => kv.Key).ToList();
        PositionBox.SelectedIndex = 0;
    }

    private void OnShow(object sender, RoutedEventArgs e)
    {
        if (StudioKit.Squads is not { } squads || _card is null) { Status.Text = "선수를 먼저 고르세요."; return; }
        int? from = FromBox.SelectedIndex > 0 ? FromBox.SelectedIndex : null;
        int? to = ToBox.SelectedIndex > 0 ? ToBox.SelectedIndex : null;
        var a = squads.Grade(_card.SpId, (string)PositionBox.SelectedItem, from, to);
        if (a is null) return;
        Status.Text = "";
        var maxLog = Math.Log10(Math.Max(10, a.Steps.Max(s => s.Price)));
        Steps.ItemsSource = a.Steps.Select(s => new GradeRow($"+{s.Grade}", s.Ovr, Bp.Format(s.Price),
            s.CostPerOvrFromPrevious is { } c ? $"OVR 1당 {Bp.Format(c)}" : "",
            s.Alternative is { } alt ? $"같은 OVR 최저: {alt.Name} {alt.Season} +{s.AlternativeGrade} {Bp.Format(s.AlternativePrice!.Value)}  " : "",
            s.PremiumOverAlternative is { } p ? $"({StudioKit.Pct(p)})" : "",
            Math.Max(4, 250 * Math.Log10(Math.Max(10, s.Price)) / maxLog), a.CompetitiveUpTo is { } up && s.Grade <= up)).ToList();
        Verdict.Visibility = Visibility.Visible;
        Title.Text = $"{a.Card.Name} {a.Card.Season} · {a.Position}";
        Conclusion.Text = a.CompetitiveUpTo is { } u
            ? $"+{u}까지는 같은 OVR을 주는 다른 카드와 비슷한 값입니다. 그 위로는 다른 카드가 더 싸게 같은 OVR을 줍니다."
            : "모든 단계에서 같은 OVR 대안보다 20% 넘게 비쌉니다. OVR이 아니라 특성·팀컬러·체감에 값을 치르는 카드입니다.";
        Upgrade.Text = a.UpgradeCost is { } cost
            ? $"+{a.FromGrade} → +{a.ToGrade}: 시세 차이 {Bp.Format(cost)}" + (a.Alternative is { } alt2 ? $"  ·  같은 OVR 이상 다른 카드: {alt2.Name} {alt2.Season} +{a.AlternativeGrade} {Bp.Format(a.AlternativePrice!.Value)}" : "")
            : "";
    }
}
