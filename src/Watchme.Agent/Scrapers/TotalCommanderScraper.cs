using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Watchme.Core;

namespace Watchme.Agent.Scrapers;

/// <summary>
/// Reads Total Commander's file operation log (Configuration → Log file) and emits
/// totalcmd/file_op events. Lines written in quick succession (one folder copy writes a line per
/// file) are grouped into one action: "Copied 120 files from C:\a to D:\b".
/// </summary>
/// <remarks>
/// Log lines look like <c>22/11/2026 14:02:37: Move: c:\a\x.md -> c:\a\y.md</c>; the date uses the
/// Windows short date format. TC logs a rename as a Move within one folder.
/// </remarks>
internal sealed partial class TotalCommanderScraper(Func<string?> logPath, int backfillDays) : IScraper
{
    private const string Source = EventSources.TotalCommander;
    private const string Kind = "file_op";
    private const string App = "TOTALCMD64.EXE";

    /// <summary>Lines this close together belong to the same action.</summary>
    private const long GroupGapMs = 2_000;

    /// <summary>A group is only emitted once nothing has been added to it for this long.</summary>
    private const long SettleMs = 10_000;

    private const int MaxItemsStored = 100;

    private static readonly JsonSerializerOptions Json = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly string[] FallbackDateFormats =
        ["dd/MM/yyyy", "dd.MM.yyyy", "d.M.yyyy", "yyyy-MM-dd", "MM/dd/yyyy", "M/d/yyyy"];

    [GeneratedRegex(@"^(?<stamp>\S+ \d{1,2}:\d{2}:\d{2}): (?<op>[A-Za-z][\w ]*?)(?: \((?<status>[^)]*)\))?: (?<args>.*)$")]
    private static partial Regex LineRegex();

    public string Name => "totalcmd";
    public TimeSpan Interval => TimeSpan.FromSeconds(15);

    /// <summary>Reads LogFile= from wincmd.ini and expands environment variables like %TEMP%.</summary>
    public static string? LogPathFromIni(string iniPath)
    {
        if (!File.Exists(iniPath))
            return null;
        // wincmd.ini can be ANSI, UTF-8 or UTF-16; ReadAllLines detects the BOM.
        var line = File.ReadAllLines(iniPath)
            .FirstOrDefault(l => l.StartsWith("LogFile=", StringComparison.OrdinalIgnoreCase));
        var value = line?["LogFile=".Length..].Trim();
        return string.IsNullOrEmpty(value) ? null : Environment.ExpandEnvironmentVariables(value);
    }

    internal sealed record LogLine(long Ts, string Op, string? Status, string? From, string? To, string Raw);

    public IReadOnlyList<WatchEvent> Scrape(ScrapeContext context)
    {
        var path = logPath();
        if (path is null)
        {
            context.Warn("no log file set in wincmd.ini; turn on Total Commander's log under Configuration → Log file");
            return [];
        }
        if (!File.Exists(path))
            context.Warn($"Total Commander's log file {path} does not exist yet");

        var lines = ReadAllLogs(path);
        var pending = NewLines(lines, context.LatestData(Source, Kind), context.Now - backfillDays * 24L * 60 * 60 * 1000);

        var events = new List<WatchEvent>();
        var group = new List<LogLine>();
        foreach (var line in pending)
        {
            if (group.Count > 0 && (line.Ts - group[^1].Ts > GroupGapMs || !Related(group[^1], line)))
            {
                events.Add(ToEvent(group));
                group = [];
            }
            group.Add(line);
        }
        // The last group may still be growing (a long copy); leave it for a later scrape.
        if (group.Count > 0 && context.Now - group[^1].Ts >= SettleMs)
            events.Add(ToEvent(group));
        return events;
    }

