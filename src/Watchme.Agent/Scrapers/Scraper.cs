using Watchme.Core;

namespace Watchme.Agent.Scrapers;

/// <summary>
/// Reads an app's own data (its database, log file...) and turns new entries into events.
/// Scrapers are stateless: where they left off is read back from Watchme's database through
/// <see cref="ScrapeContext"/>, and overlapping re-reads are safe because events carry an ExtId.
/// </summary>
internal interface IScraper
{
    string Name { get; }
    TimeSpan Interval { get; }
    IReadOnlyList<WatchEvent> Scrape(ScrapeContext context);
}

internal sealed class ScrapeContext(Database watchme, long now)
{
    public long Now { get; } = now;

    /// <inheritdoc cref="Database.Cursor"/>
    public long? Cursor(string source, string? kind, string? dataField = null) =>
        watchme.Cursor(source, kind, dataField);

    /// <inheritdoc cref="Database.LatestData"/>
    public string? LatestData(string source, string kind) => watchme.LatestData(source, kind);
}

/// <summary>Runs each scraper on its own interval and hands the results to the writer.</summary>
internal sealed class ScraperHost(IEnumerable<IScraper> scrapers, string watchmeDbPath, Action<WatchEvent> emit)
{
    public Task RunAsync(CancellationToken ct) =>
        Task.WhenAll(scrapers.Select(s => Task.Run(() => RunOneAsync(s, ct), ct)));

    private async Task RunOneAsync(IScraper scraper, CancellationToken ct)
    {
        // Overlapping re-reads return some events again. The database ignores them anyway; this
        // just keeps them out of the writer and the log. Bounded, since it is only an optimisation.
        var sent = new HashSet<string>();
        using var timer = new PeriodicTimer(scraper.Interval);
        try
        {
            do
            {
                try
                {
                    // A separate connection: the writer owns the main one, and WAL lets us read alongside it.
                    using var db = Database.Open(watchmeDbPath);
                    var events = scraper.Scrape(new ScrapeContext(db, Clock.NowMs()))
                        .Where(e => e.ExtId is null || !sent.Contains(e.ExtId))
                        .ToList();
                    if (sent.Count > 50_000)
                        sent.Clear();
                    foreach (var e in events)
                    {
                        if (e.ExtId is not null)
                            sent.Add(e.ExtId);
                        emit(e);
                    }
                    if (events.Count > 0)
                        Log.Info($"{scraper.Name}: {events.Count} events");
                }
                catch (Exception ex)
                {
                    Log.Error($"Scraper {scraper.Name} failed", ex);
                }
            } while (await timer.WaitForNextTickAsync(ct));
        }
        catch (OperationCanceledException)
        {
        }
    }
}
