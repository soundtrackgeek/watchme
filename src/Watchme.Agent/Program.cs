using System.Text.Json;
using Watchme.Agent;
using Watchme.Agent.Scrapers;
using Watchme.Core;

Paths.EnsureDataDir();

// One agent per user session. The CLI checks this same mutex to answer "is it running?".
using var instance = new Mutex(initiallyOwned: true, AgentIpc.MutexName, out var isFirst);
if (!isFirst)
    return 0;

using var stopSignal = new EventWaitHandle(false, EventResetMode.ManualReset, AgentIpc.StopEventName);
stopSignal.Reset();

try
{
    var config = WatchmeConfig.Load(Paths.Config);
    using var db = Database.Open(Paths.Database);
    if (db.CloseUncleanRun())
        Log.Info("Previous run ended uncleanly; closed it at its last heartbeat.");

    var agent = new AgentHost(db, config);
    var exitReason = agent.Run(stopSignal);
    Log.Info($"Stopped ({exitReason}).");
    return 0;
}
catch (Exception ex)
{
    Log.Error("Agent crashed", ex);
    return 1;
}

namespace Watchme.Agent
{
    internal sealed class AgentHost(Database db, WatchmeConfig config)
    {
        private readonly CancellationTokenSource _cts = new();
        private readonly ManualResetEventSlim _stopped = new();
        private readonly Lock _gate = new();
        private string? _stopReason;

        public string Run(WaitHandle stopSignal)
        {
            var writer = new EventWriter(db);
            var writerTask = Task.Run(writer.RunAsync);

            var version = typeof(AgentHost).Assembly.GetName().Version?.ToString(3);
            writer.Post(new WatchEvent(Clock.NowMs(), EventSources.Agent, "start")
            {
                Pid = Environment.ProcessId,
                Exe = Environment.ProcessPath,
                Data = JsonSerializer.Serialize(new { version }),
            });
            Log.Info($"Started v{version}, pid {Environment.ProcessId}, db {Paths.Database}");

            var pause = new PauseMonitor(PauseState.FilePath, writer.Post);
            var focus = new FocusTracker(new Win32DesktopProbe(), config, writer.Post) { IsPaused = pause.IsPaused };
            var processes = new ProcessTracker(config, writer.Post) { IsPaused = pause.IsPaused };
            using var session = new SessionTracker(focus, writer.Post, RequestStopAndWait);

            var scrapers = new List<IScraper>();
            if (config.AuroraEnabled)
            {
                var auroraDir = config.AuroraDataDir ?? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "com.soundtrackgeek.aurora");
                scrapers.Add(new AuroraScraper(auroraDir, config.ScrapeBackfillDays));
            }
            if (config.AuroraNgEnabled)
            {
                var dir = config.AuroraNgDataDir ?? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "aurora_ng");
                scrapers.Add(new AuroraNgScraper(dir, config.ScrapeBackfillDays));
            }
            if (config.MusicLibraryEnabled)
            {
                var library = config.MusicLibraryDb ?? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "com.local.musiclibrary", "music-library.sqlite3");
                scrapers.Add(new MusicLibraryScraper(library, config.ScrapeBackfillDays));
            }
            if (config.TotalCommanderEnabled)
            {
                var ini = config.TotalCommanderIni ?? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GHISLER", "wincmd.ini");
                // Re-read the ini every scrape, so turning TC's logging on or moving the file just works.
                scrapers.Add(new TotalCommanderScraper(() => TotalCommanderScraper.LogPathFromIni(ini), config.ScrapeBackfillDays));
            }
            if (config.T3Enabled)
            {
                var state = config.T3StateDb ?? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".t3", "userdata", "statev2.sqlite");
                scrapers.Add(new T3Scraper(state, config.ScrapeBackfillDays));
            }
            if (config.GitEnabled)
                scrapers.Add(new GitScraper(() => config.GitRoots, config.ScrapeBackfillDays));
            if (config.EdgeEnabled)
            {
                var userData = config.EdgeUserDataDir ?? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Edge", "User Data");
                scrapers.Add(new EdgeHistoryScraper(userData, () => config.EdgePrivateSites, config.ScrapeBackfillDays));
            }
            if (config.PowerShellEnabled)
                scrapers.Add(new PowerShellScraper(Paths.Inbox, config.ScrapeBackfillDays));
            // Flipside and Forever both write logs\activity.YYYY-MM-DD.jsonl in their data folder.
            string AppDataDir(string? configured, string identifier) => configured ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), identifier);
            if (config.FlipsideEnabled)
                scrapers.Add(new ActivityLogScraper(EventSources.Flipside, "flipside.exe",
                    Path.Combine(AppDataDir(config.FlipsideDataDir, "com.soundtrackgeek.flipside"), "logs"), config.ScrapeBackfillDays));
            if (config.ForeverEnabled)
                scrapers.Add(new ActivityLogScraper(EventSources.Forever, "forever.exe",
                    Path.Combine(AppDataDir(config.ForeverDataDir, "com.soundtrackgeek.forever"), "logs"), config.ScrapeBackfillDays));
            if (config.CapsuleEnabled)
            {
                var settings = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Capsule", "path_settings.json");
                var fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".capsule", "capsule.db");
                // Re-read Capsule's settings every scrape, so moving the journal just works.
                scrapers.Add(new CapsuleScraper(() => config.CapsuleDb ?? CapsuleScraper.DbPathFromSettings(settings, fallback), config.ScrapeBackfillDays));
            }
            var scraperHost = new ScraperHost(scrapers, Paths.Database, PauseState.FilePath, writer.Post);

            var collectors = new[]
            {
                Task.Run(() => focus.RunAsync(_cts.Token)),
                Task.Run(() => processes.RunAsync(_cts.Token)),
                scraperHost.RunAsync(_cts.Token),
            };

            using var tray = config.TrayIcon ? TrayIcon.Start(() => RequestStop("tray")) : null;

            // Block until `watchme stop` signals us, Quit in the tray menu, or logoff/shutdown calls RequestStop.
            WaitHandle.WaitAny([stopSignal, _cts.Token.WaitHandle]);
            RequestStop("stop");
            // First, so the icon goes away (and the dashboard it started stops) without waiting on the collectors.
            tray?.Dispose();

            Task.WaitAll(collectors, TimeSpan.FromSeconds(3));
            writer.Post(new WatchEvent(Clock.NowMs(), EventSources.Agent, "stop")
            {
                Pid = Environment.ProcessId,
                Data = JsonSerializer.Serialize(new { reason = _stopReason }),
            });
            writer.Complete();
            writerTask.Wait(TimeSpan.FromSeconds(5));
            _stopped.Set();
            return _stopReason!;
        }

        private void RequestStop(string reason)
        {
            lock (_gate)
            {
                _stopReason ??= reason;
            }
            _cts.Cancel();
        }

        /// <summary>For logoff/shutdown: stop, and hold Windows off until the database is flushed.</summary>
        private void RequestStopAndWait(string reason)
        {
            RequestStop(reason);
            _stopped.Wait(TimeSpan.FromSeconds(8));
        }
    }
}
