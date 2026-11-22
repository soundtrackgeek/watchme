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
        lines.AddRange(GitLines(events.Where(e => e.Source == EventSources.Git).ToList()));
        lines.AddRange(RatingLines(
            events.Where(e => e.Source == EventSources.MusicLibrary && e.Kind == "rating").ToList(),
            events.Where(e => e.Source == EventSources.AuroraNg && e.Kind == "rating").ToList()));
        events = events
            .Where(e => !(e.Source == EventSources.T3 && e.Kind == "run") && e.Source != EventSources.Git
                        && !(e.Source is EventSources.MusicLibrary or EventSources.AuroraNg && e.Kind == "rating"))
            .ToList();

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
            // Same album means the same folder, so "Milk Inc. feat. Silvy" stays on Milk Inc.'s album;
            // the artist is the fallback for listens without a path.
            var album = Str(e.Data, "album");
            var artist = Str(e.Data, "artist");
            static string? Folder(ActivityEvent x) => Str(x.Data, "path") is { } p ? Path.GetDirectoryName(p) : Str(x.Data, "artist");
            var folder = Folder(e);
            var j = i;
            while (j + 1 < events.Count && events[j + 1].Kind == "listen" && events[j + 1].Source == e.Source
                   && Str(events[j + 1].Data, "album") == album
                   && string.Equals(Folder(events[j + 1]), folder, StringComparison.OrdinalIgnoreCase))
                j++;

            var run = events.GetRange(i, j - i + 1);
            var end = run.Max(x => x.Data?["ended_ts"]?.GetValue<long>() ?? x.Ts);
            var plays = run.Count(x => x.Data?["play"]?.GetValue<bool>() == true);
            var what = run.Count == 1
                ? $"Listened to {e.Title}{(album is null ? "" : $" ({album})")}{(plays == 0 ? ", skipped" : "")}"
                : $"Listened to {run.GroupBy(x => Str(x.Data, "artist")).MaxBy(g => g.Count())!.Key} – {album}: {run.Count} tracks, {plays} counted as plays";
            lines.Add((e.Ts, $"{Time(e.Ts)}–{Time(end)}  {source,-15} {what}"));
            i = j;
        }
        return lines.OrderBy(l => l.Ts).Select(l => l.Line).ToList();
    }

    /// <summary>Rating changes on one album less than this far apart are one sitting.</summary>
    public const long RatingSittingGapMs = 30 * 60 * 1000;

    /// <summary>"4★", or "3.5★" for half stars set elsewhere; points are 0–100, 20 per star.</summary>
    public static string Stars(int points) =>
        $"{(points / 20.0).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)}★";

    /// <summary>
    /// Rating changes, one line per album and sitting. Aurora NG's log says which track got what:
    /// "Rated 12 tracks on Antiloop – LP: 5★ ×3, 4★ ×9; loved 1". Music Library's log only has the
    /// album's totals; they are added to Aurora NG's line for the same album, or shown on their own
    /// for ratings made in other apps: "Rated Milk Inc. – Closer (2003): 10 → 14 of 14 tracks rated".
    /// </summary>
    private static IEnumerable<(long Ts, string Line)> RatingLines(List<ActivityEvent> library, List<ActivityEvent> auroraNg)
    {
        var librarySittings = Sittings(library);
        var shown = new HashSet<List<ActivityEvent>>();

        foreach (var sitting in Sittings(auroraNg))
        {
            var (start, end) = (sitting[0].Ts, sitting[^1].Ts);
            var albumId = Str(sitting[0].Data, "album_id");
            // Music Library logs a change once it has read the MP3 again, a few seconds later.
            var totals = librarySittings
                .Where(s => !shown.Contains(s) && Str(s[0].Data, "album_id") == albumId
                            && s[0].Ts <= end + 10 * 60 * 1000 && s[^1].Ts >= start - 60 * 1000)
                .ToList();
            shown.UnionWith(totals);
            var summary = totals.Count == 0 ? "" : " · " + LibrarySummary(totals.SelectMany(s => s).OrderBy(e => e.Ts).ToList());
            yield return (start, AuroraNgRatingLine(sitting) + summary);
        }

        var source = EventSources.DisplayName(EventSources.MusicLibrary);
        foreach (var sitting in librarySittings.Where(s => !shown.Contains(s)))
        {
            var (first, last) = (sitting[0], sitting[^1]);
            var line = sitting.Count == 1
                ? $"{Time(first.Ts)}        {source,-15} {first.Title}"
                : $"{Time(first.Ts)}–{Time(last.Ts)}  {source,-15} Rated {LibraryAlbum(first)}: {LibrarySummary(sitting)}";
            yield return (first.Ts, line);
        }

        static List<List<ActivityEvent>> Sittings(IEnumerable<ActivityEvent> events)
        {
            var sittings = new List<List<ActivityEvent>>();
            foreach (var album in events.GroupBy(e => Str(e.Data, "album_id")))
            {
                var run = new List<ActivityEvent>();
                foreach (var e in album.OrderBy(e => e.Ts))
                {
                    if (run.Count > 0 && e.Ts - run[^1].Ts > RatingSittingGapMs)
                    {
                        sittings.Add(run);
                        run = [];
                    }
                    run.Add(e);
                }
                if (run.Count > 0)
                    sittings.Add(run);
            }
            return sittings;
        }
    }

    private static int? Int(ActivityEvent e, string key) => e.Data?[key]?.GetValue<int>();

    private static string LibraryAlbum(ActivityEvent e) =>
        $"{Str(e.Data, "artist")} – {Str(e.Data, "album")}" + (Int(e, "year") is { } y ? $" ({y})" : "");

    /// <summary>"10 → 14 of 14 tracks rated, album rating 92 → 93", over Music Library's events in order.</summary>
    private static string LibrarySummary(List<ActivityEvent> run)
    {
        var (first, last) = (run[0], run[^1]);
        var (before, after, total) = (Int(first, "rated_before") ?? 0, Int(last, "rated_after") ?? 0, Int(last, "tracks"));
        var counts = before == after ? $"{after}" : $"{before} → {after}";
        var text = $"{counts}{(total is null ? "" : $" of {total}")} tracks rated";
        return text + (Int(first, "album_rating_before"), Int(last, "album_rating_after")) switch
        {
            ({ } b, { } a) when b != a => $", album rating {b} → {a}",
            (_, { } a) => $", album rating {a}",
            _ => "",
        };
    }

    /// <summary>One album sitting from Aurora NG's rating log; a track rated twice counts with its last rating.</summary>
    private static string AuroraNgRatingLine(List<ActivityEvent> sitting)
    {
        var source = EventSources.DisplayName(EventSources.AuroraNg);
        var (first, last) = (sitting[0], sitting[^1]);
        if (sitting.Count == 1)
            return $"{Time(first.Ts)}        {source,-15} {first.Title}";

        var album = Str(first.Data, "album") ?? Path.GetFileName(Path.GetDirectoryName(Str(first.Data, "path")) ?? "?");
        var artist = Str(first.Data, "album_artist")
                     ?? sitting.GroupBy(e => Str(e.Data, "artist")).MaxBy(g => g.Count())!.Key;

        var byTrack = sitting.GroupBy(e => Str(e.Data, "path")).ToList();
        var rated = byTrack
            .Where(t => t.Any(e => Int(e, "rating_before") != Int(e, "rating_after")))
            .Select(t => Int(t.Last(), "rating_after"))
            .ToList();
        var stars = rated.OfType<int>()
            .GroupBy(p => p)
            .OrderByDescending(g => g.Key)
            .Select(g => $"{Stars(g.Key)} ×{g.Count()}")
            .ToList();
        var cleared = rated.Count(p => p is null);
        var loved = byTrack.Count(t => Str(t.First().Data, "love_before") != "loved" && Str(t.Last().Data, "love_after") == "loved");
        var unloved = byTrack.Count(t => Str(t.First().Data, "love_before") == "loved" && Str(t.Last().Data, "love_after") != "loved");

        var parts = new List<string>();
        if (stars.Count > 0)
            parts.Add(string.Join(", ", stars));
        if (cleared > 0)
            parts.Add($"cleared {cleared}");
        if (loved > 0)
            parts.Add($"loved {loved}");
        if (unloved > 0)
            parts.Add($"took Love from {unloved}");
        var withStars = rated.Count - cleared;
        var what = withStars > 0
            ? $"Rated {withStars} {(withStars == 1 ? "track" : "tracks")} on {artist} – {album}: {string.Join("; ", parts)}"
            : $"Changed ratings on {artist} – {album}: {string.Join("; ", parts)}";
        return $"{Time(first.Ts)}–{Time(last.Ts)}  {source,-15} {what}";
    }

    /// <summary>Commits to one branch more than this far apart get their own lines.</summary>
    public const long GitCommitGapMs = 30 * 60 * 1000;

    /// <summary>
    /// Git events as report lines. Commits to the same branch close together become one line
    /// ("Committed 4 times to watchme (master): msg; msg; ..."), and a new folder that is made a repo
    /// or cloned into within 10 minutes is shown as that one action.
    /// </summary>
    private static IEnumerable<(long Ts, string Line)> GitLines(List<ActivityEvent> git)
    {
        var source = EventSources.DisplayName(EventSources.Git);
        var foldersShown = new HashSet<ActivityEvent>();
        var initTitles = new Dictionary<ActivityEvent, string>();
        foreach (var e in git.Where(e => e.Kind is "repo_init" or "clone"))
        {
            var folder = git.FirstOrDefault(f => f.Kind == "folder_created" && !foldersShown.Contains(f)
                && string.Equals(Str(f.Data, "path"), Str(e.Data, "path"), StringComparison.OrdinalIgnoreCase)
                && e.Ts - f.Ts is >= -5000 and <= 10 * 60 * 1000); // reflog times are whole seconds
            if (folder is null)
                continue;
            foldersShown.Add(folder);
            if (e.Kind == "repo_init")
                initTitles[e] = $"{folder.Title} and started a git repo";
        }

        var runs = new List<List<ActivityEvent>>();
        var byBranch = git
            .Where(e => e.Kind == "commit")
            .GroupBy(e => (Str(e.Data, "path"), Str(e.Data, "worktree"), Str(e.Data, "branch")));
        foreach (var branch in byBranch)
        {
            foreach (var commit in branch.OrderBy(e => e.Ts))
            {
                if (runs.Count == 0 || !SameBranch(runs[^1][0], commit) || commit.Ts - runs[^1][^1].Ts > GitCommitGapMs)
                    runs.Add([]);
                runs[^1].Add(commit);
            }
        }

        // A push of that branch during or shortly after a run of commits is shown on the commit line.
        var pushed = new Dictionary<List<ActivityEvent>, string>();
        var pushesShown = new HashSet<ActivityEvent>();
        foreach (var run in runs)
        {
            var pushes = git.Where(p => p.Kind == "push" && !pushesShown.Contains(p) && SameBranch(run[0], p)
                && p.Ts >= run[0].Ts && p.Ts - run[^1].Ts <= GitCommitGapMs).ToList();
            if (pushes.Count == 0)
                continue;
            pushesShown.UnionWith(pushes);
            pushed[run] = $"pushed to {string.Join(", ", pushes.Select(p => Str(p.Data, "remote")).Distinct())}";
        }

        foreach (var e in git.Where(e => e.Kind != "commit" && !foldersShown.Contains(e) && !pushesShown.Contains(e)))
            yield return (e.Ts, $"{Time(e.Ts)}        {source,-15} {initTitles.GetValueOrDefault(e, e.Title ?? "")}");

        foreach (var run in runs)
        {
            var first = run[0];
            var push = pushed.GetValueOrDefault(run, "");
            if (run.Count == 1)
            {
                yield return (first.Ts, $"{Time(first.Ts)}        {source,-15} {first.Title}{(push == "" ? "" : " · " + push)}");
                continue;
            }
            var messages = string.Join("; ", run.Select(c => Str(c.Data, "message")));
            var what = $"Committed {run.Count} times to {Str(first.Data, "repo")} ({Str(first.Data, "branch")}){(push == "" ? "" : ", " + push)}: {Trim(messages, 160)}";
            yield return (first.Ts, $"{Time(first.Ts)}–{Time(run[^1].Ts)}  {source,-15} {what}");
        }

        // Pushes carry no worktree: the branch is what they share with commits.
        static bool SameBranch(ActivityEvent a, ActivityEvent b) =>
            string.Equals(Str(a.Data, "path"), Str(b.Data, "path"), StringComparison.OrdinalIgnoreCase)
            && Str(a.Data, "branch") == Str(b.Data, "branch")
            && (b.Kind == "push" || Str(a.Data, "worktree") == Str(b.Data, "worktree"));
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
