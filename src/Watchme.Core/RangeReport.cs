using System.Globalization;

namespace Watchme.Core;

/// <summary>
/// <c>watchme report 7d</c> and other ranges: one summary for several days. The numbers come
/// from <see cref="Dashboard.Build"/>, so the terminal and the dashboard always agree.
/// </summary>
public static class RangeReport
{
    /// <summary>Ranges longer than this list weeks instead of days.</summary>
    public const int MaxDayRows = 31;

    public static void Print(Database db, DateOnly from, DateOnly to, TextWriter output)
    {
        if (to < from)
            (from, to) = (to, from);
        var d = Dashboard.Build(db, from, to);
        var t = d.Totals;
        var dayCount = d.Days.Count;

        output.WriteLine($"Watchme — {Label(from, to)} ({dayCount} days)");
        output.WriteLine();

        var activeDays = d.Days.Where(x => x.ActiveMs > 0).ToList();
        if (activeDays.Count == 0 && d.Sources.Length == 0)
        {
            output.WriteLine("Nothing recorded in these days.");
            return;
        }

        if (activeDays.Count > 0)
        {
            output.WriteLine($"Active  {Dur(t.ActiveMs)}   on {activeDays.Count} of {dayCount} days, {Dur(t.ActiveMs / activeDays.Count)} a day on those");
            var busiest = activeDays.MaxBy(x => x.ActiveMs)!;
            output.WriteLine($"Busiest {DayName(busiest.Date)} ({Dur(busiest.ActiveMs)})");
        }
        else
            output.WriteLine("No focus time recorded (the agent was not running), but apps logged activity.");
        output.WriteLine();

        if (activeDays.Count > 0)
        {
            PrintDays(d, output);
            output.WriteLine();
            PrintApps(d, output);
            output.WriteLine();
        }
        else if (d.Background.Count > 0)
        {
            DailyReport.PrintBackground(d.Background, output);
            output.WriteLine();
        }

        var lines = Highlights(d);
        if (lines.Count > 0)
        {
            output.WriteLine("Highlights");
            foreach (var (head, text) in lines)
                output.WriteLine($"  {head,-11} {text}");
        }
    }

    private static void PrintDays(Dashboard.Data d, TextWriter output)
    {
        List<(string Label, long Ms)> rows;
        if (d.Days.Count <= MaxDayRows)
        {
            output.WriteLine("Days");
            rows = d.Days.Select(x => (DayName(x.Date), x.ActiveMs)).ToList();
        }
        else
        {
            output.WriteLine("Weeks (from Monday)");
            rows = d.Days
                .GroupBy(x => Monday(Parse(x.Date)))
                .Select(g => ($"{g.Key.ToString("d MMM", CultureInfo.CurrentCulture)}", g.Sum(x => x.ActiveMs)))
                .ToList();
        }
        var top = Math.Max(1, rows.Max(r => r.Ms));
        var width = rows.Max(r => r.Label.Length);
        foreach (var (label, ms) in rows)
            output.WriteLine(ms > 0
                ? $"  {label.PadRight(width)}  {Dur(ms),8}  {DailyReport.Bar(ms, top)}"
                : $"  {label.PadRight(width)}  {"—",8}");
    }

    private static void PrintApps(Dashboard.Data d, TextWriter output)
    {
        output.WriteLine("Apps");
        var top = d.Apps[0].Ms;
        var background = d.Background.ToDictionary(b => b.App, StringComparer.OrdinalIgnoreCase);
        // An app you use in the background stays listed even when it barely had focus.
        var shown = d.Apps.Where((a, i) => (a.Ms >= 60_000 && i < 15) || background.ContainsKey(a.App)).ToList();
        foreach (var a in shown)
        {
            var title = a.TopTitle is null ? "" : $"  {DailyReport.Trim(a.TopTitle, 50)}";
            output.WriteLine($"  {DailyReport.Trim(a.App, 28),-28} {Dur(a.Ms),8}  {DailyReport.Bar(a.Ms, top),-24}{title}");
            if (background.Remove(a.App, out var use))
                output.WriteLine(DailyReport.BackgroundLine(use));
        }
        // Background use of apps that never had focus.
        foreach (var use in d.Background.Where(b => background.ContainsKey(b.App)))
        {
            output.WriteLine($"  {DailyReport.Trim(use.App, 28),-28} {"—",8}");
            output.WriteLine(DailyReport.BackgroundLine(use));
        }
        var rest = d.Apps.Except(shown).ToList();
        if (rest.Count > 0)
            output.WriteLine($"  {$"other ({rest.Count})",-28} {Dur(rest.Sum(a => a.Ms)),8}");
    }

