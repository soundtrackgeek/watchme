using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Watchme.Core;

namespace Watchme.Agent.Scrapers;

/// <summary>
/// Reads Aurora's own databases (read-only) and emits:
/// <list type="bullet">
/// <item>aurora/listen — a track you listened to (aurora-history.sqlite3, listening_sessions)</item>
/// <item>aurora/tag_edit — tags changed on an album, grouped per folder (aurora-state.sqlite3, tag_edit_operations)</item>
/// <item>aurora/album_added — an album imported into the library (aurora-state.sqlite3, album_additions)</item>
/// </list>
/// </summary>
internal sealed class AuroraScraper(string dataDir, int backfillDays) : IScraper
{
    private const string Source = EventSources.Aurora;
    private const string App = "aurora.exe";

    /// <summary>Listens still playing at the last scrape are picked up by re-reading this far back.</summary>
    private const long ListenLookbackMs = 6 * 60 * 60 * 1000;

    /// <summary>Tag edits on one folder this close together are one action.</summary>
    private const long EditGroupGapMs = 60 * 1000;

    /// <summary>Leave the newest edits alone until a bulk edit has surely finished.</summary>
    private const long EditSettleMs = 2 * 60 * 1000;

    private static readonly HashSet<string> FinalStatuses = ["verified", "rolledBack", "failed"];

    /// <summary>Keep æøå, accents and arrows readable in the stored JSON instead of \uXXXX escapes.</summary>
    private static readonly JsonSerializerOptions Json = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static readonly HashSet<string> NumberingFields = ["trackNumber", "trackTotal", "discNumber", "discTotal"];

    public string Name => "aurora";
    public TimeSpan Interval => TimeSpan.FromSeconds(30);

    private string HistoryDb => Path.Combine(dataDir, "aurora-history.sqlite3");
    private string StateDb => Path.Combine(dataDir, "aurora-state.sqlite3");

    public IReadOnlyList<WatchEvent> Scrape(ScrapeContext context)
    {
        var events = new List<WatchEvent>();
        var since = context.Now - backfillDays * 24L * 60 * 60 * 1000;

        // All three queries hit an index and only read new rows, so polling is cheap.
        if (File.Exists(HistoryDb))
            events.AddRange(ReadListens(HistoryDb, Source, App, context, since));
        if (File.Exists(StateDb))
        {
            events.AddRange(ReadTagEdits(context, since));
            events.AddRange(ReadAlbumAdditions(context, since));
        }
        return events;
    }

