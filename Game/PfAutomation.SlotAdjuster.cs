using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FFXIVClientStructs.FFXIV.Client.Game.Group;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace PfPresets
{
    /// <summary>
    /// The locked-slot auto-adjuster: while you recruit as party leader, watch for a member
    /// leaving; five seconds later, open and edit the listing, broaden any Party Finder slot that
    /// is locked to a single job to that job's role, then re-post. Reuses the Auto Refresher's
    /// open/edit/recruit plumbing (<see cref="OpenOwnListing"/>, <see cref="WaitForAddonAndClickButton"/>).
    /// </summary>
    public partial class PfAutomation
    {
        // Party-membership watch state. -1 means "not initialised / not currently recruiting".
        private int lastPartyMemberCount = -1;
        private bool slotAdjustScheduled = false;
        private long slotAdjustDueTime = 0;

        /// <summary>Delay between a member leaving and the adjust firing, so the game and the
        /// listing have settled first.</summary>
        private const long SlotAdjustDelayMs = 5000;

        /// <summary>
        /// Called every framework update. Tracks the party size while you recruit as leader and,
        /// when it drops (a member left), schedules a one-shot slot adjustment 5 seconds later.
        /// </summary>
        public void UpdateLockedSlotAdjuster()
        {
            if (disposed || !config.AutoAdjustLockedJobsEnabled || !IsRecruiting() || !IsPartyLeader())
            {
                // Not eligible: forget the tracked size and drop any pending adjust so we don't
                // fire it against a stale party once conditions change.
                lastPartyMemberCount = -1;
                slotAdjustScheduled = false;
                return;
            }

            int count = GetPartyMemberCount();
            if (lastPartyMemberCount >= 0 && count < lastPartyMemberCount)
            {
                // A member left. (Re)arm the timer; repeated leaves debounce to 5s after the last.
                slotAdjustScheduled = true;
                slotAdjustDueTime = Environment.TickCount64 + SlotAdjustDelayMs;
                pluginLog.Information($"[SlotAdjuster] Party member left ({lastPartyMemberCount} -> {count}); adjusting locked slots in 5s.");
            }
            lastPartyMemberCount = count;

            if (slotAdjustScheduled && Environment.TickCount64 >= slotAdjustDueTime)
            {
                // Only clear the flag once the task actually starts; if a refresh is mid-flight
                // ExecuteSlotAdjustTask returns false and we retry on a later frame.
                if (ExecuteSlotAdjustTask())
                    slotAdjustScheduled = false;
            }
        }

        /// <summary>Total members in the current party (local or cross-world), including yourself.</summary>
        private unsafe int GetPartyMemberCount()
        {
            var crossRealmProxy = InfoProxyCrossRealm.Instance();
            if (crossRealmProxy != null && crossRealmProxy->IsInCrossRealmParty)
            {
                int total = 0;
                for (int i = 0; i < crossRealmProxy->GroupCount; i++)
                    total += crossRealmProxy->CrossRealmGroups[i].GroupMemberCount;
                return total;
            }

            var groupManager = GroupManager.Instance();
            return groupManager != null ? (int)groupManager->MainGroup.MemberCount : 0;
        }

        /// <summary>
        /// Opens the listing, presses Edit, broadens single-job slots, and re-posts. Runs as a
        /// background task (the windows take time to open); every game interaction still happens on
        /// the framework thread. Returns false without starting if a refresh/adjust is already
        /// running or the plugin is unloading.
        /// </summary>
        public bool ExecuteSlotAdjustTask()
        {
            if (isRefreshExecuting || disposed) return false;
            isRefreshExecuting = true;

            Task.Run(async () =>
            {
                try
                {
                    pluginLog.Information("[SlotAdjuster] Starting locked-slot adjustment...");

                    // 1. Open the player's own recruitment detail window.
                    if (!await OpenOwnListing()) return;

                    // 2. Wait for the detail window, then press Edit (button 109).
                    if (!await WaitForAddonAndClickButton("LookingForGroupDetail", 109, "Edit"))
                        return;

                    // 3. Wait for the recruitment criteria window, adjust the slots in memory, then
                    //    press Recruit/Apply (button 113) on the same frame to re-post.
                    if (!await WaitForConditionAndAdjustThenRecruit())
                        return;

                    // 4. Confirm the party-composition warning if the game raises it.
                    await ConfirmCompositionDialogAsync();

                    pluginLog.Information("[SlotAdjuster] Locked-slot adjustment re-posted successfully.");
                }
                catch (Exception ex)
                {
                    pluginLog.Error(ex, "[SlotAdjuster] Error during locked-slot adjustment.");
                }
                finally
                {
                    isRefreshExecuting = false;
                }
            });
            return true;
        }

        /// <summary>
        /// Polls (off-thread) until the recruitment criteria window is up and its Recruit button is
        /// enabled, then — on that same framework frame — broadens any single-job slots in memory
        /// and clicks Recruit. Writing the slot flags on the click frame (mirroring how the apply
        /// flow re-asserts the duty id at submit) ensures the game reads the widened masks.
        /// </summary>
        private async Task<bool> WaitForConditionAndAdjustThenRecruit()
        {
            for (int i = 0; i < 100; i++)
            {
                if (disposed) return false;

                bool clicked = await framework.RunOnFrameworkThread(() =>
                {
                    unsafe
                    {
                        var addon = (AtkUnitBase*)(nint)gameGui.GetAddonByName("LookingForGroupCondition");
                        if (addon == null || !addon->IsVisible) return false;
                        var btn = addon->GetComponentButtonById(113);
                        if (btn == null || !btn->IsEnabled) return false;

                        int changed = AdjustListingSlots();
                        pluginLog.Information($"[SlotAdjuster] Adjusted {changed} slot(s); re-posting.");
                        return AtkHelpers.ClickAddonButton(addon, btn);
                    }
                });
                if (clicked)
                {
                    pluginLog.Information("[SlotAdjuster] Clicked Recruit button.");
                    return true;
                }
                await Task.Delay(50);
            }
            pluginLog.Warning("[SlotAdjuster] Timed out waiting for the Recruit button (LookingForGroupCondition).");
            return false;
        }

        /// <summary>
        /// How many of the eight seats this listing is actually recruiting for.
        ///
        /// CLOSED SEATS ARE AT THE END, which is both what the game settles a listing into and what
        /// <see cref="WriteSlotFlagsToMemory"/> writes deliberately - so the count is where the
        /// zeros start. Two ways of finding it, because they fail in different places:
        ///
        ///   * <see cref="activeSlotCount"/>, recorded when the preset was applied, is the one that
        ///     knows the difference between a seat that was omitted and a seat somebody just left -
        ///     both of which are a mask of zero by the time anybody looks.
        ///   * counting back from the end covers everything else: a listing posted by hand, or one
        ///     applied before the plugin was reloaded.
        ///
        /// The recorded count wins where there is one. The fallback is deliberately the cautious
        /// reading of an ambiguous listing - a trailing zero is left closed rather than recruited
        /// for, because filling a seat the user closed is the louder mistake of the two.
        /// </summary>
        private unsafe int ActiveSlotCount(ulong* pSlotFlags)
        {
            if (activeSlotCount > 0)
                return Math.Clamp(activeSlotCount, 1, 8);

            for (int i = 7; i >= 0; i--)
            {
                if (pSlotFlags[i] != 0)
                    return i + 1;
            }

            return 0;
        }

        /// <summary>
        /// Adjusts the current listing's per-slot job masks in StoredRecruitmentInfo:
        ///  - a slot locked to exactly one battle job widens to that job's sub-category
        ///    (White Mage -> regen healers, Red Mage -> casters, Viper -> melee);
        ///  - a slot that is "All" (empty) or locked to a non-battle job (a crafter/gatherer left
        ///    and the game reset it) falls back to the same auto-fill composition the Auto-Adjust
        ///    apply path uses, so the freed seat becomes whatever role the party actually needs.
        /// Multi-job role/preset filters are left untouched. Returns the number of slots changed.
        ///
        /// ONLY EVER SEATS THE LISTING IS RECRUITING FOR. A closed seat - one the preset omitted -
        /// is a mask of zero, which is the same thing an emptied seat looks like, and this used to
        /// read every one of them as "All" and auto-fill it. The effect was that omitting a slot
        /// worked exactly until the first person left, at which point every omitted seat came back
        /// and the listing started recruiting for roles the user had struck off it. Seats past the
        /// count are not examined at all, so there is nothing there to resurrect.
        /// </summary>
        private unsafe int AdjustListingSlots()
        {
            var agent = AgentLookingForGroup.Instance();
            if (agent == null) return 0;

            ulong* pSlotFlags = (ulong*)((byte*)&agent->StoredRecruitmentInfo + OffsetSlotFlags);
            int changed = 0;
            List<(RoleType Role, uint? JobId, string Tooltip, JobCategory? Category)>? autoSlots = null;

            int active = ActiveSlotCount(pSlotFlags);

            for (int i = 0; i < active; i++)
            {
                ulong mask = pSlotFlags[i];
                bool singleJob = mask != 0 && (mask & (mask - 1)) == 0; // exactly one bit set
                bool empty = mask == 0;                                 // "All" / any job

                // Leave the user's multi-job role/preset filters exactly as they set them.
                if (!singleJob && !empty) continue;

                ulong newMask;
                string reason;

                if (singleJob)
                {
                    int gameBit = System.Numerics.BitOperations.TrailingZeroCount(mask);
                    var job = JobData.FindById(JobMasks.GetJobIdFromGameBit(gameBit));
                    if (job != null)
                    {
                        newMask = JobMasks.ToGameMask(JobMasks.GetCategoryMask(job.Category));
                        reason = $"{job.Name} -> {DisplayNames.GetCategoryName(job.Category)}";
                    }
                    else
                    {
                        // Non-battle job locked here: can't map it to a role, so auto-fill instead.
                        autoSlots ??= GetAutoAdjustedSlots();
                        newMask = i < autoSlots.Count ? GetAutoSlotGameMask(autoSlots[i]) : 0;
                        reason = $"non-battle job (bit {gameBit}) -> auto-fill";
                    }
                }
                else // empty / "All" slot - e.g. a non-battle member left and the game reset it.
                {
                    autoSlots ??= GetAutoAdjustedSlots();
                    newMask = i < autoSlots.Count ? GetAutoSlotGameMask(autoSlots[i]) : 0;
                    reason = "All -> auto-fill";
                }

                if (newMask != 0 && newMask != mask)
                {
                    pSlotFlags[i] = newMask;
                    changed++;
                    pluginLog.Information($"[SlotAdjuster] Slot {i + 1}: {reason}.");
                }
            }
            return changed;
        }
    }
}
