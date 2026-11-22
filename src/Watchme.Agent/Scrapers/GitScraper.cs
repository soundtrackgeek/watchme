using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Watchme.Core;

namespace Watchme.Agent.Scrapers;

/// <summary>
/// Finds the git repos under the configured roots and reads their reflogs, the plain-text logs git
/// keeps of everything that moved HEAD (commit, checkout, pull, merge, reset, rebase, clone) and of
/// pushes (<c>logs/refs/remotes</c>). No git process is started. Also records new folders directly
/// inside the roots and new repos (from the creation time of <c>.git</c>).
/// </summary>
internal sealed partial class GitScraper(Func<IReadOnlyList<string>> roots, int backfillDays) : IScraper
{
    private const string Source = EventSources.Git;

    /// <summary>Re-read this far back; also covers repos found by a later rescan.</summary>
    private const long LookbackMs = 20 * 60 * 1000;

    /// <summary>Looking for new repos means listing two folder levels, so not on every scrape.</summary>
    private const long RescanMs = 10 * 60 * 1000;

    private static readonly JsonSerializerOptions Json = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Folders not worth looking into for nested repos.</summary>
    private static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", "bin", "obj", "target", "dist", "build", "venv", ".venv", "packages",
    };

    // <old> <new> <name> <<email>> <unix seconds> <tz>\t<message>
    [GeneratedRegex(@"^(?<old>[0-9a-f]+) (?<new>[0-9a-f]+) .*? (?<ts>\d+) [+-]\d{4}\t(?<msg>.*)$")]
    private static partial Regex LineRegex();

    [GeneratedRegex(@"^moving from (?<from>.+) to (?<to>.+)$")]
    private static partial Regex CheckoutRegex();

    [GeneratedRegex(@"returning to refs/heads/(?<branch>.+)$")]
    private static partial Regex RebaseFinishRegex();

    [GeneratedRegex(@"^[0-9a-f]{7,64}$")]
    private static partial Regex ShaRegex();

    [GeneratedRegex(@"://[^/@\s]+@")]
    private static partial Regex UrlCredentialsRegex();

    private sealed record Repo(string Name, string Path, string GitDir);

    private List<Repo>? _repos;
    private long _scannedAt;

    public string Name => "git";
    public TimeSpan Interval => TimeSpan.FromSeconds(60);

    public IReadOnlyList<WatchEvent> Scrape(ScrapeContext context)
    {
        var cursor = context.Cursor(Source, kind: null);
        var cutoff = cursor is { } c ? c - LookbackMs : context.Now - backfillDays * 24L * 60 * 60 * 1000;

        var rootDirs = roots()
            .Select(r => Normalize(Environment.ExpandEnvironmentVariables(r)))
            .Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (_repos is null || context.Now - _scannedAt >= RescanMs)
        {
            _repos = Discover(rootDirs);
            _scannedAt = context.Now;
        }

        var events = new List<WatchEvent>();
        foreach (var root in rootDirs)
            NewFolders(root, cutoff, events);
        foreach (var repo in _repos)
        {
            try
            {
                ScrapeRepo(repo, cutoff, events);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Deleted or locked while we looked; next scrape tries again.
            }
        }
        return events.OrderBy(e => e.Ts).ToList();
    }

    private static List<Repo> Discover(IEnumerable<string> rootDirs)
    {
        var repos = new List<Repo>();
        foreach (var root in rootDirs)
        {
            foreach (var child in SafeDirs(root))
            {
                if (IsRepo(child))
                {
                    repos.Add(new Repo(System.IO.Path.GetFileName(child), child, System.IO.Path.Combine(child, ".git")));
                    continue;
                }
                if (SkipDirs.Contains(System.IO.Path.GetFileName(child)))
                    continue;
                foreach (var grandchild in SafeDirs(child).Where(IsRepo))
                    repos.Add(new Repo(System.IO.Path.GetRelativePath(root, grandchild).Replace('\\', '/'), grandchild,
                        System.IO.Path.Combine(grandchild, ".git")));
            }
        }
        return repos;

        // A checkout of a worktree or submodule has a .git *file*; those are read through their main repo.
        static bool IsRepo(string dir) => Directory.Exists(System.IO.Path.Combine(dir, ".git"));
    }

    private static IEnumerable<string> SafeDirs(string dir)
    {
        try
        {
            return Directory.GetDirectories(dir)
                .Where(d => !System.IO.Path.GetFileName(d).StartsWith('.'))
                .Select(Normalize)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static void NewFolders(string root, long cutoff, List<WatchEvent> events)
    {
        foreach (var dir in SafeDirs(root))
        {
            var created = Ms(Directory.GetCreationTimeUtc(dir));
            if (created < cutoff)
                continue;
            var name = System.IO.Path.GetFileName(dir);
            events.Add(Event(created, "folder_created", $"Created folder {name} in {root}",
                new JsonObject { ["path"] = dir, ["name"] = name, ["root"] = root },
                $"folder:{dir.ToLowerInvariant()}:{created}"));
        }
    }

    private static void ScrapeRepo(Repo repo, long cutoff, List<WatchEvent> events)
    {
        var headLog = System.IO.Path.Combine(repo.GitDir, "logs", "HEAD");
        var lines = ReadLines(headLog);

        var initialized = Ms(Directory.GetCreationTimeUtc(repo.GitDir));
        var cloned = lines.Count > 0 && Parse(lines[0]) is { } first && first.Msg.StartsWith("clone:", StringComparison.Ordinal);
        if (initialized >= cutoff && !cloned)
        {
            events.Add(Event(initialized, "repo_init", $"Started a git repo in {repo.Path}",
                new JsonObject { ["repo"] = repo.Name, ["path"] = repo.Path },
                $"init:{repo.Path.ToLowerInvariant()}:{initialized}"));
        }

        ReadHeadLog(repo, worktree: null, headLog, lines, CurrentBranch(System.IO.Path.Combine(repo.GitDir, "HEAD")), cutoff, events);

        var worktrees = System.IO.Path.Combine(repo.GitDir, "worktrees");
        if (Directory.Exists(worktrees))
        {
            foreach (var wt in Directory.GetDirectories(worktrees))
            {
                var log = System.IO.Path.Combine(wt, "logs", "HEAD");
                if (!File.Exists(log) || Ms(File.GetLastWriteTimeUtc(log)) < cutoff)
                    continue;
                // "gitdir" holds the path of the worktree's .git file.
                var gitdirFile = System.IO.Path.Combine(wt, "gitdir");
                var path = File.Exists(gitdirFile)
                    ? System.IO.Path.GetDirectoryName(File.ReadAllText(gitdirFile).Trim())?.Replace('/', '\\')
                    : null;
                ReadHeadLog(repo, path ?? wt, log, ReadLines(log), CurrentBranch(System.IO.Path.Combine(wt, "HEAD")), cutoff, events);
            }
        }

        var remotes = System.IO.Path.Combine(repo.GitDir, "logs", "refs", "remotes");
        if (Directory.Exists(remotes))
        {
            foreach (var log in Directory.EnumerateFiles(remotes, "*", SearchOption.AllDirectories))
                ReadPushLog(repo, remotes, log, cutoff, events);
        }
    }

    private sealed record Line(string Raw, string NewSha, long Ts, string Msg);

    private static Line? Parse(string raw)
    {
        var m = LineRegex().Match(raw);
        return m.Success
            ? new Line(raw, m.Groups["new"].Value, long.Parse(m.Groups["ts"].Value) * 1000, m.Groups["msg"].Value)
            : null;
    }

    private static void ReadHeadLog(Repo repo, string? worktree, string logFile, List<string> raw, string headBranch,
        long cutoff, List<WatchEvent> events)
    {
        if (raw.Count == 0 || Ms(File.GetLastWriteTimeUtc(logFile)) < cutoff)
            return;
        var lines = raw.Select(Parse).OfType<Line>().ToList();

        // Commit lines don't name the branch. Walk backwards from what HEAD points at now: a
        // checkout "from A to B" means everything before it happened on A.
        var branches = new string[lines.Count];
        var after = headBranch;
        for (var i = lines.Count - 1; i >= 0; i--)
        {
            var (action, rest) = Split(lines[i].Msg);
            if (action.StartsWith("rebase", StringComparison.Ordinal) && RebaseFinishRegex().Match(rest) is { Success: true } rb)
                after = rb.Groups["branch"].Value;
            branches[i] = after;
            if (action == "checkout" && CheckoutRegex().Match(rest) is { Success: true } co)
                after = BranchName(co.Groups["from"].Value);
        }

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (line.Ts < cutoff)
                continue;
            var (action, rest) = Split(line.Msg);
            var branch = branches[i];
            var where = $"{repo.Name} ({branch})";
            var data = new JsonObject
            {
                ["repo"] = repo.Name,
                ["path"] = repo.Path,
                ["worktree"] = worktree,
                ["branch"] = branch,
                ["action"] = action,
                ["message"] = rest,
                ["sha"] = line.NewSha[..Math.Min(12, line.NewSha.Length)],
            };

            string kind, title;
            if (action == "commit" || action == "cherry-pick" || action.StartsWith("commit (", StringComparison.Ordinal))
            {
                kind = "commit";
                title = action switch
                {
                    "commit (initial)" => $"First commit in {where}: {rest}",
                    "commit (amend)" => $"Amended the last commit in {where}: {rest}",
                    "commit (merge)" => $"Committed a merge in {where}: {rest}",
                    "commit (cherry-pick)" or "cherry-pick" => $"Cherry-picked into {where}: {rest}",
                    _ => $"Committed to {where}: {rest}",
                };
            }
            else if (action.StartsWith("pull", StringComparison.Ordinal))
            {
                kind = "pull";
                title = $"Pulled into {where}: {rest}";
            }
            else if (action.StartsWith("merge ", StringComparison.Ordinal))
            {
                kind = "merge";
                title = $"Merged {ShortRef(action["merge ".Length..])} into {where}: {rest}";
            }
            else if (action == "checkout" && CheckoutRegex().Match(rest) is { Success: true } co)
            {
                var from = BranchName(co.Groups["from"].Value);
                var to = BranchName(co.Groups["to"].Value);
                if (from == to)
                    continue;
                kind = "checkout";
                title = $"Switched {repo.Name} from {from} to {to}";
                data["from"] = from;
                data["to"] = to;
            }
            else if (action == "reset" && rest.StartsWith("moving to ", StringComparison.Ordinal))
            {
                var target = rest["moving to ".Length..];
                if (target == "HEAD") // e.g. what `git worktree add` writes first
                    continue;
                kind = "reset";
                title = $"Reset {where} to {ShortRef(target)}";
            }
            else if (action.StartsWith("rebase", StringComparison.Ordinal) && action.EndsWith("(finish)", StringComparison.Ordinal))
            {
                kind = "rebase";
                title = $"Rebased {where}";
            }
            else if (action == "clone")
            {
                var url = UrlCredentialsRegex().Replace(rest.StartsWith("from ", StringComparison.Ordinal) ? rest[5..] : rest, "://");
                kind = "clone";
                title = $"Cloned {repo.Name} from {url}";
                data["message"] = url;
            }
            else
            {
                continue; // rebase steps, branch creation and the like
            }

            events.Add(Event(line.Ts, kind, title, data, $"{kind}:{Hash(logFile, line.Raw)}"));
        }
    }

    private static void ReadPushLog(Repo repo, string remotesDir, string logFile, long cutoff, List<WatchEvent> events)
    {
        if (Ms(File.GetLastWriteTimeUtc(logFile)) < cutoff)
            return;
        var parts = System.IO.Path.GetRelativePath(remotesDir, logFile).Replace('\\', '/').Split('/', 2);
        if (parts.Length < 2 || parts[1] == "HEAD")
            return;
        var (remote, branch) = (parts[0], parts[1]);

        foreach (var line in ReadLines(logFile).Select(Parse).OfType<Line>())
        {
            if (line.Ts < cutoff || line.Msg != "update by push")
                continue;
            events.Add(Event(line.Ts, "push", $"Pushed {branch} of {repo.Name} to {remote}",
                new JsonObject
                {
                    ["repo"] = repo.Name, ["path"] = repo.Path, ["branch"] = branch, ["remote"] = remote,
                    ["sha"] = line.NewSha[..Math.Min(12, line.NewSha.Length)],
                },
                $"push:{Hash(logFile, line.Raw)}"));
        }
    }

    private static (string Action, string Text) Split(string msg)
    {
        var colon = msg.IndexOf(": ", StringComparison.Ordinal);
        return colon < 0 ? (msg, "") : (msg[..colon], msg[(colon + 2)..]);
    }

    private static string CurrentBranch(string headFile)
    {
        try
        {
            var head = File.ReadAllText(headFile).Trim();
            return head.StartsWith("ref: refs/heads/", StringComparison.Ordinal) ? head["ref: refs/heads/".Length..] : "detached HEAD";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "?";
        }
    }

    private static string BranchName(string name) => ShaRegex().IsMatch(name) ? "detached HEAD" : name;

    private static string ShortRef(string name) => ShaRegex().IsMatch(name) ? name[..7] : name;

    private static List<string> ReadLines(string path)
    {
        if (!File.Exists(path))
            return [];
        // Git appends while we read; share everything.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var lines = new List<string>();
        while (reader.ReadLine() is { } line)
            lines.Add(line);
        return lines;
    }

    private static WatchEvent Event(long ts, string kind, string title, JsonObject data, string extId) =>
        new(ts, Source, kind) { App = "git", Title = title, Data = data.ToJsonString(Json), ExtId = extId };

    private static string Hash(string file, string line) =>
        Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(file.ToLowerInvariant() + "\n" + line)))[..16];

    private static string Normalize(string path) => System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path));

    private static long Ms(DateTime utc) => new DateTimeOffset(utc, TimeSpan.Zero).ToUnixTimeMilliseconds();
}
