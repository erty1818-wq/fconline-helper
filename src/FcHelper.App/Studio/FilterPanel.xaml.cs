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
        OutlierBox.IsChecked = false;
    }

    /// <returns>The filter, or null with <paramref name="error"/> set when an entry cannot be read.</returns>
    public async Task<CardFilter?> BuildAsync(SquadService? squads, int minRatings, Action<string> status)
    {
        if (!StudioKit.TryPrice(MinPriceBox, 0, out var minPrice) || !StudioKit.TryPrice(MaxPriceBox, long.MaxValue, out var maxPrice))
        {
            status("가격은 1억, 5000만처럼 입력하세요.");
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
            ExcludePriceOutliers = OutlierBox.IsChecked == true,
            MinRatings = minRatings,
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
        return string.Join(" · ", parts);
    }

    private static int? Int(TextBox box) => int.TryParse(box.Text.Trim(), out var v) ? v : null;
}
