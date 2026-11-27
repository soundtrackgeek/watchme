using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Watchme.Core;

namespace Watchme.Agent.Scrapers;

/// <summary>
/// Reads Capsule's journal database (<c>capsule.db</c>, read-only):
/// <list type="bullet">
/// <item>capsule/entry — a new journal entry, from <c>entries</c>: words, mood, tags, its title or
/// an excerpt, place and weather, photos, and the thread it continues</item>
/// <item>capsule/edit — a save that changed an entry, from <c>history</c>, which keeps the state
/// before each save. It is compared with the state after it, so saves that changed nothing are left out</item>
/// <item>capsule/delete — a deleted entry, from <c>history</c></item>
/// </list>
/// Capsule renumbers its entries, so they are known by uuid, never by id. Its times are local.
/// Hidden entries are recorded without their title or text.
/// </summary>
internal sealed class CapsuleScraper(Func<string> dbPath, int backfillDays) : IScraper
{
    private const string Source = EventSources.Capsule;
    internal const string App = DailyReport.JournalApp;

    private static readonly JsonSerializerOptions Json = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public string Name => "capsule";
    public TimeSpan Interval => TimeSpan.FromSeconds(30);

    public CapsuleScraper(string dbPath, int backfillDays) : this(() => dbPath, backfillDays)
    {
    }

    /// <summary>
    /// The journal Capsule uses: <c>databasePath</c> in its path settings
    /// (<c>%APPDATA%\Capsule\path_settings.json</c>), else <c>%USERPROFILE%\.capsule\capsule.db</c>.
    /// Only that one setting is read.
    /// </summary>
    internal static string DbPathFromSettings(string settingsPath, string fallback)
    {
        try
        {
            if (File.Exists(settingsPath)
                && JsonNode.Parse(File.ReadAllText(settingsPath)) is JsonObject settings
                && settings["databasePath"] is JsonValue v && v.ToString() is { Length: > 0 } path)
                return Environment.ExpandEnvironmentVariables(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
        }
        return fallback;
    }

    public IReadOnlyList<WatchEvent> Scrape(ScrapeContext context)
    {
        var path = dbPath();
        if (!File.Exists(path))
        {
            context.Warn($"no journal at {path}");
            return [];
        }
        var since = context.Now - backfillDays * 24L * 60 * 60 * 1000;
        using var db = AuroraScraper.OpenReadOnly(path);
        var events = ReadEntries(db, context, since);
        if (Exists(db, "history"))
            events.AddRange(ReadHistory(db, context, since));
        return events;
    }

    /// <summary>An entry as it was at some point: from a history snapshot or the entries table.</summary>
    internal sealed record EntryState(string Text, string? Title, string? Mood, IReadOnlyList<string> Tags, bool Hidden)
    {
        public int Words => JournalText.Words(Text);

        public string Label => JournalText.Label(Title, Text, Hidden);
    }

    // ---- New entries ----

    private static List<WatchEvent> ReadEntries(SqliteConnection db, ScrapeContext context, long since)
    {
        // Ids change when Capsule renumbers, so entries are re-read from a day before the newest
        // one seen (an entry can be dated a little back) and told apart by uuid.
        var from = context.Cursor(Source, "entry") is { } cursor ? Math.Max(since, cursor - 24L * 60 * 60 * 1000) : since;
        var locations = Exists(db, "plugin_entry_locations");
        var media = Exists(db, "plugin_entry_media");
        var threads = Exists(db, "entry_continuations");
        var threadTitles = threads && Exists(db, "entry_thread_titles");

        using var cmd = db.CreateCommand();
        cmd.CommandText = $"""
            SELECT e.uuid, e.created_at, e.text, e.title, e.mood, COALESCE(e.hidden, 0), COALESCE(e.starred, 0),
                   (SELECT json_group_array(t.name) FROM entry_tags et JOIN tags t ON t.id = et.tag_id WHERE et.entry_id = e.id),
                   {(locations ? "l.place_name, l.weather_condition, l.weather_temp_c" : "NULL, NULL, NULL")},
                   {(media ? "(SELECT COUNT(*) FROM plugin_entry_media m WHERE m.entry_uuid = e.uuid)" : "0")},
                   {(threads ? "c.parent_entry_uuid" : "NULL")},
                   {(threadTitles ? "(SELECT title FROM entry_thread_titles WHERE thread_root_uuid = c.parent_entry_uuid)" : "NULL")}
            FROM entries e
            {(locations ? "LEFT JOIN plugin_entry_locations l ON l.entry_uuid = e.uuid" : "")}
            {(threads ? "LEFT JOIN entry_continuations c ON c.child_entry_uuid = e.uuid" : "")}
            WHERE e.uuid IS NOT NULL AND e.created_at >= $from
            ORDER BY e.created_at;
            """;
        cmd.Parameters.AddWithValue("$from", LocalText(from));

        var events = new List<WatchEvent>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            string? Text(int i) => r.IsDBNull(i) ? null : r.GetString(i);
            if (ParseLocal(r.GetString(1)) is not { } ts || ts < since)
                continue;
            var uuid = r.GetString(0);
            var entry = new EntryState(Text(2) ?? "", Text(3), Text(4), Tags(Text(7)), r.GetInt64(5) != 0);
            var photos = r.GetInt64(11);
            var thread = entry.Hidden ? null : Text(13);

            var data = StateData(uuid, entry);
            if (r.GetInt64(6) != 0)
                data["starred"] = true;
            if (Text(8) is { } place)
                data["place"] = place;
            if (Text(9) is { } weather)
                data["weather"] = weather;
            if (!r.IsDBNull(10))
                data["temp_c"] = Math.Round(r.GetDouble(10), 1);
            if (photos > 0)
                data["photos"] = photos;
            if (Text(12) is { } parent)
                data["continues"] = parent;
            if (thread is not null)
                data["thread"] = thread;

            events.Add(new WatchEvent(ts, Source, "entry")
            {
                App = App,
                Title = DescribeEntry(entry, photos, thread),
                Data = data.ToJsonString(Json),
                ExtId = $"entry:{uuid}",
            });
        }
        return events;
    }

