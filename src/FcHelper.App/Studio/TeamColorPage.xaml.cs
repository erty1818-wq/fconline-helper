using System.Windows;
using System.Windows.Controls;
using FcHelper.Market;

namespace FcHelper.App.Studio;

public partial class TeamColorPage : UserControl
{
    private IReadOnlyList<TeamColorItem> _popular = [];
    private IReadOnlyList<TeamColor> _all = [];

    public TeamColorPage()
    {
        InitializeComponent();
        Loaded += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        if (StudioKit.Squads is not { } squads || _all.Count > 0) return;
        await StudioKit.Run(null, Status, async () =>
        {
            Status.Text = "팀컬러 목록을 불러오는 중…";
            _all = await squads.TeamColorsAsync();
            _popular = (await squads.PopularTeamColorsAsync()).Select(p => new TeamColorItem($"{p.Color.Name} · 랭커 {p.Usage.Users}명 ({p.Usage.Share:P1})", p.Color.Id)).ToList();
            List.ItemsSource = _popular;
            Status.Text = $"전체 {_all.Count}개 · 인기 {_popular.Count}개";
        });
    }

    private void OnSearch(object sender, TextChangedEventArgs e)
    {
        var text = SearchBox.Text.Trim();
        List.ItemsSource = text.Length == 0 ? _popular
            : _all.Where(t => t.Name.Contains(text, StringComparison.OrdinalIgnoreCase)).Take(200)
                .Select(t => new TeamColorItem($"{t.Name} · {Kind(t)} · 최대 {t.MaxMembers}명", t.Id)).ToList();
    }

    private static string Kind(TeamColor t) => t.Kind switch
    {
        TeamColorKind.Club => "소속 · 클럽",
        TeamColorKind.Nation => "소속 · 국가",
        TeamColorKind.Feature => "특성",
        TeamColorKind.Enhance => "강화",
        _ => "소속 · 기타",
    };

    private async void OnPick(object sender, SelectionChangedEventArgs e)
    {
        if (List.SelectedItem is not TeamColorItem item || StudioKit.Squads is not { } squads) return;
        Detail.Children.Clear();
        Detail.Children.Add(new TextBlock { Text = "불러오는 중… (처음에는 멤버를 받느라 10~30초 걸립니다)", Style = (Style)FindResource("Hint") });
        await StudioKit.Run(null, Status, async () =>
        {
            if (await squads.TeamColorAsync(item.Id) is not { } tc) return;
            Detail.Children.Clear();
            Detail.Children.Add(new TextBlock { Text = tc.Color.Name, Style = (Style)FindResource("H1") });
            Detail.Children.Add(new TextBlock { Text = $"{Kind(tc.Color)} · 적용 카드 {tc.Members.Count:#,0}장 (OVR 105 이상)", Style = (Style)FindResource("Hint"), Margin = new Thickness(0, 0, 0, 10) });
            foreach (var l in tc.Color.Levels)
            {
                Detail.Children.Add(new TextBlock { Text = $"{l.Level}단계 · {l.Members}명", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 0) });
                Detail.Children.Add(new TextBlock { Text = string.Join(" · ", l.Effects), Foreground = (System.Windows.Media.Brush)FindResource("Accent") });
            }
            var use = new Button { Content = "이 팀컬러로 스쿼드 짜기", Style = (Style)FindResource("Primary"), Margin = new Thickness(0, 16, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
            use.Click += (_, _) => (Window.GetWindow(this) as StudioWindow)?.Navigate("squad", p => ((SquadPage)p).UseTeamColor(tc.Color.Id, tc.Color.Name, tc.Color.Category));
            Detail.Children.Add(use);
            Detail.Children.Add(new TextBlock
            {
                Text = tc.Color.Category switch
                {
                    TeamColorCategory.Affiliation => "소속 팀컬러: 단계를 채우면 선발 전원이 보너스를 받습니다.",
                    TeamColorCategory.Feature => "특성 팀컬러: 해당 카드만 보너스를 받습니다. 소속 팀컬러와 함께 적용됩니다 (예: 프랑스 + 2026 프랑스). 개별 능력치 보너스의 OVR 환산은 [추정].",
                    _ => "강화 팀컬러: 선발 전원 적용으로 계산합니다 [추정: 공식 설명에 적용 대상이 없음].",
                },
                TextWrapping = TextWrapping.Wrap, Style = (Style)FindResource("Hint"), Margin = new Thickness(0, 8, 0, 0),
            });
        });
    }
}
