using System.Text;
using System.Text.Json.Nodes;
using Watchme.Agent.Scrapers;
using Watchme.Core;

namespace Watchme.Tests;

/// <summary>Flipside's and Forever's activity logs: <see cref="ActivityLogScraper"/> and the report lines.</summary>
public sealed class MusicAppActivityTests : IDisposable
{
    private const long Min = 60_000;
    private static readonly long Now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private readonly string _logs = Path.Combine(Path.GetTempPath(), "watchme-activity-" + Guid.NewGuid().ToString("N"));
    private readonly Database _watchme = Database.Open(":memory:");
    private int _id;

    public MusicAppActivityTests() => Directory.CreateDirectory(_logs);

    public void Dispose()
    {
        _watchme.Dispose();
        Directory.Delete(_logs, recursive: true);
    }

    /// <summary>A line as the apps write it: ts, a unique id, the event, then its fields.</summary>
    private string Line(long ts, string evt, string fields)
    {
        var o = (JsonObject)JsonNode.Parse(fields)!;
        o["ts"] = ts;
        o["id"] = $"{ts}-99-{_id++}";
        o["event"] = evt;
        return o.ToJsonString();
    }

    private List<WatchEvent> Scrape(string content, string source = EventSources.Forever)
    {
        File.AppendAllText(Path.Combine(_logs, "activity.2026-11-23.jsonl"), content, new UTF8Encoding(false));
        var events = new ActivityLogScraper(source, source + ".exe", _logs, backfillDays: 7)
            .Scrape(new ScrapeContext(_watchme, Now)).ToList();
        _watchme.Insert(events);
        return events;
    }

    [Fact]
    public void Lines_become_events_and_a_line_being_written_waits()
    {
        var search = Line(Now - 10 * Min, "search", """{"service":"soulseek","scope":"network","query":"inxs remixes"}""");
        var browse = Line(Now - 9 * Min, "browse", """{"service":"soulseek","username":"alice","ok":true,"files":12345}""");
        var events = Scrape(search + "\nnot json\n" + browse[..20]);

        var e = Assert.Single(events);
        Assert.Equal(("forever", "search", "Searched Soulseek for “inxs remixes”"), (e.Source, e.Kind, e.Title));
        Assert.Equal(Now - 10 * Min, e.Ts);
        Assert.Equal("forever.exe", e.App);
        Assert.StartsWith("activity:", e.ExtId);
        Assert.Contains("\"query\":\"inxs remixes\"", e.Data);
        Assert.DoesNotContain("\"event\"", e.Data);

        // The rest of the browse arrives; the search is read again but is not new.
        Scrape(browse[20..] + "\n");
        Assert.Equal(2, Convert.ToInt32(_watchme.Scalar("SELECT COUNT(*) FROM events WHERE source = 'forever'")));
        Assert.Equal("Browsed alice's shares (12,345 files)",
            _watchme.Scalar("SELECT title FROM events WHERE kind = 'browse'"));
    }

