using System.Globalization;
using Watchme.Core;

namespace Watchme.Cli;

/// <summary><c>watchme search WORDS... [--source S] [--days N] [--from D] [--to D] [--limit N]</c></summary>
internal static class SearchCommand
{
    private const string Usage = "Usage: watchme search WORDS... [--source S] [--days N] [--from DAY] [--to DAY] [--limit N]";

    private static readonly string[] KnownSources =
    [
        EventSources.Focus, EventSources.Idle, EventSources.Process, EventSources.Session, EventSources.Agent,
        EventSources.Aurora, EventSources.AuroraNg, EventSources.MusicLibrary, EventSources.TotalCommander,
        EventSources.T3, EventSources.Git, EventSources.PowerShell, EventSources.Edge, EventSources.Flipside,
        EventSources.Forever, EventSources.Capsule,
    ];

    public static int Run(string[] args)
    {
        var terms = new List<string>();
        string? source = null;
        DateOnly? from = null, to = null;
        var limit = 50;
        var today = DateOnly.FromDateTime(DateTime.Now);

        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            string? Next() => i + 1 < args.Length ? args[++i] : null;
            switch (a.ToLowerInvariant())
            {
                case "--source" or "-s":
                    var name = Next();
                    source = name is null ? null : ResolveSource(name);
                    if (source is null)
                    {
                        Console.Error.WriteLine($"Unknown source '{name}'. Sources: {string.Join(", ", KnownSources)}.");
                        return 2;
                    }
                    break;
                case "--days" or "-d":
                    if (!int.TryParse(Next(), NumberStyles.None, CultureInfo.InvariantCulture, out var days) || days < 1)
                        return Fail("--days needs a number of days, like --days 7.");
                    from = today.AddDays(-(days - 1));
                    break;
                case "--from" or "--since":
                    if (!ReportRange.TryDay(Next(), today, out var f))
                        return Fail("--from needs a day: YYYY-MM-DD, today or yesterday.");
                    from = f;
                    break;
                case "--to" or "--until":
                    if (!ReportRange.TryDay(Next(), today, out var t))
                        return Fail("--to needs a day: YYYY-MM-DD, today or yesterday.");
                    to = t;
                    break;
                case "--limit" or "-n":
                    if (!int.TryParse(Next(), NumberStyles.None, CultureInfo.InvariantCulture, out limit) || limit < 1)
                        return Fail("--limit needs a number, like --limit 200.");
                    limit = Math.Min(limit, EventSearch.MaxLimit);
                    break;
                case "-h" or "--help":
                    Console.WriteLine(Usage);
                    return 0;
                default:
                    if (a.StartsWith("--", StringComparison.Ordinal))
                        return Fail($"Unknown option '{a}'.");
                    terms.Add(a);
                    break;
            }
        }

        if (terms.Count == 0 || terms.All(string.IsNullOrWhiteSpace))
            return Fail("What should I look for? For example: watchme search rateyourmusic --days 30");

        if (!File.Exists(Paths.Database))
        {
            Console.WriteLine($"No data yet ({Paths.Database} does not exist). Run 'watchme start' first.");
            return 0;
        }

        using var db = Database.Open(Paths.Database);
        var query = new EventSearch.Query(
            terms,
            from is { } fd ? Dashboard.DayStart(fd) : null,
            to is { } td ? Dashboard.DayStart(td.AddDays(1)) : null,
            source,
            limit);
        var result = EventSearch.Run(db, query);
        var color = !Console.IsOutputRedirected && Environment.GetEnvironmentVariable("NO_COLOR") is null;
        EventSearch.Print(result, terms, Console.Out, color);
        return 0;
    }

    /// <summary>A source by id, display name or a short alias: "edge", "Total Commander", "tc", "pwsh".</summary>
    internal static string? ResolveSource(string name)
    {
        static string Norm(string s) => new(s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
        var n = Norm(name);
        var alias = n switch
        {
            "tc" or "totalcommander" => EventSources.TotalCommander,
            "ps" or "pwsh" => EventSources.PowerShell,
            "ng" => EventSources.AuroraNg,
            "ml" or "library" => EventSources.MusicLibrary,
            "t3code" => EventSources.T3,
            _ => null,
        };
        return alias ?? KnownSources.FirstOrDefault(s => Norm(s) == n || Norm(EventSources.DisplayName(s)) == n);
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        Console.Error.WriteLine(Usage);
        return 2;
    }
}
