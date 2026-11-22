using Watchme.Core;

namespace Watchme.Cli;

/// <summary><c>watchme powershell install|uninstall|status</c>: sets up command logging in pwsh.</summary>
internal static class PowerShellCommand
{
    public static int Run(string? action) => action?.ToLowerInvariant() switch
    {
        "install" => Install(),
        "uninstall" => Uninstall(),
        null or "status" => Status(),
        _ => Usage(action),
    };

    private static string Profile => PowerShellHook.DefaultProfilePath;

    public static int Install()
    {
        WriteScript();

        var (text, encoding) = PowerShellHook.ReadProfile(Profile);
        var updated = PowerShellHook.WithBlock(text, PowerShellHook.ScriptPath);
        if (updated == text)
        {
            Console.WriteLine($"Already set up in {Profile}.");
        }
        else
        {
            // One backup, of the profile as it was before Watchme first touched it.
            var backup = Profile + ".watchme-backup";
            if (File.Exists(Profile) && !File.Exists(backup))
                File.Copy(Profile, backup);
            PowerShellHook.WriteProfile(Profile, updated, encoding);
            Console.WriteLine($"Added Watchme's hook to {Profile}.");
        }
        Console.WriteLine($"Hook:   {PowerShellHook.ScriptPath}");
        Console.WriteLine($"Inbox:  {Paths.Inbox}");
        Console.WriteLine("Open a new PowerShell tab (or run '. $PROFILE') to start logging commands.");
        return 0;
    }

    /// <summary>Rewrites the hook script, e.g. after an update. Does nothing if it was never installed.</summary>
    public static void RefreshScript()
    {
        if (File.Exists(PowerShellHook.ScriptPath))
            WriteScript();
    }

    public static int Uninstall()
    {
        var (text, encoding) = PowerShellHook.ReadProfile(Profile);
        if (PowerShellHook.HasBlock(text))
        {
            PowerShellHook.WriteProfile(Profile, PowerShellHook.WithoutBlock(text), encoding);
            Console.WriteLine($"Removed Watchme's hook from {Profile}.");
        }
        else
        {
            Console.WriteLine($"Watchme's hook is not in {Profile}.");
        }
        if (File.Exists(PowerShellHook.ScriptPath))
            File.Delete(PowerShellHook.ScriptPath);
        Console.WriteLine("Tabs that are already open keep logging until they are closed.");
        return 0;
    }

    public static int Status()
    {
        var (text, _) = PowerShellHook.ReadProfile(Profile);
        Console.WriteLine($"Profile: {Profile} ({(PowerShellHook.HasBlock(text) ? "hook added" : "no hook")})");
        Console.WriteLine($"Hook:    {PowerShellHook.ScriptPath} ({(File.Exists(PowerShellHook.ScriptPath) ? "present" : "missing")})");
        var today = Path.Combine(Paths.Inbox, $"powershell-{DateTime.Now:yyyy-MM-dd}.jsonl");
        var count = File.Exists(today) ? File.ReadLines(today).Count(l => l.Length > 0) : 0;
        Console.WriteLine($"Today:   {count} commands logged");
        if (!PowerShellHook.HasBlock(text))
            Console.WriteLine("Run 'watchme powershell install' to log the commands you run in PowerShell.");
        return 0;
    }

    private static void WriteScript()
    {
        Directory.CreateDirectory(Paths.Hooks);
        Directory.CreateDirectory(Paths.Inbox);
        File.WriteAllText(PowerShellHook.ScriptPath, PowerShellHook.Script(Paths.Inbox));
    }

    private static int Usage(string action)
    {
        Console.Error.WriteLine($"Unknown action '{action}'. Use: watchme powershell install|uninstall|status");
        return 2;
    }
}
