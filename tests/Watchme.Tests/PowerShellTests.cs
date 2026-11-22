using System.Text;
using Watchme.Agent.Scrapers;
using Watchme.Core;

namespace Watchme.Tests;

public sealed class PowerShellTests : IDisposable
{
    private const long Min = 60_000;
    private static readonly long Now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private readonly string _inbox = Path.Combine(Path.GetTempPath(), "watchme-inbox-" + Guid.NewGuid().ToString("N"));
    private readonly Database _watchme = Database.Open(":memory:");

    public PowerShellTests() => Directory.CreateDirectory(_inbox);

    public void Dispose()
    {
        _watchme.Dispose();
        Directory.Delete(_inbox, recursive: true);
    }

    private static string Line(int id, long start, string cmd, long durationMs = 2000, bool ok = true, int? exit = null,
        string cwd = @"C:\_code\watchme", long session = 1_700_000_000_000, int pid = 4242, string status = "Completed") =>
        $$"""{"v":1,"session":{{session}},"pid":{{pid}},"id":{{id}},"shell":"pwsh 7.6.10","start":{{start}},"end":{{start + durationMs}},"cmd":{{System.Text.Json.JsonSerializer.Serialize(cmd)}},"cwd":{{System.Text.Json.JsonSerializer.Serialize(cwd)}},"cwd_after":{{System.Text.Json.JsonSerializer.Serialize(cwd)}},"ok":{{(ok ? "true" : "false")}},"status":"{{status}}","exit_code":{{(exit is null ? "null" : exit.ToString())}}}""";

    private List<WatchEvent> Scrape(string content, string file = "powershell-2026-11-22.jsonl")
    {
        File.AppendAllText(Path.Combine(_inbox, file), content, new UTF8Encoding(false));
        var events = new PowerShellScraper(_inbox, backfillDays: 7).Scrape(new ScrapeContext(_watchme, Now)).ToList();
        _watchme.Insert(events);
        return events;
    }

    [Fact]
    public void Commands_are_read_and_a_line_being_written_waits()
    {
        var events = Scrape(
            Line(1, Now - 10 * Min, "git status") + "\n" +
            "not json\n" +
            Line(2, Now - 9 * Min, "dotnet test\n  --no-build", ok: false, exit: 1) + "\n" +
            Line(3, Now - 8 * Min, "git push")[..30]);

        Assert.Equal(["git status", "dotnet test …"], events.Select(e => e.Title));
        Assert.Equal("cmd:1700000000000:4242:1", events[0].ExtId);
        Assert.Equal(Now - 10 * Min, events[0].Ts);
        Assert.Contains("\"ok\":false", events[1].Data);
        Assert.Contains("\"exit_code\":1", events[1].Data);

        // The rest of line 3 arrives; earlier lines are read again but are not new.
        var again = Scrape(Line(3, Now - 8 * Min, "git push")[30..] + "\n");
        Assert.Contains(again, e => e.Title == "git push");
        Assert.Equal(3, Convert.ToInt32(_watchme.Scalar("SELECT COUNT(*) FROM events WHERE source = 'powershell'")));
    }

    [Fact]
    public void Report_shows_one_line_per_tab_session()
    {
        Scrape(string.Join("\n",
            Line(1, Now - 60 * Min, "git status"),
            Line(2, Now - 59 * Min, "dotnet build"),
            Line(3, Now - 58 * Min, "dotnet test", ok: false, exit: 1),
            Line(4, Now - 57 * Min, @"& 'C:\Program Files\dotnet\dotnet.exe' test"),
            Line(5, Now - 56 * Min, "cd ..", cwd: @"C:\_code\watchme\src"),
            // Another tab, one command.
            Line(1, Now - 50 * Min, "npm run build", session: 1_700_000_000_500, pid: 99, cwd: @"C:\_code\site", ok: false, exit: 2),
            // The first tab again, after a long break.
            Line(6, Now - 5 * Min, "[hidden]"),
            "") + "\n");

        var lines = DailyReport.LoadActivity(_watchme, Now - 120 * Min, Now + Min);

        Assert.Equal(3, lines.Count);
        Assert.EndsWith(@"PowerShell      in C:\_code\watchme (+1 more folder): 5 commands, 1 failed: dotnet ×3, cd, git", lines[0]);
        Assert.EndsWith(@"PowerShell      C:\_code\site> npm run build (failed, exit 2)", lines[1]);
        Assert.EndsWith(@"PowerShell      C:\_code\watchme> [hidden]", lines[2]);
    }

    [Theory]
    [InlineData("git push origin master", "git")]
    [InlineData(@"& 'C:\Program Files\dotnet\dotnet.exe' test", "dotnet")]
    [InlineData(@".\build.ps1 -Release", "build.ps1")]
    [InlineData(". $PROFILE", "$PROFILE")]
    [InlineData("Get-ChildItem | Select -First 3", "Get-ChildItem")]
    [InlineData("[hidden]", null)]
    public void Tool_names(string cmd, string? tool) => Assert.Equal(tool, DailyReport.ToolName(cmd));

    [Fact]
    public void Profile_block_is_added_once_and_removed_cleanly()
    {
        const string profile = "fnm env --use-on-cd --shell powershell | Out-String | Invoke-Expression";
        var added = PowerShellHook.WithBlock(profile, @"C:\Users\me\AppData\Local\Watchme\hooks\powershell.ps1");

        Assert.StartsWith(profile + "\r\n" + PowerShellHook.BeginMarker + "\r\n", added);
        Assert.EndsWith(PowerShellHook.EndMarker + "\r\n", added);
        Assert.Equal(added, PowerShellHook.WithBlock(added, @"C:\Users\me\AppData\Local\Watchme\hooks\powershell.ps1"));
        Assert.Equal(profile + "\r\n", PowerShellHook.WithoutBlock(added));
        Assert.Equal(profile, PowerShellHook.WithoutBlock(profile));
    }

    [Fact]
    public void Profile_bytes_outside_the_block_survive()
    {
        var path = Path.Combine(_inbox, "profile.ps1");
        // A UTF-8 BOM, a Norwegian letter and an ANSI byte that is not valid UTF-8.
        byte[] original = [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("# Jørn\n"), 0xE6, (byte)'\n'];
        File.WriteAllBytes(path, original);

        var (text, encoding) = PowerShellHook.ReadProfile(path);
        PowerShellHook.WriteProfile(path, PowerShellHook.WithBlock(text, @"C:\hook.ps1"), encoding);
        Assert.Contains(PowerShellHook.BeginMarker, File.ReadAllText(path));

        (text, encoding) = PowerShellHook.ReadProfile(path);
        PowerShellHook.WriteProfile(path, PowerShellHook.WithoutBlock(text), encoding);
        Assert.Equal(original, File.ReadAllBytes(path));
    }

    [Fact]
    public void Hook_script_is_ascii_and_quotes_the_inbox()
    {
        var script = PowerShellHook.Script(@"C:\Users\o'neil\Watchme\inbox");
        Assert.Contains(@"Inbox   = 'C:\Users\o''neil\Watchme\inbox'", script);
        Assert.All(script, c => Assert.True(c < 128));
    }
}
