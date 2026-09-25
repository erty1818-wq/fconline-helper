using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace FcHelper.Market;

public sealed record MarketSnapshot(long Id, string Kind, DateTime StartedAt, DateTime? FinishedAt);

/// <summary>
/// Market data in SQLite, as snapshots: a refresh writes a new snapshot and only becomes the one searched once it
/// finishes, so the previous data stays usable meanwhile and an interrupted refresh resumes where it stopped.
/// Old snapshots are pruned. Nothing here leaves the PC.
/// </summary>
public sealed class MarketStore
{
    public const int KeepSnapshots = 3;
    private readonly string _connectionString;

    public MarketStore(string path)
    {
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = true }.ToString();
        using var c = Open();
        Exec(c, """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS market_snapshot (
                id INTEGER PRIMARY KEY AUTOINCREMENT, kind TEXT NOT NULL, started_at TEXT NOT NULL, finished_at TEXT);
            CREATE TABLE IF NOT EXISTS market_card (
                snapshot INTEGER NOT NULL, grp TEXT NOT NULL, sp_id INTEGER NOT NULL, name TEXT NOT NULL, season TEXT NOT NULL,
                pay INTEGER NOT NULL, ovr1 INTEGER NOT NULL, weak_foot INTEGER NOT NULL, rating REAL, rating_count INTEGER NOT NULL,
                prices TEXT NOT NULL, stats TEXT NOT NULL, PRIMARY KEY (snapshot, grp, sp_id));
            CREATE TABLE IF NOT EXISTS market_tag (
                snapshot INTEGER NOT NULL, grp TEXT NOT NULL, sp_id INTEGER NOT NULL, tag TEXT NOT NULL, PRIMARY KEY (snapshot, grp, sp_id, tag));
            CREATE TABLE IF NOT EXISTS market_done (snapshot INTEGER NOT NULL, key TEXT NOT NULL, PRIMARY KEY (snapshot, key));
            CREATE TABLE IF NOT EXISTS market_season (season_id INTEGER PRIMARY KEY, name TEXT NOT NULL, first_seen TEXT NOT NULL);
            """);
    }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        return c;
    }

    // ── snapshots ──────────────────────────────────────────────────────────

    public MarketSnapshot? LatestFinished() => ReadSnapshot("WHERE finished_at IS NOT NULL ORDER BY id DESC LIMIT 1");
    public MarketSnapshot? Unfinished() => ReadSnapshot("WHERE finished_at IS NULL ORDER BY id DESC LIMIT 1");

    private MarketSnapshot? ReadSnapshot(string where)
    {
        using var c = Open();
        using var cmd = Cmd(c, $"SELECT id, kind, started_at, finished_at FROM market_snapshot {where}");
        using var r = cmd.ExecuteReader();
        return r.Read() ? new MarketSnapshot(r.GetInt64(0), r.GetString(1), Date(r.GetString(2)), r.IsDBNull(3) ? null : Date(r.GetString(3))) : null;
    }

    public long StartSnapshot(string kind, DateTime now)
    {
        using var c = Open();
        using var cmd = Cmd(c, "INSERT INTO market_snapshot (kind, started_at) VALUES ($k, $t); SELECT last_insert_rowid();", ("$k", kind), ("$t", Iso(now)));
        return (long)cmd.ExecuteScalar()!;
    }

    /// <summary>Marks a snapshot finished and drops all but the newest <see cref="KeepSnapshots"/> finished ones.</summary>
    public void FinishSnapshot(long id, DateTime now)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        Exec(c, "UPDATE market_snapshot SET finished_at = $t WHERE id = $i", ("$t", Iso(now)), ("$i", id));
        Exec(c, "DELETE FROM market_done WHERE snapshot = $i", ("$i", id));
        const string old = "SELECT id FROM market_snapshot WHERE finished_at IS NOT NULL ORDER BY id DESC LIMIT -1 OFFSET $n";
        foreach (var table in new[] { "market_card", "market_tag" })
            Exec(c, $"DELETE FROM {table} WHERE snapshot IN ({old})", ("$n", KeepSnapshots));
        Exec(c, $"DELETE FROM market_snapshot WHERE id IN ({old})", ("$n", KeepSnapshots));
        tx.Commit();
    }

    public int CountCards(long snapshot)
    {
        using var c = Open();
        using var cmd = Cmd(c, "SELECT COUNT(*) FROM market_card WHERE snapshot = $s", ("$s", snapshot));
        return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    public HashSet<string> DoneKeys(long snapshot)
    {
        using var c = Open();
        using var cmd = Cmd(c, "SELECT key FROM market_done WHERE snapshot = $s", ("$s", snapshot));
        using var r = cmd.ExecuteReader();
        var set = new HashSet<string>();
        while (r.Read()) set.Add(r.GetString(0));
        return set;
    }

    public void MarkDone(long snapshot, string key)
    {
        using var c = Open();
        Exec(c, "INSERT OR IGNORE INTO market_done VALUES ($s, $k)", ("$s", snapshot), ("$k", key));
    }

    // ── cards and tags ─────────────────────────────────────────────────────

    /// <summary>Inserts rows, merging stats with what an earlier query already stored for the same card.</summary>
    public void SaveRows(long snapshot, string group, IEnumerable<ListRow> rows)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        foreach (var row in rows)
        {
            var stats = new Dictionary<string, int>(row.Stats);
            using (var read = Cmd(c, "SELECT stats FROM market_card WHERE snapshot = $s AND grp = $g AND sp_id = $p",
                       ("$s", snapshot), ("$g", group), ("$p", row.SpId)))
            {
                if (read.ExecuteScalar() is string existing)
                    foreach (var (k, v) in JsonSerializer.Deserialize<Dictionary<string, int>>(existing)!) stats.TryAdd(k, v);
            }
            Exec(c, "INSERT OR REPLACE INTO market_card VALUES ($s, $g, $p, $n, $se, $pay, $o, $w, $r, $rc, $pr, $st)",
                ("$s", snapshot), ("$g", group), ("$p", row.SpId), ("$n", row.Name), ("$se", row.Season), ("$pay", row.Pay),
                ("$o", row.Ovr1), ("$w", row.WeakFoot), ("$r", row.Rating), ("$rc", row.RatingCount),
                ("$pr", JsonSerializer.Serialize(row.Prices)), ("$st", JsonSerializer.Serialize(stats)));
        }
        tx.Commit();
    }

    public void SaveTag(long snapshot, string group, IEnumerable<long> spIds, string tag)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        foreach (var id in spIds)
            Exec(c, "INSERT OR IGNORE INTO market_tag VALUES ($s, $g, $p, $t)", ("$s", snapshot), ("$g", group), ("$p", id), ("$t", tag));
        tx.Commit();
    }

    /// <summary>
    /// A price-only refresh re-reads prices and the first four stats; the other stats and the tags change only with
    /// new cards, so they are carried over from the previous snapshot for every card still listed.
    /// </summary>
    public void CarryOver(long from, long to)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        Exec(c, """
            INSERT OR IGNORE INTO market_tag
            SELECT $to, t.grp, t.sp_id, t.tag FROM market_tag t
            WHERE t.snapshot = $from AND EXISTS (SELECT 1 FROM market_card n WHERE n.snapshot = $to AND n.grp = t.grp AND n.sp_id = t.sp_id)
            """, ("$from", from), ("$to", to));
        var updates = new List<(string Grp, long SpId, string Stats)>();
        using (var cmd = Cmd(c, """
                   SELECT n.grp, n.sp_id, n.stats, o.stats FROM market_card n
                   JOIN market_card o ON o.snapshot = $from AND o.grp = n.grp AND o.sp_id = n.sp_id
                   WHERE n.snapshot = $to
                   """, ("$from", from), ("$to", to)))
        using (var r = cmd.ExecuteReader())
        {
            while (r.Read())
            {
                var stats = JsonSerializer.Deserialize<Dictionary<string, int>>(r.GetString(2))!;
                var added = false;
                foreach (var (k, v) in JsonSerializer.Deserialize<Dictionary<string, int>>(r.GetString(3))!) added |= stats.TryAdd(k, v);
                if (added) updates.Add((r.GetString(0), r.GetInt64(1), JsonSerializer.Serialize(stats)));
            }
        }
        foreach (var (grp, spId, stats) in updates)
            Exec(c, "UPDATE market_card SET stats = $st WHERE snapshot = $to AND grp = $g AND sp_id = $p", ("$st", stats), ("$to", to), ("$g", grp), ("$p", spId));
        tx.Commit();
    }

    public IReadOnlyList<MarketCard> LoadCards(long snapshot, string? group = null)
    {
        using var c = Open();
        var tags = new Dictionary<(string, long), HashSet<string>>();
        using (var cmd = Cmd(c, "SELECT grp, sp_id, tag FROM market_tag WHERE snapshot = $s" + (group is null ? "" : " AND grp = $g"),
                   ("$s", snapshot), ("$g", group)))
        using (var r = cmd.ExecuteReader())
        {
            while (r.Read())
            {
                var key = (r.GetString(0), r.GetInt64(1));
                if (!tags.TryGetValue(key, out var set)) tags[key] = set = [];
                set.Add(r.GetString(2));
            }
        }
        var cards = new List<MarketCard>();
        using (var cmd = Cmd(c, "SELECT grp, sp_id, name, season, pay, ovr1, weak_foot, rating, rating_count, prices, stats FROM market_card WHERE snapshot = $s"
                   + (group is null ? "" : " AND grp = $g"), ("$s", snapshot), ("$g", group)))
        using (var r = cmd.ExecuteReader())
        {
            while (r.Read())
            {
                var key = (r.GetString(0), r.GetInt64(1));
                cards.Add(new MarketCard
                {
                    Group = key.Item1, SpId = key.Item2, Name = r.GetString(2), Season = r.GetString(3), Pay = r.GetInt32(4),
                    Ovr1 = r.GetInt32(5), WeakFoot = r.GetInt32(6), Rating = r.IsDBNull(7) ? null : r.GetDouble(7), RatingCount = r.GetInt32(8),
                    Prices = JsonSerializer.Deserialize<Dictionary<int, long>>(r.GetString(9))!,
                    Stats = JsonSerializer.Deserialize<Dictionary<string, int>>(r.GetString(10))!,
                    Tags = tags.GetValueOrDefault(key) ?? [],
                });
            }
        }
        return cards;
    }

    // ── seasons ────────────────────────────────────────────────────────────

    public HashSet<int> KnownSeasons()
    {
        using var c = Open();
        using var cmd = Cmd(c, "SELECT season_id FROM market_season");
        using var r = cmd.ExecuteReader();
        var set = new HashSet<int>();
        while (r.Read()) set.Add(r.GetInt32(0));
        return set;
    }

    public void AddSeasons(IEnumerable<(int Id, string Name)> seasons, DateTime now)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        foreach (var (id, name) in seasons)
            Exec(c, "INSERT OR IGNORE INTO market_season VALUES ($i, $n, $t)", ("$i", id), ("$n", name), ("$t", Iso(now)));
        tx.Commit();
    }

    // ── helpers ────────────────────────────────────────────────────────────

    private static SqliteCommand Cmd(SqliteConnection c, string sql, params (string Name, object? Value)[] args)
    {
        var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }

    private static void Exec(SqliteConnection c, string sql, params (string Name, object? Value)[] args)
    {
        using var cmd = Cmd(c, sql, args);
        cmd.ExecuteNonQuery();
    }

    private static string Iso(DateTime d) => DateTime.SpecifyKind(d, DateTimeKind.Utc).ToString("O", CultureInfo.InvariantCulture);
    private static DateTime Date(string s) => DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
}
