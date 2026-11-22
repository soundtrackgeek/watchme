namespace Watchme.Agent;

/// <summary>What is in front of the user right now.</summary>
internal sealed record WindowInfo(IntPtr Hwnd, int Pid, string App, string? Exe, string Title);

/// <summary>Reads desktop state. An interface so the focus logic can be tested without a real desktop.</summary>
internal interface IDesktopProbe
{
    WindowInfo? GetForegroundWindow();
    uint GetIdleMilliseconds();
}

internal sealed class Win32DesktopProbe : IDesktopProbe
{
    public uint GetIdleMilliseconds() => Native.GetIdleMilliseconds();

    public WindowInfo? GetForegroundWindow()
    {
        var hwnd = Native.GetForegroundWindow();
        if (hwnd == IntPtr.Zero)
            return null; // Happens briefly while focus moves between windows.

        var pid = Native.GetWindowProcessId(hwnd);
        if (pid == 0)
            return null;

        // Resolved every tick (cheap: one OpenProcess call). Caching by pid would go
        // wrong over days of uptime because Windows reuses process ids.
        var (app, exe) = Resolve(pid);
        if (app.Equals("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase)
            && Native.FindHostedAppProcessId(hwnd, pid) is { } hostedPid)
        {
            pid = hostedPid;
            (app, exe) = Resolve(pid);
        }

        return new WindowInfo(hwnd, (int)pid, app, exe, Native.GetWindowTitle(hwnd));
    }

    private static (string App, string? Exe) Resolve(uint pid)
    {
        var (path, _) = Native.GetProcessDetails(pid);
        return (path is null ? $"pid:{pid}" : Path.GetFileName(path), path);
    }
}
