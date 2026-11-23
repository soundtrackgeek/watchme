using System.Globalization;
using Watchme.Core;

namespace Watchme.Cli;

/// <summary>
/// <c>watchme forget [WHEN] [--match WORDS] [--source S] [--day DAY] [--days N] [--from D] [--to D] [--redact] [--yes]</c>:
/// takes events out after the fact. See <see cref="Forgetting"/>.
/// </summary>
internal static class ForgetCommand
{
    private const string Usage = """
        Usage: watchme forget WHEN [--source S] [--redact] [--yes]
               watchme forget [WHEN] --match WORDS [--source S] [--days N | --from DAY --to DAY] [--redact] [--yes]

          WHEN       15m, 2h, 1h30m (up to now) or 14:10..14:35 (today; --day DAY for another day)
          --match    only events holding these words (title, page, command, folder, artist...)
          --redact   keep the events and their time, without titles and text
          --yes      don't ask first
        """;

    private const int Examples = 8;

    public static int Run(string[] args)
    {
        string? when = null;
        var terms = new List<string>();
        string? source = null;
        DateOnly? day = null, fromDay = null, toDay = null;
        var redact = false;
        var yes = false;
        var today = DateOnly.FromDateTime(DateTime.Now);

        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            string? Next() => i + 1 < args.Length ? args[++i] : null;
            switch (a.ToLowerInvariant())
            {
                case "--match" or "-m":
                    if (Next() is not { Length: > 0 } term || string.IsNullOrWhiteSpace(term))
                        return Fail("--match needs words, like --match \"my bank\".");
                    terms.Add(term);
                    break;
                case "--source" or "-s":
                    var name = Next();
                    source = name is null ? null : SearchCommand.ResolveSource(name);
                    if (source is null)
                        return Fail($"Unknown source '{name}'.");
                    if (source is EventSources.Session or EventSources.Agent or EventSources.Idle)
                        return Fail($"'{source}' events hold nothing to forget.");
                    break;
                case "--day":
                    if (!ReportRange.TryDay(Next(), today, out var d))
                        return Fail("--day needs a day: YYYY-MM-DD, today or yesterday.");
                    day = d;
                    break;
                case "--days" or "-d":
                    if (!int.TryParse(Next(), NumberStyles.None, CultureInfo.InvariantCulture, out var days) || days < 1)
                        return Fail("--days needs a number of days, like --days 7.");
                    fromDay = today.AddDays(-(days - 1));
                    break;
                case "--from" or "--since":
                    if (!ReportRange.TryDay(Next(), today, out var f))
                        return Fail("--from needs a day: YYYY-MM-DD, today or yesterday.");
                    fromDay = f;
                    break;
                case "--to" or "--until":
                    if (!ReportRange.TryDay(Next(), today, out var t))
                        return Fail("--to needs a day: YYYY-MM-DD, today or yesterday.");
                    toDay = t;
                    break;
                case "--redact":
                    redact = true;
                    break;
                case "--yes" or "-y":
                    yes = true;
                    break;
                case "-h" or "--help":
                    Console.WriteLine(Usage);
                    return 0;
                default:
                    if (a.StartsWith('-') || when is not null)
                        return Fail($"Unexpected '{a}'.");
                    when = a;
                    break;
            }
        }

        if (when is null && terms.Count == 0)
            return Fail("What should I forget? For example: watchme forget 15m, or watchme forget --match \"my bank\" --days 7");
        if (when is not null && (fromDay is not null || toDay is not null))
            return Fail("Use either WHEN or --days/--from/--to.");
        if ((fromDay is not null || toDay is not null) && terms.Count == 0)
            return Fail("--days, --from and --to go with --match; for a stretch of time use WHEN, like 14:10..14:35 --day yesterday.");

        long? fromMs = null, toMs = null;
        var nowLocal = DateTime.Now;
        if (when is not null)
        {
            if (!Forgetting.TryParseWindow(when, nowLocal, day, out var f, out var t, out var error))
                return Fail(error!);
            (fromMs, toMs) = (f, t);
        }
        else
        {
            if (day is not null)
                return Fail("--day goes with a time range like 14:10..14:35.");
            fromMs = fromDay is { } fd ? Dashboard.DayStart(fd) : null;
            toMs = toDay is { } td ? Dashboard.DayStart(td.AddDays(1)) : null;
        }

        if (!File.Exists(Paths.Database))
        {
            Console.WriteLine($"No data yet ({Paths.Database} does not exist), so nothing to forget.");
            return 0;
        }

        var selection = new Forgetting.Selection(fromMs, toMs, terms, source);
        using var db = Database.Open(Paths.Database);
        var plan = Forgetting.Preview(db, selection, redact);
        var now = Clock.NowMs();
        var color = !Console.IsOutputRedirected && Environment.GetEnvironmentVariable("NO_COLOR") is null;

        if (plan.Items.Count == 0 && !selection.IsWindow)
        {
            Console.WriteLine(fromMs is null ? $"Nothing matches {Label(terms)}." : $"Nothing matches {Label(terms)} {Span(fromMs, toMs, now)}.");
            return 0;
        }

        PrintPlan(plan, now, color);

