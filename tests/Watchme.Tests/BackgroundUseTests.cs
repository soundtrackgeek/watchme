using Watchme.Core;

namespace Watchme.Tests;

public class BackgroundUseTests
{
    private const long Sec = 1000;
    private const long Min = 60 * Sec;

    private static readonly DateOnly Day = new(2026, 11, 20);

    private static long At(int hour, int minute = 0, int second = 0) =>
        Dashboard.DayStart(Day) + (hour * 60 + minute) * Min + second * Sec;

    private static WatchEvent Listen(long start, double listenedS, string outcome, long? ended = null) =>
        new(start, EventSources.AuroraNg, "listen")
        {
            App = "Aurora NG.exe",
            Title = "A – B",
            ExtId = $"listen:{start}",
            Data = $$"""{"listened_s":{{listenedS}},"outcome":"{{outcome}}","ended_ts":{{ended ?? start + (long)(listenedS * 1000)}}}""",
        };

    private static WatchEvent Rating(long ts) =>
        new(ts, EventSources.AuroraNg, "rating") { App = "Aurora NG.exe", Title = "Rated A – B 4★", ExtId = $"rating:{ts}" };

    [Fact]
    public void Actions_close_together_form_one_burst()
    {
        // 0s, 90s, 200s are within 2 minutes of each other; 600s starts a second burst.
        var (ms, bursts) = BackgroundUse.HandsOn([0, 90 * Sec, 200 * Sec, 600 * Sec]);

        Assert.Equal(2, bursts);
        Assert.Equal(200 * Sec + BackgroundUse.ActionMs + BackgroundUse.ActionMs, ms);
    }

    [Fact]
    public void No_actions_means_no_hands_on_time()
    {
        Assert.Equal((0L, 0), BackgroundUse.HandsOn([]));
    }

    [Fact]
    public void Skips_count_when_the_track_ended_and_ratings_when_made()
    {
        using var db = Database.Open(":memory:");
        db.Insert([
            Listen(At(9), 80, "skipped"),             // skip at 9:01:20
            Rating(At(9, 1, 50)),                      // 30 s later
            Listen(At(9, 1, 20), 300, "completed"),    // played through: no action, but playing time
            Listen(At(9, 6, 20), 60, "skipped"),       // skip at 9:07:20, 5.5 min after the rating
        ]);

        var use = Assert.Single(BackgroundUse.Load(db, At(0), At(23, 59)));

        Assert.Equal("Aurora NG.exe", use.App);
        Assert.Equal(2, use.Skips);
        Assert.Equal(1, use.Ratings);
        Assert.Equal(2, use.Bursts);
        Assert.Equal((80 + 300 + 60) * Sec, use.PlayingMs);
        Assert.Equal(30 * Sec + 2 * BackgroundUse.ActionMs, use.HandsOnMs);
        Assert.Equal("hands-on 0m 50s · 2 skips · 1 rating · 7m 20s playing", BackgroundUse.Describe(use));
    }

    [Fact]
    public void Range_report_lists_background_use_under_the_app_even_without_focus()
    {
        using var db = Database.Open(":memory:");
        db.Insert([
            new WatchEvent(At(9), EventSources.Agent, "start"),
            new WatchEvent(At(9), EventSources.Focus, "foreground") { App = "Code.exe", Title = "a.cs" },
            new WatchEvent(At(10), EventSources.Agent, "stop"),
            Listen(At(9, 10), 60, "skipped"),
            Rating(At(9, 11, 30)),
        ]);

        var w = new StringWriter();
        RangeReport.Print(db, Day, Day.AddDays(1), w);
        var text = w.ToString();

        Assert.Contains("Aurora NG.exe", text);
        Assert.Contains("└ hands-on 0m 40s · 1 skip · 1 rating · 1m 00s playing", text);
        // Background use is not focus time.
        Assert.Contains("Active  1h 00m", text);
    }

    [Fact]
    public void Dashboard_data_carries_background_use()
    {
        using var db = Database.Open(":memory:");
        db.Insert([Listen(At(9), 45, "skipped")]);

        var data = Dashboard.Build(db, Day, Day);

        var use = Assert.Single(data.Background);
        Assert.Equal(1, use.Skips);
        Assert.Equal(0, data.Totals.ActiveMs);
    }
}
