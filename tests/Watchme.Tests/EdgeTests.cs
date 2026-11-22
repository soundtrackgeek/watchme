using Microsoft.Data.Sqlite;
using Watchme.Agent;
using Watchme.Agent.Scrapers;
using Watchme.Core;

namespace Watchme.Tests;

public sealed class EdgeTests : IDisposable
{
    private const long Min = 60_000;
    private const long EpochDeltaMicros = 11_644_473_600L * 1_000_000;
    private static readonly long Now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private readonly string _history = Path.Combine(Path.GetTempPath(), "watchme-history-" + Guid.NewGuid().ToString("N") + ".sqlite");
    private readonly Database _watchme = Database.Open(":memory:");
    private int _nextUrl;

    public EdgeTests()
    {
        using var db = Open();
        Exec(db, """
            CREATE TABLE urls (id INTEGER PRIMARY KEY, url TEXT, title TEXT);
            CREATE TABLE visits (id INTEGER PRIMARY KEY, url INTEGER, visit_time INTEGER, from_visit INTEGER, transition INTEGER);
            CREATE TABLE keyword_search_terms (keyword_id INTEGER, url_id INTEGER, term TEXT);
            """);
    }

    public void Dispose()
    {
        _watchme.Dispose();
        SqliteConnection.ClearAllPools();
        File.Delete(_history);
    }

    private SqliteConnection Open()
    {
        var db = new SqliteConnection($"Data Source={_history};Pooling=False");
        db.Open();
        return db;
    }