    /// <summary>"Wrote “So I'm a big fan of…” · 86 words · disappointed · #food #soda · 2 photos".</summary>
    internal static string DescribeEntry(EntryState entry, long photos = 0, string? thread = null)
    {
        var parts = new List<string> { $"Wrote {entry.Label}", Plural(entry.Words, "word") };
        if (entry.Mood is { Length: > 0 } mood)
            parts.Add(mood);
        if (entry.Tags.Count > 0)
            parts.Add(string.Join(" ", entry.Tags.Select(t => "#" + t)));
        if (photos > 0)
            parts.Add(Plural(photos, "photo"));
        if (thread is not null)
            parts.Add($"continues “{thread}”");
        return string.Join(" · ", parts);
    }

    // ---- Edits and deletions ----

    private static List<WatchEvent> ReadHistory(SqliteConnection db, ScrapeContext context, long since)
    {
        // History rows are only appended (AUTOINCREMENT), so their id is the cursor. Undone edits
        // are left out: Capsule deletes them as soon as you make another one.
        var cursor = context.Cursor(Source, null, "history_id");
        var rows = new List<(long Id, string Timestamp, string Op, JsonObject Old)>();
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = $"""
                SELECT id, timestamp, operation_type, old_data FROM history
                WHERE COALESCE(undone, 0) = 0 AND {(cursor is null ? "timestamp >= $since" : "id > $cursor")}
                ORDER BY id;
                """;
            cmd.Parameters.AddWithValue("$since", LocalText(since));
            cmd.Parameters.AddWithValue("$cursor", cursor ?? 0);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                if (ParseJson(r.GetString(3)) is { } old && Str(old, "uuid") is not null)
                    rows.Add((r.GetInt64(0), r.GetString(1), r.GetString(2), old));
            }
        }

        var events = new List<WatchEvent>();
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var uuid = Str(row.Old, "uuid")!;
            if (ParseLocal(row.Timestamp) is not { } ts)
                continue;

            if (row.Op == "DELETE")
            {
                var gone = State(row.Old);
                var data = StateData(uuid, gone);
                data["history_id"] = row.Id;
                events.Add(new WatchEvent(ts, Source, "delete")
                {
                    App = App,
                    Title = $"Deleted {gone.Label} ({Plural(gone.Words, "word")})",
                    Data = data.ToJsonString(Json),
                    ExtId = $"delete:{row.Id}",
                });
                continue;
            }
            if (!row.Op.StartsWith("EDIT_", StringComparison.Ordinal))
                continue;

            // One save writes a row per part it saves (text, mood, tags), each with the state
            // before that part changed; the first one has the state before the whole save.
            var j = i;
            while (j + 1 < rows.Count && rows[j + 1].Op.StartsWith("EDIT_", StringComparison.Ordinal)
                   && rows[j + 1].Timestamp == row.Timestamp && Str(rows[j + 1].Old, "uuid") == uuid)
                j++;
            var lastId = rows[j].Id;
            i = j;

            if (StateAfter(db, uuid, lastId) is not { } after)
                continue;
            var before = State(row.Old);
            var changes = Changes(before, after);
            if (changes.Count == 0)
                continue;

            var edit = StateData(uuid, after);
            edit["history_id"] = lastId;
            edit["changes"] = new JsonArray(changes.Select(c => (JsonNode?)c.Field).Distinct().ToArray());
            edit["words_before"] = before.Words;
            edit["mood_before"] = before.Mood;
            events.Add(new WatchEvent(ts, Source, "edit")
            {
                App = App,
                Title = $"Edited {after.Label}: {string.Join(", ", changes.Select(c => c.Text))}",
                Data = edit.ToJsonString(Json),
                ExtId = $"edit:{row.Id}",
            });
        }
        return events;
    }

    /// <summary>
    /// The state an entry was left in by the save whose last history row is <paramref name="lastId"/>:
    /// the next save's snapshot, or the entry as it is now. Null when the entry is gone without a trace.
    /// </summary>
    private static EntryState? StateAfter(SqliteConnection db, string uuid, long lastId)
    {
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = """
                SELECT old_data FROM history
                WHERE id > $id AND json_extract(old_data, '$.uuid') = $uuid
                ORDER BY id LIMIT 1;
                """;
            cmd.Parameters.AddWithValue("$id", lastId);
            cmd.Parameters.AddWithValue("$uuid", uuid);
            if (cmd.ExecuteScalar() is string next && ParseJson(next) is { } snapshot)
                return State(snapshot);
        }
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = """
                SELECT e.text, e.title, e.mood, COALESCE(e.hidden, 0),
                       (SELECT json_group_array(t.name) FROM entry_tags et JOIN tags t ON t.id = et.tag_id WHERE et.entry_id = e.id)
                FROM entries e WHERE e.uuid = $uuid;
                """;
            cmd.Parameters.AddWithValue("$uuid", uuid);
            using var r = cmd.ExecuteReader();
            if (!r.Read())
                return null;
            return new EntryState(r.IsDBNull(0) ? "" : r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1),
                r.IsDBNull(2) ? null : r.GetString(2), Tags(r.IsDBNull(4) ? null : r.GetString(4)), r.GetInt64(3) != 0);
        }
    }

    /// <summary>What a save changed, in words: "+12 words", "mood ok → good", "tags +work −ai", "title “Monday”".</summary>
    internal static List<(string Field, string Text)> Changes(EntryState before, EntryState after)
    {
        var changes = new List<(string, string)>();
        if (before.Text != after.Text)
        {
            var delta = after.Words - before.Words;
            changes.Add(("text", delta switch
            {
                > 0 => $"+{Plural(delta, "word")}",
                < 0 => $"−{Plural(-delta, "word")}",
                _ => "reworded",
            }));
        }
        if ((before.Title ?? "") != (after.Title ?? ""))
            changes.Add(("title", string.IsNullOrWhiteSpace(after.Title) || after.Hidden ? "removed the title" : $"title “{after.Title.Trim()}”"));
        if ((before.Mood ?? "") != (after.Mood ?? ""))
        {
            changes.Add(("mood", (before.Mood, after.Mood) switch
            {
                (null or "", var now) => $"mood {now}",
                (var was, null or "") => $"removed mood {was}",
                var (was, now) => $"mood {was} → {now}",
            }));
        }
        var added = after.Tags.Except(before.Tags, StringComparer.OrdinalIgnoreCase).ToList();
        var removed = before.Tags.Except(after.Tags, StringComparer.OrdinalIgnoreCase).ToList();
        if (added.Count + removed.Count > 0)
            changes.Add(("tags", "tags " + string.Join(" ", added.Select(t => "+" + t).Concat(removed.Select(t => "−" + t)))));
        if (before.Hidden != after.Hidden)
            changes.Add(("hidden", after.Hidden ? "hid it" : "unhid it"));
        return changes;
    }

    // ---- Helpers ----

    /// <summary>The fields every Capsule event carries about the entry; text only when it isn't hidden.</summary>
    private static JsonObject StateData(string uuid, EntryState entry)
    {
        var data = new JsonObject
        {
            ["uuid"] = uuid,
            ["words"] = entry.Words,
            ["mood"] = entry.Mood,
            ["tags"] = new JsonArray(entry.Tags.Select(t => (JsonNode?)t).ToArray()),
        };
        if (entry.Hidden)
            data["hidden"] = true;
        else
        {
            if (!string.IsNullOrWhiteSpace(entry.Title))
                data["title"] = entry.Title.Trim();
            data["excerpt"] = JournalText.Excerpt(entry.Text, 200);
        }
        return data;
    }

    private static EntryState State(JsonObject snapshot)
    {
        var tags = snapshot["tags"] is JsonArray a
            ? a.Select(t => t is JsonValue v ? v.ToString() : t?["name"]?.ToString()).OfType<string>().ToList()
            : [];
        return new EntryState(Str(snapshot, "text") ?? "", Str(snapshot, "title"), Str(snapshot, "mood"), tags,
            snapshot["hidden"] is JsonValue h && (h.TryGetValue<bool>(out var b) ? b : h.TryGetValue<int>(out var n) && n != 0));
    }

    private static List<string> Tags(string? json) =>
        json is null ? [] : JsonNode.Parse(json) is JsonArray a ? a.Select(t => t?.ToString()).OfType<string>().ToList() : [];

    private static string Plural(long n, string word) => n == 1 ? $"1 {word}" : $"{n} {word}s";

    private static string? Str(JsonObject o, string key) => o[key] is JsonValue v ? v.ToString() : null;

    private static JsonObject? ParseJson(string json)
    {
        try { return JsonNode.Parse(json) as JsonObject; }
        catch (JsonException) { return null; }
    }

    private static bool Exists(SqliteConnection db, string table)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $n;";
        cmd.Parameters.AddWithValue("$n", table);
        return cmd.ExecuteScalar() is not null;
    }

    private static readonly string[] LocalFormats =
        ["yyyy-MM-dd HH:mm", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm:ss.FFFFFFF", "yyyy-MM-dd'T'HH:mm", "yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF"];

    /// <summary>Capsule's local "2026-11-27 11:25" (or with seconds) as Unix milliseconds.</summary>
    internal static long? ParseLocal(string text) =>
        DateTime.TryParseExact(text.Trim(), LocalFormats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var t)
            ? new DateTimeOffset(t).ToUnixTimeMilliseconds()
            : null;

    /// <summary>Unix milliseconds as Capsule writes times, to compare with its columns as text.</summary>
    internal static string LocalText(long ms) =>
        DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
}
