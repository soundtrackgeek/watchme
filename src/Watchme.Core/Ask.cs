using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using static Watchme.Core.QuestionParser;

namespace Watchme.Core;

/// <summary>
/// <c>watchme ask "question"</c>: answers a question read by <see cref="QuestionParser"/> from the
/// database, as a headline ("Yes: 23 listens matching “a-ha”…"), the way the question was
/// understood, and sections of rows. The same answer is printed by the CLI and drawn by the dashboard.
/// </summary>
public static class Ask
{
    /// <param name="When">"14:02" or "14:02–14:40"; with <paramref name="Ts"/>, a row can open its day.</param>
    /// <param name="Label">The app or source.</param>
    /// <param name="Detail">The data field that matched when the text doesn't show it: "cmd: dotnet build".</param>
    /// <param name="Value">A number for the row: "45m 02s", "12".</param>
    public sealed record Row(long? Ts, string? When, string? Label, string Text, string? Detail = null, string? Value = null, long? EventId = null);

    public sealed record Section(string Title, List<Row> Rows, string? Note = null);

    /// <param name="Mode">overview, events or duration.</param>
    /// <param name="Understood">How the question was read, so a wrong reading is easy to spot.</param>
    public sealed record Answer(string Question, string Mode, string Headline, string Understood,
        long? FromMs, long? ToMs, IReadOnlyList<string> Terms, List<Section> Sections, string? Hint);

    /// <summary>Matches listed under an answer; the rest are counted.</summary>
    public const int ShownHits = 30;

    /// <summary>Overviews longer than this show apps and days, not the timeline.</summary>
    private const long TimelineMaxMs = 2 * 24 * 3600_000L;

    public static Answer Run(Database db, string question, DateTime now) => Run(db, Parse(question, now), now);

    public static Answer Run(Database db, Question q, DateTime now) => q.Intent switch
    {
        Intent.Overview => Overview(db, q, now),
        Intent.HowLong => HowLong(db, q, now),
        _ => Events(db, q, now),
    };

    // ---------- "did I…", "how many…", "when…", "which…" ----------

    private static EventSearch.Query Query(Question q, int limit) =>
        new(q.Terms, q.FromMs, q.ToMs, null, limit, q.Topic?.Types, q.DailyMinutes, q.First);

    private static Answer Events(Database db, Question q, DateTime now)
    {
        // Enough matches to group them; the first ShownHits are listed.
        var result = EventSearch.Run(db, Query(q, EventSearch.MaxLimit));
        var total = result.Total;
        var days = Scalar(db, Query(q, 1), "COUNT(DISTINCT date(ts / 1000, 'unixepoch', 'localtime'))");
        var noun = total == 1 ? q.Topic?.Singular ?? "event" : q.Topic?.Plural ?? "events";
        var matching = Matching(q.Terms);
        var onDays = days > 1 ? $", on {days} different days" : "";
        var listened = q.Topic == ListenTopic && total > 0
            ? $" ({DailyReport.Duration((long)(SumDouble(db, Query(q, 1), "json_extract(data, '$.listened_s')") * 1000))} listened)"
            : "";
        var latest = total == 0 ? null : result.Hits[0];

        string headline;
        switch (q.Intent)
        {
            case Intent.Exists when total > 0:
                headline = $"Yes: {total:N0} {noun}{listened}{matching} {q.When}{onDays}. The last was {At(latest!.Ts)}: {Sentence(latest.Title)}";
                break;
            case Intent.When when total > 0:
                headline = $"{(q.First ? "First" : "Last")} {At(latest!.Ts)}: {Sentence(latest.Title)}" +
                           (total > 1 ? $" {total:N0} {noun}{matching} {q.When} in all." : "");
                break;
            case Intent.Count or Intent.List when total > 0:
                headline = $"{total:N0} {noun}{listened}{matching} {q.When}{onDays}.";
                break;
            default:
                headline = (q.Intent == Intent.Exists ? "No: no " : "No ") + $"{q.Topic?.Plural ?? "events"}{matching} {q.When}." + HistoryNote(db, q);
                break;
        }
        if (q.Intent == Intent.Exists && total > 0 && q.Topic?.Types is not { Count: > 0 } && q.Terms.Count > 0)
        {
            // "Did I use Photoshop?": the time it was in front says more than the number of events.
            var focus = FocusMatching(db, q, now);
            if (focus.Ms > 0)
                headline += $" In front for {DailyReport.Duration(focus.Ms)} in all.";
        }

        var sections = new List<Section>();
        if (total > 3 && GroupKey(q.Topic) is { } key)
        {
            var groups = result.Hits.Select(key).Where(k => !string.IsNullOrEmpty(k))
                .GroupBy(k => k!, StringComparer.OrdinalIgnoreCase)
                .Select(g => (Name: g.Key, N: g.Count()))
                .OrderByDescending(g => g.N).ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (groups.Count > 1)
                sections.Add(new Section("Most often", groups.Take(8).Select(g => new Row(null, null, null, g.Name, Value: $"{g.N:N0}")).ToList(),
                    result.Hits.Count < total ? $"Counted over the {(q.First ? "first" : "latest")} {result.Hits.Count:N0}." : null));
        }
        if (total > 0)
        {
            var rows = result.Hits.Take(ShownHits).Select(h => new Row(h.Ts, Hhmm(h.Ts), EventSources.DisplayName(h.Source), h.Title ?? h.App ?? h.Kind,
                EventSearch.MatchedField(h, q.Terms) is var (k, v) ? $"{k}: {EventSearch.Snippet(v, q.Terms, 100 - k.Length)}" : null,
                EventId: h.Id)).ToList();
            var shown = Math.Min(ShownHits, total);
            sections.Add(new Section(q.First ? "Oldest first" : total == 1 ? "Match" : "Newest first", rows,
                total > shown ? $"Showing {shown} of {total:N0}." : null));
        }

        string? hint = null;
        if (total > ShownHits && q.Terms.Count > 0)
            hint = "For the full list: watchme search " + string.Join(" ", q.Terms.Select(t => t.Contains(' ') ? $"\"{t}\"" : t)) + SearchRange(q) + " --limit 500";
        return new Answer(q.Text, "events", headline, Understood(q), q.FromMs, q.ToMs, q.Terms, sections, hint);
    }

