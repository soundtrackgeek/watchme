using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace Watchme.Core;

/// <summary>
/// <c>watchme forget</c>: takes events out after the fact, for when pausing was forgotten.
/// <para>
/// Removing rows is not enough on its own. Scrapers find where they left off from Watchme's own
/// database, so a removed Edge visit or PowerShell command would be read again from the app's
/// history on the next run. A removed scraped event therefore leaves its <c>(source, ext_id)</c> in
/// <c>forgotten</c>, which <see cref="Database.Insert"/> skips. A forgotten stretch of time also goes
/// in <c>forgotten_windows</c>, so what apps logged in it but Watchme hasn't read yet (the agent
/// was off, or the app writes its history late) is left out, or redacted, when it is read.
/// </para>
/// <para>
/// Focus time is the gap between one state event and the next (see <c>focus_spans</c>), so
/// removing a focus event would hand its time to the window before it. Forgetting a stretch of
/// time instead writes a <c>session/forget</c> event at its start, which ends the span there, and
/// repeats the state in effect at its end, so the time after it still counts. Forgetting by
/// words only blanks the titles of focus events and keeps their time.
/// </para>
/// </summary>
public static partial class Forgetting
{
    public const string Redacted = "[redacted]";

    /// <summary>Sources that hold nothing private and give focus time its shape; never forgotten.</summary>
    private const string Forgettable = "source NOT IN ('session', 'agent', 'idle')";

    private static readonly string[] StateSources = [EventSources.Focus, EventSources.Idle, EventSources.Session, EventSources.Agent];

    public enum Action { Remove, Redact }

    /// <summary>
    /// What to forget: a stretch of time (no <see cref="Terms"/>), the events in it that hold every
    /// term, or one event. <see cref="Source"/> keeps one source only.
    /// </summary>
    public sealed record Selection(long? FromMs, long? ToMs, IReadOnlyList<string> Terms, string? Source = null, long? EventId = null)
    {
        /// <summary>A stretch of time, every event in it: what apps log in it later is left out too.</summary>
        public bool IsWindow => FromMs is not null && ToMs is not null && Terms.Count == 0 && EventId is null;

        /// <summary>A window over every source, which also takes the time out of focus time.</summary>
        public bool IsWholeWindow => IsWindow && Source is null;
    }

    public sealed record Item(long Id, long Ts, string Source, string Kind, string? App, string? Title, string? Data, string? ExtId, Action Action);

    public sealed record Plan(Selection Selection, bool Redact, List<Item> Items)
    {
        public int Removed => Items.Count(i => i.Action == Action.Remove);
        public int RedactedCount => Items.Count(i => i.Action == Action.Redact);

        public List<(string Source, int Removed, int Redacted)> BySource() =>
            Items.GroupBy(i => i.Source)
                .Select(g => (g.Key, g.Count(i => i.Action == Action.Remove), g.Count(i => i.Action == Action.Redact)))
                .OrderByDescending(x => x.Item2 + x.Item3)
                .ToList();
    }

    /// <summary>A forgotten (or, with <see cref="Redact"/>, redacted) stretch, for one source or all.</summary>
    public sealed record Window(long Start, long End, bool Redact, string? Source)
    {
        public bool Covers(WatchEvent e) => e.Ts >= Start && e.Ts < End && (Source is null || Source == e.Source);
    }