    [Theory]
    [InlineData("search", """{"service":"catalog","query":"tiësto"}""", "Searched the catalog for “tiësto”")]
    [InlineData("search", """{"service":"soulseek","scope":"user","username":"bob","query":"flac"}""", "Searched bob's shares for “flac”")]
    [InlineData("browse", """{"service":"soulseek","username":"bob","ok":false}""", "Tried to browse bob's shares (no answer)")]
    [InlineData("wanted_add", """{"service":"soulseek","artist":"INXS","title":"INXS² The Remixes","year":2004}""", "Added INXS – INXS² The Remixes (2004) to Wanted")]
    [InlineData("download_queued", """{"service":"soulseek","username":"DEMONAOR","title":"New Drama - Fantasized (1984)","files":11,"bytes":125829120,"origin":"smart"}""",
        "Queued New Drama - Fantasized (1984) from DEMONAOR (11 files, 120.0 MB) — Smart downloads")]
    [InlineData("download", """{"service":"soulseek","username":"alice","users":["alice","bob"],"title":"Anna Book - Let's Dance (2006)","files":14,"bytes":2147483648}""",
        "Downloaded Anna Book - Let's Dance (2006) from alice, bob (14 files, 2.0 GB)")]
    [InlineData("download", """{"service":"deezer","artist":"Tiësto","album":"Just Be","year":2004,"title":"Tiësto - Just Be","quality":"FLAC","files":12}""",
        "Downloaded Tiësto – Just Be (2004) from Deezer (FLAC, 12 tracks)")]
    [InlineData("download", """{"service":"usenet","artist":"Milk Inc.","album":"Closer","title":"Milk_Inc-Closer-2003-FLAC","indexer":"NZBgeek","bytes":524288000}""",
        "Downloaded Milk Inc. – Closer from Usenet (NZBgeek, 500.0 MB)")]
    public void Titles_say_what_was_done_where(string kind, string fields, string title) =>
        Assert.Equal(title, MusicAppActivity.Title(kind, (JsonObject)JsonNode.Parse(fields)!));

    [Fact]
    public void Report_groups_a_sitting_and_leaves_out_queued_downloads()
    {
        Scrape(string.Join("\n",
            Line(Now - 50 * Min, "search", """{"service":"soulseek","query":"inxs"}"""),
            Line(Now - 49 * Min, "search", """{"service":"soulseek","query":"INXS"}"""),
            Line(Now - 48 * Min, "search", """{"service":"soulseek","scope":"user","username":"alice","query":"remixes"}"""),
            Line(Now - 47 * Min, "browse", """{"service":"soulseek","username":"alice","ok":true}"""),
            Line(Now - 46 * Min, "browse", """{"service":"soulseek","username":"bob","ok":false}"""),
            Line(Now - 45 * Min, "wanted_add", """{"artist":"INXS","title":"X","year":1990}"""),
            Line(Now - 44 * Min, "wanted_add", """{"artist":"INXS","title":"Kick","year":1987}"""),
            Line(Now - 43 * Min, "download_queued", """{"service":"soulseek","username":"alice","title":"INXS - Kick (1987)"}"""),
            Line(Now - 40 * Min, "download", """{"service":"soulseek","username":"alice","title":"INXS - Kick (1987)","files":10,"bytes":1048576}"""),
            Line(Now - 39 * Min, "download", """{"service":"soulseek","username":"bob","title":"INXS - X (1990)","files":12,"bytes":1048576}"""),
            // More than 30 minutes later: a new sitting.
            Line(Now - 5 * Min, "wanted_add", """{"artist":"Tiësto","title":"Elements of Life","year":2007}""")) + "\n");

        var lines = DailyReport.LoadActivity(_watchme, Now - 120 * Min, Now + Min);

        Assert.Equal(5, lines.Count);
        Assert.EndsWith("Forever         Searched Soulseek for “inxs”; alice's shares for “remixes”", lines[0]);
        Assert.EndsWith("Browsed the shares of 2 users: alice, bob (no answer)", lines[1]);
        Assert.EndsWith("Added 2 albums to Wanted: INXS – X (1990); INXS – Kick (1987)", lines[2]);
        Assert.EndsWith("Downloaded 2 albums (2.0 MB): INXS - Kick (1987) from alice; INXS - X (1990) from bob", lines[3]);
        Assert.EndsWith("Added Tiësto – Elements of Life (2007) to Wanted", lines[4]);
        Assert.DoesNotContain(lines, l => l.Contains("Queued"));
    }

    [Fact]
    public void Each_app_is_its_own_source()
    {
        Scrape(Line(Now - 5 * Min, "search", """{"service":"catalog","query":"tiësto"}""") + "\n", EventSources.Flipside);
        var line = Assert.Single(DailyReport.LoadActivity(_watchme, Now - 60 * Min, Now + Min));
        Assert.Contains("Flipside        Searched the catalog for “tiësto”", line);
    }
}
