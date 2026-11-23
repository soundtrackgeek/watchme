using System.Globalization;
using System.Text.RegularExpressions;

namespace Watchme.Core;

/// <summary>
/// Reads a question in plain English for <c>watchme ask</c>: when it is about ("yesterday between
/// 14 and 15", "in the last 2 weeks"), what kind of event ("listen" → plays in Aurora, "repos I
/// created" → new git repos), what shape of answer it wants (yes or no, how many, when, how long,
/// or a summary of what happened) and the words left over, which are searched for like
/// <c>watchme search</c> words. It runs locally; nothing is sent anywhere.
/// </summary>
public static partial class QuestionParser
{
    public enum Intent
    {
        /// <summary>"What did I do yesterday between 14 and 15?": apps, timeline and app activity.</summary>
        Overview,
        /// <summary>"Did I…?": yes or no, how often, and the matches.</summary>
        Exists,
        /// <summary>"How many…?", "How often…?"</summary>
        Count,
        /// <summary>"When did I last…?" (or first)</summary>
        When,
        /// <summary>"How long…?", "How much time…?"</summary>
        HowLong,
        /// <summary>"What…", "Which…", "Show…": the matches.</summary>
        List,
    }

    /// <summary>What a question is about, as event kinds, and how to name them in an answer.</summary>
    /// <param name="Singular">"listen", "new git repo"</param>
    /// <param name="Where">Where they come from, for the "understood" line: "in Aurora and Aurora NG".</param>
    public sealed record Topic(string Name, IReadOnlyList<EventSearch.EventType> Types, string Singular, string Plural, string Where);

    /// <param name="FromMs">Start of the time asked about, or null for all of history.</param>
    /// <param name="DailyMinutes">A time of day across several days, in minutes after midnight.</param>
    /// <param name="When">The time asked about in words: "yesterday 14:00–15:00", "the last 14 days".</param>
    /// <param name="First">"When did I first…": the oldest match, not the newest.</param>
    public sealed record Question(
        string Text, Intent Intent, IReadOnlyList<string> Terms, Topic? Topic,
        long? FromMs, long? ToMs, (int From, int To)? DailyMinutes, string When, bool First);

    // ---------- topics ----------

    private static EventSearch.EventType T(string source, string? kind = null, string? hasField = null) => new(source, kind, hasField);

    private static readonly EventSearch.EventType[] Listens = [T(EventSources.Aurora, "listen"), T(EventSources.AuroraNg, "listen")];

    public static readonly Topic ListenTopic = new("listen", Listens, "listen", "listens", "in Aurora and Aurora NG");

    private static readonly Topic RatingTopic = new("rating", [T(EventSources.AuroraNg, "rating"), T(EventSources.MusicLibrary, "rating")], "rating", "ratings", "in Aurora NG and Music Library");
    private static readonly Topic VisitTopic = new("visit", [T(EventSources.Edge, "visit")], "page visit", "page visits", "in Edge");
    private static readonly Topic FileTopic = new("file_op", [T(EventSources.TotalCommander, "file_op")], "file operation", "file operations", "in Total Commander");

