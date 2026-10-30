using System.Diagnostics;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using FcHelper.Market;

namespace FcHelper.App.Studio;

/// <summary>
/// Everything about one card: its facts and detailed stats at a grade, the team colours it counts for with the partners
/// worth fielding in each, how rankers use it, why it is priced so, cheaper alternatives, its price graph and the
/// footballer's other cards. Cards checked in the lists go into a squad with this one. Opened by double-clicking a card.
/// </summary>
public partial class CardDetailWindow : Window
{
    /// <summary>A row that can be checked for the squad; the checks live in the window so they survive a list refresh.</summary>
    private abstract record CheckRow(long SpId, int GradeValue)
    {
        public HashSet<(long SpId, int Grade)>? Checks { get; init; }
        public Action? Changed { get; init; }
        public bool Picked
        {
            get => Checks?.Any(c => c.SpId == SpId) == true;
            set
            {
                Checks?.RemoveWhere(c => c.SpId == SpId);
                if (value) Checks?.Add((SpId, GradeValue));
                Changed?.Invoke();
            }
        }
    }
    private sealed record PartnerRow(string Role, long CardId, int CardGrade, string Name, string Season, int Ovr, int Pay, string Price, string Expected, string Diff, int Rankers, string Traits) : CheckRow(CardId, CardGrade);
    private sealed record TeammateRow(long CardId, int CardGrade, string Name, string Season, string Grade, int Ovr, string Price, int Together) : CheckRow(CardId, CardGrade);
    private sealed record SimilarRow(long CardId, int CardGrade, string Name, string Season, int Ovr, int Pay, string Price, string Cheaper, string Expected, string Traits) : CheckRow(CardId, CardGrade);
    private sealed record OtherRow(long SpId, string Season, int Ovr, int Pay, int WeakFoot, string Price, int Rankers, string Traits);
    private sealed record WhyRow(string Label, string Percent, string Running, string Detail);

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
    private static readonly int[] RangeDays = [30, 90, 365];

    private readonly MarketCard _card;
    private readonly SquadService _squads;
    private readonly HashSet<(long SpId, int Grade)> _checks = [];
    private CardProfile? _profile;
    private CardColor? _color;
    private IReadOnlyList<long> _daily = [];
    private int _dailyGrade;
    private int _similarGrade;
    private int _whyGrade;

    public CardDetailWindow(MarketCard card, int grade)
    {
        InitializeComponent();
        _card = card;
        _squads = StudioKit.Squads!;
        Title = $"{card.Name} {card.Season} · FC Online Helper";
        Pictures.SetFaceOf(Face, card.SpId);
        Pictures.SetSeasonOf(SeasonIcon, card.Season);
        NameText.Text = $"{card.Name}  {card.Season}";
        RangeBox.ItemsSource = RangeDays.Select(d => $"최근 {d}일").ToList();
        RangeBox.SelectedIndex = 1;
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
        ShowFavorite();
        if (_color is not null) await ShowPartnersAsync(_color);
        await RefreshOpenTabAsync();
    }

