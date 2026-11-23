using System.Globalization;
using System.Text.Json.Nodes;

namespace Watchme.Core;

/// <summary>
/// Use of apps you control without switching to them, such as a music player driven by global
/// hotkeys. Focus time never sees it, so it is measured from what the apps log instead:
/// <list type="bullet">
/// <item>Playing — the time tracks actually played (each listen's <c>listened_s</c>)</item>
/// <item>Hands-on — your own actions (a skip, a rating or Love change) grouped into bursts:
/// actions less than <see cref="GapMs"/> apart are one burst, which lasts from its first action to
/// its last plus <see cref="ActionMs"/></item>
/// </list>
/// Both run alongside focus time in other apps, so they are never added to Active.
/// </summary>
public static class BackgroundUse
{
    /// <summary>Actions further apart than this start a new hands-on burst.</summary>
    public const long GapMs = 2 * 60 * 1000;

    /// <summary>Time credited for an action on its own, and added after the last action of a burst.</summary>
    public const long ActionMs = 10_000;

    /// <summary>One app's background use in a range.</summary>
    public sealed record AppUse(string App, long HandsOnMs, long PlayingMs, int Skips, int Ratings, int Bursts);

    /// <summary>
    /// A logged event that counts: a listen (<paramref name="ListenedS"/> of playing; a skip is an
    /// action at <paramref name="EndedTs"/>) or a rating change (an action at <paramref name="Ts"/>).
    /// </summary>
    internal sealed record Signal(string App, long Ts, bool IsRating, double ListenedS, bool Skipped, long? EndedTs);

    /// <summary>Background use per app for events from <paramref name="fromMs"/> to <paramref name="toMs"/>, most hands-on first.</summary>
    public static List<AppUse> Load(Database db, long fromMs, long toMs)
    {
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = """
            SELECT ts, kind, app, data FROM events
            WHERE ((source IN ('aurora', 'aurora_ng') AND kind = 'listen') OR (source = 'aurora_ng' AND kind = 'rating'))
              AND app IS NOT NULL AND ts >= $from AND ts < $to
            ORDER BY ts, id;
            """;
        cmd.Parameters.AddWithValue("$from", fromMs);
        cmd.Parameters.AddWithValue("$to", toMs);

        var signals = new List<Signal>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            JsonObject? data = null;
            if (!r.IsDBNull(3))
            {
                try { data = JsonNode.Parse(r.GetString(3)) as JsonObject; }
                catch (System.Text.Json.JsonException) { }
            }
            var isRating = r.GetString(1) == "rating";
            signals.Add(new Signal(
                r.GetString(2),
                r.GetInt64(0),
                isRating,
                isRating ? 0 : Num(data?["listened_s"]),
                !isRating && data?["outcome"] is JsonValue o && o.TryGetValue<string>(out var outcome) && outcome == "skipped",
                data?["ended_ts"] is JsonValue e && e.TryGetValue<long>(out var ended) ? ended : null));
        }
        return Compute(signals);
    }

    internal static List<AppUse> Compute(IEnumerable<Signal> signals) =>
        signals.GroupBy(s => s.App, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                // A skip happened when the track stopped; without an end time there is no moment to count.
                var actions = g.Where(s => s.IsRating).Select(s => s.Ts)
                    .Concat(g.Where(s => s.Skipped && s.EndedTs is not null).Select(s => s.EndedTs!.Value))
                    .Order()
                    .ToList();
                var (handsOn, bursts) = HandsOn(actions);
                return new AppUse(
                    g.Key,
                    handsOn,
                    (long)Math.Round(g.Sum(s => s.ListenedS) * 1000),
                    g.Count(s => s.Skipped),
                    g.Count(s => s.IsRating),
                    bursts);
            })
            .Where(u => u.HandsOnMs > 0 || u.PlayingMs > 0)
            .OrderByDescending(u => u.HandsOnMs).ThenByDescending(u => u.PlayingMs)
            .ToList();

    /// <summary>Total length of the bursts in sorted action times, and how many there were.</summary>
    internal static (long Ms, int Bursts) HandsOn(IReadOnlyList<long> sortedActions)
    {
        if (sortedActions.Count == 0)
            return (0, 0);
        long total = 0;
        var bursts = 0;
        var start = sortedActions[0];
        var last = start;
        foreach (var t in sortedActions.Skip(1))
        {
            if (t - last <= GapMs)
            {
                last = t;
                continue;
            }
            total += last - start + ActionMs;
            bursts++;
            start = last = t;
        }
        total += last - start + ActionMs;
        return (total, bursts + 1);
    }

    /// <summary>"hands-on 2h 12m · 884 skips · 144 ratings · 24h 15m playing", leaving out what is zero.</summary>
    public static string Describe(AppUse u)
    {
        var parts = new List<string>();
        if (u.HandsOnMs > 0)
            parts.Add($"hands-on {DailyReport.Duration(u.HandsOnMs)}");
        if (u.Skips > 0)
            parts.Add(Count(u.Skips, "skip"));
        if (u.Ratings > 0)
            parts.Add(Count(u.Ratings, "rating"));
        if (u.PlayingMs > 0)
            parts.Add($"{DailyReport.Duration(u.PlayingMs)} playing");
        return string.Join(" · ", parts);
    }

    private static string Count(int n, string one) => $"{n.ToString("N0", CultureInfo.CurrentCulture)} {one}{(n == 1 ? "" : "s")}";

    private static double Num(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<double>(out var d) ? d : 0;
}
