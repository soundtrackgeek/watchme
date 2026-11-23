using Watchme.Core;

namespace Watchme.Tests;

public class DashboardTests
{
    private const long Min = 60_000;

    private static readonly DateOnly Day = new(2026, 11, 20);

    /// <summary>Local time on <see cref="Day"/> (or a day later), as unix ms.</summary>
    private static long At(int hour, int minute = 0, int dayOffset = 0) =>
        Dashboard.DayStart(Day.AddDays(dayOffset)) + (hour * 60 + minute) * Min;

    private static WatchEvent Focus(long ts, string app, string title = "t") =>
        new(ts, EventSources.Focus, "foreground") { App = app, Title = title };

    [Fact]
    public void Focus_time_is_split_per_day_and_per_hour()
    {
        using var db = Database.Open(":memory:");
        db.Insert([
            new WatchEvent(At(23, 30), EventSources.Agent, "start"),
            Focus(At(23, 30), "Code.exe"),
            Focus(At(0, 20, dayOffset: 1), "TOTALCMD64.EXE"),
            new WatchEvent(At(0, 50, dayOffset: 1), EventSources.Agent, "stop"),
        ]);

        var data = Dashboard.Build(db, Day, Day.AddDays(1));

        Assert.Equal(["2026-11-20", "2026-11-21"], data.Days.Select(d => d.Date));
        Assert.Equal(30 * Min, data.Days[0].ActiveMs);
        Assert.Equal(50 * Min, data.Days[1].ActiveMs);
        Assert.Equal(20 * Min, data.Days[1].Apps["Code.exe"]);
        Assert.Equal(30.0, data.FocusByHour[0][23], 3);
        Assert.Equal(50.0, data.FocusByHour[1][0], 3);
        Assert.Equal(80 * Min, data.Totals.ActiveMs);
        Assert.Equal("Code.exe", data.Apps[0].App);
    }

    [Fact]
    public void Apps_beyond_the_top_are_folded_into_other_per_day()
    {
        using var db = Database.Open(":memory:");
        var events = new List<WatchEvent> { new(At(8), EventSources.Agent, "start") };
        // App i gets (10 - i) minutes, so the last two fall outside the top 7.
        var t = At(8);
        for (var i = 0; i < Dashboard.TopApps + 2; i++)
        {
            events.Add(Focus(t, $"app{i}.exe"));
            t += (10 - i) * Min;
        }
        events.Add(new WatchEvent(t, EventSources.Agent, "stop"));
        db.Insert(events);

        var day = Assert.Single(Dashboard.Build(db, Day, Day).Days);

        Assert.Equal(Dashboard.TopApps + 1, day.Apps.Count);
        Assert.Equal((3 + 2) * Min, day.Apps["Other"]);
    }

