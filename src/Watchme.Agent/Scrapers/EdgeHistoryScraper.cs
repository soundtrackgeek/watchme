using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Watchme.Core;

namespace Watchme.Agent.Scrapers;

/// <summary>
/// Reads Edge's History database for every profile: one edge/visit event per page you opened
/// (redirect hops and frames left out), with its title, the address without query string, and
/// the search terms for search result pages. Sites matching <see cref="WatchmeConfig.EdgePrivateSites"/>
/// keep only their host. Edge has the file open, so it is copied (with its journal) when it changed.
/// </summary>
internal sealed class EdgeHistoryScraper(string userDataDir, Func<IReadOnlyList<string>> privateSites, int backfillDays) : IScraper
{
    private const string Source = EventSources.Edge;
    private const string Kind = "visit";

    /// <summary>Edge writes History in batches, a little after the visit.</summary>
    private const long LookbackMs = 10 * 60 * 1000;

    /// <summary>Chromium times are microseconds since 1601-01-01 UTC.</summary>
    private const long EpochDeltaMicros = 11_644_473_600L * 1_000_000;

    // Page transition bits (ui::PageTransition).
    private const long CoreMask = 0xFF;
    private const long AutoSubframe = 3, ManualSubframe = 4;
    private const long ClientRedirect = 0x40000000, ServerRedirect = 0x80000000;

    private static readonly JsonSerializerOptions Json = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly Dictionary<string, DateTime> _copiedAt = [];
    private readonly string _work = Path.Combine(Path.GetTempPath(), "watchme-edge-" + Environment.ProcessId);

    public string Name => "edge";
    public TimeSpan Interval => TimeSpan.FromSeconds(60);

    public IReadOnlyList<WatchEvent> Scrape(ScrapeContext context)
    {
        if (!Directory.Exists(userDataDir))
            return [];

        var events = new List<WatchEvent>();
        foreach (var profileDir in Directory.EnumerateDirectories(userDataDir))
        {
            var history = Path.Combine(profileDir, "History");
            if (!File.Exists(history))
                continue;
            var profile = Path.GetFileName(profileDir);
            var modified = File.GetLastWriteTimeUtc(history);
            // A copy of 30 MB is only worth making when Edge wrote something.
            if (_copiedAt.TryGetValue(profile, out var last) && last == modified && context.Cursor(Source, Kind) is not null)
                continue;
            var snapshot = Snapshot(history, profile);
            if (snapshot is null)
                continue;
            _copiedAt[profile] = modified;
            try
            {
                events.AddRange(ReadVisits(snapshot, profile, context));
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                TryDelete(snapshot);
                TryDelete(snapshot + "-journal");
            }
        }
        return events;
    }