    /// <summary>
    /// What questions can be about. Each has a pattern that must be found in the question; the
    /// words it matched are removed, so they aren't searched for as well. Of the patterns that
    /// match, the one earliest in the question wins (the list order breaks ties): the verb of a
    /// question comes before names in it, so "What did I <i>search</i> for on Rate Your Music?"
    /// is about searches, not ratings. Weak patterns are nouns that only say what a question is
    /// about when no verb does: "How many <i>tracks</i> did I <i>rate</i>?" is about ratings.
    /// </summary>
    private static readonly (Regex Pattern, Topic Topic, bool Weak)[] Topics =
    [
        (R(@"\b(?:(?:creat(?:e|ed|ing)|start(?:ed)?|init(?:ed|iali[sz]ed)?|made|make|new)\s+(?:(?:any|a|the|new|git|my)\s+)*(?:git\s+)?(?:repos?|repositor(?:y|ies))|(?:git\s+)?(?:repos?|repositor(?:y|ies))\s+(?:(?:did|have|has)\s+)?(?:i\s+)?(?:\w+\s+)?(?:creat(?:e|ed)|start(?:ed)?|init(?:ed|iali[sz]ed)?|made|make))\b"),
            new("repo_init", [T(EventSources.Git, "repo_init")], "new git repo", "new git repos", "in git"), false),
        (R(@"\b(?:git\s+)?(?:clone[sd]?|cloning)\b(?:\s+(?:any\s+)?(?:git\s+)?(?:repos?|repositor(?:y|ies)))?"),
            new("clone", [T(EventSources.Git, "clone")], "clone", "clones", "in git"), false),
        (R(@"\b(?:git\s+)?(?:commit(?:s|ted|ting)?)\b(?:\s+(?:to|in)\s+(?:git\s+)?)?"),
            new("commit", [T(EventSources.Git, "commit")], "commit", "commits", "in git"), false),
        (R(@"\b(?:git\s+)?push(?:es|ed|ing)?\b(?=.*\b(?:git|repos?|branch|origin|github)\b)|\bgit\s+push(?:es|ed)?\b"),
            new("push", [T(EventSources.Git, "push")], "push", "pushes", "in git"), false),
        (R(@"\b(?:git|repos?|repositor(?:y|ies)|branch(?:es)?)\b"),
            new("git", [T(EventSources.Git)], "git event", "git events", "in git"), true),

        (R(@"\b(?:albums?\s+(?:(?:did|have)\s+)?(?:i\s+)?(?:\w+\s+)?(?:add(?:ed)?|import(?:ed)?)|(?:add(?:ed)?|import(?:ed)?|new)\s+(?:(?:any|a|the|new)\s+)*albums?)\b(?:\s+to\s+(?:the\s+|my\s+)?library)?"),
            new("album_added", [T(EventSources.MusicLibrary, "album_added")], "album added", "albums added", "in Music Library"), false),
        (R(@"\b(?:albums?\s+(?:(?:did|have)\s+)?(?:i\s+)?(?:remove[ds]?|delete[ds]?)|(?:remove[ds]?|delete[ds]?)\s+(?:(?:any|a|the)\s+)*albums?)\b(?:\s+from\s+(?:the\s+|my\s+)?library)?"),
            new("album_removed", [T(EventSources.MusicLibrary, "album_removed")], "album removed", "albums removed", "in Music Library"), false),
        (R(@"\b(?:retag(?:ged|s)?|(?:chang(?:e|ed|ing)|edit(?:ed)?|fix(?:ed)?|set)\s+(?:(?:any|a|the|my)\s+)*(?:tags?|genres?)|(?:tags?|genres?)\s+(?:(?:did|have)\s+)?(?:i\s+)?(?:chang(?:e|ed)|edit(?:ed)?|fix(?:ed)?)|tag\s+(?:changes|edits))\b"),
            new("tag_change", [T(EventSources.Aurora, "tag_edit"), T(EventSources.MusicLibrary, "tag_change")], "tag change", "tag changes", "in Aurora and Music Library"), false),
        (R(@"\b(?:rat(?:e|ed|ing|ings)|lov(?:e|ed))\b"), RatingTopic, false),
        (R(@"\b(?:listen(?:s|ed|ing)?(?:\s+to)?|hear(?:d)?|play(?:s|ed)?)\b"), ListenTopic, false),

        (R(@"\b(?:download(?:s|ed|ing)?)\b"),
            new("download", [T(EventSources.Flipside, "download"), T(EventSources.Forever, "download")], "download", "downloads", "in Flipside and Forever"), false),
        (R(@"\b(?:search(?:es|ed|ing)?|googl(?:e|ed)|look(?:ed)?\s+up)\b(?:\s+for)?"),
            new("search", [T(EventSources.Edge, "visit", "search"), T(EventSources.Flipside, "search"), T(EventSources.Forever, "search")],
                "search", "searches", "in Edge, Flipside and Forever"), false),
        (R(@"\b(?:visit(?:s|ed)?|brows(?:e|ed|ing))\b"), VisitTopic, false),

        (R(@"\b(?:commands?|powershell|pwsh|terminal|shell|ran|typed)\b"),
            new("command", [T(EventSources.PowerShell, "command")], "command", "commands", "in PowerShell"), false),
        (R(@"\b(?:t3(?:\s+code)?|prompts?|claude|codex|agents?|ai|threads?)\b"),
            new("run", [T(EventSources.T3, "run")], "prompt", "prompts", "in T3 Code"), false),
        (R(@"\b(?:cop(?:y|ied)|mov(?:e|ed)|renam(?:e|ed)|delet(?:e|ed))\b"), FileTopic, false),

        (R(@"\b(?:songs?|tracks?|music)\b"), ListenTopic, true),
        (R(@"\bstars?\b"), RatingTopic, true),
        (R(@"\b(?:websites?|web\s*pages?|pages?|sites?|edge|online|the\s+web)\b"), VisitTopic, true),
        (R(@"\b(?:files?|total\s*commander)\b"), FileTopic, true),
    ];

