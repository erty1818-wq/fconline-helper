using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using FcHelper.Market;

namespace FcHelper.App.Studio;

/// <summary>
/// Everything about one card: its facts and detailed stats at a grade, the team colours it counts for with the partners
/// worth fielding in each, how rankers use it, and the footballer's other cards. Opened by double-clicking a card.
/// </summary>
public partial class CardDetailWindow : Window
{
    private sealed record PartnerRow(string Role, long SpId, string Name, string Season, int Ovr, int Pay, string Price, string Expected, string Diff, int Rankers, string Traits);
    private sealed record TeammateRow(long SpId, string Name, string Season, string Grade, int Ovr, string Price, int Together);
    private sealed record OtherRow(long SpId, string Season, int Ovr, int Pay, int WeakFoot, string Price, int Rankers, string Traits);

    private static readonly (string Title, string[] Stats)[] StatGroups =
    [
        ("속도", ["sprintspeed", "acceleration"]),
        ("슈팅", ["finishing", "shotpower", "longshots", "positioning", "volleys", "penalties"]),
        ("패스", ["shortpassing", "longpassing", "vision", "crossing", "curve", "freekickaccuracy"]),
        ("드리블", ["dribbling", "ballcontrol", "agility", "balance", "reactions", "composure"]),
        ("수비", ["marking", "standingtackle", "slidingtackle", "interceptions", "headingaccuracy"]),
        ("피지컬", ["strength", "stamina", "aggression", "jumping"]),
        ("골키퍼", ["gkdiving", "gkhandling", "gkkicking", "gkreflexes", "gkpositioning"]),
    ];

    private readonly MarketCard _card;
    private readonly SquadService _squads;
    private CardProfile? _profile;
    private CardColor? _color;

    public CardDetailWindow(MarketCard card, int grade)
    {
        InitializeComponent();
        _card = card;
        _squads = StudioKit.Squads!;
        Title = $"{card.Name} {card.Season} · FC Online Helper";
        Pictures.SetFaceOf(Face, card.SpId);
        Pictures.SetSeasonOf(SeasonIcon, card.Season);
        NameText.Text = $"{card.Name}  {card.Season}";
        GradeBox.ItemsSource = Grades.Tradable.Select(g => $"+{g}").ToList();
        GradeBox.SelectedIndex = Math.Clamp(grade, 1, Grades.MaxTradable) - 1;
        Loaded += async (_, _) => await LoadProfileAsync();
    }

    /// <summary>Opens the card's window; several can stay open side by side to compare. Never on top of the game.</summary>
    public static void Open(long spId, int grade, Window? owner)
    {
        if (StudioKit.Squads?.Card(spId) is not { } card) return;
        new CardDetailWindow(card, grade) { Owner = owner }.Show();
    }

    private int Grade => GradeBox.SelectedIndex + 1;

    private async void OnGrade(object sender, SelectionChangedEventArgs e)
    {
        if (GradeBox.SelectedIndex < 0) return;
        ShowFacts();
        ShowStats();
        ShowOthers();
        if (_color is not null) await ShowPartnersAsync(_color);
    }

    private void ShowFacts()
    {
        var c = _card;
        var skill = MarketGroups.SkillTags.LastOrDefault(s => c.Tags.Contains($"skill:{s}"));
        var body = CardFilter.BodyOf(c) switch { "thin" => "마름", "heavy" => "건장", _ => "보통" };
        var traits = c.Tags.Where(t => t.StartsWith("trait:")).Select(t => t[6..]).ToList();
        var parts = new List<string>
        {
            $"OVR {c.OvrAt(Grade)}", $"급여 {c.Pay}", c.WeakFoot >= 5 ? "양발 (약발 5)" : $"약발 {c.WeakFoot}",
            skill > 0 ? $"개인기 {skill}성" : "개인기 4성 이하", $"체형 {body}",
        };
        if (c.Stats.TryGetValue("height", out var height)) parts.Add($"키 {height}cm");
        if (c.Stats.TryGetValue("weight", out var weight)) parts.Add($"몸무게 {weight}kg");
        parts.Add(traits.Count > 0 ? $"특성 {string.Join(", ", traits)}" : "특성 없음");
        if (c.Rating is { } rating && c.RatingCount > 0) parts.Add($"평점 {rating:0.0} ({c.RatingCount}명)");
        FactsText.Text = string.Join("  ·  ", parts);
        PositionsText.Text = c.Positions.Count == 0 ? "" : "포지션별 OVR: " + string.Join("  ", c.Positions.OrderByDescending(p => p.Value)
            .Select(p => $"{p.Key} {c.OvrAt(p.Key, Grade) ?? p.Value}"));
        var price = c.PriceAt(Grade);
        var query = new ValueQuery { Group = c.Group, Grade = Grade, Filter = new() { MinOvr = MarketDefaults.MinOvr(Grade) } };
        PriceText.Text = $"시세 +{Grade} {Bp.Format(price)}" + string.Concat(new[] { 8, 10, 11 }.Where(g => g != Grade).Select(g => $"  ·  +{g} {Bp.Format(c.PriceAt(g))}"))
            + (PriceModel.AtFloor(c, Grade) ? "  ·  강화 하한가에 붙어 있음" : "");
        _ = ShowExpectedAsync(query, price);
    }

