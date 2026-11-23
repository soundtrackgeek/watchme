# Watchme

A small Windows agent that starts at logon and records what you do on the computer — which
app is in front, what its window says, when you go idle, which programs you open — into a
local SQLite database. No screenshots, no keystrokes.

It also reads what some apps already record about themselves (see [App scrapers](#app-scrapers)),
so it can tell you not just *that* you used an app but *what you did* in it.

This is a layered plan:

1. **Focus, idle, sessions and processes** — works for every app with no per-app code.
2. **App-native data sources** — Aurora, Aurora NG, Music Library ratings, Total Commander, T3 Code sessions, git, Edge, Flipside and Forever.
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
| `flipside` / `forever` | `search` | The app's activity log, `logs\activity.YYYY-MM-DD.jsonl` | `Searched Soulseek for “inxs remixes”`, `Searched the catalog for “tiësto”` |
| `forever` | `browse` | 〃 | `Browsed alice's shares (12,345 files)` |
| `forever` | `wanted_add` | 〃 | `Added INXS – INXS² The Remixes (2004) to Wanted` |
| `flipside` / `forever` | `download_queued` / `download` | 〃 | `Downloaded Tiësto – Just Be (2004) from Deezer (FLAC, 12 tracks)` |

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

### Flipside and Forever

Neither app kept a timed record of what you searched for or whose shares you browsed, so both
now write an activity log (Flipside 0.17.0 and Forever 0.0.98 onwards): one JSON line per action
in `logs\activity.YYYY-MM-DD.jsonl` in their data folder (`%APPDATA%\com.soundtrackgeek.flipside`,
`%APPDATA%\com.soundtrackgeek.forever`), kept for 90 days. Watchme reads it every 30 seconds.

| event | Flipside | Forever |
|-------|----------|---------|
| `search` | Store searches (`service: catalog`) and Soulseek searches from the Get drawer | Soulseek searches, and searches inside one user's shares (`scope: user`) |
| `browse` | — | Whose shares you opened (`ok: false` when they didn't answer) |
| `wanted_add` | — | Albums added to Wanted, alone or in bulk |
| `download_queued` | Soulseek releases and Usenet NZBs you queued | Soulseek releases and files; `origin: smart` when Smart downloads queued it |
| `download` | Finished Deezer albums, Usenet releases and Soulseek releases | Finished Soulseek releases, with every user who sent files |

Work the apps do on their own is left out: Forever's Wanted checks and relay searches, and
Flipside's Auto-get searches (the album Auto-get downloads is logged). Soulseek downloads are
logged once the release's last file is in.

In the report, each app gets one line per sitting (actions less than 30 minutes apart) for each of
searches, browsed users, Wanted additions and finished downloads. Queued downloads are in the
event log and dashboard but not the report, since the finished download says the same:

```
20:14–20:31  Forever         Searched Soulseek for “inxs remixes”, “new drama”; alice's shares for “flac”
20:16–20:20  Forever         Browsed the shares of 2 users: alice, bob (no answer)
20:22–21:05  Forever         Downloaded 3 albums (1.4 GB): INXS - Kick (1987) from alice; ...
21:10        Flipside        Downloaded Tiësto – Just Be (2004) from Deezer (FLAC, 12 tracks)
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

To update an installed copy after pulling or changing the source:

```powershell
watchme update
```

This builds the repo the installed copy was built from (or the one you name:
`watchme update C:\_code\watchme`) into a staging folder, stops the agent, replaces the installed
files and starts the agent again. A failed build changes nothing. Files still in use, such as
`watchme.exe` itself or a `watchme dashboard` left running, are renamed to `*.old` and removed by
a later update; a running dashboard keeps the old version until you restart it.

Copies older than 0.15.0 don't have `update` yet. Update those once by hand, with the agent
stopped and any `watchme dashboard` closed (a plain `dotnet publish` without `-o` builds into the
repo and leaves the installed copy unchanged):

```powershell
watchme stop
dotnet publish src/Watchme.Cli -c Release -o "$env:LOCALAPPDATA\Watchme\bin"
watchme start
```

## Usage

```text
watchme report [DAY|RANGE]                    Summary of a day (default: today) or several days
watchme search WORDS... [options]             Find events in the whole history
watchme status                                Is the agent running, and is every source working?
watchme pause [30m|2h|1h30m|14:30]            Record nothing for a while; resumes by itself
watchme resume                                End a pause early
watchme forget 15m|14:10..14:35 [--day DAY]   Remove what was recorded in a stretch of time
watchme forget --match WORDS [--days N]       Remove the events holding these words
watchme start | stop                          Start or stop the background agent
watchme install | uninstall                   Start at every logon and add to PATH (or undo both)
watchme update [repo]                         Rebuild from source and replace the installed copy
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

### Apps you use without focusing them

Focus time only counts the window in front, so a music player you drive with global hotkeys
(skip, rate, love) barely shows up. For Aurora NG and Aurora, the report adds a line under the app
with what they logged instead:

```text
  Aurora NG.exe                 20m 03s  ██                        Aurora NG
    └ hands-on 15h 00m · 884 skips · 144 ratings · 24h 16m playing
```

- **Hands-on** is your own actions grouped into stretches: a skip (timed when the track stopped)
  or a rating or Love change less than 2 minutes after the previous one continues the stretch,
  which lasts from its first action to its last plus 10 seconds.
- **Playing** is the time tracks actually played.

Both happen while you are working in other windows, so neither is added to *Active*. An app with
background use is listed even when it had little or no focus (`—`), and days without focus time
get an *In the background* section.

## Is everything being recorded?

`watchme status` shows the agent, the database and a line per source: when the agent last read
it, when its newest event happened and how many it has from the last 7 days. A source that needs
a look says why:

```text
  Source           Last run         Last event         7 days
  Focus            —                20:03               2,448
  Edge             20:11            20:03               2,975
  PowerShell       20:11            20:02                  51
  Total Commander  20:11            16:07                 507  ! no log file set in wincmd.ini; turn on Total Commander's log under Configuration → Log file
  Git              20:10            20:01                 231  ✕ failing since 19:40: IOException: …
```

*Failing* means the last read threw an error (the full error is in `agent.log`); *has not run
since* means a source stopped being read while the agent runs; the other notes say why a source
had nothing to read (a missing file, the PowerShell hook not installed). When the agent is
stopped, status says when it was last seen. The dashboard shows the same problems in a banner at
the top, along with a stopped agent or a pause.

## Pausing

```powershell
watchme pause          # 30 minutes
watchme pause 2h       # or 45 (minutes), 1h30m, or a time: 14:30
watchme resume         # start again sooner
```

For screen sharing, banking, or someone else at the keyboard. Nothing is recorded until the pause
ends, and it ends by itself (at most 24 hours later), so forgetting to turn tracking back on costs
nothing. It covers more than window focus and processes: what apps log themselves during the
pause (Edge history, PowerShell commands, plays, file operations) is left out too, even when it
is read later, and even if the agent wasn't running. Pausing again changes the end time. The
report and the dashboard's day timeline show when you paused.

You don't need a terminal for it: the [tray icon](#tray-icon) and the dashboard's **⏸ Pause**
menu pause for 15 minutes to 2 hours, and the dashboard's pause banner has **▶ Resume now**.

## Tray icon

While the agent runs, Watchme sits in the notification area next to the clock:

| icon | means |
|------|-------|
| blue eye | recording |
| grey pause sign | paused; the tooltip says until when and how long is left |
| amber badge | a source needs a look (it stopped being read, or has nothing to read) |
| red badge | a source is failing |

Its menu has:

- **Pause for** 15 minutes, 30 minutes, 1 hour or 2 hours, and **Resume recording** while paused.
  It's the same pause as `watchme pause`.
- The sources that need a look, each with what is wrong. Click one to open `agent.log`.
- **Open dashboard** (or double-click the icon). This starts the dashboard without a terminal
  window the first time and opens it in your browser. The server stops when the agent does.
- **Forget the last 15 minutes…** is `watchme forget 15m` (see [Forgetting](#forgetting)), after
  saying how many events it removes.
- **Copy today's report** puts the text of `watchme report` on the clipboard.
- **Edit settings (config.json)**, **Open data folder** and **Quit Watchme** (stops recording until
  your next logon or `watchme start`).

If you'd rather not have the icon, set `"trayIcon": false` in `config.json`.

## Forgetting

For when you forgot to pause: `watchme forget` takes things out afterwards.

```powershell
watchme forget 15m                              # the last 15 minutes (or 2h, 1h30m, 45)
watchme forget 14:10..14:35                     # today; 14:10..now works too
watchme forget 22:00..24:00 --day yesterday     # another day (YYYY-MM-DD works too)
watchme forget --match "mybank" --days 30       # every event holding these words
watchme forget 2h --match "mybank"              # ...only in the last 2 hours
watchme forget 1h --source edge                 # one source only
watchme forget 14:10..14:35 --redact            # keep the events, without titles and text
```

It lists what it will do, with a few of the events, and asks before changing anything (`--yes`
skips the question; it is needed when the input isn't a terminal). It can't be undone.

- **A stretch of time** is removed from every source: window titles, processes, pages, commands,
  plays, file operations. The time stops counting as active (Active in the report, the
  dashboard), while idle, lock and agent start/stop stay so the rest of the day adds up. What
  apps logged in that time but Watchme hasn't read yet (Edge writes its history late, or the
  agent wasn't running) is left out when it is read, like during a pause. The report says
  `Forgot 14:10–14:35`, and the dashboard's day timeline marks it.
- **Words** (`--match`, repeat it for several) match an event's title and its data: pages,
  commands, folders, artists, prompts. Matching app history is removed. Matching window titles
  are blanked to `[redacted]` but keep their time, so the time isn't handed to another app.
- **`--redact`** keeps the events and their time but replaces titles and text in their data
  with `[redacted]`; numbers (durations, ratings) stay. With a stretch of time, app history read
  later is redacted the same way.

Removed app history stays removed: the next read of Edge's history, the PowerShell log or Aurora's
plays won't bring it back, even though those apps still have it. To keep a site or a window title
out from now on, add it to `edgePrivateSites` or `redactTitlePatterns` in
[`config.json`](#configuration).

In the dashboard's event log, a row's details have **Forget this event** and **Keep without title
and text** (for a window title: **Blank this title**).

## Several days at once

`watchme report` also takes a range and prints one summary for it:

```powershell
watchme report 7d                       # the last 7 days, today included (any Nd up to 366d)
watchme report week                     # Monday to today; last-week is the whole week before
watchme report month                    # the 1st to today; last-month is the month before
watchme report 2026-11-01..2026-11-15   # any range; 2026-11-01.. runs to today
```

It shows the focus time and how it was spread (per day, or per week for ranges over 31 days),
the top apps with their most-used window, and a line or two per app that logged anything:
tracks, artists and genres; pages and sites; commits per repo and agent time per project;
PowerShell programs; Total Commander operations and library changes; downloads. The numbers
are the same ones the dashboard shows for that range.

## Search

```powershell
watchme search rateyourmusic tiesto        # every word must match somewhere
watchme search "rate your music"           # quotes keep a phrase together
watchme search dotnet --source pwsh --days 7
watchme search watchme --from 2026-11-01 --to 2026-11-15 --limit 200
```

Searches the whole history, newest first: window titles, apps, kinds and each event's data
(URLs, commands and their folders, artists and albums, agent prompts and projects). When the
match is not in the title, the field that matched is printed under it (`url: …`, `cmd: …`), and
matches are highlighted in the terminal (set `NO_COLOR` to turn that off). Process start and
exit are left out unless you ask for them with `--source process`.

| Option | Meaning |
| --- | --- |
| `--source S` | One source: `edge`, `powershell` (`pwsh`), `git`, `t3`, `aurora`, `aurora_ng`, `musiclibrary`, `totalcmd` (`tc`), `flipside`, `forever`, `focus`, … |
| `--days N` | The last N days, today included |
| `--from DAY`, `--to DAY` | A date, `today` or `yesterday` |
| `--limit N` | How many to print (default 50, at most 1000); the total is always shown |

Matching ignores case and accents: `tiesto` finds `Tiësto`, `reggaeton` finds `Reggaetón` and
`oya` finds `Øya`. A word matches inside longer words too (`rate` finds `rateyourmusic.com`).
Searches use a full-text index (SQLite FTS5 with trigrams), so they stay quick however much
history there is; the index makes the database file about 70% bigger. Words of one or two
letters aren't in the index: they are matched by reading the events, ignoring case for English
letters only.

## Dashboard

```powershell
watchme dashboard
```

This starts a small web server on <http://127.0.0.1:7317> (the next free port if that one is taken)
and opens it in your browser. Press Ctrl+C to stop it. `--port N` picks the port and `--no-open`
leaves the browser alone. It reads the same database as the report, so it works whether or not
the agent is running. **Open dashboard** in the [tray icon](#tray-icon)'s menu does the same without a
terminal.

For any range of days (today, yesterday, the last 7, 30 or 90 days, or dates you pick), it shows:

- **Totals:** focus time, songs, pages, agent prompts, commits, commands, ratings, albums added,
  downloads (Flipside and Forever).
- **Focus time per day**, stacked by app (the top 7, the rest as *Other*), with a table view.
- **When:** an hour-of-day grid of focus minutes or app events.
- **Day timeline:** one day's blocks of work by app, idle stretches, and the report's *Activity
  in apps* lines. Drag the slider above the lines to show only part of the day (17:00–19:30,
  say); **Show the whole day** clears it.
- **Apps:** time, share, blocks, hands-on time (for apps you use without focusing them, see
  above) and most-used window per app. Click an app to see its background use (hands-on,
  skips, ratings, playing), its window titles by focus time, and to show only that app's events
  in the event log.
- **Activity in apps:** events per day (or per hour, for a single day), one small chart per source.
- **Music, Web, Code, PowerShell, Files:** top artists, albums, genres, ratings given and tracks loved; sites by
  pages and your searches; commits per repo, agent time per project and model; programs and
  folders; Total Commander operations and library changes.
- **Event log:** the raw events, newest first, filterable by source and app. The search matches
  titles and the event's data too (URLs, commands, folders, artists), ignoring case and accents,
  like [`watchme search`](#search). Click a row to see
  everything recorded with it, and to forget or redact that event (see [Forgetting](#forgetting)).

Click a day in *Focus time per day* or *Activity in apps* to open it in the day timeline. Click
an hour in *When* (or in a single day's *Activity in apps* charts) to open that day with its
activity list set to that hour.
**⏸ Pause** in the header pauses recording for 15 minutes to 2 hours, and the pause banner's
**▶ Resume now** ends it (see [Pausing](#pausing)).
**↻ Refresh** reloads the data; with **Auto** ticked the page refreshes itself every minute while
the range includes today, adding new events to the top of the log without losing your place.

The page is built into `watchme.exe` and loads nothing from the internet. The server only listens
on the loopback address and only answers requests addressed to `127.0.0.1` or `localhost`, so a
web page can't read your data through a DNS rebinding trick. Pausing and resuming only work from
the dashboard page itself; other sites can't pause your recording. The range, the day and the app filter
are kept in the address (`#from=…&to=…&day=…&app=…`), so a view can be bookmarked. The theme follows Windows; the ◐ button switches it.

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

-- Events holding a word anywhere (case and accents ignored; 3 letters or more)
SELECT * FROM events_local
WHERE id IN (SELECT rowid FROM events_fts WHERE events_fts MATCH '"tiesto"');
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
| `flipsideEnabled`     | `true`  | Read Flipside's activity log (searches, downloads) |
| `flipsideDataDir`     | `null`  | `null` means `%APPDATA%\com.soundtrackgeek.flipside` |
| `foreverEnabled`      | `true`  | Read Forever's activity log (searches, browsed users, Wanted, downloads) |
| `foreverDataDir`      | `null`  | `null` means `%APPDATA%\com.soundtrackgeek.forever` |
| `scrapeBackfillDays`  | 7       | History imported from apps on first run |
| `trayIcon`            | `true`  | Show the [tray icon](#tray-icon) while the agent runs |

Settings missing from an older `config.json` use their defaults.

Set `WATCHME_HOME` to use a different data folder (handy for testing).

## Privacy

Everything stays in the local database. Window titles can still contain sensitive text
(email subjects, document names), so extend the redaction lists to taste, and use
`watchme pause` for the stretches that shouldn't be recorded at all. When something slipped
through anyway, `watchme forget` takes it out afterwards, app history included.

## Development

```powershell
dotnet build
dotnet test
```

Projects: `Watchme.Core` (schema, config, report, dashboard data), `Watchme.Agent` (the windowless
collector), `Watchme.Cli` (`watchme.exe`, and the dashboard's server and page in `dashboard.html`),
`Watchme.Tests`.
