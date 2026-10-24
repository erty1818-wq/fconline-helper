using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace FcHelper.Market;

public sealed record MarketExportSummary(int Snapshots, int Cards, int Series, int HistoryDays, int TeamColors, int RankerCharts, string? FirstDay, string? LastDay);

/// <summary>
/// Writes the local market data as gzip JSON lines for offline analysis (e.g. by another agent without data center
/// access): every kept snapshot, the daily price history, team colour members, ranker usage and season first-seen
/// dates. Opens the database read-only, so it is safe while the app runs. Only data center figures and footballer
/// names; nothing that names another user (ranker squads, opponents) is written.
/// </summary>
public static class MarketExport
{
    public const int FormatVersion = 1;
    public static readonly int[] DefaultGrades = [1, 3, 5, 8, 10, 11];

    public static MarketExportSummary Write(string dbPath, Stream output, IReadOnlyCollection<int>? grades = null)
    {
        var keep = (grades is { Count: > 0 } ? grades : DefaultGrades).ToHashSet();
        var cs = new SqliteConnectionStringBuilder { DataSource = dbPath, Mode = SqliteOpenMode.ReadOnly }.ToString();
        using var c = new SqliteConnection(cs);
        c.Open();
        using var gzip = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true);
        var writer = new LineWriter(gzip);

        var snapshots = Rows(c, "SELECT id, kind, started_at, finished_at FROM market_snapshot WHERE finished_at IS NOT NULL ORDER BY id",
            r => (Id: r.GetInt64(0), Kind: r.GetString(1), Started: r.GetString(2), Finished: r.GetString(3)));
        var latest = snapshots.Count > 0 ? snapshots[^1].Id : -1;
        var days = Table(c, "price_history") ? Rows(c, "SELECT DISTINCT day FROM price_history ORDER BY day", r => r.GetString(0)) : [];