    private async Task ShowExpectedAsync(ValueQuery query, long price)
    {
        var grade = query.Grade;
        var model = await Task.Run(() => _squads.Market.ModelFor(query));
        if (model is null || grade != Grade) return;
        var expected = model.ExpectedAt(_card, grade);
        PriceText.Text += $"  ·  예상가 {Bp.Format(expected)} ({StudioKit.Pct(price / (double)Math.Max(expected, 1) - 1)}, 검증 오차 {PriceEvidence.ErrorLabel(model.ValidationError)}) [추정]";
    }

    private void ShowStats()
    {
        var c = _card;
        var bonus = Grades.Bonus[Grade] - Grades.Bonus[1];
        StatsHint.Text = $"+{Grade} 기준 = +1 능력치 + {bonus} [계산]. 팀컬러·적응도·집중훈련은 뺀 값입니다. 진한 글씨는 이 포지션의 핵심 능력치입니다.";
        var core = MarketGroups.Get(c.Group).CoreStats.Select(s => s.Stat).ToHashSet();
        StatsPanel.Children.Clear();
        var keeper = c.Group == "GK";
        foreach (var (title, stats) in StatGroups)
        {
            var known = stats.Where(c.Stats.ContainsKey).ToList();
            if (known.Count == 0 || (title == "골키퍼" && !keeper)) continue;
            StatsPanel.Children.Add(new TextBlock { Text = title, Foreground = (System.Windows.Media.Brush)FindResource("Muted"), Margin = new Thickness(0, 6, 0, 2), FontSize = 12 });
            foreach (var s in known.OrderByDescending(s => c.Stats[s]))
                StatsPanel.Children.Add(StatRow(MarketGroups.StatNames.GetValueOrDefault(s, s), c.Stats[s] + bonus, core.Contains(s)));
        }
    }

    private FrameworkElement StatRow(string name, int value, bool isCore)
    {
        var row = new DockPanel { Margin = new Thickness(0, 1, 0, 1) };
        var number = new TextBlock { Text = value.ToString(), Width = 40, TextAlignment = TextAlignment.Right,
            Foreground = (System.Windows.Media.Brush)FindResource(isCore ? "Accent" : "Text"), FontWeight = isCore ? FontWeights.SemiBold : FontWeights.Normal };
        DockPanel.SetDock(number, Dock.Right);
        row.Children.Add(number);
        row.Children.Add(new TextBlock { Text = name, Foreground = (System.Windows.Media.Brush)FindResource("Text"), FontWeight = isCore ? FontWeights.SemiBold : FontWeights.Normal });
        return row;
    }

    private async Task LoadProfileAsync()
    {
        Status.Text = "팀컬러와 랭커 사용 확인 중…";
        await StudioKit.Run(null, Status, async () =>
        {
            _profile = await _squads.ProfileAsync(_card);
            ShowColors();
            ShowRankers();
            ShowOthers();
            Status.Text = _profile.ChartDate is { } date ? $"랭커 차트 {date} 기준 · 시세·능력치는 공식 데이터센터" : "랭커 차트가 아직 없습니다.";
            if (_profile.Colors.Count > 0) await SelectColorAsync(_profile.Colors[0]);
        });
    }

    private void ShowColors()
    {
        ColorChips.Children.Clear();
        foreach (var color in _profile!.Colors)
        {
            var chip = new ToggleButton
            {
                Style = (Style)FindResource("Chip"), Tag = color,
                Content = color.Color.Name + (color.RankerShare > 0 ? $" {color.RankerShare:P1}" : ""),
                ToolTip = (color.Color.Category == TeamColorCategory.Feature ? "특성 팀컬러" : "소속 팀컬러") + (color.RankerShare > 0 ? " · 숫자는 랭커 점유율" : ""),
            };
            chip.Click += async (_, _) => await SelectColorAsync(color);
            chip.Checked += async (_, _) => await SelectColorAsync(color);
            ColorChips.Children.Add(chip);
        }
        if (_profile.Colors.Count == 0) ColorEffects.Text = "이 카드의 팀컬러를 확인하지 못했습니다. 데이터센터 연결을 확인하세요.";
    }

