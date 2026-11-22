using Watchme.Agent.Scrapers;
using Watchme.Core;

namespace Watchme.Tests;

/// <summary>Runs the scraper against hand-written reflogs in a temporary code folder.</summary>
public sealed class GitScraperTests : IDisposable
{
    private const long Min = 60_000;
    private const string Sha1 = "1111111111111111111111111111111111111111";
    private const string Sha2 = "2222222222222222222222222222222222222222";

    private readonly long _now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "watchme-git-" + Guid.NewGuid().ToString("N"));
    private readonly Database _watchme = Database.Open(":memory:");

    public GitScraperTests() => Directory.CreateDirectory(_root);

    /// <summary>A repo whose folder and .git were created at the given times, with these reflog messages.</summary>
    private string Repo(string name, long folderCreated, long gitCreated, string head, params (long Ts, string Msg)[] reflog)
    {
        var dir = Path.Combine(_root, name);
        var git = Path.Combine(dir, ".git");
        Directory.CreateDirectory(Path.Combine(git, "logs"));
        File.WriteAllText(Path.Combine(git, "HEAD"), $"ref: refs/heads/{head}\n");
        File.WriteAllLines(Path.Combine(git, "logs", "HEAD"), reflog.Select(Line));
        Directory.SetCreationTimeUtc(git, DateTimeOffset.FromUnixTimeMilliseconds(gitCreated).UtcDateTime);
        Directory.SetCreationTimeUtc(dir, DateTimeOffset.FromUnixTimeMilliseconds(folderCreated).UtcDateTime);
        return git;
    }

    private static string Line((long Ts, string Msg) l) => $"{Sha1} {Sha2} Someone <someone@example.com> {l.Ts / 1000} +0100\t{l.Msg}";

    private IReadOnlyList<WatchEvent> Scrape(GitScraper? scraper = null)
    {
        var events = (scraper ?? new GitScraper(() => [_root], backfillDays: 1)).Scrape(new ScrapeContext(_watchme, _now));
        _watchme.Insert(events.ToList());
        return events;
    }

    [Fact]
    public void Reflog_lines_become_events_with_the_branch_they_happened_on()
    {
        var old = _now - 3 * 24 * 60 * Min;
        Repo("app", old, old, "main",
            (_now - 50 * Min, "commit: Fix crash"),
            (_now - 40 * Min, "checkout: moving from feature to main"),
            (_now - 35 * Min, "pull --ff-only: Fast-forward"),
            (_now - 30 * Min, "reset: moving to HEAD"),
            (_now - 20 * Min, "commit (amend): Fix crash properly"));

        var titles = Scrape().Select(e => e.Title).ToList();
        Assert.Equal(
        [
            "Committed to app (feature): Fix crash",
            "Switched app from feature to main",
            "Pulled into app (main): Fast-forward",
            "Amended the last commit in app (main): Fix crash properly",
        ], titles);
    }

    [Fact]
    public void Pushes_and_clones_are_read_and_credentials_dropped()
    {
        var git = Repo("lib", _now - 61 * Min, _now - 60 * Min, "master",
            (_now - 60 * Min, "clone: from https://user:secret@github.com/me/lib.git"));
        var remote = Path.Combine(git, "logs", "refs", "remotes", "origin");
        Directory.CreateDirectory(remote);
        File.WriteAllLines(Path.Combine(remote, "master"), [Line((_now - 5 * Min, "update by push"))]);
        File.WriteAllLines(Path.Combine(remote, "HEAD"), [Line((_now - 5 * Min, "update by push"))]);

        var events = Scrape();
        Assert.Equal(
        [
            "Created folder lib in " + _root,
            "Cloned lib from https://github.com/me/lib.git",
            "Pushed master of lib to origin",
        ], events.Select(e => e.Title));
        Assert.DoesNotContain(events, e => e.Kind == "repo_init");
        Assert.DoesNotContain(events, e => e.Data!.Contains("secret"));
    }

    [Fact]
    public void New_folder_made_a_repo_reads_as_one_line_and_commits_are_grouped()
    {
        var git = Repo("watchme", _now - 60 * Min, _now - 58 * Min, "master",
            (_now - 50 * Min, "commit (initial): Initial commit"),
            (_now - 40 * Min, "commit: Add agent"),
            (_now - 30 * Min, "commit: Add CLI"));
        var remote = Path.Combine(git, "logs", "refs", "remotes", "origin");
        Directory.CreateDirectory(remote);
        File.WriteAllLines(Path.Combine(remote, "master"), [Line((_now - 29 * Min, "update by push"))]);

        Scrape();
        Scrape(); // a re-read with a fresh scraper (as after a restart) adds nothing
        Assert.Equal(6L, _watchme.Scalar("SELECT COUNT(*) FROM events WHERE source = 'git'"));

        var lines = DailyReport.LoadActivity(_watchme, _now - 120 * Min, _now + Min);
        Assert.Equal(2, lines.Count);
        Assert.EndsWith($"Created folder watchme in {_root} and started a git repo", lines[0]);
        Assert.EndsWith("Committed 3 times to watchme (master), pushed to origin: Initial commit; Add agent; Add CLI", lines[1]);
    }

    public void Dispose()
    {
        _watchme.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }
}