    /// <summary>
    /// Do two consecutive lines belong to one action? Same operation, or the folder lines TC writes
    /// around a folder copy (NewFolder + Copy/Move into it) or folder delete (Delete + DeleteFolder).
    /// </summary>
    internal static bool Related(LogLine a, LogLine b)
    {
        if (a.Op == b.Op)
            return true;

        var (folder, other) = a.Op is "NewFolder" or "DeleteFolder" ? (a, b) : (b, a);
        var otherPath = (folder.Op, other.Op) switch
        {
            ("NewFolder", "Copy" or "Move") => other.To,
            ("DeleteFolder", "Delete") => other.From,
            _ => null,
        };
        if (otherPath is null || folder.From is null || Path.GetDirectoryName(otherPath) is not { } parent)
            return false;
        return IsWithin(parent, folder.From) || IsWithin(folder.From, parent);
    }

    private static bool IsWithin(string path, string folder) =>
        path.Equals(folder, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(folder.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

    /// <summary>The current log plus rotated copies next to it, oldest first.</summary>
    private static List<LogLine> ReadAllLogs(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (dir is null || !Directory.Exists(dir))
            return [];

        var pattern = Path.GetFileNameWithoutExtension(path) + "*" + Path.GetExtension(path);
        var lines = new List<LogLine>();
        foreach (var file in new DirectoryInfo(dir).GetFiles(pattern).OrderBy(f => f.LastWriteTimeUtc))
        {
            // TC keeps the file open while it runs; share it so we never block it.
            using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            while (reader.ReadLine() is { } raw)
            {
                if (Parse(raw) is { } line)
                    lines.Add(line);
            }
        }
        // Stable sort: lines within the same second keep their file order.
        return [.. lines.OrderBy(l => l.Ts)];
    }

    internal static LogLine? Parse(string raw)
    {
        var m = LineRegex().Match(raw);
        if (!m.Success || ParseStamp(m.Groups["stamp"].Value) is not { } ts)
            return null;

        var op = m.Groups["op"].Value.Trim();
        var args = m.Groups["args"].Value;
        var arrow = args.IndexOf(" -> ", StringComparison.Ordinal);
        var (from, to) = arrow >= 0 ? (args[..arrow], args[(arrow + 4)..]) : (args, (string?)null);

        // A Move that stays in the same folder is a rename.
        if (op == "Move" && to is not null && SameFolder(from, to))
            op = "Rename";

        var status = m.Groups["status"].Success ? m.Groups["status"].Value : null;
        return new LogLine(ts, op, status, from, to, raw);
    }

    private static long? ParseStamp(string stamp)
    {
        var space = stamp.LastIndexOf(' ');
        var datePart = stamp[..space];
        var timePart = stamp[(space + 1)..];
        var formats = new[] { CultureInfo.CurrentCulture.DateTimeFormat.ShortDatePattern }.Concat(FallbackDateFormats);
        foreach (var format in formats)
        {
            if (DateTime.TryParseExact($"{datePart} {timePart}", $"{format} H:mm:ss", CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeLocal, out var local))
                return new DateTimeOffset(local).ToUnixTimeMilliseconds();
        }
        return null;
    }

    /// <summary>Lines after the last one we stored, or the backfill window on first run.</summary>
    private static IEnumerable<LogLine> NewLines(List<LogLine> lines, string? latestData, long since)
    {
        if (latestData is null || JsonNode.Parse(latestData) is not JsonObject last
            || last["last_ts"]?.GetValue<long>() is not { } lastTs)
            return lines.Where(l => l.Ts >= since);

        var lastRaw = last["last_line"]?.GetValue<string>();
        var index = lines.FindLastIndex(l => l.Ts == lastTs && l.Raw == lastRaw);
        return index >= 0 ? lines.Skip(index + 1) : lines.Where(l => l.Ts > lastTs);
    }

    private static WatchEvent ToEvent(List<LogLine> group)
    {
        var first = group[0];
        var last = group[^1];

        var counts = group.GroupBy(l => l.Op).ToDictionary(g => g.Key, g => g.Count());
        var items = new JsonArray(group.Take(MaxItemsStored).Select(l => (JsonNode)new JsonObject
        {
            ["op"] = l.Op,
            ["from"] = l.From,
            ["to"] = l.To,
            ["status"] = l.Status,
        }).ToArray());

        var data = new JsonObject
        {
            ["ops"] = new JsonObject(counts.Select(c => KeyValuePair.Create(c.Key, (JsonNode?)c.Value))),
            ["count"] = group.Count,
            ["items"] = items,
            ["last_ts"] = last.Ts,
            ["last_line"] = last.Raw,
        };

        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(first.Raw)))[..12];
        return new WatchEvent(first.Ts, Source, Kind)
        {
            App = App,
            Title = Describe(group, counts),
            Data = data.ToJsonString(Json),
            ExtId = $"{Kind}:{first.Ts}:{hash}",
        };
    }

