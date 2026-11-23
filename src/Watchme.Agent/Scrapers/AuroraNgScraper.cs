using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Watchme.Core;

namespace Watchme.Agent.Scrapers;

/// <summary>
/// Reads Aurora NG's own files (read-only):
/// <list type="bullet">
/// <item>aurora_ng/listen — from its listening history (<c>aurora-history.sqlite3</c>, Aurora's format),
/// which Aurora itself only sees through OneDrive</item>
/// <item>aurora_ng/rating — a rating or Love change, from its rating log (<c>rating-log.sqlite3</c>,
/// Aurora NG 0.27.0 and newer)</item>
/// </list>
/// </summary>
internal sealed class AuroraNgScraper(string dataDir, int backfillDays) : IScraper
{
    private const string Source = EventSources.AuroraNg;
    private const string App = "Aurora NG.exe";

    private static readonly JsonSerializerOptions Json = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public string Name => "aurora_ng";
    public TimeSpan Interval => TimeSpan.FromSeconds(30);

    private string HistoryDb => Path.Combine(dataDir, "aurora-history.sqlite3");
    private string RatingLog => Path.Combine(dataDir, "rating-log.sqlite3");

    public IReadOnlyList<WatchEvent> Scrape(ScrapeContext context)
    {
        var since = context.Now - backfillDays * 24L * 60 * 60 * 1000;
        var events = new List<WatchEvent>();
        if (File.Exists(HistoryDb))
            events.AddRange(AuroraScraper.ReadListens(HistoryDb, Source, App, context, since));
        if (File.Exists(RatingLog))
            events.AddRange(ReadRatings(context, since));
        if (!File.Exists(HistoryDb) && !File.Exists(RatingLog))
            context.Warn($"no listening history or rating log in {dataDir}");
        return events;
    }

    private List<WatchEvent> ReadRatings(ScrapeContext context, long since)
    {
        // Rows are only ever appended, so the log's own id is the cursor.
        var cursor = context.Cursor(Source, "rating", "log_id");
        using var db = AuroraScraper.OpenReadOnly(RatingLog);
        using var cmd = db.CreateCommand();
        cmd.CommandText = $"""
            SELECT id, changed_at_ms, path, album_id, title, artist, album, album_artist,
                   rating_before, rating_after, love_before, love_after
            FROM rating_changes
            WHERE {(cursor is null ? "changed_at_ms >= $since" : "id > $cursor")}
            ORDER BY id;
            """;
        cmd.Parameters.AddWithValue("$since", since);
        cmd.Parameters.AddWithValue("$cursor", cursor ?? 0);

        var events = new List<WatchEvent>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            string? Text(int i) => r.IsDBNull(i) ? null : r.GetString(i);
            int? Int(int i) => r.IsDBNull(i) ? null : r.GetInt32(i);

            var track = $"{r.GetString(5)} – {r.GetString(4)}";
            var (ratingBefore, ratingAfter) = (Int(8), Int(9));
            var (loveBefore, loveAfter) = (r.GetString(10), r.GetString(11));
            var data = new JsonObject
            {
                ["log_id"] = r.GetInt64(0),
                ["path"] = r.GetString(2),
                ["album_id"] = r.GetString(3),
                ["track"] = r.GetString(4),
                ["artist"] = r.GetString(5),
                ["album"] = Text(6),
                ["album_artist"] = Text(7),
                ["rating_before"] = ratingBefore,
                ["rating_after"] = ratingAfter,
                ["love_before"] = loveBefore,
                ["love_after"] = loveAfter,
            };
            events.Add(new WatchEvent(r.GetInt64(1), Source, "rating")
            {
                App = App,
                Title = Describe(track, ratingBefore, ratingAfter, loveBefore, loveAfter),
                Data = data.ToJsonString(Json),
                ExtId = $"rating:{r.GetInt64(0)}",
            });
        }
        return events;
    }

    /// <summary>"Rated Tiësto – Traffic 4★ (was 3★)", "Loved Tiësto – Traffic".</summary>
    internal static string Describe(string track, int? ratingBefore, int? ratingAfter, string loveBefore, string loveAfter)
    {
        if (loveBefore != loveAfter)
        {
            return loveAfter switch
            {
                "loved" => $"Loved {track}",
                "banned" => $"Banned {track}",
                _ => $"Took Love away from {track}",
            };
        }
        if (ratingAfter is not { } after)
            return $"Cleared the rating of {track}" + (ratingBefore is { } was ? $" (was {DailyReport.Stars(was)})" : "");
        return $"Rated {track} {DailyReport.Stars(after)}" + (ratingBefore is { } b ? $" (was {DailyReport.Stars(b)})" : "");
    }
}
