using Microsoft.Data.Sqlite;
using Watchme.Agent.Scrapers;
using Watchme.Core;

namespace Watchme.Tests;

/// <summary>Aurora NG's listening history and Music Library's rating events, against fake databases.</summary>
public sealed class MusicScrapersTests : IDisposable
{
    private const long Min = 60_000;

    private readonly long _now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "watchme-music-" + Guid.NewGuid().ToString("N"));
    private readonly Database _watchme = Database.Open(":memory:");

    private string Library => Path.Combine(_dir, "music-library.sqlite3");
    private string NgHistory => Path.Combine(_dir, "aurora-history.sqlite3");

    public MusicScrapersTests()
    {
        Directory.CreateDirectory(_dir);
        Exec(Library, """
            CREATE TABLE rating_events (id INTEGER PRIMARY KEY AUTOINCREMENT, import_run_id INTEGER NOT NULL,
              created_at TEXT NOT NULL, event_type TEXT NOT NULL, album_id TEXT NOT NULL, album TEXT,
              album_artist_display TEXT, year INTEGER, previous_rated_tracks INTEGER, current_rated_tracks INTEGER,
              previous_rating_completeness REAL, current_rating_completeness REAL,
              previous_effective_album_rating INTEGER, current_effective_album_rating INTEGER);
            """);
        Exec(NgHistory, """
            CREATE TABLE listening_sessions (
              session_id TEXT PRIMARY KEY, track_key TEXT NOT NULL, title TEXT NOT NULL, artist TEXT NOT NULL,
              album TEXT NOT NULL, genre TEXT, directory TEXT NOT NULL, filename TEXT NOT NULL,
              duration_seconds INTEGER, started_at_ms INTEGER NOT NULL, ended_at_ms INTEGER,
              listened_seconds REAL NOT NULL DEFAULT 0, registered_play INTEGER NOT NULL DEFAULT 0,
              registered_at_ms INTEGER, threshold_seconds INTEGER NOT NULL, outcome TEXT NOT NULL DEFAULT 'active');
            """);
    }

    private static void Exec(string path, string sql)
    {
        using var c = new SqliteConnection($"Data Source={path};Pooling=False");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private void Rating(long at, string type, int before, int after, int tracks, int? ratingBefore, int? ratingAfter,
        string albumId = "a1", string album = "Closer")
    {
        var iso = DateTimeOffset.FromUnixTimeMilliseconds(at).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'+00:00'");
        string Sql(int? v) => v is null ? "NULL" : v.Value.ToString();
        Exec(Library, $"""
            INSERT INTO rating_events (import_run_id, created_at, event_type, album_id, album, album_artist_display, year,
              previous_rated_tracks, current_rated_tracks, previous_rating_completeness, current_rating_completeness,
              previous_effective_album_rating, current_effective_album_rating)
            VALUES (1, '{iso}', '{type}', '{albumId}', '{album}', 'Milk Inc.', 2003, {before}, {after},
              {((double)before / tracks).ToString(System.Globalization.CultureInfo.InvariantCulture)}, {((double)after / tracks).ToString(System.Globalization.CultureInfo.InvariantCulture)}, {Sql(ratingBefore)}, {Sql(ratingAfter)});
            """);
    }

    private IReadOnlyList<WatchEvent> Scrape(IScraper scraper)
    {
        var events = scraper.Scrape(new ScrapeContext(_watchme, _now));
        _watchme.Insert(events.ToList());
        return events;
    }

    [Fact]
    public void Rating_events_are_read_once_and_album_imports_skipped()
    {
        Rating(_now - 30 * Min, "ratedMore", 0, 1, 10, null, 80);
        Rating(_now - 25 * Min, "addedRated", 0, 12, 12, null, 70, albumId: "a2");
        var scraper = new MusicLibraryScraper(Library, backfillDays: 7);

        var e = Assert.Single(Scrape(scraper));
        Assert.Equal("Rated 1 more track on Milk Inc. – Closer (2003): 1 of 10 rated, album rating 80", e.Title);

        Rating(_now - 5 * Min, "completed", 9, 10, 10, 80, 82);
        Assert.Equal("Rated 1 more track on Milk Inc. – Closer (2003): all 10 rated, album rating 80 → 82",
            Assert.Single(Scrape(scraper)).Title);
    }

    [Fact]
    public void Report_shows_one_line_per_album_sitting()
    {
        Rating(_now - 40 * Min, "ratedMore", 10, 11, 14, 92, 92);
        Rating(_now - 38 * Min, "ratedMore", 11, 12, 14, 92, 93);
        Rating(_now - 35 * Min, "completed", 13, 14, 14, 93, 92);
        Scrape(new MusicLibraryScraper(Library, backfillDays: 7));

        var line = Assert.Single(DailyReport.LoadActivity(_watchme, _now - 60 * Min, _now));
        Assert.EndsWith("Music Library   Rated Milk Inc. – Closer (2003): 10 → 14 of 14 tracks rated, album rating 92", line);
    }

    [Fact]
    public void Removed_albums_become_one_line_per_clean_up()
    {
        Exec(Library, """
            CREATE TABLE library_updates (id INTEGER PRIMARY KEY AUTOINCREMENT, import_run_id INTEGER,
              created_at TEXT NOT NULL, change_kind TEXT NOT NULL, category TEXT NOT NULL, album_id TEXT NOT NULL,
              album_artist_display TEXT, album TEXT, year INTEGER, field TEXT, field_label TEXT,
              previous_value TEXT, current_value TEXT, change_count INTEGER, description TEXT NOT NULL,
              source_kind TEXT NOT NULL, source_label TEXT NOT NULL, source_path TEXT);
            """);
        void Update(long at, string kind, string category, string artist, string album, int year, int count)
        {
            var iso = DateTimeOffset.FromUnixTimeMilliseconds(at).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'+00:00'");
            Exec(Library, $"""
                INSERT INTO library_updates (created_at, change_kind, category, album_id, album_artist_display, album, year,
                  change_count, description, source_kind, source_label, source_path)
                VALUES ('{iso}', '{kind}', '{category}', 'mb:{album}', '{artist}', '{album}', {year}, {count}, 'x',
                  'library_import', 'Library import', 'D:\MUSIC\{artist} - {album} ({year})');
                """);
        }
        Update(_now - 50 * Min, "removed", "album", "DJ Tiësto", "Space Age 1.0", 1998, 16);
        Update(_now - 48 * Min, "changed", "metadata", "DJ Tiësto", "Magik", 1997, 1);
        Update(_now - 45 * Min, "removed", "album", "Tiësto", "Magik Three", 1998, 16);
        Update(_now - 40 * Min, "removed", "album", "DJ Tiësto", "Magik Four", 1999, 16);
        var scraper = new MusicLibraryScraper(Library, backfillDays: 7);

        var events = Scrape(scraper).Where(e => e.Kind == "album_removed").ToList();
        Assert.Equal(3, events.Count);
        Assert.Equal("Removed DJ Tiësto – Space Age 1.0 (1998), 16 tracks", events[0].Title);

        Update(_now - 5 * Min, "removed", "album", "Milk Inc.", "Closer", 2003, 14);
        Assert.Equal("Removed Milk Inc. – Closer (2003), 14 tracks", Assert.Single(Scrape(scraper)).Title);

        var lines = DailyReport.LoadActivity(_watchme, _now - 60 * Min, _now).Where(l => l.Contains("Removed")).ToList();
        Assert.Equal(2, lines.Count);
        Assert.EndsWith("Music Library   Removed 3 albums (48 tracks): DJ Tiësto: Space Age 1.0 (1998), Magik Four (1999); Tiësto: Magik Three (1998)", lines[0]);
        Assert.EndsWith("Music Library   Removed Milk Inc. – Closer (2003), 14 tracks", lines[1]);
    }

    [Fact]
    public void Added_albums_are_one_line_per_import_and_replace_auroras()
    {
        Exec(Library, """
            CREATE TABLE library_updates (id INTEGER PRIMARY KEY AUTOINCREMENT, import_run_id INTEGER,
              created_at TEXT NOT NULL, change_kind TEXT NOT NULL, category TEXT NOT NULL, album_id TEXT NOT NULL,
              album_artist_display TEXT, album TEXT, year INTEGER, field TEXT, field_label TEXT,
              previous_value TEXT, current_value TEXT, change_count INTEGER, description TEXT NOT NULL,
              source_kind TEXT NOT NULL, source_label TEXT NOT NULL, source_path TEXT);
            """);
        var iso = DateTimeOffset.FromUnixTimeMilliseconds(_now - 20 * Min).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'+00:00'");
        Exec(Library, $"""
            INSERT INTO library_updates (created_at, change_kind, category, album_id, album_artist_display, album, year,
              change_count, description, source_kind, source_label, source_path) VALUES
              ('{iso}', 'new', 'album', 'aurora:v1:a', 'Antiloop', 'LP', 1997, 12, 'New album', 'library_import', 'x', 'C:\_ToAurora'),
              ('{iso}', 'new', 'album', 'aurora:v1:b', 'Antiloop', 'Fastlane People', 2000, 10, 'New album', 'library_import', 'x', 'C:\_ToAurora');
            """);
        var events = Scrape(new MusicLibraryScraper(Library, backfillDays: 7));
        Assert.Equal(["Added Antiloop – LP (1997), 12 tracks", "Added Antiloop – Fastlane People (2000), 10 tracks"],
            events.Select(e => e.Title));

        // Aurora's own record of the same import, and of one Music Library has not logged.
        _watchme.Insert([
            new WatchEvent(_now - 21 * Min, EventSources.Aurora, "album_added")
                { Title = "Added album Antiloop - LP (1997)", Data = """{"album_id":"aurora:v1:a"}""", ExtId = "album_added:a" },
            new WatchEvent(_now - 10 * Min, EventSources.Aurora, "album_added")
                { Title = "Added album Westlife - Gravity (2010)", Data = """{"album_id":"aurora:v1:c"}""", ExtId = "album_added:c" },
        ]);

        var lines = DailyReport.LoadActivity(_watchme, _now - 60 * Min, _now);
        Assert.Equal(2, lines.Count);
        Assert.EndsWith(@"Music Library   Added 2 albums (22 tracks) from C:\_ToAurora: Antiloop: LP (1997), Fastlane People (2000)", lines[0]);
        Assert.EndsWith("Aurora          Added album Westlife - Gravity (2010)", lines[1]);
    }

    [Fact]
    public void Tag_changes_are_one_line_per_album_unless_aurora_logged_them()
    {
        Exec(Library, """
            CREATE TABLE library_updates (id INTEGER PRIMARY KEY AUTOINCREMENT, import_run_id INTEGER,
              created_at TEXT NOT NULL, change_kind TEXT NOT NULL, category TEXT NOT NULL, album_id TEXT NOT NULL,
              album_artist_display TEXT, album TEXT, year INTEGER, field TEXT, field_label TEXT,
              previous_value TEXT, current_value TEXT, change_count INTEGER, description TEXT NOT NULL,
              source_kind TEXT NOT NULL, source_label TEXT NOT NULL, source_path TEXT);
            """);
        void Change(long at, int run, string album, string? field, string? label, string? before, string? after,
            string description, string category = "metadata")
        {
            var iso = DateTimeOffset.FromUnixTimeMilliseconds(at).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'+00:00'");
            string Q(string? s) => s is null ? "NULL" : $"'{s}'";
            Exec(Library, $"""
                INSERT INTO library_updates (import_run_id, created_at, change_kind, category, album_id, album_artist_display,
                  album, year, field, field_label, previous_value, current_value, description, source_kind, source_label)
                VALUES ({run}, '{iso}', 'changed', '{category}', 'mb:{album}', 'DJ Tiësto', '{album}', 1997, {Q(field)},
                  {Q(label)}, {Q(before)}, {Q(after)}, '{description}', 'library_import', 'x');
                """);
        }
        // Made in Music Library: two fields on one album, in one re-read.
        Change(_now - 50 * Min, 1, "Magik", "album_artist_display", "Album artist", "DJ Tiësto; Tiësto", "DJ Tiësto", "Album artist changed");
        Change(_now - 50 * Min, 1, "Magik", "canonical_genre", "Genre", "Trance", "Progressive Trance", "Genre changed");
        Change(_now - 50 * Min, 1, "Magik", "album_rating", "Album rating", "80", "82", "Album rating changed");
        Change(_now - 49 * Min, 2, "Magik", "rated_tracks", "Track ratings", "0", "1", "1 track rating added", "ratings");
        // Made in Aurora (which logs it with a different folder), and Aurora's own track sync.
        Change(_now - 30 * Min, 3, "Space Age", "year", "Year", "1997", "1998", "Year changed");
        Change(_now - 20 * Min, 4, "Lost Treasures", "track_metadata", "Track metadata", null, null, "11 scoped track changes synchronized from Aurora");
        _watchme.Insert([new WatchEvent(_now - 31 * Min, EventSources.Aurora, "tag_edit")
            { Title = "Changed year 1997 → 1998 on DJ Tiësto – Space Age (16 tracks)",
              Data = """{"album":"Space Age","folder":"D:\\MUSIC\\DJ Tiësto - Space Age (1998)"}""", ExtId = "tag:1" }]);

        var events = Scrape(new MusicLibraryScraper(Library, backfillDays: 7)).Where(e => e.Kind == "tag_change").ToList();
        Assert.Equal(4, events.Count);
        Assert.Equal("Changed album artist DJ Tiësto; Tiësto → DJ Tiësto on DJ Tiësto – Magik (1997)", events[0].Title);
        Assert.Equal("Changed 11 scoped track changes synchronized from Aurora on DJ Tiësto – Lost Treasures (1997)", events[3].Title);

        var lines = DailyReport.LoadActivity(_watchme, _now - 60 * Min, _now);
        Assert.Equal(2, lines.Count);
        Assert.EndsWith("Music Library   Changed album artist DJ Tiësto; Tiësto → DJ Tiësto; genre Trance → Progressive Trance on DJ Tiësto – Magik (1997)", lines[0]);
        Assert.EndsWith("Aurora          Changed year 1997 → 1998 on DJ Tiësto – Space Age (16 tracks)", lines[1]);
    }

    [Theory]
    [InlineData("canonical_genre", "Genre", "Reggaeton", "Reggaetón", "Genre changed", "genre Reggaeton → Reggaetón")]
    [InlineData("track_release_year", "Track release year", "1999", "1983", "Track release year changed for Rich from 1999 to 1983", "track release year changed for Rich from 1999 to 1983")]
    [InlineData("total_tracks", "Tracks", "15", "14", "1 track removed", "tracks 15 → 14")]
    [InlineData("album", "Album title", null, "LP", "x", "album title ∅ → LP")]
    public void Tag_change_wording(string field, string label, string? before, string? after, string description, string expected) =>
        Assert.Equal(expected, MusicLibraryScraper.TagChange(field, label, before, after, description));

    [Fact]
    public void Aurora_ng_listens_are_their_own_source()
    {
        Exec(NgHistory, $"""
            INSERT INTO listening_sessions VALUES ('session-aurora-ng-pc-1', 'k', 'Love Comes Again', 'Tiësto', 'Just Be',
              'Trance', 'D:\MUSIC\Tiësto - Just Be (2004)', '02.mp3', 300, {_now - 10 * Min}, {_now - 9 * Min}, 35, 1, NULL, 30, 'skipped');
            """);

        var e = Assert.Single(Scrape(new AuroraNgScraper(_dir, backfillDays: 7)));
        Assert.Equal(EventSources.AuroraNg, e.Source);
        Assert.Equal("Tiësto – Love Comes Again", e.Title);

        var line = Assert.Single(DailyReport.LoadActivity(_watchme, _now - 60 * Min, _now));
        Assert.Contains("Aurora NG       Listened to Tiësto – Love Comes Again (Just Be)", line);
    }

    /// <summary>A row in Aurora NG's rating log, with the schema Aurora NG 0.27.0 creates.</summary>
    private void Logged(long at, string track, int? before, int? after, string loveBefore = "neutral", string loveAfter = "neutral")
    {
        var log = Path.Combine(_dir, "rating-log.sqlite3");
        string Sql(int? v) => v is null ? "NULL" : v.Value.ToString();
        Exec(log, $"""
            CREATE TABLE IF NOT EXISTS rating_changes (id INTEGER PRIMARY KEY AUTOINCREMENT, changed_at_ms INTEGER NOT NULL,
              track_key TEXT NOT NULL, path TEXT NOT NULL, album_id TEXT NOT NULL, title TEXT NOT NULL, artist TEXT NOT NULL,
              album TEXT, album_artist TEXT, rating_before INTEGER, rating_after INTEGER,
              love_before TEXT NOT NULL, love_after TEXT NOT NULL);
            INSERT INTO rating_changes (changed_at_ms, track_key, path, album_id, title, artist, album, album_artist,
              rating_before, rating_after, love_before, love_after)
            VALUES ({at}, 'k', 'D:\MUSIC\Milk Inc. - Closer (2003)\{track}.mp3', 'a1', '{track}', 'Milk Inc.', 'Closer', 'Milk Inc.',
              {Sql(before)}, {Sql(after)}, '{loveBefore}', '{loveAfter}');
            """);
    }

    [Fact]
    public void Aurora_ng_ratings_name_the_track_and_merge_with_library_totals()
    {
        Logged(_now - 20 * Min, "Walk On Water", null, 80);
        Logged(_now - 19 * Min, "Sleepwalker", null, 100);
        Logged(_now - 18 * Min, "Sleepwalker", 100, 100, "neutral", "loved");
        Logged(_now - 17 * Min, "Closer", 60, 90);
        Rating(_now - 19 * Min, "ratedMore", 12, 14, 14, 90, 92);

        var titles = Scrape(new AuroraNgScraper(_dir, backfillDays: 7)).Select(e => e.Title).ToList();
        Assert.Equal(
        [
            "Rated Milk Inc. – Walk On Water 4★",
            "Rated Milk Inc. – Sleepwalker 5★",
            "Loved Milk Inc. – Sleepwalker",
            "Rated Milk Inc. – Closer 4.5★ (was 3★)",
        ], titles);
        Scrape(new MusicLibraryScraper(Library, backfillDays: 7));

        var line = Assert.Single(DailyReport.LoadActivity(_watchme, _now - 60 * Min, _now));
        Assert.EndsWith("Aurora NG       Rated 3 tracks on Milk Inc. – Closer: 5★ ×1, 4.5★ ×1, 4★ ×1; loved 1"
                        + " · 12 → 14 of 14 tracks rated, album rating 90 → 92", line);
    }

    public void Dispose()
    {
        _watchme.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }
}
