#if PFP_RATINGS
using System;
using System.Collections.Generic;
using Dalamud.Game.DutyState;
using Dalamud.Plugin.Services;

namespace PfPresets
{
    /// <summary>
    /// Watches duty state and records who the player ran content with.
    ///
    /// The party is sampled when the duty starts and again on a slow timer while it runs, because
    /// a duty finder replacement joining after a wipe is exactly the sort of person worth rating,
    /// and a start-only snapshot would miss them. Sampling is deliberately infrequent - reading
    /// the party proxies every frame would be pure waste for data that changes a handful of times
    /// per run.
    ///
    /// A duty counts once it is either cleared, or has lasted <see cref="MinimumSharedTime"/>.
    /// Progression groups that never clear are the common case in Party Finder, and requiring a
    /// completion event excluded exactly the people this is for.
    ///
    /// Nothing here talks to the network. It writes to the local <see cref="EncounterStore"/> and
    /// raises <see cref="EncounterCompleted"/>; whether any of it is ever sent anywhere is the
    /// user's decision, made in the prompt.
    /// </summary>
    internal sealed class DutyTracker : IDisposable
    {
        /// <summary>How often the party is re-sampled during a duty.</summary>
        private static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(15);

        /// <summary>
        /// How often the party is re-read before the roster has been established at all.
        ///
        /// DutyStarted fires while the zone is still loading, so the first sample routinely reads an
        /// empty or half-blank party - the member loop already knows this and skips blank entries
        /// "to be caught next sample". The next sample was fifteen seconds later, and a duty that
        /// ended inside that window recorded nobody at all: no roster, no clear evidence, no duty
        /// filed, and therefore every vote out of it held forever with nothing to check it against.
        ///
        /// So the roster is chased at this cadence until it is actually there, and only then does
        /// sampling settle down to the slow interval above.
        /// </summary>
        private static readonly TimeSpan SettleInterval = TimeSpan.FromSeconds(1);

        /// <summary>
        /// How long to keep chasing before accepting that the party really is empty.
        ///
        /// Solo content is a legitimate empty roster and must not be retried forever at one second.
        /// </summary>
        private static readonly TimeSpan SettleWindow = TimeSpan.FromSeconds(45);

        /// <summary>
        /// How long a duty has to last before it counts as having played with someone, even if it
        /// was never cleared.
        ///
        /// Most Party Finder raiding is progression: hours with the same seven people and no clear
        /// at the end. Waiting for a completion event means those groups - the ones this feature
        /// exists for - are never rateable at all. Nine minutes is long enough to have pulled a few
        /// times, short enough that a wrong-instance bail-out doesn't qualify.
        /// </summary>
        private static readonly TimeSpan MinimumSharedTime = TimeSpan.FromMinutes(9);


        private readonly IDutyState dutyState;
        private readonly IFramework framework;
        private readonly IClientState clientState;
        private readonly IPluginLog log;
        private readonly Configuration config;
        private readonly PfAutomation pfAutomation;
        private readonly WorldHelper worldHelper;
        private readonly DutyDataHelper dutyDataHelper;
        private readonly EncounterStore store;
        private readonly SocialLinkResolver socialLinks;

        /// <summary>The duty in progress, or null when not in one. Members accumulate here and are
        /// only committed to the store when the duty actually completes.</summary>
        private DutyEncounter? active;

        /// <summary>Content ids already recorded for the active duty, so re-sampling doesn't add
        /// the same person repeatedly.</summary>
        private readonly HashSet<ulong> seen = new();

        /// <summary>
        /// The same, keyed on name and world, for members whose content id reads as zero.
        ///
        /// The content id is the right key when it is there and it is frequently not - an alliance
        /// member outside the local party, or anybody read mid-load. The old guard was
        /// `ContentId != 0 && !seen.Add(...)`, which short-circuits to FALSE when the id is zero and
        /// therefore added that member again on every single sample: once every fifteen seconds for
        /// the length of the duty. That is where "party of 25" came from, and the server refused
        /// those duties outright rather than recording a roster with duplicates in it.
        /// </summary>
        private readonly HashSet<string> seenByName = new(StringComparer.OrdinalIgnoreCase);

        private DateTime lastSampleUtc = DateTime.MinValue;