    private async Task SelectColorAsync(CardColor color)
    {
        // Checking a chip here raises Checked again: the guard keeps one selection, one search.
        var changed = _color != color;
        _color = color;
        foreach (var chip in ColorChips.Children.OfType<ToggleButton>()) chip.IsChecked = Equals(chip.Tag, color);
        if (changed) await ShowPartnersAsync(color);
    }

    private async Task ShowPartnersAsync(CardColor color)
    {
        var grade = Grade;
        Status.Text = $"{color.Color.Name} 선수 찾는 중…";
        await StudioKit.Run(null, Status, async () =>
        {
            var (detail, partners) = await Task.Run(() => _squads.ColorPartnersAsync(color.Color.Id, _card, grade));
            if (_color != color || grade != Grade) return;
            var levels = detail?.Levels ?? color.Color.Levels;
            ColorEffects.Text = string.Join("   ", levels.Select(l => $"{l.Members}명: {string.Join(", ", l.Effects)}"));
            Partners.ItemsSource = partners.Select(p => new PartnerRow(RankerAllocation.RoleName(p.Role), p.Card.SpId, p.Card.Name, p.Card.Season, p.Ovr, p.Card.Pay,
                Bp.Format(p.Price), Bp.Format(p.Expected), StudioKit.Pct(p.Discount), p.RankerUsers + p.SquadUses, TraitsOf(p.Card))).ToList();
            Status.Text = $"{color.Color.Name} · 역할별 최대 5장, +{grade} · 랭커 칸은 공식 차트 사용자 수와 표본 스쿼드 수의 합입니다. 예상가는 [추정]입니다.";
        });
    }

    private void ShowRankers()
    {
        var p = _profile!;
        var lines = new List<string>();
        lines.Add(p.RankerUses.Count > 0
            ? "공식 랭커 차트: " + string.Join("  ·  ", p.RankerUses.Select(u => $"{u.Position} +{u.Grade} {u.Users}명 ({u.Share:P1})"))
            : "공식 랭커 차트의 포지션별 상위 16장에 없습니다.");
        lines.Add(p.Squads.Sample == 0
            ? "랭커 스쿼드 표본이 아직 없습니다. 스쿼드 짜기의 AI 추천을 쓰면 모입니다."
            : $"상위 랭커 표본 {p.Squads.Sample}팀 중 {p.Squads.Squads}팀이 씁니다"
              + (p.Squads.Slots.Count > 0 ? ": " + string.Join("  ·  ", p.Squads.Slots.Take(6).Select(s => $"{s.Position} +{s.Grade} {s.Count}팀")) : "."));
        RankersText.Text = string.Join("\n", lines);
        Teammates.ItemsSource = p.Squads.Teammates.Select(t => new TeammateRow(t.Card.SpId, t.Card.Name, t.Card.Season, $"+{t.Grade}", t.Card.OvrAt(t.Grade),
            Bp.Format(t.Card.PriceAt(t.Grade)), t.Together)).ToList();
    }

    private void ShowOthers()
    {
        if (_profile is not { } p) return;
        Others.ItemsSource = p.OtherCards.Select(c => new OtherRow(c.SpId, c.Season, c.OvrAt(Grade), c.Pay, c.WeakFoot, Bp.Format(c.PriceAt(Grade)),
            p.RankerUsers.GetValueOrDefault(c.SpId), TraitsOf(c))).ToList();
    }

    private static string TraitsOf(MarketCard c) => string.Join(", ", c.Tags.Where(t => t.StartsWith("trait:")).Select(t => t[6..]));

    private void OnTab(object sender, RoutedEventArgs e)
    {
        var tab = (ToggleButton)sender;
        if (ColorsPanel is null) return; // raised while the window is still being built
        foreach (var t in new[] { ColorsTab, RankersTab, OthersTab }) t.IsChecked = t == tab;
        ColorsPanel.Visibility = tab == ColorsTab ? Visibility.Visible : Visibility.Collapsed;
        RankersPanel.Visibility = tab == RankersTab ? Visibility.Visible : Visibility.Collapsed;
        Others.Visibility = tab == OthersTab ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnOpenRow(object sender, MouseButtonEventArgs e)
    {
        var spId = ((DataGrid)sender).SelectedItem switch
        {
            PartnerRow r => r.SpId,
            TeammateRow r => r.SpId,
            OtherRow r => r.SpId,
            _ => 0L,
        };
        if (spId != 0) Open(spId, Grade, this);
    }

    private void OnOfficial(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo($"https://fconline.nexon.com/DataCenter/PlayerInfo?spid={_card.SpId}&n1Strong={Grade}") { UseShellExecute = true });
}