    [Fact]
    public void App_summaries_come_from_scraped_events()
    {
        using var db = Database.Open(":memory:");
        db.Insert([
            new(At(10), EventSources.AuroraNg, "listen")
                { ExtId = "1", Data = """{"artist":"Tiësto","album":"Just Be","genre":"Trance","listened_s":200,"play":true,"outcome":"finished"}""" },
            new(At(10, 5), EventSources.AuroraNg, "listen")
                { ExtId = "2", Data = """{"artist":"Tiësto","album":"Just Be","genre":"Trance","listened_s":20,"play":false,"outcome":"skipped"}""" },
            new(At(10, 6), EventSources.AuroraNg, "rating") { ExtId = "3", Data = """{"rating_after":80}""" },
            new(At(11), EventSources.Edge, "visit")
                { ExtId = "4", Title = "Tiësto - Rate Your Music", Data = """{"host":"rateyourmusic.com","search":"tiesto"}""" },
            new(At(12), EventSources.Git, "commit") { ExtId = "5", Data = """{"repo":"watchme"}""" },
            new(At(12), EventSources.T3, "run") { ExtId = "6", Data = """{"project":"watchme","model_label":"Opus 5.5","duration_s":90,"subagent":false}""" },
            new(At(12), EventSources.T3, "run") { ExtId = "7", Data = """{"project":"watchme","model_label":"Opus 5.5","duration_s":30,"subagent":true}""" },
            new(At(13), EventSources.PowerShell, "command") { ExtId = "8", Data = """{"cmd":"git status","cwd":"C:\\code","ok":false}""" },
            new(At(13), EventSources.TotalCommander, "file_op") { ExtId = "9", Data = """{"ops":{"Delete":3,"DeleteFolder":1,"NewFolder":2}}""" },
        ]);

        var data = Dashboard.Build(db, Day, Day);

        Assert.Equal((2, 1, 1, 220.0), (data.Music.Listens, data.Music.Plays, data.Music.Skips, data.Music.ListenedS));
        Assert.Equal("Tiësto", Assert.Single(data.Music.Artists).Name);
        Assert.Equal(1, data.Music.Stars[7]); // 80 → 4★
        Assert.Equal("tiesto", Assert.Single(data.Web.Searches).Query);
        Assert.Equal(new Dashboard.Count("watchme", 1), Assert.Single(data.Code.Repos));
        Assert.Equal(1, data.Totals.Prompts); // the subagent run overlaps its parent
        Assert.Equal(90, data.Totals.AgentS);
        Assert.Equal((1, 1), (data.Shell.Commands, data.Shell.Failed));
        Assert.Equal("git", Assert.Single(data.Shell.Programs).Name);
        Assert.Equal([("Deleted", 4), ("Created folders", 2)], data.FileOps.Select(c => (c.Name, c.N)));
        Assert.Equal("C:\\code", Assert.Single(data.Shell.Folders).Name);
        Assert.Equal(3, data.Days[0].Events[EventSources.AuroraNg]); // two listens and a rating
        Assert.Equal(3, data.EventsByHour[0][10]);
    }

    [Fact]
    public void Love_changes_are_counted_and_not_mistaken_for_ratings()
    {
        using var db = Database.Open(":memory:");
        db.Insert([
            new(At(10), EventSources.AuroraNg, "rating")
                { ExtId = "1", Data = """{"rating_before":100,"rating_after":100,"love_before":"neutral","love_after":"loved"}""" },
            new(At(10, 1), EventSources.AuroraNg, "rating")
                { ExtId = "2", Data = """{"rating_before":80,"rating_after":80,"love_before":"loved","love_after":"neutral"}""" },
            new(At(10, 2), EventSources.AuroraNg, "rating")
                { ExtId = "3", Data = """{"rating_before":60,"rating_after":80,"love_before":"neutral","love_after":"neutral"}""" },
        ]);

        var music = Dashboard.Build(db, Day, Day).Music;

        Assert.Equal((1, 1), (music.Loved, music.Unloved));
        Assert.Equal(1, music.Stars.Sum());
        Assert.Equal(1, music.Stars[7]);
    }

    [Fact]
    public void Event_log_leaves_out_processes_and_escapes_like_wildcards()
    {
        using var db = Database.Open(":memory:");
        db.Insert([
            new(At(9), EventSources.Process, "start") { App = "x.exe" },
            new(At(9), EventSources.Git, "commit") { Title = "100% done" },
            new(At(9), EventSources.Git, "commit") { Title = "1000 done" },
        ]);
        long from = At(0), to = At(0, dayOffset: 1);

        Assert.Equal(2, Dashboard.Events(db, from, to, "all", null, null, 50).Count);
        Assert.Single(Dashboard.Events(db, from, to, "process", null, null, 50));
        Assert.Equal("100% done", Assert.Single(Dashboard.Events(db, from, to, "all", "0%", null, 50)).Title);
    }

    [Theory]
    [InlineData("git push", "git")]
    [InlineData(@".\watchme.exe report", "watchme")]
    [InlineData(@"& ""C:\Program Files\dotnet\dotnet.exe"" build", "dotnet")]
    [InlineData(@"'C:\tools\my app.exe' --help", "my app")]
    [InlineData("[hidden]", "[hidden]")]
    [InlineData("   ", null)]
    public void Program_is_the_first_word_of_a_command(string command, string? expected) =>
        Assert.Equal(expected, Dashboard.Program(command));
}
