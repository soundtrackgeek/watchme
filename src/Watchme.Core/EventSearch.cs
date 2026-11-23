using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace Watchme.Core;

/// <summary>
/// <c>watchme search</c>: finds events in the whole history whose title, app, kind or data
/// (URLs, commands, folders, artists, prompts) hold every search term, ignoring case and accents
/// (see <see cref="TextMatch"/>).
/// </summary>
public static class EventSearch
{
    /// <param name="Terms">Every term must match somewhere in the event; a term may hold spaces (a phrase).</param>
    /// <param name="Source">One source only; null searches all but process start and exit.</param>
    public sealed record Query(IReadOnlyList<string> Terms, long? FromMs = null, long? ToMs = null, string? Source = null, int Limit = 50);

    public sealed record Hit(long Id, long Ts, string Source, string Kind, string? App, string? Title, string? Data);

    public sealed record Result(List<Hit> Hits, int Total);

    public const int MaxLimit = 1000;

    /// <summary>A LIKE pattern for "contains <paramref name="term"/>", for use with <c>ESCAPE '\'</c>.</summary>
    public static string ContainsPattern(string term) =>
        "%" + term.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";

    public static Result Run(Database db, Query q)
    {
        // The same conditions for the count and the page of hits.
        SqliteCommand Command(string select, string tail)
        {
            var cmd = db.Connection.CreateCommand();
            var where = new List<string>();
            if (q.FromMs is { } from)
            {
                where.Add("ts >= $from");
                cmd.Parameters.AddWithValue("$from", from);
            }
            if (q.ToMs is { } to)
            {
                where.Add("ts < $to");
                cmd.Parameters.AddWithValue("$to", to);
            }
            if (string.IsNullOrEmpty(q.Source))
                where.Add("source <> 'process'");
            else
            {
                where.Add("source = $source");
                cmd.Parameters.AddWithValue("$source", q.Source);
            }
            TextMatch.AddTerms(cmd, where, q.Terms);
            cmd.CommandText = $"SELECT {select} FROM events WHERE {string.Join(" AND ", where)} {tail};";
            return cmd;
        }

        using var count = Command("COUNT(*)", "");
        var total = Convert.ToInt32(count.ExecuteScalar(), CultureInfo.InvariantCulture);

        // Newest by when it happened, not by id: scrapers import history late.
        using var cmd = Command("id, ts, source, kind, app, title, data", "ORDER BY ts DESC, id DESC LIMIT $limit");
        cmd.Parameters.AddWithValue("$limit", Math.Clamp(q.Limit, 1, MaxLimit));
        var hits = new List<Hit>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            hits.Add(new Hit(r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetString(3),
                r.IsDBNull(4) ? null : r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5),
                r.IsDBNull(6) ? null : r.GetString(6)));
        return new Result(hits, total);
    }

    /// <summary>
    /// The data field that explains a hit when its title does not: for a search for "dotnet",
    /// <c>cmd: dotnet build</c>. Null when the title (or app, or kind) already shows every term.
    /// </summary>
    public static (string Key, string Value)? MatchedField(Hit hit, IReadOnlyList<string> terms)
    {
        var shown = $"{hit.Title} {hit.App} {hit.Kind}";
        var missing = terms.Where(t => !string.IsNullOrWhiteSpace(t) && !TextMatch.Contains(shown, t)).ToList();
        if (missing.Count == 0 || hit.Data is null)
            return null;
        JsonObject? data;
        try { data = JsonNode.Parse(hit.Data) as JsonObject; }
        catch (System.Text.Json.JsonException) { return null; }
        if (data is null)
            return null;
        // Of the fields that match, the longest says the most: the url rather than the host,
        // the prompt rather than the project name.
        (string Key, string Value)? best = null;
        foreach (var (key, node) in data)
        {
            var value = node switch
            {
                null => null,
                JsonValue v => v.ToString(),
                _ => node.ToJsonString(),
            };
            if (value is not null && missing.Any(t => TextMatch.Contains(value, t)) && (best is null || value.Length > best.Value.Value.Length))
                best = (key, value);
        }
        return best;
    }

    /// <summary>
    /// Shortens <paramref name="value"/> to <paramref name="max"/> characters, keeping the first
    /// matched term in view: "…ateyourmusic.com/artist/tiesto…".
    /// </summary>
    public static string Snippet(string value, IReadOnlyList<string> terms, int max)
    {
        value = value.ReplaceLineEndings(" ");
        if (value.Length <= max)
            return value;
        var at = terms.SelectMany(t => TextMatch.Find(value, t).Take(1)).Select(m => m.Start).DefaultIfEmpty(0).Min();
        var start = Math.Clamp(at - max / 3, 0, value.Length - max);
        var cut = value.Substring(start, max);
        if (start > 0)
            cut = "…" + cut[1..];
        if (start + max < value.Length)
            cut = cut[..^1] + "…";
        return cut;
    }

    /// <summary>Wraps each match of the terms in an ANSI colour; plain text when <paramref name="color"/> is false.</summary>
    public static string Highlight(string text, IReadOnlyList<string> terms, bool color)
    {
        if (!color || text.Length == 0)
            return text;
        var marked = new bool[text.Length];
        foreach (var term in terms)
            foreach (var (start, length) in TextMatch.Find(text, term))
                for (var j = start; j < start + length; j++)
                    marked[j] = true;
        var sb = new StringBuilder();
        for (var i = 0; i < text.Length; i++)
        {
            if (marked[i] && (i == 0 || !marked[i - 1]))
                sb.Append("\e[1;33m");
            sb.Append(text[i]);
            if (marked[i] && (i == text.Length - 1 || !marked[i + 1]))
                sb.Append("\e[0m");
        }
        return sb.ToString();
    }

    public static void Print(Result result, IReadOnlyList<string> terms, TextWriter output, bool color)
    {
        var label = string.Join(" ", terms.Select(t => t.Contains(' ') ? $"\"{t}\"" : t));
        if (result.Total == 0)
        {
            output.WriteLine($"Nothing matches {label}.");
            return;
        }
        output.WriteLine(result.Total == 1 ? $"1 event matches {label}" : $"{result.Total:N0} events match {label}, newest first");

        string? lastDay = null;
        foreach (var hit in result.Hits)
        {
            var local = DateTimeOffset.FromUnixTimeMilliseconds(hit.Ts).LocalDateTime;
            var day = local.ToString("dddd d MMMM yyyy", CultureInfo.CurrentCulture);
            if (day != lastDay)
            {
                output.WriteLine();
                output.WriteLine(day);
                lastDay = day;
            }
            var text = hit.Title ?? hit.App ?? "";
            var line = $"  {local:HH:mm}  {Fit(EventSources.DisplayName(hit.Source), 15)}  {Fit(hit.Kind, 12)}  ";
            output.WriteLine(line + Highlight(Snippet(text, terms, 90), terms, color));
            if (MatchedField(hit, terms) is var (key, value))
                output.WriteLine(new string(' ', line.Length) + Highlight($"{key}: {Snippet(value, terms, 90 - key.Length - 2)}", terms, color));
        }

        if (result.Total > result.Hits.Count)
        {
            output.WriteLine();
            output.WriteLine($"Showing the newest {result.Hits.Count:N0} of {result.Total:N0}. Add words, --source or --days to narrow it down, or --limit N to see more.");
        }
    }

    private static string Fit(string s, int width) => s.Length > width ? s[..(width - 1)] + "…" : s.PadRight(width);
}
