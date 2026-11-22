using Microsoft.Win32;
using Watchme.Core;

namespace Watchme.Agent;

/// <summary>
/// Lock/unlock, sleep/resume and logoff/shutdown, via SystemEvents. SystemEvents runs its own
/// hidden window on a background thread, which is why the agent needs no message loop.
/// </summary>
internal sealed class SessionTracker : IDisposable
{
    private readonly FocusTracker _focus;
    private readonly Action<WatchEvent> _emit;
    private readonly Action<string> _onSessionEnding;

    public SessionTracker(FocusTracker focus, Action<WatchEvent> emit, Action<string> onSessionEnding)
    {
        _focus = focus;
        _emit = emit;
        _onSessionEnding = onSessionEnding;
        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.SessionEnding += OnSessionEnding;
    }

    private void Emit(string kind) => _emit(new WatchEvent(Clock.NowMs(), EventSources.Session, kind));

    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        switch (e.Reason)
        {
            case SessionSwitchReason.SessionLock:
                Emit("lock");
                _focus.SetLocked(true);
                break;
            case SessionSwitchReason.SessionUnlock:
                Emit("unlock");
                _focus.SetLocked(false);
                break;
            case SessionSwitchReason.RemoteConnect:
                Emit("remote_connect");
                break;
            case SessionSwitchReason.RemoteDisconnect:
                Emit("remote_disconnect");
                break;
        }
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        switch (e.Mode)
        {
            case PowerModes.Suspend:
                Emit("suspend");
                _focus.SetSuspended(true);
                break;
            case PowerModes.Resume:
                Emit("resume");
                _focus.SetSuspended(false);
                break;
        }
    }

    private void OnSessionEnding(object? sender, SessionEndingEventArgs e)
    {
        var reason = e.Reason == SessionEndReasons.Logoff ? "logoff" : "shutdown";
        Emit(reason);
        // Runs on the SystemEvents thread while Windows waits for our answer, so this is
        // the moment to flush the database before the process gets killed.
        _onSessionEnding(reason);
    }

    public void Dispose()
    {
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SystemEvents.SessionEnding -= OnSessionEnding;
    }
}
