namespace Watchme.Core;

/// <summary>
/// One raw observation. Everything Watchme records is an event; durations and sessions
/// are derived afterwards (see the <c>focus_spans</c> view), never stored.
/// </summary>
/// <param name="Ts">Unix time in milliseconds, UTC.</param>
/// <param name="Source">The collector: focus, idle, process, session, agent.</param>
/// <param name="Kind">What happened, e.g. foreground, title, idle_start, start, exit, lock.</param>
public sealed record WatchEvent(long Ts, string Source, string Kind)
{
    /// <summary>Executable file name, e.g. "TOTALCMD64.EXE".</summary>
    public string? App { get; init; }
    public int? Pid { get; init; }
    /// <summary>Full executable path.</summary>
    public string? Exe { get; init; }
    public string? Title { get; init; }
    /// <summary>Extra details as a JSON object.</summary>
    public string? Data { get; init; }
}

public static class EventSources
{
    public const string Focus = "focus";
    public const string Idle = "idle";
    public const string Process = "process";
    public const string Session = "session";
    public const string Agent = "agent";
}

public static class Clock
{
    public static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}