    /// <summary>What a match is counted under in "Most often", per topic.</summary>
    private static Func<EventSearch.Hit, string?>? GroupKey(Topic? topic) => topic?.Name switch
    {
        "listen" => h => Data(h, "artist") is { } artist ? Data(h, "album") is { } album ? $"{artist} – {album}" : artist : h.Title,
        "visit" => h => Data(h, "host"),
        "search" => h => Data(h, "search") ?? Data(h, "query") ?? h.Title,
        "command" => h => DailyReport.ToolName(Data(h, "cmd")),
        "run" => h => Data(h, "project") is { } p ? Data(h, "thread") is { } t ? $"{p} / {t}" : p : null,
        "commit" or "push" or "git" => h => Data(h, "repo"),
        "rating" => h => Data(h, "artist") ?? Data(h, "album_artist"),
        "file_op" or "repo_init" or "clone" or "album_added" or "album_removed" or "tag_change" or "download" => null,
        _ => h => EventSources.DisplayName(h.Source),
    };

    // ---------- "how long…" ----------

    private static Answer HowLong(Database db, Question q, DateTime now)
    {
        if (q.Topic == ListenTopic)
        {
            var query = Query(q, 1);
            var n = Scalar(db, query, "COUNT(*)");
            var seconds = SumDouble(db, query, "json_extract(data, '$.listened_s')");
            var headline = n == 0
                ? $"No listens{Matching(q.Terms)} {q.When}." + HistoryNote(db, q)
                : $"{DailyReport.Duration((long)(seconds * 1000))} of listening{Matching(q.Terms)} {q.When}: {n:N0} {(n == 1 ? "track" : "tracks")}.";
            var answer = Events(db, q with { Intent = Intent.List }, now);
            return answer with { Mode = "duration", Headline = headline };
        }

        var (ms, byApp, byTitle) = FocusMatching(db, q, now);
        var what = q.Terms.Count == 0 ? "Active" : $"In front{Matching(q.Terms)}";
        var head = ms == 0
            ? $"No time in front{Matching(q.Terms)} {q.When}." + HistoryNote(db, q)
            : $"{what} for {DailyReport.Duration(ms)} {q.When}.";
        var sections = new List<Section>();
        if (byApp.Count > 0)
            sections.Add(new Section("Apps", byApp.Take(8).Select(a => new Row(null, null, null, AppName(a.Key), Value: DailyReport.Duration(a.Value))).ToList()));
        if (byTitle.Count > 1)
            sections.Add(new Section("Windows", byTitle.Take(10).Select(t => new Row(null, null, AppName(t.Key.App), t.Key.Title, Value: DailyReport.Duration(t.Value))).ToList()));
        return new Answer(q.Text, "duration", head, Understood(q, focus: true), q.FromMs, q.ToMs, q.Terms, sections, null);
    }

