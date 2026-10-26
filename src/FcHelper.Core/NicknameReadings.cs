namespace FcHelper.Core;

public sealed record NicknameGuess(string Text, int FocusedReadings, int InitialOrder);

/// <summary>Rank observed readings of the same name; never invent a replacement for a Hangul syllable.</summary>
public static class NicknameReadings
{
    public static IReadOnlyList<NicknameGuess> Rank(IReadOnlyList<string> initial, IEnumerable<IReadOnlyList<OcrLine>> focused,
        string? myNickname = null)
    {
        var votes = initial.Distinct(StringComparer.OrdinalIgnoreCase).Select((s, i) => (s, i))
            .ToDictionary(x => x.s, x => new NicknameGuess(x.s, 0, x.i), StringComparer.OrdinalIgnoreCase);
        foreach (var pass in focused)
        {
            var names = pass.Where(l => !MatchScreen.IsUiText(l.Text)).SelectMany(l => MatchScreen.CandidatesFrom(l.Text))
                .Where(s => s.Length is >= 2 and <= 24 && s.All(c => char.IsLetterOrDigit(c) || c is '_' or '-'))
                .Where(s => !string.Equals(s, myNickname, StringComparison.OrdinalIgnoreCase))
                .Where(s => initial.Any(x => MatchScreen.EditDistance(s, x) <= Math.Max(1, x.Length / 4)))
                .Distinct(StringComparer.OrdinalIgnoreCase);
            foreach (var name in names)
            {
                var old = votes.GetValueOrDefault(name) ?? new NicknameGuess(name, 0, initial.Count);
                votes[name] = old with { FocusedReadings = old.FocusedReadings + 1 };
            }
        }
        return votes.Values.OrderByDescending(g => g.FocusedReadings).ThenBy(g => g.InitialOrder).ToList();
    }

    /// <summary>Two focused reads must agree, with no competing focused reading, before an automatic lookup.</summary>
    public static bool Confident(IReadOnlyList<NicknameGuess> guesses) => guesses.Count > 0 && guesses[0].FocusedReadings >= 2
        && guesses.Skip(1).All(g => g.FocusedReadings == 0);
}
