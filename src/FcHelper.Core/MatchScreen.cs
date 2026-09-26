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

    // Labels that share the screen with the nicknames (the user saw 연장전, 준비, 감독 and tier names picked up).
    private static readonly string[] UiWords =
        ["팀정보", "유니폼", "선택", "감독", "공식경기", "준비완료", "경기가시작", "연장전", "승부차기", "킥오프", "포메이션"];

    // Short labels dropped only when they are the whole line, so "준비된자" or "전반전킹" stay possible nicknames.
    private static readonly string[] WholeWords = ["준비", "준비중", "전반", "후반", "전술", "매칭", "채팅"];

    // A division label on its own ("챌린저 2부", "월드클래스1", "슈퍼챔피언스"); matched whole so a nickname that merely
    // contains such a word ("프로류춘") still counts.
    private static readonly System.Text.RegularExpressions.Regex TierLabel = new(
        @"^(슈퍼)?(챔피언스|챌린지|챌린저|월드클래스|세미프로|프로|유망주|엘리트|아마추어)\d*(부)?(감독)?$");

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

        var managerNames = UnderManagerLabel(lines);
        return ordered
            .Where(l => !IsUiText(l.Text) && !managerNames.Contains(l))
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
        return c.Length < 2 || c is "VS" or "vs" || c.EndsWith('점') || UiWords.Any(c.Contains) || WholeWords.Contains(c) || c.All(char.IsDigit) || TierLabel.IsMatch(c) || LooksLikeTier(c);
    }

    private static readonly string[] TierWords = ["챔피언스", "챌린지", "챌린저", "월드클래스", "세미프로", "유망주", "엘리트", "아마추어"];

    /// <summary>
    /// A division label misread by one letter ("철린저 2부" for "챌린저 2부" really came through as a search). Only whole
    /// labels of three letters or more, so real nicknames are rarely caught.
    /// </summary>
    private static bool LooksLikeTier(string compact)
    {
        var core = System.Text.RegularExpressions.Regex.Replace(compact, @"(\d*부?(감독)?)$", "");
        if (core.StartsWith("슈퍼")) core = core[2..];
        return core.Length >= 3 && TierWords.Any(t => EditDistance(core, t) <= 1);
    }

    /// <summary>
    /// The manager card shows a "감독" label with the manager's name (a real football coach, e.g. "셰틸 크누첸") right
    /// under it; those names are not nicknames.
    /// </summary>
    private static HashSet<OcrLine> UnderManagerLabel(IReadOnlyList<OcrLine> lines)
    {
        var names = new HashSet<OcrLine>(ReferenceEqualityComparer.Instance);
        foreach (var label in lines.Where(l => Compact(l.Text) == "감독"))
            foreach (var l in lines)
                if (!ReferenceEquals(l, label)
                    && l.CenterY > label.CenterY && l.CenterY - label.CenterY <= 3 * Math.Max(label.Height, l.Height)
                    && l.X < label.X + label.Width + label.Width && l.X + l.Width > label.X - label.Width)
                    names.Add(l);
        return names;
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
