using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Watchme.Core;

namespace Watchme.Agent.Scrapers;

/// <summary>
/// Reads Music Library's <c>rating_events</c> (read-only): one row each time an album's ratings
/// change, whichever app the rating came from (Aurora NG, Aurora, MusicBee...). Rows are per album,
/// with rated tracks, completeness and the album rating before and after; which track got how many
/// stars is not logged there. Emits musiclibrary/rating events.
/// Also reads <c>library_updates</c>, Music Library's change log, for albums added to and removed
/// from the library (by Aurora or any other import, or deleted): one musiclibrary/album_added or
/// musiclibrary/album_removed event each; and for tag changes (genre, year, album artist...), one
/// musiclibrary/tag_change event per field and album. Rating changes there are left to rating_events.
/// </summary>
internal sealed class MusicLibraryScraper(string libraryDb, int backfillDays) : IScraper
{
    private const string Source = EventSources.MusicLibrary;
    private const string Kind = "rating";

    /// <summary>
    /// Event types that come from rating tracks. The others (addedRated, addedPartial,
    /// removedRated) come from importing or removing albums.
    /// </summary>
    private static readonly string[] RatingTypes = ["ratedMore", "ratedLess", "completed", "ratingChanged", "ratingUpdated"];

    private static readonly JsonSerializerOptions Json = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public string Name => "musiclibrary";
    public TimeSpan Interval => TimeSpan.FromSeconds(30);

    public IReadOnlyList<WatchEvent> Scrape(ScrapeContext context)
    {
        if (!File.Exists(libraryDb))
        {
            context.Warn($"no Music Library catalog at {libraryDb}");
            return [];
        }
        using var db = AuroraScraper.OpenReadOnly(libraryDb);
        return [.. Ratings(db, context), .. AlbumUpdates(db, context, "new", "album_added", "Added"),
                .. AlbumUpdates(db, context, "removed", "album_removed", "Removed"), .. TagChanges(db, context)];
    }