    /// <summary>
    /// Words that only repeat the topic once it is known: "listen to <i>music</i>", "commands I
    /// <i>ran</i>", "<i>git</i> repos". They would otherwise be searched for and match nothing.
    /// </summary>
    private static readonly Dictionary<string, HashSet<string>> Fillers = new()
    {
        ["listen"] = Set("music", "song", "songs", "track", "tracks", "album", "albums", "aurora", "ng", "listen", "listened", "play", "played"),
        ["rating"] = Set("rate", "rated", "rating", "ratings", "star", "stars", "love", "loved", "track", "tracks", "song", "songs", "album", "albums"),
        ["album_added"] = Set("album", "albums", "library", "music"),
        ["album_removed"] = Set("album", "albums", "library", "music"),
        ["tag_change"] = Set("tag", "tags", "genre", "genres", "album", "albums"),
        ["command"] = Set("command", "commands", "run", "ran", "execute", "executed", "powershell", "pwsh", "terminal", "shell", "type", "typed"),
        ["repo_init"] = Set("git", "repo", "repos", "repository", "repositories"),
        ["clone"] = Set("git", "repo", "repos", "repository", "repositories"),
        ["commit"] = Set("git", "repo", "repos", "repository", "repositories"),
        ["push"] = Set("git", "repo", "repos", "repository", "repositories", "origin", "github"),
        ["git"] = Set("git", "repo", "repos", "repository", "repositories"),
        ["visit"] = Set("visit", "visited", "site", "sites", "website", "websites", "page", "pages", "edge", "web", "browse", "browsed", "online"),
        ["search"] = Set("search", "searched", "google", "googled", "web", "online", "edge"),
        ["run"] = Set("t3", "code", "claude", "codex", "prompt", "prompts", "ask", "asked", "agent", "agents", "thread", "threads", "ai"),
        ["file_op"] = Set("file", "files", "folder", "folders", "total", "commander"),
        ["download"] = Set("download", "downloads", "downloaded", "album", "albums"),
    };

    private static HashSet<string> Set(params string[] words) => new(words, StringComparer.OrdinalIgnoreCase);

