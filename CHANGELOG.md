# Changelog

## 3.5.3.0

### Updates

Clears are announced on screen. When somebody clears an Ultimate or a savage fight, a banner appears saying who cleared what, and you can heart it from there without opening the plugin. It waits until you are out of a fight before it shows anything, never announces your own clears, and never opens with a backlog - a new install hears nothing until the next clear happens. Log in after being away and it catches you up on what you missed while you were gone, one at a time.

It has its own section in Settings: turn it off, move it, change how long it stays up, choose its typeface, or let clicks pass straight through it to the game.

A proper first run. Installing the plugin now opens a setup screen rather than pages of text. It asks three things - the shape of the window, its colour, and whether you want clears announced - and applies each answer as you give it, then shows you what the plugin does. If you would rather not answer anything, take the recommended settings and go straight to the tour.

"Beginners welcome" is part of a preset now. The sprout on the game's recruitment window is saved with the rest of the preset, applied when you post it, and shows on the preset itself so you can see at a glance which of them have it.

Savage clears are named after the boss rather than the floor. A clear that said "M1S" says who was killed. The floor is still there in the tooltip.

Sharing who is in your listing now keeps working when PFRadar is installed. Only the panel that reads other people's listings has to stand down for it; publishing your own party does not, and switching it off told everybody else your party had gone when it had not. The switch for it is also no longer hidden when "Show listing details" is off, and the panel no longer takes up space beside a listing to explain that it is standing down - Settings says so instead, on the setting it applies to.

### Bugs Fixed

The party panel forgot which duty you were recruiting for the moment the last seat filled - which is the moment you most want to see it. An hour of filling for an Ultimate ended with the panel saying "In a party". It now keeps the fight until the party breaks up.

Applying a preset cleared the "Beginners welcome" tick from your listing, even if you had set it yourself in the game's window first.

The 99th and 100th percentile parses were hard to tell apart from the ones below them - pink was almost the same dark purple as the 75th, and gold came out brown. Both read properly now.

## 3.5.2.0

### Updates

Five Party Finder categories that could not be posted at all now can. Deep Dungeons, Treasure Hunt, FATEs, PvP and Duty Roulette each put a listing up with the wrong duty attached or with none, and three of them were offering names the game's own recruitment window has never had. Each was read back off the client, one duty at a time.

Deep dungeon presets work. The category is back, and it lists the four dungeons the way the game does - the Palace of the Dead, Heaven-on-High, Eureka Orthos, Pilgrim's Traverse - instead of the fifty floor sets behind them, which is what the plugin had been offering and none of which the Party Finder has ever accepted. A preset for one of them now posts as that dungeon. Presets saved from the old list point at the dungeon they were always for.

Treasure hunt presets work too, and the category lists maps. It had been offering the dungeons a map opens into - the Aquapolis, the Excitatron 6000 - which is not what the Party Finder asks for; it asks which map you are holding, from Leather up to Gargantuaskin. Treasure hunts are also now always limited to your own world, the way FATEs and hunt trains are, because the dig only happens on the world it was started on.

FATE presets work, and they name the zone. The category has always listed every field zone in the game, but the listing went up without one - the plugin had no way to say which place it meant, so it said nothing. It does now, from Middle La Noscea through to Living Memory, and "All locations" still means anywhere.

PvP presets work. Crystalline Conflict, Frontline and Rival Wings can all be posted now, and a Crystalline Conflict listing recruits the pair it is played as rather than a full party.

Duty Roulette presets work. All ten roulettes can be listed, from Expert down to Daily Challenge: Frontline - the listing used to go up with no roulette attached at all.

Party Finder can now show you what a locked duty actually is. Listings for content your character hasn't unlocked are labelled "Locked Duty" by the game instead of being named; turning on Settings -> Party Finder -> "Show names of locked duties in party finder" puts the fight's real name back, still marked "(Locked Duty)" so you can tell you can't join it.

It is off by default and asks before it goes on, because the reason the game hides those names is spoilers. Nothing is fetched to do this - the duty is already in the listing your client received, which is how "Save as Preset" has always been able to name a locked listing.

A prompt now holds the whole plugin, not just the window it opened from. Everything the plugin draws over the game - the button beside Recruit Members, "Save as Preset", the listing panel, the leader's score, the checklist, the welcome card and the rating prompt - dims and stops taking presses while a question is waiting. Answering the question gives them back.

Confirmations read shorter and sit properly in their own box.

The party list no longer says where you are twice. Inside a duty the heading above the column already names it, so the list underneath it dropped its own "In <duty> with" line. Outside a duty the two say different things and both stay.

### Bugs Fixed

Fixed the dimming behind a prompt never appearing. Every confirmation in the plugin was supposed to darken the window behind it and had been drawing that layer all along - it was being covered by the very window it was meant to dim, so it had never once been visible. Prompts now dim what is behind them, and clicks that miss the prompt no longer land on whatever is underneath.

## 3.5.1.1

### Updates

Everybody's vote on you counts once. Rating the same person again used to add another vote on top of the last one - worth a tenth, but still another one - so people who play together every week piled up hundreds of votes between them, and a score of +300 could be eight people rather than three hundred. Your vote is now a position rather than a tally: upvote somebody and you have upvoted them, however many times you press it.

