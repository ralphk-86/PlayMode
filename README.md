# PlayMode

**One switch between your dev setup and your gaming setup on Windows.**

PlayMode closes (or freezes) the dev and background apps you choose — WSL, Docker, IDEs, sync clients, VPNs,
indexers — when you sit down to play, and brings back *exactly what was running* when you're done.

[![CI](https://github.com/ralphk-86/PlayMode/actions/workflows/ci.yml/badge.svg)](https://github.com/ralphk-86/PlayMode/actions/workflows/ci.yml)
[![Release](https://img.shields.io/github/v/release/ralphk-86/PlayMode)](https://github.com/ralphk-86/PlayMode/releases/latest)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue)](LICENSE)

> **Status: 0.1, early but working.** The tray app, settings window, engine and service control are in daily use on
> our own machines. A global hotkey and switching automatically when a game starts are next. See the
> [roadmap](#roadmap).

**Contents:** [Install](#install) · [Using it](#using-it) · [Stopping services safely](#stopping-services-safely) ·
[What it changes on your PC](#what-playmode-changes-on-your-pc) · [Uninstall](#uninstall) ·
[Troubleshooting](#troubleshooting) · [Building from source](#building-from-source)

## Why we built it

My son and I both write code and play games, on the same PCs. During the day those machines are dev rigs: a couple of
WSL distros, coding agents, terminals, sync clients, a VPN or two, and a pile of tray utilities. In the evening we just
want to play.

Getting from one to the other was a chore. You either play with all of that still running in the background — WSL
alone was holding a few gigabytes of RAM — or you spend five minutes closing things by hand, then forget half of them
the next morning. Game boosters didn't help: they're made for gaming-only PCs, they don't know what WSL or a coding
agent is, and they don't put anything back afterwards.

So we made a switch. Click it before you play and PlayMode puts the dev stuff away. Click it again when you're done
and your workspace comes back the way you left it.

## What makes it different

- **It puts things back exactly.** Only the apps that were actually running come back, started the way they normally
  start (minimized, in the tray), and any windows they pop up get tidied away. If the PC reboots or crashes while
  you're playing, PlayMode remembers what to restore.
- **It understands dev tools.** It shuts WSL down cleanly, disconnects Tailscale and reconnects it afterwards with
  the same settings, and **asks before** closing anything with live work in it, like terminals, WSL or coding agents.
- **It can freeze instead of close.** Suspend an IDE so it uses no CPU while you play, then pick up exactly where you
  left off.
- **It never touches Windows itself.** Critical system processes are off-limits, whatever a rule says.

### What it will (and won't) do for performance

Let's be honest: on a modern PC, closing background apps rarely adds many frames per second. What it does do is free
up RAM, stop background work (indexing, syncing, updaters) that causes stutter at the worst moment, keep VPN latency
out of your game traffic, and silence notifications. If an app promises +30% FPS from this kind of thing, be
skeptical.

## Install

You need Windows 10 (version 2004 or later) or Windows 11 on x64, and the
[.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0). The installer tells you if it's
missing, and winget installs it for you.

- **Installer** (recommended): download `PlayMode-<version>-setup-x64.exe` from the
  [latest release](https://github.com/ralphk-86/PlayMode/releases/latest) and run it. It installs for your account
  only, so it doesn't need admin rights, and puts PlayMode in `%LOCALAPPDATA%\Programs\PlayMode` with a Start menu
  entry. You can choose whether it starts with Windows and whether `playmodectl` goes on your PATH.
- **winget** (once the package has been approved): `winget install ralphk-86.PlayMode`
- **Portable zip**: unzip `PlayMode-<version>-win-x64.zip` anywhere and run `PlayMode.exe`.
- **From source**: see [Building from source](#building-from-source).

Each release lists SHA-256 checksums in `SHA256SUMS.txt`. The builds aren't code-signed yet, so Windows SmartScreen
may say it doesn't recognise the publisher the first time you run the installer: choose *More info* → *Run anyway*,
or build it yourself.

On first run, PlayMode opens its settings window with a suggested profile. Look it over, save, and you're ready. If
your profile stops any services, the tray menu offers the one-time service-control setup described
[below](#stopping-services-safely).

## Using it

PlayMode lives in the notification area. **Click the icon** to switch: a blue DEV computer is dev mode, a red GAME one is play
mode, amber while it's switching. If something with live work is running (WSL, a terminal, a coding agent), it asks
first. Right-click for the rest: preview what would change, pick a profile, see the last report, start with Windows.

### Settings

The first time it runs, PlayMode looks at what's installed and running and opens its settings window with a starting
profile: the dev tools, sync clients and utilities it recognises, and your game launchers and voice chat on the
never-touch list. Nothing is saved until you've looked it over.

- **Add apps** shows what's running now, what starts with Windows, and a catalog of known apps. Known apps come
  with settings that suit them (VS Code is frozen rather than closed, Spotify comes back minimised to the tray,
  Proton VPN's services stop with it). For anything else, PlayMode works out a rule from what it sees: the app's
  processes, and how Windows starts it at sign-in, so it comes back the same way.
- **Every rule is spelled out** in plain words ("Close, reopen with --startup · asks first") and can be changed:
  close or freeze, ask first, how to start it again, and whether to close or hide a window it pops up.
- **Profiles** for different evenings: a competitive profile that closes everything, a casual one that leaves your
  music alone. Pick one from the tray menu.
- **Services** to stop while you play, picked from the ones other vendors installed. Services games and anti-cheat
  need are never offered.

Settings are stored in `%APPDATA%\PlayMode\config.json`, which you can also edit by hand (see
[samples/config.example.json](samples/config.example.json)). Saving from the window keeps the previous file as
`config.json.bak`.

### Known apps

The catalog lives in [catalog.json](src/PlayMode.Core/Discovery/catalog.json). Additions are welcome, with one rule:
only startup flags you've checked. A wrong flag means a window popping up in the middle of a game.

### Command line

Handy for scripts and hotkeys:

```powershell
playmodectl status            # current mode, and what's running from your profile
playmodectl on --dry-run      # what switching on would affect
playmodectl on | off          # switch directly
playmodectl tray toggle       # ask the running tray to switch (with its usual prompts)
playmodectl scan              # what PlayMode finds on this PC, and what it would suggest
```

## Stopping services safely

Some of the background work worth pausing runs as Windows services (search indexing, updaters, VPN services), and
stopping a service needs admin rights. PlayMode doesn't want a UAC prompt every time you switch, and it doesn't want
to run as admin all the time either. So the admin part is kept small and fenced in:

- **One-time setup, one UAC prompt.** You approve the exact list of services from your settings. A tiny helper
  (`PlayMode.Helper.exe`) and that list are installed to `C:\Program Files\PlayMode\Helper`, which only admins can
  change.
- **The helper only runs when asked**, through two on-demand scheduled tasks that run it as SYSTEM. Only your
  account can trigger them; there are no triggers, no background service, nothing listening on the network.
- **Requests can't widen what you approved.** The app writes a request file that only your account can write; the
  helper acts on the intersection of that request and the approved list, and ignores anything malformed or
  oversized. Asking it to stop, say, the print spooler gets a polite refusal.
- **Changing the list means approving again.** If you edit the services in your settings, or PlayMode is updated,
  the tray offers to re-run setup.

`playmodectl remove-helper` undoes all of it.

## What PlayMode changes on your PC

PlayMode closes, freezes and restarts other programs and can stop services, which is exactly the kind of thing you
should be able to check. Here is everything it touches:

| What | Where | When |
|---|---|---|
| The app | `%LOCALAPPDATA%\Programs\PlayMode` | Installed |
| Your settings and profiles | `%APPDATA%\PlayMode\config.json` (and `config.json.bak`) | When you save settings |
| What to restore, and a log | `%LOCALAPPDATA%\PlayMode\state.json`, `playmode.log` | While running |
| Start with Windows | `PlayMode` value under `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` | If you choose it |
| `playmodectl` on PATH | Your user `Path` variable | If you choose it at install |
| Service helper and its allowlist | `C:\Program Files\PlayMode\Helper` | Only after service-control setup |
| Two on-demand scheduled tasks | `\PlayMode\ServicesStop` and `\PlayMode\ServicesStart` | Only after service-control setup |
| Helper requests, results and log | `C:\ProgramData\PlayMode` | Only after service-control setup |

It never touches Windows' own processes, whatever a rule says. There's no telemetry and no update check. The only
network activity is this:

- If you use Tailscale, PlayMode runs Tailscale's own command-line tool to disconnect and reconnect.
- If your profile stops Proton VPN's services, the helper then checks the internet still works by briefly opening a
  connection to 1.1.1.1 or 8.8.8.8, sending no data. If Proton's kill switch has left you offline, it runs Proton's
  own `ProtonVPN.RestoreInternet.exe`.

## Uninstall

Use **Settings → Apps → Installed apps → PlayMode → Uninstall**, or `winget uninstall ralphk-86.PlayMode`.

The uninstaller:

- switches you back to dev mode first if you're in play mode, so nothing is left closed;
- removes service control if you set it up, which asks for admin once;
- asks whether to keep your settings.

If you used the portable zip, run `playmodectl off` and then `playmodectl remove-helper`, and delete the folder.

## Troubleshooting

- **Something didn't come back after play mode.** Run `playmodectl off` (or click the tray icon). PlayMode keeps
  what it closed in `state.json`, so it can restore it even after a crash or reboot.
- **The tray says "PlayMode was updated: set up service control".** After an update the helper in Program Files
  is older than the app, so it needs approving again (one UAC prompt). Until then switching still works, using the
  helper you approved before.
- **An app comes back as a big window, or not at all.** Open settings, select the app, and change how it's started
  again; the rule spells out exactly what PlayMode will run. If it's an app from the catalog, please open an issue.
- **Anything else:** `%LOCALAPPDATA%\PlayMode\playmode.log` says what PlayMode did and why. The service helper logs
  to `C:\ProgramData\PlayMode\helper.log`. Please attach the relevant part when you
  [report a bug](https://github.com/ralphk-86/PlayMode/issues/new/choose).

### Antivirus

PlayMode legitimately does things malware also does: it closes and suspends other programs, stops services, and
installs a scheduled task that runs as SYSTEM. If your antivirus flags it, please report it as a false positive to
the vendor and [open an issue](https://github.com/ralphk-86/PlayMode/issues). The source is all here, and you can
build it yourself.

## Roadmap

- [x] Core engine: plan, close/force/suspend, restore, window handling, crash/sign-in recovery, Tailscale, WSL
- [x] Command line
- [x] Tray app and service control without a UAC prompt each time
- [x] Settings window: pick apps from what's running and installed, with a catalog of known apps; profiles
- [x] Installer and GitHub releases
- [ ] winget package
- [ ] Global hotkey, auto-switch when a game starts
- [ ] Code-signed releases

## Building from source

You need Windows and the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```powershell
git clone https://github.com/ralphk-86/PlayMode.git
cd PlayMode
dotnet build
dotnet test
```

To build what a release ships:

```powershell
./build/publish.ps1 -Version 0.1.0                  # app, playmodectl and helper in artifacts\publish
iscc /DAppVersion=0.1.0 installer\PlayMode.iss      # installer in artifacts\ (needs Inno Setup 6)
```

Releases are built by [GitHub Actions](.github/workflows/release.yml) when a `v*` tag is pushed.

How it's put together:

| Project | What it is |
|---|---|
| `src/PlayMode.Core` | The engine, rules, discovery and storage. Everything Windows-specific sits behind interfaces, so the logic is unit-tested without touching the machine. |
| `src/PlayMode.App` | The tray app and settings window (WinForms), `PlayMode.exe`. |
| `src/PlayMode.Cli` | `playmodectl`. |
| `src/PlayMode.Helper` | The small elevated service helper. |
| `tests/PlayMode.Core.Tests` | xUnit tests. |

## Contributing

Bug reports, catalog additions and pull requests are welcome. See [CONTRIBUTING.md](CONTRIBUTING.md). For security
issues, please follow [SECURITY.md](SECURITY.md) instead of opening a public issue.

## License

[MIT](LICENSE)
