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
        private double minutesElapsed = 0;
        private string? previousCommentString = null;
        private bool isRefreshExecuting = false;

        /// <summary>Total time the current listing has been kept alive by the refresher, in hours.
        /// Reset whenever recruitment stops. Deliberately not persisted: the cap is about one
        /// unattended session, so logging out or ending the listing starts the budget over.</summary>
        private double hoursRecruiting = 0;

        /// <summary>Set once the runtime cap is hit so we stop refreshing and only say so once.</summary>
        private bool maxDurationReached = false;

        /// <summary>Bounds for the user-set refresh interval. A Party Finder listing expires after
        /// 60 minutes, so anything at or past that would let it lapse before the refresh fires.</summary>
        public const int MinRefreshMinutes = 1;
        public const int MaxRefreshMinutes = 55;

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

        internal void MarkListingSubmitted() => listingSubmittedTick = Environment.TickCount64;

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
            var localPlayer = objectTable.LocalPlayer;
            if (localPlayer != null && localPlayer.OnlineStatus.RowId == RecruitingOnlineStatusId)
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

        /// <summary>How often the Auto Refresher re-posts the listing, in minutes. Free-form, clamped
        /// to <see cref="MinRefreshMinutes"/>..<see cref="MaxRefreshMinutes"/> so a bad stored value
        /// can never stall the timer or let the listing expire.</summary>
        public double RefreshIntervalMinutes =>
            Math.Clamp(config.AutoRefresherIntervalMinutes, MinRefreshMinutes, MaxRefreshMinutes);

        /// <summary>True while the refresh countdown is actively ticking: the feature is enabled,
        /// a Party Finder is up (preset-made or manual), and the runtime cap hasn't been hit.</summary>
        public bool IsRefreshTimerRunning => config.AutoRefresherEnabled && IsRecruiting() && !maxDurationReached;

        /// <summary>True once the "stop after N hours" cap has ended refreshing for this listing.</summary>
        public bool HasReachedMaxDuration => maxDurationReached;

        /// <summary>Seconds remaining until the next auto-refresh.</summary>
        public double SecondsUntilNextRefresh =>
            Math.Max(0.0, (RefreshIntervalMinutes - minutesElapsed) * 60.0);

        /// <summary>Hours the current listing has been kept alive by the refresher.</summary>
        public double HoursRecruiting => hoursRecruiting;

        /// <summary>Called every framework update; counts up while recruiting and triggers a
        /// refresh when the interval elapses, until the optional runtime cap is reached.</summary>
        public void UpdateAutoRefresher(double deltaMins)
        {
            if (!config.AutoRefresherEnabled || !IsRecruiting())
            {
                // Recruitment ended (or the feature was turned off): the next listing starts with a
                // fresh interval and a fresh duration budget.
                previousCommentString = null;
                refreshCount = 0;
                minutesElapsed = 0;
                hoursRecruiting = 0;
                maxDurationReached = false;
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
                pluginLog.Information($"[AutoRefresher] Reached the {maxHours}h limit after {refreshCount} refresh(es); no longer re-posting.");
                chatGui.Print($"[PF Presets] Auto-refresh stopped after {maxHours} hour(s). Your listing will expire normally.");
                return;
            }

            if (minutesElapsed >= RefreshIntervalMinutes)
            {
                ExecuteRefreshTask();
                minutesElapsed = 0;
            }
            else
            {
                minutesElapsed += deltaMins;
            }
        }

        /// <summary>
        /// Re-posts the current listing: opens it, then clicks Edit and Recruit. Runs as a
        /// background task because the two windows take time to open; every game interaction
        /// still happens on the framework thread.
        /// </summary>
        public void ExecuteRefreshTask()
        {
            if (isRefreshExecuting || disposed) return;
            isRefreshExecuting = true;

            Task.Run(async () =>
            {
                try
                {
                    pluginLog.Information("[AutoRefresher] Starting recruitment auto-refresh...");

                    // 1. Restore the comment if the client cleared it, then open the listing.
                    //    All game calls run on the framework thread - clicking native UI buttons
                    //    off-thread is unreliable.
                    if (!await OpenOwnListing()) return;

                    // 2. Wait for the listing detail window, then press Edit (button 109).
                    //    While that window is up it's the one moment the game exposes the listing's
                    //    real time-left, so grab it for the status box on the way past.
                    await framework.RunOnFrameworkThread(CaptureListingTimeLeft);

                    if (!await WaitForAddonAndClickButton("LookingForGroupDetail", 109, "Edit"))
                        return;

                    // 3. Wait for the recruitment criteria window, then press Recruit/Apply
                    //    (button 113) - this re-posts the listing and resets its timer.
                    if (!await WaitForAddonAndClickButton("LookingForGroupCondition", 113, "Recruit"))
                        return;

                    // 4. Confirm the party-composition warning if the game raises it.
                    await ConfirmCompositionDialogAsync();

                    refreshCount++;
                    pluginLog.Information($"[AutoRefresher] Recruitment auto-refreshed successfully (Count: {refreshCount}).");
                }
                catch (Exception ex)
                {
                    pluginLog.Error(ex, "[AutoRefresher] Error during auto-refresh task.");
                }
                finally
                {
                    isRefreshExecuting = false;
                }
            });
        }

        /// <summary>
        /// Restores the listing comment if the client cleared it, then opens the player's own
        /// recruitment detail window via the native OpenPartyFinder function. Runs on the framework
        /// thread. Returns false if the agent or the native function is unavailable. Shared by the
        /// Auto Refresher and the locked-slot auto-adjuster.
        /// </summary>
        private Task<bool> OpenOwnListing()
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

                    string comment = agent->StoredRecruitmentInfo.CommentString;
                    if (previousCommentString != null && comment != previousCommentString && string.IsNullOrWhiteSpace(comment))
                    {
                        pluginLog.Information($"[AutoRefresher] Comment was cleared out, reapplying: '{previousCommentString}'");
                        agent->StoredRecruitmentInfo.CommentString = previousCommentString;
                    }
                    else if (!string.IsNullOrWhiteSpace(comment))
                    {
                        previousCommentString = comment;
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
