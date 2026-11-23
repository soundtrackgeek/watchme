using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Watchme.Core;

namespace Watchme.Agent.Scrapers;

/// <summary>
/// Reads the activity log Flipside and Forever keep in <c>logs\activity.YYYY-MM-DD.jsonl</c>: one
/// JSON line per thing you did (<c>search</c>, <c>browse</c>, <c>wanted_add</c>,
/// <c>download_queued</c>, <c>download</c>) with <c>ts</c>, a unique <c>id</c> and the details.
/// Each line becomes one event of that kind; the details are kept as <c>data</c>.
/// The apps delete their own old files.
/// </summary>
internal sealed class ActivityLogScraper(string source, string app, string logsDir, int backfillDays) : IScraper
{
    /// <summary>The app writes lines in order, so a short lookback is only a safety margin.</summary>
    private const long LookbackMs = 5 * 60 * 1000;

    private static readonly JsonSerializerOptions Json = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public string Name => source;
    public TimeSpan Interval => TimeSpan.FromSeconds(30);

    public IReadOnlyList<WatchEvent> Scrape(ScrapeContext context)
    {
        if (!Directory.Exists(logsDir))
            return [];

        var cursor = context.Cursor(source, null);
        var from = cursor is { } c ? c - LookbackMs : context.Now - backfillDays * 24L * 60 * 60 * 1000;
        var fromTime = DateTimeOffset.FromUnixTimeMilliseconds(from).UtcDateTime;

        var events = new List<WatchEvent>();
        foreach (var file in new DirectoryInfo(logsDir).EnumerateFiles("activity.*.jsonl"))
        {
            if (file.LastWriteTimeUtc < fromTime)
                continue;
            foreach (var line in CompleteLines(file.FullName))
            {
                if (Parse(line, source, app) is { } e && e.Ts >= from)
                    events.Add(e);
            }
        }
        return events.OrderBy(e => e.Ts).ToList();
    }

    /// <summary>One log line as an event, or null if it is not an activity line.</summary>
    internal static WatchEvent? Parse(string line, string source, string app)
    {
        JsonObject? o;
        try { o = JsonNode.Parse(line) as JsonObject; }
        catch (JsonException) { return null; }
        if (o is null || Long(o, "ts") is not { } ts || Str(o, "id") is not { } id || Str(o, "event") is not { } kind)
            return null;

        var data = new JsonObject();
        foreach (var (key, value) in o)
        {
            if (key is not ("ts" or "id" or "event"))
                data[key] = value?.DeepClone();
        }
        return new WatchEvent(ts, source, kind)
        {
            App = app,
            Title = MusicAppActivity.Title(kind, data),
            Data = data.ToJsonString(Json),
            ExtId = $"activity:{id}",
        };
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

    private static long? Long(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue<long>(out var l) ? l : null;

    private static string? Str(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
