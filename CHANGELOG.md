# Changelog

## 3.2.0 — Community Ratings (Phase 2)

Only present in the third-party repo build. The official-repo build is compiled with
`-p:EnableRatings=false` and contains none of this code.

**New: community player ratings.** A Ratings tab shows everyone you can still vote on, and looks
any character up by `Name@World` with links out to FFLogs, Tomestone and the Lodestone.

**Voting is a thumbs up or a thumbs down, and one click casts it.** No star scale, no submit
button — after a duty you either would play with someone again or you wouldn't. Scores show as the
share of votes that are positive.

**New: a Contacts tab.** A read-only record of the last 24 players you finished a duty with and
which duty it was. Local, and forgotten after a week.

**Voting happens in two places.** The Ratings tab lists everyone you can still vote on, and a small
window appears when you leave a duty if anyone from it is still unrated. Skip it and that duty
won't ask again — but you have 24 hours to change your mind from the Ratings tab.

**New: ratings on your party.** Everyone in the party you're in shows their rating beside them.
Ratings are deliberately *not* shown while browsing the Party Finder — a score on a listing you
haven't joined is a screening tool for strangers, which isn't what this is for.

**New: kick and report, for party leaders.** A Kick button beside each member, behind an
"are you sure" confirmation, using the game's own native call rather than a synthesised chat
command. Report sends a note to the plugin author — clearly labelled as *not* a Square Enix
report, since only theirs can act on an account.

**Ratings are anonymous and hard to game.** The server stores no character names at all, only
one-way hashes, and ratings are stored with no link back to whoever submitted them. Ratings from
friends and FC mates count for half, repeat ratings of the same person count for a tenth, and the
two stack. You can rate any given player once per 24 hours, enforced on both the client and the
server. No score is shown at all until a character has at least three ratings, so one person's
opinion can never become someone's reputation.

**Off by default.** Nothing is looked up, recorded or sent until you turn it on in settings, where
the full list of what leaves your machine is spelled out. Who you met in duties is kept in a
single local file you can clear from the same screen.

---

## 3.0.0.2

**Fixed: Apply stayed available while you were queued or in a duty.** The button showed green
and could be clicked while you were sitting in the Duty Finder queue — and posting a listing
from there drops your queue registration. It's now disabled, with the reason shown on hover,
for both the Duty Finder queue (including a popped duty waiting to be accepted) and while
you're inside a duty.

**Fixed: presets half-applied if the Party Finder was left on the World or Private tab.**
The Party Finder remembers whichever of its three tabs — Data Centre, World, Private — you
used last, but recruitment can only be set up from Data Centre. Landing on either of the
others broke the setup sequence partway through. Applying a preset now switches back to the
Data Centre tab first, whether the window is already open or not.

**"Auto-Adjust Roles (Seek Job Distributions)" is now on by default for new presets.** It's
what most listings want, and it fills around whoever is already in your party. Presets you
already saved are untouched and keep whatever they were set to.

---

## 3.0.0.1

**New: share presets with a one-line code.** Pick **Share** on any preset's menu and you get a
single line of text with a copy button — paste it in Discord and anyone running PF Presets can
use your setup. The **Import** button next to *Create New Preset* takes one back in, either
pasted into the box or pulled straight off your clipboard. Codes that aren't PF Presets codes
(or that got cut off mid-copy) are rejected with a plain explanation instead of importing
something broken.

**New: `/pfp apply <name>`.** Posts a preset without opening the window, so a preset can live on
a hotbar macro. Partial names work as long as they're unambiguous. Added `/pfp list` to see the
exact names.

**Auto Refresher is no longer locked to 15 or 30 minutes.** Double-click the interval to type any
value from 1 to 55. Next to it there's a new **Stop after** limit — set it and the refresher stops
renewing your listing after that many hours, so a Party Finder can't sit up all night unattended.
Your listing isn't cancelled when the limit hits; it just expires the way it normally would.
"Never" (the default) keeps the old behaviour.

**Fixed: a renamed duty could post the wrong listing.** Presets recorded their duty by name and,
when that name no longer matched, silently fell back to the *first duty in the category* — so a
patch that renamed your duty could put up a listing for something else entirely. Presets now
store the duty's id, which patches don't change, and when a duty genuinely can't be identified
the listing is left with no duty set instead of guessing. Your existing presets are upgraded
automatically the first time you load this version.

---

## 3.0.0

**New: Auto-adjust locked job slots.** A new toggle under the Auto Refresher. While you're
recruiting as party leader, if a member leaves, any Party Finder slot locked to a single job
is automatically widened to that job's role five seconds later - e.g. White Mage becomes regen
healers, Red Mage becomes casters, Viper becomes melee - so the freed seat is easier to fill.
If a slot is somehow locked to a non-combat job, it falls back to the standard auto-fill
composition instead.

**Fixed a serious crash.** Duplicating or reordering a preset from a card's menu could crash
the plugin and, worse, corrupt the interface of Dalamud and every other plugin. That's fixed,
and the rendering is now hardened so a plugin error can never leak its styling into others.

**Taller preset cards** so long descriptions are fully visible instead of being cut off.

**Ko-fi support button** added to the "Applying Preset" window (next to the close button), and
the header support button now reads "Support me on Ko-Fi" with the heart.

---

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
