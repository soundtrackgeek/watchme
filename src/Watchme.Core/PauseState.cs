using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Watchme.Core;

/// <summary>
/// <c>watchme pause</c>: nothing is recorded for a while, and it ends by itself.
/// <para>
/// A pause lives in two places. <c>pause.json</c> in the data folder is what the running agent
/// polls to stop tracking focus and processes. A <c>session/pause</c> event (with its end time in
/// <c>data.until</c>) marks the stretch in the database: it closes the current focus span, and
/// scrapers drop anything apps logged inside it, even history they import later. An early
/// <c>watchme resume</c>, a newer pause or the agent noticing the time is up ends it with
/// <c>session/resume</c>.
/// </para>
/// </summary>
public static partial class PauseState
{
    public sealed record Pause(long Since, long Until);

    /// <summary>The longest pause: long enough for a day out, short enough not to forget about it.</summary>
    public static readonly TimeSpan MaxDuration = TimeSpan.FromHours(24);

    public static readonly TimeSpan DefaultDuration = TimeSpan.FromMinutes(30);

    public static string FilePath => Path.Combine(Paths.DataDir, "pause.json");

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The pause in the file, ended or not; null when there is none or it can't be read.</summary>
    public static Pause? Read(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<Pause>(File.ReadAllText(path), Json) : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The pause in effect at <paramref name="now"/>, if any.</summary>
    public static Pause? Active(string path, long now) => Read(path) is { } p && p.Since <= now && now < p.Until ? p : null;

    /// <summary>Pauses until <paramref name="until"/>, or changes the end of the pause already running.</summary>
    public static Pause Begin(Database db, string path, long now, long until)
    {
        var since = Active(path, now)?.Since ?? now;
        var pause = new Pause(since, until);
        // The file first: the agent stops tracking within a second, and anything it records before
        // that is stamped before the pause event below.
        File.WriteAllText(path, JsonSerializer.Serialize(pause, Json));
        db.Insert([new WatchEvent(now, EventSources.Session, "pause") { Data = JsonSerializer.Serialize(new { until }) }]);
        return pause;
    }

    /// <summary>
    /// Ends the pause in the file: early (<c>watchme resume</c>) or because its time is up.
    /// Returns the pause that ended, or null when there was none.
    /// </summary>
    public static Pause? End(Database db, string path, long now)
    {
        var pause = Read(path);
        TryDelete(path);
        if (pause is null)
            return null;
        var early = now < pause.Until;
        // An ended pause is closed at its end time, even when the agent only notices later.
        var ts = early ? now : pause.Until;
        db.Insert([new WatchEvent(ts, EventSources.Session, "resume") { Data = JsonSerializer.Serialize(new { early }) }]);
        return pause;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Every paused stretch from the database: each pause runs to its <c>until</c>, or to the next
    /// pause or resume if that comes first.
    /// </summary>
    public static List<(long Start, long End)> Windows(Database db, long fromMs = 0, long toMs = long.MaxValue)
    {
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = """
            SELECT ts, kind, data FROM events
            WHERE source = 'session' AND kind IN ('pause', 'resume')
            ORDER BY ts, id;
            """;
        var marks = new List<(long Ts, string Kind, long? Until)>();
        using (var r = cmd.ExecuteReader())
        {
            while (r.Read())
            {
                long? until = null;
                if (!r.IsDBNull(2))
                {
                    try
                    {
                        if (JsonNode.Parse(r.GetString(2))?["until"] is JsonValue v && v.TryGetValue<long>(out var u))
                            until = u;
                    }
                    catch (JsonException) { }
                }
                marks.Add((r.GetInt64(0), r.GetString(1), until));
            }
        }

        var windows = new List<(long, long)>();
        for (var i = 0; i < marks.Count; i++)
        {
            if (marks[i].Kind != "pause" || marks[i].Until is not { } until)
                continue;
            var end = i + 1 < marks.Count ? Math.Min(until, marks[i + 1].Ts) : until;
            if (end > marks[i].Ts && end > fromMs && marks[i].Ts < toMs)
                windows.Add((marks[i].Ts, end));
        }
        return windows;
    }

    public static bool Covers(IReadOnlyList<(long Start, long End)> windows, long ts) =>
        windows.Any(w => ts >= w.Start && ts < w.End);

    /// <summary>
    /// How long to pause: "30m", "2h", "1h30m", "90" (minutes) or a clock time "14:30" (the next
    /// time it comes round). Null text means the default of 30 minutes.
    /// </summary>
    public static bool TryParseDuration(string? text, DateTime nowLocal, out TimeSpan duration, out string? error)
    {
        duration = DefaultDuration;
        error = null;
        var s = text?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(s))
            return true;

        if (s.StartsWith("until", StringComparison.Ordinal))
            s = s[5..].Trim();
        if (TimeOnly.TryParseExact(s, ["H:mm", "HH:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var clock))
        {
            var at = nowLocal.Date + clock.ToTimeSpan();
            if (at <= nowLocal)
                at = at.AddDays(1);
            duration = at - nowLocal;
        }
        else if (int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var minutes))
            duration = TimeSpan.FromMinutes(minutes);
        else if (DurationRegex().Match(s) is { Success: true } m && (m.Groups[1].Success || m.Groups[2].Success))
        {
            var h = m.Groups[1].Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
            var min = m.Groups[2].Success ? int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) : 0;
            duration = new TimeSpan(h, min, 0);
        }
        else
        {
            error = $"Expected a duration like 30m, 2h or 1h30m, or a time like 14:30; got '{text}'.";
            return false;
        }

        if (duration <= TimeSpan.Zero)
        {
            error = "The pause has to last at least a minute.";
            return false;
        }
        if (duration > MaxDuration)
        {
            error = $"A pause can last at most {MaxDuration.TotalHours:0} hours, so it can't be forgotten.";
            return false;
        }
        return true;
    }

    [GeneratedRegex(@"^(?:(\d+)\s*h)?\s*(?:(\d+)\s*m(?:in)?)?$")]
    private static partial Regex DurationRegex();
}
