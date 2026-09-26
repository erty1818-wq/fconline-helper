using FcHelper.Services;

namespace FcHelper.App;

/// <summary>A row of the 선수 tab; the numbers are pre-rounded strings so the grid shows them as the card does.</summary>
public sealed record PlayerRow(long SpId, string? Season, string Name, string Position, int Apps, int Goals, int Assists, string Rating, string Attack, string Defence);

/// <summary>선수 tab (OS-17): every player they used with attack and defence indices [계산].</summary>
public partial class SearchWindow
{
    private string? _playersFor;

    private void ShowPlayers(OpponentReport report)
    {
        var key = $"{report.Ouid}|{report.Matches.Count}|{report.Matches.FirstOrDefault()?.MatchId}";
        if (_playersFor == key) return;
        _playersFor = key;

        var lines = PlayerIndex.Of(report.Matches, report.Ouid);
        var names = _app.Db?.GetPlayerNames(lines.Select(l => l.SpId)) ?? [];
        PlayerGrid.ItemsSource = lines.Select(l => new PlayerRow(
            l.SpId, _app.Squads?.Card(l.SpId)?.Season, names.GetValueOrDefault(l.SpId) ?? $"#{l.SpId}", l.Position,
            l.Apps, l.Goals, l.Assists, $"{l.Rating:0.00}", $"{l.Attack:0.0}", $"{l.Defence:0.0}")).ToList();
        PlayersInfo.Text = lines.Count == 0
            ? "선수 기록이 있는 경기가 없습니다."
            : $"최근 {report.Matches.Count}경기에 뛴 {lines.Count}명. 열 제목을 누르면 정렬됩니다. "
              + $"[계산] 경기당 공격 지수 = 골×{PlayerIndex.Goal} + 도움×{PlayerIndex.Assist} + 유효슛×{PlayerIndex.OnTarget} + 드리블 성공×{PlayerIndex.Dribble}, "
              + $"수비 지수 = 태클 성공 + 가로채기 + 블록 + 공중볼 성공×{PlayerIndex.Aerial}.";
    }
}
