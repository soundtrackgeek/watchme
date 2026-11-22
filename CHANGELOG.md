# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project uses
[Semantic Versioning](https://semver.org/).

## [0.7.0] - 2026-11-22

### Added

- Aurora NG scraper: tracks played in Aurora NG, from its own listening history file, which
  Aurora only sees through OneDrive (so they were missing before).
- Music Library scraper: rating changes from Music Library's `rating_events`, whichever app made
  them; the report shows one line per album rated in one sitting.
- Config: `auroraNgEnabled`, `auroraNgHistoryDb`, `musicLibraryEnabled`, `musicLibraryDb`.

### Changed

- Listens are grouped per album folder, so tracks with guest artists no longer split an album
  into several lines.

## [0.6.0] - 2026-11-22

### Added

- Git scraper: reads the reflogs of repos under `gitRoots` (and their worktrees) without running
  git, and records commits, pushes, pulls, merges, checkouts, resets, rebases and clones with
  the branch they happened on. Also records new repos and new folders in those roots.
- Report: commits to one branch close together become one line with the push that followed;
  a new folder turned into a repo shows as "Created folder X and started a git repo".
- Config: `gitEnabled`, `gitRoots`.

## [0.5.0] - 2026-11-22

### Added

- T3 Code scraper: one event per finished run (prompt) for every provider (Claude Code, Codex,
  Antigravity, ACP agents) with model, effort, fast mode, project, thread, branch, duration,
  status, subagent flag and the start of the prompt.
- Report groups runs into thread sessions: prompts, models, agent working time and the time
  that thread's window was in front of you.
- Config: `t3Enabled`, `t3StateDb`.

### Fixed

- README scrapers table was split in two by a blank line.

## [0.4.0] - 2026-11-22

### Added

- Total Commander scraper: reads the file operation log (location from `wincmd.ini`, including
  rotated copies) and records copies, moves, renames, deletes and folder changes. Related lines
  are grouped into one action, e.g. a whole folder copy; errors are called out in the title.
- Config: `totalCommanderEnabled`, `totalCommanderIni`.
- Report shows friendly source names (e.g. "Total Commander").

### Fixed

- Scrapers no longer re-send (and log) events they already sent this session when re-reading
  an overlapping window.

## [0.3.0] - 2026-11-22

### Added

- App scrapers: a framework that reads apps' own data on an interval, with duplicate-safe
  re-reads through a new `ext_id` column (schema v2, migrated automatically).
- Aurora scraper: listens (with play/skip outcome), tag edits grouped per album with
  before → after values, and album additions. Imports the last 7 days on first run.
- Report section "Activity in apps"; consecutive listens to one album collapse into one line.
- Config: `auroraEnabled`, `auroraDataDir`, `scrapeBackfillDays`.

### Changed

- The report no longer hides short use: apps under 30 seconds are summed into an `other`
  line, and timeline blocks under a minute into a `shorter visits` line.

## [0.2.0] - 2026-11-22

### Added

- `watchme install` adds the install folder to the user PATH, and `watchme uninstall` removes it.
  Existing `%VAR%` entries in PATH are preserved unexpanded, and the change is broadcast so new
  terminals pick it up.
- `watchme status` shows whether watchme is on the PATH.

## [0.1.0] - 2026-11-22

### Added

- Background agent (`Watchme.Agent.exe`) that records, into a local SQLite database:
  foreground window changes and stable title changes, idle periods (backdated to the last
  input), lock/unlock, sleep/resume, logoff/shutdown, and process starts/exits in the user session.
- UWP apps are attributed to the hosted app instead of ApplicationFrameHost.exe.
- `focus_spans` and `events_local` views; durations are derived, not stored.
- Heartbeat and unclean-shutdown recovery so a crash or power loss does not inflate the last span.
- Title redaction for password managers and private browsing windows; configurable ignore lists.
- `watchme` CLI: `report`, `status`, `start`, `stop`, `install` (logon via the HKCU Run key), `uninstall`.