You can change your mind whenever you like. Downvoting somebody you upvoted moves your one vote across instead of adding to it, and there is no longer a day to wait out before you can - the button used to grey itself out for twenty-four hours, which made changing your mind impossible rather than merely slow. What counts is the last thing you said.

Every vote is worth the same now. An upvote is +1 and a downvote is -1, from anybody: no more half a vote from a friend or a quarter from an account with no history. A vote either counts in full or it is held until the account has earned its place, and holding is what stopped fake votes in the first place - a quarter of a vote still moved a score.

Existing scores were rebuilt to match. Almost everybody's percentage is unchanged - the totals came down because the duplicates went, not because opinions changed.

Clears now loads sixteen at a time and fetches the next sixteen as you approach the bottom, instead of being split into numbered pages. It remembers where you were when you come back to the tab.

Clears arriving while you are reading no longer shove the list around. A button floats at the top of the list instead, and pressing it takes you to the newest.

Clears has two tabs: Broadcast, which is the feed of everybody's clears as before, and My clears, which is every clear of your own that has gone up on it. My clears appears with your first clear and is not there before that.

Removed the divider above "Recent clears".

## 3.5.1

### Updates

The panel beside a party finder listing now shows who is already in that party. One person in the party running the plugin is enough to fill it in for everybody looking at the listing, so you can see who you would be joining before you join them. It only ever describes a party that is publicly listed, it comes down when the listing does, and you can turn it off under Settings -> PF Radar.

Recruiting on your own now lists you, with your progression and a way to fetch it. Posting a listing and waiting is exactly when your own prog point is worth seeing, and the party list used to show nothing at all until somebody joined.

The listing panel's "Look up progress" now says when everyone in the listing was checked too recently to check again, instead of taking the press and appearing to do nothing.

Removed the "Read the update" button from Settings -> About.

### Bugs Fixed

Fixed progression disappearing. Prog points would drop back to "Fetch" while the server had the answer all along - most often with a party finder listing open beside your own party list, because the two were overwriting each other's results.

Fixed "Update progress" doing nothing. A press that landed while the plugin was already reading was thrown away without a word; it now goes through, and says so when it genuinely cannot.

Fixed omitted slots coming back. Omitting a slot worked until the first person left the party, at which point the locked-slot adjuster put every omitted seat back and started recruiting for the roles you had struck off.

Fixed omitted slots being posted out of place. They are now moved to the end of the listing before it goes up, which is where the game puts them anyway, and the roles you kept keep their order.

---

## 3.5.0

### Updates

We now have two layouts: portrait and landscape.

Overhauled UI and UX improvements across the entire plugin.

Updated fonts, resized icons and buttons, and rounded corners everywhere.

Adjusted the role table so that if auto-select jobs is disabled, it defaults to the default roles instead of leaving every slot as FFA.

Added a spoiler button in settings to hide and lock any duty that your current character hasn't unlocked yet. This also prevents applying presets for locked duties. You can disable the setting to show the duty names, but you can keep it on if you don't want spoilers for duties you haven't unlocked.

The PF Analysis button now hides if you open the native in-game recruitment criteria or browse listings.

Clears now have notifications for each new clear that pops up, so you can keep track if someone else clears an ultimate. Hearting them is now locked to your character, and it's a toggle — you can remove the heart.

FATEs and The Hunt now force "limit recruiting to my world" on, and restore your previous setting when you switch to a different category.

Hidden duty categories for content we don't currently support until they can be fixed at a later time.

Language selectors in PF preset letters are now centered.

### Bugs Fixed

Fixed a bug where presets were saved without pressing Save. Now they only save when you press Save.

Fixed a bug where dungeons showed as full party. Now content is correctly set to light party for instances with a light party limit.

Fixed a bug where Omit didn't work as intended and failed to omit the role.

---

## 3.3.5

**Overlay button positioning now respects multi-monitor and windowed setups.** Buttons anchored to game windows (like *PF Analysis* beside Recruit Members and *Save as Preset*) now map game coordinates to virtual desktop space correctly, preventing alignment drift on non-primary monitors or windowed modes. Added `/pfpdebug overlay` diagnostic.

**Listing watcher checks once when no listing exists.** Probing no longer repeats four times over three minutes for non-recruiting parties, keeping your chat log clean.

**Update Progress shows cooldown status.** When party members are within their server refresh window, the button displays *"Updated · 12m"* with an explanatory tooltip rather than appearing unresponsive.

---



**Progress percentages are now tied to the current fight.** They persist when your party fills or goes idle, and only clear or switch when a new duty is detected through Party Finder recruitment, Duty Finder queue, or duty commencement.

**PvP instances now trigger the rating prompt.** Frontline, Crystalline Conflict, and other PvP duties are now detected and will show the post-duty rating prompt after completion.

---

## 3.2.1

**Fixed: downvoting was broken.** Thumbs down always returned "That rating couldn't be sent" — a
validation check that was meant to guard against invalid scores was also rejecting −1, the value a
downvote sends. Upvoting was unaffected. The check now correctly accepts +1 and −1 only.

**Community ratings are on by default for new installs.** The rating system, the post-duty prompt,
and ratings on your party panel all default to enabled. Existing installs keep whatever they had.

---

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
