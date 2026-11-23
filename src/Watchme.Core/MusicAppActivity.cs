using System.Globalization;
using System.Text.Json.Nodes;

namespace Watchme.Core;

/// <summary>
/// Titles for the events read from Flipside's and Forever's activity logs, from the fields the
/// apps write: <c>service</c> (catalog, deezer, usenet, soulseek), <c>query</c>, <c>scope</c>,
/// <c>username</c>/<c>users</c>, <c>artist</c>/<c>album</c>/<c>year</c>, <c>title</c>, <c>files</c>,
/// <c>bytes</c>, <c>quality</c>, <c>indexer</c>, <c>origin</c>.
/// </summary>
public static class MusicAppActivity
{
    public static string Title(string kind, JsonObject d)
    {
        var service = Str(d, "service");
        var username = Str(d, "username");
        switch (kind)
        {
            case "search":
                var query = $"“{Str(d, "query")}”";
                return Str(d, "scope") == "user"
                    ? $"Searched {username}'s shares for {query}"
                    : $"Searched {ServiceName(service)} for {query}";
            case "browse":
                if (Bool(d, "ok") == false)
                    return $"Tried to browse {username}'s shares (no answer)";
                return Long(d, "files") is { } files
                    ? $"Browsed {username}'s shares ({files.ToString("N0", CultureInfo.InvariantCulture)} files)"
                    : $"Browsed {username}'s shares";
            case "wanted_add":
                return $"Added {Album(d)} to Wanted";
            case "download_queued":
                return $"Queued {What(d)}{From(d)}{Details(d)}{(Str(d, "origin") == "smart" ? " — Smart downloads" : "")}";
            case "download":
                return $"Downloaded {What(d)}{From(d)}{Details(d)}";
            default:
                return Str(d, "title") ?? kind;
        }
    }

    /// <summary>"Deezer", "Usenet", "Soulseek", "the catalog".</summary>
    public static string ServiceName(string? service) => service switch
    {
        "catalog" => "the catalog",
        "deezer" => "Deezer",
        "usenet" => "Usenet",
        "soulseek" => "Soulseek",
        null or "" => "?",
        _ => char.ToUpperInvariant(service[0]) + service[1..],
    };

    /// <summary>"Artist – Album (2004)" when the album is known, else the release or file title.</summary>
    public static string What(JsonObject d) =>
        Str(d, "artist") is { Length: > 0 } && Str(d, "album") is { Length: > 0 } ? Album(d) : Str(d, "title") ?? "?";

    public static string Album(JsonObject d)
    {
        var year = Long(d, "year") is { } y ? $" ({y})" : "";
        return $"{Str(d, "artist")} – {Str(d, "album") ?? Str(d, "title")}{year}";
    }

    /// <summary>" from alice" (Soulseek, naming everyone who sent files), " from Deezer", " from Usenet".</summary>
    public static string From(JsonObject d)
    {
        if (Str(d, "service") != "soulseek")
            return $" from {ServiceName(Str(d, "service"))}";
        var users = d["users"] is JsonArray a ? a.Select(u => u?.ToString()).OfType<string>().ToList() : [];
        if (users.Count == 0 && Str(d, "username") is { } u)
            users.Add(u);
        return users.Count == 0 ? "" : $" from {string.Join(", ", users)}";
    }

    public static string Details(JsonObject d)
    {
        var parts = new List<string>();
        if (Str(d, "quality") is { } quality)
            parts.Add(quality);
        if (Str(d, "indexer") is { } indexer)
            parts.Add(indexer);
        if (Long(d, "files") is { } files && files > 1)
            parts.Add($"{files} {(Str(d, "service") == "deezer" ? "tracks" : "files")}");
        if (Long(d, "bytes") is { } bytes && bytes > 0)
            parts.Add(DailyReport.Size(bytes));
        return parts.Count == 0 ? "" : $" ({string.Join(", ", parts)})";
    }

    private static long? Long(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue<long>(out var l) ? l : null;

    private static bool? Bool(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;

    private static string? Str(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
