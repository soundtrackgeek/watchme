using Watchme.Agent;
using Watchme.Core;

namespace Watchme.Tests;

public class FocusTrackerTests
{
    private sealed class FakeProbe : IDesktopProbe
    {
        public WindowInfo? Window;
        public uint IdleMs;
        public WindowInfo? GetForegroundWindow() => Window;
        public uint GetIdleMilliseconds() => IdleMs;
    }

    private static readonly WindowInfo Code = new(1, 100, "Code.exe", @"C:\Code.exe", "a.cs - watchme");
    private static readonly WindowInfo Tc = new(2, 200, "TOTALCMD64.EXE", @"C:\TC.exe", @"Total Commander - C:\_code");

    private readonly FakeProbe _probe = new();
    private readonly List<WatchEvent> _events = [];
    private readonly FocusTracker _tracker;

    public FocusTrackerTests()
    {
        var config = new WatchmeConfig { IdleThresholdSeconds = 300, TitleStableSeconds = 2 };
        _tracker = new FocusTracker(_probe, config, _events.Add);
    }

    [Fact]
    public void Emits_foreground_on_window_switch_only()
    {
        _probe.Window = Code;
        _tracker.Tick(0);
        _tracker.Tick(1000);
        _probe.Window = Tc;
        _tracker.Tick(2000);

        Assert.Equal(["foreground", "foreground"], _events.Select(e => e.Kind));
        Assert.Equal("TOTALCMD64.EXE", _events[1].App);
    }

    [Fact]
    public void Title_change_is_recorded_once_stable_and_backdated()
    {
        _probe.Window = Code;
        _tracker.Tick(0);
        _probe.Window = Code with { Title = "Copying 10%" };
        _tracker.Tick(1000);
        _probe.Window = Code with { Title = "Copying 50%" };
        _tracker.Tick(2000);
        _probe.Window = Code with { Title = "b.cs - watchme" };
        _tracker.Tick(3000);
        _tracker.Tick(4000);
        _tracker.Tick(5000);

        var title = Assert.Single(_events, e => e.Kind == "title");
        Assert.Equal("b.cs - watchme", title.Title);
        Assert.Equal(3000, title.Ts);
    }

    [Fact]
    public void Idle_is_backdated_and_focus_resumes_after()
    {
        _probe.Window = Code;
        _tracker.Tick(0);
        _probe.IdleMs = 300_000;
        _tracker.Tick(400_000);
        _tracker.Tick(401_000);
        _probe.IdleMs = 500;
        _tracker.Tick(402_000);

        Assert.Equal(["foreground", "idle_start", "idle_end", "foreground"], _events.Select(e => e.Kind));
        Assert.Equal(100_000, _events[1].Ts);
        Assert.Equal(401_500, _events[2].Ts);
        Assert.Equal(_events[2].Ts, _events[3].Ts);
    }

    [Fact]
    public void Locked_emits_nothing_and_unlock_re_emits_current_window()
    {
        _probe.Window = Code;
        _tracker.Tick(0);
        _tracker.SetLocked(true);
        _tracker.Tick(1000);
        _tracker.SetLocked(false);
        _tracker.Tick(2000);

        Assert.Equal([0L, 2000L], _events.Select(e => e.Ts));
    }

    [Fact]
    public void Sensitive_titles_are_redacted()
    {
        _probe.Window = Code with { App = "KeePass.exe", Title = "bank login" };
        _tracker.Tick(0);

        Assert.Equal("[redacted]", Assert.Single(_events).Title);
    }
}
