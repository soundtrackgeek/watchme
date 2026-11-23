using Watchme.Cli;
using Watchme.Core;

namespace Watchme.Tests;

public class SearchAndRangeTests
{
    private const long Min = 60_000;

    // A Wednesday.
    private static readonly DateOnly Today = new(2026, 11, 18);

    private static long At(int dayOffset, int hour, int minute = 0) =>
        Dashboard.DayStart(Today.AddDays(dayOffset)) + (hour * 60 + minute) * Min;

    [Theory]
    [InlineData(null, "2026-11-18", "2026-11-18")]
    [InlineData("yesterday", "2026-11-17", "2026-11-17")]
    [InlineData("2026-11-01", "2026-11-01", "2026-11-01")]
    [InlineData("7d", "2026-11-12", "2026-11-18")]
    [InlineData("1d", "2026-11-18", "2026-11-18")]
    [InlineData("week", "2026-11-16", "2026-11-18")]
    [InlineData("last-week", "2026-11-09", "2026-11-15")]
    [InlineData("month", "2026-11-01", "2026-11-18")]
    [InlineData("last-month", "2026-10-01", "2026-10-31")]
    [InlineData("2026-11-10..2026-11-12", "2026-11-10", "2026-11-12")]
    [InlineData("2026-11-12..2026-11-10", "2026-11-10", "2026-11-12")]
    [InlineData("2026-11-10..", "2026-11-10", "2026-11-18")]
    [InlineData("2026-11-10..yesterday", "2026-11-10", "2026-11-17")]
    public void Ranges_are_read(string? arg, string from, string to)
    {
        Assert.True(ReportRange.TryParse(arg, Today, out var f, out var t));
        Assert.Equal((DateOnly.Parse(from), DateOnly.Parse(to)), (f, t));
    }

    [Theory]
    [InlineData("0d")]
    [InlineData("400d")]
    [InlineData("fortnight")]
    [InlineData("2026-13-01")]
    [InlineData("2020-01-01..2026-01-01")]
    public void Bad_ranges_are_refused(string arg) =>
        Assert.False(ReportRange.TryParse(arg, Today, out _, out _));

    private static Database Seeded()
    {
        var db = Database.Open(":memory:");
        db.Insert([
            new(At(-3, 9), EventSources.PowerShell, "command") { ExtId = "1", Title = "build", Data = """{"cmd":"dotnet build","cwd":"C:\\code\\watchme"}""" },
            new(At(-1, 10), EventSources.Edge, "visit") { ExtId = "2", Title = "Tiësto - Rate Your Music", Data = """{"url":"https://rateyourmusic.com/artist/tiesto","host":"rateyourmusic.com"}""" },
            new(At(-2, 11), EventSources.Edge, "visit") { ExtId = "3", Title = "Your music", Data = """{"url":"https://example.com/rate"}""" },
            new(At(0, 8), EventSources.Process, "start") { App = "dotnet.exe" },
        ]);
        return db;
    }

    [Fact]
    public void Every_term_must_match_and_titles_and_data_both_count()
    {
        using var db = Seeded();

        // "rate" is in the second visit's URL only, "music" in its title: both still match.
        var both = EventSearch.Run(db, new(["rate", "music"]));
        Assert.Equal(2, both.Total);
        Assert.Equal(["Tiësto - Rate Your Music", "Your music"], both.Hits.Select(h => h.Title)); // newest first, by time

        // A phrase is one term.
        Assert.Equal(1, EventSearch.Run(db, new(["rate your music"])).Total);
    }

    [Fact]
    public void Processes_are_left_out_unless_asked_for_and_dates_limit()
    {
        using var db = Seeded();

        Assert.Equal(EventSources.PowerShell, Assert.Single(EventSearch.Run(db, new(["dotnet"])).Hits).Source);
        Assert.Equal(EventSources.Process, Assert.Single(EventSearch.Run(db, new(["dotnet"], Source: EventSources.Process)).Hits).Source);
        Assert.Equal(0, EventSearch.Run(db, new(["dotnet"], FromMs: At(-2, 0))).Total);
        Assert.Equal(1, EventSearch.Run(db, new(["music"], FromMs: At(-2, 0), ToMs: At(-1, 0))).Total);
    }

    [Fact]
    public void Total_counts_beyond_the_limit()
    {
        using var db = Seeded();
        var r = EventSearch.Run(db, new(["music"], Limit: 1));
        Assert.Equal(2, r.Total);
        Assert.Single(r.Hits);
    }