    private string Since(ScrapeContext context) =>
        DateTimeOffset.FromUnixTimeMilliseconds(context.Now - backfillDays * 24L * 60 * 60 * 1000)
            .UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);

    private static bool HasUpdates(Microsoft.Data.Sqlite.SqliteConnection db)
    {
        using var check = db.CreateCommand();
        check.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'library_updates';";
        return check.ExecuteScalar() is not null;
    }

    /// <summary>
    /// Tag changes Music Library saw when it re-read an album, whichever app made them:
    /// "Changed album artist DJ Tiësto; Tiësto → DJ Tiësto on DJ Tiësto – Magik: First Flight (1997)".
    /// The album rating is derived from track ratings, so it is left out like the ratings category.
    /// </summary>
    private List<WatchEvent> TagChanges(Microsoft.Data.Sqlite.SqliteConnection db, ScrapeContext context)
    {
        if (!HasUpdates(db))
            return [];
        var cursor = context.Cursor(Source, "tag_change", "update_id");
        using var cmd = db.CreateCommand();
        cmd.CommandText = $"""
            SELECT id, created_at, import_run_id, album_id, album_artist_display, album, year,
                   field, field_label, previous_value, current_value, change_count, description, source_path
            FROM library_updates
            WHERE {(cursor is null ? "created_at >= $since" : "id > $cursor")}
              AND change_kind = 'changed' AND category IN ('metadata', 'tracks')
              AND COALESCE(field, '') <> 'album_rating'
            ORDER BY id;
            """;
        if (cursor is { } c)
            cmd.Parameters.AddWithValue("$cursor", c);
        else
            cmd.Parameters.AddWithValue("$since", Since(context));

        var events = new List<WatchEvent>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            if (!DateTimeOffset.TryParse(r.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at))
                continue;
            string? S(int i) => r.IsDBNull(i) ? null : r.GetString(i);
            var artist = S(4);
            var albumTitle = S(5) ?? "?";
            int? year = r.IsDBNull(6) ? null : r.GetInt32(6);
            var album = (artist is null ? albumTitle : $"{artist} – {albumTitle}") + (year is null ? "" : $" ({year})");
            var change = TagChange(S(7), S(8), S(9), S(10), S(12));
            var data = new JsonObject
            {
                ["update_id"] = r.GetInt64(0),
                ["import_run_id"] = r.IsDBNull(2) ? null : r.GetInt64(2),
                ["album_id"] = r.GetString(3),
                ["album"] = albumTitle,
                ["artist"] = artist,
                ["year"] = year,
                ["field"] = S(7),
                ["change"] = change,
                ["before"] = S(9),
                ["after"] = S(10),
                ["count"] = r.IsDBNull(11) ? null : r.GetInt32(11),
                ["description"] = S(12),
                ["path"] = S(13),
            };
            events.Add(new WatchEvent(at.ToUnixTimeMilliseconds(), Source, "tag_change")
            {
                App = "music-library.exe",
                Title = $"Changed {change} on {album}",
                Data = data.ToJsonString(Json),
                ExtId = $"update:{r.GetInt64(0)}",
            });
        }
        return events;
    }

    /// <summary>"genre Reggaeton → Reggaetón", or the log's own words when there are no values ("1 track removed").</summary>
    /// Track-level changes name the track only in the description, so that is used for them.
    internal static string TagChange(string? field, string? label, string? before, string? after, string? description)
    {
        var trackLevel = field?.StartsWith("track_", StringComparison.Ordinal) == true && !string.IsNullOrEmpty(description);
        if (!trackLevel && (before is not null || after is not null))
        {
            var name = string.IsNullOrEmpty(label) ? "tag" : char.ToLowerInvariant(label[0]) + label[1..];
            return $"{name} {before ?? "∅"} → {after ?? "∅"}";
        }
        return string.IsNullOrEmpty(description) ? "tags" : char.ToLowerInvariant(description[0]) + description[1..];
    }

    /// <summary>Albums added or removed: "Removed DJ Tiësto – Space Age 1.0 (1998), 16 tracks".</summary>
    private List<WatchEvent> AlbumUpdates(Microsoft.Data.Sqlite.SqliteConnection db, ScrapeContext context,
        string changeKind, string kind, string verb)
    {
        if (!HasUpdates(db))
            return [];
        var cursor = context.Cursor(Source, kind, "update_id");
        using var cmd = db.CreateCommand();
        cmd.CommandText = $"""
            SELECT id, created_at, album_id, album_artist_display, album, year, change_count, source_path
            FROM library_updates
            WHERE {(cursor is null ? "created_at >= $since" : "id > $cursor")}
              AND change_kind = $kind AND category = 'album'
            ORDER BY id;
            """;
        cmd.Parameters.AddWithValue("$kind", changeKind);
        if (cursor is { } c)
            cmd.Parameters.AddWithValue("$cursor", c);
        else
            cmd.Parameters.AddWithValue("$since", Since(context));

        var events = new List<WatchEvent>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            if (!DateTimeOffset.TryParse(r.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at))
                continue;
            var artist = r.IsDBNull(3) ? null : r.GetString(3);
            var albumTitle = r.IsDBNull(4) ? "?" : r.GetString(4);
            int? year = r.IsDBNull(5) ? null : r.GetInt32(5);
            int? tracks = r.IsDBNull(6) ? null : r.GetInt32(6);
            var album = (artist is null ? albumTitle : $"{artist} – {albumTitle}") + (year is null ? "" : $" ({year})");
            var data = new JsonObject
            {
                ["update_id"] = r.GetInt64(0),
                ["album_id"] = r.GetString(2),
                ["album"] = albumTitle,
                ["artist"] = artist,
                ["year"] = year,
                ["tracks"] = tracks,
                // For a removal the album's folder; for an addition the folder it was imported from.
                ["path"] = r.IsDBNull(7) ? null : r.GetString(7),
            };
            events.Add(new WatchEvent(at.ToUnixTimeMilliseconds(), Source, kind)
            {
                App = "music-library.exe",
                Title = $"{verb} {album}" + (tracks is null ? "" : $", {tracks} {(tracks == 1 ? "track" : "tracks")}"),
                Data = data.ToJsonString(Json),
                ExtId = $"update:{r.GetInt64(0)}",
            });
        }
        return events;
    }

    private List<WatchEvent> Ratings(Microsoft.Data.Sqlite.SqliteConnection db, ScrapeContext context)
    {
        var cursor = context.Cursor(Source, Kind, "event_id");
        using var cmd = db.CreateCommand();
        var types = string.Join(", ", RatingTypes.Select(t => $"'{t}'"));
        // Both filters use an index (the primary key, idx_rating_events_created_at).
        cmd.CommandText = $"""
            SELECT id, created_at, event_type, album_id, album, album_artist_display, year,
                   previous_rated_tracks, current_rated_tracks, previous_rating_completeness,
                   current_rating_completeness, previous_effective_album_rating, current_effective_album_rating
            FROM rating_events
            WHERE {(cursor is null ? "created_at >= $since" : "id > $cursor")} AND event_type IN ({types})
            ORDER BY id;
            """;
        if (cursor is { } c)
            cmd.Parameters.AddWithValue("$cursor", c);
        else
            cmd.Parameters.AddWithValue("$since", Since(context));

        var events = new List<WatchEvent>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            if (!DateTimeOffset.TryParse(r.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at))
                continue;
            int? Int(int i) => r.IsDBNull(i) ? null : r.GetInt32(i);
            double? Real(int i) => r.IsDBNull(i) ? null : r.GetDouble(i);

            var type = r.GetString(2);
            var artist = r.IsDBNull(5) ? null : r.GetString(5);
            var albumTitle = r.IsDBNull(4) ? "?" : r.GetString(4);
            var year = Int(6);
            var album = (artist is null ? albumTitle : $"{artist} – {albumTitle}") + (year is null ? "" : $" ({year})");
            var (before, after) = (Int(7) ?? 0, Int(8) ?? 0);
            var completeness = Real(10);
            int? total = completeness is > 0 ? (int)Math.Round(after / completeness.Value) : null;
            var (ratingBefore, ratingAfter) = (Int(11), Int(12));

            var data = new JsonObject
            {
                ["event_id"] = r.GetInt64(0),
                ["type"] = type,
                ["album_id"] = r.GetString(3),
                ["album"] = albumTitle,
                ["artist"] = artist,
                ["year"] = year,
                ["rated_before"] = before,
                ["rated_after"] = after,
                ["tracks"] = total,
                ["album_rating_before"] = ratingBefore,
                ["album_rating_after"] = ratingAfter,
            };
            events.Add(new WatchEvent(at.ToUnixTimeMilliseconds(), Source, Kind)
            {
                App = "music-library.exe",
                Title = Describe(album, before, after, total, ratingBefore, ratingAfter),
                Data = data.ToJsonString(Json),
                ExtId = $"rating:{r.GetInt64(0)}",
            });
        }
        return events;
    }

    /// <summary>"Rated 1 more track on Tiësto – Just Be (2004): 1 of 10 rated, album rating 80".</summary>
    internal static string Describe(string album, int before, int after, int? total, int? ratingBefore, int? ratingAfter)
    {
        var of = total is null ? $"{after} rated" : after == total ? $"all {total} rated" : $"{after} of {total} rated";
        var what = (after - before) switch
        {
            > 0 and var n => $"Rated {n} more {(n == 1 ? "track" : "tracks")} on {album}: {of}",
            < 0 and var n => $"Removed {-n} {(n == -1 ? "rating" : "ratings")} on {album}: {of}",
            _ => $"Changed track ratings on {album}",
        };
        return what + AlbumRating(ratingBefore, ratingAfter);
    }

    internal static string AlbumRating(int? before, int? after) => (before, after) switch
    {
        (null, null) => "",
        (_, null) => ", album rating removed",
        ({ } b, { } a) when b != a => $", album rating {b} → {a}",
        (_, { } a) => $", album rating {a}",
    };
}
