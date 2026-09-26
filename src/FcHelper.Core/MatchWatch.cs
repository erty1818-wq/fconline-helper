namespace FcHelper.Core;

/// <summary>What the auto watcher should do after one look at the game window.</summary>
public enum WatchEvent { None, NewOpponent, MatchEnded }

/// <summary>
/// The state behind 완전 자동 (AM-02), fed with what each look at the game window found. Pure, so the rules are tested:
/// the scoreboard clock means a match is on; the match is over when the clock was last seen at
/// <see cref="EndMinute"/> or later and the scoreboard then stays away for <see cref="EndMissingLooks"/> looks (a replay
/// hides it only for a moment), or stays away for <see cref="QuitMissingLooks"/> looks at any time (someone left).
/// A new matchmaking screen or a clock that jumps back starts a new match.
/// </summary>
public sealed class MatchWatch
{
    public const int EndMinute = 89;
    public const int EndMissingLooks = 6;   // 12 s at one look every 2 s
    public const int QuitMissingLooks = 30; // 60 s

    public string? Opponent { get; private set; }
    public int? LastClock { get; private set; }
    public bool InGame { get; private set; }
    private int _missing;

    /// <summary>The opponent for the current match is still unknown (look for it on the scoreboard).</summary>
    public bool NeedsOpponent => Opponent is null;

    /// <summary>One look at the scoreboard: its clock in minutes, or null when it is not on screen.</summary>
    public WatchEvent Scoreboard(int? clock)
    {
        if (clock is { } c)
        {
            // The clock only runs forward within a match; a big step back is the next match.
            if (LastClock is { } last && c < last - 10) Opponent = null;
            LastClock = c;
            InGame = true;
            _missing = 0;
            return WatchEvent.None;
        }
        if (!InGame) return WatchEvent.None;
        _missing++;
        var ended = (LastClock >= EndMinute && _missing >= EndMissingLooks) || _missing >= QuitMissingLooks;
        if (!ended) return WatchEvent.None;
        InGame = false;
        return WatchEvent.MatchEnded;
    }

    /// <summary>The opponent was read (matchmaking screen or scoreboard); returns NewOpponent when it is a new one.</summary>
    public WatchEvent Found(string nickname, bool fromMatchScreen)
    {
        if (string.Equals(nickname, Opponent, StringComparison.OrdinalIgnoreCase)) return WatchEvent.None;
        Opponent = nickname;
        if (fromMatchScreen) { InGame = false; LastClock = null; _missing = 0; }
        return WatchEvent.NewOpponent;
    }

    /// <summary>After the end card: forget the match so the next one starts clean.</summary>
    public void Reset()
    {
        Opponent = null;
        LastClock = null;
        InGame = false;
        _missing = 0;
    }
}
