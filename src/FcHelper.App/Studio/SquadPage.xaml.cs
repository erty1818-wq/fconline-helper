using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using FcHelper.Market;

namespace FcHelper.App.Studio;

public sealed record TeamColorItem(string Label, int Id)
{
    public override string ToString() => Label;
}

/// <summary>
/// The squad maker: AI squads (four modes compared) and hand-made ones in the same eleven. An AI squad can be taken
/// and edited slot by slot, a squad can be built from empty, and "빈 자리 AI로 채우기" completes whatever is there.
/// Every slot offers alternatives and a search by name and season; grades can be changed per card or for all at once.
/// </summary>
public partial class SquadPage : UserControl
{
    private readonly HashSet<int> _excluded = [];
    private readonly List<ToggleButton> _grades = [];
    private SquadSlot?[] _working = new SquadSlot?[11];
    private Spot[] _spots = Formations.LayoutOf(Formations.All[0].Name).ToArray();
    private readonly Stack<(SquadSlot?[] Slots, Spot[] Spots, string[] Positions, string Formation, string DisplayName)> _undo = new();
    private bool _restoring;
    private IReadOnlyList<AppliedTeamColor> _workingColors = [];
    private SquadMaker? _maker;
    private RankerAllocation? _allocation;
    private int? _selected;
    private string[] _positions = Formations.LayoutOf(Formations.All[0].Name).Select(Formations.PositionAt).ToArray();
    /// <summary>The user started a hand-made squad: the pitch shows its empty slots.</summary>
    private bool _manual;
    private bool _optionsReady;
    private const string AdaptKey = "squad.adaptability";
    private const string TrainingKey = "squad.training";

