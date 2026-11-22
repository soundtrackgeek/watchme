# Watchme

A small Windows agent that starts at logon and records what you do on the computer — which
app is in front, what its window says, when you go idle, which programs you open — into a
local SQLite database. No screenshots, no keystrokes.

It also reads what some apps already record about themselves (see [App scrapers](#app-scrapers)),
so it can tell you not just *that* you used an app but *what you did* in it.

This is a layered plan:

1. **Focus, idle, sessions and processes** — works for every app with no per-app code.
2. **App-native data sources** — Aurora and Total Commander so far; next git and Claude/T3 sessions.
3. A local endpoint your own apps can push events to.
4. OS-level file attribution (ETW) and UI Automation scraping where nothing else works.

## What gets recorded

| source    | kind                                       | meaning |
|-----------|--------------------------------------------|---------|
| `focus`   | `foreground`                               | A different window came to the front (app, exe, pid, title) |
| `focus`   | `title`                                    | The focused window changed title and kept it for 2 s (new file, tab, track...) |
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

| `totalcmd` | `file_op`   | Total Commander's log file (path read from `wincmd.ini`) | `Copied 3 items from c:\photos to d:\backup\photos (created 2 folders)` |

Track and disc numbering changes are kept in `data.changes` but shortened to "numbering" in titles.

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
| `totalCommanderEnabled` | `true` | Read Total Commander's file operation log |
| `totalCommanderIni`   | `null`  | Path to `wincmd.ini`; `null` means `%APPDATA%\GHISLER\wincmd.ini` |
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

Projects: `Watchme.Core` (schema, config, report), `Watchme.Agent` (the windowless collector),
`Watchme.Cli` (`watchme.exe`), `Watchme.Tests`.
