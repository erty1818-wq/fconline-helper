using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using FcHelper.Market;

namespace FcHelper.App.Studio;

/// <summary>
/// Eleven cards on a pitch (skin "pitch" as background, attack at the top). Slots can be placed either by line
/// (traditional LineY/Side) or on a 7-row by 5-col tactical zone grid with drag-and-drop formation editing.
/// </summary>
public sealed class PitchView : Viewbox
{
    // Lines are 104 apart and a card with its face is about as high, so neighbouring lines do not overlap.
    private const double W = 700, H = 800, ChipWidth = 124, FaceHeight = 42;
    private readonly Canvas _canvas = new() { Width = W, Height = H };
    private readonly Canvas _guideCanvas = new() { Width = W, Height = H, IsHitTestVisible = false };
    private IReadOnlyList<SquadSlot> _slots = [];
    /// <summary>Empty slots of a hand-made squad: index and position.</summary>
    private IReadOnlyList<(int Index, string Position)> _empty = [];
    private IReadOnlyList<Spot>? _spots;

    private readonly Dictionary<int, FrameworkElement> _chips = [];
    private int? _pressedSlot;
    private Point _pressPoint;
    private bool _isDragging;
    private bool _chipMouseDown;
    private FrameworkElement? _dragGhost;
    private Spot? _nearestSpot;
    private Border? _highlightBorder;
    private TextBlock? _highlightText;

    private static readonly List<Spot> AllValidSpots = Enumerable.Range(0, 7)
        .SelectMany(r => Enumerable.Range(0, 5).Select(c => new Spot(r, c)))
        .Where(s => !string.IsNullOrEmpty(Formations.PositionAt(s)))
        .ToList();

    public event Action<SquadSlot>? SlotClicked;
    /// <summary>An empty slot was clicked (index, position): the squad maker offers cards for it.</summary>
    public event Action<int, string>? EmptySlotClicked;
    public event Action<int, Spot>? SlotMoved;
    public event Action? BackgroundClicked;

    /// <summary>Slot indices to outline (e.g. the ones an upgrade would change).</summary>
    public IReadOnlySet<int> Highlighted { get; set; } = new HashSet<int>();
    public int? Selected { get; private set; }

    public PitchView()
    {
        Faces.Changed += () => Dispatcher.BeginInvoke(Render);
        Stretch = Stretch.Uniform;
        var root = new Grid { Width = W, Height = H };
        root.Children.Add(new Image { Source = Skin.Get("pitch"), Stretch = Stretch.Fill });
        root.Children.Add(_guideCanvas);
        root.Children.Add(_canvas);
        Child = root;

        Focusable = true;
        _canvas.PreviewMouseLeftButtonDown += OnCanvasMouseDown;
        _canvas.PreviewMouseMove += OnCanvasMouseMove;
        _canvas.PreviewMouseLeftButtonUp += OnCanvasMouseUp;
        _canvas.LostMouseCapture += (_, _) => CancelDrag();
        PreviewKeyDown += OnPreviewKeyDown;
    }

    public void Show(IReadOnlyList<SquadSlot> slots)
    {
        _slots = slots;
        _empty = [];
        _spots = null;
        Render();
    }

    /// <summary>A squad being made by hand: <paramref name="positions"/> are the formation's slots, null entries are empty.</summary>
    public void Show(IReadOnlyList<SquadSlot?> slots, IReadOnlyList<string> positions)
    {
        _slots = slots.Where(s => s is not null).Cast<SquadSlot>().ToList();
        _empty = positions.Select((p, i) => (i, Formations.Normalize(p))).Where(x => slots[x.i] is null).ToList();
        _spots = null;
        Render();
    }

    public void Show(IReadOnlyList<SquadSlot?> slots, IReadOnlyList<string> positions, IReadOnlyList<Spot> spots)
    {
        _slots = slots.Where(s => s is not null).Cast<SquadSlot>().ToList();
        _empty = positions.Select((p, i) => (i, Formations.Normalize(p))).Where(x => slots[x.i] is null).ToList();
        _spots = spots;
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
        if (_spots is { Count: 11 })
        {
            RenderBySpots();
        }
        else
        {
            RenderByLine();
        }
    }

