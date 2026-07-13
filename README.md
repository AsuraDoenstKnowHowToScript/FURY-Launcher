# FURY Launcher

FURY Launcher is a Minecraft launcher for Windows. It manages isolated instances, handles mods, and supports both Microsoft and offline accounts.

[![Release](https://img.shields.io/github/v/release/AsuraDoenstKnowHowToScript/FURY-Launcher?include_prereleases&label=release&color=brightgreen)](../../releases)
[![Downloads](https://img.shields.io/github/downloads/AsuraDoenstKnowHowToScript/FURY-Launcher/total?color=blue)](../../releases)
[![Status](https://img.shields.io/badge/status-beta-yellow)](../../releases)
[![Platform](https://img.shields.io/badge/platform-Windows%2010%20%7C%2011-0078D6)](#supported-systems)
[![Languages](https://img.shields.io/badge/languages-5-purple)](#languages)
[![License](https://img.shields.io/badge/license-Proprietary-red)](LICENSE)

## Current version

The latest build is `v0.3.8-beta`. It is under active development but stable for daily use. Download it from the [Releases](../../releases) page.

## Versions

| Channel | Version | Notes |
| --- | --- | --- |
| Beta (latest) | `v0.3.8-beta` | Recommended build |
| Beta | `v0.3.7-beta` | Previous build |
| Pre-alpha | `v0.2.0-pre-alpha` | Early and unstable |

The release badge above always points to the newest published build.

## Download

1. Open the [Releases](../../releases) page and download the latest build.
2. Extract the archive wherever you prefer.
3. Run `FURY Launcher.exe`.

The build is self-contained, so you do not need to install .NET or Java beforehand. When a Minecraft version requires a specific Java runtime, the launcher downloads it on first launch.

## Supported systems

Windows 10 and Windows 11 (64-bit). A Linux build is in development, though Windows remains the priority for now. An MSI installer is planned for a future release to simplify setup and updates.

## Features

Instances. Create as many as you need. Each instance keeps its own isolated `.minecraft`, Minecraft version, loader (Vanilla, Fabric, Forge, or NeoForge), memory limits, and JVM arguments.

Accounts. Sign in with a Microsoft account through an embedded window, with no browser tab or copy and paste, or play offline by typing a nick. Microsoft accounts and offline profiles share a single picker.

Mods. Add, remove, and toggle mod jars per instance. Search Modrinth, choose a version, and install it. Required dependencies are resolved and installed for you.

Loaders. Forge and NeoForge install directly from Maven, with no ad links or browser steps.

Modpacks. Import Modrinth `.mrpack` packs, with mods downloaded and verified, or export and import a self-contained `.frpack` that bundles the manifest and the mod jars.

Skins and capes. Set them per offline profile. They are shown in game through CustomSkinLoader.

Updates. The launcher checks GitHub at startup and can update in place. Stable builds install directly; beta builds ask for confirmation first.

## Languages

The interface is available in English (default), Português, Nederlands, 繁體中文, and Русский. You can change the language at any time from the About tab.

## Reporting problems

To report a bug, open an [issue](../../issues) using the bug report template. Clear steps to reproduce make a fix much faster. If the launcher crashed, attach the `crash.log` file located next to the executable.

For security issues, do not open a public issue. Use GitHub's "Report a vulnerability" button, or email `furylauncher@gmail.com` with "SECURITY" in the subject. See [SECURITY.md](SECURITY.md) for the full policy.

## Building from source

You need the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

On Windows, run `run.bat`. It checks for the .NET SDK and offers to install it through winget if it is missing, restores dependencies, builds in Release, and starts the app. To produce a distributable build, run `run.bat publish`.

With the SDK directly:

```
dotnet build FURY.sln -c Release
dotnet run --project Launcher.App
```

The logic lives in `Launcher.Core`, a library with no UI dependency. `Launcher.App` is an Avalonia front end that subscribes to Core events and displays the result. Everything the launcher does can run without the interface through the `LauncherCore` class.

Data is stored in `%APPDATA%\FURY Launcher\`: the instance index, accounts, profiles, settings, and one isolated `.minecraft` per instance. No credentials are kept in the source.

## Trademark and affiliation

FURY Launcher is an independent project. It is not affiliated with, associated with, authorized by, or endorsed by Mojang AB or Microsoft. "Minecraft" is a trademark of Mojang AB. This software does not include or distribute any proprietary Minecraft code or assets.

## License

FURY Launcher is proprietary software and all rights are reserved. You may not use, copy, modify, or distribute it without prior written permission from the copyright holder. See the [LICENSE](LICENSE) file for the full terms. "FURY" is a trademark of the holder.

Contact: furylauncher@gmail.com
