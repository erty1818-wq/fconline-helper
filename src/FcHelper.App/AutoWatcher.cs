using System.Net.Http;
using System.Windows.Threading;
using FcHelper.Core;
using FcHelper.NexonApi;
using FcHelper.Vision;

namespace FcHelper.App;

/// <summary>
/// 완전 자동 (AM-02): while the FC Online window is in front, a look every two seconds. Mostly just the scoreboard crop
/// (the clock says a match is on), and the whole window only when no match is on, to catch the matchmaking screen.
/// Opponent found → 매칭 카드; match over → 종료 카드. Nothing runs while the game is in the background, minimised or
/// closed, and nothing touches the game itself: only screen pixels (AGENTS.md rule 1).
/// </summary>
public sealed class AutoWatcher : IDisposable
{
    private static readonly TimeSpan Every = TimeSpan.FromSeconds(2);
    /// <summary>After a failed lookup (misread, API down) the same match is tried again only after this.</summary>
    private static readonly TimeSpan RetryAfter = TimeSpan.FromSeconds(20);
    /// <summary>Once the matchmaking screen gave the opponent, it is not read again for this long.</summary>
    private static readonly TimeSpan MatchScreenRest = TimeSpan.FromSeconds(45);

    private readonly App _app;
    private readonly DispatcherTimer _timer;
    private readonly MatchWatch _watch = new();
    private ScreenReader? _reader;
    private GameWindow? _game;
    private bool _busy;
    private int _searches, _looks;
    private DateTime _retryAt, _matchScreenRestUntil;

    public AutoWatcher(App app)
    {
        _app = app;
        _timer = new DispatcherTimer { Interval = Every };
        _timer.Tick += async (_, _) => await LookAsync();
        _timer.Start();
    }

    private async Task LookAsync()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            // Finding the process costs more than a look, so do it only now and then until the game is running.
            if (_game is null && _searches++ % 5 != 0) return;
            _game ??= GameWindow.Find();
            if (_game is null) return;
            if (_game.ClientBounds is not { } area) { _game = null; return; }
            if (!_game.IsForeground || _game.IsMinimized) return;
            if ((_reader ??= ScreenReader.Create()) is not { } reader || _app.Service is not { } service) return;

            using var frame = ScreenReader.Capture(area);
            var board = await reader.ReadRegionAsync(frame, InGameScreen.ScoreboardLarge);
            var clock = InGameScreen.ClockMinutes(board);
            if (_watch.Scoreboard(clock) == WatchEvent.MatchEnded)
            {
                if (_watch.Opponent is { } opponent) _app.ShowEndCard(opponent, _watch.LastClock, area);
                _watch.Reset();
                return;
            }

            var now = DateTime.UtcNow;
            if (clock is not null)
            {
                if (!_watch.NeedsOpponent || now < _retryAt) return;
                var passes = new List<IReadOnlyList<OcrLine>> { await reader.ReadRegionAsync(frame, InGameScreen.Scoreboard), board };
                passes.Add(await reader.ReadRegionAsync(frame, InGameScreen.BottomNames));
                await ResolveAsync(service, InGameScreen.OpponentCandidates(passes, _app.Settings.MyNickname), fromMatchScreen: false, area);
            }
            else if (_looks++ % 2 == 0 && now >= _matchScreenRestUntil && now >= _retryAt)
            {
                var lines = await reader.ReadAsync(frame);
                if (!MatchScreen.LooksLikeMatchScreen(lines)) return;
                if (await ResolveAsync(service, MatchScreen.OpponentCandidates(lines, _app.Settings.MyNickname), fromMatchScreen: true, area))
                    _matchScreenRestUntil = now + MatchScreenRest;
            }
        }
        catch (Exception e) when (e is NexonApiException or HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            // A failed look is simply tried again later; the watcher must never bother the player.
            _retryAt = DateTime.UtcNow + RetryAfter;
        }
        finally
        {
            _busy = false;
        }
    }

    /// <returns>True when a real nickname was found among the candidates.</returns>
    private async Task<bool> ResolveAsync(Services.FcHelperService service, IReadOnlyList<string> candidates, bool fromMatchScreen, System.Drawing.Rectangle area)
    {
        var nickname = candidates.Count == 0 ? null : await service.FindExistingNicknameAsync(candidates);
        if (nickname is null)
        {
            _retryAt = DateTime.UtcNow + RetryAfter;
            return false;
        }
        if (_watch.Found(nickname, fromMatchScreen) == WatchEvent.NewOpponent) _app.ShowMatchCard(nickname, area);
        return true;
    }

    public void Dispose() => _timer.Stop();
}
