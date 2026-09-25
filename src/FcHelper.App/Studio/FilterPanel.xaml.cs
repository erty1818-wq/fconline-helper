using System.Windows;
using System.Windows.Controls;
using FcHelper.Market;

namespace FcHelper.App.Studio;

public sealed record Choice(string Label, string Value)
{
    public override string ToString() => Label;
}

/// <summary>The detailed card search of the value pages; builds a <see cref="CardFilter"/>.</summary>
public partial class FilterPanel : UserControl
{
    private const string Any = "상관없음";
    private int _defaultMinOvr = 135;

    public FilterPanel()
    {
        InitializeComponent();
        FootBox.ItemsSource = new[] { Any, "4 이상", "5 (양발)" };
        BodyBox.ItemsSource = new[] { new Choice(Any, ""), new Choice("마름", "thin"), new Choice("보통", "normal"), new Choice("건장", "heavy") };
        SetGroup(null);
        Reset();
        _excludedSeasons = LoadExcluded();
        UpdateSeasonButtons();
    }

    // ── seasons ──

    private const string ExcludedKey = "filter.excludedSeasons";
    private HashSet<string> _onlySeasons = [];
    private HashSet<string> _excludedSeasons = [];

    private static HashSet<string> LoadExcluded() =>
        StudioKit.App.Db?.GetValue(ExcludedKey)?.Value is { Length: > 0 } v ? v.Split('|', StringSplitOptions.RemoveEmptyEntries).ToHashSet() : [];

    private void UpdateSeasonButtons()
    {
        OnlySeasonsButton.Content = _onlySeasons.Count == 0 ? "시즌 고르기: 전체" : $"시즌 고르기: {Short(_onlySeasons)}";
        ExcludedSeasonsButton.Content = _excludedSeasons.Count == 0 ? "시즌 제외: 없음" : $"시즌 제외: {Short(_excludedSeasons)}";
    }

    private static string Short(IReadOnlyCollection<string> seasons) =>
        seasons.Count <= 3 ? string.Join(", ", seasons.Order()) : $"{string.Join(", ", seasons.Order().Take(2))} 외 {seasons.Count - 2}개";

    private void OnOnlySeasons(object sender, RoutedEventArgs e) => ShowSeasonPicker((Button)sender, _onlySeasons, remember: false);

    private void OnExcludedSeasons(object sender, RoutedEventArgs e) => ShowSeasonPicker((Button)sender, _excludedSeasons, remember: true);

    /// <summary>A checklist of every season in the market (newest first, with its card count), filtered by typing.</summary>
    private void ShowSeasonPicker(Button anchor, HashSet<string> chosen, bool remember)
    {
        var seasons = StudioKit.Squads?.Pool().GroupBy(c => c.Season).OrderByDescending(g => g.Max(c => c.SeasonId))
            .Select(g => (Season: g.Key, Cards: g.Count())).ToList() ?? [];
        var list = new StackPanel();
        var search = new TextBox { Margin = new Thickness(0, 0, 0, 6), ToolTip = "시즌 이름으로 거르기 (예: TOTS, 26)" };
        void Fill()
        {
            list.Children.Clear();
            foreach (var (season, cards) in seasons.Where(s => search.Text.Trim().Length == 0 || s.Season.Contains(search.Text.Trim(), StringComparison.OrdinalIgnoreCase)))
            {
                var box = new CheckBox { Content = $"{season}  ({cards}장)", IsChecked = chosen.Contains(season), Margin = new Thickness(0, 2, 0, 2) };
                box.Click += (_, _) =>
                {
                    if (box.IsChecked == true) chosen.Add(season);
                    else chosen.Remove(season);
                    Changed();
                };
                list.Children.Add(box);
            }
        }
        void Changed()
        {
            if (remember) StudioKit.App.Db?.SetValue(ExcludedKey, string.Join("|", chosen));
            UpdateSeasonButtons();
        }
        search.TextChanged += (_, _) => Fill();
        var clear = new Button { Content = "모두 해제", Style = (Style)FindResource("Ghost"), Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 6, 6, 0) };
        clear.Click += (_, _) => { chosen.Clear(); Changed(); Fill(); };
        var close = new Button { Content = "닫기", Style = (Style)FindResource("Primary"), Margin = new Thickness(0, 6, 0, 0) };
        var popup = new System.Windows.Controls.Primitives.Popup
        {
            PlacementTarget = anchor, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true,
            Child = new Border
            {
                Background = (System.Windows.Media.Brush)FindResource("Panel"), BorderBrush = (System.Windows.Media.Brush)FindResource("Line"),
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(12), Width = 260,
                Child = new StackPanel
                {
                    Children =
                    {
                        new TextBlock { Text = remember ? "뺄 시즌 (기억됨)" : "이 시즌만 찾기", Style = (Style)FindResource("FieldLabel") },
                        search,
                        new ScrollViewer { Content = list, MaxHeight = 320, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
                        new StackPanel { Orientation = Orientation.Horizontal, Children = { clear, close } },
                    },
                },
            },
        };
        close.Click += (_, _) => popup.IsOpen = false;
        Fill();
        popup.IsOpen = true;
        search.Focus();
    }