    internal static string Describe(List<LogLine> group, Dictionary<string, int> counts)
    {
        string title;
        if (group.Count == 1)
        {
            var l = group[0];
            title = l.Op switch
            {
                "Copy" => $"Copied {l.From} → {l.To}",
                "Move" => $"Moved {l.From} → {l.To}",
                "Rename" => $"Renamed {l.From} → {Path.GetFileName(l.To)}",
                "Delete" => $"Deleted {l.From}",
                "NewFolder" => $"Created folder {l.From}",
                "DeleteFolder" => $"Deleted folder {l.From}",
                _ => l.To is null ? $"{l.Op} {l.From}" : $"{l.Op} {l.From} → {l.To}",
            };
        }
        else
        {
            // Folder operations ride along with copies and deletes; lead with the file operation.
            var main = counts.Where(c => c.Key is not ("NewFolder" or "DeleteFolder")).MaxBy(c => c.Value).Key
                       ?? counts.MaxBy(c => c.Value).Key;
            var lines = group.Where(l => l.Op == main).ToList();
            var from = CommonFolder(lines.Select(l => l.From));
            var to = CommonFolder(lines.Where(l => l.To is not null).Select(l => l.To));
            var n = lines.Count;
            title = main switch
            {
                "Copy" => $"Copied {Items(n)} from {from} to {to}",
                "Move" => $"Moved {Items(n)} from {from} to {to}",
                "Rename" => $"Renamed {Items(n)} in {from}",
                "Delete" => $"Deleted {Items(n)} in {from}",
                "NewFolder" => $"Created {n} folders in {from}",
                "DeleteFolder" => $"Deleted {n} folders in {from}",
                _ => $"{main} {Items(n)} in {from}",
            };

            var others = counts.Where(c => c.Key != main).Select(c => c.Key switch
            {
                "NewFolder" => $"created {Folders(c.Value)}",
                "DeleteFolder" => $"deleted {Folders(c.Value)}",
                _ => $"{c.Value} × {c.Key}",
            }).ToList();
            if (others.Count > 0)
                title += $" ({string.Join(", ", others)})";
        }

        var problems = group.Where(l => l.Status is not null && !l.Status.Equals("Ok", StringComparison.OrdinalIgnoreCase))
            .GroupBy(l => l.Status!).Select(g => $"{g.Count()} {g.Key.ToLowerInvariant()}").ToList();
        if (problems.Count > 0)
            title += $" — {string.Join(", ", problems)}";
        return title;
    }

    private static string Items(int n) => n == 1 ? "1 item" : $"{n} items";
    private static string Folders(int n) => n == 1 ? "1 folder" : $"{n} folders";

    private static bool SameFolder(string a, string b) =>
        string.Equals(Path.GetDirectoryName(a), Path.GetDirectoryName(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>The deepest folder containing all the given paths.</summary>
    internal static string CommonFolder(IEnumerable<string?> paths)
    {
        string[]? common = null;
        foreach (var p in paths)
        {
            if (p is null)
                continue;
            var parts = (Path.GetDirectoryName(p) ?? p).Split('\\');
            if (common is null)
            {
                common = parts;
                continue;
            }
            var n = 0;
            while (n < common.Length && n < parts.Length && string.Equals(common[n], parts[n], StringComparison.OrdinalIgnoreCase))
                n++;
            common = common[..n];
        }
        if (common is null || common.Length == 0)
            return "(several drives)";
        var joined = string.Join('\\', common);
        return joined.EndsWith(':') ? joined + "\\" : joined;
    }
}
