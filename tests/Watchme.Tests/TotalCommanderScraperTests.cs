using System.Text;
using Watchme.Agent.Scrapers;
using Watchme.Core;

namespace Watchme.Tests;

public sealed class TotalCommanderScraperTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "watchme-tc-" + Guid.NewGuid().ToString("N"));
    private readonly Database _watchme = Database.Open(":memory:");
    private string LogPath => Path.Combine(_dir, "totalcmd.log");

    public TotalCommanderScraperTests() => Directory.CreateDirectory(_dir);

    private static readonly DateTime Base = new(2026, 11, 22, 14, 2, 0, DateTimeKind.Local);

    /// <summary>A log line in TC's format, <paramref name="second"/> seconds after 14:02:00.</summary>
    private static string Line(int second, string text) =>
        $"{Base.AddSeconds(second):dd/MM/yyyy HH:mm:ss}: {text}";

    private void WriteLog(params string[] lines) =>
        File.WriteAllText(LogPath, string.Join("\r\n", lines) + "\r\n", new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

    private static long Ms(int second) => new DateTimeOffset(Base.AddSeconds(second)).ToUnixTimeMilliseconds();

    private IReadOnlyList<WatchEvent> Scrape(int nowSecond)
    {
        var scraper = new TotalCommanderScraper(() => LogPath, backfillDays: 7);
        var events = scraper.Scrape(new ScrapeContext(_watchme, Ms(nowSecond)));
        _watchme.Insert(events.ToList());
        return events;
    }

    // The exact sample TC wrote on this machine.
    private static readonly string[] Sample =
    [
        "22/11/2026 14:02:22: NewFolder: c:\\_a\\new folder",
        "22/11/2026 14:02:27: Copy: c:\\_code\\watchme\\CHANGELOG.md -> c:\\_a\\new folder\\CHANGELOG.md",
        "22/11/2026 14:02:37: Move: c:\\_a\\new folder\\CHANGELOG.md -> c:\\_a\\new folder\\CHANGELOG_2.md",
        "22/11/2026 14:02:55: Move: c:\\_a\\new folder\\CHANGELOG_2.md -> c:\\_blitzed\\CHANGELOG_2.md",
        "22/11/2026 14:02:57: DeleteFolder: c:\\_a\\new folder",
    ];

    [Fact]
    public void Real_sample_becomes_five_readable_actions()
    {
        WriteLog(Sample);

        var titles = Scrape(nowSecond: 120).Select(e => e.Title).ToList();

        Assert.Equal(
        [
            @"Created folder c:\_a\new folder",
            @"Copied c:\_code\watchme\CHANGELOG.md → c:\_a\new folder\CHANGELOG.md",
            @"Renamed c:\_a\new folder\CHANGELOG.md → CHANGELOG_2.md",
            @"Moved c:\_a\new folder\CHANGELOG_2.md → c:\_blitzed\CHANGELOG_2.md",
            @"Deleted folder c:\_a\new folder",
        ], titles);
    }

    [Fact]
    public void Folder_copy_is_grouped_and_only_new_lines_are_read_next_time()
    {
        WriteLog(
            Line(0, @"NewFolder: d:\backup\photos"),
            Line(0, @"Copy: c:\photos\a.jpg -> d:\backup\photos\a.jpg"),
            Line(1, @"NewFolder: d:\backup\photos\2026"),
            Line(1, @"Copy: c:\photos\2026\b.jpg -> d:\backup\photos\2026\b.jpg"),
            Line(2, @"Copy: c:\photos\2026\c.jpg -> d:\backup\photos\2026\c.jpg"));

        var e = Assert.Single(Scrape(nowSecond: 30));
        Assert.Equal(@"Copied 3 items from c:\photos to d:\backup\photos (created 2 folders)", e.Title);

        File.AppendAllText(LogPath, Line(40, @"Delete: c:\photos\a.jpg") + "\r\n");
        var next = Assert.Single(Scrape(nowSecond: 60));
        Assert.Equal(@"Deleted c:\photos\a.jpg", next.Title);
        Assert.Equal(2L, _watchme.Scalar("SELECT COUNT(*) FROM events;"));
    }

    [Fact]
    public void A_copy_still_in_progress_waits()
    {
        WriteLog(Line(0, @"Copy: c:\a\1.bin -> d:\b\1.bin"));

        Assert.Empty(Scrape(nowSecond: 5));
        Assert.Single(Scrape(nowSecond: 15));
    }

    [Fact]
    public void Errors_are_called_out()
    {
        WriteLog(
            Line(0, @"Copy (Ok): c:\a\1.txt -> d:\b\1.txt"),
            Line(0, @"Copy (Error): c:\a\2.txt -> d:\b\2.txt"));

        var e = Assert.Single(Scrape(nowSecond: 30));
        Assert.Equal(@"Copied 2 items from c:\a to d:\b — 1 error", e.Title);
    }

    [Fact]
    public void Rotated_logs_are_read_too()
    {
        File.WriteAllText(Path.Combine(_dir, "totalcmd_old.log"), Line(-3600, @"Delete: c:\old.txt") + "\r\n");
        File.SetLastWriteTime(Path.Combine(_dir, "totalcmd_old.log"), Base.AddHours(-1));
        WriteLog(Line(0, @"Delete: c:\new.txt"));

        Assert.Equal(2, Scrape(nowSecond: 30).Count);
    }

    [Fact]
    public void Log_path_is_read_from_wincmd_ini()
    {
        var ini = Path.Combine(_dir, "wincmd.ini");
        File.WriteAllText(ini, "[Configuration]\r\nLogFile=%TEMP%\\totalcmd.log\r\nLogOptions=7199\r\n");

        Assert.Equal(Path.Combine(Environment.GetEnvironmentVariable("TEMP")!, "totalcmd.log"),
            TotalCommanderScraper.LogPathFromIni(ini));
    }

    public void Dispose()
    {
        _watchme.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }
}
