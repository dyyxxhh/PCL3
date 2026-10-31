[简体中文](README.md) | **English** | [繁體中文](README-ZH_TW.md)

<div align="center">

<img src="Plain Craft Launcher 2/Images/icon.png" alt="PCL₃" width="80" height="80">

# PCL₃ (Phosphorus Trichloride Launcher)

**PCL₃** — phosphorus trichloride, a colourless liquid used to make organophosphorus compounds.

A Minecraft: Java Edition launcher, forked from [PCL CE](https://github.com/PCL-Community/PCL-CE) (Plain Craft Launcher Community Edition).

[![GitHub Release](https://img.shields.io/github/v/release/dyyxxhh/pcl3?label=release&logo=github&style=for-the-badge)](https://github.com/dyyxxhh/pcl3/releases/latest)
[![GitHub Actions Workflow Status](https://img.shields.io/github/actions/workflow/status/dyyxxhh/pcl3/build-test.yml?style=for-the-badge)](https://github.com/dyyxxhh/pcl3/actions)

[Download](https://github.com/dyyxxhh/pcl3/releases/latest) | [Report an issue](https://github.com/dyyxxhh/pcl3/issues/new/choose)

</div>

## What is this

PCL₃ is a personal fork of [PCL CE](https://github.com/PCL-Community/PCL-CE). Since 2.12.x, upstream PCL CE gates features by region (geo/region detection) and by account type. This fork removes those gates and rebrands the launcher.

**Please do not report issues with this fork to the PCL CE repository.**

## What this fork changes

| Change | Detail |
|---|---|
| Region restrictions removed | `RegionPolicy` / `RestrictedFeaturePolicy` always evaluate as unrestricted, so Minecraft, modpacks and loaders can be downloaded and installed from mainland China |
| Account restrictions removed | No Microsoft account required: offline and third-party (authlib-injector) profiles can be created at any time (`ProfileUi.CanCreateOtherProfile` always `true`) |
| Purchase prompts removed | No "buy the game / verify your account" dialog before launch, and client files are no longer skipped in restricted regions |
| Rebranded | Launcher name, title bar, AUMID, assembly metadata and all 7 locale files say PCL₃; new launcher icon |
| Independent versioning | Numbered from 0.1.0; `metadata.json` records the upstream version it is based on |

## What upstream (PCL CE) provides

Everything below is inherited unchanged from upstream:

- **Launcher core**: download and install Minecraft: Java Edition, modpacks (CurseForge / Modrinth / MCBBS-style sources) and loaders (Forge / NeoForge / Fabric / Quilt / OptiFine)
- **Accounts**: Microsoft, offline, third-party auth servers (authlib-injector), unified sign-in
- **Instance management**: multiple isolated instances, mod management with Jar-in-Jar dependency analysis, worlds / screenshots / server lists
- **Download sources**: official, BMCLAPI and injector mirrors; search mods, modpacks, resource packs and shaders
- **Extras**: Java management, memory tuning, crash analysis, custom themes / BGM / effects, EasyTier multiplayer, launch optimisations

See the [PCL CE repository](https://github.com/PCL-Community/PCL-CE) for details.

## Supported platforms

| Operating System | Support | Requirements |
|---|---|---|
| Windows 10 1809 (17763) or later | ✅ Fully supported | [.NET 10 Desktop Runtime](https://get.dot.net/10) |
| Windows 8 to Windows 10 1809 (17763) | ⚠️ Expected to run | [.NET 10 Desktop Runtime](https://get.dot.net/10) |
| Windows 7 or earlier | ❌ Not supported | N/A |
| macOS / Linux | Cross-compilation only | [.NET 10 SDK](https://get.dot.net/10) |

Releases provide framework-dependent x64 and ARM64 builds; the .NET 10 Desktop Runtime must be installed.

## Staying in sync with upstream

This fork tracks upstream `dev` (the exact upstream commit is noted on each [release](https://github.com/dyyxxhh/pcl3/releases)). When upstream moves, re-apply the fork changes listed above.

## License

- `Plain Craft Launcher 2/` uses the [Custom License](Plain%20Craft%20Launcher%202/LICENCE) inherited from PCL CE (non-commercial, among other terms)
- `All other directories` use [Apache License 2.0](LICENSE)

PCL CE is maintained by [PCL Community](https://github.com/PCL-Community); the original PCL is by [龙腾猫跃](https://github.com/Meloong-Git/PCL). Thanks to them.