        writer.Line(w =>
        {
            w.WriteString("t", "meta");
            w.WriteNumber("format", FormatVersion);
            w.WriteString("exportedAt", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            w.WriteNumber("latestSnapshot", latest);
            w.WriteStartArray("grades");
            foreach (var g in keep.Order()) w.WriteNumberValue(g);
            w.WriteEndArray();
            w.WriteStartArray("snapshots");
            foreach (var s in snapshots)
            {
                w.WriteStartObject();
                w.WriteNumber("id", s.Id);
                w.WriteString("kind", s.Kind);
                w.WriteString("startedAt", s.Started);
                w.WriteString("finishedAt", s.Finished);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteStartArray("historyDays");
            foreach (var d in days) w.WriteStringValue(d);
            w.WriteEndArray();
            w.WriteStartArray("patches");
            if (Table(c, "market_patch_baseline"))
                foreach (var (at, name, snap, day) in Rows(c, "SELECT patch_at, name, snapshot, history_day FROM market_patch_baseline ORDER BY patch_at",
                             r => (r.GetString(0), r.GetString(1), r.IsDBNull(2) ? (long?)null : r.GetInt64(2), r.IsDBNull(3) ? null : r.GetString(3))))
                {
                    w.WriteStartObject();
                    w.WriteString("patchAt", at);
                    w.WriteString("name", name);
                    if (snap is { } sn) w.WriteNumber("snapshot", sn); else w.WriteNull("snapshot");
                    w.WriteString("historyDay", day);
                    w.WriteEndObject();
                }
            w.WriteEndArray();
            w.WriteStartArray("seasons");
            if (Table(c, "market_season"))
                foreach (var (id, name, first) in Rows(c, "SELECT season_id, name, first_seen FROM market_season ORDER BY season_id",
                             r => (r.GetInt64(0), r.GetString(1), r.GetString(2))))
                {
                    w.WriteStartObject();
                    w.WriteNumber("id", id);
                    w.WriteString("name", name);
                    w.WriteString("firstSeen", first);
                    w.WriteEndObject();
                }
            w.WriteEndArray();
        });

        // Cards: one line per (snapshot, card) with the position groups it is listed under. Stats, positions and
        // ratings only for the latest snapshot; older ones keep what a before/after comparison needs.
        var cards = 0;
        foreach (var snap in snapshots)
        {
            var full = snap.Id == latest;
            var tags = new Dictionary<long, SortedSet<string>>();
            foreach (var (spId, tag) in Rows(c, "SELECT sp_id, tag FROM market_tag WHERE snapshot = $s", r => (r.GetInt64(0), r.GetString(1)), ("$s", snap.Id)))
            {
                if (!tags.TryGetValue(spId, out var set)) tags[spId] = set = new SortedSet<string>(StringComparer.Ordinal);
                set.Add(tag);
            }
            var rows = Rows(c, """
                SELECT sp_id, grp, name, season, pay, ovr1, weak_foot, rating, rating_count, prices, stats, positions
                FROM market_card WHERE snapshot = $s ORDER BY sp_id, grp
                """, r => (SpId: r.GetInt64(0), Group: r.GetString(1), Name: r.GetString(2), Season: r.GetString(3), Pay: r.GetInt32(4),
                    Ovr1: r.GetInt32(5), WeakFoot: r.GetInt32(6), Rating: r.IsDBNull(7) ? (double?)null : r.GetDouble(7), RatingCount: r.GetInt32(8),
                    Prices: r.GetString(9), Stats: r.GetString(10), Positions: r.GetString(11)), ("$s", snap.Id));
            foreach (var card in rows.GroupBy(x => x.SpId))
            {
                var first = card.First();
                cards++;
                writer.Line(w =>
                {
                    w.WriteString("t", "card");
                    w.WriteNumber("snapshot", snap.Id);
                    w.WriteNumber("spId", first.SpId);
                    w.WriteString("name", first.Name);
                    w.WriteString("season", first.Season);
                    w.WriteNumber("pay", first.Pay);
                    w.WriteNumber("ovr1", first.Ovr1);
                    w.WriteNumber("weakFoot", first.WeakFoot);
                    w.WriteStartArray("groups");
                    foreach (var g in card.Select(x => x.Group).Distinct()) w.WriteStringValue(g);
                    w.WriteEndArray();
                    w.WriteStartArray("tags");
                    foreach (var t in tags.GetValueOrDefault(first.SpId) ?? []) w.WriteStringValue(t);
                    w.WriteEndArray();
                    WritePrices(w, "prices", first.Prices, keep);
                    if (full)
                    {
                        if (first.Rating is { } rating) w.WriteNumber("rating", rating); else w.WriteNull("rating");
                        w.WriteNumber("ratingCount", first.RatingCount);
                        WriteRaw(w, "stats", MergeMaps(card.Select(x => x.Stats)));
                        WriteRaw(w, "positions", MergeMaps(card.Select(x => x.Positions)));
                    }
                });
            }
        }

        // Daily prices per card (prices do not depend on the position group, so one series per card). Streamed card by
        // card; the history keeps only MarketStore.HistoryGrades, so missing grades are simply left out.
        var series = 0;
        if (days.Count > 0)
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT sp_id, day, prices FROM price_history ORDER BY sp_id, day, grp";
            using var r = cmd.ExecuteReader();
            long current = -1;
            var byDay = new SortedDictionary<string, Dictionary<int, long>>(StringComparer.Ordinal);
            void Flush()
            {
                if (byDay.Count == 0) return;
                series++;
                var spId = current;
                var gradesHere = byDay.Values.SelectMany(p => p.Keys).Where(keep.Contains).Distinct().Order().ToList();
                writer.Line(w =>
                {
                    w.WriteString("t", "series");
                    w.WriteNumber("spId", spId);
                    w.WriteStartArray("days");
                    foreach (var d in byDay.Keys) w.WriteStringValue(d);
                    w.WriteEndArray();
                    w.WriteStartObject("p");
                    foreach (var g in gradesHere)
                    {
                        w.WriteStartArray(g.ToString(CultureInfo.InvariantCulture));
                        foreach (var prices in byDay.Values)
                        {
                            if (prices.TryGetValue(g, out var price)) w.WriteNumberValue(price); else w.WriteNullValue();
                        }
                        w.WriteEndArray();
                    }
                    w.WriteEndObject();
                });
                byDay.Clear();
            }
            while (r.Read())
            {
                var spId = r.GetInt64(0);
                if (spId != current) { Flush(); current = spId; }
                var day = r.GetString(1);
                if (!byDay.ContainsKey(day)) byDay[day] = JsonSerializer.Deserialize<Dictionary<int, long>>(r.GetString(2)) ?? [];
            }
            Flush();
        }

        var colors = 0;
        if (Table(c, "team_color"))
        {
            var members = new Dictionary<long, List<long>>();
            foreach (var (id, spId) in Rows(c, "SELECT team_color, sp_id FROM team_color_member ORDER BY team_color, sp_id", r => (r.GetInt64(0), r.GetInt64(1))))
            {
                if (!members.TryGetValue(id, out var list)) members[id] = list = [];
                list.Add(spId);
            }
            foreach (var (id, name, kind, max) in Rows(c, "SELECT id, name, kind, max_members FROM team_color ORDER BY id",
                         r => (r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetInt32(3))))
            {
                colors++;
                writer.Line(w =>
                {
                    w.WriteString("t", "teamColor");
                    w.WriteNumber("id", id);
                    w.WriteString("name", name);
                    w.WriteString("kind", kind);
                    w.WriteNumber("maxMembers", max);
                    w.WriteStartArray("members");
                    foreach (var m in members.GetValueOrDefault(id) ?? []) w.WriteNumberValue(m);
                    w.WriteEndArray();
                });
            }
        }

        // Ranker usage: aggregates only (cards, formations, team colours). RankerChartData holds no nicknames.
        var charts = 0;
        if (Table(c, "ranker_chart"))
        {
            foreach (var (date, from, to, kind, json) in Rows(c, "SELECT chart_date, rank_from, rank_to, kind, json FROM ranker_chart ORDER BY chart_date, rank_from",
                         r => (r.GetString(0), r.GetInt32(1), r.GetInt32(2), r.GetString(3), r.GetString(4))))
            {
                RankerChartData? chart;
                try { chart = JsonSerializer.Deserialize<RankerChartData>(json); }
                catch (JsonException) { continue; }
                if (chart is null) continue;
                charts++;
                writer.Line(w =>
                {
                    w.WriteString("t", "ranker");
                    w.WriteString("chartDate", date);
                    w.WriteNumber("rankFrom", from);
                    w.WriteNumber("rankTo", to);
                    w.WriteString("kind", kind);
                    w.WriteStartArray("picks");
                    foreach (var p in chart.Picks)
                    {
                        w.WriteStartObject();
                        w.WriteNumber("spId", p.SpId);
                        w.WriteString("position", p.Position);
                        w.WriteNumber("grade", p.Grade);
                        w.WriteNumber("ovr", p.Ovr);
                        w.WriteNumber("users", p.Users);
                        w.WriteNumber("share", p.Share);
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                    w.WriteStartArray("teamColors");
                    foreach (var t in chart.TeamColors)
                    {
                        w.WriteStartObject();
                        w.WriteNumber("id", t.Id);
                        w.WriteString("name", t.Name);
                        w.WriteNumber("users", t.Users);
                        w.WriteNumber("share", t.Share);
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                    w.WriteStartArray("formations");
                    foreach (var f in chart.Formations)
                    {
                        w.WriteStartObject();
                        w.WriteString("formation", f.Formation);
                        w.WriteNumber("users", f.Users);
                        w.WriteNumber("share", f.Share);
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                });
            }
        }

        gzip.Flush();
        return new MarketExportSummary(snapshots.Count, cards, series, days.Count, colors, charts, days.FirstOrDefault(), days.LastOrDefault());
    }

    private static void WritePrices(Utf8JsonWriter w, string name, string json, HashSet<int> keep)
    {
        w.WriteStartObject(name);
        foreach (var (grade, price) in (JsonSerializer.Deserialize<Dictionary<int, long>>(json) ?? []).Where(kv => keep.Contains(kv.Key)).OrderBy(kv => kv.Key))
            w.WriteNumber(grade.ToString(CultureInfo.InvariantCulture), price);
        w.WriteEndObject();
    }

    private static Dictionary<string, int> MergeMaps(IEnumerable<string> jsons)
    {
        var merged = new Dictionary<string, int>();
        foreach (var json in jsons)
            foreach (var (k, v) in JsonSerializer.Deserialize<Dictionary<string, int>>(json) ?? []) merged.TryAdd(k, v);
        return merged;
    }

    private static void WriteRaw(Utf8JsonWriter w, string name, Dictionary<string, int> map)
    {
        w.WriteStartObject(name);
        foreach (var (k, v) in map.OrderBy(kv => kv.Key, StringComparer.Ordinal)) w.WriteNumber(k, v);
        w.WriteEndObject();
    }

    private static bool Table(SqliteConnection c, string name)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $n";
        cmd.Parameters.AddWithValue("$n", name);
        return cmd.ExecuteScalar() is not null;
    }

    private static List<T> Rows<T>(SqliteConnection c, string sql, Func<SqliteDataReader, T> map, params (string Name, object Value)[] args)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v);
        using var r = cmd.ExecuteReader();
        var list = new List<T>();
        while (r.Read()) list.Add(map(r));
        return list;
    }

    /// <summary>One JSON object per line.</summary>
    private sealed class LineWriter(Stream stream)
    {
        private static readonly byte[] NewLine = "\n"u8.ToArray();

        public void Line(Action<Utf8JsonWriter> body)
        {
            using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            {
                w.WriteStartObject();
                body(w);
                w.WriteEndObject();
            }
            stream.Write(NewLine);
        }
    }
}