    // ── facts and stats ────────────────────────────────────

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
        PriceText.Text = $"시세 +{Grade} {Bp.Format(price)}" + string.Concat(new[] { 8, 10, 11 }.Where(g => g != Grade).Select(g => $"  ·  +{g} {Bp.Format(c.PriceAt(g))}"))
            + (PriceModel.AtFloor(c, Grade) ? "  ·  강화 하한가에 붙어 있음" : "");
        _ = ShowExpectedAsync(Grade, price);
    }

    private ValueQuery QueryOf(int grade) => new() { Group = _card.Group, Grade = grade, Filter = new() { MinOvr = MarketDefaults.MinOvr(grade) } };

    private async Task ShowExpectedAsync(int grade, long price)
    {
        var query = QueryOf(grade);
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
            StatsPanel.Children.Add(new TextBlock { Text = title, Foreground = (Brush)FindResource("Muted"), Margin = new Thickness(0, 6, 0, 2), FontSize = 12 });
            foreach (var s in known.OrderByDescending(s => c.Stats[s]))
                StatsPanel.Children.Add(StatRow(MarketGroups.StatNames.GetValueOrDefault(s, s), c.Stats[s] + bonus, core.Contains(s)));
        }
    }

    private FrameworkElement StatRow(string name, int value, bool isCore)
    {
        var row = new DockPanel { Margin = new Thickness(0, 1, 0, 1) };
        var number = new TextBlock { Text = value.ToString(), Width = 40, TextAlignment = TextAlignment.Right,
            Foreground = (Brush)FindResource(isCore ? "Accent" : "Text"), FontWeight = isCore ? FontWeights.SemiBold : FontWeights.Normal };
        DockPanel.SetDock(number, Dock.Right);
        row.Children.Add(number);
        row.Children.Add(new TextBlock { Text = name, Foreground = (Brush)FindResource("Text"), FontWeight = isCore ? FontWeights.SemiBold : FontWeights.Normal });
        return row;
    }

    // ── colours, rankers, other cards ──────────────────────

    private async Task LoadProfileAsync()
    {
        Status.Text = "팀컬러와 랭커 사용 확인 중…";
        ShowFavorite();
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
            Partners.ItemsSource = partners.Select(p => new PartnerRow(RankerAllocation.RoleName(p.Role), p.Card.SpId, grade, p.Card.Name, p.Card.Season, p.Ovr, p.Card.Pay,
                Bp.Format(p.Price), Bp.Format(p.Expected), StudioKit.Pct(p.Discount), p.RankerUsers + p.SquadUses, TraitsOf(p.Card)) { Checks = _checks, Changed = ShowBuildLabel }).ToList();
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
        Teammates.ItemsSource = p.Squads.Teammates.Select(t => new TeammateRow(t.Card.SpId, t.Grade, t.Card.Name, t.Card.Season, $"+{t.Grade}", t.Card.OvrAt(t.Grade),
            Bp.Format(t.Card.PriceAt(t.Grade)), t.Together) { Checks = _checks, Changed = ShowBuildLabel }).ToList();
    }

    private void ShowOthers()
    {
        if (_profile is not { } p) return;
        Others.ItemsSource = p.OtherCards.Select(c => new OtherRow(c.SpId, c.Season, c.OvrAt(Grade), c.Pay, c.WeakFoot, Bp.Format(c.PriceAt(Grade)),
            p.RankerUsers.GetValueOrDefault(c.SpId), TraitsOf(c))).ToList();
    }

    private static string TraitsOf(MarketCard c) => string.Join(", ", c.Tags.Where(t => t.StartsWith("trait:")).Select(t => t[6..]));

    // ── why this price, similar cards, graph ───────────────

    private async Task RefreshOpenTabAsync()
    {
        if (WhyTab.IsChecked == true) await ShowWhyAsync();
        else if (SimilarTab.IsChecked == true) await ShowSimilarAsync();
        else if (GraphTab.IsChecked == true) await ShowGraphAsync();
    }

    private async Task ShowWhyAsync()
    {
        var grade = Grade;
        if (_whyGrade == grade) return;
        _whyGrade = grade;
        var query = QueryOf(grade);
        var model = await Task.Run(() => _squads.Market.ModelFor(query));
        if (grade != Grade) return;
        if (model is null) { WhyHeader.Text = "이 강화 단계는 가격 모델을 만들 만큼 거래가 없습니다."; WhyRows.ItemsSource = null; return; }
        var why = model.Explain(_card);
        var price = _card.PriceAt(grade);
        WhyHeader.Text = $"기본값 {Bp.Format((long)why.Base)} (OVR·시즌·포지션이 같은 평균 카드)  →  예상가 {Bp.Format(why.Expected)}  ·  실제 시세 {Bp.Format(price)}";
        var running = why.Base;
        WhyRows.ItemsSource = why.Reasons.Select(r =>
        {
            running *= Math.Exp(r.LogEffect);
            return new WhyRow(r.Label, $"{r.Percent:+0;-0}%", Bp.Format((long)running), r.Detail ?? "");
        }).ToList();
        WhyFooter.Text = (why.Expected > (long)why.Predicted ? $"계산값 {Bp.Format((long)why.Predicted)}이 강화 하한가보다 낮아 하한가를 예상가로 씁니다. " : "")
            + $"시장 회귀로 나눈 값이고 게임 내 성능이 아닙니다 [추정]. 예상가와 시세의 차이는 모델이 못 본 수요일 수 있고, 시세가 그쪽으로 움직인다는 뜻이 아닙니다. 검증 오차 {PriceEvidence.ErrorLabel(model.ValidationError)}.";
    }

    private async Task ShowSimilarAsync()
    {
        var grade = Grade;
        if (_similarGrade == grade) return;
        _similarGrade = grade;
        var similar = await Task.Run(() => _squads.SimilarCards(_card, grade));
        if (grade != Grade) return;
        var price = _card.PriceAt(grade);
        SimilarRows.ItemsSource = similar.Select(s => new SimilarRow(s.Card.SpId, grade, s.Card.Name, s.Card.Season, s.Ovr, s.Card.Pay, Bp.Format(s.Price),
            StudioKit.Pct(s.Price / (double)Math.Max(price, 1) - 1), Bp.Format(s.Expected), TraitsOf(s.Card)) { Checks = _checks, Changed = ShowBuildLabel }).ToList();
    }

    private async Task ShowGraphAsync()
    {
        var grade = Grade;
        if (_dailyGrade != grade)
        {
            GraphText.Text = "데이터센터 일별 시세를 받는 중…";
            _dailyGrade = grade;
            try { _daily = await _squads.DailyPricesAsync(_card.SpId, grade); }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { _daily = []; GraphText.Text = "일별 시세를 받지 못했습니다."; _dailyGrade = 0; return; }
            if (grade != Grade) return;
        }
        DrawGraph();
    }

    private void DrawGraph()
    {
        Graph.Children.Clear();
        var days = RangeDays[Math.Max(RangeBox.SelectedIndex, 0)];
        var values = _daily.Skip(Math.Max(0, _daily.Count - days)).Where(v => v > 0).ToList();
        if (values.Count < 2) { GraphText.Text = _dailyGrade == 0 ? GraphText.Text : "이 강화 단계의 일별 시세가 없습니다."; return; }
        var (low, high) = (values.Min(), values.Max());
        GraphText.Text = $"+{_dailyGrade} · 처음 {Bp.Format(values[0])} → 지금 {Bp.Format(values[^1])} ({StudioKit.Pct(values[^1] / (double)values[0] - 1)}) · 최저 {Bp.Format(low)} · 최고 {Bp.Format(high)} · 공식 데이터센터 [직접]";
        var (w, h) = (Graph.ActualWidth, Graph.ActualHeight);
        if (w < 20 || h < 20) return;
        const double left = 70, pad = 10;
        double X(int i) => left + i * (w - left - pad) / (values.Count - 1);
        // Log scale: a fall from 100억 to 50억 looks like one from 2억 to 1억.
        var (a, b) = (Math.Log(low), Math.Log(high));
        double Y(long v) => b - a < 1e-9 ? h / 2 : pad + (b - Math.Log(v)) * (h - 2 * pad) / (b - a);
        foreach (var v in new[] { high, (long)Math.Exp((a + b) / 2), low })
        {
            Graph.Children.Add(new Line { X1 = left, X2 = w - pad, Y1 = Y(v), Y2 = Y(v), Stroke = (Brush)FindResource("Line"), StrokeThickness = 1 });
            var label = new TextBlock { Text = Bp.Format(v), Foreground = (Brush)FindResource("Muted"), FontSize = 11, Width = left - 8, TextAlignment = TextAlignment.Right };
            Canvas.SetLeft(label, 0);
            Canvas.SetTop(label, Y(v) - 8);
            Graph.Children.Add(label);
        }
        var line = new Polyline { Stroke = (Brush)FindResource("Accent"), StrokeThickness = 2 };
        for (var i = 0; i < values.Count; i++) line.Points.Add(new Point(X(i), Y(values[i])));
        Graph.Children.Add(line);
    }

    private void OnRange(object sender, SelectionChangedEventArgs e) { if (Graph is not null) DrawGraph(); }
    private void OnGraphSize(object sender, SizeChangedEventArgs e) => DrawGraph();

    // ── favourite ──────────────────────────────────────────

    private void ShowFavorite()
    {
        var mine = _squads.Favorites().FirstOrDefault(f => f.SpId == _card.SpId && f.Grade == Grade);
        FavoriteButton.Content = mine is null ? "즐겨찾기 추가" : "즐겨찾기 빼기";
        TargetBox.Text = mine is { TargetPrice: > 0 } ? (mine.TargetPrice / (double)Bp.Eok).ToString("0.##") : "";
    }

    private void OnFavorite(object sender, RoutedEventArgs e)
    {
        var mine = _squads.Favorites().Any(f => f.SpId == _card.SpId && f.Grade == Grade);
        if (mine)
        {
            _squads.SetFavorite(_card.SpId, Grade, null);
            Status.Text = $"{_card.Name} +{Grade}을(를) 즐겨찾기에서 뺐습니다.";
        }
        else
        {
            if (!StudioKit.TryPrice(TargetBox, 0, out var target)) { Status.Text = "목표가는 억 단위 숫자로 입력하세요 (예: 10 = 10억)."; return; }
            _squads.SetFavorite(_card.SpId, Grade, target);
            Status.Text = target > 0 ? $"즐겨찾기에 넣었습니다. 시세 갱신 때 {Bp.Format(target)} 아래면 알려 드립니다." : "즐겨찾기에 넣었습니다.";
        }
        ShowFavorite();
    }

    // ── squad ──────────────────────────────────────────────

    private void ShowBuildLabel() => BuildButton.Content = _checks.Count == 0 ? "이 선수로 스쿼드 만들기" : $"이 선수와 체크한 {_checks.Count}명으로 스쿼드 만들기";

    private void OnBuildSquad(object sender, RoutedEventArgs e)
    {
        var cards = new List<(long SpId, int Grade)> { (_card.SpId, Grade) };
        cards.AddRange(_checks.Where(c => c.SpId != _card.SpId));
        var color = _color?.Color;
        (Application.Current as App)?.ShowStudio("squad", page => _ = ((SquadPage)page).BuildAroundAsync(cards, color));
    }

    private void OnTab(object sender, RoutedEventArgs e)
    {
        var tab = (ToggleButton)sender;
        if (ColorsPanel is null) return; // raised while the window is still being built
        foreach (var t in new[] { ColorsTab, RankersTab, WhyTab, SimilarTab, GraphTab, OthersTab }) t.IsChecked = t == tab;
        Visibility Of(ToggleButton t) => tab == t ? Visibility.Visible : Visibility.Collapsed;
        ColorsPanel.Visibility = Of(ColorsTab);
        RankersPanel.Visibility = Of(RankersTab);
        WhyPanel.Visibility = Of(WhyTab);
        SimilarPanel.Visibility = Of(SimilarTab);
        GraphPanel.Visibility = Of(GraphTab);
        Others.Visibility = Of(OthersTab);
        _ = RefreshOpenTabAsync();
    }

    private void OnOpenRow(object sender, MouseButtonEventArgs e)
    {
        var spId = ((DataGrid)sender).SelectedItem switch
        {
            CheckRow r => r.SpId,
            OtherRow r => r.SpId,
            _ => 0L,
        };
        if (spId != 0) Open(spId, Grade, this);
    }

    private void OnOfficial(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo($"https://fconline.nexon.com/DataCenter/PlayerInfo?spid={_card.SpId}&n1Strong={Grade}") { UseShellExecute = true });
}
