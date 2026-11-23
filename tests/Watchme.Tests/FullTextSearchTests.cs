using Watchme.Core;

namespace Watchme.Tests;

public sealed class FullTextSearchTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "watchme-tests-" + Guid.NewGuid().ToString("N"));

    public FullTextSearchTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static Database Seeded()
    {
        var db = Database.Open(":memory:");
        db.Insert([
            new(1_000, EventSources.Edge, "visit") { ExtId = "1", Title = "Tiësto - Rate Your Music", Data = """{"url":"https://rateyourmusic.com/artist/tiesto"}""" },
            new(2_000, EventSources.Aurora, "play") { ExtId = "2", Title = "Despacito", Data = """{"artist":"Luis Fonsi","genre":"Reggaetón"}""" },
            new(3_000, EventSources.PowerShell, "command") { ExtId = "3", Title = "build", Data = """{"cmd":"dotnet build","cwd":"C:\\code\\watchme"}""" },
            new(4_000, EventSources.Focus, "focus") { App = "Code.exe", Title = "Øya festival plan" },
        ]);
        return db;
    }

    private static int Total(Database db, params string[] terms) => EventSearch.Run(db, new(terms)).Total;

    [Theory]
    [InlineData("tiesto")]
    [InlineData("TIËSTO")]
    [InlineData("Tiësto")]
    public void Case_and_accents_do_not_matter(string term)
    {
        using var db = Seeded();
        Assert.Equal(1, Total(db, term));
    }

    [Fact]
    public void Data_values_and_letters_with_a_stroke_fold_too()
    {
        using var db = Seeded();
        Assert.Equal(1, Total(db, "reggaeton"));
        Assert.Equal(1, Total(db, "oya"));
        Assert.Equal(1, Total(db, "c:\\code"));
    }

    [Fact]
    public void Json_key_names_are_not_text()
    {
        using var db = Seeded();
        Assert.Equal(0, Total(db, "artist", "genre", "cwd"));
        Assert.Equal(0, Total(db, "genre"));
    }

    [Fact]
    public void Search_syntax_in_a_term_is_just_text()
    {
        using var db = Seeded();
        Assert.Equal(0, Total(db, "tiesto OR build"));
        Assert.Equal(0, Total(db, "\"quoted\""));
        Assert.Equal(0, Total(db, "cmd:build*"));
    }

    [Fact]
    public void Short_terms_fall_back_to_like()
    {
        using var db = Seeded();
        Assert.Equal(1, Total(db, "Lu"));
        Assert.Equal(1, Total(db, "build", "C:"));
    }

    [Fact]
    public void The_index_follows_updates_and_deletes()
    {
        using var db = Seeded();
        db.Execute("UPDATE events SET title = 'Café del Mar', data = NULL WHERE ext_id = '3';");
        Assert.Equal(1, Total(db, "cafe"));
        Assert.Equal(0, Total(db, "dotnet"));

        db.Execute("DELETE FROM events WHERE ext_id = '1';");
        Assert.Equal(0, Total(db, "tiesto"));
        Assert.Equal(0L, db.Scalar("SELECT count(*) FROM events_fts WHERE events_fts MATCH '\"tiesto\"';"));
    }

    [Fact]
    public void Forgetting_and_redacting_take_text_out_of_the_index()
    {
        using var db = Seeded();
        Assert.Equal(1, Forgetting.Apply(db, new Forgetting.Selection(null, null, ["tiesto"]), false, 10_000).Removed);
        Assert.Equal(0, Total(db, "tiesto"));

        Forgetting.Apply(db, new Forgetting.Selection(null, null, ["fonsi"]), true, 10_000);
        Assert.Equal(0, Total(db, "reggaeton"));
    }

    [Fact]
    public void An_older_database_gets_its_rows_indexed()
    {
        var path = Path.Combine(_dir, "v3.db");
        using (var db = Database.Open(path))
        {
            db.Insert([new(1_000, EventSources.Edge, "visit") { ExtId = "1", Title = "Tiësto" }]);
            db.Execute("""
                DROP TRIGGER events_fts_insert; DROP TRIGGER events_fts_delete; DROP TRIGGER events_fts_update; DROP TABLE events_fts;
                PRAGMA user_version = 3;
                """);
        }
        using (var db = Database.Open(path))
        {
            Assert.Equal((long)Database.SchemaVersion, db.Scalar("PRAGMA user_version;"));
            Assert.Equal(1, Total(db, "tiesto"));
        }
    }

    [Fact]
    public void Folding_keeps_positions_so_highlights_land_on_the_original_text()
    {
        Assert.Equal("tiesto oya", TextMatch.Fold("Tiësto Øya"));
        Assert.Equal([(2, 6)], TextMatch.Find("a Tiësto b", "tiesto").ToList());
        Assert.Equal("a \u001b[1;33mTiësto\u001b[0m b", EventSearch.Highlight("a Tiësto b", ["tiesto"], color: true));
        Assert.Contains("Tiësto", EventSearch.Snippet(new string('x', 100) + " Tiësto " + new string('y', 100), ["tiesto"], 40));
    }
}
