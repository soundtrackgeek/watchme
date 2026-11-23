using System.Globalization;
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
        "status" => AgentControl.Status(),
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

static int Report(string? dateArg)
{
    var today = DateOnly.FromDateTime(DateTime.Now);
    DateOnly day;
    if (dateArg is null or "today")
        day = today;
    else if (dateArg == "yesterday")
        day = today.AddDays(-1);
    else if (!DateOnly.TryParseExact(dateArg, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out day))
    {
        Console.Error.WriteLine($"Expected a date like 2026-11-22, 'today' or 'yesterday', got '{dateArg}'.");
        return 2;
    }

    if (!File.Exists(Paths.Database))
    {
        Console.WriteLine($"No data yet ({Paths.Database} does not exist). Run 'watchme start' first.");
        return 0;
    }

    using var db = Database.Open(Paths.Database);
    DailyReport.Print(db, day, Console.Out);
    return 0;
}

static int Help(string? unknown = null)
{
    if (unknown is not null)
        Console.Error.WriteLine($"Unknown command '{unknown}'.\n");
    Console.WriteLine("""
        watchme — what did I actually do on this computer?

        Usage:
          watchme report [today|yesterday|YYYY-MM-DD]   Summary of a day (default: today)
          watchme status                                Is the agent running, and what has it recorded?
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
