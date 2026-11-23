using Watchme.Agent;
using Watchme.Agent.Scrapers;
using Watchme.Core;

namespace Watchme.Tests;

public sealed class PauseAndHealthTests : IDisposable
{
    private const long Min = 60_000;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "watchme-tests-" + Guid.NewGuid().ToString("N"));

    public PauseAndHealthTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private string PauseFile => Path.Combine(_dir, "pause.json");

    [Theory]
    [InlineData(null, 30)]
    [InlineData("45", 45)]
    [InlineData("30m", 30)]
    [InlineData("2h", 120)]
    [InlineData("1h30m", 90)]
    [InlineData("1h 15min", 75)]
    [InlineData("14:30", 150)]        // now is 12:00
    [InlineData("until 11:00", 1380)] // tomorrow
    public void Durations_are_read(string? text, int minutes)
    {
        Assert.True(PauseState.TryParseDuration(text, new DateTime(2026, 11, 23, 12, 0, 0), out var d, out var error), error);
        Assert.Equal(TimeSpan.FromMinutes(minutes), d);
    }

    [Theory]
    [InlineData("0m")]
    [InlineData("25h")]
    [InlineData("soon")]
    [InlineData("h")]
    public void Bad_durations_are_refused(string text) =>
        Assert.False(PauseState.TryParseDuration(text, new DateTime(2026, 11, 23, 12, 0, 0), out _, out _));

    [Fact]
    public void A_pause_is_a_window_until_its_end_or_an_early_resume()
    {
        using var db = Database.Open(":memory:");
        PauseState.Begin(db, PauseFile, 1000 * Min, 1030 * Min);
        Assert.NotNull(PauseState.Active(PauseFile, 1010 * Min));
        Assert.Null(PauseState.Active(PauseFile, 1030 * Min));
        Assert.Equal([(1000 * Min, 1030 * Min)], PauseState.Windows(db));

        // Shortened by resuming early.
        PauseState.Begin(db, PauseFile, 2000 * Min, 2060 * Min);
        var ended = PauseState.End(db, PauseFile, 2010 * Min);
        Assert.Equal(2000 * Min, ended!.Since);
        Assert.False(File.Exists(PauseFile));
        Assert.Equal((2000 * Min, 2010 * Min), PauseState.Windows(db)[1]);

        Assert.True(PauseState.Covers(PauseState.Windows(db), 1015 * Min));
        Assert.False(PauseState.Covers(PauseState.Windows(db), 1030 * Min));
        Assert.Single(PauseState.Windows(db, 1500 * Min));
    }

    [Fact]
    public void Changing_a_pause_keeps_its_start_and_the_newer_end_wins()
    {
        using var db = Database.Open(":memory:");
        PauseState.Begin(db, PauseFile, 1000 * Min, 1060 * Min);
        var changed = PauseState.Begin(db, PauseFile, 1010 * Min, 1020 * Min);

        Assert.Equal(1000 * Min, changed.Since);
        Assert.Null(PauseState.Active(PauseFile, 1025 * Min));
        // The first pause is cut where the second begins; together they cover 1000–1020.
        Assert.Equal([(1000 * Min, 1010 * Min), (1010 * Min, 1020 * Min)], PauseState.Windows(db));
    }

    [Fact]
    public void A_pause_closes_the_focus_span_and_tracking_resumes_by_itself()
    {
        using var db = Database.Open(":memory:");
        db.Insert([
            new WatchEvent(1000 * Min, EventSources.Agent, "start"),
            new WatchEvent(1000 * Min, EventSources.Focus, "foreground") { App = "msedge.exe", Title = "Bank" },
        ]);
        PauseState.Begin(db, PauseFile, 1010 * Min, 1040 * Min);

        var events = new List<WatchEvent>();
        var monitor = new PauseMonitor(PauseFile, events.Add);
        var probe = new FakeProbe { Window = new WindowInfo(1, 100, "msedge.exe", null, "Bank") };
        var tracker = new FocusTracker(probe, new WatchmeConfig(), events.Add) { IsPaused = monitor.IsPaused };

        tracker.Tick(1020 * Min);
        Assert.Empty(events);

        tracker.Tick(1041 * Min);
        Assert.Equal(["resume", "foreground"], events.Select(e => e.Kind));
        Assert.Equal(1040 * Min, events[0].Ts);
        Assert.False(File.Exists(PauseFile));

        db.Insert(events);
        db.Insert([new WatchEvent(1050 * Min, EventSources.Agent, "stop")]);
        var spans = DailyReport.LoadSpans(db, 0, long.MaxValue);
        Assert.Equal([(1000 * Min, 1010 * Min), (1041 * Min, 1050 * Min)], spans.Select(s => (s.Start, s.End)));
    }

    private sealed class FakeProbe : IDesktopProbe
    {
        public WindowInfo? Window;
        public WindowInfo? GetForegroundWindow() => Window;
        public uint GetIdleMilliseconds() => 0;
    }

    private sealed class FakeScraper(Func<ScrapeContext, IReadOnlyList<WatchEvent>> scrape) : IScraper
    {
        public string Name => "fake";
        public TimeSpan Interval => TimeSpan.FromMinutes(5);
        public IReadOnlyList<WatchEvent> Scrape(ScrapeContext context) => scrape(context);
    }

    private async Task<List<WatchEvent>> RunOnce(string dbPath, IScraper scraper)
    {
        var emitted = new List<WatchEvent>();
        using var cts = new CancellationTokenSource();
        var host = new ScraperHost([scraper], dbPath, PauseFile, e => { lock (emitted) emitted.Add(e); });
        var run = host.RunAsync(cts.Token);
        // The first run happens straight away; wait for its health record, then stop.
        for (var i = 0; i < 100; i++)
        {
            using (var db = Database.Open(dbPath))
                if (db.GetMeta("scraper:fake") is not null)
                    break;
            await Task.Delay(20);
        }
        cts.Cancel();
        try { await run; } catch (OperationCanceledException) { }
        return emitted;
    }

    [Fact]
    public async Task Scraped_events_inside_a_pause_are_dropped_and_health_is_kept()
    {
        var dbPath = Path.Combine(_dir, "watchme.db");
        using (var db = Database.Open(dbPath))
        {
            db.Insert([new WatchEvent(1000 * Min, EventSources.Session, "pause") { Data = $$"""{"until":{{1030 * Min}}}""" }]);
        }

        var emitted = await RunOnce(dbPath, new FakeScraper(ctx =>
        {
            ctx.Warn("only a test");
            return
            [
                new WatchEvent(999 * Min, EventSources.Edge, "visit") { ExtId = "before" },
                new WatchEvent(1015 * Min, EventSources.Edge, "visit") { ExtId = "during" },
                new WatchEvent(1030 * Min, EventSources.Edge, "visit") { ExtId = "after" },
            ];
        }));

        Assert.Equal(["before", "after"], emitted.Select(e => e.ExtId));
        using var check = Database.Open(dbPath);
        var health = Assert.Single(ScraperHealth.LoadAll(check));
        Assert.Equal(("fake", 2, "only a test"), (health.Name, health.Events, health.Warning));
        Assert.Null(health.Error);
        Assert.NotNull(health.LastOk);
    }

    [Fact]
    public async Task A_failing_scraper_is_recorded_as_failing()
    {
        var dbPath = Path.Combine(_dir, "watchme.db");
        await RunOnce(dbPath, new FakeScraper(_ => throw new IOException("disk on fire")));

        using var db = Database.Open(dbPath);
        var health = Assert.Single(ScraperHealth.LoadAll(db));
        Assert.Equal("IOException: disk on fire", health.Error);
        Assert.Null(health.LastOk);
        var (level, message) = AgentHealth.Judge(health, running: true, health.LastRun);
        Assert.Equal(AgentHealth.Level.Failing, level);
        Assert.Contains("disk on fire", message);
    }

    [Fact]
    public void Health_levels()
    {
        var now = 10_000 * Min;
        var ok = ScraperHealth.After(null, "edge", TimeSpan.FromMinutes(1), now, 3, null, null);
        Assert.Equal(AgentHealth.Level.Ok, AgentHealth.Judge(ok, true, now).Item1);
        Assert.Equal(AgentHealth.Level.Warning, AgentHealth.Judge(ok with { Warning = "x" }, true, now).Item1);
        Assert.Equal(AgentHealth.Level.Stalled, AgentHealth.Judge(ok, true, now + 10 * Min).Item1);
        // Not running: an old run is expected, not stalled.
        Assert.Equal(AgentHealth.Level.Ok, AgentHealth.Judge(ok, false, now + 10 * Min).Item1);

        var failed = ScraperHealth.After(ok, "edge", TimeSpan.FromMinutes(1), now + Min, 0, null, new InvalidOperationException("boom"));
        var stillFailing = ScraperHealth.After(failed, "edge", TimeSpan.FromMinutes(1), now + 2 * Min, 0, null, new InvalidOperationException("boom"));
        Assert.Equal(now + Min, stillFailing.FailingSince);
        Assert.Equal(now, stillFailing.LastOk);
        Assert.Equal(now, stillFailing.LastEvents);
        var recovered = ScraperHealth.After(stillFailing, "edge", TimeSpan.FromMinutes(1), now + 3 * Min, 0, null, null);
        Assert.Null(recovered.FailingSince);
        Assert.Null(recovered.Error);
    }

    [Fact]
    public void Status_lists_focus_and_every_source_with_its_problem()
    {
        using var db = Database.Open(":memory:");
        var now = Clock.NowMs();
        db.Insert([
            new WatchEvent(now - Min, EventSources.Focus, "foreground") { App = "x.exe" },
            new WatchEvent(now - 2 * Min, EventSources.Edge, "visit") { ExtId = "1" },
            new WatchEvent(now - 10 * 86_400_000L, EventSources.Git, "commit") { ExtId = "2" },
        ]);
        db.SetMeta("scraper:totalcmd", ScraperHealth.After(null, "totalcmd", TimeSpan.FromSeconds(30), now, 0, "log is off", null).ToJson());

        var report = AgentHealth.Build(db, running: true, now, PauseFile);

        Assert.Equal(["focus", "edge", "git", "totalcmd"], report.Sources.Select(s => s.Source));
        Assert.Equal(0, report.Sources.Single(s => s.Source == "git").Recent);
        var tc = Assert.Single(report.Problems);
        Assert.Equal(("Total Commander", "log is off"), (tc.Name, tc.Message));
    }

    [Fact]
    public void The_daily_report_shows_pauses()
    {
        using var db = Database.Open(":memory:");
        var day = new DateOnly(2026, 11, 20);
        var at = (int h, int m) => Dashboard.DayStart(day) + (h * 60 + m) * Min;
        db.Insert([
            new WatchEvent(at(9, 0), EventSources.Agent, "start"),
            new WatchEvent(at(9, 0), EventSources.Focus, "foreground") { App = "Code.exe" },
            new WatchEvent(at(10, 0), EventSources.Session, "pause") { Data = $$"""{"until":{{at(10, 30)}}}""" },
            new WatchEvent(at(11, 0), EventSources.Agent, "stop"),
        ]);
        var w = new StringWriter();
        DailyReport.Print(db, day, w);
        Assert.Contains("Paused  10:00–10:30", w.ToString());
    }
}
