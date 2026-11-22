using Watchme.Core;

namespace Watchme.Agent;

/// <summary>
/// Polls the foreground window and input idle time once a second and emits:
/// <list type="bullet">
/// <item>focus/foreground — a different window (or process) came to the front</item>
/// <item>focus/title — the same window changed its title and kept it for TitleStableSeconds</item>
/// <item>idle/idle_start, idle/idle_end — no input for IdleThresholdSeconds, and back again</item>
/// </list>
/// While idle, locked or suspended nothing is emitted; afterwards the current window is
/// re-emitted so the next focus span starts at the right moment.
/// </summary>
internal sealed class FocusTracker(IDesktopProbe probe, WatchmeConfig config, Action<WatchEvent> emit)
{
    private WindowInfo? _current;
    private string? _pendingTitle;
    private long _pendingSince;
    private bool _idle;

    private volatile bool _locked;
    private volatile bool _suspended;
    private volatile bool _resetRequested;

    // Called from the SystemEvents thread. The reset makes the next tick re-emit the
    // current window, so a new focus span starts once we are no longer paused.
    public void SetLocked(bool locked)
    {
        _locked = locked;
        _resetRequested = true;
    }

    public void SetSuspended(bool suspended)
    {
        _suspended = suspended;
        _resetRequested = true;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            do
            {
                try
                {
                    Tick(Clock.NowMs());
                }
                catch (Exception ex)
                {
                    Log.Error("Focus tick failed", ex);
                }
            } while (await timer.WaitForNextTickAsync(ct));
        }
        catch (OperationCanceledException)
        {
        }
    }

    internal void Tick(long now)
    {
        if (_resetRequested)
        {
            _resetRequested = false;
            _current = null;
            _pendingTitle = null;
        }
        if (_locked || _suspended)
            return;

        var idleMs = probe.GetIdleMilliseconds();
        var threshold = config.IdleThresholdSeconds * 1000L;
        var foregroundTs = now;

        if (!_idle && idleMs >= threshold)
        {
            _idle = true;
            // Backdate to the last input: that is when you actually walked away.
            emit(new WatchEvent(now - idleMs, EventSources.Idle, "idle_start"));
            return;
        }
        if (_idle)
        {
            if (idleMs >= threshold)
                return;
            _idle = false;
            foregroundTs = now - idleMs;
            emit(new WatchEvent(foregroundTs, EventSources.Idle, "idle_end"));
            _current = null; // Start a fresh span for whatever window you came back to.
        }

        var window = probe.GetForegroundWindow();
        if (window is null)
            return;

        if (_current is null || window.Hwnd != _current.Hwnd || window.Pid != _current.Pid)
        {
            _current = window;
            _pendingTitle = null;
            emit(FocusEvent(foregroundTs, "foreground", window, window.Title));
            return;
        }

        if (window.Title == _current.Title)
        {
            _pendingTitle = null;
            return;
        }

        // Same window, new title: wait until it has been stable for a moment.
        if (_pendingTitle != window.Title)
        {
            _pendingTitle = window.Title;
            _pendingSince = now;
        }
        else if (now - _pendingSince >= config.TitleStableSeconds * 1000L)
        {
            _current = _current with { Title = window.Title };
            _pendingTitle = null;
            emit(FocusEvent(_pendingSince, "title", window, window.Title));
        }
    }

    /// <summary>Names Edge shows for its profiles in titles; replaceable for tests.</summary>
    internal Func<long, IEnumerable<string>> BrowserProfiles { get; init; } = WebApps.EdgeProfileNames;

    private WatchEvent FocusEvent(long ts, string kind, WindowInfo w, string title) =>
        new(ts, EventSources.Focus, kind)
        {
            App = w.App,
            Pid = w.Pid,
            Exe = w.Exe,
            // Redaction looks at the raw title: "InPrivate" is in the part the tidying removes.
            Title = config.ShouldRedactTitle(w.App, title) ? "[redacted]"
                : WebApps.IsBrowser(w.App) ? WebApps.CleanBrowserTitle(title, BrowserProfiles(ts))
                : title,
            Data = w.AppId is null ? null
                : System.Text.Json.JsonSerializer.Serialize(new { app_id = w.AppId, web_app = WebApps.FallbackName(w.AppId) }),
        };
}