    internal static List<(string Head, string Text)> Highlights(Dashboard.Data d)
    {
        var t = d.Totals;
        var lines = new List<(string, string)>();
        void Add(string head, params string?[] parts)
        {
            var kept = parts.Where(p => !string.IsNullOrEmpty(p)).ToList();
            for (var i = 0; i < kept.Count; i++)
                lines.Add((i == 0 ? head : "", kept[i]!));
        }

        var m = d.Music;
        if (m.Listens > 0 || t.Ratings > 0 || m.Loved > 0)
            Add("Music",
                m.Listens > 0 ? $"{C(m.Listens, "track")} ({N(m.Plays)} counted as plays, {N(m.Skips)} skipped), {Dur((long)(m.ListenedS * 1000))} listened" : null,
                Top("Artists", m.Artists, c => Dur((long)(c.Value * 1000))),
                Top("Albums", m.Albums, c => Dur((long)(c.Value * 1000)), 3),
                Top("Genres", m.Genres, c => Dur((long)(c.Value * 1000))),
                t.Ratings > 0 || m.Loved > 0 ? Join($"{C(t.Ratings, "rating")} changed", m.Loved > 0 ? $"{C(m.Loved, "track")} loved" : null) : null);

        if (d.Web.Pages > 0)
            Add("Web",
                $"{C(d.Web.Pages, "page")}, {C(d.Web.Searches.Count, "search", "searches")}",
                Top("Sites", d.Web.Sites, c => N(c.N)));

        if (t.Commits > 0 || t.Prompts > 0)
            Add("Code",
                t.Commits > 0 ? Join(C(t.Commits, "commit"), t.Pushes > 0 ? C(t.Pushes, "push", "pushes") : null) : null,
                Top("Repos", d.Code.Repos, c => N(c.N)),
                t.Prompts > 0 ? $"{C(t.Prompts, "agent prompt")}, {Dur((long)(t.AgentS * 1000))} of agent work" : null,
                Top("Projects", d.Code.Projects, c => Dur((long)(c.Value * 1000))));

        if (t.Commands > 0)
            Add("PowerShell",
                t.FailedCommands > 0 ? $"{C(t.Commands, "command")}, {N(t.FailedCommands)} failed" : C(t.Commands, "command"),
                Top("Programs", d.Shell.Programs, c => N(c.N)));

        if (t.FileOps > 0 || t.AlbumsAdded > 0 || t.AlbumsRemoved > 0 || t.TagChanges > 0)
            Add("Files",
                t.FileOps > 0 ? $"{C(t.FileOps, "Total Commander operation")}" + (d.FileOps.Count > 0 ? ": " + string.Join(", ", d.FileOps.Take(5).Select(c => $"{c.Name.ToLowerInvariant()} {N(c.N)}")) : "") : null,
                t.AlbumsAdded > 0 || t.AlbumsRemoved > 0 || t.TagChanges > 0
                    ? $"Library: {N(t.AlbumsAdded)} albums added, {N(t.AlbumsRemoved)} removed, {N(t.TagChanges)} tag changes"
                    : null);

        if (t.JournalEntries > 0)
            Add("Journal", $"{C(t.JournalEntries, "entry", "entries")} in Capsule, {N(t.JournalWords)} words");

        if (t.Downloads > 0 || t.MusicSearches > 0)
            Add("Downloads", $"{C(t.Downloads, "download")} in Flipside and Forever, {C(t.MusicSearches, "search", "searches")}");

        return lines;
    }

    private static string? Top(string label, List<Dashboard.Count> items, Func<Dashboard.Count, string> fmt, int take = 5) =>
        items.Count == 0 ? null : $"{label}: " + string.Join(", ", items.Take(take).Select(c => $"{c.Name} {fmt(c)}"));

    private static string Join(params string?[] parts) => string.Join(", ", parts.Where(p => !string.IsNullOrEmpty(p)));

    /// <summary>"1 commit", "3 commits".</summary>
    private static string C(int n, string one, string? many = null) => $"{N(n)} {(n == 1 ? one : many ?? one + "s")}";

    private static string N(int n) => n.ToString("N0", CultureInfo.CurrentCulture);

    private static string Dur(long ms) => DailyReport.Duration(ms);

    private static DateOnly Parse(string iso) => DateOnly.ParseExact(iso, "yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string DayName(string iso) => Parse(iso).ToString("ddd d MMM", CultureInfo.CurrentCulture);

    private static DateOnly Monday(DateOnly day) => day.AddDays(-(((int)day.DayOfWeek + 6) % 7));

    private static string Label(DateOnly from, DateOnly to) =>
        from.Year == to.Year
            ? $"{from.ToString("ddd d MMM", CultureInfo.CurrentCulture)} – {to.ToString("ddd d MMM yyyy", CultureInfo.CurrentCulture)}"
            : $"{from.ToString("d MMM yyyy", CultureInfo.CurrentCulture)} – {to.ToString("d MMM yyyy", CultureInfo.CurrentCulture)}";
}
