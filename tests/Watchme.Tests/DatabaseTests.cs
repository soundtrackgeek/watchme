using Watchme.Core;

namespace Watchme.Tests;

public class DatabaseTests
{
    private const long Min = 60_000;

    private static WatchEvent Focus(long ts, string app, string title = "t", string kind = "foreground") =>
        new(ts, EventSources.Focus, kind) { App = app, Title = title };

    [Fact]
    public void Migration_sets_schema_version()
    {
        using var db = Database.Open(":memory:");
        Assert.Equal((long)Database.SchemaVersion, db.Scalar("PRAGMA user_version;"));
    }

    [Fact]
    public void Focus_spans_end_at_the_next_state_event_and_skip_idle()
    {
        using var db = Database.Open(":memory:");
        db.Insert([
            new WatchEvent(0, EventSources.Agent, "start"),
            Focus(0, "Code.exe"),
            Focus(10 * Min, "Code.exe", "other file", kind: "title"),
            new WatchEvent(15 * Min, EventSources.Idle, "idle_start"),
            new WatchEvent(40 * Min, EventSources.Idle, "idle_end"),
            Focus(40 * Min, "TOTALCMD64.EXE"),
            new WatchEvent(45 * Min, EventSources.Agent, "stop"),
        ]);

        var spans = DailyReport.LoadSpans(db, 0, 100 * Min);

        Assert.Collection(spans,
            s => Assert.Equal((0L, 10 * Min, "Code.exe"), (s.Start, s.End, s.App)),
            s => Assert.Equal((10 * Min, 15 * Min, "other file"), (s.Start, s.End, s.Title)),
            s => Assert.Equal((40 * Min, 45 * Min, "TOTALCMD64.EXE"), (s.Start, s.End, s.App)));
    }

    [Fact]
    public void Open_span_ends_at_last_heartbeat()
    {
        using var db = Database.Open(":memory:");
        db.Insert([new WatchEvent(0, EventSources.Agent, "start"), Focus(0, "Code.exe")]);
        db.SetMeta("last_alive", (7 * Min).ToString());

        var span = Assert.Single(DailyReport.LoadSpans(db, 0, 100 * Min));
        Assert.Equal(7 * Min, span.End);
    }

    [Fact]
    public void Unclean_run_is_closed_at_last_heartbeat_only_once()
    {
        using var db = Database.Open(":memory:");
        db.Insert([new WatchEvent(0, EventSources.Agent, "start"), Focus(0, "Code.exe")]);
        db.SetMeta("last_alive", (3 * Min).ToString());

        Assert.True(db.CloseUncleanRun());
        Assert.False(db.CloseUncleanRun());
        Assert.Equal(3 * Min, db.Scalar("SELECT ts FROM events WHERE kind = 'stop';"));
    }

    [Fact]
    public void Spans_are_clipped_to_the_requested_range()
    {
        using var db = Database.Open(":memory:");
        db.Insert([Focus(0, "Code.exe"), new WatchEvent(60 * Min, EventSources.Agent, "stop")]);

        var span = Assert.Single(DailyReport.LoadSpans(db, 30 * Min, 45 * Min));
        Assert.Equal(15 * Min, span.DurationMs);
    }
}
