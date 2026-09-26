using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FcHelper.Core.Models;
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

        CompareContent.Children.Add(SectionTitle("패드 / 키보드 [직접]"));
        CompareContent.Children.Add(SplitTable(Splits.ByController(report.Matches, report.Ouid), "컨트롤러"));
        CompareContent.Children.Add(SectionTitle("포메이션별 성적 [추정]"));
        CompareContent.Children.Add(SplitTable(Splits.ByFormation(report.Matches, report.Ouid), "포메이션"));
        CompareContent.Children.Add(new TextBlock
        {
            Text = "포메이션은 게임이 알려 주지 않아 선발 11명의 포지션으로 가장 가까운 것을 고릅니다.",
            Style = (Style)FindResource("Hint"), TextWrapping = TextWrapping.Wrap,
        });
        var lineups = new StackPanel();
        CompareContent.Children.Add(lineups);
        _ = AddFormationLineupsAsync(report, key, lineups);
        AddTeams(report);
        AddRival(report, key);
    }

    /// <summary>OS-16: another manager next to this one, and their games against each other if the cache has any.</summary>
    private void AddRival(OpponentReport report, string key)
    {
        CompareContent.Children.Add(SectionTitle("라이벌 매치 · 다른 구단주와 비교"));
        var box = new TextBox { Name = "RivalBox", MinWidth = 240, ToolTip = "비교할 구단주 닉네임 (Enter)" };
        var go = new Button { Name = "RivalButton", Content = "비교", Margin = new Thickness(6, 0, 0, 0) };
        var bar = new DockPanel { MaxWidth = 420, HorizontalAlignment = HorizontalAlignment.Left };
        DockPanel.SetDock(go, Dock.Right);
        bar.Children.Add(go);
        bar.Children.Add(box);
        var result = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        CompareContent.Children.Add(bar);
        CompareContent.Children.Add(result);

        async void Run()
        {
            var nick = box.Text.Trim();
            if (nick.Length == 0) return;
            if (_app.Service is not { } service || _app.Db is not { } db)
            {
                SetTab(result, TabHint("먼저 홈 화면에서 NEXON Open API 키를 입력하세요."));
                return;
            }
            go.IsEnabled = false;
            SetTab(result, TabHint($"'{nick}' 불러오는 중… (처음이면 경기마다 API 1회)"));
            try
            {
                var other = await service.LookupAsync(nick);
                if (_compareFor != key) return;
                if (other is null) { SetTab(result, TabHint($"'{nick}' 닉네임을 찾지 못했습니다.")); return; }
                if (other.Ouid == report.Ouid) { SetTab(result, TabHint("같은 구단주입니다.")); return; }

                var rival = Rivalry.Of(db.GetMatches(db.GetHeadToHead(report.Ouid, other.Ouid, service.Options.MatchType).Select(r => r.MatchId)), report.Ouid, other.Ouid);
                var h2h = new TextBlock { FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) };
                h2h.Text = rival.Matches == 0
                    ? $"저장된 경기 중에는 {report.Nickname} 님과 {other.Nickname} 님이 맞붙은 기록이 없습니다."
                    : $"맞대결 [직접] {report.Nickname} 기준 {rival.Matches}전 {rival.Wins}승 {rival.Draws}무 {rival.Losses}패"
                        + $" · 득실 {rival.GoalsFor}:{rival.GoalsAgainst} ({rival.GoalDifference:+0;-0;0}) · 마지막 {rival.Last!.Value.ToLocalTime():M월 d일}";
                SetTab(result, h2h, ProfileTable(
                    $"{report.Nickname} ({Profile.Of(report.Matches, report.Ouid).Matches}경기)", Profile.Of(report.Matches, report.Ouid),
                    $"{other.Nickname} ({Profile.Of(other.Matches, other.Ouid).Matches}경기)", Profile.Of(other.Matches, other.Ouid)));
                result.Children.Add(new TextBlock
                {
                    Text = "맞대결은 이 PC에 저장된 경기에서만 찾습니다. 두 사람 모두 최근 경기를 불러온 범위 안에서입니다.",
                    Style = (Style)FindResource("Hint"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0),
                });
            }
            catch (Exception e) when (e is NexonApi.NexonApiException or System.Net.Http.HttpRequestException or TaskCanceledException)
            {
                if (_compareFor == key) SetTab(result, TabHint("불러오지 못했습니다. 연결 상태나 API 한도를 확인하세요."));
            }
            finally
            {
                go.IsEnabled = true;
            }
        }
        go.Click += (_, _) => Run();
        box.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) { e.Handled = true; Run(); } };
    }

    /// <summary>OS-15: the squads they switched between (7+ same starters = one team), with results and key players.</summary>
    private void AddTeams(OpponentReport report)
    {
        CompareContent.Children.Add(SectionTitle("팀 단위 분석 [계산]"));
        var teams = Teams.Group(report.Matches, report.Ouid);
        if (teams.Count == 0) { CompareContent.Children.Add(TabHint("기록이 있는 경기가 없습니다.")); return; }
        if (teams.Count == 1)
        {
            CompareContent.Children.Add(TabHint($"최근 {teams[0].Matches.Count}경기를 모두 같은 팀으로 뛰었습니다 (선발 {Teams.MinShared}명 이상 같음)."));
            return;
        }
        var label = new Dictionary<MatchInfo, string>(ReferenceEqualityComparer.Instance);
        foreach (var t in teams)
            foreach (var m in t.Matches)
                label[m.SideOf(report.Ouid)!] = $"팀 {t.Number}";
        CompareContent.Children.Add(SplitTable(Splits.By(report.Matches, report.Ouid, s => label.GetValueOrDefault(s)), "팀"));

        var ids = teams.SelectMany(t => t.Matches).SelectMany(m => m.SideOf(report.Ouid)!.Player).Select(p => p.SpId).Distinct().ToList();
        var names = _app.Db?.GetPlayerNames(ids) ?? [];
        foreach (var t in teams.Take(4))
        {
            var top = PlayerLeaders.Of(t.Matches, report.Ouid).All
                .OrderByDescending(l => (double)(l.Goals + l.Assists) / l.Apps).ThenByDescending(l => l.AvgRating).Take(3)
                .Select(l => $"{names.GetValueOrDefault(l.SpId) ?? $"#{l.SpId}"} {(double)(l.Goals + l.Assists) / l.Apps:0.0}(골+도움/경기) · 평점 {l.AvgRating:0.0}");
            var first = t.Matches[^1].MatchDate.ToLocalTime();
            var last = t.Matches[0].MatchDate.ToLocalTime();
            CompareContent.Children.Add(new TextBlock
            {
                Text = $"팀 {t.Number} ({first:M/d}~{last:M/d}): {string.Join(" · ", top)}",
                FontSize = 12, Foreground = Res<Brush>("Muted"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 1),
            });
        }
        CompareContent.Children.Add(new TextBlock
        {
            Text = $"최근 경기부터 보며 선발 {Teams.MinShared}명 이상이 같으면 같은 팀으로 묶습니다.",
            Style = (Style)FindResource("Hint"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0),
        });
    }

    /// <summary>OS-14: for the formations used at least twice (up to three), the eleven that stands for each on a small pitch.</summary>
    private async Task AddFormationLineupsAsync(OpponentReport report, string key, StackPanel host)
    {
        var picks = Splits.ByFormation(report.Matches, report.Ouid)
            .Where(l => l.Key != Splits.Unknown && l.Matches >= 2).Take(3)
            .Select(l => (Line: l, Pick: Splits.Representative(report.Matches, report.Ouid, l.Key)))
            .Where(x => x.Pick is not null)
            .ToList();
        if (picks.Count == 0) return;
        host.Children.Add(SectionTitle("포메이션별 대표 라인업"));
        if (_app.Squads is not { } squads)
        {
            host.Children.Add(TabHint("시세 데이터가 아직 준비되지 않아 카드를 그릴 수 없습니다."));
            return;
        }
        var status = TabHint("카드 정보 불러오는 중…");
        host.Children.Add(status);
        var owned = picks.Select(x => SquadContext.StartersOf(x.Pick!.Value.Side)).ToList();
        try
        {
            await squads.LoadOffMarketAsync(owned.SelectMany(o => o).Select(o => o.SpId).Distinct());
        }
        catch (Exception e) when (e is System.Net.Http.HttpRequestException or TaskCanceledException)
        {
            // Cards already known still draw; the rest are left out of the pitch.
        }
        if (_compareFor != key) return;
        host.Children.Remove(status);
        var row = new WrapPanel();
        for (var i = 0; i < picks.Count; i++)
        {
            var (line, pick) = picks[i];
            var box = new StackPanel { Width = 214, Margin = new Thickness(0, 0, 10, 8) };
            box.Children.Add(new TextBlock { Text = $"{line.Key} · {line.Matches}경기 {line.WinRate * 100:0}%", FontWeight = FontWeights.SemiBold });
            box.Children.Add(new TextBlock
            {
                Text = $"{pick!.Value.Matches}경기 중 가장 자주 나온 선발 [계산]", Foreground = Res<Brush>("Muted"), FontSize = 11, Margin = new Thickness(0, 0, 0, 2),
            });
            var pitch = new Studio.PitchView();
            pitch.Show(Theirs(squads.CurrentSquad(owned[i])));
            box.Children.Add(pitch);
            row.Children.Add(box);
        }
        host.Children.Add(row);
    }

    /// <summary>OS-12 / OS-13: one row per group with matches, W-D-L, win rate and goals per match.</summary>
    private Grid SplitTable(IReadOnlyList<SplitLine> lines, string keyTitle)
    {
        var grid = new Grid { Margin = new Thickness(0, 2, 0, 4) };
        foreach (var w in new[] { 1.6, 0.7, 1.3, 1.8, 0.8, 0.8 }) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(w, GridUnitType.Star) });
        void Put(UIElement e, int row, int col)
        {
            while (grid.RowDefinitions.Count <= row) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(e, row);
            Grid.SetColumn(e, col);
            grid.Children.Add(e);
        }
        TextBlock Cell(string text, bool head = false, bool right = true, FontWeight? weight = null) => new()
        {
            Text = text, Margin = new Thickness(0, 2, 6, 2), FontSize = head ? 11 : 13, Foreground = head ? Res<Brush>("Muted") : Res<Brush>("Text"),
            TextAlignment = right ? TextAlignment.Right : TextAlignment.Left, FontWeight = weight ?? FontWeights.Normal, VerticalAlignment = VerticalAlignment.Center,
        };
        string[] heads = [keyTitle, "경기", "승무패", "승률", "득점", "실점"];
        for (var i = 0; i < heads.Length; i++) Put(Cell(heads[i], head: true, right: i != 0 && i != 3), 0, i);
        // The most used group stands out; a group of one or two matches says little.
        var main = lines.FirstOrDefault(l => l.Key != Splits.Unknown);
        for (var r = 0; r < lines.Count; r++)
        {
            var l = lines[r];
            var bold = ReferenceEquals(l, main) ? FontWeights.SemiBold : FontWeights.Normal;
            Put(Cell(l.Key, right: false, weight: bold), r + 1, 0);
            Put(Cell($"{l.Matches}"), r + 1, 1);
            Put(Cell($"{l.Wins}-{l.Draws}-{l.Losses}"), r + 1, 2);
            var rate = new DockPanel { Margin = new Thickness(0, 2, 8, 2) };
            var pct = new TextBlock { Text = $"{l.WinRate * 100:0}%", Width = 38, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(pct, Dock.Right);
            rate.Children.Add(pct);
            rate.Children.Add(new ProgressBar
            {
                Maximum = 1, Value = l.WinRate, Height = 6, VerticalAlignment = VerticalAlignment.Center, BorderThickness = new Thickness(0),
                Background = Res<Brush>("Panel"), Foreground = Res<Brush>(l.WinRate >= 0.5 ? "Accent" : "Warn"), Opacity = l.Matches < 3 ? 0.5 : 1,
            });
            Put(rate, r + 1, 3);
            Put(Cell($"{l.GoalsFor:0.0}"), r + 1, 4);
            Put(Cell($"{l.GoalsAgainst:0.0}"), r + 1, 5);
        }
        if (lines.Count == 0) Put(Cell("경기 없음", right: false), 1, 0);
        return grid;
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
