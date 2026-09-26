using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FcHelper.Services;

namespace FcHelper.App;

/// <summary>
/// 비교 tab: me against the opponent (OS-11); controller split, formations, lineups, teams and the rival match
/// (OS-12 … OS-16) add their sections below.
/// </summary>
public partial class SearchWindow
{
    private const int MyMatchCount = 30;
    private string? _compareFor;

    private void ShowCompare(OpponentReport report)
    {
        var key = $"{report.Ouid}|{report.Matches.Count}|{report.Matches.FirstOrDefault()?.MatchId}";
        if (_compareFor == key) return;
        _compareFor = key;

        SetTab(CompareContent, SectionTitle("나 vs 상대"));
        AddMeVersus(report);
    }

    /// <summary>OS-11: my per-match averages (my synced official matches) next to theirs.</summary>
    private void AddMeVersus(OpponentReport report)
    {
        var nick = _app.Settings.MyNickname;
        var me = _app.Db is { } db && !string.IsNullOrWhiteSpace(nick) ? db.FindUserByNickname(nick) : null;
        if (me is null || _app.Db is not { } store)
        {
            CompareContent.Children.Add(TabHint("설정에서 내 닉네임을 넣고 트레이 메뉴의 [내 경기 동기화]를 하면 내 수치와 나란히 볼 수 있습니다."));
            return;
        }
        if (me.Ouid == report.Ouid)
        {
            CompareContent.Children.Add(TabHint("검색한 구단주가 내 계정입니다."));
            return;
        }
        var mine = Profile.Of(store.GetMatches(store.GetCachedMatchIds(me.Ouid, _app.Service?.Options.MatchType ?? 50, MyMatchCount)), me.Ouid);
        var theirs = Profile.Of(report.Matches, report.Ouid);
        if (mine.Matches == 0)
        {
            CompareContent.Children.Add(TabHint("저장된 내 공식경기가 없습니다. 트레이 메뉴의 [내 경기 동기화]를 해 보세요."));
            return;
        }
        CompareContent.Children.Add(ProfileTable($"나 · {me.Nickname} ({mine.Matches}경기)", mine, $"상대 · {report.Nickname} ({theirs.Matches}경기)", theirs));
        CompareContent.Children.Add(new TextBlock
        {
            Text = "[직접] 경기 기록의 평균입니다. 굵은 글씨가 더 나은 쪽입니다. 내 쪽은 동기화해 둔 내 공식경기 최근 30개입니다.",
            Style = (Style)FindResource("Hint"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0),
        });
    }

    /// <summary>Two profiles metric by metric: a bar and the number for each, the better one in bold.</summary>
    private Grid ProfileTable(string leftTitle, Profile left, string rightTitle, Profile right)
    {
        var grid = new Grid { Margin = new Thickness(0, 2, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        void Put(UIElement e, int row, int col)
        {
            while (grid.RowDefinitions.Count <= row) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(e, row);
            Grid.SetColumn(e, col);
            grid.Children.Add(e);
        }
        Put(new TextBlock { Text = leftTitle, Foreground = Res<Brush>("Info"), FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 8, 4) }, 0, 1);
        Put(new TextBlock { Text = rightTitle, Foreground = Res<Brush>("Warn"), FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 0, 4) }, 0, 2);

        for (var i = 0; i < left.Metrics.Count && i < right.Metrics.Count; i++)
        {
            var (a, b) = (left.Metrics[i], right.Metrics[i]);
            var top = Math.Max(Math.Max(a.Value, b.Value), 1e-9);
            var aBetter = a.HigherIsBetter ? a.Value > b.Value : a.Value < b.Value;
            var bBetter = a.HigherIsBetter ? b.Value > a.Value : b.Value < a.Value;
            Put(new TextBlock { Text = a.Label, Foreground = Res<Brush>("Muted"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 3, 0, 3) }, i + 1, 0);
            Put(Bar(a, top, Res<Brush>("Info"), aBetter, new Thickness(0, 3, 8, 3)), i + 1, 1);
            Put(Bar(b, top, Res<Brush>("Warn"), bBetter, new Thickness(0, 3, 0, 3)), i + 1, 2);
        }
        return grid;
    }

    private DockPanel Bar(ProfileMetric m, double top, Brush brush, bool better, Thickness margin)
    {
        var row = new DockPanel { Margin = margin };
        var value = new TextBlock
        {
            Text = m.Display, Width = 52, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
            FontWeight = better ? FontWeights.Bold : FontWeights.Normal, Foreground = better ? Res<Brush>("Text") : Res<Brush>("Muted"),
        };
        DockPanel.SetDock(value, Dock.Right);
        row.Children.Add(value);
        row.Children.Add(new ProgressBar
        {
            Maximum = top, Value = m.Value, Height = 7, VerticalAlignment = VerticalAlignment.Center, BorderThickness = new Thickness(0),
            Background = Res<Brush>("Panel"), Foreground = brush, Opacity = better ? 1 : 0.6,
        });
        return row;
    }
}
