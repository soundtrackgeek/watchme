# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project uses
[Semantic Versioning](https://semver.org/).

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
