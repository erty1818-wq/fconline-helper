using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using FcHelper.Market;

namespace FcHelper.App.Studio;

/// <summary>One card in the search results; the face and the season icon arrive after the row is shown.</summary>
public sealed class SearchRow(long spId, int grade, string name, string season, string positions, int ovr, int pay, string price, string s1, string s2,
    string s3, string s4, string rating, int weakFoot) : INotifyPropertyChanged
{
    private ImageSource? _face, _seasonIcon;
    public long SpId { get; } = spId;
    public int Grade { get; } = grade;
    public string Name { get; } = name;
    public string Season { get; } = season;
    public string Positions { get; } = positions;
    public int Ovr { get; } = ovr;
    public int Pay { get; } = pay;
    public string Price { get; } = price;
    public string S1 { get; } = s1;
    public string S2 { get; } = s2;
    public string S3 { get; } = s3;
    public string S4 { get; } = s4;
    public string Rating { get; } = rating;
    public int WeakFoot { get; } = weakFoot;
    public ImageSource? Face { get => _face; set { _face = value; PropertyChanged?.Invoke(this, new(nameof(Face))); } }
    public ImageSource? SeasonIcon { get => _seasonIcon; set { _seasonIcon = value; PropertyChanged?.Invoke(this, new(nameof(SeasonIcon))); } }
    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// 선수 검색: every condition of the data center's player search — name (initials, several names), seasons, positions,
/// league/club, continent/nation, team colour, grade/적응도/team colour level, OVR, 급여, price, skill moves, reputation,
/// three detailed stats, traits to have or not, foot, height/weight, body, birth, rating, order and the four stat
/// columns. Results come straight from the data center; a full answer (200) is split by OVR and asked again.
/// </summary>
public partial class PlayerSearchPage : UserControl
{
    private const string Any = "상관없음";
    private SearchOptions? _options;
    private readonly List<ToggleButton> _seasons = [];
    private readonly List<(ToggleButton Button, string Group, string Ids)> _positions = [];
    private readonly List<(ToggleButton Button, string Ids)> _bodies = [];
    private readonly List<(ComboBox Stat, TextBox Min, TextBox Max)> _stats = [];
    private readonly Dictionary<string, ImageSource?> _seasonIcons = [];

    public PlayerSearchPage()
    {
        InitializeComponent();
        foreach (var group in new[] { ("FW", "공격수 전체"), ("MF", "미드필더 전체"), ("DF", "수비수 전체") })
        {
            var all = Chip(group.Item2);
            all.Click += (_, _) => { foreach (var p in _positions.Where(p => p.Group == group.Item1)) p.Button.IsChecked = all.IsChecked; };
            PositionGrid.Children.Add(all);
        }
        foreach (var (label, group, ids) in SearchOptions.Positions)
        {
            var b = Chip(label);
            _positions.Add((b, group, ids));
            PositionGrid.Children.Add(b);
        }
        foreach (var (label, ids) in SearchOptions.Bodies)
        {
            var b = Chip(label);
            _bodies.Add((b, ids));
            BodyGrid.Children.Add(b);
        }
        GradeBox.ItemsSource = Enumerable.Range(1, 13).Select(g => $"+{g}").ToList();
        GrowBox.ItemsSource = new[] { "+1", "+5" };
        ColorLevelBox.ItemsSource = Enumerable.Range(0, 10).Select(i => i.ToString(CultureInfo.InvariantCulture)).ToList();
        SkillBox.ItemsSource = new[] { Any, "1성", "2성", "3성", "4성", "5성", "6성" };
        RepBox.ItemsSource = new[] { Any, "1", "2", "3", "4", "5" };
        FootBox.ItemsSource = new[] { Any, "오른발", "왼발" };
        WeakBox.ItemsSource = new[] { Any, "1", "2", "3", "4", "5" };
        BirthMonthBox.ItemsSource = new[] { "월" }.Concat(Enumerable.Range(1, 12).Select(i => $"{i}월")).ToList();
        BirthDayBox.ItemsSource = new[] { "일" }.Concat(Enumerable.Range(1, 31).Select(i => $"{i}일")).ToList();
        OrderDirBox.ItemsSource = new[] { "높은 순", "낮은 순" };
        for (var i = 0; i < 3; i++)
        {
            var stat = new ComboBox { Width = 110, Margin = new Thickness(0, 0, 4, 0) };
            var min = new TextBox { Width = 45, ToolTip = "이상 (+1 기준)" };
            var max = new TextBox { Width = 45, ToolTip = "이하" };
            _stats.Add((stat, min, max));
            StatRows.Children.Add(new StackPanel
            {
                Margin = new Thickness(0, 0, 12, 6),
                Children =
                {
                    new TextBlock { Text = $"세부 능력치 {i + 1}", Style = (Style)FindResource("FieldLabel") },
                    new StackPanel { Orientation = Orientation.Horizontal, Children = { stat, min, new TextBlock { Text = "~", Margin = new Thickness(4, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center }, max } },
                },
            });
        }
        BuildColumns();
        Reset();
        NameBox.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) OnSearch(this, e); };
        Loaded += async (_, _) => await LoadOptionsAsync();
    }

    private ToggleButton Chip(string text) =>
        new() { Content = text, Style = (Style)FindResource("Chip"), Margin = new Thickness(0, 0, 6, 6) };

    // ── options ──

    private async Task LoadOptionsAsync()
    {
        if (_options is not null || StudioKit.Squads is not { } squads) return;
        Status.Text = "검색 조건 목록 받는 중… (처음 한 번, 일주일 보관)";
        try
        {
            _options = await squads.SearchOptionsAsync();
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            Status.Text = "공식 데이터센터에 연결하지 못했습니다. 잠시 뒤 다시 열어 주세요.";
            return;
        }
        if (_options is null) return;
        Status.Text = "";
        LeagueBox.ItemsSource = new[] { new SearchPick("0", "전체") }.Concat(_options.Leagues).ToList();
        LeagueBox.DisplayMemberPath = nameof(SearchPick.Name);
        LeagueBox.SelectedIndex = 0;
        ConfedBox.ItemsSource = new[] { new SearchPick("0", "전체") }.Concat(_options.Confederations).ToList();
        ConfedBox.DisplayMemberPath = nameof(SearchPick.Name);
        ConfedBox.SelectedIndex = 0;
        TeamColorBox.ItemsSource = new[] { new SearchPick("0", "전체") }.Concat(_options.TeamColors.OrderBy(t => t.Name)).ToList();
        TeamColorBox.DisplayMemberPath = nameof(SearchPick.Name);
        TeamColorBox.SelectedIndex = 0;
        var traits = new[] { new SearchPick("", Any) }.Concat(_options.Traits).ToList();
        foreach (var box in new[] { Trait1, Trait2, Trait3, NoTrait1, NoTrait2, NoTrait3 })
        {
            box.ItemsSource = traits;
            box.DisplayMemberPath = nameof(SearchPick.Name);
            box.SelectedIndex = 0;
        }
        var abilities = new[] { new SearchPick("", Any) }.Concat(_options.Abilities).ToList();
        foreach (var (stat, _, _) in _stats)
        {
            stat.ItemsSource = abilities;
            stat.DisplayMemberPath = nameof(SearchPick.Name);
            stat.SelectedIndex = 0;
        }
        var columns = _options.Columns.ToList();
        foreach (var (box, key) in new[] { (Col1, "sprintspeed"), (Col2, "acceleration"), (Col3, "strength"), (Col4, "stamina") })
        {
            box.ItemsSource = columns;
            box.DisplayMemberPath = nameof(SearchPick.Name);
            box.SelectedItem = columns.FirstOrDefault(c => c.Value == key) ?? columns.FirstOrDefault();
        }
        OrderBox.ItemsSource = new[] { new SearchPick("overallrating", "오버롤"), new SearchPick("salary", "급여"), new SearchPick("n8playergrade1", "시세 (+1)"),
            new SearchPick("n4AvgAssessmentPoint", "선수 평점") }.Concat(columns).ToList();
        OrderBox.DisplayMemberPath = nameof(SearchPick.Name);
        OrderBox.SelectedIndex = 0;
        OnLeagueChanged(this, null!);
        OnConfedChanged(this, null!);
        BuildColumns(); // stat column headers now have names
        foreach (var s in _options.Seasons)
        {
            var icon = new Image { Width = 34, Height = 34, Stretch = Stretch.Uniform };
            var b = new ToggleButton
            {
                Tag = s.Id, Style = (Style)FindResource("Chip"), Margin = new Thickness(0, 0, 4, 4), Padding = new Thickness(4, 3, 4, 3), ToolTip = s.Name,
                Content = new StackPanel { Width = 56, Children = { icon, new TextBlock { Text = s.Code, FontSize = 10, HorizontalAlignment = HorizontalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis } } },
            };
            b.Click += (_, _) => UpdateSeasonLabel();
            _seasons.Add(b);
            SeasonGrid.Children.Add(b);
            _ = LoadIconAsync(icon, s.Icon, s.Code);
        }
        UpdateSeasonLabel();
    }

    private async Task LoadIconAsync(Image image, string url, string code)
    {
        var source = await Faces.LoadAsync(url);
        _seasonIcons[code] = source;
        if (source is not null) image.Source = source;
    }

    private void OnLeagueChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_options is null) return;
        var league = int.TryParse((LeagueBox.SelectedItem as SearchPick)?.Value, out var l) ? l : 0;
        ClubBox.ItemsSource = new[] { new SearchClub(0, "전체", 0) }.Concat(_options.Clubs.Where(c => league == 0 || c.LeagueId == league).OrderBy(c => c.Name)).ToList();
        ClubBox.DisplayMemberPath = nameof(SearchClub.Name);
        ClubBox.SelectedIndex = 0;
    }

    private void OnConfedChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_options is null) return;
        var confed = int.TryParse((ConfedBox.SelectedItem as SearchPick)?.Value, out var c) ? c : 0;
        NationBox.ItemsSource = new[] { new SearchNation(0, "전체", 0) }.Concat(_options.Nations.Where(n => confed == 0 || n.ConfederationId == confed).OrderBy(n => n.Name)).ToList();
        NationBox.DisplayMemberPath = nameof(SearchNation.Name);
        NationBox.SelectedIndex = 0;
    }

    private void UpdateSeasonLabel()
    {
        var chosen = _seasons.Where(s => s.IsChecked == true).Select(s => ((StackPanel)s.Content).Children.OfType<TextBlock>().First().Text).ToList();
        SeasonsLabel.Text = chosen.Count == 0 ? "클래스: 전체" : chosen.Count <= 6 ? $"클래스: {string.Join(", ", chosen)}" : $"클래스: {string.Join(", ", chosen.Take(5))} 외 {chosen.Count - 5}개";
    }

    private void OnSeasonsToggle(object sender, RoutedEventArgs e)
    {
        SeasonGrid.Visibility = SeasonsToggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        SeasonsToggle.Content = SeasonsToggle.IsChecked == true ? "클래스 접기" : "클래스 펴기";
    }

    private void OnSeasonsAll(object sender, RoutedEventArgs e) { foreach (var s in _seasons) s.IsChecked = true; UpdateSeasonLabel(); }
    private void OnSeasonsClear(object sender, RoutedEventArgs e) { foreach (var s in _seasons) s.IsChecked = false; UpdateSeasonLabel(); }

    private void OnMoreToggle(object sender, RoutedEventArgs e)
    {
        Conditions.Visibility = MoreToggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        MoreToggle.Content = MoreToggle.IsChecked == true ? "조건 접기" : "조건 펴기";
    }

    private void OnReset(object sender, RoutedEventArgs e) => Reset();

    private void Reset()
    {
        NameBox.Text = "";
        foreach (var s in _seasons) s.IsChecked = false;
        foreach (var p in PositionGrid.Children.OfType<ToggleButton>()) p.IsChecked = false;
        foreach (var b in _bodies) b.Button.IsChecked = false;
        foreach (var box in new[] { LeagueBox, ConfedBox, TeamColorBox, Trait1, Trait2, Trait3, NoTrait1, NoTrait2, NoTrait3, SkillBox, RepBox, FootBox, WeakBox,
                     BirthMonthBox, BirthDayBox, OrderBox, OrderDirBox, GradeBox, GrowBox, ColorLevelBox })
            if (box.Items.Count > 0) box.SelectedIndex = 0;
        foreach (var (stat, min, max) in _stats) { if (stat.Items.Count > 0) stat.SelectedIndex = 0; min.Text = max.Text = ""; }
        foreach (var box in new[] { OvrMinBox, OvrMaxBox, PayMinBox, PayMaxBox, PriceMinBox, PriceMaxBox, HeightMinBox, HeightMaxBox, WeightMinBox, WeightMaxBox,
                     BirthMinBox, BirthMaxBox, RatingMinBox, RatingMaxBox })
            box.Text = "";
        HistoryBox.IsChecked = false;
        if (_seasons.Count > 0) UpdateSeasonLabel();
    }

    // ── search ──

    private static int IntOr(TextBox box, int fallback) => int.TryParse(box.Text.Trim(), out var v) ? v : fallback;
    private static double DoubleOr(TextBox box, double fallback) =>
        double.TryParse(box.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;
    private static int Picked(ComboBox box) => int.TryParse((box.SelectedItem as SearchPick)?.Value, out var v) ? v : 0;
    private static string PickedValue(ComboBox box) => (box.SelectedItem as SearchPick)?.Value ?? "";

    private PlayerSearchQuery? BuildQuery()
    {
        var grade = GradeBox.SelectedIndex + 1;
        var columns = new[] { Col1, Col2, Col3, Col4 }.Select(PickedValue).Select((v, i) => v.Length > 0 ? v : new[] { "sprintspeed", "acceleration", "strength", "stamina" }[i]).ToList();
        return new PlayerSearchQuery
        {
            Names = NameBox.Text.Trim(),
            Seasons = _seasons.Where(s => s.IsChecked == true).Select(s => (int)s.Tag).ToList(),
            PositionIds = _positions.Where(p => p.Button.IsChecked == true).SelectMany(p => p.Ids.Split(',')).ToList(),
            League = Picked(LeagueBox),
            Club = (ClubBox.SelectedItem as SearchClub)?.Id ?? 0,
            Confederation = Picked(ConfedBox),
            Nation = (NationBox.SelectedItem as SearchNation)?.Id ?? 0,
            ClubHistory = HistoryBox.IsChecked == true,
            TeamColor = Picked(TeamColorBox),
            Grade = grade,
            Grow = GrowBox.SelectedIndex == 1 ? 4 : 0,
            TeamColorLevel = Math.Max(0, ColorLevelBox.SelectedIndex),
            OvrMin = IntOr(OvrMinBox, 40), OvrMax = IntOr(OvrMaxBox, 200),
            PayMin = IntOr(PayMinBox, 1), PayMax = IntOr(PayMaxBox, 99),
            SkillMove = Math.Max(0, SkillBox.SelectedIndex),
            Reputation = Math.Max(0, RepBox.SelectedIndex),
            PreferredFoot = Math.Max(0, FootBox.SelectedIndex),
            WeakFoot = Math.Max(0, WeakBox.SelectedIndex),
            Stats = _stats.Where(s => PickedValue(s.Stat).Length > 0).Select(s => (PickedValue(s.Stat), IntOr(s.Min, 40), IntOr(s.Max, 200))).ToList(),
            Traits = new[] { Trait1, Trait2, Trait3 }.Select(PickedValue).Where(v => v.Length > 0).Distinct().ToList(),
            NoTraits = new[] { NoTrait1, NoTrait2, NoTrait3 }.Select(PickedValue).Where(v => v.Length > 0).Distinct().ToList(),
            HeightMin = IntOr(HeightMinBox, 140), HeightMax = IntOr(HeightMaxBox, 250),
            WeightMin = IntOr(WeightMinBox, 40), WeightMax = IntOr(WeightMaxBox, 200),
            Bodies = _bodies.Where(b => b.Button.IsChecked == true).SelectMany(b => b.Ids.Split(',')).ToList(),
            BirthYearMin = IntOr(BirthMinBox, 1900), BirthYearMax = IntOr(BirthMaxBox, 2010),
            BirthMonth = Math.Max(0, BirthMonthBox.SelectedIndex), BirthDay = Math.Max(0, BirthDayBox.SelectedIndex),
            RatingMin = DoubleOr(RatingMinBox, 0), RatingMax = DoubleOr(RatingMaxBox, 10),
            Columns = columns,
            OrderBy = PickedValue(OrderBox) is { Length: > 0 } order ? order : "overallrating",
            Descending = OrderDirBox.SelectedIndex != 1,
        };
    }

    private async void OnSearch(object sender, RoutedEventArgs e)
    {
        if (StudioKit.Squads is not { } squads || BuildQuery() is not { } query) return;
        if (!StudioKit.TryPrice(PriceMinBox, 0, out var priceMin) || !StudioKit.TryPrice(PriceMaxBox, long.MaxValue, out var priceMax))
        {
            Status.Text = "시세는 억 단위 숫자로 입력하세요 (예: 1 = 1억, 0.5 = 0.5억).";
            return;
        }
        SearchButton.IsEnabled = false;
        try
        {
            var (rows, truncated, note) = await squads.PlayerSearchAsync(query, new Progress<string>(m => Status.Text = m));
            var grade = query.Grade;
            var shown = rows.Where(r => r.Prices.GetValueOrDefault(grade) is var p && p >= priceMin && p <= priceMax).ToList();
            var list = shown.Select(r =>
            {
                string Stat(int i) => r.Stats.TryGetValue(query.Columns[i], out var v) ? v.ToString(CultureInfo.InvariantCulture) : "";
                var price = r.Prices.GetValueOrDefault(grade);
                var row = new SearchRow(r.SpId, grade, r.Name, r.Season.ToUpperInvariant(),
                    string.Join(" · ", r.Positions.Select(kv => $"{kv.Key} {kv.Value}")), r.Positions.Count > 0 ? r.Positions.Values.Max() : r.Ovr1, r.Pay,
                    price > 0 ? Bp.Format(price) : "-", Stat(0), Stat(1), Stat(2), Stat(3),
                    r.Rating is { } rt ? $"{rt:0.0} ({r.RatingCount})" : "-", r.WeakFoot);
                return row;
            }).ToList();
            BuildColumns();
            Results.ItemsSource = list;
            foreach (var row in list.Take(300)) _ = FillPicturesAsync(row);
            Status.Text = (list.Count == 0 ? "조건에 맞는 선수가 없습니다." : $"{list.Count}장")
                + (truncated ? " · 결과가 너무 많아 일부만 받았습니다 (조건을 좁혀 보세요)" : "")
                + (shown.Count < rows.Count ? $" · 시세 조건으로 {rows.Count - shown.Count}장 제외" : "")
                + (note is null ? "" : $" · {note}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Status.Text = "공식 데이터센터에 연결하지 못했습니다. 잠시 뒤 다시 눌러 주세요.";
        }
        finally
        {
            SearchButton.IsEnabled = true;
        }
    }

    private async Task FillPicturesAsync(SearchRow row)
    {
        if (_options?.Seasons.FirstOrDefault(s => s.Code.Equals(row.Season, StringComparison.OrdinalIgnoreCase)) is { } season)
            row.SeasonIcon = _seasonIcons.GetValueOrDefault(season.Code) ?? await Faces.LoadAsync(season.Icon);
        if (Faces.Enabled) row.Face = await Faces.GetAsync(row.SpId);
    }

    /// <summary>The result columns; the four stat headers follow the chosen stats.</summary>
    private void BuildColumns()
    {
        Results.Columns.Clear();
        Results.Columns.Add(ImageColumn("", nameof(SearchRow.Face), 40, 34));
        Results.Columns.Add(new DataGridTextColumn { Header = "선수", Binding = new Binding(nameof(SearchRow.Name)), Width = 130 });
        var season = ImageColumn("시즌", nameof(SearchRow.SeasonIcon), 90, 22, nameof(SearchRow.Season));
        season.SortMemberPath = nameof(SearchRow.Season);
        Results.Columns.Add(season);
        Results.Columns.Add(new DataGridTextColumn { Header = "OVR", Binding = new Binding(nameof(SearchRow.Ovr)), Width = 50 });
        Results.Columns.Add(new DataGridTextColumn { Header = "포지션", Binding = new Binding(nameof(SearchRow.Positions)), Width = 150 });
        Results.Columns.Add(new DataGridTextColumn { Header = "급여", Binding = new Binding(nameof(SearchRow.Pay)), Width = 45 });
        Results.Columns.Add(new DataGridTextColumn { Header = $"시세 (+{Math.Max(1, GradeBox.SelectedIndex + 1)})", Binding = new Binding(nameof(SearchRow.Price)), Width = 80 });
        var names = new[] { Col1, Col2, Col3, Col4 }.Select(c => (c.SelectedItem as SearchPick)?.Name ?? "").ToList();
        foreach (var (name, path) in names.Zip(new[] { nameof(SearchRow.S1), nameof(SearchRow.S2), nameof(SearchRow.S3), nameof(SearchRow.S4) }))
            Results.Columns.Add(new DataGridTextColumn { Header = name, Binding = new Binding(path), Width = 70 });
        Results.Columns.Add(new DataGridTextColumn { Header = "약발", Binding = new Binding(nameof(SearchRow.WeakFoot)), Width = 45 });
        Results.Columns.Add(new DataGridTextColumn { Header = "평점", Binding = new Binding(nameof(SearchRow.Rating)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
    }

    private static DataGridTemplateColumn ImageColumn(string header, string imagePath, double width, double size, string? textPath = null)
    {
        var panel = new FrameworkElementFactory(typeof(StackPanel));
        panel.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        var image = new FrameworkElementFactory(typeof(Image));
        image.SetBinding(Image.SourceProperty, new Binding(imagePath));
        image.SetValue(FrameworkElement.WidthProperty, size);
        image.SetValue(FrameworkElement.HeightProperty, size);
        image.SetValue(RenderOptions.BitmapScalingModeProperty, BitmapScalingMode.HighQuality);
        panel.AppendChild(image);
        if (textPath is not null)
        {
            var text = new FrameworkElementFactory(typeof(TextBlock));
            text.SetBinding(TextBlock.TextProperty, new Binding(textPath));
            text.SetValue(FrameworkElement.MarginProperty, new Thickness(4, 0, 0, 0));
            text.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            panel.AppendChild(text);
        }
        return new DataGridTemplateColumn { Header = header, Width = width, CellTemplate = new DataTemplate { VisualTree = panel } };
    }

    private void OnOpenPlayer(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (Results.SelectedItem is not SearchRow row) return;
        Process.Start(new ProcessStartInfo($"https://fconline.nexon.com/DataCenter/PlayerInfo?spid={row.SpId}&n1Strong={row.Grade}") { UseShellExecute = true });
    }
}
