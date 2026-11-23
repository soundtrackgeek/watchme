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
        // Keep an installed PowerShell hook in step with this version.
        PowerShellCommand.RefreshScript();
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
        if (PowerShellHook.HasBlock(PowerShellHook.ReadProfile(PowerShellHook.DefaultProfilePath).Text))
            PowerShellCommand.Uninstall();
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
        // Through the shell, so the long-running agent inherits none of our handles: with
        // UseShellExecute = false it kept a redirected stdout open and the caller never saw EOF.
        Process.Start(new ProcessStartInfo(AgentPath) { UseShellExecute = true });

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
        var running = IsRunning();
        var now = Clock.NowMs();
        var pause = PauseState.Active(PauseState.FilePath, now);
        var agent = running ? "running" : "not running";
        if (pause is not null)
            agent += $", paused until {AgentHealth.When(pause.Until, now)} ({AgentHealth.Left(pause.Until - now)} left; 'watchme resume' ends it)";
        Console.WriteLine($"Agent:    {agent}");
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

        var health = AgentHealth.Build(db, running, now, PauseState.FilePath);
        if (!running && health.LastAlive is { } alive)
            Console.WriteLine($"Last seen {AgentHealth.When(alive, now)}; nothing new is recorded until 'watchme start'.");
        PrintSources(health, now);
        return 0;
    }

    private static void PrintSources(AgentHealth.Report health, long now)
    {
        if (health.Sources.Count == 0)
            return;
        Console.WriteLine();
        Console.WriteLine($"  {"Source",-16} {"Last run",-16} {"Last event",-16} {$"{health.RecentDays} days",8}");
        foreach (var s in health.Sources)
        {
            var mark = s.Level switch
            {
                AgentHealth.Level.Failing => "✕ ",
                AgentHealth.Level.Stalled or AgentHealth.Level.Warning => "! ",
                _ => "",
            };
            var line = $"  {s.Name,-16} {(s.LastRun is { } r ? AgentHealth.When(r, now) : "—"),-16} " +
                       $"{(s.LastEvent is { } e ? AgentHealth.When(e, now) : "—"),-16} {s.Recent,8:N0}";
            Console.WriteLine(s.Message is null ? line : $"{line}  {mark}{s.Message}");
        }
        var problems = health.Problems.Count();
        if (problems > 0)
        {
            Console.WriteLine();
            Console.WriteLine(problems == 1 ? "1 source needs a look; details are in agent.log." : $"{problems} sources need a look; details are in agent.log.");
        }
    }

    private static DateTime Local(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime;

    private static void EnsureAgentExists()
    {
        if (!File.Exists(AgentPath))
            throw new FileNotFoundException($"{AgentIpc.ExeName} was not found next to watchme.exe ({AgentPath}).");
    }
}
