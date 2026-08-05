# PF Analysis

Run your Party Finder from one window: save your recruitment settings as presets and post any
of them in a click, watch your listing and party as it fills, and look up the people who join
you. Formerly, and still internally, PF Presets.

This is a plugin for [Dalamud](https://github.com/goatcorp/Dalamud) (the FFXIV plugin
framework used with XIVLauncher).

## Features

- **Presets** — save any number of recruitment setups and apply one with a single click.
  The plugin fills in the duty, objective, comment, role slots, completion status,
  average item level, loot rules, languages, and private-party password, then posts the
  listing for you. There is a button for it beside the game's own *Recruit Members*, and a
  *Save as Preset* button under any listing you open.
- **Role & job control** — per-slot role or individual job selection with the same
  categories as the in-game window, or let the game auto-adjust roles
  ("Seek Job Distributions"). Locked one-job slots can be kept in step with who has already
  joined.
- **Share presets** — every preset exports to a single-line code you can paste into Discord.
  *Share* on a preset's menu opens the code with a copy button; the **Import** button next to
  *Create New Preset* takes one back in, either pasted or straight off your clipboard.
- **Auto Refresher** — re-posts your listing on a timer (Edit → Recruit, exactly like doing
  it by hand), with a live countdown in the main window. Double-click the interval to set any
  value from 1 to 55 minutes, and optionally have it stop on its own after a set number of
  hours. Disabled automatically if you already run the standalone RecruitmentRefresher plugin.
- **Your party, live** — everyone in the party with their job, and what the listing is
  recruiting for, including somebody else's listing you joined across worlds. Report, kick
  and blacklist sit in a menu on each row; blacklisting drives the game's own list, by party
  slot inside a duty and through the Contacts window outside one.
- **Player lookups and community ratings** — progression and ratings for the people you play
  with, and a search for any character by name and world. Only in the third-party repo build:
  the official-repo build is compiled without any of it (see `docs/ratings.md`).

## Installation

1. Install [XIVLauncher](https://goatcorp.github.io/) and enable Dalamud.
2. Open Dalamud Settings (`/xlsettings`) → **Experimental** → **Custom Plugin Repositories**.
3. Add this repository URL:
   ```
   https://raw.githubusercontent.com/Marobotic/PfPresets/main/repo/repo.json
   ```
4. Open the plugin installer (`/xlplugins`), search for **PF Analysis**, and install it.

## Commands

| Command | Effect |
| --- | --- |
| `/pfa` | Toggle the PF Analysis window |
| `/pfa apply <name>` | Post a preset by name — put this in a hotbar macro |
| `/pfa list` | List your presets and the duty each one is set to |
| `/pfa refresh` | Re-post your current listing immediately |

`/pfanalysis`, `/pfp` and `/pfpresets` are aliases for the same command, so every old macro
and forum post still works.

`apply` matches the preset name case-insensitively, and accepts a unique prefix or partial
name (`/pfa apply savage` finds "M4S Savage Prog"). If the name is ambiguous it says so
rather than guessing.

## Building

1. Install the [.NET SDK](https://dotnet.microsoft.com/download) and run FFXIV via
   XIVLauncher at least once (the build references Dalamud's libraries from
   `%AppData%\XIVLauncher\addon\Hooks\dev`).
2. Run `build.bat` on Windows or `build.sh` on Linux, or `dotnet build -c Release`.
3. `build.bat` also copies the output to `%AppData%\XIVLauncher\devPlugins\PfPresets` so
   you can load it in-game via *Dev Plugins*.

`--no-ratings` (`-p:EnableRatings=false`) builds the official-repo variant, which contains
none of the ratings code. Read `docs/building.md` first — it covers which output the in-game
dev plugin actually loads.

### Project layout

```
Plugin.cs            Entry point: service wiring, chat commands
Configuration.cs     Saved settings, preset CRUD, and config migrations
Core/                Duty tracking and the rating/analytics HTTP clients
Data/                Preset model, job/duty static data, bitmask conversions, share codec
Game/                Native Party Finder automation (memory writes, UI clicks, refresher)
UI/                  ImGui windows (one partial class file per window) and the theme
docs/                Build notes, the ratings design, and the structured release history
repo/                The Dalamud plugin repository served from GitHub (repo.json + zips)
```

Presets reference their duty by `ContentFinderCondition` row id (`PfPresetData.DutyRowId`),
with the display name kept only as a fallback for presets saved before 3.0.0.1 and for the
handful of high-end duties missing from the game sheet. `Configuration.Migrate` back-fills
the ids on first load.

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
