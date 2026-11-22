# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project uses
[Semantic Versioning](https://semver.org/).

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
