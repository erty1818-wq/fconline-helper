using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FcHelper.Market;

namespace FcHelper.App.Studio;

/// <summary>
/// Eleven cards on a pitch (skin "pitch" as background, attack at the top). Slots are placed by line — GK, defence,
/// holding midfield, midfield, attacking midfield, forwards — and spread left to right by their side.
/// </summary>
public sealed class PitchView : Viewbox
{
    // Lines are 104 apart and a card with its face is about as high, so neighbouring lines do not overlap.
    private const double W = 700, H = 800, ChipWidth = 124, FaceHeight = 42;
    private readonly Canvas _canvas = new() { Width = W, Height = H };
    private IReadOnlyList<SquadSlot> _slots = [];
    /// <summary>Empty slots of a hand-made squad: index and position.</summary>
    private IReadOnlyList<(int Index, string Position)> _empty = [];

    public event Action<SquadSlot>? SlotClicked;
    /// <summary>An empty slot was clicked (index, position): the squad maker offers cards for it.</summary>
    public event Action<int, string>? EmptySlotClicked;

    /// <summary>Slot indices to outline (e.g. the ones an upgrade would change).</summary>
    public IReadOnlySet<int> Highlighted { get; set; } = new HashSet<int>();
    public int? Selected { get; private set; }

    /// <summary>Set while the user drags the zoomed pitch around: the release is not a click on a card.</summary>
    public bool ClicksBlocked { get; set; }

    public PitchView()
    {
        Faces.Changed += () => Dispatcher.BeginInvoke(Render);
        Stretch = Stretch.Uniform;
        var root = new Grid { Width = W, Height = H };
        root.Children.Add(new Image { Source = Skin.Get("pitch"), Stretch = Stretch.Fill });
        root.Children.Add(_canvas);
        Child = root;
    }

    public void Show(IReadOnlyList<SquadSlot> slots)
    {
        _slots = slots;
        _empty = [];
        Render();
    }

    /// <summary>A squad being made by hand: <paramref name="positions"/> are the formation's slots, null entries are empty.</summary>
    public void Show(IReadOnlyList<SquadSlot?> slots, IReadOnlyList<string> positions)
    {
        _slots = slots.Where(s => s is not null).Cast<SquadSlot>().ToList();
        _empty = positions.Select((p, i) => (i, Formations.Normalize(p))).Where(x => slots[x.i] is null).ToList();
        Render();
    }

    public void Select(int? index)
    {
        Selected = index;
        Render();
    }

    private static double LineY(string position) => Formations.Normalize(position) switch
    {
        "GK" => 0.92,
        "CB" or "LB" or "RB" or "LWB" or "RWB" => 0.76,
        "CDM" => 0.605,
        "CM" => 0.475,
        "CAM" or "LM" or "RM" => 0.345,
        "CF" => 0.215,
        _ => 0.08,
    };

    private static int Side(string position) => position.StartsWith('L') ? 0 : position.StartsWith('R') ? 2 : 1;

    private void Render()
    {
        _canvas.Children.Clear();
        var items = _slots.Select(s => (s.Index, s.Position, Slot: (SquadSlot?)s)).Concat(_empty.Select(e => (e.Index, e.Position, Slot: (SquadSlot?)null)));
        foreach (var line in items.GroupBy(s => LineY(s.Position)))
        {
            var ordered = line.OrderBy(s => Side(s.Position)).ThenBy(s => s.Index).ToList();
            for (var i = 0; i < ordered.Count; i++)
            {
                var chip = ordered[i].Slot is { } slot ? Chip(slot) : EmptyChip(ordered[i].Index, ordered[i].Position);
                chip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                // Four or five on a line spread over the whole width (a back five needs it), fewer sit inward.
                var centre = ordered.Count >= 4 ? W * (i + 0.5) / ordered.Count : W * (i + 1) / (ordered.Count + 1);
                var x = centre - chip.DesiredSize.Width / 2;
                Canvas.SetLeft(chip, Math.Clamp(x, 4, W - chip.DesiredSize.Width - 4));
                Canvas.SetTop(chip, H * line.Key - chip.DesiredSize.Height / 2);
                _canvas.Children.Add(chip);
            }
        }
    }