        if (!yes)
        {
            if (Console.IsInputRedirected)
            {
                Console.Error.WriteLine("Add --yes to forget without being asked.");
                return 2;
            }
            var question = plan.Items.Count == 0
                ? "Forget this time anyway, so app history read later leaves it out?"
                : $"Forget {(plan.Items.Count == 1 ? "this event" : $"these {plan.Items.Count:N0} events")}? This can't be undone.";
            Console.Write($"{question} [y/N] ");
            if (Console.ReadLine()?.Trim().ToLowerInvariant() is not ("y" or "yes"))
            {
                Console.WriteLine("Nothing was changed.");
                return 1;
            }
        }

        var done = Forgetting.Apply(db, selection, redact, Clock.NowMs());
        var parts = new List<string>();
        if (done.Removed > 0)
            parts.Add($"removed {done.Removed:N0}");
        if (done.RedactedCount > 0)
            parts.Add($"redacted {done.RedactedCount:N0}");
        Console.WriteLine(parts.Count == 0 ? "Done." : $"Done: {string.Join(", ", parts)}.");
        if (selection.IsWholeWindow && !redact)
            Console.WriteLine("That time no longer counts as active.");
        if (!selection.IsWindow)
            Console.WriteLine("New events like these are still recorded: for a site add it to edgePrivateSites, for a window title to redactTitlePatterns (config.json).");
        return 0;
    }

    private static void PrintPlan(Forgetting.Plan plan, long now, bool color)
    {
        var s = plan.Selection;
        var what = s.Terms.Count > 0 ? $"Events matching {Label(s.Terms)}" : "Everything";
        if (s.Source is not null)
            what += $" from {EventSources.DisplayName(s.Source)}";
        Console.WriteLine($"{what} {Span(s.FromMs, s.ToMs, now)}:");

        if (plan.Items.Count == 0)
            Console.WriteLine("  nothing recorded");
        foreach (var (source, removed, redacted) in plan.BySource())
        {
            var parts = new List<string>();
            if (removed > 0)
                parts.Add($"{removed:N0} removed");
            if (redacted > 0)
                parts.Add(source == EventSources.Focus && !plan.Redact
                    ? $"{redacted:N0} titles blanked (the time stays)"
                    : $"{redacted:N0} redacted (kept without titles and text)");
            Console.WriteLine($"  {EventSources.DisplayName(source),-16} {string.Join(", ", parts)}");
        }
        if (s.IsWindow)
            Console.WriteLine(plan.Redact
                ? "  What apps logged in this time and is read later is redacted too."
                : "  What apps logged in this time and is read later (Edge, PowerShell, plays...) is left out too.");
        if (s.IsWholeWindow && !plan.Redact)
            Console.WriteLine("  The time stops counting as active; idle, lock and agent start/stop stay.");

        if (plan.Items.Count == 0)
            return;
        Console.WriteLine();
        // Process starts and exits say little about what is being forgotten; show the rest first.
        var shown = plan.Items.Where(i => i.Source != EventSources.Process).ToList();
        if (shown.Count == 0)
            shown = plan.Items;
        var multiDay = shown.Select(i => Local(i.Ts).Date).Distinct().Skip(1).Any();
        foreach (var item in shown.Count <= Examples ? shown : shown.Take(Examples / 2).Concat(shown.TakeLast(Examples / 2)))
        {
            var hit = new EventSearch.Hit(item.Id, item.Ts, item.Source, item.Kind, item.App, item.Title, item.Data);
            var text = item.Title ?? (EventSearch.MatchedField(hit, s.Terms) is var (key, value) ? $"{key}: {value}" : item.App ?? "");
            var time = Local(item.Ts).ToString(multiDay ? "d MMM HH:mm" : "HH:mm", CultureInfo.CurrentCulture);
            Console.WriteLine($"  {time}  {Fit(EventSources.DisplayName(item.Source), 15)}  {Fit(item.Kind, 12)}  {EventSearch.Highlight(EventSearch.Snippet(text, s.Terms, 80), s.Terms, color)}");
            if (shown.Count > Examples && item == shown[Examples / 2 - 1])
                Console.WriteLine($"  … {shown.Count - Examples:N0} more …");
        }
        Console.WriteLine();
    }

    private static string Span(long? from, long? to, long now)
    {
        if (from is null && to is null)
            return "in the whole history";
        string At(long ms) => AgentHealth.When(ms, now);
        if (from is { } f && to is { } t)
            return $"from {At(f)} to {(t >= now - 1000 ? "now" : At(t))} ({Minutes(t - f)})";
        return from is { } f2 ? $"since {At(f2)}" : $"before {At(to!.Value)}";
    }

    private static string Minutes(long ms)
    {
        var m = (long)Math.Round(ms / 60_000.0);
        return m < 60 ? $"{m} min" : m < 48 * 60 ? $"{m / 60} h {m % 60:00} min" : $"{m / 1440} days";
    }

    private static string Label(IReadOnlyList<string> terms) => string.Join(" ", terms.Select(t => $"\"{t}\""));

    private static DateTime Local(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime;

    private static string Fit(string s, int width) => s.Length > width ? s[..(width - 1)] + "…" : s.PadRight(width);

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        Console.Error.WriteLine();
        Console.Error.WriteLine(Usage);
        return 2;
    }
}
