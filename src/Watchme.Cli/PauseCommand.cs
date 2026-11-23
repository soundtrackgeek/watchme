using Watchme.Core;

namespace Watchme.Cli;

/// <summary><c>watchme pause [30m|2h|1h30m|14:30]</c> and <c>watchme resume</c>.</summary>
internal static class PauseCommand
{
    public static int Pause(string[] args)
    {
        var text = args.Length == 0 ? null : string.Join(" ", args);
        var nowLocal = DateTime.Now;
        if (!PauseState.TryParseDuration(text, nowLocal, out var duration, out var error))
        {
            Console.Error.WriteLine(error);
            Console.Error.WriteLine("Usage: watchme pause [30m|2h|1h30m|14:30]   (default 30 minutes)");
            return 2;
        }

        Paths.EnsureDataDir();
        var now = Clock.NowMs();
        var until = now + (long)duration.TotalMilliseconds;
        using var db = Database.Open(Paths.Database);
        var wasPaused = PauseState.Active(PauseState.FilePath, now) is not null;
        PauseState.Begin(db, PauseState.FilePath, now, until);

        var end = DateTimeOffset.FromUnixTimeMilliseconds(until).LocalDateTime;
        var day = end.Date == nowLocal.Date ? "" : end.Date == nowLocal.Date.AddDays(1) ? " tomorrow" : $" on {end:d MMM}";
        Console.WriteLine($"{(wasPaused ? "Pause changed" : "Paused")}: nothing is recorded until {end:HH:mm}{day} ({AgentHealth.Left(until - now)}).");
        Console.WriteLine("That includes what apps log themselves (Edge history, PowerShell, ...), even when it is read later.");
        Console.WriteLine("'watchme resume' starts recording again sooner.");
        if (!AgentControl.IsRunning())
            Console.WriteLine("(The agent isn't running, so nothing is being recorded anyway; the pause still keeps this time out of app history it reads when it starts.)");
        return 0;
    }

    public static int Resume()
    {
        var now = Clock.NowMs();
        if (PauseState.Active(PauseState.FilePath, now) is null)
        {
            // An ended pause whose file the agent never removed (it wasn't running): tidy it up.
            if (File.Exists(PauseState.FilePath))
            {
                using var stale = Database.Open(Paths.Database);
                PauseState.End(stale, PauseState.FilePath, now);
            }
            Console.WriteLine("Not paused.");
            return 0;
        }

        using var db = Database.Open(Paths.Database);
        var pause = PauseState.End(db, PauseState.FilePath, now)!;
        Console.WriteLine($"Recording again (paused for {AgentHealth.Left(now - pause.Since)}).");
        return 0;
    }
}
