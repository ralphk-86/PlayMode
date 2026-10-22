# PlayMode

**One switch between your dev setup and your gaming setup on Windows.**

PlayMode closes (or freezes) the dev and background apps you choose — WSL, Docker, IDEs, sync clients, VPNs,
indexers — when you sit down to play, and brings back *exactly what was running* when you're done.

> **Status: early development.** The tray app, engine and service control work and are tested on real machines;
> the settings window, catalog and installer are next. See the [roadmap](#roadmap).

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

## Using it

PlayMode lives in the notification area. **Click the icon** to switch: blue `</>` is dev mode, green 🎮 is play
mode, amber while it's switching. If something with live work is running (WSL, a terminal, a coding agent), it asks
first. Right-click for the rest: preview what would change, pick a profile, see the last report, start with Windows.

Settings live in `%APPDATA%\PlayMode\config.json` (a starter file is created on first run; see
[samples/config.example.json](samples/config.example.json) for what's possible).

There's also a command line, handy for scripts and hotkeys:

```powershell
playmodectl status            # current mode, and what's running from your profile
playmodectl on --dry-run      # what switching on would affect
playmodectl on | off          # switch directly
playmodectl tray toggle       # ask the running tray to switch (with its usual prompts)
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

## Roadmap

- [x] Core engine: plan, close/force/suspend, restore, window handling, crash/sign-in recovery, Tailscale, WSL
- [x] Command line
- [x] Tray app and service control without a UAC prompt each time
- [ ] Settings window: pick apps from what's running and installed, with a catalog of known apps
- [ ] Global hotkey, auto-switch when a game starts
- [ ] Installer, winget package

## Building

Requires the .NET 10 SDK on Windows.

```powershell
dotnet build
dotnet test
```

## License

[MIT](LICENSE)
