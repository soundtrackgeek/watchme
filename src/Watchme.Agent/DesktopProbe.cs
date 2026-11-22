using System.Collections.Concurrent;

namespace Watchme.Agent;

/// <summary>What is in front of the user right now.</summary>
/// <param name="App">Exe name, or for a web app window (an Edge PWA) the app's name, e.g. "Google Chat".</param>
/// <param name="AppId">For a web app window, its AppUserModelID.</param>
internal sealed record WindowInfo(IntPtr Hwnd, int Pid, string App, string? Exe, string Title, string? AppId = null);

/// <summary>Reads desktop state. An interface so the focus logic can be tested without a real desktop.</summary>
internal interface IDesktopProbe
{
    WindowInfo? GetForegroundWindow();
    uint GetIdleMilliseconds();
}

internal sealed class Win32DesktopProbe : IDesktopProbe
{
    /// <summary>App ids never change their names, so each is looked up once.</summary>
    private readonly ConcurrentDictionary<string, string> _appNames = new();

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

        // Edge runs every installed web app in its own browser process; only the window's
        // app id tells Google Chat, X or Reddit apart from ordinary browsing.
        if (WebApps.IsBrowser(app) && Native.GetWindowAppId(hwnd) is { } appId && WebApps.IsWebApp(appId))
        {
            var name = _appNames.GetOrAdd(appId, id => string.Join(' ',
                (Native.GetAppDisplayName(id) ?? WebApps.FallbackName(id)).Split(default(char[]), StringSplitOptions.RemoveEmptyEntries)));
            return new WindowInfo(hwnd, (int)pid, name, exe, Native.GetWindowTitle(hwnd), appId);
        }

        return new WindowInfo(hwnd, (int)pid, app, exe, Native.GetWindowTitle(hwnd));
    }

    private static (string App, string? Exe) Resolve(uint pid)
    {
        var (path, _) = Native.GetProcessDetails(pid);
        return (path is null ? $"pid:{pid}" : Path.GetFileName(path), path);
    }
}

/// <summary>Telling browser windows and web app windows apart, and tidying browser titles.</summary>
internal static partial class WebApps
{
    public static bool IsBrowser(string app) =>
        app.Equals("msedge.exe", StringComparison.OrdinalIgnoreCase) || app.Equals("chrome.exe", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Browser windows use "MSEdge" (or "MSEdge.UserData.…" per profile, "Chrome" for Chrome);
    /// web apps use their own: "chat.google.com-6003259E_fctm2g5w6wnb6!App" when installed as a
    /// Windows app, "MSEdge._crx_…" or "Chrome._crx_…" otherwise.
    /// </summary>
    public static bool IsWebApp(string appId) =>
        appId.Contains("_crx_", StringComparison.OrdinalIgnoreCase)
        || !(appId.StartsWith("MSEdge", StringComparison.OrdinalIgnoreCase) || appId.StartsWith("Chrome", StringComparison.OrdinalIgnoreCase));

    /// <summary>"chat.google.com" from "chat.google.com-6003259E_…!App" when Windows has no name for it.</summary>
    public static string FallbackName(string appId)
    {
        var dash = appId.IndexOf('-');
        return dash > 0 ? appId[..dash] : appId;
    }

    [System.Text.RegularExpressions.GeneratedRegex(@" - Microsoft​? Edge$")]
    private static partial System.Text.RegularExpressions.Regex EdgeSuffixRegex();

    [System.Text.RegularExpressions.GeneratedRegex(@" and \d+ more pages?$")]
    private static partial System.Text.RegularExpressions.Regex MorePagesRegex();

    /// <summary>
    /// "Tiësto Albums - Rate Your Music and 10 more pages - Personal - Microsoft​ Edge" →
    /// "Tiësto Albums - Rate Your Music". The profile part is only removed for names Edge uses
    /// for its profiles, so a page's own " - Yahoo Mail" stays. Other titles are unchanged.
    /// </summary>
    public static string CleanBrowserTitle(string title, IEnumerable<string> profileNames)
    {
        var suffix = EdgeSuffixRegex().Match(title);
        if (!suffix.Success)
            return title;
        var rest = title[..suffix.Index];
        foreach (var profile in profileNames)
        {
            if (profile.Length > 0 && rest.EndsWith(" - " + profile, StringComparison.Ordinal))
            {
                rest = rest[..^(profile.Length + 3)];
                break;
            }
        }
        rest = MorePagesRegex().Replace(rest, "");
        return rest.Length > 0 ? rest : title;
    }

    private static string[] _profiles = [];
    private static long _profilesReadAt = long.MinValue;

    /// <summary>The names Edge shows for its profiles in window titles, from Local State; re-read hourly.</summary>
    public static string[] EdgeProfileNames(long now)
    {
        if (now - _profilesReadAt < 60 * 60 * 1000)
            return _profiles;
        _profilesReadAt = now;
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft", "Edge", "User Data", "Local State");
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.TryGetProperty("profile", out var profile)
                && profile.TryGetProperty("info_cache", out var cache))
            {
                _profiles = cache.EnumerateObject()
                    .SelectMany(p => new[] { "shortcut_name", "name" }
                        .Select(k => p.Value.TryGetProperty(k, out var v) ? v.GetString() : null))
                    .OfType<string>().Where(n => n.Length > 0)
                    .Distinct().OrderByDescending(n => n.Length).ToArray();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
        }
        return _profiles;
    }
}