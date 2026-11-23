using System.Diagnostics;
using System.Reflection;
using Watchme.Core;

namespace Watchme.Cli;

/// <summary>
/// <c>watchme update [repo]</c>: rebuilds Watchme from its source folder and replaces the installed
/// copy, stopping the agent first and starting it again afterwards.
/// </summary>
internal static class UpdateCommand
{
    /// <summary>Hidden step run by the new watchme.exe once its files are in place.</summary>
    public const string FinishFlag = "--finish";

    private const string ProjectPath = @"src\Watchme.Cli\Watchme.Cli.csproj";

    private static string InstallDir => AppContext.BaseDirectory.TrimEnd('\\');

    public static int Run(string[] args)
    {
        if (args is [FinishFlag, var restart])
            return Finish(restart == "1");
        if (args.Length > 1)
        {
            Console.Error.WriteLine("Usage: watchme update [path to the watchme repo]");
            return 2;
        }

        if (IsBuildFolder(InstallDir))
        {
            Console.Error.WriteLine($"This watchme.exe runs from a build folder ({InstallDir}).");
            Console.Error.WriteLine("Run 'watchme update' from the installed copy instead.");
            return 2;
        }

        var repo = args.Length == 1 ? Path.GetFullPath(args[0]) : SourceDir();
        if (repo is null || !File.Exists(Path.Combine(repo, ProjectPath)))
        {
            Console.Error.WriteLine(repo is null
                ? "This copy doesn't know where its source is. Run: watchme update <path to the watchme repo>"
                : $"No Watchme source in {repo} ({ProjectPath} is missing). Run: watchme update <path to the watchme repo>");
            return 2;
        }

        RemoveLeftovers(InstallDir);
        var staging = Path.Combine(Paths.DataDir, "update");
        if (Directory.Exists(staging))
            Directory.Delete(staging, recursive: true);

        // Build into a staging folder while the agent keeps running: a failed build changes nothing.
        Console.WriteLine($"Building from {repo}...");
        var (exit, output) = RunDotnet(repo, ["publish", ProjectPath, "-c", "Release", "-o", staging, "--nologo"]);
        if (exit != 0)
        {
            foreach (var line in output.TakeLast(30))
                Console.Error.WriteLine(line);
            Console.Error.WriteLine($"Build failed (dotnet exited with {exit}). The installed copy was not changed.");
            return 1;
        }

        var oldVersion = Version(Path.Combine(InstallDir, "watchme.dll"));
        var newVersion = Version(Path.Combine(staging, "watchme.dll"));

        var wasRunning = AgentControl.IsRunning();
        if (wasRunning && AgentControl.Stop() != 0)
        {
            Console.Error.WriteLine("The agent did not stop, so nothing was replaced. Try again, or stop it in Task Manager.");
            return 1;
        }

        int copied;
        try
        {
            copied = Replace(staging, InstallDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Could not replace the installed files: {ex.Message}");
            if (wasRunning)
                AgentControl.Start();
            return 1;
        }
        finally
        {
            try { Directory.Delete(staging, recursive: true); } catch (IOException) { }
        }

        Console.WriteLine($"Updated {InstallDir}: {oldVersion ?? "?"} → {newVersion ?? "?"} ({copied} files).");

        // Let the new version restart the agent and refresh what it installs, so nothing old is written back.
        using var finish = Process.Start(new ProcessStartInfo(Path.Combine(InstallDir, "watchme.exe"))
        {
            UseShellExecute = false,
            ArgumentList = { "update", FinishFlag, wasRunning ? "1" : "0" },
        })!;
        finish.WaitForExit();
        return finish.ExitCode;
    }

    private static int Finish(bool wasRunning)
    {
        PowerShellCommand.RefreshScript();
        return wasRunning ? AgentControl.Start() : 0;
    }

    /// <summary>Where this copy was built from, stamped in at build time (see Directory.Build.props).</summary>
    private static string? SourceDir() =>
        typeof(UpdateCommand).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "WatchmeSourceDir")?.Value is { Length: > 0 } dir
            ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir))
            : null;

    internal static bool IsBuildFolder(string dir) =>
        dir.Contains(@"\bin\Debug", StringComparison.OrdinalIgnoreCase) ||
        dir.Contains(@"\bin\Release", StringComparison.OrdinalIgnoreCase);

    private static string? Version(string dll) =>
        File.Exists(dll) ? FileVersionInfo.GetVersionInfo(dll).ProductVersion?.Split('+')[0] : null;

    /// <summary>
    /// Copies every file from <paramref name="from"/> into <paramref name="to"/>. Files in use (this
    /// very watchme.exe, or a running dashboard) can't be overwritten but can be renamed, so they are
    /// moved aside as *.old and removed by the next update.
    /// </summary>
    internal static int Replace(string from, string to)
    {
        var count = 0;
        foreach (var source in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, source));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            try
            {
                File.Copy(source, target, overwrite: true);
            }
            catch (IOException) when (File.Exists(target))
            {
                var aside = target + ".old";
                try
                {
                    if (File.Exists(aside))
                        File.Delete(aside);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Still held from an earlier update (a dashboard left running): pick a fresh name.
                    aside = $"{target}.{Guid.NewGuid():N}.old";
                }
                File.Move(target, aside);
                File.Copy(source, target);
            }
            count++;
        }
        return count;
    }

    /// <summary>Deletes the *.old files a previous update set aside; ones still in use are left for next time.</summary>
    internal static void RemoveLeftovers(string dir)
    {
        foreach (var file in Directory.EnumerateFiles(dir, "*.old", SearchOption.AllDirectories))
        {
            try { File.Delete(file); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static (int Exit, List<string> Output) RunDotnet(string workingDir, IEnumerable<string> args)
    {
        var info = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = workingDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args)
            info.ArgumentList.Add(a);

        Process process;
        try
        {
            process = Process.Start(info)!;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return (-1, ["'dotnet' was not found. Install the .NET 10 SDK (see README)."]);
        }

        var output = new List<string>();
        void Collect(object _, DataReceivedEventArgs e)
        {
            if (e.Data is null) return;
            lock (output) output.Add(e.Data);
        }
        using (process)
        {
            process.OutputDataReceived += Collect;
            process.ErrorDataReceived += Collect;
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            process.WaitForExit();
            return (process.ExitCode, output);
        }
    }
}