    // ---------- words that carry no search meaning ----------

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "the", "i", "me", "my", "mine", "myself", "we", "us", "our", "you", "your", "it", "its",
        "this", "that", "these", "those", "there", "here", "what", "which", "who", "whom", "whose", "when",
        "where", "why", "how", "did", "do", "does", "done", "doing", "have", "has", "had", "having", "was",
        "were", "is", "are", "am", "be", "been", "being", "to", "of", "in", "on", "at", "by", "for", "with",
        "from", "into", "about", "between", "during", "and", "or", "but", "not", "any", "anything",
        "something", "some", "all", "ever", "every", "much", "many", "often", "long", "time", "times",
        "spend", "spent", "spending", "show", "list", "tell", "give", "please", "get", "got", "stuff",
        "things", "thing", "last", "first", "ago", "since", "yet", "still", "just", "also", "again", "can",
        "could", "would", "will", "should", "up", "out", "over", "go", "went", "use", "used", "using",
        "open", "opened", "work", "worked", "working", "happen", "happened", "else", "other", "anyone",
        "see", "seen", "look", "looked", "watch", "watched", "check", "checked", "very", "really", "lot",
        "lots", "most", "more", "less", "than", "so", "far", "least", "day", "days", "whole", "total",
        "overall", "summary", "summarize", "summarise", "kind", "kinds", "sort", "busy", "around",
        "active", "computer", "pc", "laptop", "machine", "screen", "awake",
    };

    // ---------- time ----------

    private const string Months = "january|february|march|april|may|june|july|august|september|october|november|december|jan|feb|mar|apr|jun|jul|aug|sept|sep|oct|nov|dec";
    private const string Weekdays = "monday|tuesday|wednesday|thursday|friday|saturday|sunday";
    private const string Numbers = @"\d+|a|an|one|two|three|four|five|six|seven|eight|nine|ten|eleven|twelve|couple(?:\s+of)?|few";

    /// <summary>One day in words: today, yesterday, a weekday, 2026-11-20, 20 November, November 20th.</summary>
    private static readonly string Day =
        $@"(?:the\s+day\s+before\s+yesterday|today|yesterday|(?:last\s+|this\s+|on\s+)?(?:{Weekdays})|\d{{4}}-\d{{2}}-\d{{2}}|\d{{1,2}}(?:st|nd|rd|th)?\s+(?:of\s+)?(?:{Months})(?:\s+\d{{4}})?|(?:{Months})\s+\d{{1,2}}(?:st|nd|rd|th)?(?:,?\s+\d{{4}})?)";

    /// <summary>A time of day: 14, 14:30, 14.30, 2pm, 2:30 pm.</summary>
    private const string Clock = @"(?:\d{1,2}(?:[:.]\d{2})?(?:\s*(?:am|pm))?(?:\s*o'?clock)?)";

    private static Regex R(string pattern) => new(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static Question Parse(string text, DateTime now)
    {
        var today = DateOnly.FromDateTime(now);
        var rest = " " + text.Trim() + " ";

        // Quoted phrases are search words as they are, never time or topic words.
        var terms = new List<string>();
        rest = Take(rest, R("[\"“”]([^\"“”]+)[\"“”]"), m => terms.Add(m.Groups[1].Value.Trim()));

        var lower = rest.ToLowerInvariant();
        var intent = Intent.List;
        var overview = false;
        var first = false;
        if (R(@"^\s*how\s+(?:long|much\s+time)\b").IsMatch(lower))
            intent = Intent.HowLong;
        else if (R(@"^\s*how\s+(?:many|often|much)\b").IsMatch(lower))
            intent = Intent.Count;
        else if (R(@"^\s*when\b").IsMatch(lower))
        {
            intent = Intent.When;
            first = R(@"\bfirst\b").IsMatch(lower);
            rest = Take(rest, R(@"\b(?:for\s+the\s+)?(?:first|last)(?:\s+time)?\b(?!\s+(?:\d|a\s|an\s|few|couple|week|month|year|day|hour|night))"), _ => { });
        }
        else if (R(@"^\s*(?:did|have|has|was|were|do|does|am|is|are)\b").IsMatch(lower))
            intent = Intent.Exists;
        if (R(@"^\s*(?:what|where)\s+(?:did|have|was)\s+i\s+(?:do|done|doing|been\s+doing|work(?:ing)?\s+on|up\s+to|been\s+up\s+to)\b|^\s*(?:summari[sz]e|summary|overview|recap|report)\b|\bwhat\s+happened\b").IsMatch(lower))
            overview = true;

        // ---- when ----
        DateOnly? dayFrom = null, dayTo = null;
        long? fromMs = null;
        (int From, int To)? minutes = null;
        string? rangeWords = null;

        void Days(DateOnly from, DateOnly to, string? words = null)
        {
            if (dayFrom is not null || fromMs is not null)
                return;
            (dayFrom, dayTo) = from <= to ? (from, to) : (to, from);
            rangeWords = words;
        }

        rest = Take(rest, R($@"\b(?:since|from)\s+({Day})(?:\s+(?:to|until|till|through)\s+({Day}))?\b"), m =>
        {
            if (ResolveDay(m.Groups[1].Value, today) is { } a)
                Days(a, m.Groups[2].Success && ResolveDay(m.Groups[2].Value, today) is { } b ? b : today);
        });
        rest = Take(rest, R($@"\bbetween\s+({Day})\s+and\s+({Day})\b"), m =>
        {
            if (ResolveDay(m.Groups[1].Value, today) is { } a && ResolveDay(m.Groups[2].Value, today) is { } b)
                Days(a, b);
        });
        rest = Take(rest, R(@"\blast\s+night\b|\btonight\b"), m =>
        {
            var day = m.Value.StartsWith("last", StringComparison.OrdinalIgnoreCase) ? today.AddDays(-1) : today;
            Days(day, day);
            minutes ??= (18 * 60, 24 * 60);
        });
        rest = Take(rest, R($@"\b(?:in\s+|during\s+|over\s+|within\s+|for\s+)?(?:(the)\s+)?(last|past|previous)\s+(?:({Numbers})\s+)?(minute|min|hour|day|week|month|year)s?\b"), m =>
        {
            var n = m.Groups[3].Success ? Number(m.Groups[3].Value) : 1;
            var unit = m.Groups[4].Value.ToLowerInvariant();
            // "last week" on its own is the calendar week before this one, as in `watchme report last-week`;
            // "the last week", "past week" and "last 2 weeks" count back from today.
            var calendar = !m.Groups[1].Success && !m.Groups[3].Success && unit is "week" or "month" or "year"
                           && m.Groups[2].Value.ToLowerInvariant() is "last" or "previous";
            if (calendar)
            {
                var (a, b) = unit switch
                {
                    "week" => (Monday(today).AddDays(-7), Monday(today).AddDays(-1)),
                    "month" => (new DateOnly(today.Year, today.Month, 1).AddMonths(-1), new DateOnly(today.Year, today.Month, 1).AddDays(-1)),
                    _ => (new DateOnly(today.Year - 1, 1, 1), new DateOnly(today.Year - 1, 12, 31)),
                };
                Days(a, b, $"last {unit}");
                return;
            }
            switch (unit)
            {
                case "minute" or "min" or "hour":
                    if (dayFrom is null && fromMs is null)
                    {
                        fromMs = new DateTimeOffset(now.AddMinutes(-(unit == "hour" ? 60 : 1) * n)).ToUnixTimeMilliseconds();
                        rangeWords = n == 1 ? $"the last {(unit == "hour" ? "hour" : "minute")}" : $"the last {n} {(unit == "hour" ? "hours" : "minutes")}";
                    }
                    break;
                case "day":
                    Days(today.AddDays(-(n - 1)), today, n == 1 ? "today" : $"the last {n} days");
                    break;
                case "week":
                    Days(today.AddDays(-(7 * n - 1)), today, n == 1 ? "the last 7 days" : $"the last {n} weeks");
                    break;
                case "month":
                    Days(today.AddMonths(-n).AddDays(1), today, n == 1 ? "the last month" : $"the last {n} months");
                    break;
                default:
                    Days(today.AddYears(-n).AddDays(1), today, n == 1 ? "the last year" : $"the last {n} years");
                    break;
            }
        });
        rest = Take(rest, R(@"\bthis\s+(week|month|year)\b"), m =>
        {
            var start = m.Groups[1].Value.ToLowerInvariant() switch
            {
                "week" => Monday(today),
                "month" => new DateOnly(today.Year, today.Month, 1),
                _ => new DateOnly(today.Year, 1, 1),
            };
            Days(start, today, $"this {m.Groups[1].Value.ToLowerInvariant()}");
        });
        rest = Take(rest, R($@"\b({Numbers})\s+(day|week|month|year)s?\s+ago\b"), m =>
        {
            var n = Number(m.Groups[1].Value);
            switch (m.Groups[2].Value.ToLowerInvariant())
            {
                case "day":
                    Days(today.AddDays(-n), today.AddDays(-n));
                    break;
                case "week":
                    var monday = Monday(today).AddDays(-7 * n);
                    Days(monday, monday.AddDays(6), n == 1 ? "the week before last" : null);
                    break;
                case "month":
                    var month = new DateOnly(today.Year, today.Month, 1).AddMonths(-n);
                    Days(month, month.AddMonths(1).AddDays(-1));
                    break;
                default:
                    Days(new DateOnly(today.Year - n, 1, 1), new DateOnly(today.Year - n, 12, 31));
                    break;
            }
        });
        rest = Take(rest, R($@"\b(?:in\s+|during\s+)({Months})(?:\s+(\d{{4}}))?\b|\b({Months})\s+(\d{{4}})\b"), m =>
        {
            var name = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[3].Value;
            var yearText = m.Groups[2].Success ? m.Groups[2].Value : m.Groups[4].Success ? m.Groups[4].Value : null;
            var month = MonthNumber(name);
            var year = yearText is null ? (month > today.Month ? today.Year - 1 : today.Year) : int.Parse(yearText, CultureInfo.InvariantCulture);
            var start = new DateOnly(year, month, 1);
            Days(start, start.AddMonths(1).AddDays(-1));
        });
        rest = Take(rest, R($@"\b(?:on\s+)?({Day})\b"), m =>
        {
            if (ResolveDay(m.Groups[1].Value, today) is { } d)
                Days(d, d);
        });

        // Parts of the day and clock times.
        rest = Take(rest, R(@"\b(?:in\s+the\s+|this\s+|during\s+the\s+)?(morning|afternoon|evening|night)s?\b"), m =>
        {
            if (m.Value.TrimStart().StartsWith("this", StringComparison.OrdinalIgnoreCase))
                Days(today, today);
            minutes ??= m.Groups[1].Value.ToLowerInvariant() switch
            {
                "morning" => (6 * 60, 12 * 60),
                "afternoon" => (12 * 60, 18 * 60),
                _ => (18 * 60, 24 * 60),
            };
        });
        rest = Take(rest, R($@"\b(?:between|from)\s+({Clock})\s*(?:and|to|until|till|-|–)\s*({Clock})(?![\w-])"), m =>
        {
            if (ClockRange(m.Groups[1].Value, m.Groups[2].Value) is { } r)
                minutes ??= r;
        });
        rest = Take(rest, R(@"(?<![\w-])(\d{1,2}(?:[:.]\d{2})?\s*(?:am|pm)?)\s*[-–]\s*(\d{1,2}(?:[:.]\d{2})?\s*(?:am|pm)?)(?![\w-])"), m =>
        {
            if (ClockRange(m.Groups[1].Value, m.Groups[2].Value) is { } r)
                minutes ??= r;
        });
        rest = Take(rest, R($@"\b(after|since|before|until|till|at|around|about)\s+({Clock})(?![\w-])(?!\s*(?:{Numbers}|minutes?|hours?|days?|weeks?|months?|years?|tracks?|songs?|times?)\b)"), m =>
        {
            if (ClockMinutes(m.Groups[2].Value) is not { } t)
                return;
            minutes ??= m.Groups[1].Value.ToLowerInvariant() switch
            {
                "after" or "since" => (t, 24 * 60),
                "before" or "until" or "till" => (0, t),
                "at" => (t, Math.Min(t + 60, 24 * 60)),
                _ => (Math.Max(t - 30, 0), Math.Min(t + 30, 24 * 60)),
            };
        });

        // Two or more capitalised words in a row are a name, searched for as one phrase and never
        // read as a topic: "Rate Your Music", "Total Commander", "Music Instructor".
        // The first word is left out: every question starts with a capital.
        var words = WordPattern().Matches(rest).ToList();
        var names = new List<(int Start, int End)>();
        for (var i = 1; i < words.Count; i++)
        {
            if (!IsCapital(words[i].Value))
                continue;
            var j = i;
            while (j + 1 < words.Count && IsCapital(words[j + 1].Value) && Gap(rest, words[j], words[j + 1]))
                j++;
            if (j > i)
                names.Add((words[i].Index, words[j].Index + words[j].Length));
            i = j;
        }
        terms.AddRange(names.Select(n => rest[n.Start..n.End].Trim('.', ',', ':', '\'')));
        foreach (var (start, end) in Enumerable.Reverse(names))
            rest = rest[..start] + Blank + rest[end..];

        // ---- what ----
        Topic? topic = null;
        Match? best = null;
        foreach (var weak in new[] { false, true })
        {
            foreach (var (pattern, candidate, isWeak) in Topics)
            {
                // "How long was I in T3 Code?" is about windows: only listening has a time of its own.
                if (isWeak != weak || intent == Intent.HowLong && candidate != ListenTopic)
                    continue;
                var found = pattern.Match(rest);
                if (found.Success && (best is null || found.Index < best.Index))
                    (best, topic) = (found, candidate);
            }
            if (best is not null)
                break;
        }
        if (best is { } m && topic is not null)
        {
            rest = rest.Remove(m.Index, m.Length).Insert(m.Index, Blank);
            // Total Commander titles start with what was done: "Copied 3 items from…".
            if (topic.Name == "file_op" && Regex.Match(m.Value, @"cop|mov|renam|delet", RegexOptions.IgnoreCase) is { Success: true } verb)
                terms.Add(verb.Value.ToLowerInvariant() switch { "cop" => "Copied", "mov" => "Moved", "renam" => "Renamed", _ => "Deleted" });
        }

        foreach (var word in WordPattern().Matches(rest).Select(m => m.Value.Trim('.', ',', ':', '\'', '-')))
        {
            var w = word.EndsWith("'s", StringComparison.OrdinalIgnoreCase) ? word[..^2] : word;
            if (w.Length > 0 && !StopWords.Contains(w) && !terms.Contains(w, StringComparer.OrdinalIgnoreCase)
                && !(topic is not null && Fillers.TryGetValue(topic.Name, out var fillers) && fillers.Contains(w)))
                terms.Add(w);
        }

        if (overview && topic is null && terms.Count == 0 || topic is null && terms.Count == 0 && intent != Intent.HowLong)
            intent = Intent.Overview;

        // ---- the time window ----
        long? from = null, to = null;
        (int, int)? daily = null;
        string when;
        if (fromMs is { } rolling)
        {
            from = rolling;
            to = null;
            when = "in " + rangeWords!;
        }
        else
        {
            if (dayFrom is null && (minutes is not null || intent is Intent.Overview))
                dayFrom = dayTo = today;
            if (dayFrom is { } df && dayTo is { } dt)
            {
                from = Dashboard.DayStart(df);
                to = Dashboard.DayStart(dt.AddDays(1));
                if (minutes is var (a, b))
                {
                    if (df == dt)
                    {
                        to = from + b * 60_000L;
                        from += a * 60_000L;
                    }
                    else
                        daily = (a, b);
                }
                when = DescribeDays(df, dt, today, rangeWords) + (minutes is var (ma, mb) ? $" {Hm(ma)}–{Hm(mb)}{(df == dt ? "" : " each day")}" : "");
            }
            else
                when = "in all of your history";
        }

        return new Question(text.Trim(), intent, terms, topic, from, to, daily, when, first);
    }

    [GeneratedRegex(@"[^\s?!;,()\[\]{}""“”]+")]
    private static partial Regex WordPattern();

    /// <summary>
    /// What a matched part of the question is replaced with. The comma keeps the words on either
    /// side apart, so they don't look like one name or one phrase afterwards.
    /// </summary>
    private const string Blank = " , ";

    /// <summary>Runs <paramref name="pattern"/> over <paramref name="s"/>, hands each match over and blanks it out.</summary>
    private static string Take(string s, Regex pattern, Action<Match> found) =>
        pattern.Replace(s, m =>
        {
            found(m);
            return Blank;
        });

    /// <summary>A word that starts with a capital letter, other than "I".</summary>
    private static bool IsCapital(string word) => word.Length > 0 && char.IsUpper(word[0]) && word != "I";

    /// <summary>Only spaces between two words.</summary>
    private static bool Gap(string s, Match a, Match b) => s[(a.Index + a.Length)..b.Index].All(char.IsWhiteSpace);

    internal static DateOnly? ResolveDay(string words, DateOnly today)
    {
        var s = Regex.Replace(words.Trim().ToLowerInvariant(), @"\s+", " ");
        if (s.StartsWith("on ", StringComparison.Ordinal))
            s = s[3..];
        switch (s)
        {
            case "today":
                return today;
            case "yesterday":
                return today.AddDays(-1);
            case "the day before yesterday":
                return today.AddDays(-2);
        }
        if (DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var iso))
            return iso;

        var wd = Regex.Match(s, $@"^(last |this )?({Weekdays})$");
        if (wd.Success)
        {
            // Weekdays starts at Monday; DayOfWeek starts at Sunday.
            var target = (DayOfWeek)((Array.IndexOf(Weekdays.Split('|'), wd.Groups[2].Value) + 1) % 7);
            var back = ((int)today.DayOfWeek - (int)target + 7) % 7;
            // "Monday" on a Monday is today; "last Monday" is the one before.
            if (back == 0 && wd.Groups[1].Value == "last ")
                back = 7;
            return today.AddDays(-back);
        }

        var dm = Regex.Match(s, $@"^(\d{{1,2}})(?:st|nd|rd|th)? (?:of )?({Months})(?: (\d{{4}}))?$");
        var md = Regex.Match(s, $@"^({Months}) (\d{{1,2}})(?:st|nd|rd|th)?(?:,? (\d{{4}}))?$");
        int day, month;
        string? year;
        if (dm.Success)
            (day, month, year) = (int.Parse(dm.Groups[1].Value, CultureInfo.InvariantCulture), MonthNumber(dm.Groups[2].Value), dm.Groups[3].Success ? dm.Groups[3].Value : null);
        else if (md.Success)
            (day, month, year) = (int.Parse(md.Groups[2].Value, CultureInfo.InvariantCulture), MonthNumber(md.Groups[1].Value), md.Groups[3].Success ? md.Groups[3].Value : null);
        else
            return null;
        var y = year is null ? today.Year : int.Parse(year, CultureInfo.InvariantCulture);
        if (day < 1 || day > DateTime.DaysInMonth(y, month))
            return null;
        var date = new DateOnly(y, month, day);
        // "20 December" asked in November is last year's.
        return year is null && date > today ? date.AddYears(-1) : date;
    }

    private static int MonthNumber(string name)
    {
        var n = name.ToLowerInvariant();
        string[] months = ["jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec"];
        return Array.FindIndex(months, m => n.StartsWith(m, StringComparison.Ordinal)) + 1;
    }

    private static int Number(string words) => words.Trim().ToLowerInvariant() switch
    {
        "a" or "an" or "one" => 1,
        "two" => 2,
        "three" or "few" => 3,
        "four" => 4,
        "five" => 5,
        "six" => 6,
        "seven" => 7,
        "eight" => 8,
        "nine" => 9,
        "ten" => 10,
        "eleven" => 11,
        "twelve" => 12,
        var s when s.StartsWith("couple", StringComparison.Ordinal) => 2,
        var s => Math.Clamp(int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 1, 1, 10_000),
    };

    /// <summary>A clock time as minutes after midnight: "14" → 840, "2:30pm" → 870; null if it isn't one.</summary>
    internal static int? ClockMinutes(string text)
    {
        var m = Regex.Match(text.Trim().ToLowerInvariant(), @"^(\d{1,2})(?:[:.](\d{2}))?\s*(am|pm)?(?:\s*o'?clock)?$");
        if (!m.Success)
            return null;
        var h = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        var min = m.Groups[2].Success ? int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) : 0;
        if (m.Groups[3].Success)
        {
            if (h is < 1 or > 12)
                return null;
            h = m.Groups[3].Value == "pm" ? h % 12 + 12 : h % 12;
        }
        if (h > 24 || min > 59 || h == 24 && min > 0)
            return null;
        return h * 60 + min;
    }

    /// <summary>"14" to "15", or "2" to "4pm" (the am/pm of the end carries over to the start).</summary>
    private static (int, int)? ClockRange(string a, string b)
    {
        var suffix = Regex.Match(b, @"(am|pm)\s*$", RegexOptions.IgnoreCase);
        if (suffix.Success && !Regex.IsMatch(a, "am|pm", RegexOptions.IgnoreCase))
        {
            var withSuffix = ClockMinutes(a + suffix.Value);
            var end = ClockMinutes(b);
            if (withSuffix is { } ws && end is { } e && ws < e)
                a += suffix.Value;
        }
        if (ClockMinutes(a) is not { } from || ClockMinutes(b) is not { } to || to <= from)
            return null;
        return (from, to);
    }

    /// <summary>
    /// The days asked about as words that fit in a sentence: "today", "on Friday 20 November",
    /// "in the last 14 days (10–23 November)", "last week (16–22 November)", "during 1–23 November".
    /// </summary>
    private static string DescribeDays(DateOnly from, DateOnly to, DateOnly today, string? words)
    {
        if (from == to)
        {
            // "this week" on a Monday is one day, but still what was asked.
            if (words is not null && words.StartsWith("this ", StringComparison.Ordinal))
                return words;
            if (from == today)
                return "today";
            if (from == today.AddDays(-1))
                return "yesterday";
            return "on " + from.ToString(from.Year == today.Year ? "dddd d MMMM" : "dddd d MMMM yyyy", CultureInfo.InvariantCulture);
        }
        var dates = Dates(from, to, today);
        return words is null ? "during " + dates
            : (words.StartsWith("the ", StringComparison.Ordinal) ? "in " : "") + $"{words} ({dates})";
    }

    private static string Dates(DateOnly from, DateOnly to, DateOnly today)
    {
        var sameYear = from.Year == to.Year;
        var left = from.ToString(sameYear && from.Month == to.Month ? "%d" : sameYear ? "d MMMM" : "d MMMM yyyy", CultureInfo.InvariantCulture);
        var right = to.ToString(to.Year == today.Year ? "d MMMM" : "d MMMM yyyy", CultureInfo.InvariantCulture);
        return $"{left}–{right}";
    }

    internal static string Hm(int minutes) => $"{minutes / 60:00}:{minutes % 60:00}";

    private static DateOnly Monday(DateOnly day) => day.AddDays(-(((int)day.DayOfWeek + 6) % 7));
}
