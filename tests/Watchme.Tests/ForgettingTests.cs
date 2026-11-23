using System.Text.Json.Nodes;
using Watchme.Core;

namespace Watchme.Tests;

public sealed class ForgettingTests : IDisposable
{
    private const long Min = 60_000;
    private const long T0 = 1_800_000_000_000; // some time in 2027, well in the past of "now" below
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "watchme-tests-" + Guid.NewGuid().ToString("N"));

    public ForgettingTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static long At(int minute) => T0 + minute * Min;

    private static WatchEvent Focus(int minute, string app, string title) =>
        new(At(minute), EventSources.Focus, "foreground") { App = app, Exe = $@"C:\{app}", Title = title };

    private static WatchEvent Visit(int minute, string url, string title) =>
        new(At(minute), EventSources.Edge, "visit") { App = "msedge.exe", Title = title, Data = $$"""{"url":"{{url}}","visit_id":{{minute}}}""", ExtId = $"visit:{minute}" };

    private static long Count(Database db, string where) => (long)db.Scalar($"SELECT COUNT(*) FROM events WHERE {where};")!;

    /// <summary>Active ms per app from the focus_spans view, up to <paramref name="end"/>.</summary>
    private static Dictionary<string, long> Active(Database db, long end)
    {
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = "SELECT app, SUM(MIN(end_ts, $end) - start_ts) FROM focus_spans WHERE start_ts < $end GROUP BY app;";
        cmd.Parameters.AddWithValue("$end", end);
        var result = new Dictionary<string, long>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            result[r.GetString(0)] = r.GetInt64(1);
        return result;
    }

    private static Database Day()
    {
        var db = Database.Open(":memory:");
        db.Insert(
        [
            Focus(0, "code.exe", "watchme"),
            Focus(10, "msedge.exe", "My Bank - Accounts"),
            Visit(11, "https://mybank.example/accounts", "My Bank - Accounts"),
            new WatchEvent(At(12), EventSources.Session, "lock"),
            new WatchEvent(At(13), EventSources.Session, "unlock"),
            Focus(13, "msedge.exe", "My Bank - Transfer"),
            Visit(14, "https://mybank.example/transfer", "My Bank - Transfer"),
            Focus(20, "code.exe", "watchme"),
            Visit(25, "https://learn.microsoft.com/dotnet", "dotnet docs"),
            new WatchEvent(At(30), EventSources.Agent, "stop"),
        ]);
        return db;
    }

    [Fact]
    public void Forgetting_a_stretch_removes_it_and_keeps_the_time_around_it_right()
    {
        using var db = Day();
        var plan = Forgetting.Apply(db, new Forgetting.Selection(At(10), At(18), []), redact: false, now: At(60));

        // Both bank focus events and both bank visits; lock and unlock stay.
        Assert.Equal(4, plan.Removed);
        Assert.Equal(0, Count(db, "title LIKE '%Bank%'"));
        Assert.Equal(2, Count(db, "source = 'session' AND kind IN ('lock', 'unlock')"));
        Assert.Equal(1, Count(db, "source = 'session' AND kind = 'forget'"));

        var active = Active(db, At(30));
        Assert.Equal(10 * Min, active["code.exe"] - 10 * Min);  // 0–10 and 20–30
        // Edge had focus from 13 to 20; the part after the window (18–20) still counts, without its title.
        Assert.Equal(2 * Min, active["msedge.exe"]);
        Assert.Equal(1, Count(db, $"source = 'focus' AND ts = {At(18)} AND app = 'msedge.exe' AND title IS NULL"));
    }

    [Fact]
    public void A_forgotten_stretch_keeps_a_title_from_before_it()
    {
        using var db = Day();
        Forgetting.Apply(db, new Forgetting.Selection(At(22), At(27), []), redact: false, now: At(60));
        // code.exe had focus since minute 20, before the window: picked up again at 27 with its title.
        Assert.Equal(1, Count(db, $"source = 'focus' AND ts = {At(27)} AND title = 'watchme'"));
        Assert.Equal(15 * Min, Active(db, At(30))["code.exe"]); // 0–10, 20–22, 27–30
    }

    [Fact]
    public void Forgotten_app_history_is_not_read_again()
    {
        using var db = Day();
        Forgetting.Apply(db, new Forgetting.Selection(null, null, ["mybank"]), redact: false, now: At(60));
        Assert.Equal(0, Count(db, "source = 'edge' AND data LIKE '%mybank%'"));

        // The scraper's cursor moved back, so it reads the same visits again.
        db.Insert([Visit(11, "https://mybank.example/accounts", "My Bank - Accounts"), Visit(14, "https://mybank.example/transfer", "x"), Visit(40, "https://example.org", "new")]);
        Assert.Equal(0, Count(db, "source = 'edge' AND data LIKE '%mybank%'"));
        Assert.Equal(1, Count(db, "source = 'edge' AND title = 'new'"));
    }

    [Fact]
    public void Forgetting_words_blanks_focus_titles_but_keeps_their_time()
    {
        using var db = Day();
        var before = Active(db, At(30));
        var plan = Forgetting.Apply(db, new Forgetting.Selection(null, null, ["bank"]), redact: false, now: At(60));

        Assert.Equal(2, plan.Removed);       // the visits
        Assert.Equal(2, plan.RedactedCount); // the focus titles
        Assert.Equal(before, Active(db, At(30)));
        Assert.Equal(2, Count(db, "source = 'focus' AND title = '[redacted]'"));
        Assert.Equal(0, Count(db, "source = 'session' AND kind = 'forget'"));
        Assert.Empty(Forgetting.Windows(db)); // words don't forget time
        Assert.Equal(1, Count(db, "source = 'edge'"));
    }

    [Fact]
    public void Redacting_keeps_events_without_their_text()
    {
        using var db = Day();
        var plan = Forgetting.Apply(db, new Forgetting.Selection(At(10), At(18), []), redact: true, now: At(60));
        Assert.Equal(0, plan.Removed);
        Assert.Equal(4, plan.RedactedCount);
        Assert.Equal(0, Count(db, "title LIKE '%Bank%' OR data LIKE '%mybank%'"));
        Assert.Equal(1, Count(db, "source = 'edge' AND data LIKE '%\"visit_id\":11%'"));
        Assert.Equal(Active(Day(), At(30)), Active(db, At(30)));
        Assert.True(Assert.Single(Forgetting.Windows(db)).Redact);

        // Nothing left to do the second time.
        Assert.Empty(Forgetting.Preview(db, new Forgetting.Selection(At(10), At(18), []), redact: true).Items);
    }

    [Fact]
    public void One_source_in_a_stretch()
    {
        using var db = Day();
        var plan = Forgetting.Apply(db, new Forgetting.Selection(At(0), At(30), [], EventSources.Edge), redact: false, now: At(60));
        Assert.Equal(3, plan.Removed);
        Assert.Equal(0, Count(db, "source = 'edge'"));
        Assert.Equal(4, Count(db, "source = 'focus' AND title IS NOT NULL AND title <> '[redacted]'"));
        Assert.Equal(EventSources.Edge, Assert.Single(Forgetting.Windows(db)).Source);
    }

    [Fact]
    public void One_event_by_id()
    {
        using var db = Day();
        var id = (long)db.Scalar("SELECT id FROM events WHERE ext_id = 'visit:25';")!;
        var plan = Forgetting.Apply(db, new Forgetting.Selection(null, null, [], EventId: id), redact: false, now: At(60));
        Assert.Equal(1, plan.Removed);
        Assert.Equal(2, Count(db, "source = 'edge'"));
        Assert.Empty(Forgetting.Windows(db));
    }

    [Fact]
    public void App_history_read_later_goes_through_the_windows()
    {
        var windows = new List<Forgetting.Window>
        {
            new(At(10), At(20), Redact: false, Source: null),
            new(At(30), At(40), Redact: true, Source: null),
            new(At(50), At(60), Redact: false, Source: EventSources.PowerShell),
        };
        var kept = Forgetting.Filter(
            [Visit(5, "https://a", "a"), Visit(15, "https://b", "b"), Visit(35, "https://c", "c"), Visit(55, "https://d", "d")],
            windows, out var dropped, out var redacted);
        Assert.Equal(1, dropped);
        Assert.Equal(1, redacted);
        Assert.Equal(["a", Forgetting.Redacted, "d"], kept.Select(e => e.Title));
        Assert.Equal("visit:35", kept[1].ExtId);
    }

    [Fact]
    public void Redacted_data_keeps_numbers_and_shape()
    {
        var json = Forgetting.RedactData("""{"url":"https://x","visit_id":42,"items":[{"from":"C:\\a","n":2}],"ok":true,"none":null}""");
        var node = JsonNode.Parse(json!)!;
        Assert.Equal(Forgetting.Redacted, (string?)node["url"]);
        Assert.Equal(42, (int)node["visit_id"]!);
        Assert.Equal(Forgetting.Redacted, (string?)node["items"]![0]!["from"]);
        Assert.Equal(2, (int)node["items"]![0]!["n"]!);
        Assert.True((bool)node["ok"]!);
        Assert.Null(Forgetting.RedactData(null));
        Assert.Null(Forgetting.RedactData("not json"));
    }

    [Theory]
    [InlineData("15m", "11:45", "12:00")]
    [InlineData("1h30m", "10:30", "12:00")]
    [InlineData("45", "11:15", "12:00")]
    [InlineData("10:10..10:35", "10:10", "10:35")]
    [InlineData("11:30..now", "11:30", "12:00")]
    [InlineData("11:30..13:00", "11:30", "12:00")] // never past now
    public void Windows_are_read(string text, string from, string to)
    {
        var now = new DateTime(2026, 11, 23, 12, 0, 0, DateTimeKind.Local);
        Assert.True(Forgetting.TryParseWindow(text, now, null, out var f, out var t, out var error), error);
        long Ms(string hhmm) => new DateTimeOffset(now.Date + TimeSpan.Parse(hhmm)).ToUnixTimeMilliseconds();
        Assert.Equal(Ms(from), f);
        Assert.Equal(Ms(to), t);
    }

    [Fact]
    public void A_window_on_another_day()
    {
        var now = new DateTime(2026, 11, 23, 12, 0, 0, DateTimeKind.Local);
        Assert.True(Forgetting.TryParseWindow("22:00..24:00", now, new DateOnly(2026, 11, 22), out var f, out var t, out _));
        Assert.Equal(2 * 3_600_000L, t - f);
        Assert.Equal(new DateTimeOffset(new DateTime(2026, 11, 23, 0, 0, 0, DateTimeKind.Local)).ToUnixTimeMilliseconds(), t);
    }

    [Theory]
    [InlineData("13:00..14:00")] // the future
    [InlineData("11:00..10:00")]
    [InlineData("0m")]
    [InlineData("soon")]
    [InlineData("10:00..")]
    public void Bad_windows_are_refused(string text) =>
        Assert.False(Forgetting.TryParseWindow(text, new DateTime(2026, 11, 23, 12, 0, 0, DateTimeKind.Local), null, out _, out _, out _));

    [Fact]
    public void A_version_2_database_gets_the_new_tables()
    {
        var path = Path.Combine(_dir, "old.db");
        using (var db = Database.Open(path))
        {
            db.Insert([Visit(1, "https://a", "a")]);
            db.Execute("""
                DROP TABLE forgotten; DROP TABLE forgotten_windows;
                DROP TRIGGER events_fts_insert; DROP TRIGGER events_fts_delete; DROP TRIGGER events_fts_update; DROP TABLE events_fts;
                PRAGMA user_version = 2;
                """);
        }
        using (var db = Database.Open(path))
        {
            Assert.Equal((long)Database.SchemaVersion, db.Scalar("PRAGMA user_version;"));
            Assert.Equal(1, Forgetting.Apply(db, new Forgetting.Selection(null, null, ["https://a"]), false, At(60)).Removed);
            db.Insert([Visit(1, "https://a", "a")]);
            Assert.Equal(0, Count(db, "source = 'edge'"));
        }
    }
}
