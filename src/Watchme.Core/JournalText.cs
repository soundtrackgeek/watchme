using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Watchme.Core;

/// <summary>How Capsule journal entries are named in titles and reports.</summary>
public static partial class JournalText
{
    /// <summary>Markdown as one line of plain text: images dropped, links as their text, no markup.</summary>
    public static string Plain(string markdown)
    {
        var text = ImageRegex().Replace(markdown, " ");
        text = LinkRegex().Replace(text, "$1");
        text = MarkupRegex().Replace(text, "");
        return SpaceRegex().Replace(text, " ").Trim();
    }

    public static int Words(string markdown) => WordRegex().Count(Plain(markdown));

    /// <summary>The start of an entry as plain text, cut at a word: "So I'm a big fan of Soda Stream…".</summary>
    public static string Excerpt(string markdown, int max = 60)
    {
        var text = Plain(markdown);
        if (text.Length <= max)
            return text;
        var cut = text.LastIndexOf(' ', max - 1);
        return text[..(cut > max / 2 ? cut : max - 1)].TrimEnd(',', '.', ';', ':', ' ') + "…";
    }

    /// <summary>“Its title”, “The first words of it…”, "a hidden entry" or "an empty entry".</summary>
    public static string Label(string? title, string text, bool hidden) =>
        hidden ? "a hidden entry"
        : !string.IsNullOrWhiteSpace(title) ? $"“{title.Trim()}”"
        : Excerpt(text) is { Length: > 0 } x ? $"“{x}”"
        : "an empty entry";

    /// <summary><see cref="Label"/> from a Capsule event's data (title, excerpt, hidden).</summary>
    public static string Label(JsonObject? data) =>
        Label(data?["title"]?.ToString(), data?["excerpt"]?.ToString() ?? "", data?["hidden"]?.GetValue<bool>() == true);

    [GeneratedRegex(@"\S+")]
    private static partial Regex WordRegex();

    [GeneratedRegex(@"!\[[^\]]*\]\([^)]*\)")]
    private static partial Regex ImageRegex();

    [GeneratedRegex(@"\[([^\]]*)\]\([^)]*\)")]
    private static partial Regex LinkRegex();

    [GeneratedRegex(@"(?m)^\s{0,3}(?:#{1,6}|>|[-*+]|\d+\.)\s+|[*_`~]+")]
    private static partial Regex MarkupRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex SpaceRegex();
}
