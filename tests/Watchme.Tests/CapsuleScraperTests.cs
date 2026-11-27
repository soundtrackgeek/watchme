using System.Globalization;
using Microsoft.Data.Sqlite;
using Watchme.Agent.Scrapers;
using Watchme.Core;

namespace Watchme.Tests;

/// <summary>Runs the scraper against a fake Capsule journal with the tables and columns it reads.</summary>
public sealed class CapsuleScraperTests : IDisposable
{
    private const long Min = 60_000;

    // Capsule writes local times to the minute; a whole minute keeps them exact.
    private static readonly long Now = new DateTimeOffset(new DateTime(2026, 11, 27, 12, 0, 0, DateTimeKind.Local)).ToUnixTimeMilliseconds();

    private readonly string _path = Path.Combine(Path.GetTempPath(), "watchme-capsule-" + Guid.NewGuid().ToString("N") + ".db");
    private readonly Database _watchme = Database.Open(":memory:");

    public CapsuleScraperTests()
    {
        Exec("""
            CREATE TABLE entries (id INTEGER PRIMARY KEY AUTOINCREMENT, created_at TEXT NOT NULL, text TEXT NOT NULL, mood TEXT,
              uuid TEXT, starred INTEGER DEFAULT 0, pinned INTEGER DEFAULT 0, updated_at TEXT, content_format TEXT NOT NULL DEFAULT 'plain',
              text_plain TEXT NOT NULL DEFAULT '', hidden INTEGER DEFAULT 0, title TEXT, summary TEXT);
            CREATE TABLE tags (id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL UNIQUE);
            CREATE TABLE entry_tags (entry_id INTEGER NOT NULL, tag_id INTEGER NOT NULL, PRIMARY KEY (entry_id, tag_id));
            CREATE TABLE history (id INTEGER PRIMARY KEY AUTOINCREMENT, timestamp TEXT NOT NULL, operation_type TEXT NOT NULL,
              entry_id INTEGER NOT NULL, old_data TEXT NOT NULL, additional_data TEXT, undone INTEGER DEFAULT 0, redo_data TEXT);
            CREATE TABLE plugin_entry_locations (id INTEGER PRIMARY KEY AUTOINCREMENT, entry_uuid TEXT NOT NULL UNIQUE,
              latitude REAL NOT NULL, longitude REAL NOT NULL, place_name TEXT, weather_condition TEXT, weather_temp_c REAL, created_at TEXT NOT NULL);
            CREATE TABLE plugin_entry_media (id INTEGER PRIMARY KEY AUTOINCREMENT, entry_uuid TEXT NOT NULL, media_id INTEGER NOT NULL,
              position INTEGER NOT NULL DEFAULT 0, created_at TEXT NOT NULL);
            CREATE TABLE entry_continuations (child_entry_uuid TEXT PRIMARY KEY, parent_entry_uuid TEXT NOT NULL, updated_at TEXT);
            CREATE TABLE entry_thread_titles (thread_root_uuid TEXT PRIMARY KEY, title TEXT NOT NULL, updated_at TEXT NOT NULL);
            """);
    }

    public void Dispose()
    {
        _watchme.Dispose();
        SqliteConnection.ClearAllPools();
        File.Delete(_path);
    }

