# Changelog

All notable changes to PlayMode are listed here. The format follows [Keep a Changelog](https://keepachangelog.com/),
and versions follow [Semantic Versioning](https://semver.org/).

## [0.1.0] - 2026-10-22

The first public release.

### Added

- Tray app: click to switch between dev mode and play mode. A blue DEV computer means dev mode, a red GAME one means
  play mode. Asks before closing anything with live work in it (WSL, terminals, coding agents).
- Engine: closes or freezes the apps in your profile, stops WSL cleanly, disconnects Tailscale and reconnects it with
  the same settings, and restores exactly what was running, the way it normally starts. Survives a reboot or crash
  while in play mode.
- Settings window that builds a starting profile from what's installed and running, with a catalog of 37 known apps,
  multiple profiles, services to stop, and a never-touch list.
- Service control without a UAC prompt on every switch: a small helper, approved once, that can only touch the
  services you picked.
- `playmodectl` command line: `status`, `on`, `off`, `tray`, `scan`, `setup`, `remove-helper`.
- Updates: a daily check for a new GitHub release (off switch in the tray menu), then one-click install that verifies
  the download against the release's checksums and restarts the tray.
- Per-user installer (no admin needed) and a portable zip.

[0.1.0]: https://github.com/ralphk-86/PlayMode/releases/tag/v0.1.0
