using System.Runtime.InteropServices;
using System.Text;

namespace Watchme.Agent;

internal static partial class Native
{
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint TH32CS_SNAPPROCESS = 0x00000002;
    private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLengthW(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder text, int maxCount);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO info);

    [DllImport("kernel32.dll")]
    private static extern uint GetTickCount();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, StringBuilder name, ref uint size);

    [DllImport("kernel32.dll")]
    private static extern bool GetProcessTimes(IntPtr process, out long creation, out long exit, out long kernel, out long user);

    [DllImport("kernel32.dll")]
    private static extern bool ProcessIdToSessionId(uint processId, out uint sessionId);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32W
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Process32FirstW(IntPtr snapshot, ref PROCESSENTRY32W entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Process32NextW(IntPtr snapshot, ref PROCESSENTRY32W entry);

    // ---- Shell: app ids of windows ----

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey
    {
        public Guid FormatId;
        public uint PropertyId;
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public IntPtr Pointer;
    }

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        [PreserveSig] int BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int GetParent(out IShellItem parent);
        [PreserveSig] int GetDisplayName(uint sigdn, out IntPtr name);
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetPropertyStoreForWindow(IntPtr hwnd, ref Guid iid,
        [MarshalAs(UnmanagedType.Interface)] out IPropertyStore store);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr pbc, ref Guid iid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItem item);

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant value);

    private static readonly Guid PropertyStoreIid = new("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99");
    private static readonly Guid ShellItemIid = new("43826D1E-E718-42EE-BC55-A1E261C37BFE");
    private const ushort VT_LPWSTR = 31;
    private const uint SIGDN_NORMALDISPLAY = 0;

    /// <summary>
    /// The AppUserModelID Windows groups a window under on the taskbar, e.g. "MSEdge" for an Edge
    /// window and "chat.google.com-6003259E_fctm2g5w6wnb6!App" for the Google Chat web app.
    /// Null for windows that set none (most apps: theirs comes from the process).
    /// </summary>
    public static string? GetWindowAppId(IntPtr hwnd)
    {
        var iid = PropertyStoreIid;
        if (SHGetPropertyStoreForWindow(hwnd, ref iid, out var store) != 0 || store is null)
            return null;
        try
        {
            // PKEY_AppUserModel_ID
            var key = new PropertyKey { FormatId = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), PropertyId = 5 };
            if (store.GetValue(ref key, out var value) != 0)
                return null;
            try
            {
                return value.Type == VT_LPWSTR ? Marshal.PtrToStringUni(value.Pointer) : null;
            }
            finally
            {
                PropVariantClear(ref value);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(store);
        }
    }

    /// <summary>The name Start shows for an installed app ("Google Chat"), or null.</summary>
    public static string? GetAppDisplayName(string appId)
    {
        var iid = ShellItemIid;
        if (SHCreateItemFromParsingName(@"shell:AppsFolder\" + appId, IntPtr.Zero, ref iid, out var item) != 0 || item is null)
            return null;
        try
        {
            if (item.GetDisplayName(SIGDN_NORMALDISPLAY, out var name) != 0)
                return null;
            try
            {
                return Marshal.PtrToStringUni(name);
            }
            finally
            {
                Marshal.FreeCoTaskMem(name);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(item);
        }
    }

    // ---- Friendly wrappers ----

    public static string GetWindowTitle(IntPtr hwnd)
    {
        var length = GetWindowTextLengthW(hwnd);
        if (length <= 0)
            return "";
        var sb = new StringBuilder(length + 1);
        GetWindowTextW(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    public static uint GetWindowProcessId(IntPtr hwnd)
    {
        GetWindowThreadProcessId(hwnd, out var pid);
        return pid;
    }

    /// <summary>
    /// UWP apps (Settings, Calculator...) are hosted in ApplicationFrameHost.exe; the real
    /// app owns a child window. Returns that child's process id, or null if none differs.
    /// </summary>
    public static uint? FindHostedAppProcessId(IntPtr frameHwnd, uint framePid)
    {
        uint? found = null;
        EnumChildWindows(frameHwnd, (child, _) =>
        {
            var pid = GetWindowProcessId(child);
            if (pid != 0 && pid != framePid)
            {
                found = pid;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>Milliseconds since the last keyboard or mouse input in this session.</summary>
    public static uint GetIdleMilliseconds()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref info))
            return 0;
        // Both are 32-bit tick counts that wrap every ~49 days; unsigned subtraction handles the wrap.
        return unchecked(GetTickCount() - info.dwTime);
    }

    /// <summary>Full exe path and creation time (unix ms) of a process, where Windows lets us see them.</summary>
    public static (string? Path, long? CreatedMs) GetProcessDetails(uint pid)
    {
        var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle == IntPtr.Zero)
            return (null, null);
        try
        {
            string? path = null;
            var sb = new StringBuilder(1024);
            var size = (uint)sb.Capacity;
            if (QueryFullProcessImageNameW(handle, 0, sb, ref size))
                path = sb.ToString();

            long? created = null;
            if (GetProcessTimes(handle, out var creation, out _, out _, out _) && creation > 0)
                created = DateTimeOffset.FromFileTime(creation).ToUnixTimeMilliseconds();

            return (path, created);
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    public static uint? GetSessionId(uint pid) => ProcessIdToSessionId(pid, out var session) ? session : null;

    public readonly record struct ProcessEntry(uint Pid, uint ParentPid, string Name);

    /// <summary>A cheap snapshot of every process (pid, parent pid, exe name). No handles are opened.</summary>
    public static List<ProcessEntry> SnapshotProcesses()
    {
        var list = new List<ProcessEntry>(512);
        var snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snapshot == INVALID_HANDLE_VALUE)
            return list;
        try
        {
            var entry = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
            if (!Process32FirstW(snapshot, ref entry))
                return list;
            do
            {
                list.Add(new ProcessEntry(entry.th32ProcessID, entry.th32ParentProcessID, entry.szExeFile));
            } while (Process32NextW(snapshot, ref entry));
        }
        finally
        {
            CloseHandle(snapshot);
        }
        return list;
    }
}