        /// <summary>When the active duty began being chased, for <see cref="SettleWindow"/>.</summary>
        private DateTime settleStartedUtc = DateTime.MinValue;

        /// <summary>Raised on the framework thread when a duty finishes with rateable players in
        /// it. The UI subscribes to raise its prompt.</summary>
        public event Action<DutyEncounter>? EncounterCompleted;

        public DutyTracker(
            IDutyState dutyState,
            IFramework framework,
            IClientState clientState,
            IPluginLog log,
            Configuration config,
            PfAutomation pfAutomation,
            WorldHelper worldHelper,
            DutyDataHelper dutyDataHelper,
            EncounterStore store,
            SocialLinkResolver socialLinks)
        {
            this.dutyState = dutyState;
            this.framework = framework;
            this.clientState = clientState;
            this.log = log;
            this.config = config;
            this.pfAutomation = pfAutomation;
            this.worldHelper = worldHelper;
            this.dutyDataHelper = dutyDataHelper;
            this.store = store;
            this.socialLinks = socialLinks;

            this.dutyState.DutyStarted += OnDutyStarted;
            this.dutyState.DutyCompleted += OnDutyCompleted;
            this.clientState.Logout += OnLogout;
            this.framework.Update += OnFrameworkUpdate;
        }

        // ══════════════════════════════════════════════════════════
        //  DUTY LIFECYCLE
        // ══════════════════════════════════════════════════════════

        private void OnDutyStarted(IDutyStateEventArgs args)
        {
            if (!IsTrackingEnabled())
                return;

            try
            {
                uint dutyRowId = args.ContentFinderCondition.RowId;

                active = new DutyEncounter
                {
                    DutyRowId = dutyRowId,
                    DutyName = ResolveDutyName(dutyRowId),
                    StartedUtc = DateTime.UtcNow,
                };
                seen.Clear();
                seenByName.Clear();
                lastSampleUtc = DateTime.MinValue;
                settleStartedUtc = DateTime.UtcNow;

                SampleParty();
            }
            catch (Exception ex)
            {
                log.Debug($"[Ratings] Failed to start duty tracking: {ex.Message}");
                Reset();
            }
        }

        private void OnDutyCompleted(IDutyStateEventArgs args) => Finish(cleared: true);

        /// <summary>The local player's current job, or 0 if they cannot be read - which happens on
        /// a zone change racing the duty's end.</summary>
        private uint LocalJob()
        {
            try
            {
                var player = pfAutomation.PlayerState;
                return player.IsLoaded ? player.ClassJob.RowId : 0u;
            }
            catch (Exception)
            {
                return 0u;
            }
        }

        /// <summary>
        /// Commits the duty if it's worth remembering, and clears the in-progress state either way.
        ///
        /// A clear always counts. Anything else counts once it has run for
        /// <see cref="MinimumSharedTime"/> - which is what makes a two-hour prog session rateable
        /// without also capturing the thirty seconds someone spent in the wrong instance.
        /// </summary>
        private void Finish(bool cleared)
        {
            if (active == null)
                return;

            try
            {
                var elapsed = DateTime.UtcNow - active.StartedUtc;

                // A CLEAR, OR NINE MINUTES. A departure is deliberately NOT a third way in: a
                // duty somebody walked out of inside nine minutes is a duty that did not really
                // happen, and recording it would file a roster for a room nobody played in. What a
                // departure does is force a sample - see NoteDepartures - so the roster is whole at
                // the moment somebody leaves rather than up to fifteen seconds stale.
                bool worthKeeping = cleared || elapsed >= MinimumSharedTime;

                if (!worthKeeping)
                {
                    log.Debug($"[Ratings] Duty lasted {elapsed.TotalMinutes:0.#}m; too short to record.");
                    return;
                }

                // One last sample: someone who joined late still counts, and this is the last
                // moment the party is guaranteed to still be assembled.
                SampleParty();

                active.CompletedUtc = DateTime.UtcNow;
                active.Cleared = cleared;
                active.LocalJobId = LocalJob();

                // One more try at the name if it did not resolve at the start. The sheet lookup can
                // fail while the zone is still loading and succeed perfectly well a few minutes
                // later, and the name is what decides whether this duty ever reaches the feed.
                if (string.IsNullOrWhiteSpace(active.DutyName))
                    active.DutyName = ResolveDutyName(active.DutyRowId);

                if (active.Members.Count > 0 && IsTrackingEnabled())
                {
                    var finished = active;
                    store.Add(finished);
                    EncounterCompleted?.Invoke(finished);
                }
            }
            catch (Exception ex)
            {
                log.Debug($"[Ratings] Failed to record a duty: {ex.Message}");
            }
            finally
            {
                Reset();
            }
        }

