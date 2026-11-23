# Watchme

A small Windows agent that starts at logon and records what you do on the computer — which
app is in front, what its window says, when you go idle, which programs you open — into a
local SQLite database. No screenshots, no keystrokes.

It also reads what some apps already record about themselves (see [App scrapers](#app-scrapers)),
so it can tell you not just *that* you used an app but *what you did* in it.

This is a layered plan:

1. **Focus, idle, sessions and processes** — works for every app with no per-app code.
2. **App-native data sources** — Aurora, Aurora NG, Music Library ratings, Total Commander, T3 Code sessions and git.
3. A local endpoint your own apps can push events to.
4. OS-level file attribution (ETW) and UI Automation scraping where nothing else works.

## What gets recorded

| source    | kind                                       | meaning |
|-----------|--------------------------------------------|---------|
| `focus`   | `foreground`                               | A different window came to the front (app, exe, pid, title) |
| `focus`   | `title`                                    | The focused window changed title and kept it for 2 s (new file, tab, track...) |

Edge web apps (installed sites such as Google Chat, X or Reddit) run inside Edge's own process, so
Watchme tells them apart by their window's app id and records them under their own name (`Google
Chat`, with `data.app_id`) instead of `msedge.exe`. Edge window titles lose their
` and 10 more pages - Personal - Microsoft Edge` tail.
| `idle`    | `idle_start` / `idle_end`                  | No input for 5 minutes, backdated to your last input / you are back |
| `session` | `lock` / `unlock` / `suspend` / `resume` / `logoff` / `shutdown` | Session and power state |
| `process` | `start` / `exit`                           | Processes in your session (start time is exact, exit within 5 s) |
| `agent`   | `start` / `stop`                           | The agent itself; an unclean stop is closed at its last heartbeat |

Durations are never stored; they are derived by the `focus_spans` view.

## App scrapers

Scrapers read an app's own data every 30 seconds, read-only, and store new entries as events with
an `ext_id`, so re-reading the same rows never creates duplicates. On first run they import the
last `scrapeBackfillDays` days.

| source   | kind          | from | example title |
|----------|---------------|------|---------------|
| `aurora` | `listen`      | `aurora-history.sqlite3` → `listening_sessions` | `Bad Manners – Lorraine` (data: album, genre, listened seconds, play/skip) |
| `aurora` | `tag_edit`    | `aurora-state.sqlite3` → `tag_edit_operations`, grouped per album folder | `Changed genre Reggaeton → Reggaetón on Daddy Yankee – King Daddy (11 tracks)` |
| `aurora` | `album_added` | `aurora-state.sqlite3` → `album_additions` | `Added album Antiloop - LP (1997)` |
| `aurora_ng` | `listen`   | `%LOCALAPPDATA%\aurora_ng\aurora-history.sqlite3` (Aurora's format, its own file) | `Tiësto – Love Comes Again` |
| `aurora_ng` | `rating`   | `%LOCALAPPDATA%\aurora_ng\rating-log.sqlite3` → `rating_changes` (Aurora NG 0.27.0+) | `Rated Tiësto – Traffic 4★ (was 3★)` |
| `musiclibrary` | `rating` | Music Library's `music-library.sqlite3` → `rating_events`, from any app | `Rated 1 more track on Tiësto – Just Be (2004): 1 of 10 rated, album rating 80` |
| `totalcmd` | `file_op`   | Total Commander's log file (path read from `wincmd.ini`) | `Copied 3 items from c:\photos to d:\backup\photos (created 2 folders)` |
| `t3`     | `run`         | T3 Code's `~\.t3\userdata\statev2.sqlite` → runs, threads, projects, messages | `Claude Code Opus 5.5 (medium) · watchme / Activity monitor · 4m 12s` |
| `git`    | `commit` / `push` / `pull` / `merge` / `checkout` / `reset` / `rebase` / `clone` | Reflogs (`.git/logs/HEAD`, worktrees, `logs/refs/remotes`) of repos under `gitRoots` | `Committed to watchme (master): Add T3 Code scraper` |
| `git`    | `repo_init` / `folder_created` | Creation time of `.git` / of folders directly inside `gitRoots` | `Created folder watchme in C:\_code` |
| `edge`   | `visit`       | Each Edge profile's `History` database (a copy, read when it changed) | `Magik One: First Flight by Tiësto - Rate Your Music` (data: host, url, search) |

Track and disc numbering changes are kept in `data.changes` but shortened to "numbering" in titles.

### Aurora NG and ratings

Aurora NG keeps its own listening history and shares it with Aurora only through OneDrive, so
Watchme reads its file directly. Since 0.27.0, Aurora NG also logs every rating and Love change it
saves in `rating-log.sqlite3` (track, album, before and after), which Watchme reads as
`aurora_ng/rating` events like `Rated Milk Inc. – Closer 4.5★ (was 3★)`.

Music Library logs every change to an album's ratings in `rating_events`, whichever app made it
(Aurora NG, Aurora, MusicBee...), but only per album: rated tracks, completeness and album rating
before and after. The report shows one line per album you rated in one sitting. For Aurora NG
ratings, Music Library's totals are added to Aurora NG's line; ratings from other apps get Music
Library's line on its own:

```
10:48–11:07  Aurora NG       Listened to Milk Inc. – Closer: 14 tracks, 14 counted as plays
10:49–11:07  Aurora NG       Rated 14 tracks on Milk Inc. – Closer: 5★ ×4, 4★ ×8, 3★ ×2; loved 1 · 0 → 14 of 14 tracks rated, album rating 92
14:02–14:10  Music Library   Rated Antiloop – LP (1997): 0 → 12 of 12 tracks rated, album rating 86
```

Listens are grouped per album folder, so guest artists ("feat. ...") stay on the album's line.

### T3 Code sessions

Each `t3/run` is one prompt and the agent work that followed, for every provider T3 drives
(Claude Code, Codex, Antigravity, ACP agents). The event time is when the agent started; `data`
holds the provider, model, effort (`effort`, `reasoningEffort` or `thought_level`), fast mode,
project, workspace, branch, thread, duration, status, whether it was a subagent, and the first
200 characters of your prompt (images become `[image]`).

The report turns runs into one line per thread session. Runs in the same thread with less than 30
minutes between them are one session; subagent runs are left out, because they overlap their parent:

```
13:28–14:09  T3 Code         watchme / Activity monitor: 7 prompts, Opus 5.5 (medium), agent 26m 26s, you 20m 42s
```

*agent* is the time models spent working. *you* is the time that thread's window was in front,
from 5 minutes before the first prompt until 10 minutes after the last answer. It is matched on
the window title, which T3 sets to `project / thread`.

### Git

Git writes a line to a repo's *reflog* for everything that moves `HEAD`, with the time you did it,
and another for each push. Watchme reads those files directly, without running git: repos are
looked for in the `gitRoots` folders and one level below them, every 10 minutes, and new reflog
lines are read every minute. Worktrees, including those T3 and Codex create elsewhere, are read
through their main repo. Commit lines don't name the branch, so it is recovered by replaying the
checkouts. Credentials in clone URLs are dropped.

In the report, commits to one branch less than 30 minutes apart become one line, with the push
that followed. A new folder that became a repo within 10 minutes is shown as one action:

```
13:21        Git             Created folder watchme in C:\_code and started a git repo
13:21–14:17  Git             Committed 6 times to watchme (master), pushed to origin: Initial commit; ...
```

### Added and removed albums, and tag changes

Music Library logs every change to the library in its `library_updates` table. Watchme reads
the albums added to it (imported by Aurora or any other way) and removed from it (deleted in
Aurora, or gone from disk) as `musiclibrary/album_added` and `musiclibrary/album_removed`
events. Additions or removals less than 30 minutes apart become one line, with the albums
listed per artist. Albums Aurora imported are shown on that line only, not again as Aurora's
own `album_added` events:

```
10:22        Music Library   Added 2 albums (22 tracks) from C:\_ToAurora: Antiloop: LP (1997), Fastlane People (2000)
14:44–15:02  Music Library   Removed 9 albums (137 tracks): DJ Tiësto: Space Age 1.0 (1998), Magik Four: A New Adventure (1999), ...
```

Tag changes (genre, year, album artist, album title, publisher, track count...) are read from
the same log as `musiclibrary/tag_change` events, one per field, whichever app made them. The
report shows one line per album and re-read. Edits made in Aurora are already reported by
Aurora, with more detail, so a Music Library change to an album whose title Aurora edited from
a minute before to 10 minutes before is left out, as are Aurora's own track syncs:

```
12:39        Music Library   Changed album artist DJ Tiësto; Tiësto → DJ Tiësto on DJ Tiësto – Magik: First Flight (1997)
```

### PowerShell commands

PowerShell keeps no timestamped log of what you run, so Watchme adds one. Run once:

```powershell
watchme powershell install
```

This writes a small hook script to `%LOCALAPPDATA%\Watchme\hooks\powershell.ps1` and adds three
marked lines to the end of your PowerShell 7 profile (`$PROFILE`, the one Windows Terminal uses)
that load it. A backup of the profile as it was is kept next to it as `.watchme-backup`. Open a
new tab to start logging. `watchme powershell status` shows whether it is set up and how many
commands were logged today, and `watchme powershell uninstall` (or `watchme uninstall`) removes it.

The hook wraps your `prompt` function, which PowerShell runs after every command, so prompts from
oh-my-posh, Starship and the like keep working. For each command it appends one JSON line to
`%LOCALAPPDATA%\Watchme\inbox\powershell-YYYY-MM-DD.jsonl` with the command, the folder it ran
in, start and end time, whether it succeeded, and `$LASTEXITCODE`. The agent reads the inbox
every 30 seconds, and lines written while it is stopped are read later. Inbox files are deleted
after 30 days.

- Commands that **start with a space**, or mention passwords, secrets, tokens, API keys,
  credentials, connection strings or `-AsPlainText`, are logged as `[hidden]`.
- Windows PowerShell 5.1 and shells started with `-NoProfile` (as tools and scripts often are)
  are not logged.

In the report, the commands of one tab become one line per sitting (30 minutes apart start a new
one), with the folder, how many failed and the programs used most:

```
14:02–14:40  PowerShell      in C:\_code\watchme: 23 commands, 2 failed: dotnet ×6, git ×5, watchme ×4
15:10        PowerShell      C:\_code\site> npm run build (failed, exit 2)
```

### Edge browsing

Every page you open in Edge (web apps included) is read from Edge's history, leaving out frames
and redirect hops. Addresses are stored **without query string or fragment**, since that is where
session ids and tokens live; the search terms of search pages (Google, Bing, and sites' own search
boxes such as Rate Your Music's or eBay's) are kept as `data.search`. Hosts matching
`edgePrivateSites` (banks, payment, sign-in and account pages by default) keep only the host: no
title, address or search. InPrivate windows are not in Edge's history at all.

The report shows one line per browsing sitting (pages less than 15 minutes apart), sites by number
of pages, and what the busiest two were about, plus the searches made:

```
07:54–11:07  Edge            195 pages: Rate Your Music 63 (Coolio Albums, My Soul by Coolio, …), Reddit 57 (…), GitHub 19, YouTube 13
07:54        Edge            Searched Google for “t3 code”; Rate Your Music for “coolio”, “gigi d'agostino”
```

### Total Commander setup

Turn on *Configuration → Options → Log file → Create a log file*, with the operations you want
logged ticked. **Point it outside `%TEMP%`**, for example `%APPDATA%\GHISLER\totalcmd.log`:
temp folders get cleaned, and a log deleted before Watchme reads it is lost. Daily rotation is
fine; rotated copies next to the log are read too, so a higher "keep a maximum of" lets Watchme
catch up after being off for a while.

How log lines become events:

- Lines less than 2 seconds apart that belong together (same operation, or the folder lines
  around a folder copy or delete) are one event: `Copied 120 items from X to Y (created 12 folders)`.
- TC logs renames as `Move`; a move within the same folder is reported as `Renamed`.
- A group is stored once it has been quiet for 10 seconds, so a long copy is not split.
- Non-`Ok` statuses are called out, e.g. `… — 2 error`.
- Every line in a group (up to 100) is kept in `data.items`.

## Install

Requires Windows and the .NET 10 SDK.

```powershell
dotnet publish src/Watchme.Cli -c Release -o "$env:LOCALAPPDATA\Watchme\bin"
& "$env:LOCALAPPDATA\Watchme\bin\watchme.exe" install
```

`install` adds the agent to `HKCU\...\CurrentVersion\Run`, adds the install folder to your
user PATH (open a new terminal afterwards to use `watchme` from anywhere), and starts the agent.
No admin rights needed. `uninstall` undoes both and stops the agent; your data is kept.
Publishing to a separate folder means you can keep rebuilding the repo while the agent runs.

To update an installed copy, stop the agent first so its files are not locked:

```powershell
watchme stop
dotnet publish src/Watchme.Cli -c Release -o "$env:LOCALAPPDATA\Watchme\bin"
watchme start
```

## Usage

```text
watchme report [today|yesterday|YYYY-MM-DD]   Summary of a day (default: today)
watchme status                                Is the agent running, and what has it recorded?
watchme start | stop                          Start or stop the background agent
watchme install | uninstall                   Start at every logon and add to PATH (or undo both)
watchme powershell install|uninstall|status   Log the commands you run in PowerShell 7
watchme dashboard [--port N] [--no-open]      Charts and tables in your browser
```

Example report:

```text
Watchme — Sunday 22 November 2026

Active  5h 12m   (first 08:58, last 17:41)

Apps
  T3 Code (Beta).exe            2h 01m  ████████████████████████
  TOTALCMD64.EXE                  48m 10s  ██████████
  ...

  other (3)                      0m 10s  █  SnippingTool.exe, TOTALCMD64.EXE, explorer.exe

Timeline (blocks of 1 minute or more)
  14:04–14:49  45m 02s  T3 Code (Beta).exe        Watchme / Activity monitor
  + 28 shorter visits, 5m 32s in total

Activity in apps
  12:08        Aurora   Changed genre Reggae → Pop Reggae on Inner Circle – Bad To The Bone (18 tracks)
  12:11        Aurora   Changed genre Pop → Boy Band; numbering on A1 – Make It Good (14 tracks)
  15:02–15:41  Aurora   Listened to Bad Manners – Loonee Tunes!: 12 tracks, 9 counted as plays
```

Apps under 30 seconds are summed into an `other` line, and timeline blocks under a minute into a
`shorter visits` line, so short use is still counted.

## Dashboard

```powershell
watchme dashboard
```

This starts a small web server on <http://127.0.0.1:7317> (the next free port if that one is taken)
and opens it in your browser. Press Ctrl+C to stop it. `--port N` picks the port and `--no-open`
leaves the browser alone. It reads the same database as the report, so it works whether or not
the agent is running.

For any range of days (today, yesterday, the last 7, 30 or 90 days, or dates you pick), it shows:

- **Totals:** focus time, songs, pages, agent prompts, commits, commands, ratings, albums added.
- **Focus time per day**, stacked by app (the top 7, the rest as *Other*), with a table view.
- **When:** an hour-of-day grid of focus minutes or app events.
- **Day timeline:** one day's blocks of work by app, idle stretches, and the report's *Activity
  in apps* lines.
- **Apps:** time, share, blocks and most-used window per app.
- **Activity in apps:** events per day (or per hour, for a single day), one small chart per source.
- **Music, Web, Code, PowerShell, Files:** top artists, albums, genres and ratings given; sites by
  pages and your searches; commits per repo, agent time per project and model; programs and
  folders; Total Commander operations and library changes.
- **Event log:** the raw events, filterable by source and searchable, newest first.

The page is built into `watchme.exe` and loads nothing from the internet. The server only listens
on the loopback address and only answers requests addressed to `127.0.0.1` or `localhost`, so a
web page can't read your data through a DNS rebinding trick. The range is kept in the address
(`#from=…&to=…`), so a view can be bookmarked. The theme follows Windows; the ◐ button switches it.

## Querying the data yourself

The database is `%LOCALAPPDATA%\Watchme\watchme.db` (WAL mode, so you can read it while the agent runs):

```sql
-- Raw events in local time
SELECT * FROM events_local ORDER BY id DESC LIMIT 50;

-- Time per app today
SELECT app, SUM(end_ts - start_ts) / 60000.0 AS minutes
FROM focus_spans
WHERE start_ts >= unixepoch('now', 'start of day', 'utc') * 1000
GROUP BY app ORDER BY minutes DESC;
```

## Configuration

`%LOCALAPPDATA%\Watchme\config.json` is created with defaults on first run. Restart the agent after editing.

| key                   | default | |
|-----------------------|---------|-|
| `idleThresholdSeconds`| 300     | No input this long counts as idle |
| `titleStableSeconds`  | 2       | A title must hold this long to be recorded (filters progress counters) |
| `processPollSeconds`  | 5       | Process snapshot interval |
| `redactTitleApps`     | password managers | Apps whose titles are stored as `[redacted]` |
| `redactTitlePatterns` | `InPrivate`, `Incognito`, `Private Browsing` | Titles containing these are redacted |
| `ignoreProcesses`     | Windows helper processes | Left out of process start/exit events |
| `auroraEnabled`       | `true`  | Read Aurora's plays, tag edits and album additions |
| `auroraDataDir`       | `null`  | Aurora's data folder; `null` means `%APPDATA%\com.soundtrackgeek.aurora` |
| `auroraNgEnabled`     | `true`  | Read Aurora NG's listening history and rating log |
| `auroraNgDataDir`     | `null`  | Aurora NG's data folder; `null` means `%LOCALAPPDATA%\aurora_ng` |
| `musicLibraryEnabled` | `true`  | Read rating changes, tag changes and added and removed albums from Music Library |
| `musicLibraryDb`      | `null`  | `null` means `%APPDATA%\com.local.musiclibrary\music-library.sqlite3` |
| `totalCommanderEnabled` | `true` | Read Total Commander's file operation log |
| `totalCommanderIni`   | `null`  | Path to `wincmd.ini`; `null` means `%APPDATA%\GHISLER\wincmd.ini` |
| `t3Enabled`           | `true`  | Read T3 Code runs (Claude Code, Codex, Antigravity...) |
| `t3StateDb`           | `null`  | T3's state database; `null` means `%USERPROFILE%\.t3\userdata\statev2.sqlite` |
| `gitEnabled`          | `true`  | Read git reflogs and new folders |
| `gitRoots`            | `C:\_code`, `C:\code`, `%USERPROFILE%\source\repos`, `%USERPROFILE%\code` | Folders holding your repos; missing ones are skipped |
| `powerShellEnabled`   | `true`  | Read the commands logged by `watchme powershell install` |
| `edgeEnabled`         | `true`  | Read Edge's browsing history |
| `edgeUserDataDir`     | `null`  | `null` means `%LOCALAPPDATA%\Microsoft\Edge\User Data` (every profile in it is read) |
| `edgePrivateSites`    | banks, payment and sign-in hosts | Hosts containing an entry keep only the host; entries ending in `.` (`login.`) must start the host |
| `scrapeBackfillDays`  | 7       | History imported from apps on first run |

Settings missing from an older `config.json` use their defaults.

Set `WATCHME_HOME` to use a different data folder (handy for testing).

## Privacy

Everything stays in the local database. Window titles can still contain sensitive text
(email subjects, document names), so extend the redaction lists to taste.

## Development

```powershell
dotnet build
dotnet test
```

Projects: `Watchme.Core` (schema, config, report, dashboard data), `Watchme.Agent` (the windowless
collector), `Watchme.Cli` (`watchme.exe`, and the dashboard's server and page in `dashboard.html`),
`Watchme.Tests`.