    private void RenderBySpots()
    {
        _canvas.Children.Clear();
        _chips.Clear();
        if (_spots is null) return;

        for (var i = 0; i < 11; i++)
        {
            var spot = _spots[i];
            var filled = _slots.FirstOrDefault(s => s.Index == i);
            var chip = filled is not null
                ? Chip(filled)
                : EmptyChip(i, Formations.PositionAt(spot));

            chip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var cx = W * Formations.ColX(spot.Col);
            var cy = H * Formations.RowY(spot.Row);
            var x = Math.Clamp(cx - chip.DesiredSize.Width / 2, 4, W - chip.DesiredSize.Width - 4);
            var y = Math.Clamp(cy - chip.DesiredSize.Height / 2, 4, H - chip.DesiredSize.Height - 4);
            Canvas.SetLeft(chip, x);
            Canvas.SetTop(chip, y);

            _chips[i] = chip;
            _canvas.Children.Add(chip);
        }
    }

    private void RenderByLine()
    {
        _canvas.Children.Clear();
        _chips.Clear();
        var items = _slots.Select(s => (s.Index, s.Position, Slot: (SquadSlot?)s)).Concat(_empty.Select(e => (e.Index, e.Position, Slot: (SquadSlot?)null)));
        foreach (var line in items.GroupBy(s => LineY(s.Position)))
        {
            var ordered = line.OrderBy(s => Side(s.Position)).ThenBy(s => s.Index).ToList();
            for (var i = 0; i < ordered.Count; i++)
            {
                var chip = ordered[i].Slot is { } slot ? Chip(slot) : EmptyChip(ordered[i].Index, ordered[i].Position);
                chip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
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

        if (_spots is not null)
        {
            var slotIndex = index;
            border.PreviewMouseLeftButtonDown += (_, e) => OnChipMouseDown(slotIndex, e);
        }
        else
        {
            border.MouseLeftButtonUp += (_, _) =>
            {
                Selected = index;
                Render();
                EmptySlotClicked?.Invoke(index, position);
            };
        }
        return border;
    }

    private FrameworkElement Chip(SquadSlot s)
    {
        var res = Application.Current.Resources;
        var accent = (Brush)res["Accent"];
        var muted = (Brush)res["Muted"];
        var warn = (Brush)res["Warn"];

        // The in-game OVR (grade, 적응도, team colours, 집중훈련) when the squad maker worked it out.
        var shown = (int)Math.Floor(s.ShownOvr + (s.Final is null ? 0.5 : 0));
        var boosted = s.Final is { } f ? f.AllStats + f.Detail + f.Training > 0 : s.TeamColorBonus > 0;
        var ovr = new TextBlock
        {
            Text = shown.ToString(), FontSize = 21, FontWeight = FontWeights.Bold, FontFamily = new FontFamily("Segoe UI"),
            Foreground = boosted ? accent : (Brush)res["Text"], VerticalAlignment = VerticalAlignment.Center,
        };

        var isOffPos = !s.Card.Positions.ContainsKey(s.Position);
        var posBrush = isOffPos ? warn : muted;
        var position = new TextBlock
        {
            Text = s.Position, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = posBrush,
            Margin = new Thickness(5, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
            ToolTip = isOffPos ? "원래 포지션 아님" : null,
        };

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
        var cardGrid = new Grid { Children = { panel } };

        // 꿀선수 배지: trading well under similar cards
        if (!s.Owned && Honey.IsHoney(s.Discount))
        {
            var beeBadge = new Border
            {
                Width = 16,
                Height = 16,
                CornerRadius = new CornerRadius(8),
                Background = (Brush)res["Panel"],
                BorderBrush = (Brush)res["Warn"],
                BorderThickness = new Thickness(1),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(1, 1, 0, 0),
                Child = AppIcons.Make("Icon.Honey", 12),
                ToolTip = "꿀선수: 같은 스펙 카드보다 30% 이상 싸게 거래",
            };
            cardGrid.Children.Add(beeBadge);
        }

        // 고정된 카드 배지
        if (s.Locked)
        {
            var lockBadge = new Border
            {
                Width = 16,
                Height = 16,
                CornerRadius = new CornerRadius(8),
                Background = (Brush)res["Panel"],
                BorderBrush = (Brush)res["Line"],
                BorderThickness = new Thickness(1),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 1, 1, 0),
                Child = AppIcons.Make("Icon.Lock", 11),
                ToolTip = "고정됨 (AI가 바꾸지 않음)",
            };
            cardGrid.Children.Add(lockBadge);
        }

        var outline = s.Index == Selected ? accent : Highlighted.Contains(s.Index) ? warn : (Brush)res["Line"];
        var tip = $"{s.Card.Name} {s.Card.Season} +{s.Grade} · {s.Position}" + (isOffPos ? " (원래 포지션 아님)\n" : "\n")
            + (s.Final is { } fin ? $"인게임 OVR {fin.Value}: {fin.Breakdown}" : $"OVR {s.Ovr}{(s.TeamColorBonus > 0 ? $" (+팀컬러 {s.TeamColorBonus:0.#})" : "")}")
            + $"\n환산 {s.EffectiveOvr:0.0} [추정] · 급여 {s.Pay}" + (s.RankerUsers > 0 ? $" · 랭커 {s.RankerUsers}명" : "") + (s.Locked ? "\n고정됨" : "");
        var border = new Border
        {
            Child = cardGrid, Background = Skin.Brush("card-frame") ?? (Brush)res["Raised"], BorderBrush = outline, BorderThickness = new Thickness(s.Index == Selected || Highlighted.Contains(s.Index) ? 2 : 1),
            CornerRadius = new CornerRadius(9), Padding = new Thickness(4, 3, 4, 4), Width = ChipWidth, Cursor = Cursors.Hand, ToolTip = tip,
        };

        if (_spots is not null)
        {
            var slotIndex = s.Index;
            border.PreviewMouseLeftButtonDown += (_, e) => OnChipMouseDown(slotIndex, e);
        }
        else
        {
            border.MouseLeftButtonUp += (_, _) =>
            {
                Selected = s.Index;
                Render();
                SlotClicked?.Invoke(s);
            };
        }
        return border;
    }

    private void OnCanvasMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!_chipMouseDown)
        {
            _pressedSlot = null;
        }
        _chipMouseDown = false;
    }

    private void OnChipMouseDown(int slotIndex, MouseButtonEventArgs e)
    {
        _chipMouseDown = true;
        _pressedSlot = slotIndex;
        _pressPoint = e.GetPosition(_canvas);
    }

    private void OnCanvasMouseMove(object sender, MouseEventArgs e)
    {
        if (_pressedSlot is not { } slotIndex) return;

        var cur = e.GetPosition(_canvas);
        if (!_isDragging)
        {
            if ((cur - _pressPoint).Length >= 5 && _spots is not null)
            {
                StartDrag(slotIndex, cur);
            }
            return;
        }

        UpdateDrag(cur);
    }

    private void StartDrag(int slotIndex, Point cur)
    {
        _isDragging = true;
        _canvas.CaptureMouse();

        if (_chips.TryGetValue(slotIndex, out var chip))
        {
            chip.Opacity = 0.35;
        }

        _dragGhost = CreateGhost(slotIndex);
        _dragGhost.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(_dragGhost, cur.X - ChipWidth / 2);
        Canvas.SetTop(_dragGhost, cur.Y - FaceHeight);
        _canvas.Children.Add(_dragGhost);

        DrawGuides();
        UpdateNearest(cur);
    }

    private void UpdateDrag(Point cur)
    {
        if (_dragGhost is not null)
        {
            Canvas.SetLeft(_dragGhost, cur.X - ChipWidth / 2);
            Canvas.SetTop(_dragGhost, cur.Y - FaceHeight);
        }
        UpdateNearest(cur);
    }

    private void OnCanvasMouseUp(object sender, MouseButtonEventArgs e)
    {
        var cur = e.GetPosition(_canvas);
        if (_isDragging)
        {
            var slot = _pressedSlot;
            var targetSpot = _nearestSpot;
            var inside = cur.X >= 0 && cur.X <= W && cur.Y >= 0 && cur.Y <= H;

            CancelDrag();

            if (inside && slot is { } s && targetSpot is not null)
            {
                SlotMoved?.Invoke(s, targetSpot);
            }
        }
        else if (_pressedSlot is { } s)
        {
            _pressedSlot = null;
            Select(s);
            if (_slots.FirstOrDefault(x => x.Index == s) is { } filled)
            {
                SlotClicked?.Invoke(filled);
            }
            else
            {
                var pos = _spots is not null && s < _spots.Count
                    ? Formations.PositionAt(_spots[s])
                    : (_empty.FirstOrDefault(x => x.Index == s).Position ?? "");
                EmptySlotClicked?.Invoke(s, pos);
            }
        }
        else
        {
            BackgroundClicked?.Invoke();
        }
    }

    public void CancelDrag()
    {
        if (!_isDragging && _pressedSlot is null) return;

        if (_isDragging)
        {
            if (_pressedSlot is { } s && _chips.TryGetValue(s, out var chip))
            {
                chip.Opacity = 1.0;
            }
            if (_dragGhost is not null)
            {
                _canvas.Children.Remove(_dragGhost);
                _dragGhost = null;
            }
            _guideCanvas.Children.Clear();
            _highlightBorder = null;
            _highlightText = null;
            _canvas.ReleaseMouseCapture();
        }

        _isDragging = false;
        _pressedSlot = null;
        _nearestSpot = null;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _isDragging)
        {
            CancelDrag();
            e.Handled = true;
        }
    }

    private FrameworkElement CreateGhost(int slotIndex)
    {
        var res = Application.Current.Resources;
        var slot = _slots.FirstOrDefault(s => s.Index == slotIndex);
        var pos = _spots is not null && slotIndex < _spots.Count
            ? Formations.PositionAt(_spots[slotIndex])
            : (slot?.Position ?? "선수");

        var panel = new StackPanel();
        if (slot is not null)
        {
            var shown = (int)Math.Floor(slot.ShownOvr + (slot.Final is null ? 0.5 : 0));
            var head = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                Children =
                {
                    new TextBlock { Text = shown.ToString(), FontSize = 18, FontWeight = FontWeights.Bold, Foreground = (Brush)res["Text"] },
                    new TextBlock { Text = slot.Position, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = (Brush)res["Muted"], Margin = new Thickness(4, 2, 0, 0) },
                },
            };
            var name = new TextBlock
            {
                Text = slot.Card.Name, FontSize = 12, FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = ChipWidth - 10, HorizontalAlignment = HorizontalAlignment.Center,
            };
            panel.Children.Add(head);
            panel.Children.Add(name);
        }
        else
        {
            panel.Children.Add(new TextBlock { Text = "+", FontSize = 22, FontWeight = FontWeights.Bold, Foreground = (Brush)res["Accent"], HorizontalAlignment = HorizontalAlignment.Center });
            panel.Children.Add(new TextBlock { Text = pos, FontSize = 13, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center });
        }

        return new Border
        {
            Child = panel,
            Background = Skin.Brush("card-frame") ?? (Brush)res["Raised"],
            BorderBrush = (Brush)res["Accent"],
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(6, 6, 6, 6),
            Width = ChipWidth,
            Opacity = 0.9,
            IsHitTestVisible = false,
            Effect = new DropShadowEffect
            {
                BlurRadius = 16,
                ShadowDepth = 6,
                Opacity = 0.6,
                Color = Colors.Black,
            },
        };
    }

    private void DrawGuides()
    {
        _guideCanvas.Children.Clear();
        var res = Application.Current.Resources;
        var muted = (Brush)res["Muted"];
        var line = (Brush)res["Line"];

        foreach (var spot in AllValidSpots)
        {
            var cx = W * Formations.ColX(spot.Col);
            var cy = H * Formations.RowY(spot.Row);

            var dot = new Ellipse
            {
                Width = 6, Height = 6, Fill = line, IsHitTestVisible = false,
            };
            Canvas.SetLeft(dot, cx - 3);
            Canvas.SetTop(dot, cy - 3);
            _guideCanvas.Children.Add(dot);

            var txt = new TextBlock
            {
                Text = Formations.PositionAt(spot), FontSize = 10, FontWeight = FontWeights.SemiBold,
                Foreground = muted, IsHitTestVisible = false,
            };
            txt.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(txt, cx - txt.DesiredSize.Width / 2);
            Canvas.SetTop(txt, cy + 5);
            _guideCanvas.Children.Add(txt);
        }

        _highlightText = new TextBlock
        {
            FontSize = 11, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false,
        };
        _highlightBorder = new Border
        {
            Width = ChipWidth + 4, Height = 62, CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(2), Child = _highlightText, IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
        };
        _guideCanvas.Children.Add(_highlightBorder);
    }

    private void UpdateNearest(Point cur)
    {
        if (_highlightBorder is null || _highlightText is null || _spots is null || _pressedSlot is not { } slotIndex) return;

        var inside = cur.X >= 0 && cur.X <= W && cur.Y >= 0 && cur.Y <= H;
        if (!inside)
        {
            _nearestSpot = null;
            _highlightBorder.Visibility = Visibility.Collapsed;
            return;
        }

        Spot? bestSpot = null;
        var bestDist = double.MaxValue;
        foreach (var spot in AllValidSpots)
        {
            var cx = W * Formations.ColX(spot.Col);
            var cy = H * Formations.RowY(spot.Row);
            var d = (cur.X - cx) * (cur.X - cx) + (cur.Y - cy) * (cur.Y - cy);
            if (d < bestDist)
            {
                bestDist = d;
                bestSpot = spot;
            }
        }

        _nearestSpot = bestSpot;
        if (bestSpot is null)
        {
            _highlightBorder.Visibility = Visibility.Collapsed;
            return;
        }

        var res = Application.Current.Resources;
        var accent = (Brush)res["Accent"];
        var warn = (Brush)res["Warn"];
        var move = Formations.Move(_spots, slotIndex, bestSpot);

        var bcx = W * Formations.ColX(bestSpot.Col);
        var bcy = H * Formations.RowY(bestSpot.Row);
        Canvas.SetLeft(_highlightBorder, bcx - (ChipWidth + 4) / 2);
        Canvas.SetTop(_highlightBorder, bcy - 31);
        _highlightBorder.Visibility = Visibility.Visible;

        if (!move.Success)
        {
            _highlightBorder.BorderBrush = warn;
            _highlightBorder.Background = new SolidColorBrush(Color.FromArgb(50, 255, 75, 75));
            _highlightText.Text = move.Reason ?? "이동 불가";
            _highlightText.Foreground = warn;
        }
        else if (_spots.Where((s, i) => i != slotIndex && s == bestSpot).Any())
        {
            _highlightBorder.BorderBrush = warn;
            _highlightBorder.Background = new SolidColorBrush(Color.FromArgb(40, 255, 180, 50));
            _highlightText.Text = "선수 교체";
            _highlightText.Foreground = warn;
        }
        else
        {
            _highlightBorder.BorderBrush = accent;
            _highlightBorder.Background = new SolidColorBrush(Color.FromArgb(40, 61, 220, 151));
            _highlightText.Text = Formations.PositionAt(bestSpot);
            _highlightText.Foreground = accent;
        }
    }

    private static async Task LoadFaceAsync(Image image, long spId)
    {
        var source = await Faces.GetAsync(spId);
        if (source is not null) image.Source = source;
    }
}
