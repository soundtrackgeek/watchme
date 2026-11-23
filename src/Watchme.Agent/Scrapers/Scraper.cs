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

    /// <summary>Why this run found nothing to read, shown in <c>watchme status</c>; the first one counts.</summary>
    public string? Warning { get; private set; }

    /// <summary>Says why there is nothing to read ("the log file is off"), for <c>watchme status</c>.</summary>
    public void Warn(string message) => Warning ??= message;
}

/// <summary>
/// Runs each scraper on its own interval and hands the results to the writer. Events that fall in
/// a pause or a forgotten stretch are dropped (or, in a redacted one, lose their text), and how
/// each run went is kept in <c>meta</c> for <c>watchme status</c>.
/// </summary>
internal sealed class ScraperHost(IEnumerable<IScraper> scrapers, string watchmeDbPath, string pauseFile, Action<WatchEvent> emit)
{
    public Task RunAsync(CancellationToken ct) =>
        Task.WhenAll(scrapers.Select(s => Task.Run(() => RunOneAsync(s, ct), ct)));

    private async Task RunOneAsync(IScraper scraper, CancellationToken ct)
    {
        // Overlapping re-reads return some events again. The database ignores them anyway; this
        // just keeps them out of the writer and the log. Bounded, since it is only an optimisation.
        var sent = new HashSet<string>();
        using var timer = new PeriodicTimer(scraper.Interval);
        ScraperHealth? health = null;
        try
        {
            do
            {
                // A separate connection: the writer owns the main one, and WAL lets us read alongside it.
                Database? db = null;
                try
                {
                    db = Database.Open(watchmeDbPath);
                    health ??= ScraperHealth.FromJson(db.GetMeta(ScraperHealth.MetaPrefix + scraper.Name));
                    var now = Clock.NowMs();
                    var context = new ScrapeContext(db, now);
                    var events = Run(scraper, context, db, now, out var dropped, out var forgotten, out var redacted);
                    health = ScraperHealth.After(health, scraper.Name, scraper.Interval, now, events.Count, context.Warning, null);
                    if (dropped > 0)
                        Log.Info($"{scraper.Name}: left out {dropped} events from a pause");
                    if (forgotten > 0 || redacted > 0)
                        Log.Info($"{scraper.Name}: left out {forgotten} and redacted {redacted} events in forgotten time");
                }
                catch (Exception ex)
                {
                    Log.Error($"Scraper {scraper.Name} failed", ex);
                    health = ScraperHealth.After(health, scraper.Name, scraper.Interval, Clock.NowMs(), 0, null, ex);
                }
                finally
                {
                    SaveHealth(db, scraper.Name, health);
                    db?.Dispose();
                }
            } while (await timer.WaitForNextTickAsync(ct));
        }
        catch (OperationCanceledException)
        {
        }

        List<WatchEvent> Run(IScraper scraper, ScrapeContext context, Database db, long now, out int dropped, out int forgotten, out int redacted)
        {
            var paused = PauseState.Windows(db);
            if (PauseState.Active(pauseFile, now) is { } p)
                paused.Add((p.Since, p.Until)); // in case its event isn't written yet
            var scraped = scraper.Scrape(context)
                .Where(e => e.ExtId is null || !sent.Contains(e.ExtId))
                .ToList();
            var events = paused.Count == 0 ? scraped : scraped.Where(e => !PauseState.Covers(paused, e.Ts)).ToList();
            dropped = scraped.Count - events.Count;
            events = Forgetting.Filter(events, Forgetting.Windows(db), out forgotten, out redacted);
            if (sent.Count > 50_000)
                sent.Clear();
            // Dropped ones count as handled too, so they aren't read and dropped again every run.
            foreach (var e in scraped)
                if (e.ExtId is not null)
                    sent.Add(e.ExtId);
            foreach (var e in events)
                emit(e);
            if (events.Count > 0)
                Log.Info($"{scraper.Name}: {events.Count} events");
            return events;
        }
    }

    private static void SaveHealth(Database? db, string name, ScraperHealth? health)
    {
        if (db is null || health is null)
            return;
        try
        {
            db.SetMeta(ScraperHealth.MetaPrefix + name, health.ToJson());
        }
        catch (Exception ex)
        {
            Log.Error($"Could not save the health of {name}", ex);
        }
    }
}
