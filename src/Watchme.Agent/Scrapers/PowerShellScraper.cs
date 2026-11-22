using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Watchme.Core;

namespace Watchme.Agent.Scrapers;

/// <summary>
/// Reads the commands Watchme's PowerShell hook (see <see cref="PowerShellHook"/>) appends to
/// <c>inbox\powershell-YYYY-MM-DD.jsonl</c>, one JSON line per command, and emits one
/// powershell/command event each. Inbox files older than 30 days are deleted.
/// </summary>
internal sealed class PowerShellScraper(string inbox, int backfillDays) : IScraper
{
    private const string Source = EventSources.PowerShell;
    private const string Kind = "command";
    private const string App = "pwsh.exe";
    private const int TitleChars = 200;

    /// <summary>Tabs write their own lines, so a slow command can land after a later one.</summary>
    private const long LookbackMs = 10 * 60 * 1000;

    private static readonly TimeSpan KeepFiles = TimeSpan.FromDays(30);

    private static readonly JsonSerializerOptions Json = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public string Name => "powershell";
    public TimeSpan Interval => TimeSpan.FromSeconds(30);

    public IReadOnlyList<WatchEvent> Scrape(ScrapeContext context)
    {
        if (!Directory.Exists(inbox))
            return [];

        var cursor = context.Cursor(Source, Kind, "end_ts");
        var from = cursor is { } c ? c - LookbackMs : context.Now - backfillDays * 24L * 60 * 60 * 1000;
        var fromTime = DateTimeOffset.FromUnixTimeMilliseconds(from).UtcDateTime;

        var events = new List<WatchEvent>();
        foreach (var file in new DirectoryInfo(inbox).EnumerateFiles("powershell-*.jsonl"))
        {
            if (file.LastWriteTimeUtc < DateTime.UtcNow - KeepFiles)
            {
                TryDelete(file);
                continue;
            }
            if (file.LastWriteTimeUtc < fromTime)
                continue;
            foreach (var line in CompleteLines(file.FullName))
            {
                if (Parse(line) is { } e && (Long(e.Data, "end_ts") ?? e.Ts) >= from)
                    events.Add(e);
            }
        }
        return events.OrderBy(e => e.Ts).ToList();
    }

    /// <summary>One line of the inbox as an event, or null if it is not one the hook wrote.</summary>
    internal static WatchEvent? Parse(string line)
    {
        JsonObject? o;
        try { o = JsonNode.Parse(line) as JsonObject; }
        catch (JsonException) { return null; }
        if (o is null || Long(o, "start") is not { } start || Long(o, "end") is not { } end
            || Long(o, "session") is not { } session || Long(o, "pid") is not { } pid || Long(o, "id") is not { } id
            || Str(o, "cmd") is not { } cmd)
            return null;

        var ok = o["ok"] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : (bool?)null;
        var data = new JsonObject
        {
            ["cmd"] = cmd,
            ["cwd"] = Str(o, "cwd"),
            ["cwd_after"] = Str(o, "cwd_after"),
            ["duration_ms"] = Math.Max(0, end - start),
            ["ok"] = ok,
            ["exit_code"] = Long(o, "exit_code"),
            ["status"] = Str(o, "status"),
            ["shell"] = Str(o, "shell"),
            ["session"] = session,
            ["pid"] = pid,
            ["end_ts"] = end,
        };
        return new WatchEvent(start, Source, Kind)
        {
            App = App,
            Pid = (int)pid,
            Title = Title(cmd),
            Data = data.ToJsonString(Json),
            ExtId = $"cmd:{session}:{pid}:{id}",
        };
    }

    private static string Title(string cmd)
    {
        var first = cmd.ReplaceLineEndings("\n").Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "";
        if (first.Length > TitleChars)
            first = first[..TitleChars] + "…";
        return cmd.Contains('\n') ? first + " …" : first;
    }

    /// <summary>Lines that end in a line break; a line still being written is left for the next scrape.</summary>
    private static IEnumerable<string> CompleteLines(string path)
    {
        string text;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            text = reader.ReadToEnd();
        }
        catch (IOException)
        {
            yield break;
        }
        var lastBreak = text.LastIndexOf('\n');
        if (lastBreak < 0)
            yield break;
        foreach (var line in text[..lastBreak].Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');
            if (trimmed.Length > 0)
                yield return trimmed;
        }
    }

    private static void TryDelete(FileInfo file)
    {
        try { file.Delete(); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static long? Long(JsonObject? o, string key) => o?[key] is JsonValue v && v.TryGetValue<long>(out var l) ? l : null;

    private static long? Long(string? json, string key) =>
        json is null ? null : Long(JsonNode.Parse(json) as JsonObject, key);

    private static string? Str(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
