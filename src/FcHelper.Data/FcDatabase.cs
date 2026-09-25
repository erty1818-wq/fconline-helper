using System.IO.Compression;
using System.Text;
using System.Text.Json;
using FcHelper.Core.Models;
using Microsoft.Data.Sqlite;

namespace FcHelper.Data;

public sealed record CachedUser(string Ouid, string Nickname, int Level, DateTime UpdatedAt);

public sealed record Memo(string Ouid, string Text, IReadOnlyList<string> Tags, DateTime UpdatedAt);

public sealed record MatchSideRow(string MatchId, string Ouid, string Nickname, string Result, DateTime MatchDate, int MatchType);

/// <summary>
/// Local SQLite cache. Finished matches never change, so match details are kept forever (compressed raw JSON);
/// everything else carries an update time and the caller decides whether it is fresh enough.
/// </summary>
public sealed class FcDatabase
{
    private const int SchemaVersion = 1;
    private readonly string _connectionString;

    public FcDatabase(string path)
    {
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = true,
        }.ToString();
        Migrate();
    }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        return c;
    }

    private void Migrate()
    {
        using var c = Open();
        Exec(c, "PRAGMA journal_mode=WAL;");
        Exec(c, """
            CREATE TABLE IF NOT EXISTS kv (key TEXT PRIMARY KEY, value TEXT NOT NULL, updated_at TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS users (
                ouid TEXT PRIMARY KEY, nickname TEXT NOT NULL, nickname_key TEXT NOT NULL, level INTEGER NOT NULL, updated_at TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_users_nickname ON users(nickname_key);
            CREATE TABLE IF NOT EXISTS nickname_history (
                ouid TEXT NOT NULL, nickname TEXT NOT NULL, first_seen TEXT NOT NULL, PRIMARY KEY (ouid, nickname));
            CREATE TABLE IF NOT EXISTS max_division (
                ouid TEXT NOT NULL, match_type INTEGER NOT NULL, division INTEGER NOT NULL, achieved_at TEXT NOT NULL,
                updated_at TEXT NOT NULL, PRIMARY KEY (ouid, match_type));
            CREATE TABLE IF NOT EXISTS matches (
                match_id TEXT PRIMARY KEY, match_date TEXT NOT NULL, match_type INTEGER NOT NULL, json BLOB NOT NULL);
            CREATE TABLE IF NOT EXISTS match_side (
                match_id TEXT NOT NULL, ouid TEXT NOT NULL, nickname TEXT NOT NULL, result TEXT NOT NULL,
                match_date TEXT NOT NULL, match_type INTEGER NOT NULL, PRIMARY KEY (match_id, ouid));
            CREATE INDEX IF NOT EXISTS ix_match_side_ouid ON match_side(ouid, match_type, match_date DESC);
            CREATE TABLE IF NOT EXISTS memos (ouid TEXT PRIMARY KEY, text TEXT NOT NULL, tags TEXT NOT NULL, updated_at TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS players (sp_id INTEGER PRIMARY KEY, name TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS divisions (division_id INTEGER PRIMARY KEY, name TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS player_market (
                sp_id INTEGER NOT NULL, strong INTEGER NOT NULL, name TEXT NOT NULL, ovr INTEGER NOT NULL,
                position TEXT NOT NULL, price TEXT, updated_at TEXT NOT NULL, PRIMARY KEY (sp_id, strong));
            """);
        SetValue(c, "schema_version", SchemaVersion.ToString());
    }

    public static string NicknameKey(string nickname) => nickname.Trim().ToLowerInvariant();

    // ── users ──────────────────────────────────────────────────────────────

    public CachedUser? FindUserByNickname(string nickname)
    {
        using var c = Open();
        using var cmd = Cmd(c, "SELECT ouid, nickname, level, updated_at FROM users WHERE nickname_key = $k ORDER BY updated_at DESC LIMIT 1",
            ("$k", NicknameKey(nickname)));
        using var r = cmd.ExecuteReader();
        return r.Read() ? new CachedUser(r.GetString(0), r.GetString(1), r.GetInt32(2), ParseDate(r.GetString(3))) : null;
    }

    public CachedUser? FindUser(string ouid)
    {
        using var c = Open();
        using var cmd = Cmd(c, "SELECT ouid, nickname, level, updated_at FROM users WHERE ouid = $o", ("$o", ouid));
        using var r = cmd.ExecuteReader();
        return r.Read() ? new CachedUser(r.GetString(0), r.GetString(1), r.GetInt32(2), ParseDate(r.GetString(3))) : null;
    }

    public void UpsertUser(string ouid, string nickname, int level, DateTime now)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        Exec(c, """
            INSERT INTO users (ouid, nickname, nickname_key, level, updated_at) VALUES ($o, $n, $k, $l, $t)
            ON CONFLICT(ouid) DO UPDATE SET nickname = $n, nickname_key = $k, level = $l, updated_at = $t
            """, ("$o", ouid), ("$n", nickname), ("$k", NicknameKey(nickname)), ("$l", level), ("$t", Iso(now)));
        Exec(c, "INSERT OR IGNORE INTO nickname_history (ouid, nickname, first_seen) VALUES ($o, $n, $t)",
            ("$o", ouid), ("$n", nickname), ("$t", Iso(now)));
        tx.Commit();
    }

    /// <summary>Every nickname this ouid has been seen with, oldest first.</summary>
    public IReadOnlyList<string> GetNicknameHistory(string ouid)
    {
        using var c = Open();
        using var cmd = Cmd(c, "SELECT nickname FROM nickname_history WHERE ouid = $o ORDER BY first_seen", ("$o", ouid));
        return ReadAll(cmd, r => r.GetString(0));
    }

    public (IReadOnlyList<MaxDivision> Divisions, DateTime? UpdatedAt) GetMaxDivisions(string ouid)
    {
        using var c = Open();
        using var cmd = Cmd(c, "SELECT match_type, division, achieved_at, updated_at FROM max_division WHERE ouid = $o", ("$o", ouid));
        DateTime? updated = null;
        var list = ReadAll(cmd, r =>
        {
            updated = ParseDate(r.GetString(3));
            return new MaxDivision { MatchType = r.GetInt32(0), Division = r.GetInt32(1), AchievementDate = ParseDate(r.GetString(2)) };
        });
        return (list, updated);
    }

    public void ReplaceMaxDivisions(string ouid, IEnumerable<MaxDivision> divisions, DateTime now)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        Exec(c, "DELETE FROM max_division WHERE ouid = $o", ("$o", ouid));
        foreach (var d in divisions)
        {
            Exec(c, "INSERT INTO max_division VALUES ($o, $m, $d, $a, $t)",
                ("$o", ouid), ("$m", d.MatchType), ("$d", d.Division), ("$a", Iso(d.AchievementDate)), ("$t", Iso(now)));
        }
        tx.Commit();
    }

    // ── matches ────────────────────────────────────────────────────────────

    public bool HasMatch(string matchId)
    {
        using var c = Open();
        using var cmd = Cmd(c, "SELECT 1 FROM matches WHERE match_id = $m", ("$m", matchId));
        return cmd.ExecuteScalar() is not null;
    }

    public HashSet<string> FilterMissing(IEnumerable<string> matchIds)
    {
        var missing = new HashSet<string>();
        using var c = Open();
        using var cmd = Cmd(c, "SELECT 1 FROM matches WHERE match_id = $m", ("$m", ""));
        foreach (var id in matchIds)
        {
            cmd.Parameters["$m"].Value = id;
            if (cmd.ExecuteScalar() is null) missing.Add(id);
        }
        return missing;
    }

    /// <summary>Stores the raw API JSON and indexes both sides of the match.</summary>
    public MatchDetail SaveMatch(string rawJson)
    {
        var match = JsonSerializer.Deserialize(rawJson, FcJsonContext.Default.MatchDetail)
            ?? throw new InvalidDataException("match-detail JSON is empty.");
        if (string.IsNullOrEmpty(match.MatchId)) throw new InvalidDataException("match-detail JSON has no matchId.");

        using var c = Open();
        using var tx = c.BeginTransaction();
        Exec(c, "INSERT OR REPLACE INTO matches (match_id, match_date, match_type, json) VALUES ($m, $d, $t, $j)",
            ("$m", match.MatchId), ("$d", Iso(match.MatchDate)), ("$t", match.MatchType), ("$j", Compress(rawJson)));
        foreach (var side in match.MatchInfo)
        {
            Exec(c, "INSERT OR REPLACE INTO match_side VALUES ($m, $o, $n, $r, $d, $t)",
                ("$m", match.MatchId), ("$o", side.Ouid), ("$n", side.Nickname), ("$r", side.MatchDetail.MatchResult),
                ("$d", Iso(match.MatchDate)), ("$t", match.MatchType));
        }
        tx.Commit();
        return match;
    }

    public MatchDetail? GetMatch(string matchId)
    {
        using var c = Open();
        using var cmd = Cmd(c, "SELECT json FROM matches WHERE match_id = $m", ("$m", matchId));
        return cmd.ExecuteScalar() is byte[] blob ? Deserialize(blob) : null;
    }

    public IReadOnlyList<MatchDetail> GetMatches(IEnumerable<string> matchIds)
    {
        var list = new List<MatchDetail>();
        using var c = Open();
        using var cmd = Cmd(c, "SELECT json FROM matches WHERE match_id = $m", ("$m", ""));
        foreach (var id in matchIds)
        {
            cmd.Parameters["$m"].Value = id;
            if (cmd.ExecuteScalar() is byte[] blob) list.Add(Deserialize(blob));
        }
        return list;
    }

    /// <summary>Cached matches the user played, newest first.</summary>
    public IReadOnlyList<string> GetCachedMatchIds(string ouid, int matchType, int limit)
    {
        using var c = Open();
        using var cmd = Cmd(c, "SELECT match_id FROM match_side WHERE ouid = $o AND match_type = $t ORDER BY match_date DESC LIMIT $l",
            ("$o", ouid), ("$t", matchType), ("$l", limit));
        return ReadAll(cmd, r => r.GetString(0));
    }

    /// <summary>Matches in which both users took part, newest first.</summary>
    public IReadOnlyList<MatchSideRow> GetHeadToHead(string ouid, string otherOuid, int? matchType = null)
    {
        using var c = Open();
        using var cmd = Cmd(c, """
            SELECT a.match_id, a.ouid, a.nickname, a.result, a.match_date, a.match_type
            FROM match_side a JOIN match_side b ON a.match_id = b.match_id
            WHERE a.ouid = $a AND b.ouid = $b AND ($t IS NULL OR a.match_type = $t)
            ORDER BY a.match_date DESC
            """, ("$a", ouid), ("$b", otherOuid), ("$t", matchType));
        return ReadAll(cmd, r => new MatchSideRow(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), ParseDate(r.GetString(4)), r.GetInt32(5)));
    }

    public int CountMatches(int matchType)
    {
        using var c = Open();
        using var cmd = Cmd(c, "SELECT COUNT(*) FROM matches WHERE match_type = $t", ("$t", matchType));
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    /// <summary>Streams every cached match of a type (for baseline statistics).</summary>
    public IEnumerable<MatchDetail> EnumerateMatches(int matchType, int limit)
    {
        using var c = Open();
        using var cmd = Cmd(c, "SELECT json FROM matches WHERE match_type = $t ORDER BY match_date DESC LIMIT $l", ("$t", matchType), ("$l", limit));
        using var r = cmd.ExecuteReader();
        while (r.Read()) yield return Deserialize((byte[])r[0]);
    }

    // ── memos ──────────────────────────────────────────────────────────────

    // ── data center (overall, price) ───────────────────────────────────────

    public PlayerMarket? GetPlayerMarket(int spId, int strong)
    {
        using var c = Open();
        using var cmd = Cmd(c, "SELECT name, ovr, position, price, updated_at FROM player_market WHERE sp_id = $s AND strong = $g",
            ("$s", spId), ("$g", strong));
        using var r = cmd.ExecuteReader();
        return r.Read()
            ? new PlayerMarket(spId, strong, r.GetString(0), r.GetInt32(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), ParseDate(r.GetString(4)))
            : null;
    }

    public void SavePlayerMarket(PlayerMarket m)
    {
        using var c = Open();
        Exec(c, "INSERT OR REPLACE INTO player_market VALUES ($s, $g, $n, $o, $p, $v, $t)",
            ("$s", m.SpId), ("$g", m.Strong), ("$n", m.Name), ("$o", m.Ovr), ("$p", m.Position), ("$v", m.Price), ("$t", Iso(m.FetchedAt)));
    }

    public Memo? GetMemo(string ouid)
    {
        using var c = Open();
        using var cmd = Cmd(c, "SELECT text, tags, updated_at FROM memos WHERE ouid = $o", ("$o", ouid));
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        var tags = r.GetString(1).Split('\u001f', StringSplitOptions.RemoveEmptyEntries);
        return new Memo(ouid, r.GetString(0), tags, ParseDate(r.GetString(2)));
    }

    public void SaveMemo(string ouid, string text, IEnumerable<string> tags, DateTime now)
    {
        var tagList = tags.Select(t => t.Trim()).Where(t => t.Length > 0).Distinct().ToList();
        using var c = Open();
        if (string.IsNullOrWhiteSpace(text) && tagList.Count == 0)
        {
            Exec(c, "DELETE FROM memos WHERE ouid = $o", ("$o", ouid));
            return;
        }
        Exec(c, "INSERT OR REPLACE INTO memos VALUES ($o, $x, $g, $t)",
            ("$o", ouid), ("$x", text.Trim()), ("$g", string.Join('\u001f', tagList)), ("$t", Iso(now)));
    }

    // ── metadata ───────────────────────────────────────────────────────────

    public void ReplacePlayers(IEnumerable<SpIdMeta> players) => ReplaceNames("players", "sp_id", players.Select(p => (p.Id, p.Name)));

    public void ReplaceDivisions(IEnumerable<DivisionMeta> divisions) =>
        ReplaceNames("divisions", "division_id", divisions.Select(d => (d.DivisionId, d.DivisionName)));

    private void ReplaceNames(string table, string idColumn, IEnumerable<(int Id, string Name)> rows)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        Exec(c, $"DELETE FROM {table}");
        using var cmd = Cmd(c, $"INSERT OR REPLACE INTO {table} ({idColumn}, name) VALUES ($i, $n)", ("$i", 0), ("$n", ""));
        foreach (var (id, name) in rows)
        {
            cmd.Parameters["$i"].Value = id;
            cmd.Parameters["$n"].Value = name;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    /// <summary>Looks up only the requested names so the (large) player list never has to be held in memory.</summary>
    public Dictionary<int, string> GetPlayerNames(IEnumerable<int> spIds) => GetNames("players", "sp_id", spIds);

    public string? GetDivisionName(int divisionId) => GetNames("divisions", "division_id", [divisionId]).GetValueOrDefault(divisionId);

    private Dictionary<int, string> GetNames(string table, string idColumn, IEnumerable<int> ids)
    {
        var result = new Dictionary<int, string>();
        using var c = Open();
        using var cmd = Cmd(c, $"SELECT name FROM {table} WHERE {idColumn} = $i", ("$i", 0));
        foreach (var id in ids.Distinct())
        {
            cmd.Parameters["$i"].Value = id;
            if (cmd.ExecuteScalar() is string name) result[id] = name;
        }
        return result;
    }

    // ── key/value ──────────────────────────────────────────────────────────

    public (string Value, DateTime UpdatedAt)? GetValue(string key)
    {
        using var c = Open();
        using var cmd = Cmd(c, "SELECT value, updated_at FROM kv WHERE key = $k", ("$k", key));
        using var r = cmd.ExecuteReader();
        return r.Read() ? (r.GetString(0), ParseDate(r.GetString(1))) : null;
    }

    public void SetValue(string key, string value)
    {
        using var c = Open();
        SetValue(c, key, value);
    }

    private static void SetValue(SqliteConnection c, string key, string value) =>
        Exec(c, "INSERT OR REPLACE INTO kv VALUES ($k, $v, $t)", ("$k", key), ("$v", value), ("$t", Iso(DateTime.UtcNow)));

    // ── helpers ────────────────────────────────────────────────────────────

    private static byte[] Compress(string json)
    {
        using var ms = new MemoryStream();
        using (var brotli = new BrotliStream(ms, CompressionLevel.Fastest))
        {
            brotli.Write(Encoding.UTF8.GetBytes(json));
        }
        return ms.ToArray();
    }

    private static MatchDetail Deserialize(byte[] blob)
    {
        using var brotli = new BrotliStream(new MemoryStream(blob), CompressionMode.Decompress);
        return JsonSerializer.Deserialize(brotli, FcJsonContext.Default.MatchDetail)
            ?? throw new InvalidDataException("Cached match is empty.");
    }

    private static string Iso(DateTime d) => DateTime.SpecifyKind(d, d.Kind == DateTimeKind.Unspecified ? DateTimeKind.Utc : d.Kind)
        .ToUniversalTime().ToString("O");

    private static DateTime ParseDate(string s) =>
        DateTime.Parse(s, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal);

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

    private static List<T> ReadAll<T>(SqliteCommand cmd, Func<SqliteDataReader, T> map)
    {
        var list = new List<T>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(map(r));
        return list;
    }
}