    internal static SqliteConnection OpenReadOnly(string path)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
            DefaultTimeout = 5,
        };
        var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        return connection;
    }

    // ---- Listens ----

    /// <summary>
    /// Finished listens from a database in Aurora's history format. Aurora NG keeps its own file
    /// in the same format, so this serves both.
    /// </summary>
    internal static List<WatchEvent> ReadListens(string historyDb, string source, string app, ScrapeContext context, long since)
    {
        var from = context.Cursor(source, "listen") is { } cursor ? cursor - ListenLookbackMs : since;

        using var db = OpenReadOnly(historyDb);
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT session_id, title, artist, album, genre, directory, filename, duration_seconds,
                   started_at_ms, ended_at_ms, listened_seconds, registered_play, outcome
            FROM listening_sessions
            WHERE started_at_ms >= $from AND outcome <> 'active'
            ORDER BY started_at_ms;
            """;
        cmd.Parameters.AddWithValue("$from", from);

        var events = new List<WatchEvent>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var artist = r.GetString(2);
            var title = r.GetString(1);
            var data = new JsonObject
            {
                ["artist"] = artist,
                ["track"] = title,
                ["album"] = r.GetString(3),
                ["genre"] = r.IsDBNull(4) ? null : r.GetString(4),
                ["path"] = Path.Combine(r.GetString(5), r.GetString(6)),
                ["duration_s"] = r.IsDBNull(7) ? null : r.GetInt64(7),
                ["listened_s"] = Math.Round(r.GetDouble(10), 1),
                ["ended_ts"] = r.IsDBNull(9) ? null : r.GetInt64(9),
                ["play"] = r.GetInt64(11) == 1,
                ["outcome"] = r.GetString(12),
            };
            events.Add(new WatchEvent(r.GetInt64(8), source, "listen")
            {
                App = app,
                Title = $"{artist} – {title}",
                Data = data.ToJsonString(Json),
                ExtId = "listen:" + r.GetString(0),
            });
        }
        return events;
    }

    // ---- Tag edits ----

    private sealed record TagOp(
        long Id, string TargetPath, string Status, long UpdatedMs, string[] Fields,
        JsonObject? Before, JsonObject? After,
        double? BeforeRating, double? AfterRating, string BeforeLove, string AfterLove,
        long? BeforeReleaseYear, long? AfterReleaseYear)
    {
        public string Folder => Path.GetDirectoryName(TargetPath) ?? TargetPath;
        public string FieldKey => string.Join(',', Fields);
    }

    private List<WatchEvent> ReadTagEdits(ScrapeContext context, long since)
    {
        var lastOpId = context.Cursor(Source, "tag_edit", "last_op_id");

        using var db = OpenReadOnly(StateDb);
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT id, target_path, status, updated_at_ms, edited_fields_json,
                   before_file_tags_json, after_file_tags_json,
                   before_rating, after_rating, before_love_state, after_love_state,
                   before_release_year, after_release_year
            FROM tag_edit_operations
            WHERE ($last IS NOT NULL AND id > $last) OR ($last IS NULL AND updated_at_ms >= $since)
            ORDER BY id;
            """;
        cmd.Parameters.AddWithValue("$last", (object?)lastOpId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$since", since);

        var ops = new List<TagOp>();
        using (var r = cmd.ExecuteReader())
        {
            while (r.Read())
            {
                var op = new TagOp(
                    r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetInt64(3),
                    ParseFields(r.IsDBNull(4) ? null : r.GetString(4)),
                    ParseObject(r, 5), ParseObject(r, 6),
                    r.IsDBNull(7) ? null : r.GetDouble(7), r.IsDBNull(8) ? null : r.GetDouble(8),
                    r.GetString(9), r.GetString(10),
                    r.IsDBNull(11) ? null : r.GetInt64(11), r.IsDBNull(12) ? null : r.GetInt64(12));
                if (op.Fields.Length == 0)
                    op = op with { Fields = ColumnFields(op) };

                // Stop at the first edit that is still in progress or too fresh: everything after
                // it waits for the next scrape, so the cursor (last_op_id) never skips over it.
                if (!FinalStatuses.Contains(op.Status) || context.Now - op.UpdatedMs < EditSettleMs)
                    break;
                ops.Add(op);
            }
        }

        var events = new List<WatchEvent>();
        var group = new List<TagOp>();
        foreach (var op in ops)
        {
            var last = group.Count > 0 ? group[^1] : null;
            if (last is not null && (last.Folder != op.Folder || last.Status != op.Status
                                     || last.FieldKey != op.FieldKey || op.UpdatedMs - last.UpdatedMs > EditGroupGapMs))
            {
                events.Add(TagEditEvent(group));
                group = [];
            }
            group.Add(op);
        }
        if (group.Count > 0)
            events.Add(TagEditEvent(group));
        return events;
    }

    private static WatchEvent TagEditEvent(List<TagOp> group)
    {
        var first = group[0];
        var tags = first.After ?? first.Before;
        var artist = Str(tags, "albumArtist") ?? Str(tags, "artist");
        var album = Str(tags, "album") ?? Path.GetFileName(first.Folder);
        var label = artist is null ? album : $"{artist} – {album}";

        var changes = new JsonObject();
        var summaries = new List<string>();
        foreach (var field in first.Fields)
        {
            var pairs = group
                .Select(op => (From: Value(op, field, before: true), To: Value(op, field, before: false)))
                .Where(p => p.From != p.To)
                .Distinct()
                .Take(5)
                .Select(p => $"{p.From ?? "∅"} → {p.To ?? "∅"}")
                .ToList();
            if (pairs.Count == 0)
                continue;
            changes[field] = new JsonArray(pairs.Select(p => (JsonNode?)p).ToArray());
            if (!NumberingFields.Contains(field))
                summaries.Add($"{field} {string.Join(", ", pairs)}");
        }
        // Track/disc numbering fixes ride along with most album edits; keep them out of the headline.
        if (first.Fields.Any(NumberingFields.Contains) && changes.Any(c => NumberingFields.Contains(c.Key)))
            summaries.Add("numbering");

        var verb = first.Status switch
        {
            "rolledBack" => "Undid",
            "failed" => "Failed",
            _ => "Changed",
        };
        var tracks = group.Count == 1 ? "1 track" : $"{group.Count} tracks";
        var what = summaries.Count > 0 ? string.Join("; ", summaries) : string.Join(", ", first.Fields);

        var data = new JsonObject
        {
            ["album"] = album,
            ["artist"] = artist,
            ["folder"] = first.Folder,
            ["fields"] = new JsonArray(first.Fields.Select(f => (JsonNode?)f).ToArray()),
            ["changes"] = changes,
            ["tracks"] = group.Count,
            ["status"] = first.Status,
            ["first_op_id"] = first.Id,
            ["last_op_id"] = group[^1].Id,
        };
        return new WatchEvent(first.UpdatedMs, Source, "tag_edit")
        {
            App = App,
            Title = $"{verb} {what} on {label} ({tracks})",
            Data = data.ToJsonString(Json),
            ExtId = $"tag_edit:{first.Id}",
        };
    }

    /// <summary>
    /// Rating, love and release year live in their own columns; everything else in the tag JSON.
    /// Operations that list no fields changed one of those column values.
    /// </summary>
    private static string[] ParseFields(string? json)
    {
        if (json is null)
            return [];
        try
        {
            return JsonNode.Parse(json)?["fields"]?.AsArray().Select(n => n!.GetValue<string>()).ToArray() ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string[] ColumnFields(TagOp op)
    {
        var fields = new List<string>();
        if (op.BeforeRating != op.AfterRating)
            fields.Add("rating");
        if (op.BeforeLove != op.AfterLove)
            fields.Add("love");
        if (op.BeforeReleaseYear != op.AfterReleaseYear)
            fields.Add("releaseYear");
        return [.. fields];
    }

    private static string? Value(TagOp op, string field, bool before) => field switch
    {
        "rating" => Format(before ? op.BeforeRating : op.AfterRating),
        "love" => before ? op.BeforeLove : op.AfterLove,
        "releaseYear" => (before ? op.BeforeReleaseYear : op.AfterReleaseYear)?.ToString(),
        _ => Str(before ? op.Before : op.After, field),
    };

    private static string? Format(double? rating) => rating?.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);

    private static JsonObject? ParseObject(SqliteDataReader r, int ordinal)
    {
        if (r.IsDBNull(ordinal))
            return null;
        try
        {
            return JsonNode.Parse(r.GetString(ordinal)) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Str(JsonObject? obj, string key) =>
        obj?[key] is JsonValue v ? v.ToString() : null;

    // ---- Album additions ----

    private List<WatchEvent> ReadAlbumAdditions(ScrapeContext context, long since)
    {
        var from = context.Cursor(Source, "album_added") is { } cursor ? cursor - 60_000 : since;

        using var db = OpenReadOnly(StateDb);
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT album_id, destination_path, added_at_ms
            FROM album_additions
            WHERE added_at_ms >= $from
            ORDER BY added_at_ms;
            """;
        cmd.Parameters.AddWithValue("$from", from);

        var events = new List<WatchEvent>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var path = r.GetString(1);
            events.Add(new WatchEvent(r.GetInt64(2), Source, "album_added")
            {
                App = App,
                Title = $"Added album {Path.GetFileName(path.TrimEnd('\\', '/'))}",
                Data = new JsonObject { ["path"] = path, ["album_id"] = r.GetString(0) }.ToJsonString(Json),
                ExtId = "album_added:" + r.GetString(0),
            });
        }
        return events;
    }
}
