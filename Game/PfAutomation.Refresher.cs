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

        /// <summary>True while the player has a Party Finder listing up.</summary>
        public bool IsRecruiting()
        {
            return this.condition[ConditionFlag.UsingPartyFinder];
        }

        /// <summary>How often the Auto Refresher re-posts the listing, in minutes. Driven by the
        /// user's choice (15 or 30); falls back to 30 if the stored value is something else.</summary>
        public double RefreshIntervalMinutes =>
            config.AutoRefresherIntervalMinutes == 15 ? 15.0 : 30.0;

        /// <summary>True while the refresh countdown is actively ticking: the feature is enabled
        /// and a Party Finder is currently up (whether it was set up via a preset or by hand).</summary>
        public bool IsRefreshTimerRunning => config.AutoRefresherEnabled && IsRecruiting();

        /// <summary>Seconds remaining until the next auto-refresh.</summary>
        public double SecondsUntilNextRefresh =>
            Math.Max(0.0, (RefreshIntervalMinutes - minutesElapsed) * 60.0);

        /// <summary>Called every framework update; counts up while recruiting and triggers a
        /// refresh when the interval elapses.</summary>
        public void UpdateAutoRefresher(double deltaMins)
        {
            if (!config.AutoRefresherEnabled || !IsRecruiting())
            {
                previousCommentString = null;
                refreshCount = 0;
                minutesElapsed = 0;
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
                    bool opened = await framework.RunOnFrameworkThread(() =>
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
                    if (!opened) return;

                    // 2. Wait for the listing detail window, then press Edit (button 109).
                    if (!await WaitForAddonAndClickButton("LookingForGroupDetail", 109, "Edit"))
                        return;

                    // 3. Wait for the recruitment criteria window, then press Recruit/Apply
                    //    (button 113) - this re-posts the listing and resets its timer.
                    if (!await WaitForAddonAndClickButton("LookingForGroupCondition", 113, "Recruit"))
                        return;

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