    /// <summary>Copies History and its journal; opening the copy rolls back a half-written change.</summary>
    private string? Snapshot(string history, string profile)
    {
        Directory.CreateDirectory(_work);
        var copy = Path.Combine(_work, profile.Replace(' ', '_') + ".sqlite");
        try
        {
            CopyShared(history, copy);
            var journal = history + "-journal";
            if (File.Exists(journal) && new FileInfo(journal).Length > 0)
                CopyShared(journal, copy + "-journal");
            else
                TryDelete(copy + "-journal");
            return copy;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static void CopyShared(string from, string to)
    {
        using var source = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var target = new FileStream(to, FileMode.Create, FileAccess.Write);
        source.CopyTo(target);
    }

    /// <summary>Visits in a History database (or a copy of one), as events.</summary>
    internal IEnumerable<WatchEvent> ReadVisits(string historyDb, string profile, ScrapeContext context)
    {
        var cursor = context.Cursor(Source, Kind);
        var fromMs = cursor is { } c ? c - LookbackMs : context.Now - backfillDays * 24L * 60 * 60 * 1000;

        using var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = historyDb,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString());
        db.Open();

        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT v.id, v.visit_time, v.transition, u.url, u.title,
                   (SELECT k.term FROM keyword_search_terms k WHERE k.url_id = u.id LIMIT 1)
            FROM visits v JOIN urls u ON u.id = v.url
            WHERE v.visit_time >= $from
              AND NOT EXISTS (SELECT 1 FROM visits n
                              WHERE n.from_visit = v.id AND (n.transition & $redirects) != 0)
            ORDER BY v.visit_time;
            """;
        cmd.Parameters.AddWithValue("$from", fromMs * 1000 + EpochDeltaMicros);
        cmd.Parameters.AddWithValue("$redirects", ClientRedirect | ServerRedirect);

        var sites = privateSites();
        var events = new List<WatchEvent>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var transition = r.GetInt64(2);
            var core = transition & CoreMask;
            // Frames are not pages you opened. Redirect hops are left out by the query: a visit
            // another visit redirected away from. (Edge often leaves CHAIN_END off real pages.)
            if (core is AutoSubframe or ManualSubframe)
                continue;
            if (!Uri.TryCreate(r.GetString(3), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
                continue;

            var host = uri.Host;
            var isPrivate = IsPrivate(host, sites);
            var title = r.IsDBNull(4) ? "" : r.GetString(4);
            var search = r.IsDBNull(5) ? SiteSearch(uri) : r.GetString(5);
            var data = new JsonObject
            {
                ["visit_id"] = r.GetInt64(0),
                ["profile"] = profile,
                ["host"] = host,
                // No query string or fragment: that is where session ids and tokens live.
                ["url"] = isPrivate ? null : $"{uri.Scheme}://{uri.Authority}{uri.AbsolutePath}",
                ["search"] = isPrivate ? null : search,
                ["transition"] = TransitionName(core),
                ["private"] = isPrivate ? true : null,
            };
            events.Add(new WatchEvent((r.GetInt64(1) - EpochDeltaMicros) / 1000, Source, Kind)
            {
                App = "msedge.exe",
                Title = isPrivate ? host : (title.Length > 0 ? title : host + uri.AbsolutePath),
                Data = data.ToJsonString(Json),
                ExtId = $"visit:{profile}:{r.GetInt64(0)}",
            });
        }
        return events;
    }

    /// <summary>
    /// A host is private when it contains an entry ("bank"), or for entries ending in a dot
    /// ("login."), when it starts with it, so "id." matches id.example.com but not android.com.
    /// </summary>
    internal static bool IsPrivate(string host, IEnumerable<string> sites) =>
        sites.Any(s => s.Length > 0 && (s.EndsWith('.')
            ? host.StartsWith(s, StringComparison.OrdinalIgnoreCase)
            : host.Contains(s, StringComparison.OrdinalIgnoreCase)));

    /// <summary>Query parameters sites use for their own search box (Rate Your Music, eBay, Yahoo...).</summary>
    private static readonly string[] SearchParameters = ["q", "query", "searchterm", "search_query", "search", "_nkw", "p", "text"];

    /// <summary>The term of a site's own search page, which Edge's keyword table does not cover.</summary>
    internal static string? SiteSearch(Uri uri)
    {
        if (uri.Query.Length < 2)
            return null;
        var pairs = uri.Query[1..].Split('&')
            .Select(p => p.Split('=', 2))
            .Where(p => p.Length == 2)
            .Select(p => (Key: Uri.UnescapeDataString(p[0]), Value: Uri.UnescapeDataString(p[1].Replace('+', ' '))))
            .ToList();
        foreach (var name in SearchParameters)
        {
            if (pairs.FirstOrDefault(p => p.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value is { Length: > 0 } term)
                return term.Trim();
        }
        return null;
    }

    private static string TransitionName(long core) => core switch
    {
        0 => "link",
        1 => "typed",
        2 => "bookmark",
        5 => "generated",
        6 => "start_page",
        7 => "form",
        8 => "reload",
        9 => "keyword",
        _ => core.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
