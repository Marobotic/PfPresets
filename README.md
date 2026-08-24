# PF Analysis

Know who you are playing with, and stop fighting the Party Finder. Upvote or downvote the people
you run with, check how far anybody has got before you join them, and put your Ultimate clears
somewhere they count for something — then post a recruitment preset in one click and let the
plugin keep the listing alive while you play. Formerly, and still internally, PF Presets.

This is a plugin for [Dalamud](https://github.com/goatcorp/Dalamud) (the FFXIV plugin
framework used with XIVLauncher).

## Features

### Know who you're playing with

- **Upvote and downvote the people you play with.** One click, up or down, after a duty you
  actually shared. **Everybody's vote on you counts once**: pressing the same arrow again
  changes nothing, and pressing the other one moves your single vote across rather than adding
  to it — so a score is a number of people, not a number of button presses. You can change your
  mind at any time; the last thing you said is what counts. Every vote weighs the same, from
  anybody. See `docs/ratings.md`.
- **Progression at a glance, from Tomestone.** Look up how far anybody has got in the current
  tier — for your party, for a listing you are about to join, or for any character by name and
  world. Answers are shared, so once anyone has looked a player up everybody sees the same one
  without spending another lookup.
- **Broadcast your Ultimate clears.** Clear an Ultimate or a savage tier and it goes up on the
  Clears feed for everybody to see, with a heart on each. *My clears* keeps every one of yours in
  one place. Profile cards also show what a player has killed — every Ultimate, the current
  savage tier, and the expansion's Extremes and Unreals, with the best parse on each. A clear
  counts whether it was logged or not, so people who never upload logs don't read as having
  cleared nothing. Broadcasting is a setting; turning it off takes your existing posts down too.
- **See a listing before you join it.** The panel beside a party finder listing shows who is
  already in that party, with their progression. One person in the party running the plugin is
  enough to fill it in for everybody looking at the listing, and it comes down when the listing
  does. Turn it off under Settings → PF Radar.

The community half — ratings, progression, clears, the feed — is only in the third-party repo
build. The official-repo build is compiled without any of it (see `docs/ratings.md`).

### Automation

- **Apply a party finder preset with a single click.** Save any number of recruitment setups;
  applying one fills in the duty, objective, comment, role slots, completion status, average item
  level, loot rules, languages and private-party password, then posts the listing for you. There
  is a button for it beside the game's own *Recruit Members*, and a *Save as Preset* button under
  any listing you open.
- **Keep your party alive with Auto Refresh.** Re-posts your listing on a timer (Edit → Recruit,
  exactly like doing it by hand) so it never falls off the board, with a live countdown in the
  main window. Double-click the interval to set anything from 1 to 55 minutes, and optionally have
  it stop on its own after a set number of hours. Disabled automatically if you already run the
  standalone RecruitmentRefresher plugin.
- **Fixes the party finder's one-job slot trap.** Lock a slot to a single job in the game's own
  window and it stays locked to that job — so when that player leaves, the seat can only ever be
  filled by somebody on the same job, and your listing quietly stops recruiting. The plugin
  notices the member leaving, waits five seconds for the game to settle, then broadens the seat
  from the one job back to its role and re-posts. *Adjust 1-slot locked jobs while recruiting*,
  under the party list.
- **Role & job control** — per-slot role or individual job selection with the same categories as
  the in-game window, or let the game auto-adjust roles ("Seek Job Distributions").
- **Share presets** — every preset exports to a single-line code you can paste into Discord.
  *Share* on a preset's menu opens the code with a copy button; the **Import** button next to
  *Create New Preset* takes one back in, either pasted or straight off your clipboard.
- **Your party, live** — everyone in the party with their job, and what the listing is
  recruiting for, including somebody else's listing you joined across worlds. Report, kick
  and blacklist sit in a menu on each row; blacklisting drives the game's own list, by party
  slot inside a duty and through the Contacts window outside one.

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
Core/                Duty tracking, and the rating / clears / feed HTTP clients
Data/                Preset model, job/duty static data, bitmask conversions, share codec
Game/                Native Party Finder automation (memory writes, UI clicks, refresher)
UI/                  ImGui windows (one partial class file per window) and the theme
docs/                Build notes, the ratings design, and the structured release history
repo/                The Dalamud plugin repository served from GitHub (repo.json + zips)
```

`Core/` and `UI/` are both split by concern rather than by size — `RatingService.*.cs` and
`PluginUI.*.cs` are partial classes, one file per thing they do, so the ratings service and the
main window are each readable a piece at a time.

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
