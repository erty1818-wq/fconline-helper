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
    private const double W = 600, H = 800;
    private readonly Canvas _canvas = new() { Width = W, Height = H };
    private IReadOnlyList<SquadSlot> _slots = [];

    public event Action<SquadSlot>? SlotClicked;

    /// <summary>Slot indices to outline (e.g. the ones an upgrade would change).</summary>
    public IReadOnlySet<int> Highlighted { get; set; } = new HashSet<int>();
    public int? Selected { get; private set; }

    public PitchView()
    {
        Stretch = Stretch.Uniform;
        var root = new Grid { Width = W, Height = H };
        root.Children.Add(new Image { Source = Skin.Get("pitch"), Stretch = Stretch.Fill });
        root.Children.Add(_canvas);
        Child = root;
    }

    public void Show(IReadOnlyList<SquadSlot> slots)
    {
        _slots = slots;
        Render();
    }

    private static double LineY(string position) => Formations.Normalize(position) switch
    {
        "GK" => 0.92,
        "CB" or "LB" or "RB" or "LWB" or "RWB" => 0.76,
        "CDM" => 0.6,
        "CM" => 0.5,
        "CAM" or "LM" or "RM" => 0.37,
        "CF" => 0.24,
        _ => 0.09,
    };

    private static int Side(string position) => position.StartsWith('L') ? 0 : position.StartsWith('R') ? 2 : 1;

    private void Render()
    {
        _canvas.Children.Clear();
        foreach (var line in _slots.GroupBy(s => LineY(s.Position)))
        {
            var ordered = line.OrderBy(s => Side(s.Position)).ThenBy(s => s.Index).ToList();
            for (var i = 0; i < ordered.Count; i++)
            {
                var chip = Chip(ordered[i]);
                chip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                var x = W * (i + 1) / (ordered.Count + 1) - chip.DesiredSize.Width / 2;
                Canvas.SetLeft(chip, Math.Clamp(x, 4, W - chip.DesiredSize.Width - 4));
                Canvas.SetTop(chip, H * line.Key - chip.DesiredSize.Height / 2);
                _canvas.Children.Add(chip);
            }
        }
    }

    private FrameworkElement Chip(SquadSlot s)
    {
        var res = Application.Current.Resources;
        var accent = (Brush)res["Accent"];
        var ovr = new TextBlock
        {
            Text = (s.Ovr + s.TeamColorBonus).ToString(), FontSize = 20, FontWeight = FontWeights.Bold, FontFamily = new FontFamily("Segoe UI"),
            Foreground = s.TeamColorBonus > 0 ? accent : (Brush)res["Text"], HorizontalAlignment = HorizontalAlignment.Center,
        };
        var name = new TextBlock { Text = s.Card.Name, FontSize = 12, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 104, HorizontalAlignment = HorizontalAlignment.Center };
        var meta = new TextBlock
        {
            Text = $"{s.Position} · {s.Card.Season} · +{s.Grade}", FontSize = 10, Foreground = (Brush)res["Muted"], HorizontalAlignment = HorizontalAlignment.Center,
        };
        var price = new TextBlock
        {
            Text = s.Owned ? "보유" : Bp.Format(s.Price), FontSize = 10, Foreground = s.Owned ? accent : (Brush)res["Muted"], HorizontalAlignment = HorizontalAlignment.Center,
        };
        var panel = new StackPanel { Children = { ovr, name, meta, price } };
        var outline = s.Index == Selected ? accent : Highlighted.Contains(s.Index) ? (Brush)res["Warn"] : (Brush)res["Line"];
        var border = new Border
        {
            Child = panel, Background = (Brush)res["Raised"], BorderBrush = outline, BorderThickness = new Thickness(s.Index == Selected || Highlighted.Contains(s.Index) ? 2 : 1),
            CornerRadius = new CornerRadius(10), Padding = new Thickness(6, 4, 6, 5), Width = 112, Cursor = Cursors.Hand,
            ToolTip = $"{s.Card.Name} {s.Card.Season} +{s.Grade}\nOVR {s.Ovr}{(s.TeamColorBonus > 0 ? $" (+팀컬러 {s.TeamColorBonus})" : "")} · 환산 {s.EffectiveOvr:0.0} [추정]\n급여 {s.Pay}"
                + (s.RankerUsers > 0 ? $" · 랭커 {s.RankerUsers}명" : "") + (s.Locked ? "\n고정됨" : ""),
        };
        if (s.Locked)
            border.Child = new Grid { Children = { panel, new TextBlock { Text = "🔒", FontSize = 10, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top } } };
        border.MouseLeftButtonUp += (_, _) =>
        {
            Selected = s.Index;
            Render();
            SlotClicked?.Invoke(s);
        };
        return border;
    }
}