    [Fact]
    public void The_data_field_that_matched_is_shown_when_the_title_does_not_show_it()
    {
        var hit = new EventSearch.Hit(1, 0, EventSources.PowerShell, "command", null, "build", """{"cmd":"dotnet build","cwd":"C:\\code"}""");
        Assert.Equal(("cmd", "dotnet build"), EventSearch.MatchedField(hit, ["dotnet"]));
        Assert.Null(EventSearch.MatchedField(hit, ["build"]));
        Assert.Equal(("cwd", @"C:\code"), EventSearch.MatchedField(hit, ["code"]));

        var visit = new EventSearch.Hit(2, 0, EventSources.Edge, "visit", null, "Releases",
            """{"host":"github.com","url":"https://github.com/pingdotgg/t3code/releases"}""");
        Assert.Equal("url", EventSearch.MatchedField(visit, ["github"])?.Key);
    }

    [Fact]
    public void Snippets_keep_the_match_in_view_and_highlight_marks_it()
    {
        var url = "https://example.com/" + new string('x', 100) + "/tiesto/" + new string('y', 50);
        var snip = EventSearch.Snippet(url, ["tiesto"], 40);
        Assert.Equal(40, snip.Length);
        Assert.Contains("tiesto", snip);
        Assert.StartsWith("…", snip);

        Assert.Equal("a \e[1;33mTiësto\e[0m b", EventSearch.Highlight("a Tiësto b", ["tiësto"], color: true));
        Assert.Equal("a Tiësto b", EventSearch.Highlight("a Tiësto b", ["tiësto"], color: false));
    }

    [Fact]
    public void Printed_results_group_by_day_and_say_when_there_are_more()
    {
        using var db = Seeded();
        var terms = new[] { "rateyourmusic" };
        var w = new StringWriter();
        EventSearch.Print(EventSearch.Run(db, new(terms)), terms, w, color: false);
        var text = w.ToString();
        Assert.Contains("1 event matches rateyourmusic", text);
        Assert.Contains("url: https://rateyourmusic.com/artist/tiesto", text);

        w = new StringWriter();
        EventSearch.Print(EventSearch.Run(db, new(["music"], Limit: 1)), ["music"], w, color: false);
        Assert.Contains("Showing the newest 1 of 2", w.ToString());
    }

    [Theory]
    [InlineData("edge", EventSources.Edge)]
    [InlineData("Total Commander", EventSources.TotalCommander)]
    [InlineData("tc", EventSources.TotalCommander)]
    [InlineData("pwsh", EventSources.PowerShell)]
    [InlineData("aurora-ng", EventSources.AuroraNg)]
    [InlineData("T3", EventSources.T3)]
    [InlineData("nope", null)]
    public void Sources_are_found_by_id_name_or_alias(string name, string? source) =>
        Assert.Equal(source, SearchCommand.ResolveSource(name));

    [Fact]
    public void Range_report_sums_the_days_and_lists_highlights()
    {
        using var db = Database.Open(":memory:");
        db.Insert([
            new WatchEvent(At(-2, 9), EventSources.Agent, "start"),
            new WatchEvent(At(-2, 9), EventSources.Focus, "foreground") { App = "Code.exe", Title = "a.cs" },
            new WatchEvent(At(-2, 10), EventSources.Agent, "stop"),
            new WatchEvent(At(0, 9), EventSources.Agent, "start"),
            new WatchEvent(At(0, 9), EventSources.Focus, "foreground") { App = "Code.exe", Title = "b.cs" },
            new WatchEvent(At(0, 11), EventSources.Agent, "stop"),
            new(At(0, 10), EventSources.Git, "commit") { ExtId = "c1", Data = """{"repo":"watchme"}""" },
        ]);

        var w = new StringWriter();
        RangeReport.Print(db, Today.AddDays(-2), Today, w);
        var text = w.ToString();

        Assert.Contains("(3 days)", text);
        Assert.Contains("3h 00m", text);       // total focus
        Assert.Contains("on 2 of 3 days", text);
        Assert.Contains("1h 30m a day", text);
        Assert.Contains("Code.exe", text);
        Assert.Contains("1 commit", text);
        Assert.DoesNotContain("1 commits", text);
        Assert.Contains("Repos: watchme 1", text);
    }

    [Fact]
    public void Range_report_says_when_nothing_was_recorded()
    {
        using var db = Database.Open(":memory:");
        var w = new StringWriter();
        RangeReport.Print(db, Today.AddDays(-6), Today, w);
        Assert.Contains("Nothing recorded", w.ToString());
    }
}
