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
        // Added after the first release of the table: OVR per listed position.
        using var cols = Cmd(c, "SELECT COUNT(*) FROM pragma_table_info('market_card') WHERE name = 'positions'");
        if (Convert.ToInt32(cols.ExecuteScalar(), CultureInfo.InvariantCulture) == 0)
            Exec(c, "ALTER TABLE market_card ADD COLUMN positions TEXT NOT NULL DEFAULT '{}'");
        ExtraSchema(c);
    }

    /// <summary>Tables of the ranker, team colour and price-history features, kept next to the market cards.</summary>
    private static void ExtraSchema(SqliteConnection c) => Exec(c, """
        CREATE TABLE IF NOT EXISTS price_history (
            day TEXT NOT NULL, grp TEXT NOT NULL, sp_id INTEGER NOT NULL, prices TEXT NOT NULL, PRIMARY KEY (day, grp, sp_id));
        CREATE TABLE IF NOT EXISTS ranker_chart (
            chart_date TEXT NOT NULL, rank_from INTEGER NOT NULL, rank_to INTEGER NOT NULL, kind TEXT NOT NULL, fetched_at TEXT NOT NULL,
            json TEXT NOT NULL, PRIMARY KEY (chart_date, rank_from, rank_to, kind));
        CREATE TABLE IF NOT EXISTS team_color (
            id INTEGER PRIMARY KEY, name TEXT NOT NULL, kind TEXT NOT NULL, max_members INTEGER NOT NULL, json TEXT NOT NULL, updated_at TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS team_color_member (
            team_color INTEGER NOT NULL, sp_id INTEGER NOT NULL, PRIMARY KEY (team_color, sp_id));
        CREATE TABLE IF NOT EXISTS kv_market (key TEXT PRIMARY KEY, value TEXT NOT NULL, updated_at TEXT NOT NULL);
        """);

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
            var positions = new Dictionary<string, int>(row.Positions);
            using (var read = Cmd(c, "SELECT stats, positions FROM market_card WHERE snapshot = $s AND grp = $g AND sp_id = $p",
                       ("$s", snapshot), ("$g", group), ("$p", row.SpId)))
            using (var r = read.ExecuteReader())
            {
                if (r.Read())
                {
                    foreach (var (k, v) in JsonSerializer.Deserialize<Dictionary<string, int>>(r.GetString(0))!) stats.TryAdd(k, v);
                    foreach (var (k, v) in JsonSerializer.Deserialize<Dictionary<string, int>>(r.GetString(1))!) positions.TryAdd(k, v);
                }
            }
            Exec(c, """
                INSERT OR REPLACE INTO market_card (snapshot, grp, sp_id, name, season, pay, ovr1, weak_foot, rating, rating_count, prices, stats, positions)
                VALUES ($s, $g, $p, $n, $se, $pay, $o, $w, $r, $rc, $pr, $st, $po)
                """,
                ("$s", snapshot), ("$g", group), ("$p", row.SpId), ("$n", row.Name), ("$se", row.Season), ("$pay", row.Pay),
                ("$o", row.Ovr1), ("$w", row.WeakFoot), ("$r", row.Rating), ("$rc", row.RatingCount),
                ("$pr", JsonSerializer.Serialize(row.Prices)), ("$st", JsonSerializer.Serialize(stats)), ("$po", JsonSerializer.Serialize(positions)));
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
        using (var cmd = Cmd(c, "SELECT grp, sp_id, name, season, pay, ovr1, weak_foot, rating, rating_count, prices, stats, positions FROM market_card WHERE snapshot = $s"
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
                    Positions = JsonSerializer.Deserialize<Dictionary<string, int>>(r.GetString(11))!,
                    Tags = tags.GetValueOrDefault(key) ?? [],
                });
            }
        }
        return cards;
    }

    // ── price history ──────────────────────────────────────────────────────

    /// <summary>Grades kept in the daily price history: enough for trends at the grades people buy at.</summary>
    public static readonly int[] HistoryGrades = [1, 5, 8, 10];
    public const int HistoryDays = 35;

    /// <summary>Stores the prices of a finished snapshot under its day and drops days older than <see cref="HistoryDays"/>.</summary>
    public void RecordPriceHistory(long snapshot, DateTime day)
    {
        var key = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        using var c = Open();
        using var tx = c.BeginTransaction();
        var rows = new List<(string Grp, long SpId, string Prices)>();
        using (var cmd = Cmd(c, "SELECT grp, sp_id, prices FROM market_card WHERE snapshot = $s", ("$s", snapshot)))
        using (var r = cmd.ExecuteReader())
        {
            while (r.Read())
            {
                var all = JsonSerializer.Deserialize<Dictionary<int, long>>(r.GetString(2))!;
                rows.Add((r.GetString(0), r.GetInt64(1), JsonSerializer.Serialize(HistoryGrades.Where(all.ContainsKey).ToDictionary(g => g, g => all[g]))));
            }
        }
        foreach (var (grp, spId, prices) in rows)
            Exec(c, "INSERT OR REPLACE INTO price_history VALUES ($d, $g, $p, $pr)", ("$d", key), ("$g", grp), ("$p", spId), ("$pr", prices));
        Exec(c, "DELETE FROM price_history WHERE day < $cut", ("$cut", day.AddDays(-HistoryDays).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
        tx.Commit();
    }

    public IReadOnlyList<DateOnly> PriceHistoryDays()
    {
        using var c = Open();
        using var cmd = Cmd(c, "SELECT DISTINCT day FROM price_history ORDER BY day");
        using var r = cmd.ExecuteReader();
        var days = new List<DateOnly>();
        while (r.Read()) days.Add(DateOnly.Parse(r.GetString(0), CultureInfo.InvariantCulture));
        return days;
    }

    public Dictionary<(string Grp, long SpId), Dictionary<int, long>> PriceHistory(DateOnly day)
    {
        using var c = Open();
        using var cmd = Cmd(c, "SELECT grp, sp_id, prices FROM price_history WHERE day = $d", ("$d", day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
        using var r = cmd.ExecuteReader();
        var result = new Dictionary<(string, long), Dictionary<int, long>>();
        while (r.Read()) result[(r.GetString(0), r.GetInt64(1))] = JsonSerializer.Deserialize<Dictionary<int, long>>(r.GetString(2))!;
        return result;
    }

    // ── ranker chart, team colours, small values ───────────────────────────

    public void SaveChart(RankerChartData chart, DateTime now)
    {
        using var c = Open();
        Exec(c, "INSERT OR REPLACE INTO ranker_chart VALUES ($d, $f, $t, 'all', $n, $j)",
            ("$d", chart.ChartDate), ("$f", chart.RankFrom), ("$t", chart.RankTo), ("$n", Iso(now)), ("$j", JsonSerializer.Serialize(chart)));
        Exec(c, "DELETE FROM ranker_chart WHERE fetched_at < $cut", ("$cut", Iso(now.AddDays(-HistoryDays))));
    }

    /// <summary>The newest chart fetched for a ranker range, with when it was fetched.</summary>
    public (RankerChartData Chart, DateTime FetchedAt)? LatestChart(int rankFrom, int rankTo)
    {
        using var c = Open();
        using var cmd = Cmd(c, "SELECT json, fetched_at FROM ranker_chart WHERE rank_from = $f AND rank_to = $t ORDER BY chart_date DESC, fetched_at DESC LIMIT 1",
            ("$f", rankFrom), ("$t", rankTo));
        using var r = cmd.ExecuteReader();
        return r.Read() ? (JsonSerializer.Deserialize<RankerChartData>(r.GetString(0))!, Date(r.GetString(1))) : null;
    }

    public void SaveTeamColors(IEnumerable<TeamColor> colors, DateTime now)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        foreach (var t in colors)
            Exec(c, "INSERT OR REPLACE INTO team_color VALUES ($i, $n, $k, $m, $j, $u)",
                ("$i", t.Id), ("$n", t.Name), ("$k", t.Kind.ToString()), ("$m", t.MaxMembers), ("$j", JsonSerializer.Serialize(t.Levels)), ("$u", Iso(now)));
        tx.Commit();
    }

    public IReadOnlyList<TeamColor> LoadTeamColors()
    {
        using var c = Open();
        using var cmd = Cmd(c, "SELECT id, name, kind, max_members, json FROM team_color ORDER BY id");
        using var r = cmd.ExecuteReader();
        var list = new List<TeamColor>();
        while (r.Read())
            list.Add(new TeamColor(r.GetInt32(0), r.GetString(1), Enum.Parse<TeamColorKind>(r.GetString(2)), r.GetInt32(3),
                JsonSerializer.Deserialize<List<TeamColorLevel>>(r.GetString(4))!));
        return list;
    }

    public void SaveTeamColorMembers(int id, IEnumerable<long> spIds)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        Exec(c, "DELETE FROM team_color_member WHERE team_color = $i", ("$i", id));
        foreach (var s in spIds) Exec(c, "INSERT OR IGNORE INTO team_color_member VALUES ($i, $s)", ("$i", id), ("$s", s));
        tx.Commit();
    }

    public IReadOnlySet<long> LoadTeamColorMembers(int id)
    {
        using var c = Open();
        using var cmd = Cmd(c, "SELECT sp_id FROM team_color_member WHERE team_color = $i", ("$i", id));
        using var r = cmd.ExecuteReader();
        var set = new HashSet<long>();
        while (r.Read()) set.Add(r.GetInt64(0));
        return set;
    }

    public (string Value, DateTime UpdatedAt)? GetValue(string key)
    {
        using var c = Open();
        using var cmd = Cmd(c, "SELECT value, updated_at FROM kv_market WHERE key = $k", ("$k", key));
        using var r = cmd.ExecuteReader();
        return r.Read() ? (r.GetString(0), Date(r.GetString(1))) : null;
    }

    public void SetValue(string key, string value, DateTime now)
    {
        using var c = Open();
        Exec(c, "INSERT OR REPLACE INTO kv_market VALUES ($k, $v, $t)", ("$k", key), ("$v", value), ("$t", Iso(now)));
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
