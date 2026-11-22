using Microsoft.Data.Sqlite;
using Watchme.Agent.Scrapers;
using Watchme.Core;

namespace Watchme.Tests;

/// <summary>Runs the scraper against a fake Aurora data folder with the real table layouts.</summary>
public sealed class AuroraScraperTests : IDisposable
{
    private const long Min = 60_000;
    private const long Now = 1_800_000_000_000;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "watchme-aurora-" + Guid.NewGuid().ToString("N"));
    private readonly Database _watchme = Database.Open(":memory:");

    public AuroraScraperTests()
    {
        Directory.CreateDirectory(_dir);
        Exec("aurora-history.sqlite3", """
            CREATE TABLE listening_sessions (
              session_id TEXT PRIMARY KEY, track_key TEXT NOT NULL, title TEXT NOT NULL, artist TEXT NOT NULL,
              album TEXT NOT NULL, genre TEXT, directory TEXT NOT NULL, filename TEXT NOT NULL,
              duration_seconds INTEGER, started_at_ms INTEGER NOT NULL, ended_at_ms INTEGER,
              listened_seconds REAL NOT NULL DEFAULT 0, registered_play INTEGER NOT NULL DEFAULT 0,
              registered_at_ms INTEGER, threshold_seconds INTEGER NOT NULL, outcome TEXT NOT NULL DEFAULT 'active');
            """);
        Exec("aurora-state.sqlite3", """
            CREATE TABLE tag_edit_operations (
              id INTEGER PRIMARY KEY AUTOINCREMENT, track_key TEXT NOT NULL, target_path TEXT NOT NULL,
              temp_path TEXT, backup_path TEXT, before_rating REAL, before_love_state TEXT NOT NULL,
              before_release_year INTEGER, after_rating REAL, after_love_state TEXT NOT NULL,
              after_release_year INTEGER, source_fingerprint TEXT NOT NULL, status TEXT NOT NULL,
              created_at_ms INTEGER NOT NULL, updated_at_ms INTEGER NOT NULL, error_message TEXT,
              before_file_tags_json TEXT, after_file_tags_json TEXT, edited_fields_json TEXT);
            CREATE TABLE album_additions (
              album_id TEXT PRIMARY KEY, destination_path TEXT NOT NULL, import_run_id INTEGER NOT NULL,
              added_at_ms INTEGER NOT NULL);
            """);
    }

    private void Exec(string file, string sql)
    {
        using var c = new SqliteConnection($"Data Source={Path.Combine(_dir, file)};Pooling=False");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private void Listen(string id, long start, string outcome, int play = 1, string album = "Loonee Tunes!") =>
        Exec("aurora-history.sqlite3", $"""
            INSERT INTO listening_sessions VALUES ('{id}', 'k', 'Lorraine', 'Bad Manners', '{album}', '2 Tone',
              'D:\Music\Bad Manners', '01.mp3', 180, {start}, {start + 3 * Min}, 175, {play}, NULL, 30, '{outcome}');
            """);

    private void GenreEdit(int track, long at, string status = "verified", string album = "King Daddy") =>
        Exec("aurora-state.sqlite3", $$"""
            INSERT INTO tag_edit_operations (track_key, target_path, before_love_state, after_love_state,
              source_fingerprint, status, created_at_ms, updated_at_ms,
              before_file_tags_json, after_file_tags_json, edited_fields_json)
            VALUES ('k{{track}}', 'D:\MUSIC\{{album}}\{{track:00}}.mp3', 'neutral', 'neutral', 'f', '{{status}}', {{at}}, {{at}},
              '{"albumArtist":"Daddy Yankee","album":"{{album}}","genre":"Reggaeton"}',
              '{"albumArtist":"Daddy Yankee","album":"{{album}}","genre":"Reggaetón"}',
              '{"fields":["genre"]}');
            """);

    /// <summary>One scrape, with the events written to the in-memory Watchme database like the agent does.</summary>
    private IReadOnlyList<WatchEvent> Scrape(long now = Now)
    {
        var events = new AuroraScraper(_dir, backfillDays: 7).Scrape(new ScrapeContext(_watchme, now));
        _watchme.Insert(events.ToList());
        return events;
    }

    [Fact]
    public void Finished_listens_are_imported_once_and_active_ones_wait()
    {
        Listen("a", Now - 30 * Min, "completed");
        Listen("b", Now - 5 * Min, "active");

        var first = Scrape();
        var listen = Assert.Single(first);
        Assert.Equal(("listen", "Bad Manners – Lorraine", Now - 30 * Min), (listen.Kind, listen.Title, listen.Ts));

        // "b" finishes; the next scrape re-reads an overlapping window but only "b" is new.
        Exec("aurora-history.sqlite3", "UPDATE listening_sessions SET outcome = 'skipped' WHERE session_id = 'b';");
        Scrape(Now + Min);

        Assert.Equal(2L, _watchme.Scalar("SELECT COUNT(*) FROM events WHERE kind = 'listen';"));
    }

    [Fact]
    public void Genre_edits_on_one_album_become_one_event()
    {
        for (var t = 1; t <= 11; t++)
            GenreEdit(t, Now - 10 * Min + t * 1000);

        var e = Assert.Single(Scrape());
        Assert.Equal("tag_edit", e.Kind);
        Assert.Equal("Changed genre Reggaeton → Reggaetón on Daddy Yankee – King Daddy (11 tracks)", e.Title);
        Assert.Empty(Scrape(Now + Min)); // Cursor (last_op_id) moved past them.
    }

    [Fact]
    public void Fresh_and_unfinished_edits_wait_until_they_settle()
    {
        GenreEdit(1, Now - 10 * Min);
        GenreEdit(2, Now - 10 * Min, status: "prepared");
        GenreEdit(3, Now - 10 * Min);

        Assert.Single(Scrape()); // Only op 1; op 2 blocks op 3 so the cursor can't skip op 2.

        Exec("aurora-state.sqlite3", "UPDATE tag_edit_operations SET status = 'verified' WHERE id = 2;");
        var later = Assert.Single(Scrape(Now + Min));
        Assert.Contains("(2 tracks)", later.Title);
    }

    [Fact]
    public void Rating_change_without_listed_fields_is_described()
    {
        Exec("aurora-state.sqlite3", $"""
            INSERT INTO tag_edit_operations (track_key, target_path, before_rating, after_rating, before_love_state,
              after_love_state, source_fingerprint, status, created_at_ms, updated_at_ms, edited_fields_json)
            VALUES ('k', 'D:\MUSIC\X\01.mp3', 3, 4.5, 'neutral', 'loved', 'f', 'verified', {Now - 10 * Min}, {Now - 10 * Min}, NULL);
            """);

        var e = Assert.Single(Scrape());
        Assert.Contains("rating 3 → 4.5", e.Title);
        Assert.Contains("love neutral → loved", e.Title);
    }

    [Fact]
    public void Numbering_fixes_are_shortened_in_the_title()
    {
        Exec("aurora-state.sqlite3", $$"""
            INSERT INTO tag_edit_operations (track_key, target_path, before_love_state, after_love_state,
              source_fingerprint, status, created_at_ms, updated_at_ms,
              before_file_tags_json, after_file_tags_json, edited_fields_json)
            VALUES ('k', 'D:\MUSIC\A1\01.mp3', 'neutral', 'neutral', 'f', 'verified', {{Now - 10 * Min}}, {{Now - 10 * Min}},
              '{"albumArtist":"A1","album":"Here We Come","genre":"Pop","discNumber":0,"discTotal":0}',
              '{"albumArtist":"A1","album":"Here We Come","genre":"Boy Band","discNumber":1,"discTotal":1}',
              '{"fields":["genre","discNumber","discTotal"]}');
            """);

        var e = Assert.Single(Scrape());
        Assert.Equal("Changed genre Pop → Boy Band; numbering on A1 – Here We Come (1 track)", e.Title);
        Assert.Contains("\"discNumber\":[\"0 → 1\"]", e.Data);
    }

    [Fact]
    public void Report_collapses_listens_per_album()
    {
        Listen("a", Now - 30 * Min, "completed");
        Listen("b", Now - 27 * Min, "completed");
        Listen("c", Now - 24 * Min, "skipped", play: 0);
        Scrape();

        var line = Assert.Single(DailyReport.LoadActivity(_watchme, Now - 60 * Min, Now));
        Assert.Contains("Listened to Bad Manners – Loonee Tunes!: 3 tracks, 2 counted as plays", line);
    }

    public void Dispose()
    {
        _watchme.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }
}
