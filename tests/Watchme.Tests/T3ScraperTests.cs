using System.Globalization;
using Microsoft.Data.Sqlite;
using Watchme.Agent.Scrapers;
using Watchme.Core;

namespace Watchme.Tests;

/// <summary>Runs the scraper against a fake T3 state database with the columns it reads.</summary>
public sealed class T3ScraperTests : IDisposable
{
    private const long Min = 60_000;
    private const long Now = 1_800_000_000_000;

    private readonly string _path = Path.Combine(Path.GetTempPath(), "watchme-t3-" + Guid.NewGuid().ToString("N") + ".sqlite");
    private readonly Database _watchme = Database.Open(":memory:");

    public T3ScraperTests()
    {
        Exec("""
            CREATE TABLE orchestration_v2_projection_runs (run_id TEXT PRIMARY KEY, thread_id TEXT NOT NULL,
              provider TEXT NOT NULL, status TEXT NOT NULL, requested_at TEXT NOT NULL, completed_at TEXT,
              payload_json TEXT NOT NULL);
            CREATE TABLE orchestration_v2_projection_threads (thread_id TEXT PRIMARY KEY, project_id TEXT NOT NULL,
              title TEXT NOT NULL, payload_json TEXT NOT NULL);
            CREATE TABLE projection_projects (project_id TEXT PRIMARY KEY, title TEXT NOT NULL, workspace_root TEXT);
            CREATE TABLE orchestration_v2_projection_messages (message_id TEXT PRIMARY KEY, payload_json TEXT NOT NULL);
            INSERT INTO projection_projects VALUES ('p1', 'watchme', 'C:\_code\watchme');
            INSERT INTO orchestration_v2_projection_threads VALUES
              ('t1', 'p1', 'Activity monitor', '{"branch":"master"}'),
              ('t2', 'p1', 'Helper', '{"lineage":{"relationshipToParent":"subagent","parentThreadId":"t1"}}');
            """);
    }

    private void Exec(string sql)
    {
        using var c = new SqliteConnection($"Data Source={_path};Pooling=False");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static string Iso(long ms) =>
        DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private void Run(string id, long start, long? end, string status = "completed", string thread = "t1",
        string provider = "claudeAgent", string model = "claude-opus-5-5", string effortJson = """{"id":"effort","value":"medium"}""",
        bool started = true)
    {
        var startedAt = started ? $"\"startedAt\":\"{Iso(start)}\"," : "";
        var payload = "{" + startedAt + $"\"userMessageId\":\"m-{id}\",\"modelSelection\":{{\"model\":\"{model}\",\"options\":[{effortJson}]}}}}";
        var completed = end is { } e ? $"'{Iso(e)}'" : "NULL";
        Exec($$"""
            INSERT INTO orchestration_v2_projection_runs VALUES ('{{id}}', '{{thread}}', '{{provider}}', '{{status}}',
              '{{Iso(start - 1000)}}', {{completed}}, '{{payload}}');
            INSERT INTO orchestration_v2_projection_messages VALUES ('m-{{id}}',
              '{"text":"Fix the report ![shot.png](t3-context://abc)\nplease"}');
            """);
    }

    private IReadOnlyList<WatchEvent> Scrape(long now = Now)
    {
        var events = new T3Scraper(_path, backfillDays: 7).Scrape(new ScrapeContext(_watchme, now));
        _watchme.Insert(events.ToList());
        return events;
    }

    [Fact]
    public void Finished_runs_become_events_with_model_effort_and_prompt()
    {
        Run("r1", Now - 40 * Min, Now - 30 * Min);
        Run("r2", Now - 5 * Min, null, status: "running");

        var e = Assert.Single(Scrape());
        Assert.Equal(Now - 40 * Min, e.Ts);
        Assert.Equal("run:r1", e.ExtId);
        Assert.Equal("Claude Code Opus 5.5 (medium) · watchme / Activity monitor · 10m 00s", e.Title);
        Assert.Contains("\"prompt\":\"Fix the report [image] please\"", e.Data);
        Assert.Contains("\"branch\":\"master\"", e.Data);
    }

    [Fact]
    public void Codex_and_antigravity_efforts_are_read_and_queued_cancels_skipped()
    {
        Run("c", Now - 20 * Min, Now - 18 * Min, provider: "codex", model: "gpt-5.5",
            effortJson: """{"id":"reasoningEffort","value":"high"},{"id":"fastMode","value":true}""");
        Run("g", Now - 15 * Min, Now - 14 * Min, provider: "antigravity", model: "gemini-3-pro",
            effortJson: """{"id":"thought_level","value":"low"}""");
        Run("x", Now - 10 * Min, Now - 10 * Min, status: "cancelled", started: false);

        var titles = Scrape().Select(e => e.Title).ToList();
        Assert.Equal(
        [
            "Codex gpt-5.5 (high, fast) · watchme / Activity monitor · 2m 00s",
            "Antigravity gemini-3-pro (low) · watchme / Activity monitor · 1m 00s",
        ], titles);
    }

    [Fact]
    public void Rescrapes_do_not_duplicate_runs()
    {
        Run("r1", Now - 40 * Min, Now - 30 * Min);
        Scrape();
        Run("r2", Now - 20 * Min, Now - 10 * Min);
        Scrape(Now + Min);

        Assert.Equal(2L, _watchme.Scalar("SELECT COUNT(*) FROM events WHERE source = 't3'"));
    }

    [Fact]
    public void Report_groups_runs_into_thread_sessions_with_your_window_time()
    {
        Run("r1", Now - 60 * Min, Now - 50 * Min);
        Run("r2", Now - 45 * Min, Now - 40 * Min, provider: "codex", model: "gpt-5.5",
            effortJson: """{"id":"reasoningEffort","value":"high"}""");
        Run("sub", Now - 59 * Min, Now - 55 * Min, thread: "t2");
        Run("later", Now + 60 * Min, Now + 61 * Min); // > 30 min after the last run: a new session
        Scrape(Now + 70 * Min);

        var spans = new List<Span>
        {
            new(Now - 62 * Min, Now - 52 * Min, "T3 Code (Beta).exe", "watchme / Activity monitor"),
            new(Now - 52 * Min, Now - 40 * Min, "Code.exe", "something else"),
        };
        var lines = DailyReport.LoadActivity(_watchme, Now - 120 * Min, Now + 120 * Min, spans);

        Assert.Equal(2, lines.Count);
        Assert.EndsWith("watchme / Activity monitor: 2 prompts, Opus 5.5 (medium) + gpt-5.5 (high), agent 15m 00s, you 10m 00s", lines[0]);
        Assert.EndsWith("watchme / Activity monitor: 1 prompt, Opus 5.5 (medium), agent 1m 00s", lines[1]);
    }

    public void Dispose()
    {
        _watchme.Dispose();
        SqliteConnection.ClearAllPools();
        try { File.Delete(_path); } catch (IOException) { }
    }
}