    private FrameworkElement EmptyChip(int index, string position)
    {
        var res = Application.Current.Resources;
        var selected = index == Selected;
        var border = new Border
        {
            Child = new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = "+", FontSize = 28, FontWeight = FontWeights.Bold, Foreground = (Brush)res["Accent"], HorizontalAlignment = HorizontalAlignment.Center },
                    new TextBlock { Text = position, FontSize = 15, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center },
                    new TextBlock { Text = "선수 추가", FontSize = 12, Foreground = (Brush)res["Muted"], HorizontalAlignment = HorizontalAlignment.Center },
                },
            },
            Background = Skin.Brush("card-empty") ?? (Brush)res["Panel"], BorderBrush = selected ? (Brush)res["Accent"] : (Brush)res["Line"],
            BorderThickness = new Thickness(selected ? 2 : 1), CornerRadius = new CornerRadius(10), Padding = new Thickness(6, 5, 6, 6),
            Width = ChipWidth, Cursor = Cursors.Hand, ToolTip = $"{position}: 눌러서 선수 고르기",
        };
        border.MouseLeftButtonUp += (_, _) =>
        {
            if (ClicksBlocked) return;
            Selected = index;
            Render();
            EmptySlotClicked?.Invoke(index, position);
        };
        return border;
    }

    private FrameworkElement Chip(SquadSlot s)
    {
        var res = Application.Current.Resources;
        var accent = (Brush)res["Accent"];
        var muted = (Brush)res["Muted"];
        // The in-game OVR (grade, 적응도, team colours, 집중훈련) when the squad maker worked it out.
        var shown = (int)Math.Floor(s.ShownOvr + (s.Final is null ? 0.5 : 0));
        var boosted = s.Final is { } f ? f.AllStats + f.Detail + f.Training > 0 : s.TeamColorBonus > 0;
        var ovr = new TextBlock
        {
            Text = shown.ToString(), FontSize = 21, FontWeight = FontWeights.Bold, FontFamily = new FontFamily("Segoe UI"),
            Foreground = boosted ? accent : (Brush)res["Text"], VerticalAlignment = VerticalAlignment.Center,
        };
        var position = new TextBlock { Text = s.Position, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = muted, Margin = new Thickness(5, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        var head = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Children = { ovr, position } };
        if (s.Final is { Exact: false }) head.Children.Add(new TextBlock { Text = "~", FontSize = 12, Foreground = muted, Margin = new Thickness(2, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, ToolTip = "카드 스탯을 받는 중이라 추정값입니다" });
        var name = new TextBlock { Text = s.Card.Name, FontSize = 13, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = ChipWidth - 10, HorizontalAlignment = HorizontalAlignment.Center };
        var meta = new TextBlock
        {
            Text = $"+{s.Grade} · {s.Card.Season} · {(s.Owned ? "보유" : Bp.Format(s.Price))}", FontSize = 10.5, Foreground = muted,
            TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = ChipWidth - 10, HorizontalAlignment = HorizontalAlignment.Center,
        };
        var panel = new StackPanel { Children = { head, name, meta } };
        if (Faces.Enabled)
        {
            var face = new Image { Height = FaceHeight, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center, Source = Faces.Cached(s.Card.SpId) };
            RenderOptions.SetBitmapScalingMode(face, BitmapScalingMode.HighQuality);
            if (face.Source is null) _ = LoadFaceAsync(face, s.Card.SpId);
            panel.Children.Insert(0, new Border { Height = FaceHeight, Child = face, ClipToBounds = true });
        }
        // 🐝 꿀선수: trading well under similar cards (AI squads only hold cards that trade).
        FrameworkElement body = !s.Owned && Honey.IsHoney(s.Discount)
            ? new Grid { Children = { panel, new TextBlock { Text = Honey.Mark, Style = (Style)res["HoneyMark"], FontSize = 13, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top } } }
            : panel;
        var outline = s.Index == Selected ? accent : Highlighted.Contains(s.Index) ? (Brush)res["Warn"] : (Brush)res["Line"];
        var tip = $"{s.Card.Name} {s.Card.Season} +{s.Grade} · {s.Position}\n"
            + (s.Final is { } fin ? $"인게임 OVR {fin.Value}: {fin.Breakdown}" : $"OVR {s.Ovr}{(s.TeamColorBonus > 0 ? $" (+팀컬러 {s.TeamColorBonus:0.#})" : "")}")
            + $"\n환산 {s.EffectiveOvr:0.0} [추정] · 급여 {s.Pay}" + (s.RankerUsers > 0 ? $" · 랭커 {s.RankerUsers}명" : "") + (s.Locked ? "\n고정됨" : "");
        var border = new Border
        {
            Child = body, Background = Skin.Brush("card-frame") ?? (Brush)res["Raised"], BorderBrush = outline, BorderThickness = new Thickness(s.Index == Selected || Highlighted.Contains(s.Index) ? 2 : 1),
            CornerRadius = new CornerRadius(9), Padding = new Thickness(4, 3, 4, 4), Width = ChipWidth, Cursor = Cursors.Hand, ToolTip = tip,
        };
        if (s.Locked)
        {
            // Detach the panel first: an element can have one parent (fixed and owned cards crashed 내 스쿼드 here).
            border.Child = null;
            border.Child = new Grid { Children = { body, new TextBlock { Text = "🔒", FontSize = 10, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top } } };
        }
        border.MouseLeftButtonUp += (_, _) =>
        {
            if (ClicksBlocked) return;
            Selected = s.Index;
            Render();
            SlotClicked?.Invoke(s);
        };
        return border;
    }

    private static async Task LoadFaceAsync(Image image, long spId)
    {
        var source = await Faces.GetAsync(spId);
        if (source is not null) image.Source = source;
    }
}
