using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Watchme.Cli;

/// <summary>
/// Adds or removes a folder in the per-user PATH (HKCU\Environment), no admin needed.
/// Works on the raw registry value so entries like %USERPROFILE%\bin stay unexpanded.
/// </summary>
internal static class UserPath
{
    private const string EnvironmentKey = "Environment";

    /// <summary>Returns true if the folder was added, false if it was already there.</summary>
    public static bool Add(string folder)
    {
        var (entries, kind) = Read();
        if (entries.Any(e => Same(e, folder)))
            return false;
        entries.Add(folder);
        Write(entries, kind);
        return true;
    }

    /// <summary>Returns true if the folder was removed, false if it was not there.</summary>
    public static bool Remove(string folder)
    {
        var (entries, kind) = Read();
        var removed = entries.RemoveAll(e => Same(e, folder));
        if (removed == 0)
            return false;
        Write(entries, kind);
        return true;
    }

    /// <summary>Is the folder in the saved user PATH?</summary>
    public static bool Contains(string folder) => Read().Entries.Any(e => Same(e, folder));

    /// <summary>Is the folder on the PATH this process was started with?</summary>
    public static bool InCurrentProcess(string folder) =>
        (Environment.GetEnvironmentVariable("Path") ?? "")
            .Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Any(e => Same(e, folder));

    private static (List<string> Entries, RegistryValueKind Kind) Read()
    {
        using var key = Registry.CurrentUser.OpenSubKey(EnvironmentKey);
        if (key?.GetValue("Path", null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not string raw)
            return ([], RegistryValueKind.ExpandString);
        var kind = key.GetValueKind("Path");
        var entries = raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        return (entries, kind);
    }

    private static void Write(List<string> entries, RegistryValueKind kind)
    {
        using (var key = Registry.CurrentUser.CreateSubKey(EnvironmentKey))
        {
            if (kind is not (RegistryValueKind.String or RegistryValueKind.ExpandString))
                kind = RegistryValueKind.ExpandString;
            key.SetValue("Path", string.Join(';', entries), kind);
        }
        BroadcastEnvironmentChange();
    }

    private static bool Same(string entry, string folder) =>
        string.Equals(Normalize(entry), Normalize(folder), StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string path) =>
        Environment.ExpandEnvironmentVariables(path.Trim().Trim('"')).TrimEnd('\\');

    // Tell Explorer (and anything else listening) that environment variables changed, so
    // new terminals started from the Start menu or taskbar see the new PATH.
    private static void BroadcastEnvironmentChange()
    {
        const int HWND_BROADCAST = 0xffff;
        const uint WM_SETTINGCHANGE = 0x001A;
        const uint SMTO_ABORTIFHUNG = 0x0002;
        SendMessageTimeoutW(new IntPtr(HWND_BROADCAST), WM_SETTINGCHANGE, IntPtr.Zero, "Environment",
            SMTO_ABORTIFHUNG, 2000, out _);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeoutW(IntPtr hWnd, uint msg, IntPtr wParam, string lParam,
        uint flags, uint timeoutMs, out IntPtr result);
}
