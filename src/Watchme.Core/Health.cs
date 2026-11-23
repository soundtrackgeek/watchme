using System.Globalization;
using System.Text.Json;

namespace Watchme.Core;

/// <summary>
/// How a scraper's last runs went, kept in <c>meta</c> under <c>scraper:NAME</c> by the agent
/// after every run. A scraper that finds nothing to read says why in <see cref="Warning"/>, so
/// "Total Commander's log file is off" doesn't look the same as "nothing new".
/// </summary>
/// <param name="LastRun">When it last ran, successfully or not.</param>
/// <param name="LastOk">When it last ran without an error.</param>
/// <param name="LastEvents">When a run last produced new events.</param>
/// <param name="FailingSince">The first of the failures in a row that are still going on.</param>
public sealed record ScraperHealth(
    string Name, int IntervalS, long LastRun, long? LastOk, long? LastEvents, int Events,
    string? Warning, string? Error, long? FailingSince)
{
    public const string MetaPrefix = "scraper:";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The record after one more run, given the one before (null the first time).</summary>
    public static ScraperHealth After(ScraperHealth? before, string name, TimeSpan interval, long now, int events, string? warning, Exception? error) =>
        new(
            name,
            (int)interval.TotalSeconds,
            now,
            error is null ? now : before?.LastOk,
            events > 0 ? now : before?.LastEvents,
            events,
            warning,
            error is null ? null : $"{error.GetType().Name}: {error.Message}",
            error is null ? null : before?.FailingSince ?? now);

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static ScraperHealth? FromJson(string? json)
    {
        if (string.IsNullOrEmpty(json))
            return null;
        try { return JsonSerializer.Deserialize<ScraperHealth>(json, Json); }
        catch (JsonException) { return null; }
    }

    public static List<ScraperHealth> LoadAll(Database db)
    {
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = "SELECT value FROM meta WHERE key LIKE 'scraper:%' ORDER BY key;";
        var list = new List<ScraperHealth>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            if (FromJson(r.GetString(0)) is { } h)
                list.Add(h);
        return list;
    }
}

/// <summary>What <c>watchme status</c> and the dashboard say about the agent and each source.</summary>
public static class AgentHealth
{
    public enum Level { Ok, Warning, Failing, Stalled, Unknown }

    public sealed record SourceRow(
        string Source, string Name, long? LastRun, long? LastEvent, int Recent, Level Level, string? Message);

    public sealed record Report(
        bool Running, long? LastAlive, PauseState.Pause? Pause, List<SourceRow> Sources, int RecentDays)
    {
        public IEnumerable<SourceRow> Problems => Sources.Where(s => s.Level is Level.Warning or Level.Failing or Level.Stalled);
    }

    /// <summary>A scraper that hasn't run for this many of its intervals (plus a minute) is stuck.</summary>
    public const int StallIntervals = 3;

    public static Report Build(Database db, bool running, long now, string pauseFile, int recentDays = 7)
    {
        var health = ScraperHealth.LoadAll(db).ToDictionary(h => h.Name);
        var since = now - recentDays * 86_400_000L;

        var stats = new Dictionary<string, (long Last, int Recent)>();
        using (var cmd = db.Connection.CreateCommand())
        {
            // One indexed lookup per source rather than a scan of the whole table.
            cmd.CommandText = """
                SELECT s.source,
                       (SELECT MAX(ts) FROM events e WHERE e.source = s.source),
                       (SELECT COUNT(*) FROM events e WHERE e.source = s.source AND e.ts >= $since)
                FROM (SELECT DISTINCT source FROM events) s;
                """;
            cmd.Parameters.AddWithValue("$since", since);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                stats[r.GetString(0)] = (r.IsDBNull(1) ? 0 : r.GetInt64(1), r.GetInt32(2));
        }

        var rows = new List<SourceRow>();
        if (stats.TryGetValue(EventSources.Focus, out var focus))
            rows.Add(new SourceRow(EventSources.Focus, "Focus", null, focus.Last, focus.Recent, Level.Ok, null));
        foreach (var name in health.Keys.Union(stats.Keys.Where(s => !EventSources.Builtin.Contains(s))).Order(StringComparer.Ordinal))
        {
            var h = health.GetValueOrDefault(name);
            var s = stats.GetValueOrDefault(name);
            var (level, message) = Judge(h, running, now);
            rows.Add(new SourceRow(name, EventSources.DisplayName(name), h?.LastRun, s.Last == 0 ? null : s.Last, s.Recent, level, message));
        }

        long? lastAlive = long.TryParse(db.GetMeta("last_alive"), NumberStyles.None, CultureInfo.InvariantCulture, out var la) ? la : null;
        return new Report(running, lastAlive, PauseState.Active(pauseFile, now), rows, recentDays);
    }

    internal static (Level, string?) Judge(ScraperHealth? h, bool running, long now)
    {
        if (h is null)
            return (Level.Unknown, null);
        if (h.Error is not null)
            return (Level.Failing, $"failing since {When(h.FailingSince ?? h.LastRun, now)}: {h.Error}");
        if (running && now - h.LastRun > StallIntervals * h.IntervalS * 1000L + 60_000)
            return (Level.Stalled, $"has not run since {When(h.LastRun, now)}");
        if (h.Warning is not null)
            return (Level.Warning, h.Warning);
        return (Level.Ok, null);
    }

    /// <summary>"14:02" today, "yesterday 14:02", or "21 Nov 14:02".</summary>
    public static string When(long ms, long now)
    {
        var t = DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime;
        var today = DateTimeOffset.FromUnixTimeMilliseconds(now).LocalDateTime.Date;
        if (t.Date == today)
            return t.ToString("HH:mm", CultureInfo.InvariantCulture);
        if (t.Date == today.AddDays(-1))
            return "yesterday " + t.ToString("HH:mm", CultureInfo.InvariantCulture);
        return t.ToString(t.Year == today.Year ? "d MMM HH:mm" : "d MMM yyyy", CultureInfo.CurrentCulture);
    }

    /// <summary>"12 min", "1 h 05 min".</summary>
    public static string Left(long ms)
    {
        var m = (int)Math.Ceiling(ms / 60_000.0);
        return m < 60 ? $"{m} min" : $"{m / 60} h {m % 60:00} min";
    }
}
