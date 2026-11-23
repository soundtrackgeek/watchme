using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Watchme.Core;

namespace Watchme.Agent.Scrapers;

/// <summary>
/// Reads T3 Code's state database (read-only) and emits one t3/run event per finished run,
/// i.e. per prompt you sent and the agent work that followed, for every provider (Claude Code,
/// Codex, Antigravity...): project, thread, model, effort, timing and the start of your prompt.
/// </summary>
internal sealed partial class T3Scraper(string statePath, int backfillDays) : IScraper
{
    private const string Source = EventSources.T3;
    private const string Kind = "run";
    private const string App = "T3 Code (Beta).exe";

    /// <summary>Runs can finish out of order (parallel threads); re-read this far back.</summary>
    private const long LookbackMs = 30 * 60 * 1000;

    private const int PromptChars = 200;

    private static readonly JsonSerializerOptions Json = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>The option that means "how hard does it think", per provider.</summary>
    private static readonly string[] EffortOptions = ["effort", "reasoningEffort", "thought_level"];

    [GeneratedRegex(@"!\[[^\]]*\]\(t3-context://[^)]*\)")]
    private static partial Regex AttachmentRegex();

    [GeneratedRegex(@"^claude-(opus|sonnet|haiku|fable)-(\d+)-(\d+)$")]
    private static partial Regex ClaudeModelRegex();

    public string Name => "t3";
    public TimeSpan Interval => TimeSpan.FromSeconds(30);

    public IReadOnlyList<WatchEvent> Scrape(ScrapeContext context)
    {
        if (!File.Exists(statePath))
        {
            context.Warn($"no T3 Code state database at {statePath}");
            return [];
        }

        var cursor = context.Cursor(Source, Kind, "completed_ts");
        var from = cursor is { } c ? c - LookbackMs : context.Now - backfillDays * 24L * 60 * 60 * 1000;

        using var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = statePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
            DefaultTimeout = 5,
        }.ToString());
        db.Open();

        using var cmd = db.CreateCommand();
        // ISO-8601 UTC strings sort like the times they stand for, so compare them as text.
        cmd.CommandText = """
            SELECT r.run_id, r.provider, r.status, r.requested_at, r.completed_at, r.payload_json,
                   t.thread_id, t.title, t.payload_json, p.title, p.workspace_root, m.payload_json
            FROM orchestration_v2_projection_runs r
            JOIN orchestration_v2_projection_threads t ON t.thread_id = r.thread_id
            LEFT JOIN projection_projects p ON p.project_id = t.project_id
            LEFT JOIN orchestration_v2_projection_messages m
                   ON m.message_id = json_extract(r.payload_json, '$.userMessageId')
            WHERE r.completed_at IS NOT NULL AND r.completed_at >= $from
            ORDER BY r.completed_at;
            """;
        cmd.Parameters.AddWithValue("$from", IsoUtc(from));

        var events = new List<WatchEvent>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var run = JsonNode.Parse(r.GetString(5)) as JsonObject;
            var thread = JsonNode.Parse(r.GetString(8)) as JsonObject;
            var message = r.IsDBNull(11) ? null : JsonNode.Parse(r.GetString(11)) as JsonObject;

            var completed = ParseIso(r.GetString(4));
            var started = ParseIso(run?["startedAt"]?.GetValue<string>()) ?? ParseIso(r.GetString(3));
            if (completed is null || started is null)
                continue;
            // A run cancelled while still queued never started; nothing happened.
            if (r.GetString(2) == "cancelled" && run?["startedAt"] is null)
                continue;

            var provider = r.GetString(1);
            var selection = run?["modelSelection"] as JsonObject;
            var model = selection?["model"]?.GetValue<string>();
            var options = ReadOptions(selection);
            var effort = EffortOptions.Select(o => options.GetValueOrDefault(o)).FirstOrDefault(v => v is not null);
            var fast = options.GetValueOrDefault("fastMode") == "true";

            var project = r.IsDBNull(9) ? null : r.GetString(9);
            var threadTitle = r.GetString(7);
            var status = r.GetString(2);
            var lineage = thread?["lineage"] as JsonObject;
            var subagent = lineage?["relationshipToParent"]?.GetValue<string>() == "subagent";
            var durationS = (completed.Value - started.Value) / 1000;

            var modelLabel = ModelLabel(model) + (effort is null ? "" : $" ({effort}{(fast ? ", fast" : "")})");
            var where = project is null ? threadTitle : $"{project} / {threadTitle}";
            var title = $"{ProviderLabel(provider)} {modelLabel} · {where} · {DailyReport.Duration(durationS * 1000)}"
                        + (status == "completed" ? "" : $" ({status})");

            var data = new JsonObject
            {
                ["thread_id"] = r.GetString(6),
                ["thread"] = threadTitle,
                ["project"] = project,
                ["workspace"] = r.IsDBNull(10) ? null : r.GetString(10),
                ["branch"] = thread?["branch"]?.GetValue<string>(),
                ["worktree"] = thread?["worktreePath"]?.GetValue<string>(),
                ["provider"] = provider,
                ["model"] = model,
                ["model_label"] = ModelLabel(model),
                ["effort"] = effort,
                ["options"] = new JsonObject(options.Select(o => KeyValuePair.Create(o.Key, (JsonNode?)o.Value))),
                ["status"] = status,
                ["subagent"] = subagent,
                ["parent_thread_id"] = lineage?["parentThreadId"]?.GetValue<string>(),
                ["duration_s"] = durationS,
                ["completed_ts"] = completed.Value,
                ["prompt"] = PromptPreview(message?["text"]?.GetValue<string>()),
            };

            events.Add(new WatchEvent(started.Value, Source, Kind)
            {
                App = App,
                Title = title,
                Data = data.ToJsonString(Json),
                ExtId = "run:" + r.GetString(0),
            });
        }
        return events;
    }

    private static Dictionary<string, string?> ReadOptions(JsonObject? selection)
    {
        var result = new Dictionary<string, string?>();
        if (selection?["options"] is not JsonArray options)
            return result;
        foreach (var o in options.OfType<JsonObject>())
        {
            if (o["id"]?.GetValue<string>() is { } id)
                result[id] = o["value"] is JsonValue v ? v.ToString().ToLowerInvariant() : null;
        }
        return result;
    }

    /// <summary>"claude-opus-5-5" → "Opus 5.5"; other providers' ids are already readable.</summary>
    internal static string ModelLabel(string? model)
    {
        if (model is null)
            return "(default model)";
        var m = ClaudeModelRegex().Match(model);
        return m.Success
            ? $"{CultureInfo.InvariantCulture.TextInfo.ToTitleCase(m.Groups[1].Value)} {m.Groups[2].Value}.{m.Groups[3].Value}"
            : model;
    }

    internal static string ProviderLabel(string provider) => provider switch
    {
        "claudeAgent" => "Claude Code",
        "codex" => "Codex",
        "antigravity" => "Antigravity",
        _ when provider.StartsWith("acpRegistry_", StringComparison.Ordinal) => provider["acpRegistry_".Length..],
        _ => provider,
    };

    private static string? PromptPreview(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var flat = AttachmentRegex().Replace(text, "[image]").ReplaceLineEndings(" ").Trim();
        return flat.Length <= PromptChars ? flat : flat[..PromptChars] + "…";
    }

    private static long? ParseIso(string? iso) =>
        iso is not null && DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t)
            ? t.ToUnixTimeMilliseconds()
            : null;

    private static string IsoUtc(long ms) =>
        DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
}
