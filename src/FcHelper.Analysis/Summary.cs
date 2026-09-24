namespace FcHelper.Analysis;

public static class Summary
{
    /// <summary>
    /// One sentence for the card header and the voice briefing, e.g.
    /// "감아차기 득점 · 호날두 의존 위주. 약점: 헤더 실점."
    /// </summary>
    public static string OneLine(UserAnalysis a)
    {
        if (a.Record.Matches == 0) return "분석할 공식경기 기록이 없습니다.";
        var threats = a.Threats.Take(2).Select(t => t.Short).ToList();
        var weakness = a.Weaknesses.FirstOrDefault()?.Short;

        var parts = new List<string>();
        parts.Add(threats.Count > 0 ? $"{string.Join(" · ", threats)} 위주." : "뚜렷한 득점 패턴 없음.");
        if (weakness is not null) parts.Add($"약점: {weakness}.");
        return string.Join(" ", parts);
    }
}
