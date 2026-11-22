using System.Text.Json.Nodes;

namespace Watchme.Core;

/// <summary>A focus span clipped to the day being reported.</summary>
public sealed record Span(long Start, long End, string App, string? Title)
{
    public long DurationMs => End - Start;
}

/// <summary>Spans of one app merged into a single stretch of work.</summary>
public sealed record Block(long Start, long End, string App, long ActiveMs, string? MainTitle);

public static class DailyReport
{
    /// <summary>The same app coming back within this gap continues the same block.</summary>
    public const long MergeGapMs = 2 * 60 * 1000;

    public static List<Span> LoadSpans(Database db, long fromMs, long toMs)
    {
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = """
            SELECT start_ts, end_ts, app, title
            FROM focus_spans
            WHERE start_ts < $to AND end_ts > $from
            ORDER BY start_ts, id;
            """;
        cmd.Parameters.AddWithValue("$from", fromMs);
        cmd.Parameters.AddWithValue("$to", toMs);

        var spans = new List<Span>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var start = Math.Max(r.GetInt64(0), fromMs);
            var end = Math.Min(r.GetInt64(1), toMs);
            if (end > start)
                spans.Add(new Span(start, end, r.IsDBNull(2) ? "?" : r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3)));
        }
        return spans;
    }

    private sealed class Run(Span first)
    {
        public long Start = first.Start;
        public long End = first.End;
        public readonly string App = first.App;
        public long ActiveMs;
        public readonly Dictionary<string, long> Titles = [];

        public void Add(Span s)
        {
            End = Math.Max(End, s.End);
            ActiveMs += s.DurationMs;
            if (!string.IsNullOrEmpty(s.Title))
                Titles[s.Title] = Titles.GetValueOrDefault(s.Title) + s.DurationMs;
        }

        public void Absorb(Run other)
        {
            End = Math.Max(End, other.End);
            ActiveMs += other.ActiveMs;
            foreach (var (title, ms) in other.Titles)
                Titles[title] = Titles.GetValueOrDefault(title) + ms;
        }

        public Block ToBlock() =>
            new(Start, End, App, ActiveMs, Titles.Count == 0 ? null : Titles.MaxBy(t => t.Value).Key);
    }

    /// <summary>
    /// Turns spans into blocks of work in two passes:
    /// 1. consecutive spans of the same app (title changes, short idle gaps) become one run;
    /// 2. short detours are folded away: A(10m) B(30s) A(10m) becomes one A block, and B
    ///    is listed separately. ActiveMs only counts time the app actually had focus.
    /// </summary>
    public static List<Block> BuildBlocks(IReadOnlyList<Span> spans, long mergeGapMs)
    {
        // Pass 1: runs.
        var runs = new List<Run>();
        foreach (var s in spans)
        {
            var last = runs.Count > 0 ? runs[^1] : null;
            if (last is null || last.App != s.App || s.Start - last.End > mergeGapMs)
            {
                last = new Run(s);
                runs.Add(last);
            }
            last.Add(s);
        }

        // Pass 2: fold runs back into the previous block of the same app across short detours.
        var kept = new List<Run>();
        var detours = new List<Run>();
        foreach (var run in runs)
        {
            var main = kept.Count > 0 ? kept[^1] : null;
            if (main is not null && main.App == run.App && run.Start - main.End <= mergeGapMs)
            {
                main.Absorb(run);
                kept.AddRange(detours);       // Detours stay visible as their own (short) blocks,
                detours.Clear();              // listed after the block they interrupted.
                kept.Remove(main);
                kept.Add(main);
            }
            else if (main is not null && run.ActiveMs < mergeGapMs)
            {
                detours.Add(run);
            }
            else
            {
                kept.AddRange(detours);
                detours.Clear();
                kept.Add(run);
            }
        }
        kept.AddRange(detours);

        return kept.Select(r => r.ToBlock()).OrderBy(b => b.Start).ToList();
    }

    public static void Print(Database db, DateOnly day, TextWriter output)
    {
        var from = new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Local)).ToUnixTimeMilliseconds();
        var to = new DateTimeOffset(day.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Local)).ToUnixTimeMilliseconds();
        var spans = LoadSpans(db, from, to);

        output.WriteLine($"Watchme — {day.ToDateTime(TimeOnly.MinValue):dddd d MMMM yyyy}");
        output.WriteLine();
        if (spans.Count == 0)
        {
            output.WriteLine("Nothing recorded for this day.");
            return;
        }

        var active = spans.Sum(s => s.DurationMs);
        output.WriteLine($"Active  {Duration(active)}   (first {Time(spans[0].Start)}, last {Time(spans[^1].End)})");
        output.WriteLine();

        output.WriteLine("Apps");
        var byApp = spans.GroupBy(s => s.App, StringComparer.OrdinalIgnoreCase)
            .Select(g => (App: g.Key, Ms: g.Sum(s => s.DurationMs)))
            .OrderByDescending(a => a.Ms)
            .ToList();
        var top = byApp[0].Ms;
        var shown = byApp.Where(a => a.Ms >= 30_000).Take(20).ToList();
        foreach (var (app, ms) in shown)
            output.WriteLine($"  {Trim(app, 28),-28} {Duration(ms),8}  {Bar(ms, top)}");
        var rest = byApp.Skip(shown.Count).ToList();
        if (rest.Count > 0)
        {
            var restMs = rest.Sum(a => a.Ms);
            var names = Trim(string.Join(", ", rest.Select(a => a.App)), 60);
            output.WriteLine($"  {$"other ({rest.Count})",-28} {Duration(restMs),8}  {Bar(restMs, top)}  {names}");
        }
        output.WriteLine();

        output.WriteLine("Timeline (blocks of 1 minute or more)");
        var blocks = BuildBlocks(spans, MergeGapMs);
        foreach (var b in blocks.Where(b => b.ActiveMs >= 60_000))
        {
            var title = b.MainTitle is null ? "" : $"  {Trim(b.MainTitle, 70)}";
            output.WriteLine($"  {Time(b.Start)}–{Time(b.End)} {Duration(b.ActiveMs),8}  {Trim(b.App, 24),-24}{title}");
        }
        var shortBlocks = blocks.Where(b => b.ActiveMs < 60_000).ToList();
        if (shortBlocks.Count > 0)
            output.WriteLine($"  + {shortBlocks.Count} shorter visits, {Duration(shortBlocks.Sum(b => b.ActiveMs))} in total");

        var activity = LoadActivity(db, from, to, spans);
        if (activity.Count > 0)
        {
            output.WriteLine();
            output.WriteLine("Activity in apps");
            foreach (var line in activity)
                output.WriteLine($"  {line}");
        }

        var launched = LoadLaunches(db, from, to, byApp.Select(a => a.App).ToHashSet(StringComparer.OrdinalIgnoreCase));
        if (launched.Count > 0)
        {
            output.WriteLine();
            output.WriteLine("Opened (apps you also used this day)");
            foreach (var (ts, app) in launched)
                output.WriteLine($"  {Time(ts)}  {app}");
        }
    }

    private sealed record ActivityEvent(long Ts, string Source, string Kind, string? Title, JsonObject? Data);

    /// <summary>
    /// Events scraped from apps, as report lines. Consecutive listens to the same album become one
    /// line ("Listened to X – Y: 9 tracks"); everything else is printed with its own title.
    /// </summary>
    public static List<string> LoadActivity(Database db, long fromMs, long toMs, IReadOnlyList<Span>? spans = null)
    {
        using var cmd = db.Connection.CreateCommand();
        var builtin = string.Join(", ", EventSources.Builtin.Select(s => $"'{s}'"));
        cmd.CommandText = $"""
            SELECT ts, source, kind, title, data FROM events
            WHERE source NOT IN ({builtin}) AND ts >= $from AND ts < $to
            ORDER BY ts, id;
            """;
        cmd.Parameters.AddWithValue("$from", fromMs);
        cmd.Parameters.AddWithValue("$to", toMs);

        var events = new List<ActivityEvent>();
        using (var r = cmd.ExecuteReader())
        {
            while (r.Read())
            {
                JsonObject? data = null;
                if (!r.IsDBNull(4))
                {
                    try { data = JsonNode.Parse(r.GetString(4)) as JsonObject; }
                    catch (System.Text.Json.JsonException) { }
                }
                events.Add(new ActivityEvent(r.GetInt64(0), r.GetString(1), r.GetString(2),
                    r.IsDBNull(3) ? null : r.GetString(3), data));
            }
        }

        // T3 runs become one line per thread session; collected separately because runs in
        // parallel threads interleave with each other and with everything else.
        var lines = AgentSessions(events.Where(e => e.Source == EventSources.T3 && e.Kind == "run").ToList(), spans)
            .ToList();
        events = events.Where(e => !(e.Source == EventSources.T3 && e.Kind == "run")).ToList();

        for (var i = 0; i < events.Count; i++)
        {
            var e = events[i];
            var source = EventSources.DisplayName(e.Source);
            if (e.Kind != "listen")
            {
                lines.Add((e.Ts, $"{Time(e.Ts)}        {source,-15} {e.Title}"));
                continue;
            }

            // Collapse a run of listens from the same album.
            var album = Str(e.Data, "album");
            var artist = Str(e.Data, "artist");
            var j = i;
            while (j + 1 < events.Count && events[j + 1].Kind == "listen" && events[j + 1].Source == e.Source
                   && Str(events[j + 1].Data, "album") == album && Str(events[j + 1].Data, "artist") == artist)
                j++;

            var run = events.GetRange(i, j - i + 1);
            var end = run.Max(x => x.Data?["ended_ts"]?.GetValue<long>() ?? x.Ts);
            var plays = run.Count(x => x.Data?["play"]?.GetValue<bool>() == true);
            var what = run.Count == 1
                ? $"Listened to {e.Title}{(album is null ? "" : $" ({album})")}{(plays == 0 ? ", skipped" : "")}"
                : $"Listened to {artist} – {album}: {run.Count} tracks, {plays} counted as plays";
            lines.Add((e.Ts, $"{Time(e.Ts)}–{Time(end)}  {source,-15} {what}"));
            i = j;
        }
        return lines.OrderBy(l => l.Ts).Select(l => l.Line).ToList();
    }

    /// <summary>Runs of one thread more than this far apart are separate sessions.</summary>
    public const long AgentSessionGapMs = 30 * 60 * 1000;

    /// <summary>
    /// Groups T3 runs into sessions per thread: "watchme / Activity monitor: 7 prompts,
    /// Opus 5.5 (medium), agent 32m, you 12m". "agent" is the time models spent working;
    /// "you" is the time that thread's window was in front of you (its title is "project / thread").
    /// Subagent runs overlap their parent's and are left out.
    /// </summary>
    private static IEnumerable<(long Ts, string Line)> AgentSessions(List<ActivityEvent> runs, IReadOnlyList<Span>? spans)
    {
        var byThread = runs
            .Where(r => r.Data?["subagent"]?.GetValue<bool>() != true)
            .GroupBy(r => Str(r.Data, "thread_id") ?? r.Title ?? "");

        foreach (var thread in byThread)
        {
            var session = new List<ActivityEvent>();
            foreach (var run in thread.OrderBy(r => r.Ts))
            {
                if (session.Count > 0 && run.Ts - End(session[^1]) > AgentSessionGapMs)
                {
                    yield return SessionLine(session, spans);
                    session = [];
                }
                session.Add(run);
            }
            if (session.Count > 0)
                yield return SessionLine(session, spans);
        }

        static long End(ActivityEvent run) => run.Data?["completed_ts"]?.GetValue<long>() ?? run.Ts;
    }

    private static (long Ts, string Line) SessionLine(List<ActivityEvent> session, IReadOnlyList<Span>? spans)
    {
        var first = session[0];
        var start = first.Ts;
        var end = session.Max(r => r.Data?["completed_ts"]?.GetValue<long>() ?? r.Ts);
        var project = Str(first.Data, "project");
        var thread = Str(session[^1].Data, "thread");
        var where = project is null ? thread : $"{project} / {thread}";

        var busyMs = session.Sum(r => (r.Data?["duration_s"]?.GetValue<long>() ?? 0) * 1000);
        var models = session
            .Select(r => Str(r.Data, "model_label") + (Str(r.Data, "effort") is { } e ? $" ({e})" : ""))
            .Distinct()
            .ToList();
        var prompts = session.Count == 1 ? "1 prompt" : $"{session.Count} prompts";
        var failed = session.Count(r => Str(r.Data, "status") is not (null or "completed"));

        var line = $"{where}: {prompts}, {string.Join(" + ", models)}, agent {Duration(busyMs)}";
        if (spans is not null && where is not null)
        {
            // Time this thread's window was in front, from just before the first prompt until a
            // little after the last answer (reading the result counts too).
            var windowFrom = start - 5 * 60 * 1000;
            var windowTo = end + 10 * 60 * 1000;
            var youMs = spans
                .Where(s => s.Title == where && s.App.StartsWith("T3 Code", StringComparison.OrdinalIgnoreCase))
                .Sum(s => Math.Max(0, Math.Min(s.End, windowTo) - Math.Max(s.Start, windowFrom)));
            if (youMs > 0)
                line += $", you {Duration(youMs)}";
        }
        if (failed > 0)
            line += $" ({failed} not completed)";

        return (start, $"{Time(start)}–{Time(end)}  {EventSources.DisplayName(EventSources.T3),-15} {line}");
    }

    private static string? Str(JsonObject? obj, string key) => obj?[key] is JsonValue v ? v.ToString() : null;

    private static string Bar(long ms, long top) => new('█', (int)Math.Max(1, Math.Round(24.0 * ms / top)));

    private static List<(long Ts, string App)> LoadLaunches(Database db, long fromMs, long toMs, HashSet<string> usedApps)
    {
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = """
            SELECT ts, app FROM events
            WHERE source = 'process' AND kind = 'start' AND ts >= $from AND ts < $to AND app IS NOT NULL
            ORDER BY ts;
            """;
        cmd.Parameters.AddWithValue("$from", fromMs);
        cmd.Parameters.AddWithValue("$to", toMs);

        var result = new List<(long, string)>();
        var lastSeen = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var ts = r.GetInt64(0);
            var app = r.GetString(1);
            if (!usedApps.Contains(app))
                continue;
            // Browsers and Electron apps start a burst of helper processes; list the burst once.
            var burst = lastSeen.TryGetValue(app, out var prev) && ts - prev < 60_000;
            lastSeen[app] = ts;
            if (!burst)
                result.Add((ts, app));
        }
        return result;
    }

    private static string Time(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime.ToString("HH:mm");

    public static string Duration(long ms)
    {
        var t = TimeSpan.FromMilliseconds(ms);
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes:00}m" : $"{t.Minutes}m {t.Seconds:00}s";
    }

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
}