        private void OnFrameworkUpdate(IFramework _)
        {
            bool inDuty = pfAutomation.IsInDuty();

            if (active == null)
            {
                // Fallback start. DutyStarted is the normal path, but it is missed if the plugin
                // was loaded or reloaded partway through a duty - and losing a two-hour prog
                // session to a plugin reload is exactly what this feature can't afford.
                if (inDuty && IsTrackingEnabled())
                    BeginFromCurrentDuty();
                return;
            }

            // Left the instance. Whether that's worth recording is Finish's decision now, not a
            // blanket discard - most Party Finder raiding never fires a completion at all.
            if (!inDuty)
            {
                Finish(cleared: false);
                return;
            }

            // THE ROSTER IS CHASED UNTIL IT EXISTS, then sampled slowly.
            //
            // Until somebody has actually been read, this runs every second rather than every
            // fifteen: DutyStarted fires while the zone is still loading, and a duty that ended
            // before the second sample used to record an empty party. An empty party is not a
            // harmless gap - it produces no clear evidence, so no duty is ever filed for it, so
            // every vote cast out of that duty is held forever against a record that does not
            // exist. Chasing costs one party read a second for at most three quarters of a minute.
            bool settled = active.Members.Count > 0
                || DateTime.UtcNow - settleStartedUtc > SettleWindow;

            // THE MOMENT THE DUTY BECOMES WORTH RECORDING, the roster is read again.
            //
            // Nine minutes is the line between a duty that is discarded and one that is filed, and
            // until now the reading either side of it was whatever the fifteen-second timer
            // happened to have. Confirming it on the crossing costs one party read per duty and
            // means the roster that gets filed was taken while the duty was definitely real - the
            // first of the three moments a snapshot is worth taking. The other two are a departure
            // after that point (see NoteDepartures) and the duty ending (see Finish).
            if (!active.PassedMinimum && DateTime.UtcNow - active.StartedUtc >= MinimumSharedTime)
            {
                active.PassedMinimum = true;
                SampleParty();
                return;
            }

            if (DateTime.UtcNow - lastSampleUtc < (settled ? SampleInterval : SettleInterval))
                return;

            SampleParty();
        }

        /// <summary>Starts tracking a duty already in progress, for the case where DutyStarted was
        /// never seen. The real start time is unknown so it's taken as now, which only ever makes
        /// the nine-minute threshold harder to reach, never easier.</summary>
        private void BeginFromCurrentDuty()
        {
            try
            {
                uint dutyRowId = dutyState.ContentFinderCondition.RowId;

                active = new DutyEncounter
                {
                    DutyRowId = dutyRowId,
                    DutyName = ResolveDutyName(dutyRowId),
                    StartedUtc = DateTime.UtcNow,
                };
                seen.Clear();
                seenByName.Clear();
                lastSampleUtc = DateTime.MinValue;
                settleStartedUtc = DateTime.UtcNow;

                SampleParty();
                log.Debug("[Ratings] Picked up a duty already in progress.");
            }
            catch (Exception ex)
            {
                log.Debug($"[Ratings] Couldn't pick up the running duty: {ex.Message}");
                Reset();
            }
        }

        private void OnLogout(int type, int code) => Finish(cleared: false);

        private void Reset()
        {
            active = null;
            seen.Clear();
            seenByName.Clear();
            lastSampleUtc = DateTime.MinValue;
            settleStartedUtc = DateTime.MinValue;
        }

        // ══════════════════════════════════════════════════════════
        //  PARTY SAMPLING
        // ══════════════════════════════════════════════════════════

