# FURY Launcher

A lightweight, no-nonsense Minecraft launcher. Isolated instances, Forge/NeoForge
installed straight from Maven (no ad-link redirects), self-contained `.frpack`
modpacks, and per-profile skin/cape display.

> ⚠️ **Pre-alpha — unstable, in active development.** Expect rough edges and
> breaking changes. It is published so testers can try it and send feedback.

## Download & test

1. Go to the [**Releases**](../../releases) page and grab the latest build.
2. Unzip it anywhere.
3. Run **`FURY Launcher.exe`**. The build is self-contained — you do **not** need
   the .NET runtime installed.

Found a bug or have a suggestion? Please open an
[**Issue**](../../issues) — clear steps to reproduce and your `crash.log` (created
next to the executable on a crash) help a lot.

## What works today (end to end)

- **Instances**: create (name, version, loader), list, edit (min/max RAM, JVM
  args, Java path), delete. Each instance has its own isolated `.minecraft`.
- **Play**: offline account (deterministic UUID from the name) or Microsoft
  (OAuth). Installs whatever is missing (manifests, libraries, assets, Java
  runtime), with a live download progress bar and game log. Stop button included.
- **Mods** (per instance): installs the loader (Fabric/Forge/NeoForge) on first
  launch; add/remove/enable-disable jars; search Modrinth and download the version
  matching the instance's Minecraft version + loader.
- **Forge/NeoForge** resolved and downloaded directly from Maven — no ad-link
  redirects, no browser step.
- **.frpack**: export/import a self-contained modpack (manifest + real mod jars).
- **Skin/cape** display per offline profile (via CustomSkinLoader).

## Build from source (developers)

Requires the **.NET 8 SDK**.

On Windows, just run **`run.bat`** — it checks for the .NET SDK (offering to install
it via winget if missing), restores dependencies, builds in Release and launches
the app. To produce a distributable build:

```
run.bat publish
```

Or with the SDK directly:

```
dotnet build -c Release
dotnet run --project Launcher.App
```

### Architecture

All logic lives in `Launcher.Core` (a plain library with **zero** UI dependency);
`Launcher.App` is a minimal Avalonia UI that only subscribes to Core events and
shows results. Everything in the launcher is usable without the UI through the
`LauncherCore` facade.

## Data location

`%APPDATA%/FURY Launcher/` (created on first run): instances index, accounts,
profiles, settings, and one isolated `.minecraft` per instance. No credentials are
stored in the code.

## Not affiliated with Mojang / Microsoft

FURY Launcher is an independent project and is **not** affiliated with, associated
with, authorized, or endorsed by Mojang AB or Microsoft. "Minecraft" is a trademark
of Mojang AB. This software does not include or distribute any proprietary Minecraft
code or assets.

## License

FURY Launcher is **proprietary software** — **all rights reserved**. No permission
to use, copy, modify, or distribute is granted without prior written authorization
from the copyright holder. See the [LICENSE](LICENSE) file. **"FURY"** is a
trademark of the holder and is not licensed by this project.
