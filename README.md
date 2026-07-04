# PF Presets

One-click Party Finder presets for FFXIV. Save your recruitment settings — duty, objective,
roles, conditions, comment, password, and more — and re-apply them to the in-game Party
Finder with a single click. Includes an Auto Refresher that re-posts your listing every
15 or 30 minutes so it never expires while you wait.

This is a plugin for [Dalamud](https://github.com/goatcorp/Dalamud) (the FFXIV plugin
framework used with XIVLauncher).

## Features

- **Presets** — save any number of recruitment setups and apply one with a single click.
  The plugin fills in the duty, objective, comment, role slots, completion status,
  average item level, loot rules, languages, and private-party password, then posts the
  listing for you.
- **Role & job control** — per-slot role or individual job selection with the same
  categories as the in-game window, or let the game auto-adjust roles
  ("Seek Job Distributions").
- **Auto Refresher** — re-posts your listing every 15 or 30 minutes (Edit → Recruit,
  exactly like doing it by hand), with a live countdown in the main window. Disabled
  automatically if you already run the standalone RecruitmentRefresher plugin.
- **Party-aware** — works with cross-world parties and detects party leadership live, so
  a freshly passed lead is recognized immediately.

## Installation

1. Install [XIVLauncher](https://goatcorp.github.io/) and enable Dalamud.
2. Open Dalamud Settings (`/xlsettings`) → **Experimental** → **Custom Plugin Repositories**.
3. Add this repository URL:
   ```
   https://raw.githubusercontent.com/Marobotic/PfPresets/main/repo/repo.json
   ```
4. Open the plugin installer (`/xlplugins`), search for **PF Presets**, and install it.

## Commands

| Command | Effect |
| --- | --- |
| `/pfp` or `/pfpresets` | Toggle the PF Presets window |
| `/pfp refresh` | Re-post your current listing immediately |

## Building

1. Install the [.NET SDK](https://dotnet.microsoft.com/download) and run FFXIV via
   XIVLauncher at least once (the build references Dalamud's libraries from
   `%AppData%\XIVLauncher\addon\Hooks\dev`).
2. Run `build.bat`, or `dotnet build -c Release`.
3. `build.bat` also copies the output to `%AppData%\XIVLauncher\devPlugins\PfPresets` so
   you can load it in-game via *Dev Plugins*.

### Project layout

```
Plugin.cs            Entry point: service wiring, chat commands
Configuration.cs     Saved settings and preset CRUD
Data/                Preset model, job/duty static data, bitmask conversions
Game/                Native Party Finder automation (memory writes, UI clicks, refresher)
UI/                  ImGui windows (one partial class file per window) and the theme
repo/                The Dalamud plugin repository served from GitHub (repo.json + zips)
```

## Credits

- The auto-refresh approach (native OpenPartyFinder signature and the Edit → Recruit
  click sequence) is ported from
  [RecruitmentRefresher](https://github.com/anya-hichu/RecruitmentRefresher)
  by anya-hichu (AGPL-3.0).
- The native button-click helper is a port of `ClickAddonButton` from
  [ECommons](https://github.com/NightmareXIV/ECommons) by NightmareXIV (MIT).
- Built on [Dalamud](https://github.com/goatcorp/Dalamud) and
  [FFXIVClientStructs](https://github.com/aers/FFXIVClientStructs).

## License

[AGPL-3.0](LICENSE). This project incorporates code from RecruitmentRefresher
(AGPL-3.0), so the project as a whole is distributed under the same license.

FINAL FANTASY XIV © SQUARE ENIX CO., LTD. This project is not affiliated with
Square Enix.
