using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using FcHelper.Market;

namespace FcHelper.App.Studio;

public sealed record TeamColorItem(string Label, int Id)
{
    public override string ToString() => Label;
}

/// <summary>Build squads under a budget and compare the four modes on one pitch.</summary>
public partial class SquadPage : UserControl
{
    private readonly Dictionary<int, LockedCard> _locked = [];
    private readonly HashSet<int> _excluded = [];
    private readonly List<ToggleButton> _grades = [];
    private SquadPlan? _plan;

    public SquadPage()
    {
        InitializeComponent();
        FormationBox.ItemsSource = Formations.All.Select(f => f.Name).ToList();
        FormationBox.SelectedIndex = 0;
        FormationBox.SelectionChanged += (_, _) => { _locked.Clear(); ShowLocks(); };
        foreach (var g in new[] { 5, 6, 7, 8, 9, 10, 11 })
        {
            var chip = new ToggleButton { Content = $"+{g}", Tag = g, Style = (Style)FindResource("Chip"), IsChecked = g == 8 };
            _grades.Add(chip);
            GradeChips.Children.Add(chip);
        }
        RankBox.ItemsSource = new[] { "상위 10,000명", "상위 1,000명", "상위 100명" };
        RankBox.SelectedIndex = 0;
        TeamColorBox.ItemsSource = new[] { new TeamColorItem("없음", 0) };
        TeamColorBox.SelectedIndex = 0;
        FeatureBox.ItemsSource = new[] { new TeamColorItem("없음", 0) };
        FeatureBox.SelectedIndex = 0;
        Pitch.SlotClicked += ShowDetail;
        Loaded += async (_, _) =>
        {
            await LoadSalaryCapAsync();
            await LoadTeamColorsAsync();
        };
        ShowLocks();
    }

    private (int, int) RankRange => RankBox.SelectedIndex switch { 1 => (1, 1000), 2 => (1, 100), _ => (1, 10000) };

    private int? _capShown;

    /// <summary>Fills the salary cap from the official squad maker unless the user typed their own.</summary>
    private async Task LoadSalaryCapAsync()
    {
        if (StudioKit.Squads is not { } squads) return;
        var typed = CapBox.Text.Trim();
        if (typed.Length > 0 && typed != _capShown?.ToString()) return;
        CapBox.Text = (_capShown = squads.SalaryCap).ToString();
        var cap = await squads.SalaryCapAsync();
        if ((CapBox.Text ?? "").Trim() == _capShown?.ToString()) CapBox.Text = (_capShown = cap).ToString();
    }

    private IReadOnlyList<TeamColor> _catalog = [];

