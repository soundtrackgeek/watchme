using Watchme.Core;

namespace Watchme.Agent.Scrapers;

/// <summary>
/// Aurora NG keeps its own listening history in Aurora's format
/// (<c>%LOCALAPPDATA%\aurora_ng\aurora-history.sqlite3</c>) and only shares it with Aurora through
/// OneDrive, so it is read on its own: aurora_ng/listen events. Its ratings go into the MP3s and
/// Music Library, where <see cref="MusicLibraryScraper"/> picks them up.
/// </summary>
internal sealed class AuroraNgScraper(string historyDb, int backfillDays) : IScraper
{
    public string Name => "aurora_ng";
    public TimeSpan Interval => TimeSpan.FromSeconds(30);

    public IReadOnlyList<WatchEvent> Scrape(ScrapeContext context) =>
        File.Exists(historyDb)
            ? AuroraScraper.ReadListens(historyDb, EventSources.AuroraNg, "Aurora NG.exe", context,
                context.Now - backfillDays * 24L * 60 * 60 * 1000)
            : [];
}
