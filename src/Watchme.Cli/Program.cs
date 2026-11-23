using System.Text;
using Watchme.Cli;
using Watchme.Core;

Console.OutputEncoding = Encoding.UTF8;

var command = args.Length > 0 ? args[0].ToLowerInvariant() : "report";
try
{
    return command switch
    {
        "report" or "today" => Report(args.Skip(1).FirstOrDefault()),
        "search" or "find" => SearchCommand.Run(args.Skip(1).ToArray()),
        "status" => AgentControl.Status(),
        "pause" => PauseCommand.Pause(args.Skip(1).ToArray()),
        "resume" or "unpause" => PauseCommand.Resume(),
        "install" => AgentControl.Install(),
        "uninstall" => AgentControl.Uninstall(),
        "start" => AgentControl.Start(),
        "stop" => AgentControl.Stop(),
        "update" => UpdateCommand.Run(args.Skip(1).ToArray()),
        "dashboard" or "web" => DashboardServer.Run(args.Skip(1).ToArray()),
        "powershell" or "pwsh" => PowerShellCommand.Run(args.Skip(1).FirstOrDefault()),
        "help" or "-h" or "--help" or "/?" => Help(),
        _ => Help(unknown: command),
    };
}
catch (Exception ex)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    return 1;
}

static int Report(string? rangeArg)
{
    var today = DateOnly.FromDateTime(DateTime.Now);
    if (!ReportRange.TryParse(rangeArg, today, out var from, out var to))
    {
        Console.Error.WriteLine($"Expected a day or range ({ReportRange.Forms}), got '{rangeArg}'.");
        return 2;
    }

    if (!File.Exists(Paths.Database))
    {
        Console.WriteLine($"No data yet ({Paths.Database} does not exist). Run 'watchme start' first.");
        return 0;
    }

    using var db = Database.Open(Paths.Database);
    if (from == to)
        DailyReport.Print(db, from, Console.Out);
    else
        RangeReport.Print(db, from, to, Console.Out);
    return 0;
}

static int Help(string? unknown = null)
{
    if (unknown is not null)
        Console.Error.WriteLine($"Unknown command '{unknown}'.\n");
    Console.WriteLine("""
        watchme — what did I actually do on this computer?

        Usage:
          watchme report [DAY|RANGE]                    Summary of a day (default: today) or of a range:
                                                        7d, week, last-week, month, last-month, FROM..TO
          watchme search WORDS... [options]             Find events in the whole history (titles, URLs,
                                                        commands, folders, artists, prompts)
                                                        --source S  --days N  --from D  --to D  --limit N
          watchme status                                Is the agent running, and is every source working?
          watchme pause [30m|2h|1h30m|14:30]            Record nothing for a while (default 30 minutes),
                                                        app history included; resumes by itself
          watchme resume                                End a pause early
          watchme start                                 Start the background agent now
          watchme stop                                  Stop the background agent
          watchme install                               Start the agent at every Windows logon (and now),
                                                        and add watchme to your user PATH
          watchme uninstall                             Undo install and stop the agent (data is kept)
          watchme update [repo]                         Rebuild from source and replace this copy
                                                        (stops and restarts the agent)
          watchme powershell install|uninstall|status   Log the commands you run in PowerShell 7
          watchme dashboard [--port N] [--no-open]      Charts and tables in your browser
                                                        (http://127.0.0.1:7317, Ctrl+C to stop)

        Data:   %LOCALAPPDATA%\Watchme\watchme.db (override with WATCHME_HOME)
        Config: %LOCALAPPDATA%\Watchme\config.json
        """);
    return unknown is null ? 0 : 2;
}