        /// <summary>
        /// Reads the party now and folds anybody new into the active roster.
        ///
        /// CADENCE IS THE CALLER'S, not this method's. It used to carry a second copy of the
        /// fifteen-second interval, which meant the settle chase below could ask for a re-read every
        /// second and be silently refused fourteen times out of fifteen by the very method it was
        /// calling - the retry existed and did nothing.
        /// </summary>
        private void SampleParty()
        {
            if (active == null)
                return;

            lastSampleUtc = DateTime.UtcNow;

            try
            {
                // Counted before the loop adds to it, so the comparison below is against the roster
                // as it stood at the last sample rather than as it stands after this one.
                int knownBefore = active.Members.Count;
                int presentNow = 0;

                foreach (var member in pfAutomation.GetOtherPartyMemberDetails())
                {
                    presentNow++;
                    if (member.ContentId != 0 && !seen.Add(member.ContentId))
                        continue;

                    string world = worldHelper.GetWorldName(member.HomeWorldId);
                    if (string.IsNullOrWhiteSpace(member.Name) || string.IsNullOrWhiteSpace(world))
                        continue; // transient blank during a zone change - it'll be caught next sample

                    // A ZERO CONTENT ID IS NOT A NEW PERSON. Deduplicated on name and world instead,
                    // which is what identifies a character everywhere else in this plugin. Checked
                    // after the blank guard above so a half-read entry cannot claim the name.
                    if (member.ContentId == 0 && !seenByName.Add($"{member.Name}@{world}"))
                        continue;

                    active.Members.Add(new EncounterMember
                    {
                        Name = member.Name,
                        World = world,
                        JobId = member.JobId,
                        Social = socialLinks.Resolve(member.Name, member.HomeWorldId, member.FcTag),
                        AllianceIndex = member.AllianceIndex,
                    });
                }

                NoteDepartures(knownBefore, presentNow);
            }
            catch (Exception ex)
            {
                log.Debug($"[Ratings] Party sample failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Notices that the party has got smaller.
        ///
        /// WHAT THIS DOES NOT DO IS MAKE THE DUTY COUNT. A duty is recorded when it clears or when
        /// it has run <see cref="MinimumSharedTime"/>, and somebody leaving before that is a duty
        /// that fell apart rather than one that happened - filing it would put a roster on record
        /// for a room nobody really played in, which is a false positive of exactly the kind this
        /// is all trying to avoid.
        ///
        /// What it does is mark the roster as worth confirming. Sampling settles to once every
        /// fifteen seconds, so the last reading before somebody walked out can be a quarter of a
        /// minute old; a departure is the one moment where the party is provably different from
        /// what was last read, and the cheapest response is to read it again now.
        ///
        /// The person who left stays in the roster either way - members accumulate and are never
        /// removed - so nobody loses the ability to rate them by leaving.
        /// </summary>
        private void NoteDepartures(int knownBefore, int presentNow)
        {
            if (active == null)
                return;

            // Nothing to have left yet. The first samples of a duty run while the zone is still
            // loading and read a party that is still filling in; treating that as people leaving
            // would fire on every duty, which is the opposite of the point.
            if (knownBefore == 0 || presentNow >= knownBefore)
                return;

            // Only once the duty is old enough to be recorded at all. Before that the departure
            // changes nothing - the duty is going to be discarded - so there is nothing worth
            // confirming and no reason to spend the read.
            if (DateTime.UtcNow - active.StartedUtc < MinimumSharedTime)
                return;

            if (active.SawDeparture)
                return;

            active.SawDeparture = true;

            // Read again right now rather than waiting out the rest of the interval. This is the
            // one instant the party is known to differ from the last reading.
            SampleParty();

            log.Debug($"[Ratings] Party shrank from {knownBefore} to {presentNow} after "
                + $"{MinimumSharedTime.TotalMinutes:0}m; roster confirmed.");
        }

        private string ResolveDutyName(uint dutyRowId)
        {
            try
            {
                return dutyDataHelper.GetDutyName(dutyRowId) ?? string.Empty;
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        private bool IsTrackingEnabled() => config.CommunityEnabled && config.TrackEncounters;

        public void Dispose()
        {
            try
            {
                dutyState.DutyStarted -= OnDutyStarted;
                dutyState.DutyCompleted -= OnDutyCompleted;
                clientState.Logout -= OnLogout;
                framework.Update -= OnFrameworkUpdate;
                store.Flush();
            }
            catch (Exception)
            {
                // Nothing useful to do while unloading.
            }
        }
    }
}
#endif
