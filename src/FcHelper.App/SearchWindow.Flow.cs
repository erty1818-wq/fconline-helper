using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using FcHelper.Services;

namespace FcHelper.App;

/// <summary>흐름 tab: the stat line and the shot timeline (OS-08); time buckets and habits follow (OS-09, OS-10).</summary>
public partial class SearchWindow
{
    private string? _flowFor;

    private void ShowFlow(OpponentReport report)
    {
        var key = $"{report.Ouid}|{report.Matches.Count}|{report.Matches.FirstOrDefault()?.MatchId}";
        if (_flowFor == key) return;
        _flowFor = key;

        var line = StatLine.Of(report.Matches, report.Ouid);
        if (line.Matches == 0)
        {
            SetTab(FlowContent, TabHint("기록이 있는 경기가 없습니다 (몰수 경기만 있음)."));
            return;
        }
        SetTab(FlowContent, SectionTitle($"경기 요약 · 최근 {line.Matches}경기 평균 [직접]"), StatTiles(line));

        var taken = ShotMap.Of(report.Matches, report.Ouid, conceded: false);
        var allowed = ShotMap.Of(report.Matches, report.Ouid, conceded: true);
        FlowContent.Children.Add(SectionTitle("슈팅 타임라인 [직접]"));
        FlowContent.Children.Add(new TextBlock
        {
            Text = $"{line.Matches}경기를 겹쳐 그린 슛입니다. 큰 점이 골. 45는 전반 추가시간까지, 90+는 후반 추가시간과 연장입니다.",
            Style = (Style)FindResource("Hint"), TextWrapping = TextWrapping.Wrap,
        });
        FlowContent.Children.Add(new Viewbox { Child = Timeline(taken, allowed), Stretch = Stretch.Uniform, MaxWidth = 720, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 0) });
        AddTimeBuckets(TimeBuckets.Of(taken, allowed));
        AddHabits(Habits.Of(report.Matches, report.Ouid));
    }

    /// <summary>OS-10: forfeits, pauses and the hours and days they play.</summary>
    private void AddHabits(Habits h)
    {
        FlowContent.Children.Add(SectionTitle($"플레이 습관 · 최근 {h.Matches}경기"));
        FlowContent.Children.Add(new TextBlock
        {
            Text = $"몰수패 {h.ForfeitLosses}경기 ({h.ForfeitLossRate * 100:0}%) · 몰수승 {h.ForfeitWins}경기 · 평균 일시정지 {h.AvgPauses:0.0}회 [직접]",
            FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap,
            ToolTip = "몰수패: 이 구단주가 경기를 나가거나 연결이 끊겨 진 경기. 몰수승: 상대가 나간 경기.",
        });
        if (h.Matches == 0) return;
        var (start, count) = h.PeakHours();
        FlowContent.Children.Add(new TextBlock
        {
            Text = $"주로 하는 시간 {start}시~{(start + 3) % 24}시 ({count}경기) · 경기 시각은 한국 시간 [추정]",
            Foreground = Res<Brush>("Muted"), FontSize = 12, Margin = new Thickness(0, 2, 0, 4), TextWrapping = TextWrapping.Wrap,
            ToolTip = "API의 경기 시각에는 시간대 표시가 없어 UTC로 보고 9시간을 더했습니다.",
        });

        const double barMax = 50;
        var most = Math.Max(1, h.ByHour.Max());
        var hours = new UniformGrid { Columns = 24, Rows = 1, MaxWidth = 640, HorizontalAlignment = HorizontalAlignment.Stretch };
        for (var i = 0; i < 24; i++)
        {
            var cell = new StackPanel { ToolTip = $"{i}시 {h.ByHour[i]}경기" };
            cell.Children.Add(new Border
            {
                Height = barMax, Child = new Rectangle
                {
                    Height = Math.Max(2, h.ByHour[i] / (double)most * barMax), VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(1, 0, 1, 0),
                    Fill = Res<Brush>("Info"), RadiusX = 2, RadiusY = 2, Opacity = h.ByHour[i] == 0 ? 0.25 : 0.9,
                },
            });
            cell.Children.Add(new TextBlock { Text = i % 3 == 0 ? $"{i}" : "", FontSize = 10, Foreground = Res<Brush>("Muted"), HorizontalAlignment = HorizontalAlignment.Center });
            hours.Children.Add(cell);
        }
        FlowContent.Children.Add(hours);

        var days = new UniformGrid { Columns = 7, Rows = 1, MaxWidth = 640, HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 6, 0, 0) };
        var busiest = Math.Max(1, h.ByDay.Max());
        for (var i = 0; i < 7; i++)
        {
            var box = new StackPanel();
            box.Children.Add(new TextBlock { Text = Habits.DayLabels[i], FontSize = 11, Foreground = Res<Brush>("Muted"), HorizontalAlignment = HorizontalAlignment.Center });
            box.Children.Add(new TextBlock { Text = $"{h.ByDay[i]}", FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center });
            var fill = new SolidColorBrush(((SolidColorBrush)Res<Brush>("Info")).Color) { Opacity = 0.08 + 0.4 * h.ByDay[i] / busiest };
            days.Children.Add(new Border { Background = fill, CornerRadius = new CornerRadius(6), Padding = new Thickness(4), Margin = new Thickness(0, 0, 4, 0), Child = box });
        }
        FlowContent.Children.Add(days);
    }

    /// <summary>OS-09: goals for and against per quarter-hour, and the busiest ones named.</summary>
    private void AddTimeBuckets(IReadOnlyList<TimeBucket> buckets)
    {
        FlowContent.Children.Add(SectionTitle("시간대별 득점 · 실점 [직접]"));
        var bestFor = buckets.MaxBy(b => b.For)!;
        var worstAgainst = buckets.MaxBy(b => b.Against)!;
        FlowContent.Children.Add(new TextBlock
        {
            Text = (bestFor.For > 0 ? $"가장 많이 넣는 때 {bestFor.Label}분 ({bestFor.For}골)" : "득점 없음")
                + (worstAgainst.Against > 0 ? $" · 가장 많이 먹히는 때 {worstAgainst.Label}분 ({worstAgainst.Against}골)" : ""),
            FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap,
        });

        const double barMax = 90;
        var top = Math.Max(1, buckets.Max(b => Math.Max(b.For, b.Against)));
        var grid = new UniformGrid { Columns = buckets.Count, Rows = 1, MaxWidth = 640, HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 6, 0, 0) };
        foreach (var b in buckets)
        {
            var bars = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Height = barMax + 16 };
            foreach (var (n, brush, word) in new[] { (b.For, Res<Brush>("Accent"), "득점"), (b.Against, Res<Brush>("Danger"), "실점") })
            {
                var column = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(2, 0, 2, 0), ToolTip = $"{b.Label}분 {word} {n}" };
                column.Children.Add(new TextBlock { Text = $"{n}", FontSize = 11, Foreground = Res<Brush>("Muted"), HorizontalAlignment = HorizontalAlignment.Center });
                column.Children.Add(new Rectangle { Width = 16, Height = Math.Max(2, n / (double)top * barMax), Fill = brush, RadiusX = 3, RadiusY = 3, Opacity = n == 0 ? 0.3 : 0.9 });
                bars.Children.Add(column);
            }
            var cell = new StackPanel();
            cell.Children.Add(bars);
            cell.Children.Add(new TextBlock { Text = b.Label, FontSize = 11, Foreground = Res<Brush>("Muted"), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 0) });
            grid.Children.Add(cell);
        }
        FlowContent.Children.Add(grid);
        var legend = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        foreach (var (label, brush) in new[] { ("득점", Res<Brush>("Accent")), ("실점", Res<Brush>("Danger")) })
        {
            legend.Children.Add(new Rectangle { Width = 10, Height = 10, Fill = brush, RadiusX = 2, RadiusY = 2, Margin = new Thickness(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center });
            legend.Children.Add(new TextBlock { Text = label, FontSize = 12, Foreground = Res<Brush>("Muted"), Margin = new Thickness(0, 0, 12, 0) });
        }
        legend.Children.Add(new TextBlock { Text = "30-45에는 전반 추가시간, 90+에는 후반 추가시간과 연장이 들어갑니다.", FontSize = 12, Foreground = Res<Brush>("Muted") });
        FlowContent.Children.Add(legend);
    }

    private UniformGrid StatTiles(StatLine l)
    {
        var tiles = new UniformGrid { Columns = 6, Margin = new Thickness(0, 0, 0, 4) };
        foreach (var (label, value) in new[]
                 {
                     ("평균 평점", $"{l.Rating:0.00}"), ("득점", $"{l.Goals:0.00}"), ("도움", $"{l.Assists:0.00}"),
                     ("점유율", $"{l.Possession:0}%"), ("슈팅 정확도", $"{l.ShotAccuracy * 100:0}%"), ("패스 정확도", $"{l.PassAccuracy * 100:0}%"),
                 })
        {
            var box = new StackPanel();
            box.Children.Add(new TextBlock { Text = label, Foreground = Res<Brush>("Muted"), FontSize = 11 });
            box.Children.Add(new TextBlock { Text = value, FontSize = 17, FontWeight = FontWeights.SemiBold });
            tiles.Children.Add(new Border { Style = (Style)FindResource("CardPanel"), Padding = new Thickness(8, 6, 8, 6), Margin = new Thickness(0, 0, 6, 0), Child = box });
        }
        return tiles;
    }

    /// <summary>Two lanes on a 0–90(+) axis: the shots they took on top, the shots they allowed below.</summary>
    private Canvas Timeline(ShotMap taken, ShotMap allowed)
    {
        const double w = 700, left = 70, lane = 46, top = 6;
        var axis = w - left - 10;
        double X(double minute) => left + minute / (MatchClock.Late + 2) * axis;
        var canvas = new Canvas { Width = w, Height = top + lane * 2 + 26 };
        var line = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));

        // Lanes, quarter-hour ticks and the half-time line.
        for (var i = 0; i < 2; i++)
        {
            var band = new Rectangle { Width = axis, Height = lane - 6, Fill = Res<Brush>("Panel"), RadiusX = 6, RadiusY = 6 };
            Canvas.SetLeft(band, left);
            Canvas.SetTop(band, top + i * lane);
            canvas.Children.Add(band);
            var name = new TextBlock { Text = i == 0 ? "찬 슛" : "허용한 슛", Foreground = Res<Brush>("Muted"), FontSize = 12 };
            Canvas.SetLeft(name, 0);
            Canvas.SetTop(name, top + i * lane + 12);
            canvas.Children.Add(name);
        }
        foreach (var m in new[] { 0, 15, 30, 45, 60, 75, 90 })
        {
            var tick = new Rectangle { Width = m == 45 ? 2 : 1, Height = lane * 2 - 6, Fill = m == 45 ? line : new SolidColorBrush(Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF)) };
            Canvas.SetLeft(tick, X(m));
            Canvas.SetTop(tick, top);
            canvas.Children.Add(tick);
            var label = new TextBlock { Text = m == 90 ? "90" : $"{m}", Foreground = Res<Brush>("Muted"), FontSize = 11 };
            Canvas.SetLeft(label, X(m) - 6);
            Canvas.SetTop(label, top + lane * 2 + 2);
            canvas.Children.Add(label);
        }
        var late = new TextBlock { Text = "90+", Foreground = Res<Brush>("Muted"), FontSize = 11 };
        Canvas.SetLeft(late, X(MatchClock.Late) - 9);
        Canvas.SetTop(late, top + lane * 2 + 2);
        canvas.Children.Add(late);

        void Dots(ShotMap map, int laneIndex)
        {
            var i = 0;
            foreach (var d in map.Dots.Where(d => d.Clock is not null).OrderBy(d => d.Result == ShotResult.Goal))
            {
                var goal = d.Result == ShotResult.Goal;
                var size = goal ? 8.0 : 5.0;
                // Spread the dots over the lane height in a fixed pattern so overlapping minutes stay readable.
                var jitter = (i++ * 7919 % 29) / 28.0;
                var dot = new Ellipse
                {
                    Width = size, Height = size, Fill = goal ? Res<Brush>("Accent") : Res<Brush>("Muted"), Opacity = goal ? 0.95 : 0.55,
                    ToolTip = $"{d.Minute}분 · {(goal ? "골" : d.Result == ShotResult.OnTarget ? "유효" : "빗나감")}",
                };
                Canvas.SetLeft(dot, X(d.Clock!.Value) - size / 2);
                Canvas.SetTop(dot, top + laneIndex * lane + 4 + jitter * (lane - 14) - size / 2 + 3);
                canvas.Children.Add(dot);
            }
        }
        Dots(taken, 0);
        Dots(allowed, 1);
        return canvas;
    }
}
