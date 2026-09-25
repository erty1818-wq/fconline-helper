using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using FcHelper.Market;
using FcHelper.Services;

namespace FcHelper.App.Studio;

public sealed record UpgradeRow(string Title, string Detail, UpgradePlan Plan);

/// <summary>The user's current eleven and the one or two swaps that help most, keeping their team colour.</summary>
public partial class MySquadPage : UserControl
{
    private IReadOnlyList<OwnedCard> _owned = [];
    private IReadOnlyList<SquadSlot> _current = [];

    public MySquadPage()
    {
        InitializeComponent();
        TeamColorBox.ItemsSource = new[] { new TeamColorItem("없음 (팀컬러 무시)", 0) };
        TeamColorBox.SelectedIndex = 0;
        Loaded += async (_, _) => { if (_owned.Count == 0) await LoadAsync(); };
    }

    private async void OnLoad(object sender, RoutedEventArgs e) => await LoadAsync();

    private async Task LoadAsync()
    {
        var app = StudioKit.App;
        if (StudioKit.Squads is not { } squads || app.Db is not { } db) return;
        var nick = app.Settings.MyNickname;
        if (string.IsNullOrWhiteSpace(nick)) { Status.Text = "설정에서 내 닉네임을 입력하세요."; return; }
        await StudioKit.Run(LoadButton, Status, async () =>
        {
            Status.Text = "내 최근 경기를 불러오는 중…";
            if (app.Service is { } service) await service.SyncMyMatchesAsync(20);
            if (db.FindUserByNickname(nick) is not { } me) { Status.Text = "내 경기를 찾지 못했습니다. API 키와 닉네임을 확인하세요."; return; }
            _owned = SquadContext.MyCurrentCards(db, me.Ouid);
            _current = squads.CurrentSquad(_owned);
            if (_current.Count == 0) { Status.Text = "최근 공식경기 기록이 없습니다."; return; }
            Pitch.Highlighted = new HashSet<int>();
            Pitch.Show(_current);
            Who.Text = $"{nick} · 최근 공식경기 선발 {_current.Count}명 · 시세 합 {Bp.Format(_current.Sum(s => s.Price))} · 평균 OVR {_current.Average(s => s.Ovr):0.0}"
                + (_current.Count < _owned.Count ? $" ({_owned.Count - _current.Count}명은 시세 데이터에 없음)" : "");
            Status.Text = "팀컬러 확인 중… (처음에는 30초쯤 걸립니다)";
            var detected = await squads.DetectTeamColorsAsync(_owned);
            var items = new List<TeamColorItem> { new("없음 (팀컬러 무시)", 0) };
            items.AddRange(detected.Select(d => new TeamColorItem($"{d.Color.Name} · {d.Owned}명 {d.Level.Level}단계 (+{d.Level.AllStats})", d.Color.Id)));
            TeamColorBox.ItemsSource = items;
            TeamColorBox.SelectedIndex = items.Count > 1 ? 1 : 0;
            Status.Text = detected.Count > 0 ? $"팀컬러 감지: {string.Join(", ", detected.Select(d => $"{d.Color.Name} {d.Owned}명"))}. 유지할 팀컬러를 고르고 [교체 추천]을 누르세요." : "적용 중인 팀컬러가 없습니다.";
        });
    }

    private async void OnFind(object sender, RoutedEventArgs e)
    {
        if (StudioKit.Squads is not { } squads) return;
        if (_owned.Count == 0) { Status.Text = "먼저 내 스쿼드를 불러오세요."; return; }
        if (!StudioKit.TryPrice(BudgetBox, 500_000_000, out var budget)) { Status.Text = "예산은 10억처럼 입력하세요."; return; }
        var fee = double.TryParse(FeeBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? Math.Clamp(f, 0, 100) / 100 : 0;
        var tc = (TeamColorBox.SelectedItem as TeamColorItem)?.Id ?? 0;
        await StudioKit.Run(FindButton, Status, async () =>
        {
            Status.Text = "계산 중…";
            var grades = _owned.Select(o => o.Grade).Distinct().Order().ToList();
            var plans = await squads.UpgradesAsync(_owned, budget, grades, fee, 2, tc);
            Plans.ItemsSource = plans.Select(p => new UpgradeRow(
                string.Join("  +  ", p.Moves.Select(m => $"{m.Out.Card.Name} → {m.In.Name} {m.In.Season} +{m.Grade}")),
                $"환산 +{p.TotalGain:0.0} · 순비용 {Bp.Format(Math.Max(0, p.NetCost))} · " + string.Join(" · ", p.Moves.Select(m => $"{m.In.Name} OVR {m.Ovr} 구매 {Bp.Format(m.BuyPrice)}")),
                p)).ToList();
            Status.Text = plans.Count == 0 ? "예산 안에서 좋아지는 교체가 없습니다." : "교체안을 누르면 바뀌는 자리가 피치에 표시됩니다.";
        });
    }

    private void OnPlanSelected(object sender, SelectionChangedEventArgs e)
    {
        if (Plans.SelectedItem is not UpgradeRow row) return;
        Pitch.Highlighted = row.Plan.Moves.Select(m => m.Out.Index).ToHashSet();
        Pitch.Show(_current);
    }
}
