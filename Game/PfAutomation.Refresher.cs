using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace PfPresets
{
    /// <summary>
    /// The Auto Refresher: periodically re-posts the player's Party Finder listing by
    /// opening it and clicking Edit -> Recruit, exactly like doing it by hand.
    /// </summary>
    public partial class PfAutomation
    {
        // Native "open party finder recruitment for content id" function, imported exactly from
        // the RecruitmentRefresher plugin (https://github.com/anya-hichu/RecruitmentRefresher).
        // This opens the player's own recruitment detail window, which is what makes the
        // auto-refresh actually work (AgentLookingForGroup.OpenListingByContentId does not).
        private const string OpenPartyFinderSignature = "40 53 48 83 EC 20 48 8B D9 E8 ?? ?? ?? ?? 84 C0 74 07 C6 83 ?? ?? ?? ?? ?? 48 83 C4 20 5B C3 CC CC CC CC CC CC CC CC CC CC CC CC CC CC CC CC CC 40 53";
        private unsafe delegate void OpenPartyFinderDelegate(void* agentLfg, ulong contentId);
        private readonly OpenPartyFinderDelegate? openPartyFinder;

        /// <summary>Set on Dispose so the fire-and-forget refresh task stops touching the game.</summary>
        private volatile bool disposed = false;

        private int refreshCount = 0;
        private byte[]? previousCommentBytes = null;
        private volatile bool isRefreshExecuting = false;

        /// <summary>Total time the current listing has been kept alive by the refresher, in hours.
        /// Reset whenever recruitment stops. Deliberately not persisted: the cap is about one
        /// unattended session, so logging out or ending the listing starts the budget over.</summary>
        private double hoursRecruiting = 0;

        /// <summary>Set once the runtime cap is hit so we stop refreshing and only say so once.</summary>
        private bool maxDurationReached = false;

        /// <summary>
        /// Bounds for "refresh at N minutes left". A listing lives 60 minutes.
        ///
        /// The floor leaves room for a refresh that does not take to be noticed and tried again
        /// before the listing runs out - a check thirty seconds after, then another attempt. The
        /// ceiling keeps a fresh listing from counting as already due: at 55, a listing just
        /// posted waits five minutes, not none.
        /// </summary>
        public const int MinRefreshAtMinutesLeft = 3;
        public const int MaxRefreshAtMinutesLeft = 55;

        /// <summary>Upper bound for the "stop after" cap, in hours (0 = no limit).</summary>
        public const int MaxRefreshDurationHours = 24;

        /// <summary>
        /// Resolves the native OpenPartyFinder function once at load. If the signature can't be
        /// found (e.g. after a game patch), auto-refresh is disabled gracefully rather than throwing.
        /// </summary>
        private OpenPartyFinderDelegate? ResolveOpenPartyFinder(ISigScanner sigScanner)
        {
            try
            {
                var ptr = sigScanner.ScanText(OpenPartyFinderSignature);
                return Marshal.GetDelegateForFunctionPointer<OpenPartyFinderDelegate>(ptr);
            }
            catch (Exception ex)
            {
                pluginLog.Error(ex, "[AutoRefresher] Failed to resolve OpenPartyFinder signature; auto-refresh disabled.");
                return null;
            }
        }

        /// <summary>When this plugin last clicked Recruit Members itself.</summary>
        private long listingSubmittedTick = long.MinValue / 2;

        /// <summary>How long OwnListingId is believed after our own submit, while the online
        /// status catches up. A second or two is normal; ten is generous and self-clearing.</summary>
        private const long SubmitGraceMs = 10_000;

        internal void MarkListingSubmitted()
        {
            listingSubmittedTick = Environment.TickCount64;

            // Anything we post resets the listing's clock, so whatever the schedule was sleeping
            // towards is now wrong. Check it the way a refresh is checked, rather than refreshing
            // a listing that was renewed a moment ago.
            if (refreshPhase is RefreshPhase.Waiting or RefreshPhase.Due && !refreshStepInFlight)
            {
                refreshPhase = RefreshPhase.Verifying;
                refreshNextAt = DateTime.UtcNow + VerifyAfter;
                refreshDueAt = refreshNextAt;
            }
        }

        /// <summary>
        /// Whether this character actually has a Party Finder listing up.
        ///
        /// Deliberately NOT ConditionFlag.UsingPartyFinder, which is what this used to be: that
        /// flag is set whenever the Party Finder *window* is open, so idly browsing listings made
        /// the plugin believe it was recruiting.
        ///
        /// OnlineStatus 26 is the authority, and is the only thing here that is: it is the game's
        /// own "Recruiting Party Members" state, the one other players see next to your name, so
        /// it is true exactly while a listing is up and false the instant it isn't.
        ///
        /// OwnListingId is NOT evidence on its own, which is what this used to get wrong. It
        /// outlives the listing it names and is repopulated whenever the agent reloads its cache -
        /// which happens just from opening the Party Finder or the Recruitment Criteria window. So
        /// browsing someone else's listing, or opening the native recruit window without posting
        /// anything, produced a full "Your Recruitment" card with a counting-down timer and an End
        /// Recruitment button.
        ///
        /// It is still worth something in exactly one case: the beat between our own submit
        /// landing and the online status updating. So it counts only just after we clicked the
        /// button ourselves, and never otherwise.
        /// </summary>
        public unsafe bool IsRecruiting()
        {
            if (RecruitingStatusSet())
                return true;

            if (Environment.TickCount64 - listingSubmittedTick > SubmitGraceMs)
                return false;

            try
            {
                var agent = FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentLookingForGroup.Instance();
                return agent != null && agent->OwnListingId != 0;
            }
            catch (Exception)
            {
                // Agent unavailable during a zone change.
                return false;
            }
        }

        /// <summary>OnlineStatus row for "Recruiting Party Members".</summary>
        private const uint RecruitingOnlineStatusId = 26;

        /// <summary>
        /// Whether this character has "Recruiting Party Members" among its online statuses.
        ///
        /// NOT THE DISPLAYED STATUS. A character shows one status, the highest-ranked of those set,
        /// and Away from Keyboard, Busy, In Duty, Viewing Cutscene and a handful more all outrank
        /// Recruiting. Reading the displayed one decided that a host who stepped away from the
        /// keyboard had stopped recruiting - the exact moment a listing most needs looking after.
        /// The info module holds every status that is set; the displayed one is only the fallback.
        /// </summary>
        public unsafe bool RecruitingStatusSet()
        {
            var info = FFXIVClientStructs.FFXIV.Client.UI.Info.InfoModule.Instance();
            if (info != null && info->IsOnlineStatusSet((byte)RecruitingOnlineStatusId))
                return true;

            return objectTable.LocalPlayer?.OnlineStatus.RowId == RecruitingOnlineStatusId;
        }

        /// <summary>Refresh once this many minutes or fewer are left on the listing. Falls back to
        /// the equivalent of the old fixed interval for an install that never set it.</summary>
        public int RefreshAtMinutesLeft =>
            Math.Clamp(config.AutoRefreshAtMinutesLeft ?? (60 - config.AutoRefresherIntervalMinutes),
                MinRefreshAtMinutesLeft, MaxRefreshAtMinutesLeft);

        /// <summary>True while the refresher is looking after a listing: the feature is enabled,
        /// a Party Finder is up (preset-made or manual), and the runtime cap hasn't been hit.</summary>
        public bool IsRefreshTimerRunning => config.AutoRefresherEnabled && IsRecruiting() && !maxDurationReached;

        /// <summary>True once the "stop after N hours" cap has ended refreshing for this listing.</summary>
        public bool HasReachedMaxDuration => maxDurationReached;

        /// <summary>Hours the current listing has been kept alive by the refresher.</summary>
        public double HoursRecruiting => hoursRecruiting;

        // ══════════════════════════════════════════════════════════
        //  THE SCHEDULE
        //
        //  Driven by the listing's own clock, not by one of ours. The old refresher counted N
        //  minutes and fired, whatever the listing said - so a listing re-posted by hand in the
        //  meantime was refreshed again for nothing, and a refresh that silently failed was not
        //  noticed until the listing had expired.
        //
        //  Now every step is checked against the game:
        //
        //    Reading    open our listing, read how long it has left, close it again
        //    Waiting    sleep until exactly (time left - threshold)
        //    Due        refresh: Edit -> Recruit, as before
        //    Verifying  thirty seconds later, read the clock again. Above the threshold means the
        //               refresh took: back to Waiting. Still at or under means it did not: refresh
        //               again. Nothing moves on to waiting until a refresh is seen to have worked.
        // ══════════════════════════════════════════════════════════

        public enum RefreshPhase
        {
            /// <summary>Not looking after a listing.</summary>
            Idle,

            /// <summary>Finding out how long the listing has left.</summary>
            Reading,

            /// <summary>Asleep until the listing reaches the threshold.</summary>
            Waiting,

            /// <summary>At the threshold; the refresh starts at <see cref="refreshNextAt"/>.</summary>
            Due,

            /// <summary>Re-posting the listing.</summary>
            Refreshing,

            /// <summary>Waiting out the pause after a refresh, then checking it took.</summary>
            Verifying,
        }

        /// <summary>How long after a refresh before checking it took. The listing has to come back
        /// from the server before its clock reads fresh.</summary>
        private static readonly TimeSpan VerifyAfter = TimeSpan.FromSeconds(30);

        /// <summary>A read that could not be taken - the window never came, or the player is using
        /// the Party Finder - is tried again after this.</summary>
        private static readonly TimeSpan ReadRetryAfter = TimeSpan.FromSeconds(10);

        /// <summary>After this many refreshes in a row that did not take, slow down: something is
        /// in the way, and hammering the window every thirty seconds will not move it.</summary>
        private const int FailedRefreshesBeforeBackoff = 3;
        private static readonly TimeSpan FailedRefreshBackoff = TimeSpan.FromSeconds(60);

        /// <summary>Reads that fail this many times in a row fall back to the estimate from when
        /// recruitment was first seen, rather than leaving the listing unattended.</summary>
        private const int FailedReadsBeforeEstimate = 3;

        /// <summary>A reading within this of the threshold counts as at it, so a listing is not
        /// scheduled for a wait of two seconds and then read all over again.</summary>
        private static readonly TimeSpan ThresholdSlack = TimeSpan.FromSeconds(5);

        private volatile RefreshPhase refreshPhase = RefreshPhase.Idle;

        /// <summary>When the refresher next looks at the phase it is in. Moved by retries and holds,
        /// so it is NOT what the footer counts down to - see <see cref="refreshDueAt"/>.</summary>
        private DateTime refreshNextAt = DateTime.MinValue;

        /// <summary>The real deadline of the step in hand: when the listing reaches the threshold,
        /// or when the check after a refresh is due. Only the schedule moves it; a hold or a retry
        /// never does, so the countdown on screen is the process's own timer.</summary>
        private DateTime refreshDueAt = DateTime.MinValue;
        private volatile bool refreshStepInFlight;
        private int failedRefreshes;
        private int failedReads;
        private bool warnedRefreshNotTaking;

        public RefreshPhase CurrentRefreshPhase => refreshPhase;

        /// <summary>Seconds until the next refresh, while one is scheduled. Zero while the
        /// refresher is reading, refreshing or checking - see <see cref="CurrentRefreshPhase"/>.</summary>
        public double SecondsUntilNextRefresh =>
            refreshPhase is RefreshPhase.Waiting or RefreshPhase.Due
                ? Math.Max(0.0, (refreshDueAt - DateTime.UtcNow).TotalSeconds)
                : 0.0;

        /// <summary>Seconds until the check after a refresh, while waiting for it.</summary>
        public double SecondsUntilVerify =>
            refreshPhase == RefreshPhase.Verifying
                ? Math.Max(0.0, (refreshDueAt - DateTime.UtcNow).TotalSeconds)
                : 0.0;

        /// <summary>Called every framework update. Cheap on every frame but the one where a step
        /// is due: a phase check and a clock comparison.</summary>
        public void UpdateAutoRefresher(double deltaMins)
        {
            if (!config.AutoRefresherEnabled || !IsRecruiting())
            {
                // Recruitment ended (or the feature was turned off): the next listing starts with a
                // fresh read and a fresh duration budget.
                previousCommentBytes = null;
                refreshCount = 0;
                hoursRecruiting = 0;
                maxDurationReached = false;
                ResetRefreshSchedule();
                return;
            }

            if (maxDurationReached)
                return;

            hoursRecruiting += deltaMins / 60.0;

            int maxHours = Math.Clamp(config.AutoRefresherMaxHours, 0, MaxRefreshDurationHours);
            if (maxHours > 0 && hoursRecruiting >= maxHours)
            {
                // Stop renewing but leave the listing alone - it expires on its own, exactly as it
                // would have if the refresher had never been running.
                maxDurationReached = true;
                refreshPhase = RefreshPhase.Idle;
                pluginLog.Information($"[AutoRefresher] Reached the {maxHours}h limit after {refreshCount} refresh(es); no longer re-posting.");
                chatGui.Print($"[PF Analysis] Auto-refresh stopped after {maxHours} hour(s). Your listing will expire normally.");
                return;
            }

            // A coordinated handoff is taking the listing down and putting another up; refreshing
            // in the middle of that would re-post the one being replaced.
            if (refreshStepInFlight || isRefreshExecuting || disposed || IsCoordinationBusy)
                return;

            var now = DateTime.UtcNow;

            switch (refreshPhase)
            {
                case RefreshPhase.Idle:
                    // A listing we have just posted ourselves has its full hour: nothing to read.
                    // Anything else - posted by hand, inherited from a reload - is read first.
                    if (Environment.TickCount64 - listingSubmittedTick < SubmitGraceMs)
                    {
                        ScheduleFrom(ListingLifetime
                            - TimeSpan.FromMilliseconds(Environment.TickCount64 - listingSubmittedTick),
                            verifying: false);
                        break;
                    }

                    refreshPhase = RefreshPhase.Reading;
                    refreshNextAt = now + TimeSpan.FromSeconds(3);
                    break;

                case RefreshPhase.Waiting:
                    AdoptFresherReading(now);
                    if (now >= refreshNextAt)
                    {
                        refreshPhase = RefreshPhase.Due;
                        refreshNextAt = now;
                        refreshDueAt = now;
                    }
                    break;

                case RefreshPhase.Due:
                    if (now >= refreshNextAt && CanTouchTheWindow(forRefresh: true))
                        StartScheduledRefresh();
                    break;

                case RefreshPhase.Reading:
                case RefreshPhase.Verifying:
                    if (now >= refreshNextAt)
                        StartScheduledRead(verifying: refreshPhase == RefreshPhase.Verifying);
                    break;
            }
        }

        /// <summary>When the current wait was worked out. A reading of the listing's clock taken
        /// after this - the player opening their own listing, say - is newer than the schedule.</summary>
        private DateTime scheduledAt = DateTime.MinValue;

        /// <summary>
        /// Moves the wait onto any exact reading of the listing's clock newer than the one it was
        /// worked out from.
        ///
        /// The player re-posting by hand, the slot adjuster re-posting, or anything else renewing
        /// the listing, puts the clock somewhere the schedule did not expect. The game hands us the
        /// new value whenever the listing's window is opened, and there is no reason to sleep
        /// towards a time that is no longer true when a better one is sitting right there.
        /// </summary>
        private void AdoptFresherReading(DateTime now)
        {
            if (capturedAt <= scheduledAt)
                return;

            var left = ComputeTimeLeft(out bool exact);
            scheduledAt = now;

            if (!exact || left == null)
                return;

            ScheduleFrom(left.Value, verifying: false);
        }

        private void ResetRefreshSchedule()
        {
            refreshPhase = RefreshPhase.Idle;
            refreshNextAt = DateTime.MinValue;
            refreshDueAt = DateTime.MinValue;
            dueHeldSince = DateTime.MinValue;
            holdReason = null;
            failedRefreshes = 0;
            failedReads = 0;
            warnedRefreshNotTaking = false;
        }

        /// <summary>
        /// Whether now is a moment the refresher may open the Party Finder.
        ///
        /// Not while the player is using it - opening our listing would take over the window they
        /// are reading - and not mid-fight, mid-cutscene or mid-zone, where the window either
        /// cannot open or would open over something that matters more. The step waits a few
        /// seconds and asks again; the listing's own clock is what it is racing, and minutes of
        /// slack are built into the threshold for exactly this.
        /// </summary>
        private bool CanTouchTheWindow(bool forRefresh = false)
        {
            // THE PLAYER IN THE PARTY FINDER holds up a refresh, which takes the window over for a
            // few seconds - but only for a minute, because the listing's clock does not wait for
            // them. It does not hold up a read at all: a read opens our listing and presses Back,
            // and the game itself can leave the Party Finder open after a refresh, which would
            // otherwise stall the check after it for as long as nobody closed it.
            if (forRefresh && PartyFinderWindowOnScreen())
            {
                if (dueHeldSince == DateTime.MinValue)
                    dueHeldSince = DateTime.UtcNow;

                if (DateTime.UtcNow - dueHeldSince < DueHoldLimit)
                {
                    refreshNextAt = DateTime.UtcNow + TimeSpan.FromSeconds(1);
                    holdReason = "Party Finder open";
                    return false;
                }
            }

            if (condition[ConditionFlag.InCombat]
                || condition[ConditionFlag.BetweenAreas]
                || condition[ConditionFlag.BetweenAreas51]
                || condition[ConditionFlag.OccupiedInCutSceneEvent]
                || condition[ConditionFlag.WatchingCutscene])
            {
                refreshNextAt = DateTime.UtcNow + TimeSpan.FromSeconds(1);
                holdReason = condition[ConditionFlag.InCombat] ? "in combat" : "busy";
                return false;
            }

            dueHeldSince = DateTime.MinValue;
            holdReason = null;
            return true;
        }

        /// <summary>
        /// Whether one of the Party Finder's own windows is actually on screen.
        ///
        /// NOT ConditionFlag.UsingPartyFinder. That flag stays set for as long as the listing is
        /// up, window or no window, so asking it held every due refresh for the full minute and
        /// made the countdown loop. The windows are the thing the player would lose.
        /// </summary>
        private unsafe bool PartyFinderWindowOnScreen()
        {
            foreach (var name in PartyFinderWindows)
            {
                var addon = (AtkUnitBase*)(nint)gameGui.GetAddonByName(name);
                if (addon != null && addon->IsVisible)
                    return true;
            }

            return false;
        }

        private static readonly string[] PartyFinderWindows =
        {
            "LookingForGroup", "LookingForGroupDetail", "LookingForGroupCondition",
        };

        /// <summary>Why a due step is not running yet, for the footer. Null when nothing is
        /// holding it.</summary>
        private volatile string? holdReason;

        public string? RefreshHoldReason => holdReason;

        /// <summary>How long a due refresh waits for the player to finish in the Party Finder.</summary>
        private static readonly TimeSpan DueHoldLimit = TimeSpan.FromSeconds(60);
        private DateTime dueHeldSince = DateTime.MinValue;

        private void StartScheduledRead(bool verifying)
        {
            if (!CanTouchTheWindow())
                return;

            refreshStepInFlight = true;

            _ = Task.Run(async () =>
            {
                try
                {
                    var left = await ReadOwnListingTimeLeftAsync();

                    if (left == null)
                    {
                        failedReads++;

                        // The listing's own window would not tell us. After a few tries, go on the
                        // estimate from when recruitment was first seen rather than stall - an
                        // unattended listing expiring is worse than a refresh a minute early.
                        if (failedReads >= FailedReadsBeforeEstimate)
                        {
                            left = ComputeTimeLeft(out _);
                            pluginLog.Warning($"[AutoRefresher] Couldn't read the listing's time left {failedReads} times; using the estimate ({left?.TotalMinutes:F1} min).");
                        }

                        if (left == null)
                        {
                            refreshNextAt = DateTime.UtcNow + ReadRetryAfter;
                            return;
                        }
                    }
                    else
                    {
                        failedReads = 0;
                    }

                    ScheduleFrom(left.Value, verifying);
                }
                catch (Exception ex)
                {
                    pluginLog.Error(ex, "[AutoRefresher] Failed while reading the listing's time left.");
                    refreshNextAt = DateTime.UtcNow + ReadRetryAfter;
                }
                finally
                {
                    refreshStepInFlight = false;
                }
            });
        }

        /// <summary>
        /// Decides what happens next from how long the listing has left.
        ///
        /// Above the threshold: sleep until it reaches it - the wait is exact, because it comes
        /// from the listing's own clock. At or under it: refresh. If this reading was the check
        /// after a refresh, being at or under it means the refresh did not take, and it is tried
        /// again rather than trusted.
        /// </summary>
        private void ScheduleFrom(TimeSpan left, bool verifying)
        {
            var threshold = TimeSpan.FromMinutes(RefreshAtMinutesLeft);
            var now = DateTime.UtcNow;

            if (left > threshold + ThresholdSlack)
            {
                if (verifying)
                {
                    pluginLog.Information($"[AutoRefresher] Refresh confirmed: {left.TotalMinutes:F1} min left.");
                    failedRefreshes = 0;
                    warnedRefreshNotTaking = false;
                }

                refreshPhase = RefreshPhase.Waiting;
                refreshNextAt = now + (left - threshold);
                refreshDueAt = refreshNextAt;
                scheduledAt = now;
                pluginLog.Information($"[AutoRefresher] {left.TotalMinutes:F1} min left; refreshing at {RefreshAtMinutesLeft} min left, in {(left - threshold).TotalMinutes:F1} min.");
                return;
            }

            if (verifying)
            {
                failedRefreshes++;
                pluginLog.Warning($"[AutoRefresher] Refresh didn't take ({left.TotalMinutes:F1} min left, attempt {failedRefreshes}); trying again.");

                if (failedRefreshes >= FailedRefreshesBeforeBackoff && !warnedRefreshNotTaking)
                {
                    warnedRefreshNotTaking = true;
                    chatGui.Print($"[PF Analysis] Auto-refresh has tried {failedRefreshes} times and your listing still has {Math.Max(1, (int)left.TotalMinutes)} min left. Still trying.");
                }
            }

            refreshPhase = RefreshPhase.Due;
            refreshNextAt = verifying && failedRefreshes >= FailedRefreshesBeforeBackoff
                ? now + FailedRefreshBackoff
                : now;
            refreshDueAt = refreshNextAt;
        }

        private void StartScheduledRefresh()
        {
            refreshStepInFlight = true;
            refreshPhase = RefreshPhase.Refreshing;

            _ = Task.Run(async () =>
            {
                try
                {
                    await RunRefreshAsync();
                }
                finally
                {
                    // Whether or not the sequence reported success: the only answer trusted is the
                    // listing's own clock, read after the listing has had time to come back.
                    refreshPhase = RefreshPhase.Verifying;
                    refreshNextAt = DateTime.UtcNow + VerifyAfter;
                    refreshDueAt = refreshNextAt;
                    refreshStepInFlight = false;
                }
            });
        }

        /// <summary>
        /// How long our own listing has left, read from the game, or null when it could not be.
        ///
        /// The game only holds that number while the listing's detail window is open, so this
        /// opens it, reads it and presses Back - the same round trip the listing watcher makes for
        /// somebody else's listing. When the detail window is already open on our own listing the
        /// live value is read from it and the window is left alone.
        /// </summary>
        private async Task<TimeSpan?> ReadOwnListingTimeLeftAsync()
        {
            if (disposed || isEndingRecruitment || isCapturingListing)
                return null;

            // Already on screen: read it and leave the player's window where it is. A detail
            // window showing somebody else's listing is theirs too, and is not ours to replace.
            var already = await framework.RunOnFrameworkThread(() =>
            {
                if (!ListingDetailIsOpen())
                    return (Open: false, Left: (TimeSpan?)null);

                return (Open: true, Left: OwnListingTimeLeftNow());
            });

            if (already.Open)
                return already.Left;

            isCapturingListing = true;
            try
            {
                if (!await OpenOwnListing(restoreComment: false))
                    return null;

                // ~2s for the window: a server round trip, and it never arrives when there is no
                // listing to show.
                bool shown = false;
                for (int i = 0; i < 40 && !disposed; i++)
                {
                    await Task.Delay(50);
                    shown = await framework.RunOnFrameworkThread(() => ListingDetailIsOpen());
                    if (shown)
                        break;
                }

                if (!shown)
                {
                    pluginLog.Debug("[AutoRefresher] Our listing's detail window didn't appear.");
                    return null;
                }

                // Let it populate before reading.
                await Task.Delay(400);

                return await framework.RunOnFrameworkThread(() =>
                {
                    var left = OwnListingTimeLeftNow();
                    CloseListingDetail();
                    return left;
                });
            }
            finally
            {
                isCapturingListing = false;
            }
        }

        /// <summary>The live time-left of our own listing in the detail window, or null when the
        /// window is showing somebody else's or carries no plausible countdown. Framework thread.</summary>
        private unsafe TimeSpan? OwnListingTimeLeftNow()
        {
            var agent = AgentLookingForGroup.Instance();
            if (agent == null)
                return null;

            var listing = agent->LastViewedListing;
            if (listing.LeaderContentId != playerState.ContentId)
                return null;

            if (listing.ListingId != 0)
                knownOwnListingId = listing.ListingId;

            uint seconds = listing.TimeLeft;
            if (seconds == 0 || seconds > ListingLifetime.TotalSeconds)
                return null;

            // Hand it to the status card while we have it - it is the same exact reading the card
            // otherwise has to wait for somebody to open the listing to get.
            CaptureListingTimeLeft();
            return TimeSpan.FromSeconds(seconds);
        }

        /// <summary>
        /// Re-posts the current listing now: the Refresh button and /pfp refresh.
        ///
        /// Goes through the schedule when the refresher is on, so a refresh by hand is checked
        /// thirty seconds later exactly like a scheduled one, and the next wait is taken from the
        /// fresh listing rather than from the old countdown.
        /// </summary>
        public void ExecuteRefreshTask()
        {
            if (isRefreshExecuting || refreshStepInFlight || disposed || IsCoordinationBusy)
                return;

            if (config.AutoRefresherEnabled && IsRecruiting() && !maxDurationReached)
            {
                StartScheduledRefresh();
                return;
            }

            _ = Task.Run(RunRefreshAsync);
        }

        /// <summary>
        /// Re-posts the current listing: opens it, then clicks Edit and Recruit. Runs as a
        /// background task because the two windows take time to open; every game interaction
        /// still happens on the framework thread. True when every click landed - which is not the
        /// same as the listing having been renewed, and the schedule does not treat it as such.
        /// </summary>
        private async Task<bool> RunRefreshAsync()
        {
            if (isRefreshExecuting || disposed) return false;
            isRefreshExecuting = true;

            try
            {
                pluginLog.Information("[AutoRefresher] Starting recruitment auto-refresh...");

                // 1. Restore the comment if the client cleared it, then open the listing.
                //    All game calls run on the framework thread - clicking native UI buttons
                //    off-thread is unreliable.
                if (!await OpenOwnListing()) return false;

                // 2. Wait for the listing detail window, then press Edit (button 109).
                //    While that window is up it's the one moment the game exposes the listing's
                //    real time-left, so grab it for the status box on the way past.
                await framework.RunOnFrameworkThread(CaptureListingTimeLeft);

                if (!await WaitForAddonAndClickButton("LookingForGroupDetail", 109, "Edit"))
                    return false;

                // 3. Wait for the recruitment criteria window, then press Recruit/Apply
                //    (button 113) - this re-posts the listing and resets its timer. The same seats
                //    go back up, so the remembered layout still describes the listing.
                intendedWritePending = intendedSeats != null;
                if (!await WaitForAddonAndClickButton("LookingForGroupCondition", 113, "Recruit"))
                    return false;

                // 4. Confirm the party-composition warning if the game raises it.
                await ConfirmCompositionDialogAsync();

                refreshCount++;
                pluginLog.Information($"[AutoRefresher] Recruitment refresh sequence completed (Count: {refreshCount}).");
                return true;
            }
            catch (Exception ex)
            {
                pluginLog.Error(ex, "[AutoRefresher] Error during auto-refresh task.");
                return false;
            }
            finally
            {
                isRefreshExecuting = false;
            }
        }

        /// <summary>
        /// Restores the listing comment if the client cleared it, then opens the player's own
        /// recruitment detail window via the native OpenPartyFinder function. Runs on the framework
        /// thread. Returns false if the agent or the native function is unavailable. Shared by the
        /// Auto Refresher and the locked-slot auto-adjuster.
        /// </summary>
        private Task<bool> OpenOwnListing(bool restoreComment = true)
        {
            return framework.RunOnFrameworkThread(() =>
            {
                unsafe
                {
                    var agent = AgentLookingForGroup.Instance();
                    if (agent == null)
                    {
                        pluginLog.Warning("[AutoRefresher] AgentLookingForGroup instance is null.");
                        return false;
                    }
                    if (openPartyFinder == null)
                    {
                        pluginLog.Warning("[AutoRefresher] OpenPartyFinder function unavailable (signature not found).");
                        return false;
                    }

                    if (!restoreComment)
                    {
                        openPartyFinder(agent, playerState.ContentId);
                        return true;
                    }

                    // Kept as bytes rather than as a string. The comment buffer is SeString, so a
                    // listing carrying an auto-translate phrase used to lose it the first time the
                    // refresher stashed and restored it - the round trip through a C# string
                    // decoded the payload to replacement characters and wrote those back.
                    byte* commentBuffer = (byte*)&agent->StoredRecruitmentInfo + OffsetComment;
                    byte[] comment = CommentText.RawBytes(commentBuffer, MaxCommentLength + 1);
                    bool cleared = comment.Length == 0 ||
                        string.IsNullOrWhiteSpace(CommentText.Decode(comment));

                    if (previousCommentBytes != null && cleared)
                    {
                        pluginLog.Information(
                            $"[AutoRefresher] Comment was cleared out, reapplying: '{CommentText.Decode(previousCommentBytes)}'");
                        AtkHelpers.SetFixedBytes(commentBuffer, previousCommentBytes, MaxCommentLength + 1);
                    }
                    else if (!cleared)
                    {
                        previousCommentBytes = comment;
                    }

                    openPartyFinder(agent, playerState.ContentId);
                    return true;
                }
            });
        }

        /// <summary>
        /// After a Recruit/Apply click, watches briefly (~0.6s) for the game's party-composition
        /// confirmation dialog and clicks Yes if it appears. No-op when it doesn't. Shared by the
        /// Auto Refresher and the locked-slot adjuster; the apply flow handles it in its own step.
        /// </summary>
        private async Task ConfirmCompositionDialogAsync()
        {
            for (int i = 0; i < 12; i++)
            {
                if (disposed) return;
                bool confirmed = await framework.RunOnFrameworkThread(() => TryConfirmCompositionDialog());
                if (confirmed) return;
                await Task.Delay(50);
            }
        }

        /// <summary>
        /// Polls (off-thread) until the named addon is visible and the given component button is
        /// enabled, then clicks it on the framework thread via the native event. Returns false on
        /// timeout (~5s) or when the plugin is unloaded mid-wait. This mirrors
        /// RecruitmentRefresher's Edit -> Recruit sequence.
        /// </summary>
        private async Task<bool> WaitForAddonAndClickButton(string addonName, uint buttonId, string label)
        {
            for (int i = 0; i < 100; i++)
            {
                if (disposed) return false;

                bool clicked = await framework.RunOnFrameworkThread(() =>
                {
                    unsafe
                    {
                        var addon = (AtkUnitBase*)(nint)gameGui.GetAddonByName(addonName);
                        if (addon == null || !addon->IsVisible) return false;
                        var btn = addon->GetComponentButtonById(buttonId);
                        if (btn == null || !btn->IsEnabled) return false;
                        return AtkHelpers.ClickAddonButton(addon, btn);
                    }
                });
                if (clicked)
                {
                    pluginLog.Information($"[AutoRefresher] Clicked {label} button.");
                    return true;
                }
                await Task.Delay(50);
            }
            pluginLog.Warning($"[AutoRefresher] Timed out waiting for the {label} button ({addonName}).");
            return false;
        }
    }
}