    /// <summary>Traits and stats offered for a group (null: every group).</summary>
    public void SetGroup(MarketGroup? group)
    {
        var groups = group is null ? MarketGroups.All : [group];
        var traits = new[] { Any }.Concat(groups.SelectMany(g => g.KeyTraits.Concat(g.Traits)).Distinct()).ToList();
        Trait1Box.ItemsSource = traits;
        Trait2Box.ItemsSource = traits;
        Trait1Box.SelectedIndex = Trait2Box.SelectedIndex = 0;
        // Core stats first, in the order the position needs them.
        var stats = groups.SelectMany(g => g.CoreStats.Select(s => s.Stat).Concat(g.AllStats)).Distinct()
            .Where(s => s is not ("height" or "weight")).Select(s => new Choice(MarketGroups.StatNames.GetValueOrDefault(s, s), s)).ToList();
        foreach (var box in new[] { Stat1Box, Stat2Box, Stat3Box })
        {
            box.ItemsSource = new[] { new Choice(Any, "") }.Concat(stats).ToList();
            box.SelectedIndex = 0;
        }
        _defaultMinOvr = group?.Key == "GK" ? 140 : 135;
        MinOvrBox.Text = _defaultMinOvr.ToString();
    }

    private void OnReset(object sender, RoutedEventArgs e) => Reset();

    private void Reset()
    {
        MinOvrBox.Text = _defaultMinOvr.ToString();
        foreach (var box in new[] { MaxOvrBox, MinPriceBox, MaxPriceBox, MaxPayBox, MinHeightBox, MaxHeightBox, NameBox, Stat1Value, Stat2Value, Stat3Value, CoreGapBox })
            box.Text = "";
        foreach (var box in new[] { FootBox, BodyBox, Trait1Box, Trait2Box, Stat1Box, Stat2Box, Stat3Box }) box.SelectedIndex = 0;
        SkillBox.IsChecked = false;
        TeamColorBox.IsChecked = true;
        _onlySeasons?.Clear(); // the excluded seasons are a standing choice: 조건 초기화 keeps them
        if (OnlySeasonsButton is not null && _excludedSeasons is not null) UpdateSeasonButtons();
    }

    /// <returns>The filter, or null with <paramref name="error"/> set when an entry cannot be read.</returns>
    public async Task<CardFilter?> BuildAsync(SquadService? squads, int minRatings, Action<string> status)
    {
        if (!StudioKit.TryPrice(MinPriceBox, 0, out var minPrice) || !StudioKit.TryPrice(MaxPriceBox, long.MaxValue, out var maxPrice))
        {
            status("가격은 억 단위 숫자로 입력하세요 (예: 1 = 1억, 0.5 = 0.5억).");
            return null;
        }
        var stats = new Dictionary<string, int>();
        foreach (var (box, value) in new[] { (Stat1Box, Stat1Value), (Stat2Box, Stat2Value), (Stat3Box, Stat3Value) })
            if (box.SelectedItem is Choice { Value.Length: > 0 } c && Int(value) is { } v) stats[c.Value] = v;
        IReadOnlySet<long>? members = null;
        if (squads is not null)
        {
            // Fetched even when not filtering: the price model prices team colour membership with it.
            status("랭커 주요 팀컬러 멤버 확인 중… (처음에는 2분쯤 걸립니다)");
            members = await squads.RankerTeamColorMembersAsync();
            if (members.Count == 0 || TeamColorBox.IsChecked != true) members = null; // no chart yet: do not hide everything
        }
        return new CardFilter
        {
            MinPrice = minPrice, MaxPrice = maxPrice,
            MinOvr = Int(MinOvrBox), MaxOvr = Int(MaxOvrBox),
            MinWeakFoot = FootBox.SelectedIndex switch { 1 => 4, 2 => 5, _ => 0 },
            Traits = new[] { Trait1Box, Trait2Box }.Select(b => b.SelectedItem as string).Where(t => t is not null && t != Any).Distinct().ToList()!,
            SkillMove = SkillBox.IsChecked == true ? 5 : 0,
            Body = BodyBox.SelectedItem is Choice { Value.Length: > 0 } body ? body.Value : null,
            MinHeight = Int(MinHeightBox), MaxHeight = Int(MaxHeightBox),
            MaxPay = Int(MaxPayBox),
            MinStats = stats,
            MinCoreGap = double.TryParse(CoreGapBox.Text.Trim(), out var gap) ? gap : null,
            Name = NameBox.Text.Trim() is { Length: > 0 } n ? n : null,
            Members = members,
            MinRatings = minRatings,
            OnlySeasons = _onlySeasons.Count > 0 ? _onlySeasons.ToHashSet() : null,
            ExcludedSeasons = _excludedSeasons.ToHashSet(),
        };
    }

    /// <summary>A short description of the active conditions for the status line.</summary>
    public string Summary(CardFilter f)
    {
        var parts = new List<string>();
        if (f.MinOvr is { } lo) parts.Add($"OVR {lo}+");
        if (f.MaxOvr is { } hi) parts.Add($"OVR ≤{hi}");
        if (f.Members is not null) parts.Add("랭커 팀컬러 20");
        if (f.Traits.Count > 0) parts.Add(string.Join("+", f.Traits));
        if (f.Body is { } b) parts.Add(b switch { "thin" => "마름", "heavy" => "건장", _ => "보통" });
        if (f.MinHeight is not null || f.MaxHeight is not null) parts.Add($"키 {f.MinHeight}~{f.MaxHeight}");
        parts.AddRange(f.MinStats.Select(kv => $"{MarketGroups.StatNames.GetValueOrDefault(kv.Key, kv.Key)}≥{kv.Value}"));
        if (f.MinCoreGap is { } g) parts.Add($"코어 {g:+0;-0}");
        if (f.OnlySeasons is { Count: > 0 } only) parts.Add($"시즌 {Short(only)}");
        if (f.ExcludedSeasons.Count > 0) parts.Add($"제외 {Short(f.ExcludedSeasons)}");
        return string.Join(" · ", parts);
    }

    /// <summary>The OVR floor as typed (for the factor view).</summary>
    public string MinOvrText => MinOvrBox.Text.Trim();

    private static int? Int(TextBox box) => int.TryParse(box.Text.Trim(), out var v) ? v : null;
}