    /// <summary>특성 colours that go with the chosen 소속 one by name ("프랑스" → "2026 프랑스", "프랑스 1기 황금세대").</summary>
    private void OnTeamColorChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TeamColorBox.SelectedItem is not TeamColorItem { Id: > 0 } item) return;
        var name = _catalog.FirstOrDefault(t => t.Id == item.Id)?.Name;
        var keep = FeatureBox.SelectedItem as TeamColorItem;
        var items = new List<TeamColorItem> { new("없음", 0) };
        if (name is not null)
            items.AddRange(_catalog.Where(t => t.Category == TeamColorCategory.Feature && t.Name.Contains(name, StringComparison.Ordinal))
                .OrderByDescending(t => t.Name).Select(t => new TeamColorItem($"{t.Name} ({t.MaxMembers}명)", t.Id)));
        if (keep is { Id: > 0 } && items.All(i => i.Id != keep.Id)) items.Add(keep);
        FeatureBox.ItemsSource = items;
        FeatureBox.SelectedItem = items.FirstOrDefault(i => i.Id == keep?.Id) ?? items[0];
    }

    private async Task LoadTeamColorsAsync()
    {
        if (StudioKit.Squads is not { } squads || TeamColorBox.Items.Count > 1) return;
        try
        {
            _catalog = await squads.TeamColorsAsync();
            var popular = await squads.PopularTeamColorsAsync();
            var items = new List<TeamColorItem> { new("없음", 0) };
            items.AddRange(popular.Select(p => new TeamColorItem($"{p.Color.Name} (랭커 {p.Usage.Share:P0})", p.Color.Id)));
            TeamColorBox.ItemsSource = items;
            TeamColorBox.SelectedIndex = 0;
        }
        catch (Exception e) when (e is HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
            // The list stays at "없음"; building still works.
        }
    }

    /// <summary>Selects a team colour (from the team colour page): 특성 colours go in their own box.</summary>
    public void UseTeamColor(int id, string name, TeamColorCategory category)
    {
        var box = category == TeamColorCategory.Feature ? FeatureBox : TeamColorBox;
        var items = box.ItemsSource.Cast<TeamColorItem>().ToList();
        if (items.All(i => i.Id != id)) items.Add(new TeamColorItem(name, id));
        box.ItemsSource = items;
        box.SelectedItem = items.First(i => i.Id == id);
    }

    /// <summary>Fixes a card into the first free slot of its position (from other pages: "스쿼드에 넣기").</summary>
    public void LockCard(long spId, int grade, string position, bool owned = false)
    {
        var formation = Formations.Find((string)FormationBox.SelectedItem)!;
        var pos = Formations.Normalize(position);
        var index = Enumerable.Range(0, 11).FirstOrDefault(i => formation.Slots[i] == pos && !_locked.ContainsKey(i), -1);
        if (index < 0)
        {
            Status.Text = $"{(string)FormationBox.SelectedItem}에는 비어 있는 {pos} 자리가 없습니다. 포메이션을 바꿔 보세요.";
            return;
        }
        _locked[index] = new LockedCard(spId, grade, owned);
        ShowLocks();
    }

    private void ShowLocks()
    {
        var squads = StudioKit.Squads;
        string Name(long id) => squads?.Card(id) is { } c ? $"{c.Name} {c.Season}" : id.ToString();
        var parts = _locked.OrderBy(kv => kv.Key).Select(kv => $"🔒 {Name(kv.Value.SpId)} +{kv.Value.Grade}{(kv.Value.Owned ? " (보유)" : "")}")
            .Concat(_excluded.Select(p => $"✕ {squads?.Pool().FirstOrDefault(c => c.PlayerId == p)?.Name ?? p.ToString()}"));
        LockInfo.Text = string.Join("   ", parts);
    }

    private void OnBudgetChip(object sender, RoutedEventArgs e) => BudgetBox.Text = (string)((Button)sender).Content;

    private async void OnBuild(object sender, RoutedEventArgs e)
    {
        if (StudioKit.Squads is not { } squads) { Status.Text = "준비 중입니다."; return; }
        if (!StudioKit.TryPrice(BudgetBox, long.MaxValue, out var budget)) { Status.Text = "예산은 100억, 5000만처럼 입력하세요."; return; }
        var grades = _grades.Where(g => g.IsChecked == true).Select(g => (int)g.Tag).ToList();
        if (grades.Count == 0) { Status.Text = "강화 단계를 하나 이상 고르세요."; return; }
        Status.Text = "";
        await StudioKit.Run(BuildButton, Status, async () =>
        {
            Status.Text = "계산 중… (처음에는 랭커 데이터를 받느라 1분쯤 걸립니다)";
            var targets = await squads.TargetsAsync([(TeamColorBox.SelectedItem as TeamColorItem)?.Id ?? 0, (FeatureBox.SelectedItem as TeamColorItem)?.Id ?? 0]);
            var request = new SquadRequest
            {
                Formation = Formations.Find((string)FormationBox.SelectedItem)!,
                Budget = budget,
                SalaryCap = StudioKit.IntOr(CapBox, int.MaxValue),
                Grades = grades,
                Locked = new Dictionary<int, LockedCard>(_locked),
                ExcludedPlayers = new HashSet<int>(_excluded),
                TeamColors = targets,
                RankerPicksOnly = RankerOnlyBox.IsChecked == true,
            };
            var (from, to) = RankRange;
            var plans = await squads.CompareModesAsync(request, from, to);
            Status.Text = plans.Count == 0 ? "조건에 맞는 스쿼드가 없습니다." : "";
            var cheapest = plans.MinBy(p => p.TotalPrice);
            var strongest = plans.MaxBy(p => p.AverageEffectiveOvr);
            Modes.ItemsSource = plans.Select(p => new ModeCard(p, p.Label, Bp.Format(p.TotalPrice),
                $"평균 OVR {p.AverageOvr:0.0}", $"환산 {p.AverageEffectiveOvr:0.0} [추정]", $"급여 {p.TotalPay}",
                string.Join("\n", p.TeamColors.Select(StudioKit.TeamColorLine)),
                ReferenceEquals(p, cheapest) ? "가장 쌈" : ReferenceEquals(p, strongest) ? "가장 강함" : "")).ToList();
            Modes.SelectedIndex = plans.Count > 1 ? 1 : 0;
        });
    }

    private void OnModeSelected(object sender, SelectionChangedEventArgs e)
    {
        if (Modes.SelectedItem is not ModeCard card) return;
        _plan = card.Plan;
        EmptyState.Visibility = Visibility.Collapsed;
        Pitch.Show(card.Plan.Slots);
    }

    private void ShowDetail(SquadSlot s)
    {
        Detail.Children.Clear();
        var c = s.Card;
        Detail.Children.Add(new Image { Source = Skin.Get("player"), Width = 72, Height = 72, HorizontalAlignment = HorizontalAlignment.Left });
        Detail.Children.Add(new TextBlock { Text = c.Name, Style = (Style)FindResource("H1"), Margin = new Thickness(0, 8, 0, 0) });
        Detail.Children.Add(new TextBlock { Text = $"{c.Season} · {s.Position} · +{s.Grade} · 약발 {c.WeakFoot} · 급여 {s.Pay}", Style = (Style)FindResource("Hint") });
        Line("OVR", $"{s.Ovr}" + (s.TeamColorBonus > 0 ? $" + 팀컬러 {s.TeamColorBonus:0.#}" + (s.TeamColorBonus % 1 != 0 ? " [추정]" : "") : ""));
        Line("환산 OVR [추정]", $"{s.EffectiveOvr:0.0}  (시장 가치 {s.Premium:+0.0;-0.0})");
        Line("시세", s.Owned ? "보유 (0으로 계산)" : Bp.Format(s.Price));
        Line("같은 스펙 예상가 [추정]", $"{Bp.Format(s.Expected)}  ({StudioKit.Pct(s.Discount)})");
        if (s.RankerUsers > 0) Line("랭커 사용", $"{s.RankerUsers}명 ({s.RankerShare:P1}, 전날 공식경기)");
        if (c.Tags.Count > 0) Line("특성·개인기·체형", StudioKit.Tags(c));
        var stats = string.Join("  ", c.Stats.Where(kv => MarketGroups.StatNames.ContainsKey(kv.Key)).Take(8).Select(kv => $"{MarketGroups.StatNames[kv.Key]} {kv.Value}"));
        if (stats.Length > 0) Line("능력치 (+1 기준)", stats);

        var actions = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        actions.Children.Add(Action(s.Locked ? "고정 해제" : "이 자리에 고정", () => { if (!_locked.Remove(s.Index)) _locked[s.Index] = new LockedCard(c.SpId, s.Grade, s.Owned); }));
        actions.Children.Add(Action("보유 중 (가격 0)", () => _locked[s.Index] = new LockedCard(c.SpId, s.Grade, true)));
        actions.Children.Add(Action("이 선수 빼기", () => { _locked.Remove(s.Index); _excluded.Add(c.PlayerId); }));
        actions.Children.Add(Action("고정·제외 모두 해제", () => { _locked.Clear(); _excluded.Clear(); }));
        Detail.Children.Add(actions);
        Detail.Children.Add(new TextBlock { Text = "바꾼 뒤 [스쿼드 짜기]를 다시 누르세요.", Style = (Style)FindResource("Hint"), Margin = new Thickness(0, 4, 0, 0) });

        var toGrade = new Button { Content = "강화 효율 보기", Style = (Style)FindResource("Ghost"), Margin = new Thickness(0, 10, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        toGrade.Click += (_, _) => (Window.GetWindow(this) as StudioWindow)?.Navigate("grade", p => ((GradePage)p).Load(c.SpId, s.Position, s.Grade));
        Detail.Children.Add(toGrade);
        _ = ShowRankerStatsAsync(s);
    }

    private async Task ShowRankerStatsAsync(SquadSlot s)
    {
        if (StudioKit.Squads is not { } squads) return;
        var line = new TextBlock { Style = (Style)FindResource("Hint"), Margin = new Thickness(0, 12, 0, 0), Text = "랭커 20경기 기록 불러오는 중…" };
        Detail.Children.Add(line);
        try
        {
            var stats = await squads.RankerStatsAsync([(s.Card.SpId, s.Position)]);
            line.Text = stats.TryGetValue(s.Card.SpId, out var r)
                ? $"랭커가 이 카드로 뛴 {r.Status.MatchCount}경기 평균: 골 {r.Status.Goal:0.00} · 도움 {r.Status.Assist:0.00} · 슈팅 {r.Status.Shoot:0.0} · 패스 성공 {r.Status.PassSuccess:0.0}/{r.Status.PassTry:0.0} (공식 API)"
                : "랭커 20경기 기록: 이 포지션에서 쓴 기록이 없습니다 (API 키가 없으면 표시되지 않습니다).";
        }
        catch (Exception e) when (e is HttpRequestException or FcHelper.NexonApi.NexonApiException or TaskCanceledException)
        {
            line.Text = "랭커 20경기 기록을 불러오지 못했습니다.";
        }
    }

    private void Line(string label, string value)
    {
        Detail.Children.Add(new TextBlock { Text = label, Style = (Style)FindResource("FieldLabel"), Margin = new Thickness(0, 10, 0, 0) });
        Detail.Children.Add(new TextBlock { Text = value });
    }

    private Button Action(string text, Action change)
    {
        var b = new Button { Content = text, Style = (Style)FindResource("Ghost"), Margin = new Thickness(0, 0, 6, 6), Padding = new Thickness(9, 4, 9, 4) };
        b.Click += (_, _) => { change(); ShowLocks(); };
        return b;
    }
}
