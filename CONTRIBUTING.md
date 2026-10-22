# Contributing to PlayMode

Thanks for helping. PlayMode is a small project run in spare time, so clear reports and focused pull requests make the
biggest difference.

## Reporting a bug

[Open an issue](https://github.com/ralphk-86/PlayMode/issues/new/choose) using the bug template. Most useful:

- what you did, what you expected, and what happened instead;
- your Windows version and PlayMode version (`playmodectl --version`, or Settings → Apps → Installed apps);
- the relevant part of `%LOCALAPPDATA%\PlayMode\playmode.log`, and for service problems
  `C:\ProgramData\PlayMode\helper.log`.

Logs and configs can contain app names and file paths from your PC. Look them over before posting.

## Adding an app to the catalog

The catalog of known apps is [src/PlayMode.Core/Discovery/catalog.json](src/PlayMode.Core/Discovery/catalog.json).
It's the easiest way to contribute, and it helps everyone who uses the same app.

- **Only startup flags you've checked.** Start the app the way the entry says, make sure it comes up the way it does
  at sign-in (usually minimised or in the tray), and say in the pull request how you checked. A wrong flag means a
  window popping up in the middle of someone's game.
- Prefer `close` for apps that save their state, and `suspend` (freeze) for ones that would lose work, like IDEs.
- Mark apps with live work in them (terminals, agents, anything holding a session) as `risky`, so PlayMode asks first.
- Services: only list ones that are safe to stop and start again, and never anything games, anti-cheat or Windows
  need.

`dotnet test` checks that the catalog loads and is consistent.

## Pull requests

1. For anything bigger than a fix or a catalog entry, open an issue first so we can agree on the approach.
2. Keep pull requests focused: one change, with tests for engine or discovery logic.
3. `dotnet build` and `dotnet test` must pass. Warnings are errors.
4. Follow the style of the surrounding code; `.editorconfig` covers the basics.
5. Describe what you tested on a real machine, especially for anything that closes, starts or suspends programs.

### Ground rules for the code

- **Never touch Windows itself.** Anything that decides what to close goes through `ProcessMatcher.IsNeverTouch`,
  which keeps Windows' own processes off-limits.
- **The elevated helper stays small.** `PlayMode.Helper` runs as SYSTEM. Changes to it, to `HelperContract.cs`, or to
  how setup installs it get extra scrutiny; it must keep acting only on the intersection of the request and the
  approved allowlist.
- **No telemetry.** The only network calls are the update check and download (GitHub only), and the connectivity
  check after stopping Proton VPN. Anything new needs a good reason and a line in the README's network section.
- Windows specifics live in `Platform/Windows`, behind the interfaces in `Platform/Abstractions.cs`, so the engine
  can be tested with the fakes in `tests/`.

## Releasing (maintainers)

1. Update `CHANGELOG.md` with a section for the new version, and `<Version>` in `Directory.Build.props`.
2. Tag and push: `git tag v0.2.0 && git push origin v0.2.0`. The release workflow builds, tests and publishes the
   installer, zip and checksums.
3. Update the winget manifest (`wingetcreate update ralphk-86.PlayMode --version 0.2.0 --urls <installer url> --submit`).

## Code of conduct

Be kind and assume good intent. Harassment or abuse gets you removed from the project.
