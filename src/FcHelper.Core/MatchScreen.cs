namespace FcHelper.Core;

/// <summary>One line of OCR text, positioned as fractions (0..1) of the captured game image.</summary>
public sealed record OcrLine(string Text, double X, double Y, double Width, double Height)
{
    public double CenterX => X + Width / 2;
    public double CenterY => Y + Height / 2;
}

/// <summary>
/// Finds the opponent's nickname on the matchmaking screen ("팀 정보" tab: both nicknames on one row, mine on the
/// left, the opponent's on the right). Works on relative positions, so it does not depend on the game resolution.
/// Returns candidates rather than one answer: the API's id lookup decides which of them is a real user.
/// </summary>
public static class MatchScreen
{
    public const int MaxCandidates = 4;

    // Labels that share the screen with the nicknames.
    private static readonly string[] UiWords = ["팀정보", "유니폼", "선택", "감독", "공식경기", "준비완료", "경기가시작"];

    /// <summary>True when the OCR text looks like the matchmaking screen.</summary>
    public static bool LooksLikeMatchScreen(IReadOnlyList<OcrLine> lines) =>
        lines.Any(l => Compact(l.Text) is "VS" or "vs") && lines.Any(l => Compact(l.Text).Contains("팀정보"));

    public static IReadOnlyList<string> OpponentCandidates(IReadOnlyList<OcrLine> lines, string? myNickname)
    {
        var me = string.IsNullOrWhiteSpace(myNickname) ? null : FindLine(lines, myNickname);
        IEnumerable<OcrLine> ordered;
        if (me is not null)
        {
            // Same row as my nickname, to its right; closest to the mirror image of my position first.
            var mirror = 1 - me.CenterX;
            ordered = lines
                .Where(l => !ReferenceEquals(l, me)
                    && l.CenterX > me.CenterX + 0.05
                    && Math.Abs(l.CenterY - me.CenterY) <= Math.Max(me.Height, l.Height))
                .OrderBy(l => Math.Abs(l.CenterX - mirror));
        }
        else
        {
            // No anchor: nickname-sized text in the right half, largest first.
            ordered = lines.Where(l => l.CenterX > 0.5).OrderByDescending(l => l.Height);
        }

        return ordered
            .Where(l => !IsUiText(l.Text))
            .SelectMany(l => CandidatesFrom(l.Text))
            .Where(c => myNickname is null || !string.Equals(c, Compact(myNickname), StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxCandidates)
            .ToList();
    }

    /// <summary>
    /// Nicknames have no spaces, but OCR may split one into words or read the division badge after it as a
    /// character ("류춘 3"). Order: without such a trailing badge, the whole line, then the longest words.
    /// </summary>
    public static IEnumerable<string> CandidatesFrom(string text)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length > 1 && words[^1].Length == 1)
        {
            var trimmed = string.Concat(words[..^1]);
            if (trimmed.Length >= 2) yield return trimmed;
        }
        var joined = string.Concat(words);
        if (joined.Length >= 2) yield return joined;
        if (words.Length < 2) yield break;
        foreach (var w in words.Where(w => w.Length >= 2).OrderByDescending(w => w.Length)) yield return w;
    }

    private static OcrLine? FindLine(IReadOnlyList<OcrLine> lines, string nickname)
    {
        var target = Compact(nickname);
        return lines.FirstOrDefault(l => Compact(l.Text).Contains(target, StringComparison.OrdinalIgnoreCase))
            ?? lines.FirstOrDefault(l => EditDistance(Compact(l.Text), target) <= Math.Max(1, target.Length / 4));
    }

    private static bool IsUiText(string text)
    {
        var c = Compact(text);
        return c.Length < 2 || c is "VS" or "vs" || c.EndsWith('점') || UiWords.Any(c.Contains) || c.All(char.IsDigit);
    }

    private static string Compact(string s) => string.Concat(s.Where(ch => !char.IsWhiteSpace(ch)));

    private static int EditDistance(string a, string b)
    {
        var d = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) d[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            var prev = d[0];
            d[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var tmp = d[j];
                d[j] = Math.Min(Math.Min(d[j] + 1, d[j - 1] + 1), prev + (char.ToLowerInvariant(a[i - 1]) == char.ToLowerInvariant(b[j - 1]) ? 0 : 1));
                prev = tmp;
            }
        }
        return d[b.Length];
    }
}