    /// <summary>Focus time in the question's window in windows whose app and title hold every term.</summary>
    private static (long Ms, List<KeyValuePair<string, long>> ByApp, List<KeyValuePair<(string App, string Title), long>> ByTitle) FocusMatching(Database db, Question q, DateTime now)
    {
        var to = q.ToMs ?? new DateTimeOffset(now).ToUnixTimeMilliseconds();
        var from = q.FromMs ?? FirstEventTs(db) ?? to;
        var byApp = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var byTitle = new Dictionary<(string, string), long>();
        long total = 0;
        foreach (var (wFrom, wTo) in Windows(from, to, q.DailyMinutes))
        {
            foreach (var s in DailyReport.LoadSpans(db, wFrom, wTo))
            {
                var text = $"{s.App} {s.Title}";
                if (!q.Terms.All(t => TextMatch.Contains(text, t)))
                    continue;
                total += s.DurationMs;
                byApp[s.App] = byApp.GetValueOrDefault(s.App) + s.DurationMs;
                if (!string.IsNullOrEmpty(s.Title))
                    byTitle[(s.App, s.Title)] = byTitle.GetValueOrDefault((s.App, s.Title)) + s.DurationMs;
            }
        }
        return (total, byApp.OrderByDescending(a => a.Value).ToList(), byTitle.OrderByDescending(t => t.Value).ToList());
    }

    // ---------- "what did I do…" ----------

    private static Answer Overview(Database db, Question q, DateTime now)
    {
        var nowMs = new DateTimeOffset(now).ToUnixTimeMilliseconds();
        var to = Math.Min(q.ToMs ?? nowMs, nowMs);
        var from = q.FromMs ?? Dashboard.DayStart(DateOnly.FromDateTime(now));
        var windows = Windows(from, to, q.DailyMinutes).ToList();

        var spans = windows.SelectMany(w => DailyReport.LoadSpans(db, w.From, w.To)).ToList();
        var activeMs = spans.Sum(s => s.DurationMs);
        var activity = new List<Row>();
        foreach (var (wFrom, wTo) in windows)
        {
            var dayStart = Dashboard.DayStart(DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeMilliseconds(wFrom).LocalDateTime));
            var windowSpans = spans.Where(s => s.Start < wTo && s.End > wFrom).ToList();
            activity.AddRange(DailyReport.LoadActivity(db, wFrom, wTo, windowSpans).Select(line => ActivityRow(line, dayStart)));
        }

        var sections = new List<Section>();
        var apps = spans.GroupBy(s => s.App).Select(g => (App: g.Key, Ms: g.Sum(s => s.DurationMs))).OrderByDescending(a => a.Ms).ToList();
        if (apps.Count > 0)
            sections.Add(new Section("Apps", apps.Take(8).Select(a => new Row(null, null, null, AppName(a.App), Value: DailyReport.Duration(a.Ms))).ToList(),
                apps.Count > 8 ? $"and {apps.Count - 8} more" : null));

