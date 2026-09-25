using System.Windows;
using System.Windows.Controls;
using FcHelper.Market;

namespace FcHelper.App;

public sealed record ValueRow(string Name, string Season, int Ovr, int WeakFoot, int Pay, string Stats, string Price,
    string Expected, string Diff, double Discount, string Tags);

/// <summary>
/// Cards trading below what the price model expects for their spec, inside a chosen price range. Opened from the
/// tray, closed (not hidden) to free memory. Searching works on the last finished data even while a refresh runs.
/// </summary>
public partial class ValueWindow : Window
{
    private const string AnyTrait = "상관없음";
    private readonly MarketService _market;

    public ValueWindow(MarketService market)
    {
        _market = market;
        InitializeComponent();
        GroupBox.ItemsSource = MarketGroups.All;
        GradeBox.ItemsSource = Enumerable.Range(1, 13).Select(g => $"+{g}").ToList();
        GradeBox.SelectedIndex = 7;
        FootBox.ItemsSource = new[] { "상관없음", "4 이상", "5 (양발)" };
        FootBox.SelectedIndex = 0;
        GroupBox.SelectedIndex = 0;
        _market.Changed += OnMarketChanged;
        Closed += (_, _) => _market.Changed -= OnMarketChanged;
        ShowStatus();
    }

    private MarketGroup Group => (MarketGroup)GroupBox.SelectedItem;
    private int Grade => GradeBox.SelectedIndex + 1;

    private void OnGroupChanged(object sender, SelectionChangedEventArgs e)
    {
        if (GroupBox.SelectedItem is not MarketGroup g) return;
        TraitBox.ItemsSource = new[] { AnyTrait }.Concat(g.Traits).ToList();
        TraitBox.SelectedIndex = 0;
    }

    private async void OnSearch(object sender, RoutedEventArgs e)
    {
        if (!TryPrice(MinBox.Text, 0, out var min) || !TryPrice(MaxBox.Text, long.MaxValue, out var max))
        {
            SummaryText.Text = "가격은 1억, 5000만, 2억 5000만, 1.5조처럼 입력하세요.";
            return;
        }
        var query = new ValueQuery
        {
            Group = Group.Key, Grade = Grade, MinPrice = min, MaxPrice = max,
            MinWeakFoot = FootBox.SelectedIndex switch { 1 => 4, 2 => 5, _ => 0 },
            Trait = TraitBox.SelectedItem is string t && t != AnyTrait ? t : null,
            SkillMove = SkillBox.IsChecked == true ? 5 : 0,
        };
        SearchButton.IsEnabled = false;
        SummaryText.Text = "계산 중…";
        try
        {
            // Fitting the model reads a few thousand cards: off the UI thread.
            var (picks, model) = await Task.Run(() => (_market.FindValue(query), _market.Model(query.Group, query.Grade)));
            if (model is null)
            {
                SummaryText.Text = _market.Status.Current is null
                    ? "아직 시세 데이터가 없습니다. 처음 데이터를 받는 중이면 끝날 때까지 기다려 주세요."
                    : "이 포지션·강화 단계는 거래되는 카드가 너무 적어 계산하지 못했습니다.";
                Results.ItemsSource = null;
                return;
            }
            var stats = Group.Stats[0];
            Results.ItemsSource = picks.Take(300).Select(p => new ValueRow(
                p.Card.Name, p.Card.Season, p.Card.OvrAt(p.Grade), p.Card.WeakFoot, p.Card.Pay,
                string.Join(" · ", stats.Where(p.Card.Stats.ContainsKey).Select(s => $"{MarketGroups.StatNames.GetValueOrDefault(s, s)} {p.Card.Stats[s]}")),
                Bp.Format(p.Price), Bp.Format(p.Expected), $"{p.Discount * 100:+0;-0}%", p.Discount,
                string.Join(" ", p.Card.Tags.Order().Select(MarketGroups.TagLabel)))).ToList();
            SummaryText.Text = $"{Group.Name} +{Grade} · 조건에 맞는 카드 {picks.Count}장 · 예상가보다 싼 순서 (모델 설명력 R² {model.R2:0.00}, 카드 {model.Cards}장으로 계산)";
        }
        finally
        {
            SearchButton.IsEnabled = true;
        }
    }

    private static bool TryPrice(string text, long empty, out long value)
    {
        value = empty;
        return string.IsNullOrWhiteSpace(text) || Bp.TryParse(text, out value);
    }

    private async void OnRefresh(object sender, RoutedEventArgs e)
    {
        RefreshButton.IsEnabled = false;
        try
        {
            await _market.RefreshIfDueAsync(force: true);
        }
        finally
        {
            RefreshButton.IsEnabled = true;
        }
    }

    private void OnMarketChanged() => Dispatcher.BeginInvoke(ShowStatus);

    private void ShowStatus()
    {
        var s = _market.Status;
        var current = s.Current?.FinishedAt is { } at
            ? $"시세 기준 {at.ToLocalTime():M/d HH:mm} · {s.Cards:#,0}장"
            : "시세 데이터 없음";
        StatusText.Text = s.Running is { } r
            ? $"{current} · 갱신 중 {r.Done}/{r.Total}" + (s.Current is null ? " (처음은 약 20분 걸립니다)" : " (그동안 이전 데이터로 검색됩니다)")
            : s.LastError is { } err ? $"{current} · {err}"
            : _market.NextDue is { } due ? $"{current} · 다음 자동 갱신 {due.ToLocalTime():M/d HH:mm} 이후" : current;
        RefreshButton.IsEnabled = s.Running is null;
    }
}
