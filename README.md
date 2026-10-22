# PlayMode

**One switch between your dev setup and your gaming setup on Windows.**

PlayMode closes (or freezes) the dev and background apps you choose — WSL, Docker, IDEs, sync clients, VPNs,
indexers — when you sit down to play, and brings back *exactly what was running* when you're done.

> **Status: early development.** The core engine and CLI work and are tested; the tray app, settings window and
> installer are in progress. See the [roadmap](#roadmap).

## Why

Game boosters have existed for years, but they're built for a gaming-only PC: they lower priorities or kill a fixed
list and leave you to put things back. If the same machine is your workstation, you want something that:

- **Restores precisely.** Only what was running comes back, started the way it normally starts (minimized, in the
  tray), with pop-up windows put away. A reboot or crash in between doesn't lose track.
- **Understands dev tools.** Shuts WSL down cleanly, disconnects Tailscale (and restates its settings to reconnect),
  and **asks before** closing anything holding live work — terminals, WSL, coding agents.
- **Can freeze instead of close.** Suspend an IDE so it costs no CPU but keeps its state.
- **Never touches Windows itself.** Critical system processes are off-limits whatever a rule says.

### What it will and won't do for performance

On a modern PC, closing background apps rarely adds many frames per second. What it does: frees RAM, removes
background CPU/disk spikes (indexing, sync, updaters) that show up as stutter, takes VPN latency out of game traffic,
and stops notifications. If an app claims +30% FPS from this kind of thing, be skeptical.

## Usage (CLI, for now)

```powershell
playmode status               # what's running from your profile
playmode on --dry-run         # what switching on would affect
playmode on                   # switch (asks first if risky apps are running; --yes to skip)
playmode off                  # restore
```

Configuration lives in `%APPDATA%\PlayMode\config.json`; start from [samples/config.example.json](samples/config.example.json).

## Roadmap

- [x] Core engine: plan, close/force/suspend, restore, window handling, crash/sign-in recovery, Tailscale, WSL
- [x] CLI
- [ ] Tray app and elevated service helper (stop/start services without a UAC prompt each time)
- [ ] Settings window: pick apps from what's running and installed, with a catalog of known apps
- [ ] Multiple profiles in the tray, global hotkey, auto-switch when a game starts
- [ ] Installer, winget package

## Building

Requires the .NET 10 SDK on Windows.

```powershell
dotnet build
dotnet test
```

## License

[MIT](LICENSE)
