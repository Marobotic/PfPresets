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

        private DateTime lastSampleUtc = DateTime.MinValue;

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
                lastSampleUtc = DateTime.MinValue;

                SampleParty();
            }
            catch (Exception ex)
            {
                log.Debug($"[Ratings] Failed to start duty tracking: {ex.Message}");
                Reset();
            }
        }

        private void OnDutyCompleted(IDutyStateEventArgs args) => Finish(cleared: true);

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
                bool worthKeeping = cleared || elapsed >= MinimumSharedTime;

                if (!worthKeeping)
                {
                    log.Debug($"[Ratings] Duty lasted {elapsed.TotalMinutes:0.#}m; too short to record.");
                    return;
                }

                // One last sample: someone who joined late still counts, and this is the last
                // moment the party is guaranteed to still be assembled.
                SampleParty(force: true);

                active.CompletedUtc = DateTime.UtcNow;
                active.Cleared = cleared;

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

            if (DateTime.UtcNow - lastSampleUtc < SampleInterval)
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
                lastSampleUtc = DateTime.MinValue;

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
            lastSampleUtc = DateTime.MinValue;
        }

        // ══════════════════════════════════════════════════════════
        //  PARTY SAMPLING
        // ══════════════════════════════════════════════════════════

        private void SampleParty(bool force = false)
        {
            if (active == null)
                return;

            if (!force && DateTime.UtcNow - lastSampleUtc < SampleInterval)
                return;

            lastSampleUtc = DateTime.UtcNow;

            try
            {
                foreach (var member in pfAutomation.GetOtherPartyMemberDetails())
                {
                    if (member.ContentId != 0 && !seen.Add(member.ContentId))
                        continue;

                    string world = worldHelper.GetWorldName(member.HomeWorldId);
                    if (string.IsNullOrWhiteSpace(member.Name) || string.IsNullOrWhiteSpace(world))
                        continue; // transient blank during a zone change - it'll be caught next sample

                    active.Members.Add(new EncounterMember
                    {
                        Name = member.Name,
                        World = world,
                        JobId = member.JobId,
                        Social = socialLinks.Resolve(member.Name, member.HomeWorldId, member.FcTag),
                        AllianceIndex = member.AllianceIndex,
                    });
                }
            }
            catch (Exception ex)
            {
                log.Debug($"[Ratings] Party sample failed: {ex.Message}");
            }
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

        private bool IsTrackingEnabled() => config.RatingsEnabled && config.TrackEncounters;

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
