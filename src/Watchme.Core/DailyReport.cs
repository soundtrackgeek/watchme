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
        foreach (var (app, ms) in byApp.Where(a => a.Ms >= 30_000).Take(20))
        {
            var bar = new string('█', (int)Math.Max(1, Math.Round(24.0 * ms / top)));
            output.WriteLine($"  {Trim(app, 28),-28} {Duration(ms),8}  {bar}");
        }
        output.WriteLine();

        output.WriteLine("Timeline (blocks of 1 minute or more)");
        foreach (var b in BuildBlocks(spans, MergeGapMs).Where(b => b.ActiveMs >= 60_000))
        {
            var title = b.MainTitle is null ? "" : $"  {Trim(b.MainTitle, 70)}";
            output.WriteLine($"  {Time(b.Start)}–{Time(b.End)} {Duration(b.ActiveMs),8}  {Trim(b.App, 24),-24}{title}");
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