    private void Exec(string sql)
    {
        using var c = new SqliteConnection($"Data Source={_path};Pooling=False");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static string Local(long ms, bool seconds = false) =>
        DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime.ToString(seconds ? "yyyy-MM-dd HH:mm:ss" : "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    private void Entry(int id, string uuid, long at, string text, string? mood = null, string[]? tags = null, bool hidden = false, string? title = null)
    {
        Exec($"""
            INSERT INTO entries (id, uuid, created_at, updated_at, text, mood, hidden, title, content_format)
            VALUES ({id}, '{uuid}', '{Local(at)}', '{Local(at)}', '{text.Replace("'", "''")}', {Sql(mood)}, {(hidden ? 1 : 0)}, {Sql(title)}, 'markdown');
            """);
        SetTags(id, tags ?? []);
    }

    private void SetTags(int id, string[] tags)
    {
        Exec($"DELETE FROM entry_tags WHERE entry_id = {id};");
        foreach (var t in tags)
            Exec($"INSERT OR IGNORE INTO tags (name) VALUES ('{t}'); INSERT INTO entry_tags SELECT {id}, id FROM tags WHERE name = '{t}';");
    }

    /// <summary>One save as Capsule records it: a row per part, each with the state before it.</summary>
    private void Save(int id, string uuid, long at, string text, string? moodBefore, string[] tagsBefore, string? moodAfter, string[] tagsAfter, string? textAfter = null)
    {
        string Snapshot(string? mood, string[] tags, string body) =>
            $$"""{"id":{{id}},"uuid":"{{uuid}}","created_at":"x","text":"{{body}}","title":null,"mood":{{(mood is null ? "null" : $"\"{mood}\"")}},"tags":[{{string.Join(",", tags.Select(t => $"\"{t}\""))}}],"hidden":false}""";
        var after = textAfter ?? text;
        var stamp = Local(at, seconds: true);
        Exec($"""
            INSERT INTO history (timestamp, operation_type, entry_id, old_data) VALUES
              ('{stamp}', 'EDIT_TEXT', {id}, '{Snapshot(moodBefore, tagsBefore, text)}'),
              ('{stamp}', 'EDIT_MOOD', {id}, '{Snapshot(moodBefore, tagsBefore, after)}'),
              ('{stamp}', 'EDIT_TAGS', {id}, '{Snapshot(moodAfter, tagsBefore, after)}');
            UPDATE entries SET text = '{after}', mood = {Sql(moodAfter)} WHERE id = {id};
            """);
        SetTags(id, tagsAfter);
    }

    private long Stored(string kind)
    {
        using var cmd = _watchme.Connection.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM events WHERE source = 'capsule' AND kind = '{kind}';";
        return (long)cmd.ExecuteScalar()!;
    }

    private static string Sql(string? s) => s is null ? "NULL" : $"'{s.Replace("'", "''")}'";

    private IReadOnlyList<WatchEvent> Scrape(long? now = null)
    {
        var events = new CapsuleScraper(_path, backfillDays: 7).Scrape(new ScrapeContext(_watchme, now ?? Now));
        _watchme.Insert(events.ToList());
        return events;
    }

    [Fact]
    public void A_new_entry_has_its_words_mood_tags_place_and_photos()
    {
        Entry(1, "entry_a", Now - 10 * Min, "So I'm a **big** fan of Soda Stream Pepsi Max taste, but recently it has changed for the worse.",
            mood: "disappointed", tags: ["food", "soda"]);
        Exec($"""
            INSERT INTO plugin_entry_locations (entry_uuid, latitude, longitude, place_name, weather_condition, weather_temp_c, created_at)
              VALUES ('entry_a', 69.6, 18.9, 'Utsikten, Tromsø, Norway', 'Clear', -5.46, 'x');
            INSERT INTO plugin_entry_media (entry_uuid, media_id, created_at) VALUES ('entry_a', 1, 'x'), ('entry_a', 2, 'x');
            """);

        var e = Assert.Single(Scrape());
        Assert.Equal(Now - 10 * Min, e.Ts);
        Assert.Equal("capsule", e.Source);
        Assert.Equal("entry", e.Kind);
        Assert.Equal("entry:entry_a", e.ExtId);
        Assert.Equal("capsule-tauri.exe", e.App);
        Assert.Equal("Wrote “So I'm a big fan of Soda Stream Pepsi Max taste, but…” · 19 words · disappointed · #food #soda · 2 photos", e.Title);
        Assert.Contains("\"place\":\"Utsikten, Tromsø, Norway\"", e.Data);
        Assert.Contains("\"temp_c\":-5.5", e.Data);

        Scrape(Now + Min); // read again: the same entry, not stored twice
        Assert.Equal(1, Stored("entry"));
    }

    [Fact]
    public void Entries_are_told_apart_by_uuid_when_Capsule_renumbers_them()
    {
        Entry(1, "entry_a", Now - 10 * Min, "First");
        Scrape();
        Exec("UPDATE entries SET id = 2 WHERE uuid = 'entry_a';");
        Entry(1, "entry_b", Now - 5 * Min, "Second");

        Assert.Contains(Scrape(Now + Min), e => e.ExtId == "entry:entry_b");
        Assert.Equal(2, Stored("entry"));
    }

    [Fact]
    public void A_title_is_used_over_the_text_and_a_thread_is_named()
    {
        Entry(1, "entry_root", Now - 60 * Min, "Root");
        Entry(2, "entry_a", Now - 10 * Min, "Lots of text", title: "Tough Work Days");
        Exec("""
            INSERT INTO entry_continuations VALUES ('entry_a', 'entry_root', NULL);
            INSERT INTO entry_thread_titles VALUES ('entry_root', 'Work', 'x');
            """);

        var e = Scrape().Single(x => x.ExtId == "entry:entry_a");
        Assert.Equal("Wrote “Tough Work Days” · 3 words · continues “Work”", e.Title);
        Assert.Contains("\"continues\":\"entry_root\"", e.Data);
    }

    [Fact]
    public void A_hidden_entry_is_recorded_without_its_text()
    {
        Entry(1, "entry_a", Now - 10 * Min, "Something private", mood: "sad", hidden: true, title: "Secret");

        var e = Assert.Single(Scrape());
        Assert.Equal("Wrote a hidden entry · 2 words · sad", e.Title);
        Assert.DoesNotContain("private", e.Data);
        Assert.DoesNotContain("Secret", e.Data);
    }

    [Fact]
    public void A_save_that_adds_a_mood_and_tags_is_one_edit()
    {
        Entry(1, "entry_a", Now - 10 * Min, "On Saturday Ratna is leaving us");
        Scrape();
        Save(1, "entry_a", Now - 9 * Min, "On Saturday Ratna is leaving us", null, [], "ok", ["italy", "milan"]);

        var e = Assert.Single(Scrape(Now + Min), x => x.Kind == "edit");
        Assert.Equal(Now - 9 * Min, e.Ts);
        Assert.Equal("Edited “On Saturday Ratna is leaving us”: mood ok, tags +italy +milan", e.Title);
        Assert.Contains("\"changes\":[\"mood\",\"tags\"]", e.Data);

        Assert.DoesNotContain(Scrape(Now + 2 * Min), x => x.Kind == "edit");
        Assert.Equal(1, Stored("edit"));
    }

    [Fact]
    public void Text_changes_count_words_and_a_later_save_gives_the_earlier_one_its_after_state()
    {
        Entry(1, "entry_a", Now - 30 * Min, "One two");
        Save(1, "entry_a", Now - 20 * Min, "One two", null, [], "good", [], textAfter: "One two three four");
        Save(1, "entry_a", Now - 10 * Min, "One two three four", "good", [], "great", [], textAfter: "One two three");

        var edits = Scrape().Where(e => e.Kind == "edit").ToList();
        Assert.Equal(2, edits.Count);
        Assert.Equal("Edited “One two three four”: +2 words, mood good", edits[0].Title);
        Assert.Equal("Edited “One two three”: −1 word, mood good → great", edits[1].Title);
    }

    [Fact]
    public void A_save_that_changed_nothing_is_left_out()
    {
        Entry(1, "entry_a", Now - 10 * Min, "Same", mood: "ok", tags: ["work"]);
        Save(1, "entry_a", Now - 9 * Min, "Same", "ok", ["work"], "ok", ["work"]);

        Assert.DoesNotContain(Scrape(), e => e.Kind == "edit");
    }

    [Fact]
    public void A_deleted_entry_is_named()
    {
        Exec($$"""
            INSERT INTO history (timestamp, operation_type, entry_id, old_data) VALUES ('{{Local(Now - 5 * Min, true)}}', 'DELETE', 3,
              '{"id":3,"uuid":"entry_gone","text":"A test entry","title":null,"mood":null,"tags":[],"hidden":false}');
            """);

        var e = Assert.Single(Scrape());
        Assert.Equal("delete", e.Kind);
        Assert.Equal("Deleted “A test entry” (3 words)", e.Title);
    }

    [Fact]
    public void Excerpts_drop_markdown_and_are_cut_at_a_word()
    {
        Assert.Equal("Today I fixed the cover wall", JournalText.Excerpt("## Today\n\nI **fixed** the [cover wall](https://x) ![shot](a.png)"));
        Assert.Equal("One two three…", JournalText.Excerpt("One two three four five", 16));
    }

    [Fact]
    public void The_report_shows_an_entry_and_the_save_after_it_as_one_line_with_time_in_Capsule()
    {
        Entry(1, "entry_a", Now - 10 * Min, "On Saturday Ratna is leaving us");
        Scrape();
        Save(1, "entry_a", Now - 9 * Min, "On Saturday Ratna is leaving us", null, [], "ok", ["italy"]);
        Scrape(Now + Min);
        _watchme.Insert(
        [
            new WatchEvent(Now - 14 * Min, EventSources.Focus, "foreground") { App = "capsule-tauri.exe", Title = "Capsule" },
            new WatchEvent(Now - 8 * Min, EventSources.Focus, "foreground") { App = "Code.exe", Title = "x" },
        ]);

        var spans = DailyReport.LoadSpans(_watchme, Now - 120 * Min, Now + Min);
        var line = Assert.Single(DailyReport.LoadActivity(_watchme, Now - 120 * Min, Now + Min, spans));
        Assert.EndsWith("Capsule         Wrote “On Saturday Ratna is leaving us”: 6 words · ok · #italy, in Capsule 6m 00s", line);
        Assert.StartsWith(DateTimeOffset.FromUnixTimeMilliseconds(Now - 14 * Min).LocalDateTime.ToString("HH:mm") + "–", line);
    }

    [Fact]
    public void Capsules_own_settings_say_where_the_journal_is()
    {
        var settings = _path + ".json";
        try
        {
            File.WriteAllText(settings, """{"databasePath":"D:\\journal\\capsule.db","githubGistToken":"x"}""");
            Assert.Equal(@"D:\journal\capsule.db", CapsuleScraper.DbPathFromSettings(settings, "fallback"));
            File.WriteAllText(settings, "{}");
            Assert.Equal("fallback", CapsuleScraper.DbPathFromSettings(settings, "fallback"));
        }
        finally
        {
            File.Delete(settings);
        }
    }

    [Theory]
    [InlineData("How many journal entries did I write this week?")]
    [InlineData("When did I last write in my journal?")]
    public void Questions_about_the_journal_are_about_Capsule_entries(string question)
    {
        var q = QuestionParser.Parse(question, new DateTime(2026, 11, 27, 12, 0, 0, DateTimeKind.Local));
        Assert.Equal("journal", q.Topic?.Name);
        Assert.Empty(q.Terms);
    }
}