    /// <summary>What forgetting <paramref name="selection"/> would do; nothing is changed.</summary>
    public static Plan Preview(Database db, Selection selection, bool redact)
    {
        using var cmd = db.Connection.CreateCommand();
        var where = new List<string> { Forgettable };
        if (selection.FromMs is { } from)
        {
            where.Add("ts >= $from");
            cmd.Parameters.AddWithValue("$from", from);
        }
        if (selection.ToMs is { } to)
        {
            where.Add("ts < $to");
            cmd.Parameters.AddWithValue("$to", to);
        }
        if (selection.Source is { Length: > 0 } source)
        {
            where.Add("source = $source");
            cmd.Parameters.AddWithValue("$source", source);
        }
        if (selection.EventId is { } id)
        {
            where.Add("id = $id");
            cmd.Parameters.AddWithValue("$id", id);
        }
        var terms = selection.Terms.Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
        for (var i = 0; i < terms.Count; i++)
        {
            // Only a focus event's title is its own; its app is just which program had focus.
            where.Add($"""
                ((source = 'focus' AND title LIKE $q{i} ESCAPE '\')
                 OR (source <> 'focus' AND (title LIKE $q{i} ESCAPE '\' OR app LIKE $q{i} ESCAPE '\' OR data LIKE $q{i} ESCAPE '\')))
                """);
            cmd.Parameters.AddWithValue($"$q{i}", EventSearch.ContainsPattern(terms[i]));
        }
        cmd.CommandText = $"SELECT id, ts, source, kind, app, title, data, ext_id FROM events WHERE {string.Join(" AND ", where)} ORDER BY ts, id;";

        var items = new List<Item>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var src = r.GetString(2);
            var title = r.IsDBNull(5) ? null : r.GetString(5);
            var data = r.IsDBNull(6) ? null : r.GetString(6);
            // Only a whole window can take focus time out (it writes the events that keep the
            // spans around it right); anywhere else a focus event keeps its time and loses its title.
            var action = redact || (src == EventSources.Focus && !selection.IsWholeWindow) ? Action.Redact : Action.Remove;
            // Nothing left to redact: leave it out of the counts.
            if (action == Action.Redact && title is null or Redacted && RedactData(data) == data)
                continue;
            items.Add(new Item(r.GetInt64(0), r.GetInt64(1), src, r.GetString(3),
                r.IsDBNull(4) ? null : r.GetString(4), title, data, r.IsDBNull(7) ? null : r.GetString(7), action));
        }
        return new Plan(selection, redact, items);
    }

    /// <summary>Forgets what <see cref="Preview"/> lists, in one transaction.</summary>
    public static Plan Apply(Database db, Selection selection, bool redact, long now)
    {
        var plan = Preview(db, selection, redact);
        using var tx = db.Connection.BeginTransaction();

        // Read before anything is removed: the state the window ends in.
        var restore = selection.IsWholeWindow && !redact ? LatestState(db, tx, selection.ToMs!.Value) : null;

        foreach (var item in plan.Items)
        {
            if (item.Action == Action.Remove)
            {
                if (item.ExtId is not null)
                    Run(db, tx, "INSERT OR IGNORE INTO forgotten (source, ext_id) VALUES ($s, $e);", ("$s", item.Source), ("$e", item.ExtId));
                Run(db, tx, "DELETE FROM events WHERE id = $id;", ("$id", item.Id));
            }
            else
            {
                Run(db, tx, "UPDATE events SET title = $t, data = $d WHERE id = $id;",
                    ("$t", item.Title is null ? DBNull.Value : Redacted), ("$d", (object?)RedactData(item.Data) ?? DBNull.Value), ("$id", item.Id));
            }
        }

        if (selection.IsWindow)
        {
            var (from, to) = (selection.FromMs!.Value, selection.ToMs!.Value);
            Run(db, tx, """
                INSERT INTO forgotten_windows (start_ts, end_ts, redact, created, source) VALUES ($f, $t, $r, $now, $s);
                """, ("$f", from), ("$t", to), ("$r", redact ? 1 : 0), ("$now", now), ("$s", (object?)selection.Source ?? DBNull.Value));

            if (selection.IsWholeWindow && !redact)
            {
                // Ends the focus span that was running when the window began...
                InsertEvent(db, tx, new WatchEvent(from, EventSources.Session, "forget") { Data = JsonSerializer.Serialize(new { until = to }) });
                // ...and picks it up again where the window ends. A title from inside the window stays forgotten.
                if (restore is { Source: EventSources.Focus } focus && to <= now)
                    InsertEvent(db, tx, focus with { Ts = to, Kind = "foreground", Title = focus.Ts < from ? focus.Title : null, Data = null, ExtId = null });
            }
        }
        tx.Commit();
        return plan;
    }

    /// <summary>The last state event (focus, idle, session, agent) before <paramref name="ts"/>.</summary>
    private static WatchEvent? LatestState(Database db, SqliteTransaction tx, long ts)
    {
        using var cmd = db.Connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"""
            SELECT ts, source, kind, app, pid, exe, title FROM events
            WHERE source IN ({string.Join(", ", StateSources.Select(s => $"'{s}'"))}) AND ts < $ts
            ORDER BY ts DESC, id DESC LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$ts", ts);
        using var r = cmd.ExecuteReader();
        if (!r.Read())
            return null;
        return new WatchEvent(r.GetInt64(0), r.GetString(1), r.GetString(2))
        {
            App = r.IsDBNull(3) ? null : r.GetString(3),
            Pid = r.IsDBNull(4) ? null : r.GetInt32(4),
            Exe = r.IsDBNull(5) ? null : r.GetString(5),
            Title = r.IsDBNull(6) ? null : r.GetString(6),
        };
    }

    private static void InsertEvent(Database db, SqliteTransaction tx, WatchEvent e) =>
        Run(db, tx, """
            INSERT INTO events (ts, source, kind, app, pid, exe, title, data) VALUES ($ts, $src, $kind, $app, $pid, $exe, $title, $data);
            """,
            ("$ts", e.Ts), ("$src", e.Source), ("$kind", e.Kind), ("$app", (object?)e.App ?? DBNull.Value),
            ("$pid", (object?)e.Pid ?? DBNull.Value), ("$exe", (object?)e.Exe ?? DBNull.Value),
            ("$title", (object?)e.Title ?? DBNull.Value), ("$data", (object?)e.Data ?? DBNull.Value));

    private static void Run(Database db, SqliteTransaction tx, string sql, params (string Name, object Value)[] parameters)
    {
        using var cmd = db.Connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Every forgotten or redacted stretch that overlaps <c>[fromMs, toMs)</c>.</summary>
    public static List<Window> Windows(Database db, long fromMs = 0, long toMs = long.MaxValue)
    {
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = """
            SELECT start_ts, end_ts, redact, source FROM forgotten_windows
            WHERE end_ts > $from AND start_ts < $to ORDER BY start_ts, id;
            """;
        cmd.Parameters.AddWithValue("$from", fromMs);
        cmd.Parameters.AddWithValue("$to", toMs);
        var windows = new List<Window>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            windows.Add(new Window(r.GetInt64(0), r.GetInt64(1), r.GetInt64(2) != 0, r.IsDBNull(3) ? null : r.GetString(3)));
        return windows;
    }

    /// <summary>
    /// Newly read app history through the forgotten stretches: events in a forgotten one are
    /// dropped, events in a redacted one lose their titles and text.
    /// </summary>
    public static List<WatchEvent> Filter(IEnumerable<WatchEvent> events, IReadOnlyList<Window> windows, out int dropped, out int redacted)
    {
        dropped = redacted = 0;
        var kept = new List<WatchEvent>();
        foreach (var e in events)
        {
            var hit = windows.Where(w => w.Covers(e)).ToList();
            if (hit.Any(w => !w.Redact))
                dropped++;
            else if (hit.Count > 0)
            {
                redacted++;
                kept.Add(e with { Title = e.Title is null ? null : Redacted, Data = RedactData(e.Data) });
            }
            else
                kept.Add(e);
        }
        return kept;
    }

    /// <summary>
    /// The event's JSON with every text value replaced by <c>[redacted]</c>. Numbers stay: they are
    /// durations, ratings and the ids scrapers use to know where they left off.
    /// </summary>
    public static string? RedactData(string? json)
    {
        if (string.IsNullOrEmpty(json))
            return json;
        JsonNode? node;
        try { node = JsonNode.Parse(json); }
        catch (JsonException) { return null; }
        if (node is null)
            return json;
        if (IsText(node))
            return JsonValue.Create(Redacted).ToJsonString();
        Scrub(node);
        return node.ToJsonString();

        static bool IsText(JsonNode? n) => n is JsonValue v && v.GetValueKind() == JsonValueKind.String;

        // Replaces text in place; containers keep their place in the tree.
        static void Scrub(JsonNode node)
        {
            if (node is JsonObject o)
            {
                foreach (var key in o.Select(p => p.Key).ToList())
                {
                    if (IsText(o[key]))
                        o[key] = Redacted;
                    else if (o[key] is { } child)
                        Scrub(child);
                }
            }
            else if (node is JsonArray a)
            {
                for (var i = 0; i < a.Count; i++)
                {
                    if (IsText(a[i]))
                        a[i] = Redacted;
                    else if (a[i] is { } child)
                        Scrub(child);
                }
            }
        }
    }

    /// <summary>
    /// The stretch to forget: <c>15m</c>, <c>2h</c>, <c>1h30m</c> or <c>45</c> (minutes) up to now,
    /// or <c>14:10..14:35</c> on <paramref name="day"/> (today by default), where the end may be
    /// <c>now</c> or <c>24:00</c>. The end is never later than now.
    /// </summary>
    public static bool TryParseWindow(string text, DateTime nowLocal, DateOnly? day, out long fromMs, out long toMs, out string? error)
    {
        fromMs = toMs = 0;
        error = null;
        var s = text.Trim().ToLowerInvariant();
        var nowMs = new DateTimeOffset(nowLocal).ToUnixTimeMilliseconds();

        var dots = s.IndexOf("..", StringComparison.Ordinal);
        if (dots >= 0)
        {
            var date = day ?? DateOnly.FromDateTime(nowLocal);
            if (!TryClock(s[..dots], date, out var start) || !TryClock(s[(dots + 2)..], date, out var end, allowNow: true, nowMs))
            {
                error = $"Expected times like 14:10..14:35 (or 14:10..now); got '{text}'.";
                return false;
            }
            (fromMs, toMs) = (start, Math.Min(end, nowMs));
        }
        else
        {
            if (day is not null)
            {
                error = "--day goes with a time range like 14:10..14:35.";
                return false;
            }
            if (!TryDuration(s, out var duration))
            {
                error = $"Expected how long ago (15m, 2h, 1h30m) or times (14:10..14:35); got '{text}'.";
                return false;
            }
            (fromMs, toMs) = (nowMs - (long)duration.TotalMilliseconds, nowMs);
        }

        if (fromMs >= nowMs)
        {
            error = "That hasn't happened yet.";
            return false;
        }
        if (toMs <= fromMs)
        {
            error = "The end has to come after the start.";
            return false;
        }
        return true;
    }

    private static bool TryClock(string s, DateOnly date, out long ms, bool allowNow = false, long nowMs = 0)
    {
        ms = 0;
        s = s.Trim();
        if (allowNow && s == "now")
        {
            ms = nowMs;
            return true;
        }
        TimeSpan offset;
        if (s is "24:00")
            offset = TimeSpan.FromDays(1);
        else if (TimeOnly.TryParseExact(s, ["H:mm", "HH:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var t))
            offset = t.ToTimeSpan();
        else
            return false;
        ms = new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Local) + offset).ToUnixTimeMilliseconds();
        return true;
    }

    private static bool TryDuration(string s, out TimeSpan duration)
    {
        duration = TimeSpan.Zero;
        if (int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var minutes))
            duration = TimeSpan.FromMinutes(minutes);
        else if (DurationRegex().Match(s) is { Success: true } m && (m.Groups[1].Success || m.Groups[2].Success))
            duration = new TimeSpan(
                m.Groups[1].Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 0,
                m.Groups[2].Success ? int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) : 0, 0);
        else
            return false;
        return duration > TimeSpan.Zero;
    }

    [GeneratedRegex(@"^(?:(\d+)\s*h)?\s*(?:(\d+)\s*m(?:in)?)?$")]
    private static partial Regex DurationRegex();
}