        var span = to - from;
        if (span <= TimelineMaxMs && q.DailyMinutes is null)
        {
            var blocks = DailyReport.BuildBlocks(spans, DailyReport.MergeGapMs).Where(b => b.ActiveMs >= 60_000).ToList();
            if (blocks.Count > 0)
                sections.Add(new Section("Timeline", blocks.Select(b => new Row(b.Start, $"{Hhmm(b.Start)}–{Hhmm(b.End)}", AppName(b.App),
                    b.MainTitle ?? "", Value: DailyReport.Duration(b.ActiveMs))).ToList(), "blocks of a minute or more"));
        }
        else
        {
            var perDay = spans.GroupBy(s => DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeMilliseconds(s.Start).LocalDateTime))
                .OrderBy(g => g.Key)
                .Select(g => new Row(Dashboard.DayStart(g.Key), null, null, g.Key.ToString("dddd d MMMM", CultureInfo.InvariantCulture),
                    Value: DailyReport.Duration(g.Sum(s => s.DurationMs))))
                .ToList();
            if (perDay.Count > 0)
                sections.Add(new Section("Active per day", perDay));
        }
        if (activity.Count > 0)
        {
            const int max = 60;
            sections.Add(new Section("Activity in apps", activity.Take(max).ToList(), activity.Count > max ? $"Showing {max} of {activity.Count:N0}." : null));
        }

        var headline = activeMs == 0 && activity.Count == 0
            ? $"Nothing recorded {q.When}." + HistoryNote(db, q)
            : $"Active for {DailyReport.Duration(activeMs)} {q.When}" +
              (apps.Count > 0 ? $", mostly in {string.Join(", ", apps.Take(3).Select(a => AppName(a.App)))}" : "") +
              (activity.Count > 0 ? $"; {activity.Count:N0} {(activity.Count == 1 ? "thing" : "things")} done in apps." : ".");
        string? hint = null;
        if (span > TimelineMaxMs && q.DailyMinutes is null && q.FromMs is { } f && q.ToMs is { } t)
        {
            var a = DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeMilliseconds(f).LocalDateTime);
            var b = DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeMilliseconds(t - 1).LocalDateTime);
            hint = $"For the full report: watchme report {a:yyyy-MM-dd}..{b:yyyy-MM-dd}";
        }
        return new Answer(q.Text, "overview", headline, $"Summed up what was recorded {q.When}.", q.FromMs ?? from, q.ToMs ?? to, q.Terms, sections, hint);
    }

    /// <summary>An activity line of the report ("14:02–14:40  PowerShell      in C:\…") as a row.</summary>
    private static Row ActivityRow(string line, long dayStart)
    {
        var m = Regex.Match(line, @"^(\d\d):(\d\d)(?:–\d\d:\d\d)?");
        if (!m.Success)
            return new Row(null, null, null, line.Trim());
        var when = m.Value;
        var rest = line[m.Length..].TrimStart();
        var ts = dayStart + (int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) * 60 + int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture)) * 60_000L;
        // The source name is padded to 15 characters, then a space.
        if (rest.Length > 16 && rest[15] == ' ')
            return new Row(ts, when, rest[..15].TrimEnd(), rest[16..]);
        return new Row(ts, when, null, rest);
    }

    // ---------- helpers ----------

    /// <summary>The stretches of time a question covers: one, or one per day for a time of day across days.</summary>
    private static IEnumerable<(long From, long To)> Windows(long from, long to, (int From, int To)? daily)
    {
        if (daily is not var (a, b))
        {
            if (to > from)
                yield return (from, to);
            yield break;
        }
        var day = DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeMilliseconds(from).LocalDateTime);
        for (; Dashboard.DayStart(day) < to; day = day.AddDays(1))
        {
            var start = Math.Max(from, Dashboard.DayStart(day) + a * 60_000L);
            var end = Math.Min(to, Dashboard.DayStart(day) + b * 60_000L);
            if (end > start)
                yield return (start, end);
        }
    }

    private static string Understood(Question q, bool focus = false)
    {
        var what = focus
            ? q.Terms.Count == 0 ? "Added up the time any window was in front" : "Added up the time windows were in front whose app or title holds " + Quoted(q.Terms)
            : $"Looked for {q.Topic?.Plural ?? "events"} {q.Topic?.Where ?? "in every source but processes"}{Matching(q.Terms)}";
        return $"{what}, {q.When}.";
    }

    private static string Matching(IReadOnlyList<string> terms) => terms.Count == 0 ? "" : " matching " + Quoted(terms);

    private static string Quoted(IReadOnlyList<string> terms) => string.Join(" and ", terms.Select(t => $"“{t}”"));

    /// <summary>For an empty answer: " Watchme's history starts on 1 November 2026." when the question reaches back further.</summary>
    private static string HistoryNote(Database db, Question q)
    {
        if (FirstEventTs(db) is not { } first)
            return " Nothing has been recorded yet.";
        if (q.FromMs is { } from && from >= first)
            return "";
        var day = DateTimeOffset.FromUnixTimeMilliseconds(first).LocalDateTime;
        return $" Watchme's history starts on {day.ToString("d MMMM yyyy", CultureInfo.InvariantCulture)}.";
    }

    private static long? FirstEventTs(Database db) => db.Scalar("SELECT MIN(ts) FROM events;") is long ts ? ts : null;

    /// <summary>The <c>--from</c> and <c>--to</c> of the same window for <c>watchme search</c>.</summary>
    private static string SearchRange(Question q)
    {
        var s = "";
        if (q.FromMs is { } f)
            s += $" --from {DateTimeOffset.FromUnixTimeMilliseconds(f).LocalDateTime:yyyy-MM-dd}";
        if (q.ToMs is { } t)
            s += $" --to {DateTimeOffset.FromUnixTimeMilliseconds(t - 1).LocalDateTime:yyyy-MM-dd}";
        return s;
    }

    private static int Scalar(Database db, EventSearch.Query q, string select)
    {
        using var cmd = EventSearch.Command(db, q, select, "");
        return Convert.ToInt32(cmd.ExecuteScalar() ?? 0, CultureInfo.InvariantCulture);
    }

    private static double SumDouble(Database db, EventSearch.Query q, string expr)
    {
        using var cmd = EventSearch.Command(db, q, $"TOTAL({expr})", "");
        return Convert.ToDouble(cmd.ExecuteScalar() ?? 0.0, CultureInfo.InvariantCulture);
    }

    private static string? Data(EventSearch.Hit h, string key)
    {
        if (h.Data is null)
            return null;
        try
        {
            return JsonNode.Parse(h.Data)?[key] is JsonValue v && v.GetValueKind() == System.Text.Json.JsonValueKind.String ? v.GetValue<string>() : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>A title as the end of a sentence: with a full stop unless it already ends in one (or ? or !).</summary>
    private static string Sentence(string? title)
    {
        var t = (title ?? "").TrimEnd();
        return t.Length > 0 && ".?!…".Contains(t[^1]) ? t : t + ".";
    }

    private static string AppName(string app) => app.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? app[..^4] : app;

    private static string Hhmm(long ts) => DateTimeOffset.FromUnixTimeMilliseconds(ts).LocalDateTime.ToString("HH:mm", CultureInfo.InvariantCulture);

    /// <summary>"today at 14:02", "yesterday at 09:10", "on Friday 20 November at 21:14".</summary>
    private static string At(long ts)
    {
        var local = DateTimeOffset.FromUnixTimeMilliseconds(ts).LocalDateTime;
        var today = DateTime.Today;
        var day = local.Date == today ? "today"
            : local.Date == today.AddDays(-1) ? "yesterday"
            : "on " + local.ToString(local.Year == today.Year ? "dddd d MMMM" : "dddd d MMMM yyyy", CultureInfo.InvariantCulture);
        return $"{day} at {local:HH:mm}";
    }

    // ---------- printing ----------

    public static void Print(Answer answer, TextWriter output, bool color)
    {
        output.WriteLine(color ? $"\e[1m{answer.Headline}\e[0m" : answer.Headline);
        output.WriteLine(color ? $"\e[2m{answer.Understood}\e[0m" : answer.Understood);
        var multiDay = answer.FromMs is null || answer.ToMs is null || answer.ToMs - answer.FromMs > 24 * 3600_000L;
        foreach (var section in answer.Sections)
        {
            output.WriteLine();
            output.WriteLine(section.Title);
            var labelWidth = Math.Min(16, section.Rows.Select(r => r.Label?.Length ?? 0).DefaultIfEmpty(0).Max());
            var valueWidth = section.Rows.Select(r => r.Value?.Length ?? 0).DefaultIfEmpty(0).Max();
            string? lastDay = null;
            foreach (var row in section.Rows)
            {
                if (multiDay && row.EventId is not null && row.Ts is { } ts)
                {
                    var day = DateTimeOffset.FromUnixTimeMilliseconds(ts).LocalDateTime.ToString("ddd d MMM yyyy", CultureInfo.InvariantCulture);
                    if (day != lastDay)
                    {
                        output.WriteLine($"  {day}");
                        lastDay = day;
                    }
                }
                var line = "  ";
                if (row.When is not null)
                    line += row.When.PadRight(11) + "  ";
                if (row.Value is not null && row.When is null)
                    line += row.Value.PadLeft(valueWidth) + "  ";
                if (labelWidth > 0)
                    line += DailyReport.Trim(row.Label ?? "", labelWidth).PadRight(labelWidth) + "  ";
                var text = EventSearch.Highlight(EventSearch.Snippet(row.Text, answer.Terms, 100), answer.Terms, color);
                output.WriteLine(line + text + (row.Value is not null && row.When is not null ? $"  ({row.Value})" : ""));
                if (row.Detail is not null)
                    output.WriteLine(new string(' ', line.Length) + EventSearch.Highlight(row.Detail, answer.Terms, color));
            }
            if (section.Note is not null)
                output.WriteLine(color ? $"  \e[2m{section.Note}\e[0m" : $"  {section.Note}");
        }
        if (answer.Hint is not null)
        {
            output.WriteLine();
            output.WriteLine(answer.Hint);
        }
    }
}
