using System.Text.Json;
using FcHelper.Analysis;
using FcHelper.Core.Models;
using FcHelper.Data;
using FcHelper.NexonApi;

namespace FcHelper.Services;

/// <summary>
/// Orchestrates cache → API → analysis. Cached data is shown first, and only matches missing from the cache
/// are fetched, newest first, with partial results published along the way.
/// </summary>
public sealed class FcHelperService(
    IFcOnlineApi api, FcDatabase db, FcHelperOptions options, TimeProvider? time = null, IPlayerMarketSource? market = null)
{
    private const string MetaPlayersKey = "meta.spid.updated";
    private const string MetaDivisionsKey = "meta.division.updated";

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly SemaphoreSlim _baselineGate = new(1, 1);
    private Baseline? _baseline;
    private DateTime _baselineBuiltAt;
    private int _baselineMatchCount;

    public FcHelperOptions Options { get; } = options;

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    // ── lookup ─────────────────────────────────────────────────────────────

    /// <returns>The report, or null when no user has that nickname.</returns>
    public async Task<OpponentReport?> LookupAsync(string nickname, IProgress<LookupProgress>? progress = null, CancellationToken ct = default)
    {
        nickname = nickname.Trim();
        if (nickname.Length == 0) return null;

        progress?.Report(new LookupProgress(LookupStage.ResolvingUser, null, 0, 0));
        var user = await ResolveUserAsync(nickname, ct);
        if (user is null) return null;

        var window = Options.MatchWindow;
        var context = await PrepareContextAsync(user.Ouid, ct);

        var cachedIds = db.GetCachedMatchIds(user.Ouid, Options.MatchType, window);
        if (cachedIds.Count > 0)
        {
            var cached = await Task.Run(() => BuildReport(user, cachedIds, window, context), ct);
            progress?.Report(new LookupProgress(LookupStage.ShowingCache, cached, 0, 0));
        }

        var ids = await api.GetUserMatchIdsAsync(user.Ouid, Options.MatchType, 0, window, ct);
        var missing = db.FilterMissing(ids);
        var toFetch = ids.Where(missing.Contains).ToList();
        var fetched = 0;

        foreach (var id in toFetch)
        {
            try
            {
                db.SaveMatch(await api.GetMatchDetailJsonAsync(id, ct));
            }
            catch (Exception e) when (IsTransient(e, ct))
            {
                // Out of quota, network down or the service is under maintenance: stop and show what we have.
                break;
            }
            fetched++;
            if (fetched % Options.ProgressBatch == 0 && fetched < toFetch.Count)
            {
                var partial = await Task.Run(() => BuildReport(user, ids, window, context), ct);
                progress?.Report(new LookupProgress(LookupStage.FetchingMatches, partial, fetched, toFetch.Count));
            }
        }

        var report = await Task.Run(() => BuildReport(user, ids, ids.Count, context), ct);
        progress?.Report(new LookupProgress(LookupStage.Done, report, fetched, toFetch.Count));

        // The card is already up; overall and price follow a moment later.
        var withMarket = await AddMarketAsync(report, ct);
        if (!ReferenceEquals(withMarket, report))
        {
            report = withMarket;
            progress?.Report(new LookupProgress(LookupStage.Done, report, fetched, toFetch.Count));
        }
        return report;
    }

    private async Task<OpponentReport> AddMarketAsync(OpponentReport report, CancellationToken ct)
    {
        if (market is null || Options.MarketPlayers <= 0) return report;
        var found = new Dictionary<int, PlayerMarket>();
        foreach (var p in report.Analysis.Players.Take(Options.MarketPlayers))
        {
            var strong = Math.Max(1, p.TopGrade);
            var cached = db.GetPlayerMarket(p.SpId, strong);
            if (cached is not null && Now - cached.FetchedAt < Options.MarketTtl)
            {
                found[p.SpId] = cached;
                continue;
            }
            try
            {
                if (await market.GetPlayerAsync(p.SpId, strong, ct) is { } fresh)
                {
                    db.SavePlayerMarket(fresh);
                    found[p.SpId] = fresh;
                    continue;
                }
            }
            catch (Exception e) when (e is HttpRequestException || (e is TaskCanceledException && !ct.IsCancellationRequested))
            {
                // The data center is optional: an old value, or none, is better than a failed card.
            }
            if (cached is not null) found[p.SpId] = cached;
        }
        return found.Count == 0 ? report : report with { Market = found };
    }

    private sealed record LookupContext(Baseline? Baseline, string? MyOuid, UserAnalysis? Me);

    private async Task<LookupContext> PrepareContextAsync(string opponentOuid, CancellationToken ct)
    {
        var baseline = await GetBaselineAsync(ct);
        string? myOuid = null;
        UserAnalysis? me = null;
        if (!string.IsNullOrWhiteSpace(Options.MyNickname))
        {
            myOuid = db.FindUserByNickname(Options.MyNickname)?.Ouid;
            if (myOuid is not null && myOuid != opponentOuid)
            {
                var myMatches = db.GetMatches(db.GetCachedMatchIds(myOuid, Options.MatchType, 50));
                if (myMatches.Count > 0) me = UserAnalyzer.Analyze(myMatches, myOuid, baseline);
            }
        }
        return new LookupContext(baseline, myOuid, me);
    }

    private OpponentReport BuildReport(CachedUser user, IReadOnlyList<string> matchIds, int requested, LookupContext context)
    {
        var matches = db.GetMatches(matchIds).OrderByDescending(m => m.MatchDate).ToList();
        var analysis = UserAnalyzer.Analyze(matches, user.Ouid, context.Baseline,
            id => db.GetPlayerNames([id]).GetValueOrDefault(id) ?? $"#{id}");

        // Names are looked up only for the players that appear on the card.
        var ids = analysis.Players.Take(10).Select(p => p.SpId)
            .Concat(analysis.Combos.SelectMany(c => new[] { c.AssistSpId, c.ScorerSpId }))
            .Concat(analysis.Signature is { } s ? [s.ScorerSpId, s.AssistSpId ?? 0] : []);
        var names = db.GetPlayerNames(ids);

        var divisions = db.GetMaxDivisions(user.Ouid).Divisions;
        var division = divisions.FirstOrDefault(d => d.MatchType == Options.MatchType);
        var recentDivision = matches
            .Select(m => m.SideOf(user.Ouid)?.Division ?? 0)
            .FirstOrDefault(d => d > 0);
        var history = db.GetNicknameHistory(user.Ouid).Where(n => n != user.Nickname).ToList();

        return new OpponentReport
        {
            Ouid = user.Ouid,
            Nickname = user.Nickname,
            Level = user.Level,
            MaxDivisionName = division is null ? null : db.GetDivisionName(division.Division) ?? $"등급 {division.Division}",
            RecentDivisionName = recentDivision == 0 ? null : db.GetDivisionName(recentDivision) ?? $"등급 {recentDivision}",
            MaxDivisionId = division?.Division,
            RecentDivisionId = recentDivision == 0 ? null : recentDivision,
            Matches = matches,
            Form = RecentForm.Of(matches, user.Ouid),
            PreviousNicknames = history,
            Analysis = analysis,
            OneLine = Summary.OneLine(analysis),
            MatchupAlerts = context.Me is null ? [] : Matchup.Compare(analysis, context.Me),
            HeadToHead = context.MyOuid is null || context.MyOuid == user.Ouid ? null : HeadToHeadWith(context.MyOuid, user.Ouid),
            Memo = db.GetMemo(user.Ouid),
            PlayerNames = names,
            LoadedMatches = analysis.Record.Matches,
            RequestedMatches = requested,
        };
    }

    private HeadToHead? HeadToHeadWith(string myOuid, string opponentOuid)
    {
        var rows = db.GetHeadToHead(myOuid, opponentOuid, Options.MatchType);
        if (rows.Count == 0) return null;
        string? lastScore = null;
        if (db.GetMatch(rows[0].MatchId) is { } last && last.SideOf(myOuid) is { } me && last.OpponentOf(myOuid) is { } opp)
        {
            // The scoreboard both players saw, own goals and forfeit 3:0 included.
            lastScore = $"{me.Shoot.GoalTotalDisplay}:{opp.Shoot.GoalTotalDisplay}";
        }
        return new HeadToHead(
            rows.Count,
            rows.Count(r => r.Result == "승"),
            rows.Count(r => r.Result == "무"),
            rows.Count(r => r.Result == "패"),
            rows[0].MatchDate,
            lastScore);
    }

    // ── users ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Picks the first OCR candidate that is a real user: the id API doubles as the OCR checker.
    /// Known users are answered from the cache without a call.
    /// </summary>
    /// <returns>The nickname as the API spells it, or null when none of the candidates exists.</returns>
    public async Task<string?> FindExistingNicknameAsync(IEnumerable<string> candidates, CancellationToken ct = default)
    {
        foreach (var candidate in candidates)
        {
            var user = await ResolveUserAsync(candidate.Trim(), ct);
            if (user is not null) return user.Nickname;
        }
        return null;
    }

    private async Task<CachedUser?> ResolveUserAsync(string nickname, CancellationToken ct)
    {
        var cached = db.FindUserByNickname(nickname);
        if (cached is not null && Now - cached.UpdatedAt < Options.UserInfoTtl) return cached;

        var ouid = await api.GetOuidAsync(nickname, ct);
        if (ouid is null) return null;

        var basic = await api.GetUserBasicAsync(ouid, ct);
        db.UpsertUser(ouid, basic.Nickname.Length > 0 ? basic.Nickname : nickname, basic.Level, Now);

        var (_, divisionsUpdated) = db.GetMaxDivisions(ouid);
        if (divisionsUpdated is null || Now - divisionsUpdated.Value >= Options.UserInfoTtl)
        {
            db.ReplaceMaxDivisions(ouid, await api.GetMaxDivisionAsync(ouid, ct), Now);
        }
        return db.FindUser(ouid);
    }

    /// <summary>
    /// A user's last <paramref name="count"/> 감독모드 matches (matchtype 52): record, goals, every card's per-game numbers and
    /// the formations with their record. New matches are fetched once and cached like official ones.
    /// </summary>
    /// <returns>Null when no user has that nickname.</returns>
    public async Task<ManagerReport?> ManagerAnalysisAsync(string nickname, int count = 100, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        progress?.Report("구단주 확인 중…");
        var user = await ResolveUserAsync(nickname.Trim(), ct);
        if (user is null) return null;
        try { await EnsureMetadataAsync(ct); } catch (HttpRequestException) { } // names only
        var ids = await api.GetUserMatchIdsAsync(user.Ouid, ManagerAnalysis.ManagerMatch, 0, Math.Clamp(count, 1, 100), ct);
        var missing = db.FilterMissing(ids);
        var toFetch = ids.Where(missing.Contains).ToList();
        for (var i = 0; i < toFetch.Count; i++)
        {
            progress?.Report($"감독모드 경기 불러오는 중 {i + 1}/{toFetch.Count}");
            try
            {
                db.SaveMatch(await api.GetMatchDetailJsonAsync(toFetch[i], ct));
            }
            catch (Exception e) when (IsTransient(e, ct))
            {
                break; // out of quota or offline: analyse what we have
            }
        }
        var matches = db.GetMatches(ids);
        var spIds = matches.SelectMany(m => m.SideOf(user.Ouid)?.Player ?? []).Select(p => p.SpId).Distinct();
        var names = db.GetPlayerNames(spIds);
        return await Task.Run(() => ManagerAnalysis.Build(user.Nickname, user.Ouid, matches, names), ct);
    }

    /// <summary>Pulls my latest matches into the cache so head-to-head records and matchup alerts work.</summary>
    /// <returns>How many new matches were stored.</returns>
    public async Task<int> SyncMyMatchesAsync(int limit = 100, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(Options.MyNickname)) return 0;
        var me = await ResolveUserAsync(Options.MyNickname, ct);
        if (me is null) return 0;

        var ids = await api.GetUserMatchIdsAsync(me.Ouid, Options.MatchType, 0, Math.Clamp(limit, 1, 100), ct);
        var missing = db.FilterMissing(ids);
        var stored = 0;
        foreach (var id in ids.Where(missing.Contains))
        {
            try
            {
                db.SaveMatch(await api.GetMatchDetailJsonAsync(id, ct));
                stored++;
            }
            catch (Exception e) when (IsTransient(e, ct))
            {
                break;
            }
        }
        return stored;
    }

    /// <summary>Failures worth stopping a batch for but not worth failing the lookup: everything except a bad key or a user cancel.</summary>
    private static bool IsTransient(Exception e, CancellationToken ct) => e switch
    {
        NexonApiException api => !api.IsAuthError,
        HttpRequestException => true,
        TaskCanceledException => !ct.IsCancellationRequested,
        _ => false,
    };

    public void SaveMemo(string ouid, string text, IEnumerable<string> tags) => db.SaveMemo(ouid, text, tags, Now);

    // ── metadata & baseline ────────────────────────────────────────────────

    /// <summary>Loads player and division names into SQLite when missing or older than the TTL.</summary>
    public async Task EnsureMetadataAsync(CancellationToken ct = default)
    {
        if (IsStale(MetaPlayersKey))
        {
            var json = await api.GetMetadataJsonAsync("spid", ct);
            var players = JsonSerializer.Deserialize(json, FcJsonContext.Default.ListSpIdMeta) ?? [];
            await Task.Run(() => db.ReplacePlayers(players), ct);
            db.SetValue(MetaPlayersKey, players.Count.ToString());
        }
        if (IsStale(MetaDivisionsKey))
        {
            var json = await api.GetMetadataJsonAsync("division", ct);
            var divisions = JsonSerializer.Deserialize(json, FcJsonContext.Default.ListDivisionMeta) ?? [];
            db.ReplaceDivisions(divisions);
            db.SetValue(MetaDivisionsKey, divisions.Count.ToString());
        }
    }

    private bool IsStale(string key) => db.GetValue(key) is not { } v || Now - v.UpdatedAt >= Options.MetadataTtl;

    /// <summary>
    /// The population every user is compared against, rebuilt from the cache when it is a day old or the cache
    /// has grown by a fifth since the last build.
    /// </summary>
    public async Task<Baseline?> GetBaselineAsync(CancellationToken ct = default)
    {
        await _baselineGate.WaitAsync(ct);
        try
        {
            var count = db.CountMatches(Options.MatchType);
            var fresh = _baseline is not null
                && Now - _baselineBuiltAt < Options.BaselineTtl
                && count < _baselineMatchCount * 1.2 + 10;
            if (!fresh)
            {
                _baseline = await Task.Run(() => Baseline.Build(db.EnumerateMatches(Options.MatchType, Options.BaselineSampleLimit)), ct);
                _baselineBuiltAt = Now;
                _baselineMatchCount = count;
            }
            return _baseline;
        }
        finally
        {
            _baselineGate.Release();
        }
    }
}
