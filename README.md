# Watchme

A small Windows agent that starts at logon and records what you do on the computer — which
app is in front, what its window says, when you go idle, which programs you open — into a
local SQLite database. No screenshots, no keystrokes.

This is step 1 of a layered plan:

1. **Focus, idle, sessions and processes** (this release) — works for every app with no per-app code.
2. App-native data sources (Total Commander's operation log, git, Claude/T3 sessions, browser history).
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

Timeline (blocks of 1 minute or more)
  14:04–14:49      45m 02s  T3 Code (Beta).exe        Watchme / Activity monitor
  ...
```

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
