# Windows Offline Installation

Windows offline bundles use the same versioned ZIP transport with a Windows-specific layout. Export from a Windows PCL₃ release matching the target architecture. The package contains one prepared version, its Java runtime, and the self-contained PCL₃ executable; it excludes accounts, saves, logs, and launcher settings.

Windows releases ship two launcher variants per architecture. The framework-dependent build (`PCL3_Release_<arch>.exe`, about 22 MB) requires the .NET 10 Desktop Runtime; the compressed self-contained build (`PCL3_Release_<arch>_selfcontained.exe`, about 98 MB) needs no .NET installation. Offline export works from either build: the embedded launcher must be self-contained, so a framework-dependent launcher first downloads the matching `_selfcontained` asset of its own version from GitHub Releases, verifies the published `.sha256`, and caches it under `PCL\Offline\`; a self-contained build embeds itself. The first export from a framework-dependent build therefore needs network access to GitHub, while later exports reuse the cache. If the download cannot be verified the export is rejected instead of embedding the framework-dependent executable.

On the source computer, use the version export page's **完整 Windows 离线包** control. The launcher verifies local dependencies, builds the ZIP, starts a read-only server on the selected private LAN address, and displays copyable PowerShell and CMD commands. On the target computer, either command downloads and verifies the launcher and package before invoking the offline importer. After a successful import, PCL₃ prompts for an offline player name in an empty input field, never using the Windows username or an existing account as the default. Enter a name with 3–16 ASCII letters, digits, or underscores to start the newly installed instance in offline mode. If PCL₃ is already open, confirm the displayed instance path in that window before entering the name. Cancelling either prompt leaves the installation intact without logging in or starting the game.

For the original install-only behavior, use the `/direct` PowerShell or CMD command shown by the exporter (it fetches `/direct` instead of `/install.ps1`). It does not ask for a name, change the selected account, or start the game. No Java, .NET, curl, Bash, administrator rights, or permanent execution-policy changes are required on the target. Use the service only on a trusted LAN; the checksum detects corruption, not a malicious server.

The current importer installs the version under `<game directory>\versions\<instance name>`, places its Java under the version folder, and merges libraries/assets only when existing content has matching SHA-256. Conflicts and existing version names are rejected. The offline import command is `PCL3.exe offline-import --bundle <zip> --game-dir <directory> [--instance <name>]`.

PowerShell and CMD bootstrap commands use the same generated script. Optional script parameters are `-InstallDirectory`, `-GameDirectory`, `-InstanceName`, and `-Connections` (1 through 8). The default is four connections. LAN server transfers support GET/HEAD and a single byte range; only fixed routes are served. Network drivers and graphics capabilities remain target-system requirements. The current integration does not enable the install button for unsupported versions or architectures until their dependency closure can be validated.

## Windows release acceptance

Use a disposable Windows account or VM snapshot and a small known-good bundle. Keep the LAN reachable to install, but disconnect from the public internet to verify offline launch. Use a fresh instance name for each successful import.

| Case | Expected result |
| --- | --- |
| Default command in Windows PowerShell 5.1, PowerShell 7, and CMD | The bundle imports. After first-run consent, the name field is blank even if Windows has a different username. Blank or invalid input cannot start the game. A valid name starts the imported instance with its bundled Java. |
| PCL₃ already open with another instance and account selected; target game path contains spaces | The existing window asks to confirm the imported instance path. After entering a different, valid offline name, the imported instance starts, not the previously selected one. |
| Cancel confirmation or name entry | The imported files remain, the previous account stays selected, and the game does not start. |
| Repeat install with a name that already exists | Import fails without overwriting the instance or opening a name prompt. |
| `/direct` command in PowerShell and CMD | Import succeeds without opening a name prompt, changing the active account, or launching the game. |

Linux-hosted route tests and a Windows cross-build are useful regressions but do not replace these checks.
