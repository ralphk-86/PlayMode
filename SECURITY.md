# Security policy

PlayMode closes and suspends programs, and its optional service helper runs as SYSTEM. If you find a way to make it
do something it shouldn't, we want to know.

## Reporting a vulnerability

Please **don't open a public issue**. Report it privately through GitHub:
[Security → Report a vulnerability](https://github.com/ralphk-86/PlayMode/security/advisories/new).

Include what an attacker needs (for example, a standard user account on the same PC), the steps, and what they gain.
We aim to reply within a week. Once a fix is released, we'll credit you in the advisory unless you'd rather not
be named.

## What's in scope

The most interesting part is the elevated service helper (`src/PlayMode.Helper`) and how it's set up:

- getting the helper to stop or start a service that isn't on the allowlist the user approved;
- getting it to run code or write files as SYSTEM;
- triggering its scheduled tasks from an account other than the one that set it up;
- tampering with the helper, its allowlist or its request files without admin rights.

Also in scope: making the tray app or `playmodectl` close or suspend processes it should never touch, or start
something other than what the user's rules say.

## Supported versions

Security fixes go into the latest release.
