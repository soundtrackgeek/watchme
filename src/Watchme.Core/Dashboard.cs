using System.Globalization;
using System.Text.Json.Nodes;

namespace Watchme.Core;

/// <summary>
/// Turns the events of a date range into what the web dashboard draws: totals, time per day and
/// app, an hour-of-day grid, and per-app summaries (music, web, code, shell, files).
/// Everything is computed from raw events, the same way the daily report does.
/// </summary>
public static class Dashboard
{
    /// <summary>Apps beyond this many are summed as "Other" in the per-day chart.</summary>
    public const int TopApps = 7;

    public sealed record Totals(
        long ActiveMs, int AppsUsed, int Listens, double ListenedS, int Pages, int Commits, int Pushes,
        int Prompts, double AgentS, int Commands, int FailedCommands, int FileOps, int Ratings,
        int AlbumsAdded, int AlbumsRemoved, int TagChanges, int Downloads, int MusicSearches);

    public sealed record Day(string Date, long ActiveMs, Dictionary<string, long> Apps, Dictionary<string, int> Events);

    public sealed record AppRow(string App, long Ms, int Blocks, string? TopTitle);

    public sealed record Count(string Name, int N, double Value = 0);

    public sealed record Search(long Ts, string Site, string Query);

    public sealed record Music(int Listens, int Plays, int Skips, double ListenedS,
        List<Count> Artists, List<Count> Albums, List<Count> Genres, int[] Stars, int Loved, int Unloved);

    public sealed record Web(int Pages, List<Count> Sites, List<Search> Searches);

    public sealed record Code(List<Count> Repos, List<Count> Projects, List<Count> Models);

    public sealed record Shell(int Commands, int Failed, List<Count> Programs, List<Count> Folders);

    public sealed record Data(
        string From, string To, string[] Sources, Totals Totals, List<Day> Days, List<AppRow> Apps,
        double[][] FocusByHour, int[][] EventsByHour, Dictionary<string, int[]> SourceHours, Music Music, Web Web, Code Code, Shell Shell,
        List<Count> FileOps);

    public sealed record TimelineBlock(long Start, long End, string App, long ActiveMs, string? Title);

    public sealed record DayDetail(string Date, long ActiveMs, List<TimelineBlock> Blocks, List<long[]> Idle, List<string> Activity);

    public sealed record EventRow(long Id, long Ts, string Source, string Kind, string? App, string? Title);

    private sealed record Ev(long Ts, string Source, string Kind, string? App, string? Title, JsonObject? Data);

