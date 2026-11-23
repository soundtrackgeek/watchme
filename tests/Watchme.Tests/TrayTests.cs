using System.Drawing;
using Microsoft.AspNetCore.Http;
using Watchme.Agent;
using Watchme.Cli;
using Watchme.Core;

namespace Watchme.Tests;

public sealed class TrayTests
{
    private const long Min = 60_000;

    private static AgentHealth.SourceRow Row(string name, AgentHealth.Level level, string? message = null) =>
        new(name.ToLowerInvariant(), name, null, null, 0, level, message);

    [Fact]
    public void Recording_with_no_problems_has_a_plain_icon()
    {
        var status = new TrayStatus(null, []);
        Assert.False(status.Paused);
        Assert.Equal(TrayStatus.Alert.None, status.Badge);
        Assert.Equal("Watchme: Recording", status.Tooltip(0));
    }

    [Fact]
    public void A_pause_shows_its_end_and_time_left()
    {
        var now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        var until = now + 12 * Min;
        var status = new TrayStatus(new PauseState.Pause(now - Min, until), []);
        Assert.True(status.Paused);
        Assert.Equal($"Paused until {AgentHealth.When(until, now)} (12 min left)", status.Headline(now));
    }

    [Fact]
    public void A_failing_source_turns_the_badge_red_and_a_warning_amber()
    {
        var warning = new TrayStatus(null, [Row("Edge", AgentHealth.Level.Warning, "no history file")]);
        Assert.Equal(TrayStatus.Alert.Warning, warning.Badge);
        Assert.Equal("Watchme: Recording\n1 source needs a look", warning.Tooltip(0));

        var failing = new TrayStatus(null, [Row("Edge", AgentHealth.Level.Stalled), Row("Git", AgentHealth.Level.Failing)]);
        Assert.Equal(TrayStatus.Alert.Failing, failing.Badge);
        Assert.EndsWith("2 sources need a look", failing.Tooltip(0));
    }

    [Fact]
    public void Tooltips_and_menu_lines_fit()
    {
        var status = new TrayStatus(new PauseState.Pause(0, 23 * 60 * Min), [Row(new string('x', 200), AgentHealth.Level.Failing)]);
        Assert.True(status.Tooltip(0).Length <= TrayStatus.MaxTooltip);

        var line = TrayStatus.MenuLine(Row("Git", AgentHealth.Level.Failing, "failing since 14:02: IOException:\r\n" + new string('y', 200)));
        Assert.Equal(80, line.Length);
        Assert.StartsWith("Git: failing since 14:02: IOException: y", line);
        Assert.EndsWith("…", line);
    }

    [Fact]
    public void The_status_is_built_from_the_health_report()
    {
        var pause = new PauseState.Pause(0, 10 * Min);
        var report = new AgentHealth.Report(true, null, pause,
            [Row("Focus", AgentHealth.Level.Ok), Row("Edge", AgentHealth.Level.Warning, "off")], 7);
        var status = TrayStatus.From(report);
        Assert.Equal(pause, status.Pause);
        Assert.Equal("Edge", Assert.Single(status.Problems).Name);
        Assert.Null(status.WithPause(null).Pause);
    }

    [Theory]
    [InlineData(16, false, 0)]
    [InlineData(24, true, 1)]
    [InlineData(32, false, 2)]
    public void Icons_are_drawn_at_the_size_asked_for(int size, bool paused, int alert)
    {
        using var icon = TrayIcon.DrawIcon(size, paused, (TrayStatus.Alert)alert);
        Assert.Equal(new Size(size, size), icon.Size);
        using var bmp = icon.ToBitmap();
        // Transparent corner, solid middle.
        Assert.Equal(0, bmp.GetPixel(0, 0).A);
        Assert.Equal(255, bmp.GetPixel(size / 2, size / 2).A);
    }

    [Theory]
    [InlineData("1", null, true)]
    [InlineData("1", "http://127.0.0.1:7317", true)]
    [InlineData("1", "http://localhost:7317", true)]
    [InlineData(null, null, false)]                      // a plain form post from anywhere
    [InlineData("1", "https://evil.example", false)]
    [InlineData("1", "http://127.0.0.1:9999", false)]    // another local server
    public void Pause_requests_must_come_from_the_dashboard_page(string? header, string? origin, bool allowed)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Host = new HostString("127.0.0.1", 7317);
        if (header is not null)
            ctx.Request.Headers["X-Watchme"] = header;
        if (origin is not null)
            ctx.Request.Headers.Origin = origin;
        Assert.Equal(allowed, DashboardServer.FromThisPage(ctx));
    }
}
