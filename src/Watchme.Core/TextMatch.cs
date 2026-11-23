using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace Watchme.Core;

/// <summary>
/// Matching search words against events, through the <c>events_fts</c> full-text index.
/// <para>
/// The index uses SQLite's trigram tokenizer: it keeps every three-character slice of an event's
/// text, so a term matches anywhere inside a word ("rate" finds rateyourmusic.com), the same as
/// <c>LIKE '%rate%'</c> did, but from the index instead of reading every row. It ignores case and
/// accents, so "tiesto" finds Tiësto and "reggaeton" finds Reggaetón. Terms shorter than three
/// characters have no trigram to look up and fall back to <c>LIKE</c> (without accent folding).
/// </para>
/// <para>
/// What is indexed: the title, app, kind, and every text value in the event's JSON data (the
/// values only, so "url" doesn't match every page). <see cref="Fold"/> folds text the same way in
/// C#, for highlighting matches and for checks the index can't make.
/// </para>
/// </summary>
public static class TextMatch
{
    /// <summary>Terms this long or longer are looked up in the index.</summary>
    public const int MinIndexedLength = 3;

    /// <summary>The SQL for the text the index holds for a row (<c>new</c> in a trigger, or a table alias).</summary>
    internal static string IndexedText(string row) => FoldStrokes($"""
        COALESCE({row}.title, '') || ' ' || COALESCE({row}.app, '') || ' ' || {row}.kind || ' ' ||
        COALESCE(CASE WHEN json_valid({row}.data)
                      THEN (SELECT group_concat(value, ' ') FROM json_tree({row}.data) WHERE type = 'text')
                      ELSE {row}.data END, '')
        """);

    /// <summary>
    /// Letters with a stroke have no decomposition, so SQLite's remove_diacritics leaves them alone;
    /// they are folded here instead, in the indexed text and in the terms, so "oya" finds Øya.
    /// </summary>
    private static readonly (char From, char To)[] Strokes =
        [('ø', 'o'), ('Ø', 'o'), ('đ', 'd'), ('Đ', 'd'), ('ł', 'l'), ('Ł', 'l'), ('ħ', 'h'), ('Ħ', 'h')];

    private static string FoldStrokes(string sql) =>
        Strokes.Aggregate(sql, (inner, s) => $"replace({inner}, '{s.From}', '{s.To}')");

    private static string FoldStrokesInTerm(string term) =>
        Strokes.Aggregate(term, (t, s) => t.Replace(s.From, s.To));

    /// <summary>
    /// Adds conditions for "every term matches" to <paramref name="where"/>. Long terms go through
    /// the index as one MATCH; short ones become LIKE conditions.
    /// </summary>
    /// <param name="id">The events id column in the query, such as <c>id</c> or <c>e.id</c>.</param>
    public static void AddTerms(SqliteCommand cmd, List<string> where, IEnumerable<string?> terms, string id = "id")
    {
        var list = terms.Select(t => t?.Trim()).Where(t => !string.IsNullOrEmpty(t)).Select(t => t!).Distinct().ToList();
        var indexed = list.Where(t => new StringInfo(t).LengthInTextElements >= MinIndexedLength).ToList();
        if (indexed.Count > 0)
        {
            where.Add($"{id} IN (SELECT rowid FROM events_fts WHERE events_fts MATCH $fts)");
            cmd.Parameters.AddWithValue("$fts", string.Join(" AND ", indexed.Select(t => Quote(FoldStrokesInTerm(t)))));
        }
        var short_ = list.Except(indexed).ToList();
        for (var i = 0; i < short_.Count; i++)
        {
            where.Add($"(title LIKE $short{i} ESCAPE '\\' OR app LIKE $short{i} ESCAPE '\\' OR kind LIKE $short{i} ESCAPE '\\' OR data LIKE $short{i} ESCAPE '\\')");
            cmd.Parameters.AddWithValue($"$short{i}", EventSearch.ContainsPattern(short_[i]));
        }
    }

    /// <summary>A term as an FTS5 string: in double quotes, so AND, OR, * or a colon in it are just text.</summary>
    internal static string Quote(string term) => "\"" + term.Replace("\"", "\"\"") + "\"";

    /// <summary>
    /// Lower case without accents, one character for one, so a position in the folded text is the
    /// same position in the original: "Tiësto" → "tiesto", "Øya" → "oya".
    /// </summary>
    public static string Fold(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
            sb.Append(FoldChar(c));
        return sb.ToString();
    }

    private static char FoldChar(char c)
    {
        if (c < 0x80)
            return c is >= 'A' and <= 'Z' ? (char)(c + 32) : c;
        // Letters with a stroke have no decomposition; the index folds them too (see Strokes).
        foreach (var (from, to) in Strokes)
            if (c == from)
                return to;
        var lower = char.ToLowerInvariant(c);
        var decomposed = lower.ToString().Normalize(NormalizationForm.FormD);
        return decomposed.Length > 0 && char.GetUnicodeCategory(decomposed[0]) != UnicodeCategory.NonSpacingMark
            ? decomposed[0]
            : lower;
    }

    /// <summary>Whether <paramref name="text"/> holds <paramref name="term"/>, ignoring case and accents.</summary>
    public static bool Contains(string? text, string term) =>
        text is not null && Fold(text).Contains(Fold(term.Trim()), StringComparison.Ordinal);

    /// <summary>Where <paramref name="term"/> occurs in <paramref name="text"/>, ignoring case and accents.</summary>
    public static IEnumerable<(int Start, int Length)> Find(string text, string term)
    {
        var needle = Fold(term.Trim());
        if (needle.Length == 0)
            yield break;
        var hay = Fold(text);
        var i = 0;
        while ((i = hay.IndexOf(needle, i, StringComparison.Ordinal)) >= 0)
        {
            yield return (i, needle.Length);
            i += needle.Length;
        }
    }

    /// <summary>Every text value in an event's JSON data, as the index sees it.</summary>
    public static IEnumerable<string> DataText(string? json)
    {
        if (string.IsNullOrEmpty(json))
            yield break;
        JsonNode? root;
        try { root = JsonNode.Parse(json); }
        catch (JsonException) { root = JsonValue.Create(json); }
        var stack = new Stack<JsonNode?>([root]);
        while (stack.Count > 0)
        {
            switch (stack.Pop())
            {
                case JsonObject o:
                    foreach (var (_, v) in o)
                        stack.Push(v);
                    break;
                case JsonArray a:
                    foreach (var v in a)
                        stack.Push(v);
                    break;
                case JsonValue v when v.GetValueKind() == JsonValueKind.String:
                    yield return v.GetValue<string>();
                    break;
            }
        }
    }
}
