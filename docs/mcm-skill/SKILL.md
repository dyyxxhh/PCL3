---
name: mcm
description: Operate the MCM Minecraft Java launcher on Linux. Use for MCM installation, game launch, mods, packs, Java, accounts, configuration, backups, and troubleshooting.
---
# MCM (Minecraft Manager) operations

Use MCM as the source of truth for Minecraft instances on Linux. Do not invent paths, credentials, or downloads. Ask the user before destructive actions such as removing mods, restoring a backup in place, or changing accounts.

## Install, discover, and launch
- Download the official `MCM_Release_linux-x64.tar.gz` and its `.sha256` from the PCL3 GitHub release. Verify the checksum before extracting the `mcm` executable to a user-writable directory on `PATH`.
- Update an official installed build with `mcm update`. Use `mcm --help` to verify commands supported by older versions.
- `mcm info`
- `mcm instance list [--game-dir PATH]`
- `mcm launch INSTANCE [--game-dir PATH] [--java PATH] [--offline]`
- `mcm scan PATH --migrate --version VERSION --instance NAME --game-dir PATH`

## Minecraft, Java, mods, and packs
- Install a version: `mcm -S VERSION --instance NAME [--loader fabric|quilt|forge|neoforge] [--game-dir PATH]`
- Search mods: `mcm -Ss QUERY [--game-version VERSION] [--loader LOADER] [--source modrinth|curseforge]`
- Install a mod: `mcm -S MOD_OR_PROJECT --instance NAME --game-dir PATH`
- Manage mods: `mcm mod list --instance NAME` or `mcm mod update|enable|disable|remove FILE.jar --instance NAME`
- Import/export packs: `mcm pack import|export FILE --instance NAME`
- Required dependencies are installed automatically; optional dependencies are not. If a dependency cannot be resolved, stop and explain the choice instead of silently installing an incompatible file.
- Java: `mcm java scan` and `mcm java install MAJOR`

## Accounts and offline use
- Microsoft: `mcm login`
- Offline: `mcm login offline PLAYER_NAME`
- Authlib: `mcm login authlib SERVER USERNAME`
- Sign out: `mcm logout`
- `--offline` must not refresh online accounts or download missing files.

## Configuration and recovery
- `mcm config`, `mcm config list`, `mcm config get KEY`, `mcm config set KEY VALUE`, `mcm config reset [KEY]`
- `mcm backup create INSTANCE`, `mcm backup list INSTANCE`, `mcm backup restore BACKUP --instance INSTANCE [--clone]`
- Automatic backups are disabled by default. Treat a backup path and a restore target as untrusted input; never follow links outside the MCM backup root.
- Before an in-place restore, preserve the current instance. Prefer restoring as a new instance when inspecting an old state.

## Network troubleshooting
- `mcm tor status` shows configuration and `mcm tor test` tests the connection. `mcm tor bridge list` gives redacted indexes for `mcm tor bridge remove NUMBER`.
- When automatic backups are enabled, choose `backup.scope` (`saves` or `instance`) and `backup.retention` with `mcm config set`. Use `--instance NAME` for an override.
MCM uses HTTPS and verifies downloaded content. Keep accounts, tokens, passwords, cookies, and bridge strings out of prompts and logs. If a configured Tor fallback is available, use it only for public resource queries and downloads, not account refresh or game networking. Report the exact failed resource and suggest `mcm config` or a retry instead of looping forever.
