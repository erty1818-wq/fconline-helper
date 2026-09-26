using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using FcHelper.Core;
using FcHelper.Services;

namespace FcHelper.App;

/// <summary>슈팅 tab (OS-05): where the opponent shoots from, and where they let shots in from, on a half pitch.</summary>
public partial class SearchWindow
{
    // The attacking end of a real pitch: 68 m wide, drawn 36 m deep (almost every shot is inside that);
    // the few from further out sit on the bottom edge.
    private const double ShotDepth = 36, ShotW = 340, ShotH = ShotW * ShotDepth / 68;
    private string? _shotsFor;
    private bool _shotsConceded;

    private void ShowShots(OpponentReport report, bool force = false)
    {
        var key = $"{report.Ouid}|{report.Matches.Count}|{report.Matches.FirstOrDefault()?.MatchId}|{_shotsConceded}";
        if (!force && _shotsFor == key) return;
        _shotsFor = key;

        var map = ShotMap.Of(report.Matches, report.Ouid, _shotsConceded);
        var mine = Chip("상대가 찬 슛", !_shotsConceded, "검색한 구단주가 찬 슛");
        var theirs = Chip("상대가 허용한 슛", _shotsConceded, "검색한 구단주를 상대로 다른 사람들이 찬 슛: 수비가 약한 곳");
        mine.Click += (_, _) => { _shotsConceded = false; ShowShots(report, force: true); };
        theirs.Click += (_, _) => { _shotsConceded = true; ShowShots(report, force: true); };
        var switcher = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
        switcher.Children.Add(mine);
        switcher.Children.Add(theirs);

        if (map.Shots == 0)
        {
            SetTab(ShotsContent, switcher, TabHint("슈팅 기록이 있는 경기가 없습니다."));
            return;
        }

        var inBox = map.Dots.Count(d => d.InBox);
        var summary = new TextBlock
        {
            Text = $"최근 {map.Matches}경기 · 슈팅 {map.Shots} (경기당 {map.PerMatch:0.0}) · 유효 {map.OnTarget} · 골 {map.Goals}"
                + $" · 골 전환 {100.0 * map.Goals / map.Shots:0}% · 박스 안 {100.0 * inBox / map.Shots:0}%",
            TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold,
        };
        var legend = new WrapPanel { Margin = new Thickness(0, 4, 0, 6) };
        foreach (var (label, brush) in new[] { ("골", Res<Brush>("Accent")), ("유효(막힘)", Res<Brush>("Info")), ("빗나감", Res<Brush>("Muted")) })
        {
            legend.Children.Add(new Ellipse { Width = 9, Height = 9, Fill = brush, Margin = new Thickness(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center });
            legend.Children.Add(new TextBlock { Text = label, FontSize = 12, Foreground = Res<Brush>("Muted"), Margin = new Thickness(0, 0, 12, 0) });
        }
        legend.Children.Add(new TextBlock { Text = "[직접] 위가 공격하는 골문", FontSize = 12, Foreground = Res<Brush>("Muted") });

        var leaders = PlayerLeaders.Of(report.Matches, report.Ouid);
        var names = _app.Db?.GetPlayerNames(map.Dots.Select(d => d.SpId).Concat(leaders.All.Select(l => l.SpId)).Distinct()) ?? [];
        var pitch = new Viewbox { Child = ShotPitch(map, names), MaxWidth = 520, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center };
        SetTab(ShotsContent, switcher, summary, legend, pitch);
        AddRecentSummary(report, leaders, names);
    }

    /// <summary>OS-06: how the opponent scores (goal types) and who carries their team, under the map.</summary>
    private void AddRecentSummary(OpponentReport report, PlayerLeaders leaders, IReadOnlyDictionary<int, string> names)
    {
        ShotsContent.Children.Add(SectionTitle("최근 경기 요약"));
        if (_view is { GoalTypes.Count: > 0 } view && report.Analysis.GoalTypes.Count > 0)
        {
            ShotsContent.Children.Add(new TextBlock { Text = $"골 유형 [직접] · 골 {report.Analysis.GoalTypes[0].Total}", Foreground = Res<Brush>("Muted"), FontSize = 12 });
            ShotsContent.Children.Add(new ItemsControl { ItemsSource = view.GoalTypes, ItemTemplate = (DataTemplate)FindResource("ShareBar"), Margin = new Thickness(0, 2, 0, 8) });
        }

        var cards = new UniformGrid { Columns = 3, Margin = new Thickness(0, 2, 0, 0) };
        void Add(string title, PlayerLine? line, Func<PlayerLine, string> detail)
        {
            var box = new StackPanel();
            box.Children.Add(new TextBlock { Text = title, Foreground = Res<Brush>("Muted"), FontSize = 12 });
            if (line is null)
            {
                box.Children.Add(new TextBlock { Text = "기록 없음", Style = (Style)FindResource("Hint") });
            }
            else
            {
                var face = new Image { Width = 44, Height = 44, Margin = new Thickness(0, 4, 0, 2), HorizontalAlignment = HorizontalAlignment.Left };
                Studio.Pictures.SetFaceOf(face, line.SpId);
                box.Children.Add(face);
                box.Children.Add(new TextBlock { Text = names.GetValueOrDefault(line.SpId) ?? $"#{line.SpId}", FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
                box.Children.Add(new TextBlock { Text = detail(line), Foreground = Res<Brush>("Muted"), FontSize = 12, TextWrapping = TextWrapping.Wrap });
            }
            cards.Children.Add(new Border { Style = (Style)FindResource("CardPanel"), Padding = new Thickness(10), Margin = new Thickness(0, 0, 6, 0), Child = box });
        }
        Add("최고 평점", leaders.TopRated, l => $"평균 {l.AvgRating:0.00} · {l.Apps}경기");
        Add("최다 득점", leaders.TopScorer, l => $"{l.Goals}골 {l.Assists}도움 · {l.Apps}경기");
        Add("최다 도움", leaders.TopAssister, l => $"{l.Assists}도움 {l.Goals}골 · {l.Apps}경기");
        ShotsContent.Children.Add(cards);
        ShotsContent.Children.Add(new TextBlock
        {
            Text = $"[직접] 경기 기록의 선수 평점·골·도움 합. 평점은 {PlayerLeaders.MinAppsForRating}경기 이상 뛴 선수 중에서 고른다.",
            Style = (Style)FindResource("Hint"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0),
        });
    }

    private ToggleButton Chip(string text, bool on, string tip) => new()
    {
        Content = text, IsChecked = on, Style = (Style)FindResource("Chip"), Margin = new Thickness(0, 0, 6, 0), ToolTip = tip,
    };

    private Canvas ShotPitch(ShotMap map, IReadOnlyDictionary<int, string> names)
    {
        var canvas = new Canvas { Width = ShotW, Height = ShotH, Background = Res<Brush>("Pitch"), ClipToBounds = true };
        var line = new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF));
        double Mx(double metres) => metres / 68 * ShotW;   // across
        double My(double metres) => metres / ShotDepth * ShotH; // down from the goal line

        void Rect(double width, double depth)
        {
            var r = new Rectangle { Width = Mx(width), Height = My(depth), Stroke = line, StrokeThickness = 1 };
            Canvas.SetLeft(r, (ShotW - r.Width) / 2);
            Canvas.SetTop(r, 0);
            canvas.Children.Add(r);
        }
        canvas.Children.Add(new Rectangle { Width = ShotW, Height = ShotH, Stroke = line, StrokeThickness = 1 });
        Rect(40.32, 16.5);
        Rect(18.32, 5.5);
        var goal = new Rectangle { Width = Mx(7.32), Height = 4, Fill = line };
        Canvas.SetLeft(goal, (ShotW - goal.Width) / 2);
        canvas.Children.Add(goal);
        var spot = new Ellipse { Width = 3, Height = 3, Fill = line };
        Canvas.SetLeft(spot, ShotW / 2 - 1.5);
        Canvas.SetTop(spot, My(11) - 1.5);
        canvas.Children.Add(spot);
        // The penalty arc: the part of a 9.15 m circle round the spot that lies outside the box.
        var arc = new Ellipse { Width = Mx(18.3), Height = My(18.3), Stroke = line, StrokeThickness = 1,
            Clip = new RectangleGeometry(new Rect(0, My(16.5) - My(11) + My(9.15), Mx(18.3), My(18.3))) };
        Canvas.SetLeft(arc, (ShotW - arc.Width) / 2);
        Canvas.SetTop(arc, My(11) - My(9.15));
        canvas.Children.Add(arc);

        // Misses first so goals stay on top where dots overlap.
        foreach (var d in map.Dots.OrderBy(d => d.Result switch { ShotResult.OffTarget => 0, ShotResult.OnTarget => 1, _ => 2 }))
        {
            var (brush, word) = d.Result switch
            {
                ShotResult.Goal => (Res<Brush>("Accent"), "골"),
                ShotResult.OnTarget => (Res<Brush>("Info"), "유효"),
                _ => (Res<Brush>("Muted"), "빗나감"),
            };
            var size = d.Result == ShotResult.Goal ? 8.0 : 6.5;
            var dot = new Ellipse
            {
                Width = size, Height = size, Fill = brush, Opacity = d.Result == ShotResult.OffTarget ? 0.6 : 0.95,
                Stroke = d.Result == ShotResult.Goal ? Res<Brush>("Bg") : null, StrokeThickness = 1,
                ToolTip = $"{names.GetValueOrDefault(d.SpId) ?? $"#{d.SpId}"} · {ShotTypes.Label(d.Type)} · {d.Minute}분 · {word}",
            };
            Canvas.SetLeft(dot, d.Across * ShotW - size / 2);
            Canvas.SetTop(dot, Math.Min(d.Down * 52.5 / ShotDepth, 1) * ShotH - size / 2);
            canvas.Children.Add(dot);
        }
        return canvas;
    }
}