    private static void Exec(SqliteConnection db, string sql, params (string, object?)[] args)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (k, v) in args)
            cmd.Parameters.AddWithValue(k, v ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    private long Visit(long id, long ts, string url, string? title, long transition = 0x30000000, long fromVisit = 0, string? term = null)
    {
        using var db = Open();
        var urlId = ++_nextUrl;
        Exec(db, "INSERT INTO urls VALUES ($id, $url, $title)", ("$id", urlId), ("$url", url), ("$title", title));
        Exec(db, "INSERT INTO visits VALUES ($id, $url, $time, $from, $tr)",
            ("$id", id), ("$url", urlId), ("$time", ts * 1000 + EpochDeltaMicros), ("$from", fromVisit), ("$tr", transition));
        if (term is not null)
            Exec(db, "INSERT INTO keyword_search_terms VALUES (2, $url, $term)", ("$url", urlId), ("$term", term));
        return id;
    }

    private List<WatchEvent> Scrape()
    {
        var scraper = new EdgeHistoryScraper("unused", () => ["bank", "login."], backfillDays: 7);
        var events = scraper.ReadVisits(_history, "Default", new ScrapeContext(_watchme, Now)).ToList();
        _watchme.Insert(events);
        return events;
    }

    [Fact]
    public void Visits_are_read_without_frames_redirect_hops_or_query_strings()
    {
        Visit(1, Now - 30 * Min, "https://rateyourmusic.com/artist/tiesto?session=abc#top", "Tiësto - Rate Your Music");
        // A real page without CHAIN_END: kept.
        Visit(2, Now - 29 * Min, "https://rateyourmusic.com/release/x", "Magik One - Rate Your Music", transition: 0x10000000);
        Visit(3, Now - 28 * Min, "https://ads.example.com/frame", "ad", transition: 3);
        // A server redirect from 4 to 5: only 5 is a page you saw.
        Visit(4, Now - 27 * Min, "http://github.com/", null, transition: 0x10000001);
        Visit(5, Now - 27 * Min, "https://github.com/", "GitHub", transition: 0xA0000001, fromVisit: 4);
        Visit(6, Now - 26 * Min, "https://www.google.com/search?q=ti%C3%ABsto&sca=123", "tiësto - Google Search", term: "tiësto");
        Visit(7, Now - 25 * Min, "https://login.example.com/session?token=secret", "Sign in to Example");
        Visit(8, Now - 24 * Min, "https://mybank.no/accounts?id=1", "Accounts - My Bank");
        Visit(9, Now - 23 * Min, "edge://settings", "Settings");

        var events = Scrape();

        Assert.Equal([1, 2, 5, 6, 7, 8], events.Select(e => Convert.ToInt32(System.Text.Json.JsonDocument.Parse(e.Data!).RootElement.GetProperty("visit_id").GetInt64())));
        Assert.Equal(Now - 30 * Min, events[0].Ts);
        Assert.Equal("visit:Default:1", events[0].ExtId);
        Assert.Contains("\"url\":\"https://rateyourmusic.com/artist/tiesto\"", events[0].Data);
        Assert.DoesNotContain("session", events[0].Data);
        Assert.Contains("\"search\":\"tiësto\"", events[3].Data);
        Assert.DoesNotContain("sca=", events[3].Data);
        // Private sites keep only the host.
        Assert.Equal("login.example.com", events[4].Title);
        Assert.DoesNotContain("token", events[4].Data);
        Assert.Contains("\"url\":null", events[4].Data);
        Assert.Equal("mybank.no", events[5].Title);
        Assert.DoesNotContain("Accounts", events[5].Data);

        // A second read finds nothing new.
        Scrape();
        Assert.Equal(6, Convert.ToInt32(_watchme.Scalar("SELECT COUNT(*) FROM events WHERE source = 'edge'")));
    }

    [Fact]
    public void Report_shows_sites_per_sitting_and_searches()
    {
        Visit(1, Now - 90 * Min, "https://rateyourmusic.com/artist/tiesto", "Tiësto Albums - Rate Your Music");
        Visit(2, Now - 89 * Min, "https://rateyourmusic.com/release/x", "Magik One: First Flight by Tiësto (DJ Mix, Trance): Reviews, Ratings - Rate Your Music");
        Visit(3, Now - 88 * Min, "https://rateyourmusic.com/search?searchterm=tone+norum&searchtype=a", "Search - Rate Your Music");
        Visit(4, Now - 87 * Min, "https://github.com/jtill/watchme", "GitHub - jtill/watchme");
        Visit(5, Now - 86 * Min, "https://mybank.no/accounts", "Accounts - My Bank");
        // A new sitting after a break.
        Visit(6, Now - 10 * Min, "https://www.google.com/search?q=edge+history", "edge history - Google Search", term: "edge history");
        Scrape();

        var lines = DailyReport.LoadActivity(_watchme, Now - 120 * Min, Now + Min);

        Assert.Equal(4, lines.Count);
        Assert.EndsWith("Edge            5 pages: Rate Your Music 3 (Tiësto Albums, Magik One: First Flight by Tiësto), GitHub 1 (jtill/watchme), mybank.no 1", lines[0]);
        Assert.EndsWith("Edge            Searched Rate Your Music for “tone norum”", lines[1]);
        Assert.EndsWith("Edge            1 page: Google Search 1", lines[2]);
        Assert.EndsWith("Edge            Searched Google for “edge history”", lines[3]);
    }

    [Theory]
    [InlineData("id.example.com", true)]
    [InlineData("android.com", false)]
    [InlineData("www.mybank.no", true)]
    [InlineData("login.microsoftonline.com", true)]
    [InlineData("blogin.example.com", false)]
    public void Private_sites(string host, bool expected) =>
        Assert.Equal(expected, EdgeHistoryScraper.IsPrivate(host, ["bank", "login.", "id."]));

    [Theory]
    [InlineData("Tiësto Albums - Rate Your Music and 10 more pages - Personal - Microsoft​ Edge", "Tiësto Albums - Rate Your Music")]
    [InlineData("Inbox - Yahoo Mail - Personal - Microsoft Edge", "Inbox - Yahoo Mail")]
    [InlineData("Inbox - Yahoo Mail - Microsoft​ Edge", "Inbox - Yahoo Mail")]
    [InlineData("Notepad", "Notepad")]
    public void Browser_titles(string title, string expected) =>
        Assert.Equal(expected, WebApps.CleanBrowserTitle(title, ["Personal", "Profile 1"]));

    [Theory]
    [InlineData("chat.google.com-6003259E_fctm2g5w6wnb6!App", true)]
    [InlineData("MSEdge", false)]
    [InlineData("MSEdge.UserData.Profile2", false)]
    [InlineData("MSEdge._crx_abcdef", true)]
    public void Web_app_ids(string appId, bool expected) => Assert.Equal(expected, WebApps.IsWebApp(appId));
}