    public SquadPage()
    {
        InitializeComponent();
        FormationBox.ItemsSource = Formations.All.Select(f => f.Name).ToList();
        FormationBox.SelectedIndex = 0;
        _spots = Formations.LayoutOf(CurrentFormation.Name).ToArray();
        _positions = _spots.Select(Formations.PositionAt).ToArray();
        FormationName.Text = CurrentFormation.Name;
        FormationBox.SelectionChanged += async (_, _) => await OnFormationChangedAsync();
        foreach (var g in new[] { 5, 6, 7, 8, 9, 10, 11 })
        {
            var chip = new ToggleButton { Content = $"+{g}", Tag = g, Style = (Style)FindResource("Chip"), IsChecked = g == 8 };
            _grades.Add(chip);
            GradeChips.Children.Add(chip);
        }
        foreach (var g in new[] { 5, 8, 9, 10, 11 })
        {
            var b = new Button { Content = $"+{g}", Tag = g, Style = (Style)FindResource("Ghost"), Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(4, 0, 0, 0) };
            b.Click += async (_, _) => await SetAllGradesAsync((int)b.Tag);
            BulkGrades.Children.Add(b);
        }
        RankBox.ItemsSource = new[] { "상위 10,000명", "상위 1,000명", "상위 100명" };
        RankBox.SelectedIndex = 0;
        TeamColorBox.ItemsSource = new[] { new TeamColorItem("없음", 0) };
        TeamColorBox.SelectedIndex = 0;
        FeatureBox.ItemsSource = new[] { new TeamColorItem("없음", 0) };
        FeatureBox.SelectedIndex = 0;
        FeatureBox.SelectionChanged += async (_, _) => await RefreshWorkingAsync();
        AdaptBox.ItemsSource = Enumerable.Range(1, FinalOvrMath.MaxAdaptability).Select(a => $"+{a}").ToList();
        AdaptBox.SelectedIndex = (int.TryParse(StudioKit.App.Db?.GetValue(AdaptKey)?.Value, out var adapt) ? Math.Clamp(adapt, 1, 5) : FinalOvrMath.MaxAdaptability) - 1;
        TrainingToggle.IsChecked = StudioKit.App.Db?.GetValue(TrainingKey)?.Value == "1";
        FacesToggle.IsChecked = Faces.Enabled;
        _optionsReady = true;
        Pitch.SlotClicked += s => _ = ShowSlotAsync(s.Index);
        Pitch.EmptySlotClicked += (i, _) => { var shown = ShowSlotAsync(i); };
        Pitch.SlotMoved += OnSlotMoved;
        Pitch.BackgroundClicked += OnPitchBackgroundClicked;
        Loaded += async (_, _) =>
        {
            await LoadSalaryCapAsync();
            await LoadTeamColorsAsync();
        };
        ShowWorking();
        LoadLibrary();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Z && (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) != 0 && e.OriginalSource is not TextBox)
            {
                OnUndo(this, e);
                e.Handled = true;
            }
            else if (e.Key == System.Windows.Input.Key.F11)
            {
                ToggleFocus();
                e.Handled = true;
            }
            else if (e.Key == System.Windows.Input.Key.Escape)
            {
                if (_inFocusMode)
                {
                    if (FocusDrawer.Visibility == Visibility.Visible)
                    {
                        FocusDrawer.Visibility = Visibility.Collapsed;
                        Pitch.Select(null);
                        _selected = null;
                    }
                    else
                    {
                        SetFocusMode(false);
                    }
                    e.Handled = true;
                }
            }
        };
    }

    // ── undo, image, library ───────────────────────────────────────────────

    /// <summary>Keeps the eleven (and formation) before a change, for 되돌리기 / Ctrl + Z (the last 30 steps).</summary>
    private void Remember()
    {
        if (_restoring) return;
        _undo.Push((_working.ToArray(), _spots.ToArray(), _positions.ToArray(), (string)FormationBox.SelectedItem, FormationName.Text));
        if (_undo.Count > 30)
        {
            var keep = _undo.Take(30).Reverse().ToList();
            _undo.Clear();
            foreach (var k in keep) _undo.Push(k);
        }
        UndoButton.IsEnabled = true;
    }

    private async void OnUndo(object sender, RoutedEventArgs e)
    {
        if (_undo.Count == 0) return;
        var (slots, spots, positions, formation, displayName) = _undo.Pop();
        _restoring = true;
        _working = slots;
        _spots = spots;
        _positions = positions;
        FormationBox.SelectedItem = formation;
        FormationName.Text = displayName;
        _restoring = false;
        _selected = null;
        UndoButton.IsEnabled = _undo.Count > 0;
        await RefreshWorkingAsync();
        Status.Text = "되돌렸습니다.";
    }

    /// <summary>The pitch with the totals and team colours as a PNG in Pictures\FcHelper (sharp: twice the screen size).</summary>
    private void OnSaveImage(object sender, RoutedEventArgs e)
    {
        if (_working.All(s => s is null)) { Status.Text = "저장할 스쿼드가 없습니다."; return; }
        try
        {
            const double width = 900;
            var header = new StackPanel { Margin = new Thickness(18, 14, 18, 8) };
            header.Children.Add(new TextBlock { Text = $"{(string)FormationBox.SelectedItem} · {Totals.Text}", FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = (System.Windows.Media.Brush)FindResource("Text"), TextWrapping = TextWrapping.Wrap });
            if (TeamColorsLine.Text.Length > 0)
                header.Children.Add(new TextBlock { Text = TeamColorsLine.Text, FontSize = 13, Foreground = (System.Windows.Media.Brush)FindResource("Accent"), TextWrapping = TextWrapping.Wrap });
            var pitch = new System.Windows.Shapes.Rectangle
            {
                Width = width - 36, Height = (width - 36) * Pitch.ActualHeight / Math.Max(1, Pitch.ActualWidth), Margin = new Thickness(18, 0, 18, 18),
                Fill = new System.Windows.Media.VisualBrush(Pitch) { Stretch = System.Windows.Media.Stretch.Uniform },
            };
            var sheet = new StackPanel { Width = width, Background = (System.Windows.Media.Brush)FindResource("Bg"), Children = { header, pitch } };
            sheet.Measure(new Size(width, double.PositiveInfinity));
            sheet.Arrange(new Rect(sheet.DesiredSize));
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)(sheet.ActualWidth * 2), (int)(sheet.ActualHeight * 2), 192, 192, System.Windows.Media.PixelFormats.Pbgra32);
            bitmap.Render(sheet);
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "FcHelper");
            Directory.CreateDirectory(folder);
            var file = Path.Combine(folder, $"squad-{DateTime.Now:yyyyMMdd-HHmmss}.png");
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            using (var stream = File.Create(file)) encoder.Save(stream);
            Status.Text = $"이미지로 저장했습니다: {file}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status.Text = "이미지를 저장하지 못했습니다 (사진 폴더에 쓸 수 없습니다).";
        }
    }

    private sealed record SavedSlot(long SpId, int Grade, bool Owned, bool Locked);
    private sealed record SavedSquad(string Name, string Formation, List<SavedSlot?> Slots, DateTime SavedAt, List<Spot>? Spots = null);
    private const string LibraryKey = "squad.library";

    private List<SavedSquad> Library()
    {
        try
        {
            return StudioKit.App.Db?.GetValue(LibraryKey) is { } v ? JsonSerializer.Deserialize<List<SavedSquad>>(v.Value) ?? [] : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private void LoadLibrary()
    {
        var names = Library().OrderByDescending(s => s.SavedAt).Select(s => s.Name).ToList();
        LibraryBox.ItemsSource = names;
        if (names.Count > 0) LibraryBox.SelectedIndex = 0;
    }

    /// <summary>Saves the eleven under a name (in this PC's database); the same name replaces the old one.</summary>
    private void OnLibrarySave(object sender, RoutedEventArgs e)
    {
        if (_working.All(s => s is null)) { Status.Text = "저장할 스쿼드가 없습니다."; return; }
        var name = LibraryName.Text.Trim();
        if (name.Length == 0) name = $"스쿼드 {DateTime.Now:M/d HH:mm}";
        var library = Library().Where(s => s.Name != name).ToList();
        library.Add(new SavedSquad(name, (string)FormationBox.SelectedItem,
            _working.Select(s => s is null ? null : new SavedSlot(s.Card.SpId, s.Grade, s.Owned, s.Locked)).ToList(),
            DateTime.Now,
            _spots.ToList()));
        StudioKit.App.Db?.SetValue(LibraryKey, JsonSerializer.Serialize(library));
        LoadLibrary();
        LibraryBox.SelectedItem = name;
        Status.Text = $"보관함에 '{name}'을(를) 저장했습니다.";
    }

    private async void OnLibraryLoad(object sender, RoutedEventArgs e)
    {
        if (LibraryBox.SelectedItem is not string name || Library().FirstOrDefault(s => s.Name == name) is not { } saved) return;
        if (StudioKit.Squads is not { } squads || await MakerAsync() is not { } maker) return;
        Remember();
        var spots = saved.Spots is { Count: 11 }
            ? saved.Spots
            : Formations.LayoutOf(saved.Formation);
        _spots = spots.ToArray();
        _positions = _spots.Select(Formations.PositionAt).ToArray();
        var detected = Formations.Detect(_spots);

        _restoring = true;
        if (Formations.PresetSpots.ContainsKey(detected))
        {
            FormationBox.SelectedItem = detected;
            FormationName.Text = detected;
        }
        else
        {
            FormationName.Text = $"사용자 지정 {detected}";
        }
        _restoring = false;

        _working = saved.Slots.Select((x, i) => x is not null && squads.Card(x.SpId) is { } card && i < _positions.Length
            ? maker.Slot(i, _positions[i], card, x.Grade, x.Owned, x.Locked) : null).ToArray();
        _manual = true;
        _selected = null;
        await RefreshWorkingAsync();
        var missing = saved.Slots.Count(x => x is not null) - _working.Count(x => x is not null);
        Status.Text = $"'{name}'을(를) 불러왔습니다 (오늘 시세로 다시 계산)" + (missing > 0 ? $" · 시세 데이터에 없는 {missing}장은 빈 자리로" : "");
    }

    private void OnLibraryDelete(object sender, RoutedEventArgs e)
    {
        if (LibraryBox.SelectedItem is not string name) return;
        StudioKit.App.Db?.SetValue(LibraryKey, JsonSerializer.Serialize(Library().Where(s => s.Name != name).ToList()));
        LoadLibrary();
        Status.Text = $"보관함에서 '{name}'을(를) 지웠습니다.";
    }

    private (int, int) RankRange => RankBox.SelectedIndex switch { 1 => (1, 1000), 2 => (1, 100), _ => (1, 10000) };
    private Formation CurrentFormation => Formations.Find((string)FormationBox.SelectedItem)!;
    /// <summary>The grade new cards come in at: the first chosen grade chip.</summary>
    private int DefaultGrade => _grades.FirstOrDefault(g => g.IsChecked == true)?.Tag as int? ?? 8;
    private IReadOnlySet<int> UsedPlayers(int except = -1) =>
        _working.Where((s, i) => s is not null && i != except).Select(s => s!.Card.PlayerId).ToHashSet();

    private async Task<SquadMaker?> MakerAsync()
    {
        if (StudioKit.Squads is not { } squads) return null;
        var (from, to) = RankRange;
        return _maker ??= await squads.MakerAsync(from, to);
    }

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

    /// <summary>
    /// 특성 colours that go with the chosen 소속 one: first by name at once ("프랑스" → "2026 프랑스"), then those printed on
    /// its members' cards ("바이언 첫번째 트레블" for 바이에른 뮌헨; read once, then kept for a week).
    /// </summary>
    private async void OnTeamColorChanged(object sender, SelectionChangedEventArgs e)
    {
        await RefreshWorkingAsync();
        if (TeamColorBox.SelectedItem is not TeamColorItem { Id: > 0 } item || StudioKit.Squads is not { } squads) return;
        var name = _catalog.FirstOrDefault(t => t.Id == item.Id)?.Name;
        var byName = name is null ? [] : _catalog.Where(t => t.Category == TeamColorCategory.Feature && t.Name.Contains(name, StringComparison.Ordinal)).ToList();
        ShowFeatures(byName.Select(t => (t, 1.0)).ToList());
        Status.Text = $"{name}의 특성 팀컬러 찾는 중… (처음에는 1~2분 걸립니다)";
        try
        {
            var related = await squads.RelatedFeatureColorsAsync(item.Id, progress: new Progress<string>(m => Status.Text = m));
            if ((TeamColorBox.SelectedItem as TeamColorItem)?.Id != item.Id) return; // the user moved on
            ShowFeatures(related);
            Status.Text = "";
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
            Status.Text = "특성 팀컬러를 다 찾지 못했습니다 (이름이 같은 것만 보입니다).";
        }
    }

    private void ShowFeatures(IReadOnlyList<(TeamColor Color, double Overlap)> features)
    {
        var keep = FeatureBox.SelectedItem as TeamColorItem;
        var items = new List<TeamColorItem> { new("없음", 0) };
        items.AddRange(features.Select(f => new TeamColorItem($"{f.Color.Name} ({f.Color.MaxMembers}명)", f.Color.Id)));
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

    /// <summary>Puts a card into the first free slot of its position, fixed for the AI (from other pages: "스쿼드에 넣기").</summary>
    public async void LockCard(long spId, int grade, string position, bool owned = false)
    {
        if (StudioKit.Squads?.Card(spId) is not { } card || await MakerAsync() is not { } maker) return;
        var pos = Formations.Normalize(position);
        var index = Enumerable.Range(0, _positions.Length).Where(i => _positions[i] == pos).OrderBy(i => _working[i] is null ? 0 : 1).DefaultIfEmpty(-1).First();
        if (index < 0)
        {
            Status.Text = $"{(string)FormationBox.SelectedItem}에는 {pos} 자리가 없습니다. 포메이션을 바꿔 보세요.";
            return;
        }
        _working[index] = maker.Slot(index, pos, card, grade, owned, locked: true);
        await RefreshWorkingAsync();
    }

    private void OnBudgetChip(object sender, RoutedEventArgs e) => BudgetBox.Text = (string)((Button)sender).Content;

    // ── pitch size ─────────────────────────────────────────────────────────

    private void OnToggleConditions(object sender, RoutedEventArgs e) => ShowConditions(ConditionsToggle.IsChecked == true);

    private void ShowConditions(bool show)
    {
        ConditionsToggle.IsChecked = show;
        Conditions.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        ConditionsToggle.Content = show ? "조건 접기" : "조건 펴기";
        MakerHint.Visibility = Conditions.Visibility; // folded, the page is all pitch
    }

    // ── focus mode & pitch interaction ─────────────────────────────────────

    private bool _inFocusMode;

    public void ToggleFocus() => SetFocusMode(!_inFocusMode);

    private void OnToggleFocus(object sender, RoutedEventArgs e) => ToggleFocus();

    private void OnExitFocus(object sender, RoutedEventArgs e) => SetFocusMode(false);

    private void OnCloseDrawer(object sender, RoutedEventArgs e)
    {
        FocusDrawer.Visibility = Visibility.Collapsed;
        Pitch.Select(null);
        _selected = null;
    }

    private void OnToggleFocusCollapse(object sender, RoutedEventArgs e)
    {
        if (FocusPanelBody.Visibility == Visibility.Visible)
        {
            FocusPanelBody.Visibility = Visibility.Collapsed;
            FocusCollapseBtn.Content = "▶";
        }
        else
        {
            FocusPanelBody.Visibility = Visibility.Visible;
            FocusCollapseBtn.Content = "◀";
        }
    }

    public void SetFocusMode(bool on)
    {
        if (_inFocusMode == on) return;
        _inFocusMode = on;

        (Window.GetWindow(this) as StudioWindow)?.SetFocusMode(on);

        if (on)
        {
            RequestBorder.Visibility = Visibility.Collapsed;
            Status.Visibility = Visibility.Collapsed;
            ToolsBorder.Visibility = Visibility.Collapsed;
            MainSplitter.Visibility = Visibility.Collapsed;
            SplitterBar.Visibility = Visibility.Collapsed;
            SideDock.Visibility = Visibility.Collapsed;
            SplitterColumn.Width = new GridLength(0);
            SideColumn.Width = new GridLength(0);

            NormalFormationHost.Children.Remove(FormationBox);
            NormalFormationHost.Children.Remove(FormationName);
            FocusFormationHost.Children.Add(FormationBox);
            FocusFormationHost.Children.Add(FormationName);

            SideDetailHost.Child = null;
            FocusDrawerHost.Child = DetailScroller;

            FocusPanel.Visibility = Visibility.Visible;
            FocusDrawer.Visibility = _selected is not null ? Visibility.Visible : Visibility.Collapsed;

            FocusTotals.Text = Totals.Text;
            FocusTotals.Foreground = Totals.Foreground;
            FocusTeamColors.Text = TeamColorsLine.Text;
        }
        else
        {
            RequestBorder.Visibility = Visibility.Visible;
            Status.Visibility = Visibility.Visible;
            ToolsBorder.Visibility = Visibility.Visible;
            MainSplitter.Visibility = Visibility.Visible;
            SplitterBar.Visibility = Visibility.Visible;
            SideDock.Visibility = Visibility.Visible;
            SplitterColumn.Width = new GridLength(12);
            SideColumn.Width = new GridLength(400);

            FocusFormationHost.Children.Remove(FormationBox);
            FocusFormationHost.Children.Remove(FormationName);
            NormalFormationHost.Children.Add(FormationBox);
            NormalFormationHost.Children.Add(FormationName);

            FocusDrawerHost.Child = null;
            SideDetailHost.Child = DetailScroller;

            FocusPanel.Visibility = Visibility.Collapsed;
            FocusDrawer.Visibility = Visibility.Collapsed;
        }
    }

    private async void OnSlotMoved(int slot, Spot to)
    {
        var move = Formations.Move(_spots, slot, to);
        if (!move.Success)
        {
            Status.Text = move.Reason ?? "이동할 수 없습니다.";
            return;
        }
        if (_spots.SequenceEqual(move.Spots)) return;

        Remember();
        var otherIndex = -1;
        for (var i = 0; i < _spots.Length; i++)
        {
            if (i != slot && _spots[i] == to)
            {
                otherIndex = i;
                break;
            }
        }
        _spots = move.Spots.ToArray();
        _positions = _spots.Select(Formations.PositionAt).ToArray();

        if (StudioKit.Squads is { } squads && await MakerAsync() is { } maker)
        {
            if (_working[slot] is { } curSlot)
            {
                _working[slot] = maker.Slot(slot, _positions[slot], curSlot.Card, curSlot.Grade, curSlot.Owned, curSlot.Locked);
            }
            if (otherIndex >= 0 && _working[otherIndex] is { } otherSlot)
            {
                _working[otherIndex] = maker.Slot(otherIndex, _positions[otherIndex], otherSlot.Card, otherSlot.Grade, otherSlot.Owned, otherSlot.Locked);
            }
        }

        var detected = Formations.Detect(_spots);
        _restoring = true;
        if (Formations.PresetSpots.ContainsKey(detected))
        {
            FormationBox.SelectedItem = detected;
            FormationName.Text = detected;
        }
        else
        {
            FormationName.Text = $"사용자 지정 {detected}";
        }
        _restoring = false;

        await RefreshWorkingAsync();
        Status.Text = "";
        if (_selected is { } sel) _ = ShowSlotAsync(sel);
    }

    private void OnPitchBackgroundClicked()
    {
        if (_inFocusMode && FocusDrawer.Visibility == Visibility.Visible)
        {
            FocusDrawer.Visibility = Visibility.Collapsed;
            Pitch.Select(null);
            _selected = null;
        }
    }

    // ── AI ─────────────────────────────────────────────────────────────────

    private async Task<SquadRequest?> RequestAsync(SquadService squads, IReadOnlyDictionary<int, LockedCard> locked)
    {
        if (!StudioKit.TryPrice(BudgetBox, long.MaxValue, out var budget)) { Status.Text = "예산은 100억, 5000만처럼 입력하세요."; return null; }
        var grades = _grades.Where(g => g.IsChecked == true).Select(g => (int)g.Tag).ToList();
        if (grades.Count == 0) { Status.Text = "강화 단계를 하나 이상 고르세요."; return null; }
        var targets = await squads.TargetsAsync([(TeamColorBox.SelectedItem as TeamColorItem)?.Id ?? 0, (FeatureBox.SelectedItem as TeamColorItem)?.Id ?? 0]);
        _allocation = null;
        if (AllocBox.IsChecked == true)
        {
            try
            {
                _allocation = await squads.RankerAllocationAsync(progress: new Progress<string>(m => Status.Text = $"{m} (처음 한 번, 3일마다 갱신)"));
            }
            catch (InvalidOperationException)
            {
                Status.Text = "랭커 분배는 API 키가 있어야 받을 수 있습니다. 분배 없이 짭니다.";
            }
        }
        return new SquadRequest
        {
            Formation = Formations.Custom(
                FormationName.Text.Length > 0 ? FormationName.Text : CurrentFormation.Name,
                _spots.Select(s => Formations.Normalize(Formations.PositionAt(s)))),
            Budget = budget,
            SalaryCap = StudioKit.IntOr(CapBox, int.MaxValue),
            Grades = grades,
            Locked = locked,
            ExcludedPlayers = new HashSet<int>(_excluded),
            TeamColors = targets,
            Allocation = _allocation,
            RankerPicksOnly = RankerOnlyBox.IsChecked == true,
        };
    }

    /// <summary>Slots the user fixed (🔒) or owns stay as they are when the AI builds.</summary>
    private Dictionary<int, LockedCard> FixedSlots(bool allFilled = false) =>
        _working.Select((s, i) => (s, i)).Where(x => x.s is not null && (allFilled || x.s.Locked || x.s.Owned))
            .ToDictionary(x => x.i, x => new LockedCard(x.s!.Card.SpId, x.s.Grade, x.s.Owned));

    private async void OnBuild(object sender, RoutedEventArgs e)
    {
        if (StudioKit.Squads is not { } squads) { Status.Text = "준비 중입니다."; return; }
        Status.Text = "";
        await StudioKit.Run(BuildButton, Status, async () =>
        {
            if (await RequestAsync(squads, FixedSlots()) is not { } request) return;
            Status.Text = "계산 중… (처음에는 랭커 데이터를 받느라 1분쯤 걸립니다)";
            var (from, to) = RankRange;
            var plans = await squads.CompareModesAsync(request, from, to, progress: new Progress<string>(m => Status.Text = m));
            Status.Text = plans.Count == 0 ? "조건에 맞는 스쿼드가 없습니다." : "";
            var cheapest = plans.MinBy(p => p.TotalPrice);
            var strongest = plans.MaxBy(p => p.AverageEffectiveOvr);
            Modes.ItemsSource = plans.Select(p => new ModeCard(p, p.Label, Bp.Format(p.TotalPrice),
                $"인게임 {p.Slots.Average(x => FinalOvrMath.Compute(x.Card, squads.KnownAbility(x.Card.SpId), x.Position, x.Grade, Adaptability, x.ColorLevels).Value):0.0} · 급여 {p.TotalPay}",
                $"환산 {p.AverageEffectiveOvr:0.0} [추정]", $"급여 {p.TotalPay}",
                string.Join("\n", p.TeamColors.Select(StudioKit.TeamColorLine)),
                ReferenceEquals(p, cheapest) ? "가장 쌈" : ReferenceEquals(p, strongest) ? "가장 강함" : "")).ToList();
            Modes.SelectedIndex = plans.Count > 1 ? 1 : 0;
            ShowConditions(false); // more room for the pitch; one click brings the conditions back
            if (_allocation is { } a) Status.Text = $"랭커 {a.Squads}팀의 포지션별 가격·급여 분배를 따랐습니다 (10억 미만 {a.Skipped}팀 제외).";
        });
    }

    /// <summary>Takes an AI squad into the editable eleven.</summary>
    private void OnModeSelected(object sender, SelectionChangedEventArgs e)
    {
        if (Modes.SelectedItem is not ModeCard card) return;
        if (_working.Any(s => s is not null)) Remember();
        _working = card.Plan.Slots.OrderBy(s => s.Index).Select(s => (SquadSlot?)s).ToArray();
        _workingColors = card.Plan.TeamColors;
        if (Formations.PresetSpots.ContainsKey(card.Plan.Formation.Name))
        {
            _spots = Formations.LayoutOf(card.Plan.Formation.Name).ToArray();
            FormationBox.SelectedItem = card.Plan.Formation.Name;
            FormationName.Text = card.Plan.Formation.Name;
        }
        _positions = _spots.Select(Formations.PositionAt).ToArray();
        _selected = null;
        ApplyFinals();
        ShowWorking();
        _ = LoadAbilitiesAsync();
        ShowHint($"'{card.Plan.Label}' 안을 불러왔습니다. 선수를 누르면 바꾸거나 강화를 고칠 수 있습니다.");
    }

    /// <summary>Keeps every card already in the eleven and lets the AI fill the empty slots.</summary>
    private async void OnFillEmpty(object sender, RoutedEventArgs e)
    {
        if (StudioKit.Squads is not { } squads) return;
        if (_working.All(s => s is not null)) { Status.Text = "빈 자리가 없습니다."; return; }
        await StudioKit.Run(FillButton, Status, async () =>
        {
            if (await RequestAsync(squads, FixedSlots(allFilled: true)) is not { } request) return;
            Status.Text = "빈 자리 채우는 중…";
            var (from, to) = RankRange;
            var plan = (await squads.BuildAsync(request with { Mode = SquadMode.Balanced, Plans = 1 }, from, to, progress: new Progress<string>(m => Status.Text = m))).FirstOrDefault();
            if (plan is null) { Status.Text = "조건에 맞는 카드가 없습니다."; return; }
            var kept = _working.ToArray();
            Remember();
            _working = plan.Slots.OrderBy(s => s.Index).Select(s => kept[s.Index] ?? s).Select(s => (SquadSlot?)s).ToArray();
            Status.Text = "";
            await RefreshWorkingAsync();
        });
    }

    // ── hand-made eleven ───────────────────────────────────────────────────

    private async void OnNewSquad(object sender, RoutedEventArgs e)
    {
        if (_working.Any(s => s is not null)) Remember();
        _spots = Formations.LayoutOf(CurrentFormation.Name).ToArray();
        _positions = _spots.Select(Formations.PositionAt).ToArray();
        FormationName.Text = CurrentFormation.Name;
        _working = new SquadSlot?[11];
        _manual = true;
        _selected = null;
        Modes.SelectedIndex = -1;
        await RefreshWorkingAsync();
        ShowHint("빈 자리(+)를 누르고 선수를 고르세요. 직접 넣은 선수는 🔒 고정되어, [AI 추천 스쿼드]나 [빈 자리 AI로 채우기]를 누르면 나머지 자리만 AI가 채웁니다.");
    }

    private async Task OnFormationChangedAsync()
    {
        if (_restoring || await MakerAsync() is not { } maker) return;
        Remember();
        _spots = Formations.LayoutOf(CurrentFormation.Name).ToArray();
        _positions = _spots.Select(Formations.PositionAt).ToArray();
        FormationName.Text = CurrentFormation.Name;
        _working = maker.Remap(_working, CurrentFormation).ToArray();
        _selected = null;
        await RefreshWorkingAsync();
    }

    private async Task SetAllGradesAsync(int grade)
    {
        if (await MakerAsync() is not { } maker) return;
        Remember();
        _working = _working.Select(s => s is null || s.Owned ? s : maker.WithGrade(s, grade)).ToArray();
        await RefreshWorkingAsync();
        Status.Text = $"모든 카드를 +{grade}로 바꿨습니다 (보유 카드는 그대로).";
    }

    private async Task SetSlotAsync(int index, SquadSlot? slot)
    {
        Remember();
        _working[index] = slot;
        await RefreshWorkingAsync();
        await ShowSlotAsync(index);
    }

    /// <summary>Re-applies team colours to the hand-made eleven and redraws the pitch and totals.</summary>
    private async Task RefreshWorkingAsync()
    {
        if (StudioKit.Squads is { } squads && await MakerAsync() is { } maker)
        {
            try
            {
                var selected = await squads.TargetsAsync([(TeamColorBox.SelectedItem as TeamColorItem)?.Id ?? 0, (FeatureBox.SelectedItem as TeamColorItem)?.Id ?? 0]);
                var filled = _working.Where(s => s is not null).Cast<SquadSlot>().ToList();
                var (slots, colors) = await maker.ApplyTeamColorsAsync(filled, selected);
                foreach (var s in slots) _working[s.Index] = s;
                _workingColors = colors;
            }
            catch (Exception e) when (e is HttpRequestException or InvalidOperationException or TaskCanceledException)
            {
                // Team colours could not be read: the eleven stays without bonuses.
            }
        }
        ApplyFinals();
        ShowWorking();
        _ = LoadAbilitiesAsync();
    }

    // ── in-game OVR ────────────────────────────────────────────────────────

    private int Adaptability => AdaptBox.SelectedIndex + 1;

    /// <summary>The OVR the game shows for each card: grade, 적응도, the team colour levels that reach it and (if on) 집중훈련.</summary>
    private void ApplyFinals()
    {
        var training = TrainingToggle.IsChecked == true;
        for (var i = 0; i < _working.Length; i++)
        {
            if (_working[i] is not { } s) continue;
            var ability = StudioKit.Squads?.KnownAbility(s.Card.SpId);
            var plan = training ? FinalOvrMath.Training(s.Position, s.Grade).AsBonus : null;
            _working[i] = s with { Final = FinalOvrMath.Compute(s.Card, ability, s.Position, s.Grade, Adaptability, s.ColorLevels, plan) };
        }
    }

    private bool _loadingAbilities;

    /// <summary>Fetches the stats of cards seen for the first time (one data center request each, kept a week), then redraws exactly.</summary>
    private async Task LoadAbilitiesAsync()
    {
        if (_loadingAbilities || StudioKit.Squads is not { } squads) return;
        _loadingAbilities = true;
        try
        {
            while (_working.Where(x => x is not null).Select(x => x!.Card.SpId).FirstOrDefault(id => squads.KnownAbility(id) is null) is var next && next != 0)
            {
                var missing = _working.Count(x => x is not null && squads.KnownAbility(x.Card.SpId) is null);
                Status.Text = $"인게임 OVR을 정확히 계산하려고 카드 스탯을 받는 중… (남은 {missing}장, 한 번 받으면 일주일 보관)";
                if (await squads.AbilityAsync(next) is null) break;
                ApplyFinals();
                ShowWorking();
            }
            if (Status.Text.StartsWith("인게임 OVR을 정확히")) Status.Text = "";
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            Status.Text = "카드 스탯을 받지 못해 인게임 OVR 일부는 추정값(~)입니다.";
        }
        finally
        {
            _loadingAbilities = false;
        }
    }

    private void OnFinalOptionChanged(object sender, RoutedEventArgs e)
    {
        if (!_optionsReady) return;
        StudioKit.App.Db?.SetValue(AdaptKey, Adaptability.ToString());
        StudioKit.App.Db?.SetValue(TrainingKey, TrainingToggle.IsChecked == true ? "1" : "0");
        ApplyFinals();
        ShowWorking();
        if (_selected is { } i) _ = ShowSlotAsync(i);
    }

    private void OnFacesToggle(object sender, RoutedEventArgs e) => Faces.Enabled = FacesToggle.IsChecked == true;

    private void ShowWorking()
    {
        var any = _working.Any(s => s is not null);
        EmptyState.Visibility = any || _manual || _hintClosed ? Visibility.Collapsed : Visibility.Visible;
        Pitch.Show(_working, _positions, _spots);
        Pitch.Select(_selected);
        var filled = _working.Where(s => s is not null).Cast<SquadSlot>().ToList();
        var cap = StudioKit.IntOr(CapBox, int.MaxValue);
        var pay = filled.Sum(s => s.Pay);
        _ = StudioKit.TryPrice(BudgetBox, long.MaxValue, out var budget);
        var price = filled.Where(s => !s.Owned).Sum(s => s.Price);
        Totals.Text = filled.Count == 0 ? "빈 스쿼드"
            : $"{filled.Count}/{_positions.Length}명 · 시세 {Bp.Format(price)}{(budget < long.MaxValue ? $" / 예산 {Bp.Format(budget)}" : "")} · 급여 {pay}{(cap < int.MaxValue ? $"/{cap} ({pay * 100 / cap}%)" : "")}"
              + $" · 인게임 평균 OVR {filled.Average(s => s.ShownOvr):0.0} (최고 {filled.Max(s => s.ShownOvr):0} · 최저 {filled.Min(s => s.ShownOvr):0})"
              + $" · 환산 {filled.Average(s => s.EffectiveOvr):0.0} [추정]";
        Totals.Foreground = (System.Windows.Media.Brush)FindResource(pay > cap || price > budget ? "Warn" : "Text");
        TeamColorsLine.Text = string.Join("   ", _workingColors.Select(StudioKit.TeamColorLine));
        var fixedCount = filled.Count(s => s.Locked || s.Owned);
        LockInfo.Text = (fixedCount > 0 ? $"🔒 고정 {fixedCount}명 (AI가 바꾸지 않음)" : "")
            + (_excluded.Count > 0 ? $"   ✕ 제외 {string.Join(", ", _excluded.Select(p => StudioKit.Squads?.Pool().FirstOrDefault(c => c.PlayerId == p)?.Name ?? p.ToString()))}" : "");

        FocusTotals.Text = Totals.Text;
        FocusTotals.Foreground = Totals.Foreground;
        FocusTeamColors.Text = TeamColorsLine.Text;
    }

    private bool _hintClosed;

    private void OnCloseHint(object sender, RoutedEventArgs e)
    {
        _hintClosed = true;
        EmptyState.Visibility = Visibility.Collapsed;
    }

    private void ShowHint(string text)
    {
        Detail.Children.Clear();
        Detail.Children.Add(new TextBlock { Text = text, Style = (Style)FindResource("Hint"), TextWrapping = TextWrapping.Wrap });
    }

    // ── slot panel ─────────────────────────────────────────────────────────

    /// <summary>Where the slot panel's helpers add their lines (a section of <see cref="Detail"/>), or Detail itself.</summary>
    private Panel? _into;
    private Panel Into => _into ?? Detail;

    /// <summary>
    /// The slot panel, top to bottom: the card (grade, in-game OVR, buttons), 선수 검색 (results at once, no waiting),
    /// the [AI 추천픽] button (trade volume is checked only when it is pressed), then mini face, 집중훈련 and the details.
    /// </summary>
    private async Task ShowSlotAsync(int index)
    {
        if (await MakerAsync() is not { } maker) return;
        _selected = index;
        Pitch.Select(index);
        _hintClosed = true; // the user found the slots
        EmptyState.Visibility = Visibility.Collapsed;
        if (_inFocusMode)
        {
            FocusDrawer.Visibility = Visibility.Visible;
        }
        var position = Formations.Normalize(_positions[index]);
        var s = _working[index];
        var grade = s?.Grade ?? DefaultGrade;
        Detail.Children.Clear();
        var head = new StackPanel();
        var search = new StackPanel();
        var ai = new StackPanel();
        var faces = new StackPanel();
        var more = new StackPanel();
        foreach (var section in new[] { head, search, ai, faces, more }) Detail.Children.Add(section);

        _into = head;
        if (s is not null)
        {
            var c = s.Card;
            head.Children.Add(new TextBlock { Text = c.Name, Style = (Style)FindResource("H1") });
            head.Children.Add(new TextBlock { Text = $"{c.Season} · {s.Position} · 약발 {c.WeakFoot} · 급여 {s.Pay}", Style = (Style)FindResource("Hint") });
            // Grade of this card: one click, the price and OVR follow.
            // +12/+13 only for cards the user owns: nobody sells them.
            var gradeBox = new ComboBox { ItemsSource = Enumerable.Range(1, s.Owned ? 13 : Grades.MaxTradable).Select(g => $"+{g}").ToList(), Width = 80, HorizontalAlignment = HorizontalAlignment.Left };
            gradeBox.SelectedIndex = Math.Min(s.Grade, gradeBox.Items.Count) - 1;
            gradeBox.SelectionChanged += async (_, _) => await SetSlotAsync(index, maker.WithGrade(s, gradeBox.SelectedIndex + 1));
            head.Children.Add(new TextBlock { Text = "강화", Style = (Style)FindResource("FieldLabel"), Margin = new Thickness(0, 10, 0, 0) });
            head.Children.Add(gradeBox);
            ShowFinal(s);
            var actions = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
            actions.Children.Add(Action(s.Locked ? "고정 해제" : "🔒 고정 (AI가 안 바꿈)", async () => await SetSlotAsync(index, s with { Locked = !s.Locked })));
            actions.Children.Add(Action(s.Owned ? "보유 해제" : "보유 중 (가격 0)", async () => await SetSlotAsync(index, s with { Owned = !s.Owned })));
            actions.Children.Add(Action("자리 비우기", async () => await SetSlotAsync(index, null)));
            actions.Children.Add(Action("이 선수 제외", async () => { _excluded.Add(c.PlayerId); await SetSlotAsync(index, null); }));
            actions.Children.Add(Action("강화 효율 보기", () =>
            {
                (Window.GetWindow(this) as StudioWindow)?.Navigate("grade", p => ((GradePage)p).Load(c.SpId, s.Position, s.Grade));
                return Task.CompletedTask;
            }));
            head.Children.Add(actions);
        }
        else
        {
            head.Children.Add(new TextBlock { Text = $"{position} 자리", Style = (Style)FindResource("H1") });
            head.Children.Add(new TextBlock { Text = $"비어 있습니다. 검색해서 고르거나 [AI 추천픽]을 눌러 보세요 (+{grade}로 들어갑니다).", Style = (Style)FindResource("Hint"), TextWrapping = TextWrapping.Wrap });
        }
        var members = await AffiliationMembersAsync();
        if (_selected != index) return;

        // 선수 검색: by name and season, shown at once (the market data is already here, nothing to wait for).
        search.Children.Add(new TextBlock { Text = "선수 검색", Style = (Style)FindResource("H2"), Margin = new Thickness(0, 14, 0, 4) });
        var nameBox = new TextBox { Margin = new Thickness(0, 0, 0, 4), ToolTip = "선수 이름 (일부만 써도 됩니다) · Enter로 검색" };
        var seasonBox = new ComboBox { ItemsSource = new[] { "모든 시즌" }.Concat(maker.Seasons()).ToList(), SelectedIndex = 0, Margin = new Thickness(0, 0, 4, 0), MinWidth = 110 };
        var sortBox = new ComboBox { ItemsSource = new[] { "OVR 높은 순", "가격 낮은 순", "스펙 대비 싼 순", "랭커 많이 쓰는 순" }, SelectedIndex = 0, MinWidth = 120 };
        var results = new StackPanel();
        var onlyMembers = new CheckBox { Content = "소속 팀컬러 선수만", IsChecked = members is not null, IsEnabled = members is not null, Margin = new Thickness(0, 4, 0, 4) };
        async Task RunSearch()
        {
            var name = nameBox.Text;
            var season = seasonBox.SelectedIndex > 0 ? (string)seasonBox.SelectedItem : null;
            var sort = (CandidateSort)sortBox.SelectedIndex;
            var only = onlyMembers.IsChecked == true ? members : null;
            var found = await Task.Run(() => maker.Search(index, position, grade, name, season, sort, UsedPlayers(index), 30, only));
            results.Children.Clear();
            if (found.Count == 0) results.Children.Add(new TextBlock { Text = "찾은 카드가 없습니다.", Style = (Style)FindResource("Hint") });
            foreach (var f in found) results.Children.Add(CandidateButton(index, f, s));
        }
        nameBox.KeyDown += async (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) await RunSearch(); };
        seasonBox.SelectionChanged += async (_, _) => await RunSearch();
        sortBox.SelectionChanged += async (_, _) => await RunSearch();
        onlyMembers.Click += async (_, _) => await RunSearch();
        var searchButton = new Button { Content = "검색", Style = (Style)FindResource("Ghost"), Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(4, 0, 0, 0) };
        searchButton.Click += async (_, _) => await RunSearch();
        search.Children.Add(nameBox);
        search.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { seasonBox, sortBox, searchButton } });
        search.Children.Add(onlyMembers);
        search.Children.Add(results);
        _ = RunSearch();

        // AI 추천픽: alternatives inside the chosen 소속 colour and the money left, only on request (their trade volume
        // is checked then: each card and grade once, kept three days).
        _ = StudioKit.TryPrice(BudgetBox, long.MaxValue, out var budget);
        var spent = _working.Where((x, i) => x is not null && i != index && !x.Owned).Sum(x => x!.Price);
        var maxPrice = budget < long.MaxValue ? Math.Max(0, budget - spent) : long.MaxValue;
        var aiButton = new Button
        {
            Content = s is null ? "✨ AI 추천픽 보기" : "✨ AI 대체 선수 보기", Style = (Style)FindResource("Primary"), Margin = new Thickness(0, 14, 0, 4), HorizontalAlignment = HorizontalAlignment.Stretch,
            ToolTip = "예산·팀컬러·랭커 가격 분배에 맞는 카드를 고르고, 거래가 거의 없는 매물은 빼고 보여 줍니다.",
        };
        var aiList = new StackPanel();
        aiButton.Click += async (_, _) =>
        {
            aiButton.IsEnabled = false;
            aiList.Children.Clear();
            var checking = new TextBlock { Text = "AI 추천 고르는 중…", Style = (Style)FindResource("Hint") };
            aiList.Children.Add(checking);
            try
            {
                var found = await Task.Run(() => maker.Suggest(index, position, grade, s, UsedPlayers(index), budget, _allocation, perKind: 5, members: members, maxPrice: maxPrice));
                IReadOnlySet<(long SpId, int Grade)> illiquid = StudioKit.Squads is { } sq
                    ? await sq.IlliquidAsync(found.Select(x => (x.Slot.Card.SpId, x.Slot.Grade)), new Progress<string>(m => checking.Text = m))
                    : new HashSet<(long, int)>();
                if (_selected != index) return;
                aiList.Children.Clear();
                var suggestions = found.Where(x => !illiquid.Contains((x.Slot.Card.SpId, x.Slot.Grade))).GroupBy(x => x.Reason).SelectMany(g => g.Take(4)).ToList();
                aiList.Children.Add(new TextBlock
                {
                    Text = (members is null ? "" : "소속 팀컬러 선수만 · ") + (maxPrice < long.MaxValue ? $"남은 예산 {Bp.Format(maxPrice)} 안에서" : "예산 제한 없음")
                        + (illiquid.Count > 0 ? $" · 거래가 거의 없는 {illiquid.Count}장 제외" : ""),
                    Style = (Style)FindResource("Hint"), Margin = new Thickness(0, 0, 0, 4), TextWrapping = TextWrapping.Wrap,
                });
                if (suggestions.Count == 0) aiList.Children.Add(new TextBlock { Text = "조건에 맞는 카드가 없습니다.", Style = (Style)FindResource("Hint") });
                foreach (var group in suggestions.GroupBy(x => x.Reason))
                {
                    aiList.Children.Add(new TextBlock { Text = group.Key, Style = (Style)FindResource("FieldLabel"), Margin = new Thickness(0, 6, 0, 2) });
                    foreach (var x in group) aiList.Children.Add(CandidateButton(index, x.Slot, s));
                }
            }
            finally
            {
                aiButton.IsEnabled = true;
            }
        };
        ai.Children.Add(aiButton);
        ai.Children.Add(aiList);

        if (s is not null)
        {
            _into = faces;
            _ = ShowFacesAsync(s);
            _into = more;
            ShowTraining(s);
            var c = s.Card;
            Line("시세 · 같은 스펙 예상가 [추정]", s.Owned ? "보유 (0으로 계산)" : $"{Bp.Format(s.Price)} · {Bp.Format(s.Expected)} ({StudioKit.Pct(s.Discount)})");
            if (s.RankerUsers > 0) Line("랭커 사용", $"{s.RankerUsers}명 ({s.RankerShare:P1}, 전날 공식경기)");
            if (c.Tags.Count > 0) Line("특성·개인기·체형", StudioKit.Tags(c));
            var g = MarketGroups.Get(c.Group);
            var core = g.CoreStats.OrderByDescending(x => x.Weight).Where(x => c.Stats.ContainsKey(x.Stat)).Take(8)
                .Select(x => $"{MarketGroups.StatNames.GetValueOrDefault(x.Stat, x.Stat)} {c.Stats[x.Stat]}");
            Line($"코어 능력치 (+1){(g.CoreGap(c) is { } gap ? $" · 코어 {gap:+0.0;-0.0}" : "")}", string.Join("  ", core));
            _ = ShowRankerStatsAsync(s, more);
        }
        _into = null;
    }

    /// <summary>Members of the chosen 소속 colour (the AI uses only them), or null when none is chosen.</summary>
    private async Task<IReadOnlySet<long>?> AffiliationMembersAsync()
    {
        if (StudioKit.Squads is not { } squads || TeamColorBox.SelectedItem is not TeamColorItem { Id: > 0 } item) return null;
        try
        {
            return await squads.TeamColorAsync(item.Id) is { Color.Category: TeamColorCategory.Affiliation } tc ? tc.Members : null;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// One card offered for a slot. Clicking puts it in fixed (🔒), so [AI 추천 스쿼드] and [빈 자리 AI로 채우기] build
    /// around the players the user chose; a listing that hardly trades gets a warning.
    /// </summary>
    private Button CandidateButton(int index, SquadSlot candidate, SquadSlot? current)
    {
        var c = candidate.Card;
        var known = StudioKit.Squads?.KnownLiquidity(c.SpId, candidate.Grade);
        var text = new StackPanel
        {
            Children =
            {
                new TextBlock
                {
                    Text = (Honey.IsHoney(candidate.Discount, known) ? Honey.Mark + " " : "") + $"{c.Name} · {c.Season} +{candidate.Grade}",
                    FontWeight = FontWeights.SemiBold,
                },
                new TextBlock
                {
                    Text = $"OVR {candidate.Ovr} · 환산 {candidate.EffectiveOvr:0.0} · {Bp.Format(candidate.Price)} · 급여 {c.Pay}"
                        + (current is null ? "" : $" · {(candidate.Price - current.Price >= 0 ? "+" : "−")}{Bp.Format(Math.Abs(candidate.Price - current.Price))}")
                        + (known is { Tradable: false } ? " · ⚠ 거래 거의 없음" : ""),
                    Style = (Style)FindResource("Hint"),
                },
            },
        };
        // The card's own season picture (or the one the user picked for it), like on the pitch.
        FrameworkElement content = text;
        if (Faces.Enabled)
        {
            var face = new Image { Width = 40, Height = 40, Stretch = System.Windows.Media.Stretch.Uniform, Margin = new Thickness(0, 0, 8, 0), Source = Faces.Cached(c.SpId) };
            System.Windows.Media.RenderOptions.SetBitmapScalingMode(face, System.Windows.Media.BitmapScalingMode.HighQuality);
            if (face.Source is null) _ = SetFaceAsync(face, c.SpId);
            DockPanel.SetDock(face, Dock.Left);
            content = new DockPanel { LastChildFill = true, Children = { face, text } };
        }
        text.VerticalAlignment = VerticalAlignment.Center;
        var b = new Button
        {
            Content = content, HorizontalContentAlignment = HorizontalAlignment.Left, Style = (Style)FindResource("Ghost"),
            Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(0, 0, 0, 4), HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        b.Click += async (_, _) =>
        {
            await SetSlotAsync(index, candidate with { Locked = true });
            if (StudioKit.Squads is not { } squads) return;
            try
            {
                if (await squads.LiquidityAsync(c.SpId, candidate.Grade) is { Tradable: false } l)
                    Status.Text = $"{c.Name} {c.Season} +{candidate.Grade}: 최근 30일 시세가 {l.Changes30}번만 바뀐 매물입니다. 실제로 구하기 어려울 수 있습니다.";
            }
            catch (HttpRequestException)
            {
                // No warning when the data center cannot be reached.
            }
        };
        return b;
    }

    private async Task ShowRankerStatsAsync(SquadSlot s, Panel into)
    {
        if (StudioKit.Squads is not { } squads) return;
        var line = new TextBlock { Style = (Style)FindResource("Hint"), Margin = new Thickness(0, 4, 0, 0), Text = "랭커 20경기 기록 불러오는 중…", TextWrapping = TextWrapping.Wrap };
        into.Children.Add(line);
        try
        {
            var stats = await squads.RankerStatsAsync([(s.Card.SpId, s.Position)]);
            line.Text = stats.TryGetValue(s.Card.SpId, out var r)
                ? $"랭커가 이 카드로 뛴 {r.Status.MatchCount}경기 평균: 골 {r.Status.Goal:0.00} · 도움 {r.Status.Assist:0.00} · 슈팅 {r.Status.Shoot:0.0} (공식 API)"
                : "랭커 20경기 기록: 이 포지션에서 쓴 기록이 없습니다.";
        }
        catch (Exception e) when (e is HttpRequestException or FcHelper.NexonApi.NexonApiException or TaskCanceledException or InvalidOperationException)
        {
            line.Text = "랭커 20경기 기록을 불러오지 못했습니다.";
        }
    }

    /// <summary>The in-game OVR of the card and where each point comes from.</summary>
    private void ShowFinal(SquadSlot s)
    {
        if (s.Final is not { } f)
        {
            Line("OVR", $"{s.Ovr}" + (s.TeamColorBonus > 0 ? $" + 팀컬러 {s.TeamColorBonus:0.#} [추정]" : ""));
            return;
        }
        Into.Children.Add(new TextBlock { Text = "인게임 OVR" + (f.Exact ? " [계산]" : " [추정]"), Style = (Style)FindResource("FieldLabel"), Margin = new Thickness(0, 10, 0, 0) });
        Into.Children.Add(new TextBlock { Text = f.Value.ToString(), Style = (Style)FindResource("BigNumber"), Foreground = (System.Windows.Media.Brush)FindResource("Accent") });
        Into.Children.Add(new TextBlock { Text = f.Breakdown, Style = (Style)FindResource("Hint"), TextWrapping = TextWrapping.Wrap });
        var colors = s.ColorLevels.Select(l => string.Join(", ", l.Effects)).Where(e => e.Length > 0).ToList();
        if (colors.Count > 0)
            Into.Children.Add(new TextBlock { Text = "적용 팀컬러: " + string.Join(" / ", colors), Style = (Style)FindResource("Hint"), TextWrapping = TextWrapping.Wrap });
        if (!f.Exact)
            Into.Children.Add(new TextBlock { Text = "카드 스탯을 받으면 정확한 값으로 바뀝니다.", Style = (Style)FindResource("Hint") });
    }

    /// <summary>
    /// 집중훈련 for this card at its slot: the stats the position's OVR weighs most (+2 each, 5 of them, 6 from +11), what
    /// that adds, and the fewest steps to the next OVR; below, every stat of the position with its weight.
    /// </summary>
    private void ShowTraining(SquadSlot s)
    {
        var weights = OvrFormula.Of(s.Position);
        if (weights.Count == 0) return;
        var ability = StudioKit.Squads?.KnownAbility(s.Card.SpId);
        var before = FinalOvrMath.Compute(s.Card, ability, s.Position, s.Grade, Adaptability, s.ColorLevels);
        var plan = FinalOvrMath.Training(s.Position, s.Grade, before);
        Into.Children.Add(new TextBlock { Text = $"집중훈련 추천 ({s.Position})", Style = (Style)FindResource("H2"), Margin = new Thickness(0, 14, 0, 2) });
        Into.Children.Add(new TextBlock
        {
            Text = $"스탯 {plan.Stats.Count}개 +2씩: " + string.Join(" · ", plan.Stats.Select(t => $"{t.Stat} +2")) + $"\n= 가중 {plan.Points}점 → OVR +{plan.Gain}"
                + (plan.PointsToNext is { } need ? $" (지금 다음 OVR까지 {need}점)" : " (카드 스탯을 받으면 정확히 계산)"),
            TextWrapping = TextWrapping.Wrap,
        });
        if (plan.Cheapest.Count > 0 && plan.PointsToNext is { } n)
            Into.Children.Add(new TextBlock
            {
                Text = $"OVR +1만 원하면: " + string.Join(" · ", plan.Cheapest.Select(c => $"{c.Stat} +{c.Plus}")) + $" ({n}점 이상)",
                Style = (Style)FindResource("Hint"), TextWrapping = TextWrapping.Wrap,
            });
        Into.Children.Add(new TextBlock
        {
            Text = $"{s.Position} OVR에 들어가는 스탯 (가중치, 합 100): " + string.Join(" · ", weights.Select(kv => $"{kv.Key} {kv.Value}"))
                + "\n스탯 1 오를 때 OVR +가중치/100. 공식 데이터센터 카드 244장에서 계산해 모두 일치 [계산].",
            Style = (Style)FindResource("Hint"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0),
        });
    }

    /// <summary>Mini face: this card's own picture, any other season's picture of the footballer, an image of the user's, or none.</summary>
    private async Task ShowFacesAsync(SquadSlot s)
    {
        var index = s.Index;
        var header = new TextBlock { Text = "미니페이스", Style = (Style)FindResource("H2"), Margin = new Thickness(0, 14, 0, 2) };
        var hint = new TextBlock { Text = "시즌별 사진 불러오는 중…", Style = (Style)FindResource("Hint"), TextWrapping = TextWrapping.Wrap };
        var grid = new WrapPanel();
        var buttons = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        foreach (var e in new UIElement[] { header, hint, grid, buttons }) Into.Children.Add(e);
        buttons.Children.Add(Action("이 시즌 기본", () => { Faces.Pick(s.Card.SpId, null); return Task.CompletedTask; }));
        buttons.Children.Add(Action("내 이미지…", () =>
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "이미지|*.png;*.jpg;*.jpeg;*.webp;*.bmp", Title = $"{s.Card.Name} 미니페이스" };
            if (dialog.ShowDialog(Window.GetWindow(this)) == true)
            {
                try { Faces.PickFile(s.Card.SpId, dialog.FileName); }
                catch (System.IO.IOException) { Status.Text = "이미지를 복사하지 못했습니다."; }
            }
            return Task.CompletedTask;
        }));
        buttons.Children.Add(Action("사진 없음", () => { Faces.PickNone(s.Card.SpId); return Task.CompletedTask; }));
        if (StudioKit.Squads is not { } squads) return;
        IReadOnlyList<FaceOption> options;
        try
        {
            options = await squads.FaceOptionsAsync(s.Card.SpId);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            options = [];
        }
        if (_selected != index) return;
        hint.Text = options.Count == 0 ? "시즌별 사진 목록을 받지 못했습니다. 내 이미지는 넣을 수 있습니다."
            : $"이 선수의 시즌별 사진 {options.Count}장 (공식 스쿼드메이커 목록). 카드마다 따로 고를 수 있습니다.";
        var chosen = Faces.Choice(s.Card.SpId);
        foreach (var o in options)
        {
            var image = new Image { Height = 54, Width = 54, Stretch = System.Windows.Media.Stretch.Uniform };
            var picked = chosen == o.Url || chosen is null && o.SpId == s.Card.SpId;
            var b = new Button
            {
                Content = new StackPanel { Children = { image, new TextBlock { Text = (picked ? "✓ " : "") + o.Season, FontSize = 10, FontWeight = picked ? FontWeights.Bold : FontWeights.Normal, Foreground = (System.Windows.Media.Brush)FindResource(picked ? "Accent" : "Text"), HorizontalAlignment = HorizontalAlignment.Center } } },
                Style = (Style)FindResource("Ghost"), Padding = new Thickness(3), Margin = new Thickness(0, 0, 4, 4), ToolTip = $"{o.Season} 사진",
                BorderBrush = (System.Windows.Media.Brush)FindResource(picked ? "Accent" : "Line"), BorderThickness = new Thickness(picked ? 2 : 1),
            };
            b.Click += (_, _) => { Faces.Pick(s.Card.SpId, o.Url); _ = ShowSlotAsync(index); };
            grid.Children.Add(b);
            _ = SetImageAsync(image, b, o.Url);
        }
    }

    private static async Task SetFaceAsync(Image image, long spId)
    {
        if (await Faces.GetAsync(spId) is { } source) image.Source = source;
    }

    private static async Task SetImageAsync(Image image, Button button, string url)
    {
        if (await Faces.LoadAsync(url) is { } source) image.Source = source;
        else button.Visibility = Visibility.Collapsed; // withdrawn pictures (licence) are not offered
    }

    private void Line(string label, string value)
    {
        Into.Children.Add(new TextBlock { Text = label, Style = (Style)FindResource("FieldLabel"), Margin = new Thickness(0, 10, 0, 0) });
        Into.Children.Add(new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap });
    }

    private Button Action(string text, Func<Task> change)
    {
        var b = new Button { Content = text, Style = (Style)FindResource("Ghost"), Margin = new Thickness(0, 0, 6, 6), Padding = new Thickness(9, 4, 9, 4) };
        b.Click += async (_, _) => await change();
        return b;
    }
}
