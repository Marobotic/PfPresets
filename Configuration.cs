using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Configuration;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace PfPresets
{
    [Serializable]
    public class Configuration : IPluginConfiguration
    {
        /// <summary>Schema version of the saved config. See <see cref="Migrate"/>.</summary>
        public int Version { get; set; } = 0;

        /// <summary>The schema this build writes. Bump alongside a new case in <see cref="Migrate"/>.
        /// Deliberately the same number in the ratings and non-ratings builds: the v2 and v3 fields
        /// are inert data, so a config written by one build must load cleanly in the other.</summary>
        public const int CurrentVersion = 7;

        // ── Preset Storage ────────────────────────────────────────
        public List<PfPresetData> Presets { get; set; } = new();

        // ── UI Preferences ────────────────────────────────────────

        /// <summary>
        /// Which of the two fixed windows to draw. See <see cref="DeviceLayout"/> for why there are
        /// only two and why neither can be resized.
        ///
        /// Portrait by default, including for people upgrading from the resizable window. The wide
        /// layout is better on a big monitor and worse on every other one, and a plugin that opens
        /// at 1180px on a 1080p screen with the game behind it has covered the game.
        /// </summary>
        public DeviceLayout Device { get; set; } = DeviceLayout.Portrait;

        /// <summary>
        /// Dead: the window has a fixed size and nothing writes these any more.
        ///
        /// Kept as properties so an existing config file still round-trips - Dalamud deserialises
        /// into this class, and a member that vanishes takes its saved value with it the first time
        /// the config is written back. They cost two ints and they mean a downgrade to an older
        /// build finds its window where it left it.
        /// </summary>
        public int PanelWidth { get; set; } = 980;
        public int PanelHeight { get; set; } = 640;

        /// <summary>
        /// The one colour the player picks, used for every interactive accent in the chrome:
        /// primary buttons, the active nav marker, section labels, checked toggles, the live
        /// countdown and focused input borders.
        ///
        /// Stored as hex text rather than a Vector4 so the config file stays readable and a bad
        /// value is recoverable by hand. Role and vote colours are deliberately not affected: those
        /// are data, and a tank staying blue matters more than a consistent palette. Red is not on
        /// offer - it belongs to Ko-fi and to destructive actions.
        /// </summary>
        public string AccentColorHex { get; set; } = "#9b6dff";

        /// <summary>
        /// Whether the plugin puts an "Apply a recruitment preset" button beside the game's
        /// "Recruit Members".
        ///
        /// On by default. It is the one place someone is guaranteed to be standing when they want
        /// any of this, and a plugin reachable only by a typed command is one people forget they
        /// installed. Anyone who wants their Party Finder untouched can turn it off here and use
        /// /pfa as before.
        /// </summary>
        public bool ShowPartyFinderButton { get; set; } = true;

        /// <summary>
        /// Whether the plugin puts a "Save as Preset" button under a Party Finder listing you are
        /// viewing.
        ///
        /// Separate from <see cref="ShowPartyFinderButton"/> because the two appear in different
        /// places and answer different questions - one is how you post a listing, the other is how
        /// you keep somebody else's. Turning one off is no reason to lose the other.
        /// </summary>
        public bool ShowSaveListingButton { get; set; } = true;

        /// <summary>
        /// Whether a preset for content this character has not unlocked hides what it is for.
        ///
        /// Off by default, and deliberately so. A locked preset is still yours - you wrote it, and
        /// on the alt that has the fight unlocked it is the one you want - so the normal state is
        /// to show it in full with the duty marked as locked. On it goes the other way: the name is
        /// replaced with "(Locked duty)" and locked content stops appearing in the duty picker at
        /// all, for anyone who would rather not be reminded of what they have not got to yet.
        ///
        /// Neither setting changes what can be posted. A locked preset cannot be applied either
        /// way, because the game will not take it.
        /// </summary>
        public bool HideLockedDuties { get; set; } = false;

        /// <summary>
        /// Whether a listing you are viewing shows its leader's community score beside their name.
        ///
        /// Its own setting rather than part of the party scores, because it is the one score shown
        /// about somebody you have not met: the party rows describe people you are already with,
        /// and this one is read while deciding whether to join at all.
        /// </summary>
        public bool ShowListingLeaderRating { get; set; } = true;

        /// <summary>
        /// Whether the Recruit footer is showing its settings rather than just the live status bar.
        ///
        /// Remembered, because it is a preference about how much room the footer is allowed to
        /// take: someone who has set the refresh interval once wants the bar, and someone tuning it
        /// wants the controls to stay put between sessions.
        /// </summary>
        public bool FooterExpanded { get; set; } = false;

        // ── Auto Refresher ────────────────────────────────────────
        public bool AutoRefresherEnabled { get; set; } = false;

        /// <summary>How often the Auto Refresher re-posts the listing, in minutes. Free-form since
        /// 3.0.0.1 (was 15 or 30); clamped to <see cref="PfAutomation.MinRefreshMinutes"/>..
        /// <see cref="PfAutomation.MaxRefreshMinutes"/> when read.</summary>
        public int AutoRefresherIntervalMinutes { get; set; } = 30;

        /// <summary>How many hours the Auto Refresher keeps re-posting before it stops on its own.
        /// 0 means no limit. The listing is never cancelled - it just stops being renewed and
        /// expires the way an unattended one normally would.</summary>
        public int AutoRefresherMaxHours { get; set; } = 0;

        /// <summary>
        /// The highest welcome revision this install has been shown, or 0 for a fresh one.
        ///
        /// A number rather than a bool so a future release with something genuinely new to say can
        /// show it once to existing users too, without showing it twice to anyone.
        /// </summary>
        public int WelcomeSeenVersion { get; set; } = 0;

        // ── Anonymous usage stats ─────────────────────────────────

        /// <summary>How much anonymous usage data is sent. See <see cref="AnalyticsInstallId"/> and
        /// <see cref="AnalyticsMode"/> for exactly what each level covers.</summary>
        public AnalyticsMode AnalyticsMode { get; set; } = AnalyticsMode.Full;

        /// <summary>
        /// Superseded by <see cref="AnalyticsMode"/>. Kept only so a config written before the mode
        /// existed can be read: nullable so "absent" is distinguishable from "explicitly off", which
        /// is the whole question the migration has to answer. Nothing reads it after that.
        /// </summary>
        public bool? AnalyticsEnabled { get; set; }

        /// <summary>Whether anything at all is sent.</summary>
        public bool AnalyticsActive => AnalyticsMode != AnalyticsMode.Off;

        /// <summary>Whether feature-usage counts are sent on top of the install id and version.</summary>
        public bool AnalyticsDetailed => AnalyticsMode == AnalyticsMode.Full;

        /// <summary>
        /// A random id generated once on this machine, used only to avoid counting the same install
        /// twice. It is not derived from anything - not your character, account, world or hardware -
        /// so it identifies nothing beyond "one copy of the plugin". Clearing it makes this install
        /// count as new.
        /// </summary>
        public string AnalyticsInstallId { get; set; } = string.Empty;

        /// <summary>Presets created on this install, ever. Not reduced by deleting presets, so the
        /// total reflects use rather than what happens to be saved right now.</summary>
        public int LifetimePresetsCreated { get; set; } = 0;

        /// <summary>Presets applied on this install, ever.</summary>
        public int LifetimePresetsApplied { get; set; } = 0;

        /// <summary>Presets brought in from a share code on this install, ever.</summary>
        public int LifetimePresetsImported { get; set; } = 0;

        /// <summary>Presets turned into a share code on this install, ever. Counted when the code is
        /// produced, not when it is copied - generating it is the export, and plenty of people read
        /// the code straight off the window.</summary>
        public int LifetimePresetsExported { get; set; } = 0;

        /// <summary>
        /// Player progress lookups made on this install, ever.
        ///
        /// One number, not two. This was briefly split into "all fights" and "one fight" on the
        /// expectation that a lookup covering every fight at once was coming; what actually shipped
        /// was a rule about which expansion's listing to read, so every lookup is and always was
        /// scoped to a single fight. The second counter could only ever have reported zero.
        /// </summary>
        public int LifetimeProgressFetches { get; set; } = 0;

        /// <summary>Superseded by <see cref="LifetimeProgressFetches"/>. Read once by the migration
        /// and never again; nullable so an absent field is distinguishable from a real zero.</summary>
        public int? LifetimeProgressSpecificFight { get; set; }

        // The counters above are only kept while the analytics mode is Full.
        //
        // Basic is documented as sending nothing but the install and the version, and a counter
        // quietly accruing in the background against the day someone switches to Full is not that -
        // it makes the earlier setting retroactive. So the count is not merely withheld, it is never
        // taken. The cost is that switching Basic -> Full starts from where the numbers were left,
        // which is the right way round: an undercount is a smaller lie than a count nobody agreed to.

        /// <summary>Records a preset being created, if the analytics mode collects usage.</summary>
        public void CountPresetCreated()
        {
            if (AnalyticsDetailed) LifetimePresetsCreated++;
        }

        /// <summary>Records a preset being applied, if the analytics mode collects usage.</summary>
        public void CountPresetApplied()
        {
            if (AnalyticsDetailed) LifetimePresetsApplied++;
        }

        /// <summary>Records a preset being imported from a share code.</summary>
        public void CountPresetImported()
        {
            if (AnalyticsDetailed) LifetimePresetsImported++;
        }

        /// <summary>Records a preset being turned into a share code.</summary>
        public void CountPresetExported()
        {
            if (AnalyticsDetailed) LifetimePresetsExported++;
        }

        /// <summary>Records a player progress lookup.</summary>
        public void CountProgressFetch()
        {
            if (AnalyticsDetailed) LifetimeProgressFetches++;
        }

        /// <summary>
        /// How many times a preset has been applied for each duty, newest activity first when read.
        ///
        /// The one piece of analytics that is about content rather than about counts, and the only
        /// reason it is defensible: a duty name says what people raid, not who with or when. No
        /// timestamps beyond "last used" leave the machine, and the party is never involved.
        ///
        /// Collected silently and never shown in the plugin. It answers a question the author has
        /// about where to spend effort, not one a player has about their own evening - a tab
        /// showing it back would be decoration standing in for a feature.
        /// </summary>
        public List<DutyUsage> DutyUsage { get; set; } = new();

        /// <summary>
        /// The duties worth sending, busiest first and capped.
        ///
        /// Capped because the analytics endpoint has a body limit and a long-lived install
        /// accumulates a long tail of ones. The shape of the answer is in the head of the list, so
        /// truncating beats a request that gets rejected whole.
        /// </summary>
        public List<DutyUsage> TopDuties(int max)
            => DutyUsage
                .Where(d => d.Applied > 0 && !string.IsNullOrWhiteSpace(d.DutyName))
                .OrderByDescending(d => d.Applied)
                .Take(max)
                .ToList();

        /// <summary>Records a preset being applied for a duty.</summary>
        public void CountDutyApplied(uint dutyRowId, string dutyName, string categoryName)
        {
            if (!AnalyticsDetailed || string.IsNullOrWhiteSpace(dutyName))
                return;

            // "None" is the placeholder a preset carries when no duty is set. Counting it would put
            // a row at the top of the list that means "not answered".
            if (dutyName.Equals("None", StringComparison.OrdinalIgnoreCase))
                return;

            string key = dutyRowId != 0 ? $"#{dutyRowId}" : dutyName.Trim().ToLowerInvariant();
            var row = DutyUsage.Find(d => d.Key == key);

            if (row == null)
            {
                row = new DutyUsage
                {
                    DutyRowId = dutyRowId,
                    DutyName = dutyName,
                    CategoryName = categoryName ?? string.Empty,
                };
                DutyUsage.Add(row);
            }

            // Refreshed on every apply so a duty renamed or recategorised by a patch corrects
            // itself rather than keeping whatever it was first seen as.
            row.DutyName = dutyName;
            if (!string.IsNullOrWhiteSpace(categoryName))
                row.CategoryName = categoryName;

            row.Applied++;
            row.LastAppliedUtc = DateTime.UtcNow;
        }

        /// <summary>When enabled, a party member leaving while you recruit as leader broadens any
        /// slot locked to a single job to that job's role, so the freed seat is easier to fill.</summary>
        public bool AutoAdjustLockedJobsEnabled { get; set; } = false;

        /// <summary>
        /// How much of another player's name to draw, everywhere the plugin shows one - the
        /// recruitment card, the party panel, ratings, recent players and profiles.
        ///
        /// Display only: the full name is still what gets looked up, stored and sent. Lives outside
        /// the ratings block because the recruitment card shows names in either build.
        /// </summary>
        public PlayerNameStyle PlayerNameStyle { get; set; } = PlayerNameStyle.FullName;

#if PFP_RATINGS
        // ── Community ratings ─────────────────────────────────────

        /// <summary>
        /// Master switch for every rating feature. Opt-in, not opt-out: the plugin does not look
        /// up, record or send anything about other players until this is deliberately turned on,
        /// because the feature involves data about people who never installed it.
        /// </summary>
        public bool RatingsEnabled { get; set; } = true;

        /// <summary>
        /// Whether this install takes part in the community side of the plugin at all: ratings,
        /// the achievements feed, hearts, votes, and anything sent to the server about a duty.
        ///
        /// TWO SWITCHES, ONE ANSWER, and that is deliberate. Opting out and refusing to send
        /// anonymous usage stats were separate settings that meant the same thing to anybody who
        /// read them: I do not want to be part of this. Honouring the first and quietly ignoring
        /// the second - still posting their clears, still counting their votes, still showing them
        /// a feed built out of everybody's duties - was the plugin deciding it knew better.
        ///
        /// So the stats slider is the floor. Anything below Full opts you out, and opting out is
        /// total: what is left is the presets, which is the part of this plugin that involves
        /// nobody else. See <see cref="AnalyticsMode"/> and PluginUI.RatingSettings.
        ///
        /// Read everywhere the old code read RatingsEnabled. The raw flag survives for the one
        /// thing it is: the local half of the server-side enrolment, written by the settings toggle
        /// and by whatever the server says the enrolment is.
        /// </summary>
        public bool CommunityEnabled => RatingsEnabled && AnalyticsMode == AnalyticsMode.Full;

        /// <summary>
        /// Whether the plugin has been told to stop mentioning community votes.
        ///
        /// Survives restarts, unlike the once-per-session hold, and covers every future poll rather
        /// than the current one. Somebody who says "do not ask again" has not said "not today".
        /// </summary>
        public bool VotePromptSilenced { get; set; }

        /// <summary>Whether to offer the rating prompt after a duty finishes.</summary>
        public bool PostDutyPromptEnabled { get; set; } = true;

        /// <summary>
        /// Whether this character's Ultimate and savage tier clears go to the achievements feed.
        ///
        /// On by default, which is the one default in this file that shares something about the
        /// player rather than about other people - and it shares a clear, which is a thing people
        /// announce in shout the moment it happens. Turning it off stops this client posting AND
        /// hides what is already up there; see PushBroadcastSetting.
        /// </summary>
        public bool BroadcastAchievements { get; set; } = true;

        /// <summary>
        /// How far the achievements feed has been read, as a mark the SERVER issued (unix ms).
        ///
        /// Zero means the tab has never been opened, which is a state in its own right: the badge
        /// shows a dot rather than a number, because counting every clear ever posted and putting
        /// four hundred on a tab is not an invitation, it is a wall. It stops being zero the first
        /// time the feed is actually shown to somebody.
        ///
        /// The server's clock rather than this machine's, on purpose - see AchievementFeedResponse.
        /// </summary>
        public long AchievementsSeenMark { get; set; }

        /// <summary>
        /// The poll whose Vote tab has actually been opened, by slug. Empty until one has been.
        ///
        /// A slug rather than a flag, so the next poll marks the tab by itself: a boolean would have
        /// to be found and reset by hand every time one starts, and the time somebody forgets is the
        /// time nobody is told there is a poll.
        /// </summary>
        public string PollSeenSlug { get; set; } = string.Empty;

        /// <summary>Whether to show ratings beside the members of the party you are actually in.
        /// Deliberately not offered while browsing the Party Finder: a score attached to a listing
        /// you haven't joined turns the feature into a screening tool for strangers, which is not
        /// what it is for.</summary>
        public bool PartyRatingsEnabled { get; set; } = true;

        /// <summary>Whether to keep the local log of players met in duties. Turning this off
        /// empties the rateable list and with it the ability to rate anyone, since rating is gated
        /// on having actually played with the person. Lookup still works.</summary>
        public bool TrackEncounters { get; set; } = true;

        /// <summary>
        /// Whether to show the extra detail the game already has about a listing you open.
        ///
        /// Reads the structure the client fills in to draw the listing window - the jobs sitting in
        /// each slot, the leader, the comment - and shows the parts the window leaves out. Nothing
        /// is fetched and nobody is asked; it is on this machine already.
        ///
        /// Turns itself off while PFRadar is running. Both hook the same game function, and the
        /// second hook onto one function is how unload order starts to matter.
        /// </summary>
        public bool ListingDetailsEnabled { get; set; } = true;

        // ListingDetailsAutoLookup WAS DECLARED HERE AND IS GONE (2026-08-17).
        //
        // It was added for an auto-lookup that was then deliberately not built - the progression
        // button is a button precisely so that browsing the party finder does not fire a network
        // call about every stranger whose listing you open. Nothing ever read the setting, so it
        // was a switch in the config file that could not do anything, which is worse than no switch.

        /// <summary>
        /// Whether this character is published to the party finder while it is in a listing.
        ///
        /// WHAT IT SENDS IS ONLY YOU: your name, world and job, filed against the listing's leader
        /// so other plugin users looking at that listing can see you are in it. Nothing about
        /// anybody else in your party is sent - they have not agreed to be published and there is
        /// no way to ask them - and the server refuses a report that names a different character
        /// than the one signed in.
        ///
        /// The report is withdrawn when the listing ends, and the server forgets it within the hour
        /// either way. Turning this off stops sending and removes what is up.
        /// </summary>
        public bool PfCrowdsourceEnabled { get; set; } = true;

        /// <summary>
        /// Override for the rating server, for development and for anyone self-hosting. Empty
        /// means the built-in endpoint.
        ///
        /// Read once when the client is constructed, so a change needs a plugin reload to take
        /// effect. Whatever is set here receives your character name and the names you look up, so
        /// it should only ever point somewhere you control.
        /// </summary>
        public string RatingApiBaseUrl { get; set; } = string.Empty;

        /// <summary>
        /// The last time this install rated each player, keyed by the canonical "name@world".
        /// Drives the instant client-side half of the 24h cooldown; the server enforces the real
        /// one. Entries are pruned once expired, so this stays small.
        /// </summary>
        public Dictionary<string, DateTime> LocalCooldowns { get; set; } = new();

        /// <summary>
        /// When this install last reported each player, keyed by the canonical "name@world".
        ///
        /// Serves two limits at once: an entry inside the repeat window blocks reporting the same
        /// person again, and the number of entries inside the last hour is how many reports have
        /// been filed in that hour. Both are client-side courtesy checks - the server enforces its
        /// own, and is authoritative if the two disagree.
        /// </summary>
        public Dictionary<string, DateTime> ReportCooldowns { get; set; } = new();
#endif

        [NonSerialized]
        private IDalamudPluginInterface? pluginInterface;

        public void Initialize(IDalamudPluginInterface pi)
        {
            this.pluginInterface = pi;
        }

        public void Save()
        {
            this.pluginInterface?.SavePluginConfig(this);
        }

        // ── Migration ─────────────────────────────────────────────

        /// <summary>
        /// Brings a config saved by an older build up to <see cref="CurrentVersion"/>. Called once at
        /// startup, after the duty data is available. Saves only when something actually changed, so
        /// a current config costs nothing.
        /// </summary>
        public void Migrate(DutyDataHelper dutyDataHelper, IPluginLog log)
        {
            if (Version >= CurrentVersion)
                return;

            int startVersion = Version;

            // v0 -> v1: presets referenced their duty by name only. Back-fill the ContentFinderCondition
            // row id so applying a preset no longer depends on matching display strings.
            if (Version < 1)
            {
                int resolved = 0, unresolved = 0;
                foreach (var preset in Presets)
                {
                    if (preset.DutyRowId != 0 || preset.DutyCategoryId == 0)
                        continue;

                    var duty = dutyDataHelper.FindDutyByName(preset.DutyCategoryName, preset.DutyName);
                    if (duty == null)
                    {
                        unresolved++;
                        continue;
                    }

                    // Synthetic entries have unstable ids, so those keep using the name.
                    if (!DutyDataHelper.IsSyntheticRowId(duty.RowId))
                    {
                        preset.DutyRowId = duty.RowId;
                        resolved++;
                    }
                }
                log.Information($"[Migration] v0 -> v1: resolved {resolved} preset duty id(s), {unresolved} left on name lookup.");
                Version = 1;
            }

            // v1 -> v2: added the community rating settings. Every new field has a usable default
            // and Newtonsoft fills them in on load, so there is nothing to convert - this only
            // repairs a collection that an older hand-edited config could have left null.
            if (Version < 2)
            {
#if PFP_RATINGS
                LocalCooldowns ??= new Dictionary<string, DateTime>();
#endif
                Version = 2;
            }

            // v2 -> v3: added the report cooldown list. Same shape as the v1 -> v2 step - the field
            // has a usable default, so this only repairs a config that predates it or that a hand
            // edit left null.
            if (Version < 3)
            {
#if PFP_RATINGS
                ReportCooldowns ??= new Dictionary<string, DateTime>();
#endif
                Version = 3;
            }

            // v3 -> v4: the analytics on/off switch became a three-level mode. Only an explicit
            // "off" carries meaning - a missing flag is an install that never saw the setting, and
            // consent to send everything is not something to infer from silence either way, so it
            // keeps the default. The old field is cleared so it can never re-apply.
            if (Version < 4)
            {
                if (AnalyticsEnabled == false)
                    AnalyticsMode = AnalyticsMode.Off;

                AnalyticsEnabled = null;
                Version = 4;
            }

            // v4 -> v5: the two progress counters became one. Only the single-fight tally ever
            // moved - the other counted a kind of lookup that was never built - so it carries over
            // whole and nothing is lost.
            if (Version < 5)
            {
                if (LifetimeProgressSpecificFight is int previous && previous > LifetimeProgressFetches)
                    LifetimeProgressFetches = previous;

                LifetimeProgressSpecificFight = null;
                Version = 5;
            }

            // v5 -> v6: presets learned how big a party the duty actually fields, and auto-adjust
            // became an option only trials, raids and high-end duties get. Saved presets predate
            // both, so every one of them is checked against the two rules here rather than left to
            // be discovered wrong: a dungeon preset still advertising eight seats posts four
            // openings nobody can fill, and one still carrying auto-adjust ignores its own slots
            // while greying out the editor you would fix them in.
            //
            // Deliberately built compositions survive - see DutyComposition.Normalize for exactly
            // what is and is not overwritten.
            if (Version < 6)
            {
                int autoCleared = 0, reshaped = 0;
                foreach (var preset in Presets)
                {
                    bool hadAuto = preset.AutoAdjustRoles;
                    int hadSeats = preset.Slots?.Count ?? 0;

                    if (!DutyComposition.Normalize(preset))
                        continue;

                    if (hadAuto && !preset.AutoAdjustRoles) autoCleared++;
                    if ((preset.Slots?.Count ?? 0) != hadSeats) reshaped++;
                }
                log.Information($"[Migration] v5 -> v6: cleared auto-adjust on {autoCleared} preset(s), resized {reshaped}.");
                Version = 6;
            }

            // v6 -> v7: the mentor roulette is entered alone, so a listing for it is one nobody can
            // join - the plugin stopped offering it and the presets already written for it go too.
            //
            // DELETED RATHER THAN REPOINTED. A preset is for a duty; with the duty gone there is
            // nothing left of it worth keeping, and silently aiming it at some other roulette would
            // be worse than removing it - the next time it was applied it would post something
            // nobody chose. The names are logged so it is possible to see what went.
            if (Version < 7)
            {
                var doomed = Presets.Where(DutyDataHelper.IsMentorRoulette).ToList();

                foreach (var preset in doomed)
                    Presets.Remove(preset);

                if (doomed.Count > 0)
                    log.Information("[Migration] v6 -> v7: removed "
                        + $"{doomed.Count} mentor roulette preset(s): "
                        + string.Join(", ", doomed.Select(p => $"\"{p.Name}\"")));

                Version = 7;
            }

            log.Information($"[Migration] Configuration upgraded from v{startVersion} to v{Version}.");
            Save();
        }

        // ── CRUD Operations ───────────────────────────────────────

        /// <summary>
        /// Builds a preset and does NOT keep it.
        ///
        /// The editor opens on one of these. Pressing "New preset" used to call
        /// <see cref="AddPreset"/>, which added the thing to the list and wrote it to disk before
        /// a single field had been filled in - so the preset appeared behind the sheet immediately,
        /// and a session that ended without touching Cancel left it there for good. Cancelling
        /// deleted it again, which is a repair rather than a design: nothing should have to be
        /// unmade because a window was opened.
        ///
        /// Nothing is saved until <see cref="CommitNewPreset"/>.
        /// </summary>
        public PfPresetData CreateDetachedPreset(string? name = null) => new()
        {
            Name = name ?? $"Preset {Presets.Count + 1}",
            LangJapanese = true,
            LangEnglish = true,
            LangGerman = true,
            LangFrench = true,
        };

        /// <summary>Keeps a preset built by <see cref="CreateDetachedPreset"/>. This is the moment
        /// it becomes real: it joins the list, counts towards the tally, and reaches disk.</summary>
        public void CommitNewPreset(PfPresetData preset)
        {
            if (Presets.Any(p => p.Id == preset.Id))
            {
                Save();
                return;
            }

            Presets.Add(preset);
            CountPresetCreated();
            Save();
        }

        /// <summary>Builds a preset and keeps it in one step, for callers with nothing to fill in
        /// first.</summary>
        public PfPresetData AddPreset(string? name = null)
        {
            var preset = CreateDetachedPreset(name);
            CommitNewPreset(preset);
            return preset;
        }

        /// <summary>Adds a preset that came from a share code, giving it a name that doesn't collide
        /// with an existing one. The caller has already validated and sanitized it.</summary>
        public PfPresetData AddImportedPreset(PfPresetData preset)
        {
            string baseName = preset.Name;
            int suffix = 2;
            while (Presets.Any(p => p.Name.Equals(preset.Name, StringComparison.OrdinalIgnoreCase)))
                preset.Name = $"{baseName} ({suffix++})";

            // A share code can carry either of the mistakes the v6 migration repairs, since it may
            // well have been exported by a build from before this.
            DutyComposition.Normalize(preset);

            Presets.Add(preset);
            CountPresetCreated();
            CountPresetImported();
            Save();
            return preset;
        }

        /// <summary>Finds a preset by name for the <c>/pfp apply</c> command: an exact
        /// case-insensitive match wins, otherwise a unique prefix or substring match. Returns null
        /// when nothing matches or when the name is ambiguous (<paramref name="ambiguous"/> tells
        /// the caller which it was).</summary>
        public PfPresetData? FindPresetByName(string name, out bool ambiguous)
        {
            ambiguous = false;
            if (string.IsNullOrWhiteSpace(name))
                return null;

            string query = name.Trim();

            var exact = Presets.Where(p => p.Name.Equals(query, StringComparison.OrdinalIgnoreCase)).ToList();
            if (exact.Count > 0)
                return exact[0];

            foreach (var candidates in new[]
            {
                Presets.Where(p => p.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase)).ToList(),
                Presets.Where(p => p.Name.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList(),
            })
            {
                if (candidates.Count == 1)
                    return candidates[0];
                if (candidates.Count > 1)
                {
                    ambiguous = true;
                    return null;
                }
            }

            return null;
        }

        public bool UpdatePreset(PfPresetData updated)
        {
            int idx = Presets.FindIndex(p => p.Id == updated.Id);
            if (idx < 0)
                return false;

            Presets[idx] = updated;
            Save();
            return true;
        }

        public PfPresetData? DuplicatePreset(string id)
        {
            var original = Presets.Find(p => p.Id == id);
            if (original == null)
                return null;

            var copy = original.Duplicate();
            Presets.Add(copy);
            CountPresetCreated();
            Save();
            return copy;
        }

        public bool DeletePreset(string id)
        {
            int removed = Presets.RemoveAll(p => p.Id == id);
            if (removed > 0)
            {
                Save();
                return true;
            }
            return false;
        }

        public PfPresetData? GetPreset(string id)
        {
            return Presets.Find(p => p.Id == id);
        }

        public void MarkPresetUsed(string id)
        {
            var preset = Presets.Find(p => p.Id == id);
            if (preset != null)
            {
                preset.LastUsedAt = DateTime.UtcNow;
                CountPresetApplied();
                CountDutyApplied(preset.DutyRowId, preset.DutyName, preset.DutyCategoryName);
                Save();
            }
        }

        /// <summary>Moves a preset up in the list order.</summary>
        public void MovePresetUp(string id)
        {
            int idx = Presets.FindIndex(p => p.Id == id);
            if (idx > 0)
            {
                (Presets[idx], Presets[idx - 1]) = (Presets[idx - 1], Presets[idx]);
                Save();
            }
        }

        /// <summary>Moves a preset down in the list order.</summary>
        public void MovePresetDown(string id)
        {
            int idx = Presets.FindIndex(p => p.Id == id);
            if (idx >= 0 && idx < Presets.Count - 1)
            {
                (Presets[idx], Presets[idx + 1]) = (Presets[idx + 1], Presets[idx]);
                Save();
            }
        }
    }
}