    public static long DayStart(DateOnly day) =>
        new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Local)).ToUnixTimeMilliseconds();

    private static DateTime Local(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime;

    /// <summary>The dashboard for the days <paramref name="from"/> to <paramref name="to"/>, both included.</summary>
    public static Data Build(Database db, DateOnly from, DateOnly to)
    {
        if (to < from)
            (from, to) = (to, from);
        var dayCount = to.DayNumber - from.DayNumber + 1;
        var fromMs = DayStart(from);
        var toMs = DayStart(to.AddDays(1));

        // Focus time, split at midnight so each day gets its own share.
        var spans = new List<Span>();
        var days = new List<Day>();
        var focusByHour = new double[dayCount][];
        for (var i = 0; i < dayCount; i++)
        {
            var d = from.AddDays(i);
            var daySpans = DailyReport.LoadSpans(db, DayStart(d), DayStart(d.AddDays(1)));
            spans.AddRange(daySpans);
            focusByHour[i] = new double[24];
            foreach (var s in daySpans)
                AddToHours(focusByHour[i], s.Start, s.End);
            var apps = daySpans.GroupBy(s => s.App, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Sum(s => s.DurationMs));
            days.Add(new Day(d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), daySpans.Sum(s => s.DurationMs), apps, []));
        }

        var blocks = DailyReport.BuildBlocks(spans, DailyReport.MergeGapMs);
        var appRows = spans.GroupBy(s => s.App, StringComparer.OrdinalIgnoreCase)
            .Select(g => new AppRow(
                g.Key,
                g.Sum(s => s.DurationMs),
                blocks.Count(b => string.Equals(b.App, g.Key, StringComparison.OrdinalIgnoreCase) && b.ActiveMs >= 60_000),
                g.Where(s => !string.IsNullOrEmpty(s.Title)).GroupBy(s => s.Title)
                    .MaxBy(t => t.Sum(s => s.DurationMs))?.Key))
            .OrderByDescending(a => a.Ms)
            .ToList();

        // Fold the per-day app maps to the top apps of the whole range plus "Other".
        var top = appRows.Take(TopApps).Select(a => a.App).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < days.Count; i++)
        {
            var folded = new Dictionary<string, long>();
            foreach (var (app, ms) in days[i].Apps)
            {
                var key = top.Contains(app) ? app : "Other";
                folded[key] = folded.GetValueOrDefault(key) + ms;
            }
            days[i] = days[i] with { Apps = folded };
        }

        var events = LoadScraped(db, fromMs, toMs);
        var eventsByHour = new int[dayCount][];
        var sourceHours = new Dictionary<string, int[]>();
        for (var i = 0; i < dayCount; i++)
            eventsByHour[i] = new int[24];
        foreach (var e in events)
        {
            var local = Local(e.Ts);
            var index = DateOnly.FromDateTime(local).DayNumber - from.DayNumber;
            if (index < 0 || index >= dayCount)
                continue;
            eventsByHour[index][local.Hour]++;
            if (!sourceHours.TryGetValue(e.Source, out var hours))
                sourceHours[e.Source] = hours = new int[24];
            hours[local.Hour]++;
            var counts = days[index].Events;
            counts[e.Source] = counts.GetValueOrDefault(e.Source) + 1;
        }

        var music = BuildMusic(events);
        var web = BuildWeb(events);
        var code = BuildCode(events);
        var shell = BuildShell(events);
        var fileOps = events.Where(e => e.Source == EventSources.TotalCommander && e.Kind == "file_op")
            .SelectMany(e => e.Data?["ops"] as JsonObject ?? [])
            .GroupBy(op => OpName(op.Key))
            .Select(g => new Count(g.Key, g.Sum(op => (int)Num(op.Value))))
            .OrderByDescending(c => c.N)
            .ToList();

        int Kind(string source, string kind) => events.Count(e => e.Source == source && e.Kind == kind);
        var runs = TopLevelRuns(events);
        var totals = new Totals(
            ActiveMs: spans.Sum(s => s.DurationMs),
            AppsUsed: appRows.Count,
            Listens: music.Listens,
            ListenedS: music.ListenedS,
            Pages: web.Pages,
            Commits: Kind(EventSources.Git, "commit"),
            Pushes: Kind(EventSources.Git, "push"),
            Prompts: runs.Count,
            AgentS: runs.Sum(e => Num(e.Data?["duration_s"])),
            Commands: shell.Commands,
            FailedCommands: shell.Failed,
            FileOps: Kind(EventSources.TotalCommander, "file_op"),
            Ratings: Kind(EventSources.AuroraNg, "rating") + Kind(EventSources.MusicLibrary, "rating"),
            AlbumsAdded: Kind(EventSources.MusicLibrary, "album_added"),
            AlbumsRemoved: Kind(EventSources.MusicLibrary, "album_removed"),
            TagChanges: Kind(EventSources.MusicLibrary, "tag_change"),
            Downloads: Kind(EventSources.Flipside, "download") + Kind(EventSources.Forever, "download"),
            MusicSearches: Kind(EventSources.Flipside, "search") + Kind(EventSources.Forever, "search"));

        return new Data(
            from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            events.Select(e => e.Source).Distinct().Order(StringComparer.Ordinal).ToArray(),
            totals, days, appRows, focusByHour, eventsByHour, sourceHours, music, web, code, shell, fileOps);
    }

    /// <summary>One day's timeline: focus blocks, idle stretches and the report's activity lines.</summary>
    public static DayDetail BuildDay(Database db, DateOnly day)
    {
        var from = DayStart(day);
        var to = DayStart(day.AddDays(1));
        var spans = DailyReport.LoadSpans(db, from, to);
        var blocks = DailyReport.BuildBlocks(spans, DailyReport.MergeGapMs)
            .Select(b => new TimelineBlock(b.Start, b.End, b.App, b.ActiveMs, b.MainTitle))
            .ToList();

        var idle = new List<long[]>();
        using (var cmd = db.Connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT ts, kind FROM events
                WHERE source = 'idle' AND ts >= $from AND ts < $to
                ORDER BY ts, id;
                """;
            cmd.Parameters.AddWithValue("$from", from);
            cmd.Parameters.AddWithValue("$to", to);
            using var r = cmd.ExecuteReader();
            long? start = null;
            while (r.Read())
            {
                if (r.GetString(1) == "idle_start")
                    start = r.GetInt64(0);
                else if (start is { } s)
                {
                    idle.Add([s, r.GetInt64(0)]);
                    start = null;
                }
            }
            if (start is { } open)
                idle.Add([open, Math.Min(to, Clock.NowMs())]);
        }

        return new DayDetail(day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), spans.Sum(s => s.DurationMs),
            blocks, idle, DailyReport.LoadActivity(db, from, to, spans));
    }

    /// <summary>
    /// Raw events, newest first, for the event log table. <paramref name="source"/> "all" means
    /// every source but process start and exit, which would drown everything else.
    /// </summary>
    public static List<EventRow> Events(Database db, long fromMs, long toMs, string? source, string? query, long? beforeId, int limit)
    {
        using var cmd = db.Connection.CreateCommand();
        var where = new List<string> { "ts >= $from", "ts < $to" };
        if (string.IsNullOrEmpty(source) || source == "all")
            where.Add("source <> 'process'");
        else
        {
            where.Add("source = $source");
            cmd.Parameters.AddWithValue("$source", source);
        }
        if (!string.IsNullOrWhiteSpace(query))
        {
            where.Add("(title LIKE $q ESCAPE '\\' OR app LIKE $q ESCAPE '\\' OR kind LIKE $q ESCAPE '\\')");
            var escaped = query.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
            cmd.Parameters.AddWithValue("$q", $"%{escaped}%");
        }
        if (beforeId is { } id)
        {
            where.Add("id < $before");
            cmd.Parameters.AddWithValue("$before", id);
        }
        // Ordered by id, not ts: scrapers insert history late, but paging needs a stable key.
        cmd.CommandText = $"""
            SELECT id, ts, source, kind, app, title FROM events
            WHERE {string.Join(" AND ", where)}
            ORDER BY id DESC LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$from", fromMs);
        cmd.Parameters.AddWithValue("$to", toMs);
        cmd.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 500));

        var rows = new List<EventRow>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            rows.Add(new EventRow(r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetString(3),
                r.IsDBNull(4) ? null : r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5)));
        return rows;
    }

    /// <summary>Adds the minutes of [start, end) to the local-time hour buckets they fall in.</summary>
    internal static void AddToHours(double[] hours, long start, long end)
    {
        var t = start;
        while (t < end)
        {
            var local = Local(t);
            var nextHour = new DateTimeOffset(local.Date.AddHours(local.Hour + 1), TimeZoneInfo.Local.GetUtcOffset(local.Date.AddHours(local.Hour + 1)))
                .ToUnixTimeMilliseconds();
            if (nextHour <= t)
                nextHour = t + 3_600_000; // DST edge: never loop on the same instant.
            var stop = Math.Min(end, nextHour);
            hours[local.Hour] += (stop - t) / 60_000.0;
            t = stop;
        }
    }

    private static List<Ev> LoadScraped(Database db, long fromMs, long toMs)
    {
        using var cmd = db.Connection.CreateCommand();
        var builtin = string.Join(", ", EventSources.Builtin.Select(s => $"'{s}'"));
        cmd.CommandText = $"""
            SELECT ts, source, kind, app, title, data FROM events
            WHERE source NOT IN ({builtin}) AND ts >= $from AND ts < $to
            ORDER BY ts, id;
            """;
        cmd.Parameters.AddWithValue("$from", fromMs);
        cmd.Parameters.AddWithValue("$to", toMs);
        var events = new List<Ev>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            JsonObject? data = null;
            if (!r.IsDBNull(5))
            {
                try { data = JsonNode.Parse(r.GetString(5)) as JsonObject; }
                catch (System.Text.Json.JsonException) { }
            }
            events.Add(new Ev(r.GetInt64(0), r.GetString(1), r.GetString(2),
                r.IsDBNull(3) ? null : r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4), data));
        }
        return events;
    }

    private static Music BuildMusic(List<Ev> events)
    {
        var listens = events.Where(e => e.Source is EventSources.Aurora or EventSources.AuroraNg && e.Kind == "listen").ToList();
        static string Artist(Ev e) => Str(e.Data, "artist") ?? "?";

        var artists = listens.GroupBy(Artist, StringComparer.OrdinalIgnoreCase)
            .Select(g => new Count(g.Key, g.Count(), g.Sum(e => Num(e.Data?["listened_s"]))))
            .OrderByDescending(c => c.Value).Take(15).ToList();
        var albums = listens.GroupBy(e => $"{Artist(e)} – {Str(e.Data, "album") ?? "?"}", StringComparer.OrdinalIgnoreCase)
            .Select(g => new Count(g.Key, g.Count(), g.Sum(e => Num(e.Data?["listened_s"]))))
            .OrderByDescending(c => c.Value).Take(15).ToList();
        var genres = listens.GroupBy(e => Str(e.Data, "genre") ?? "Unknown", StringComparer.OrdinalIgnoreCase)
            .Select(g => new Count(g.Key, g.Count(), g.Sum(e => Num(e.Data?["listened_s"]))))
            .OrderByDescending(c => c.Value).Take(12).ToList();

        // Ratings are stored 0–100; the histogram has a bucket per half star from 0.5 to 5.
        // A Love change carries the track's rating unchanged, so only changed ratings count.
        var stars = new int[10];
        int loved = 0, unloved = 0;
        foreach (var e in events.Where(e => e.Source == EventSources.AuroraNg && e.Kind == "rating"))
        {
            var (loveBefore, loveAfter) = (Str(e.Data, "love_before"), Str(e.Data, "love_after"));
            if (loveBefore != loveAfter)
            {
                if (loveAfter == "loved") loved++;
                else if (loveBefore == "loved") unloved++;
            }

            var rating = Num(e.Data?["rating_after"]);
            if (rating <= 0 || rating == Num(e.Data?["rating_before"]))
                continue;
            var bucket = (int)Math.Round(rating / 10.0) - 1;
            stars[Math.Clamp(bucket, 0, 9)]++;
        }

        return new Music(
            listens.Count,
            listens.Count(e => e.Data?["play"] is JsonValue p && p.TryGetValue<bool>(out var b) && b),
            listens.Count(e => Str(e.Data, "outcome") == "skipped"),
            listens.Sum(e => Num(e.Data?["listened_s"])),
            artists, albums, genres, stars, loved, unloved);
    }

    private static Web BuildWeb(List<Ev> events)
    {
        var visits = events.Where(e => e.Source == EventSources.Edge && e.Kind == "visit").ToList();
        static string Host(Ev e)
        {
            var host = Str(e.Data, "host") ?? "?";
            return host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? host[4..] : host;
        }

        var byHost = visits.GroupBy(Host, StringComparer.OrdinalIgnoreCase).ToList();
        var names = byHost.ToDictionary(g => g.Key, g => DailyReport.SiteName(g.Key, g.Select(v => v.Title)), StringComparer.OrdinalIgnoreCase);
        var sites = byHost
            .Select(g => new Count(names[g.Key], g.Count()))
            .GroupBy(c => c.Name)
            .Select(g => new Count(g.Key, g.Sum(c => c.N)))
            .OrderByDescending(c => c.N).Take(15).ToList();
        var searches = visits
            .Where(v => !string.IsNullOrWhiteSpace(Str(v.Data, "search")))
            .Select(v => new Search(v.Ts, names[Host(v)], Str(v.Data, "search")!))
            .DistinctBy(s => (s.Site, s.Query.ToLowerInvariant()))
            .OrderByDescending(s => s.Ts).Take(40).ToList();
        return new Web(visits.Count, sites, searches);
    }

    private static List<Ev> TopLevelRuns(List<Ev> events) =>
        events.Where(e => e.Source == EventSources.T3 && e.Kind == "run"
                          && !(e.Data?["subagent"] is JsonValue s && s.TryGetValue<bool>(out var sub) && sub))
            .ToList();

    private static Code BuildCode(List<Ev> events)
    {
        var repos = events.Where(e => e.Source == EventSources.Git && e.Kind == "commit")
            .GroupBy(e => Str(e.Data, "repo") ?? "?", StringComparer.OrdinalIgnoreCase)
            .Select(g => new Count(g.Key, g.Count()))
            .OrderByDescending(c => c.N).Take(15).ToList();
        var runs = TopLevelRuns(events);
        var projects = runs.GroupBy(e => Str(e.Data, "project") ?? "?", StringComparer.OrdinalIgnoreCase)
            .Select(g => new Count(g.Key, g.Count(), g.Sum(e => Num(e.Data?["duration_s"]))))
            .OrderByDescending(c => c.Value).Take(15).ToList();
        var models = runs.GroupBy(e => Str(e.Data, "model_label") ?? Str(e.Data, "model") ?? "?", StringComparer.OrdinalIgnoreCase)
            .Select(g => new Count(g.Key, g.Count(), g.Sum(e => Num(e.Data?["duration_s"]))))
            .OrderByDescending(c => c.N).ToList();
        return new Code(repos, projects, models);
    }

    private static Shell BuildShell(List<Ev> events)
    {
        var commands = events.Where(e => e.Source == EventSources.PowerShell && e.Kind == "command").ToList();
        var programs = commands
            .Select(e => Program(Str(e.Data, "cmd") ?? e.Title))
            .Where(p => p is not null)
            .GroupBy(p => p!, StringComparer.OrdinalIgnoreCase)
            .Select(g => new Count(g.Key, g.Count()))
            .OrderByDescending(c => c.N).Take(12).ToList();
        var folders = commands
            .GroupBy(e => Str(e.Data, "cwd") ?? "?", StringComparer.OrdinalIgnoreCase)
            .Select(g => new Count(g.Key, g.Count()))
            .OrderByDescending(c => c.N).Take(10).ToList();
        var failed = commands.Count(e => e.Data?["ok"] is JsonValue ok && ok.TryGetValue<bool>(out var b) && !b);
        return new Shell(commands.Count, failed, programs, folders);
    }

    /// <summary>The program a command line runs: "git" for "git push", "watchme" for ".\watchme.exe report".</summary>
    internal static string? Program(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return null;
        if (command == "[hidden]")
            return "[hidden]";
        var rest = command.TrimStart('&', ' ', '\t');
        string? first;
        if (rest.Length > 0 && rest[0] is '"' or '\'')
        {
            // A quoted path may hold spaces: & "C:\Program Files\dotnet\dotnet.exe" build
            var close = rest.IndexOf(rest[0], 1);
            first = close > 0 ? rest[1..close] : rest[1..];
        }
        else
            first = rest.Split([' ', '\t', '|', ';'], 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(first))
            return null;
        var name = Path.GetFileName(first.Replace('/', '\\'));
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            name = name[..^4];
        return name.Length == 0 ? null : name.ToLowerInvariant();
    }

    private static string OpName(string op) => op switch
    {
        "Copy" or "CopyFolder" => "Copied",
        "Move" or "MoveFolder" => "Moved",
        "Delete" or "DeleteFolder" => "Deleted",
        "Rename" => "Renamed",
        "NewFolder" or "MkDir" or "CreateFolder" => "Created folders",
        "Pack" => "Packed",
        "Unpack" => "Unpacked",
        _ => op,
    };

    private static string? Str(JsonObject? obj, string key) =>
        obj?[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static double Num(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<double>(out var d) ? d : 0;
}
