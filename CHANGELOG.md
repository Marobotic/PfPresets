# Changelog

## 2.1.2

**Added a confirmation prompt before deleting a preset**, so a misclick no longer wipes
a preset instantly.

**Fixed a rare crash** that could happen if the plugin was unloaded or updated while it
was in the middle of applying a preset.

**Made duty selection safer.** If a high-end duty has been renamed or removed by a game
patch, the plugin now leaves the listing's duty unset instead of posting a wrong one.

**Removed two options that never did anything.** "Unselect Classes" and the item-level
"or Above" toggle were saved but never applied to the Party Finder, so they're gone.

Under the hood, the whole codebase was reorganized (Data / Game / UI) for maintainability.
No change to your saved presets.

---

## 2.1.1

**The Auto Refresher now works end to end.** It performs the full
Edit -> Apply Changes sequence on your listing (exactly like the
RecruitmentRefresher plugin), so your Party Finder is reliably re-posted
instead of just opening the window.

Also added:
- A live countdown timer next to the Auto Refresher toggle showing the time
  until the next refresh. It starts once your Party Finder is up.
- A choice of refresh interval: every 15 or 30 minutes.
- A `/pfp refresh` command to refresh your listing on demand.

---

## 2.1.0

**Fixed: party job detection now works when members are in different zones.**
Auto-adjust slots now read party jobs from the social party list instead of the
in-zone party list, so a teammate being on another map no longer wipes out their
detected job.

**Fixed: party leader detection now updates when lead is passed to you.**
Previously the plugin could still think you weren't the leader after leadership
was handed to you, blocking recruitment. It now reads the live party leader at
the moment you apply a preset, so a passed lead is recognized immediately.

**Fixed: the built-in Auto Refresher now actually works.** It refreshes your
Party Finder listing every 30 minutes using the same proven method as the
RecruitmentRefresher plugin. The toggle now also sits as its own section above
the Create button instead of overlapping it.

**Redesigned the "Applying preset" window.** It now matches the modern look of
the rest of the plugin, with a friendly play-by-play of what's happening and a
smooth progress bar. When it finishes you get a clear "All set, PF is up!" and a
green Done button. The old settings-comparison list has been removed.

Also: moved the Ko-fi support button into the window header (and made it bigger),
surfaced the Auto Refresher toggle directly in the main window (it hides
automatically if you have the RecruitmentRefresher plugin), fixed the
"Create New Preset" button icon, and fixed long preset comments so they wrap
instead of running under the Apply Preset button.

---

## 2.0.1

Moved the private-party password next to the preset name so it's no longer hidden
behind the Apply Preset button.

---

## 2.0.0

Revamped the design and fixed various bugs, enhanced usability and readability of
the plugin and more!

---

## 1.0.6

**Fixed: applying a preset now selects the right duty.**

Before, applying a preset would often leave your Party Finder listing set to
**"All"** instead of the duty you saved. You had to manually open the in-game
duty menu and pick a duty once before presets would work.

That's fixed now. Applying a preset sets the correct duty on its own - no need
to open the duty menu first. It just works.

---

## 1.0.5 and earlier

Initial releases: save and manage Party Finder recruitment presets (duty,
objective, roles, conditions, languages, and more) and apply them with one click.
