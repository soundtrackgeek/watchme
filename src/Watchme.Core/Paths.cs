namespace Watchme.Core;

/// <summary>Where Watchme keeps its data. Override with the WATCHME_HOME environment variable.</summary>
public static class Paths
{
    public static string DataDir { get; } =
        Environment.GetEnvironmentVariable("WATCHME_HOME") is { Length: > 0 } home
            ? home
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Watchme");

    public static string Database => Path.Combine(DataDir, "watchme.db");
    public static string Config => Path.Combine(DataDir, "config.json");
    public static string Log => Path.Combine(DataDir, "agent.log");

    /// <summary>Where shell hooks drop events (JSON lines) for the agent to read.</summary>
    public static string Inbox => Path.Combine(DataDir, "inbox");

    /// <summary>Scripts that shells load from their profiles (see <c>watchme powershell install</c>).</summary>
    public static string Hooks => Path.Combine(DataDir, "hooks");

    public static void EnsureDataDir() => Directory.CreateDirectory(DataDir);
}

/// <summary>Names shared by the agent and the CLI so they can find each other.</summary>
public static class AgentIpc
{
    /// <summary>Held by the running agent; also guarantees a single instance per user session.</summary>
    public const string MutexName = @"Local\Watchme.Agent";

    /// <summary>Signalled by <c>watchme stop</c> to ask the agent to shut down cleanly.</summary>
    public const string StopEventName = @"Local\Watchme.Agent.Stop";

    public const string ExeName = "Watchme.Agent.exe";
}
