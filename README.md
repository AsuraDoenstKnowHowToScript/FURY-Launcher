# FURY Launcher

A lightweight Minecraft launcher focused on doing the basics well: isolated
instances, mod management, and a simple offline/Microsoft login. No clutter.

[![Release](https://img.shields.io/github/v/release/AsuraDoenstKnowHowToScript/FURY-Launcher?include_prereleases&label=release&color=brightgreen)](../../releases)
[![Downloads](https://img.shields.io/github/downloads/AsuraDoenstKnowHowToScript/FURY-Launcher/total?color=blue)](../../releases)
[![Status](https://img.shields.io/badge/status-beta-yellow)](../../releases)
[![Platform](https://img.shields.io/badge/platform-Windows%2010%20%7C%2011-0078D6)](#supported-systems)
[![Languages](https://img.shields.io/badge/languages-5-purple)](#languages)
[![License](https://img.shields.io/badge/license-Proprietary-red)](LICENSE)

> [!IMPORTANT]
> **Current version: `v0.3.5-beta` (testing).** It is stable enough for everyday
> testing but still a work in progress. Grab the latest build from the
> [Releases](../../releases) page and please send feedback.

## Versions

| Channel | Version | Who it's for |
| --- | --- | --- |
| **Beta (latest)** | `v0.3.5-beta` | Testers — start here |
| Beta | `v0.3.4-beta` | Previous build |
| Pre-alpha | `v0.2.0-pre-alpha` | Early, unstable |

The **release** badge above always points to the newest published build.

## Download and play

1. Open the [Releases](../../releases) page and download the latest build.
2. Unzip it wherever you like.
3. Run `FURY Launcher.exe`.

The build is self contained, so you do not need to install .NET or Java first.
Java is downloaded automatically the first time you launch a version that needs it.

### Supported systems

> [!NOTE]
> Windows 10 and Windows 11 (64-bit). A Linux version is in development, but
> Windows is the priority for now. A proper MSI installer is planned for a later
> release to make setup and updates smoother.

## What it does

- **Instances.** Create as many as you want, each with its own isolated
  `.minecraft`. Pick the Minecraft version from a dropdown, choose a loader
  (Vanilla, Fabric, Forge or NeoForge), and set RAM and JVM arguments per instance.
- **Play.** Log in with a Microsoft account (in an embedded window, no browser tab
  or copy/paste) or play offline. Your accounts and offline profiles share one
  picker. For offline you just type a nick. The launcher installs anything that is
  missing (libraries, assets, Java) and shows live progress and a game log.
- **Auto-updates.** The launcher checks GitHub on startup and offers to update in
  place. Stable builds install right away; beta builds ask first.
- **Mods.** Add, remove and toggle mod jars per instance, or search Modrinth and
  download the version that matches your instance.
- **Forge and NeoForge** are installed straight from Maven, so there is no ad link
  or browser step.
- **Modpacks.** Export and import a self contained `.frpack` (manifest plus the
  real mod jars).
- **Skins and capes** per offline profile, shown in game through CustomSkinLoader.

## Languages

The interface is available in English (default), Português, Nederlands,
繁體中文 and Русский. You can switch language at any time from the About tab.

## Feedback

Found a bug or have an idea? Open an [Issue](../../issues) using the bug report
template. Clear steps to reproduce help a lot, and if the app crashed, attach the
`crash.log` file that appears next to the executable.

> [!WARNING]
> Found a **security** problem? Do **not** open a public issue. See the
> [Security Policy](SECURITY.md) for how to report it privately.

## Security

Security issues are handled privately. Please read [SECURITY.md](SECURITY.md) and
report through GitHub's **Report a vulnerability** button or by emailing
`furylauncher@gmail.com` with "SECURITY" in the subject.

## Build from source

You need the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

On Windows the easiest way is to run `run.bat`. It checks for the .NET SDK (and
offers to install it through winget if it is missing), restores dependencies,
builds in Release and starts the app. To produce a distributable build, run
`run.bat publish`.

With the SDK directly:

```
dotnet build FURY.sln -c Release
dotnet run --project Launcher.App
```

### How it is organized

All the logic lives in `Launcher.Core`, a plain library with no UI dependency.
`Launcher.App` is a small Avalonia interface that just subscribes to Core events
and shows the result. Everything the launcher does can be driven without the UI
through the `LauncherCore` class.

Data is kept in `%APPDATA%/FURY Launcher/`: the instances index, accounts,
profiles, settings, and one isolated `.minecraft` per instance. No credentials are
stored in the code.

## Not affiliated with Mojang or Microsoft

FURY Launcher is an independent project. It is not affiliated with, associated
with, authorized, or endorsed by Mojang AB or Microsoft. "Minecraft" is a
trademark of Mojang AB. This software does not include or distribute any
proprietary Minecraft code or assets.

## License

FURY Launcher is proprietary software and all rights are reserved. You may not
use, copy, modify or distribute it without prior written permission from the
copyright holder. See the [LICENSE](LICENSE) file for the full terms. "FURY" is a
trademark of the holder.

Contact: furylauncher@gmail.com
