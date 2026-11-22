using System.Diagnostics;
using Microsoft.Win32;
using Watchme.Core;

namespace Watchme.Cli;

/// <summary>Starts, stops and registers the agent. It is expected next to watchme.exe.</summary>
internal static class AgentControl
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "Watchme";

    private static string InstallDir => AppContext.BaseDirectory.TrimEnd('\\');
    private static string AgentPath => Path.Combine(InstallDir, AgentIpc.ExeName);

    public static bool IsRunning()
    {
        if (!Mutex.TryOpenExisting(AgentIpc.MutexName, out var mutex))
            return false;
        mutex.Dispose();
        return true;
    }

    public static int Install()
    {
        EnsureAgentExists();
        if (AgentPath.Contains(@"\bin\Debug\", StringComparison.OrdinalIgnoreCase) ||
            AgentPath.Contains(@"\bin\Release\", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("Warning: installing from a build folder. Rebuilding will fail while the agent runs.");
            Console.WriteLine("         Consider publishing first (see README) and installing from there.");
        }

        using (var key = Registry.CurrentUser.CreateSubKey(RunKeyPath))
            key.SetValue(RunValueName, $"\"{AgentPath}\"");
        Console.WriteLine($"Registered to start at logon: {AgentPath}");

        if (UserPath.Add(InstallDir))
            Console.WriteLine($"Added to your PATH: {InstallDir}");
        if (!UserPath.InCurrentProcess(InstallDir))
            Console.WriteLine("Open a new terminal to use 'watchme' from anywhere.");

        return Start();
    }

    public static int Uninstall()
    {
        using (var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true))
            key?.DeleteValue(RunValueName, throwOnMissingValue: false);
        Console.WriteLine("Removed from logon startup.");
        if (UserPath.Remove(InstallDir))
            Console.WriteLine($"Removed from your PATH: {InstallDir}");
        Stop();
        Console.WriteLine($"Your data is kept in {Paths.DataDir}.");
        return 0;
    }

    public static int Start()
    {
        if (IsRunning())
        {
            Console.WriteLine("Agent is already running.");
            return 0;
        }
        EnsureAgentExists();
        Process.Start(new ProcessStartInfo(AgentPath) { UseShellExecute = false, CreateNoWindow = true });

        for (var i = 0; i < 50 && !IsRunning(); i++)
            Thread.Sleep(100);
        Console.WriteLine(IsRunning() ? "Agent started." : $"Agent did not start; check {Paths.Log}.");
        return IsRunning() ? 0 : 1;
    }

    public static int Stop()
    {
        if (!EventWaitHandle.TryOpenExisting(AgentIpc.StopEventName, out var stop) || !IsRunning())
        {
            stop?.Dispose();
            Console.WriteLine("Agent is not running.");
            return 0;
        }
        using (stop)
            stop.Set();

        for (var i = 0; i < 100 && IsRunning(); i++)
            Thread.Sleep(100);
        Console.WriteLine(IsRunning() ? "Agent did not stop within 10 seconds." : "Agent stopped.");
        return IsRunning() ? 1 : 0;
    }

    public static int Status()
    {
        Console.WriteLine($"Agent:    {(IsRunning() ? "running" : "not running")}");
        using (var key = Registry.CurrentUser.OpenSubKey(RunKeyPath))
        {
            var registered = key?.GetValue(RunValueName) as string;
            Console.WriteLine($"Startup:  {(registered is null ? "not registered" : registered)}");
        }
        Console.WriteLine($"PATH:     {(UserPath.Contains(InstallDir) ? "yes" : "no")} ({InstallDir})");
        Console.WriteLine($"Data:     {Paths.DataDir}");

        if (!File.Exists(Paths.Database))
        {
            Console.WriteLine("Database: not created yet");
            return 0;
        }

        using var db = Database.Open(Paths.Database);
        var count = Convert.ToInt64(db.Scalar("SELECT COUNT(*) FROM events;"));
        var first = db.Scalar("SELECT MIN(ts) FROM events;");
        var last = db.Scalar("SELECT MAX(ts) FROM events;");
        var size = new FileInfo(Paths.Database).Length;
        Console.WriteLine($"Database: {count:N0} events, {size / 1024.0 / 1024.0:N1} MB");
        if (first is long f && last is long l)
            Console.WriteLine($"Range:    {Local(f):yyyy-MM-dd HH:mm} → {Local(l):yyyy-MM-dd HH:mm}");
        return 0;
    }

    private static DateTime Local(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime;

    private static void EnsureAgentExists()
    {
        if (!File.Exists(AgentPath))
            throw new FileNotFoundException($"{AgentIpc.ExeName} was not found next to watchme.exe ({AgentPath}).");
    }
}
