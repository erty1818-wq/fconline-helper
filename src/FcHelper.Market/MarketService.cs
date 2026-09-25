using System.Text.Json;

namespace FcHelper.Market;

public sealed record MarketStatus(MarketSnapshot? Current, int Cards, HarvestProgress? Running, string? LastError);

/// <summary>
/// Keeps the market data fresh without the user doing anything: a price refresh once the data is a day old, and at
/// once when the Open API's season list shows a season it has not seen (new cards). The first run is a full
/// collection. Callers decide *when* to call <see cref="RefreshIfDueAsync"/> (the app defers it while the game runs).
/// </summary>
public sealed class MarketService(
    IMarketListSource source, MarketStore store, Func<CancellationToken, Task<string>> seasonListJson, TimeProvider? time = null)
{
    public static readonly TimeSpan PriceTtl = TimeSpan.FromHours(20);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<(long, string, int), PriceModel?> _models = [];
    private readonly Dictionary<(long, string), IReadOnlyList<MarketCard>> _cards = [];
    private IReadOnlySet<long>? _rankerMembers;

    /// <summary>Cards of the team colours rankers use most; the price models then price that membership (refit on change).</summary>
    public void SetRankerTeamColorMembers(IReadOnlySet<long> members)
    {
        lock (_models)
        {
            if (_rankerMembers is not null && _rankerMembers.SetEquals(members)) return;
            _rankerMembers = members.Count > 0 ? members : null;
            _models.Clear();
        }
    }
    private HarvestProgress? _running;
    private string? _lastError;

    /// <summary>Raised (on any thread) when a refresh starts, advances or finishes.</summary>
    public event Action? Changed;

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    public MarketStatus Status
    {
        get
        {
            var current = store.LatestFinished();
            return new MarketStatus(current, current is null ? 0 : store.CountCards(current.Id), _running, _lastError);
        }
    }

    public bool IsRefreshing => _running is not null;

    /// <summary>When the data next needs a price refresh; null when there is no data yet (refresh now).</summary>
    public DateTime? NextDue => store.Unfinished() is not null || SchemaChanged ? null : store.LatestFinished()?.FinishedAt + PriceTtl;

    private const string SchemaKey = "market.schema";

    /// <summary>The collected stats or tags changed since the last full refresh: the next refresh collects everything once.</summary>
    public bool SchemaChanged => store.LatestFinished() is not null && store.GetValue(SchemaKey)?.Value != MarketGroups.SchemaVersion;

    /// <returns>True when a refresh ran to the end.</returns>
    public async Task<bool> RefreshIfDueAsync(bool force = false, CancellationToken ct = default)
    {
        if (!await _gate.WaitAsync(0, ct)) return false;
        try
        {
            var newSeasons = await DetectNewSeasonsAsync(ct);
            var latest = store.LatestFinished();
            var unfinished = store.Unfinished();
            var stale = latest?.FinishedAt is not { } done || Now - done >= PriceTtl;
            var schemaChanged = SchemaChanged;
            if (unfinished is null && !stale && !schemaChanged && newSeasons.Count == 0 && !force) return false;

            var kind = unfinished is not null ? Enum.Parse<RefreshKind>(unfinished.Kind)
                : latest is null || schemaChanged ? RefreshKind.Full : RefreshKind.Prices;
            var snapshot = unfinished?.Id ?? store.StartSnapshot(kind.ToString(), Now);
            _lastError = null;
            Report(new HarvestProgress(0, 1, "starting"));
            // Reported synchronously: Progress<T> would post late updates after the refresh has already finished.
            var progress = new Immediate(Report);
            await new MarketHarvester(source, store).RunAsync(snapshot, kind, latest?.Id, newSeasons.Select(s => s.Id).ToList(), progress, ct);
            store.FinishSnapshot(snapshot, Now);
            if (kind == RefreshKind.Full) store.SetValue(SchemaKey, MarketGroups.SchemaVersion, Now);
            store.RecordPriceHistory(snapshot, Now);
            store.AddSeasons(newSeasons, Now);
            lock (_models) { _models.Clear(); _cards.Clear(); }
            return true;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            // Offline or the site is down: keep the old data, try again at the next trigger.
            _lastError = "데이터센터에 연결하지 못했습니다. 다음에 다시 시도합니다.";
            return false;
        }
        finally
        {
            _running = null;
            _gate.Release();
            Changed?.Invoke();
        }
    }

    private void Report(HarvestProgress p)
    {
        _running = p;
        Changed?.Invoke();
    }

    private sealed class Immediate(Action<HarvestProgress> report) : IProgress<HarvestProgress>
    {
        public void Report(HarvestProgress value) => report(value);
    }

    /// <summary>Seasons in the official list that were not there last time. The first run just records them all.</summary>
    private async Task<IReadOnlyList<(int Id, string Name)>> DetectNewSeasonsAsync(CancellationToken ct)
    {
        List<(int Id, string Name)> seasons;
        try
        {
            using var doc = JsonDocument.Parse(await seasonListJson(ct));
            seasons = doc.RootElement.EnumerateArray()
                .Select(e => (e.GetProperty("seasonId").GetInt32(), e.GetProperty("className").GetString() ?? ""))
                .ToList();
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return [];
        }
        var known = store.KnownSeasons();
        if (known.Count == 0)
        {
            store.AddSeasons(seasons, Now);
            return [];
        }
        return seasons.Where(s => !known.Contains(s.Id)).ToList();
    }

    // ── queries ────────────────────────────────────────────────────────────

    public PriceModel? Model(string group, int grade)
    {
        var snap = store.LatestFinished();
        if (snap is null) return null;
        lock (_models)
        {
            if (!_models.TryGetValue((snap.Id, group, grade), out var model))
                _models[(snap.Id, group, grade)] = model = PriceModel.Fit(group, grade, Cards(snap.Id, group), _rankerMembers);
            return model;
        }
    }

    public IReadOnlyList<ValuePick> FindValue(ValueQuery q)
    {
        var snap = store.LatestFinished();
        var model = Model(q.Group, q.Grade);
        return snap is null || model is null ? [] : ValueFinder.Find(model, Cards(snap.Id, q.Group), q);
    }

    private IReadOnlyList<MarketCard> Cards(long snapshot, string group)
    {
        lock (_models)
        {
            if (!_cards.TryGetValue((snapshot, group), out var cards))
                _cards[(snapshot, group)] = cards = store.LoadCards(snapshot, group);
            return cards;
        }
    }
}
