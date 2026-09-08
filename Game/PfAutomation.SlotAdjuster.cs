using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;
using FFXIVClientStructs.FFXIV.Client.Game.Group;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace PfPresets
{
    /// <summary>
    /// The locked-slot auto-adjuster: while you recruit as party leader, watch for a member
    /// leaving; five seconds later, read the listing that is actually up and broaden any seat that
    /// has been left locked to a single job. Reuses the Auto Refresher's open/edit/recruit plumbing
    /// (<see cref="OpenOwnListing"/>, <see cref="WaitForAddonAndClickButton"/>).
    ///
    /// THE WHOLE CYCLE IS "SCOUT, THEN DECIDE", and it is written that way because the version
    /// before it decided first and looked afterwards. It opened the listing on every leave, and
    /// once it was in there it filled every seat it could not account for from a fresh 2T/2H/4D
    /// composition - which is a statement about a party that does not exist. A listing with a
    /// healer omitted, one healer in it, and that healer leaving came back out of this as five DPS
    /// seats: the omission had been overwritten, the roles reshuffled, and the listing re-posted
    /// for a party nobody had asked for.
    ///
    /// So there are two hard rules here now, and every part of this file follows from them:
    ///
    ///   1. A SEAT IS ONLY EVER WIDENED, NEVER INVENTED. The only edit this makes is taking a seat
    ///      locked to exactly one job and opening it up to that job's role. A mask of zero - which
    ///      is what an omitted seat is, and the only thing an omitted seat is - is never written
    ///      to, so an omission cannot come back however many people leave.
    ///   2. NOTHING TO WIDEN MEANS NOTHING HAPPENS. The listing is read where it stands, before
    ///      any window is opened. If every seat already advertises a role, the listing is already
    ///      right and re-posting it would interrupt recruitment to change nothing.
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
        /// The first seat this may touch, as an index into the eight slot flags.
        ///
        /// Slot 1 is the leader's own, and the game keeps it locked to whatever job the leader is
        /// standing there on. Widening it to a role says the party is looking for another one of
        /// you - it is not, you are already in it - and the old code did exactly that on every
        /// single run, because a seat locked to one job is precisely what slot 1 always is.
        /// </summary>
        private const int FirstAdjustableSlot = 1;

        /// <summary>
        /// Called every framework update. Tracks the party size while you recruit as leader and,
        /// when it drops (a member left), schedules a one-shot scout 5 seconds later.
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
                pluginLog.Information($"[SlotAdjuster] Party member left ({lastPartyMemberCount} -> {count}); scouting the listing in 5s.");
            }
            lastPartyMemberCount = count;

            if (slotAdjustScheduled && Environment.TickCount64 >= slotAdjustDueTime)
            {
                // Only clear the flag once the scout has actually run; if a refresh is mid-flight
                // ScoutAndAdjust returns false and we retry on a later frame.
                if (ScoutAndAdjust())
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
        /// One seat this run intends to change: where it is, and what it becomes.
        /// </summary>
        private readonly record struct SlotEdit(int Index, ulong Mask, string Reason);

        // ══════════════════════════════════════════════════════════
        //  STEP 1 - SCOUT
        // ══════════════════════════════════════════════════════════

        /// <summary>
        /// Reads the listing as it stands and, if anything wants widening, starts the edit.
        ///
        /// Called from the framework update, so the native read below is already on the framework
        /// thread and needs no marshalling of its own.
        /// </summary>
        /// <returns>True when the scout ran - whether or not it found anything to do. False only
        /// when something else is already driving the listing, so the caller retries later.</returns>
        private bool ScoutAndAdjust()
        {
            if (isRefreshExecuting || disposed)
                return false;

            var plan = ScoutListingSlots();

            if (plan.Count == 0)
            {
                // THE LISTING IS LEFT ALONE, and this is the branch that did not exist before.
                // Every seat either advertises a role already or is closed, which means the seat
                // the leaver vacated is being recruited for correctly right now. Opening the
                // listing to re-post it identically would only take recruitment down for a few
                // seconds and push it back to the bottom of everybody's list.
                pluginLog.Information("[SlotAdjuster] Nothing locked to a single job; leaving the listing up as it is.");
                return true;
            }

            return ExecuteSlotAdjustTask(plan);
        }

        /// <summary>
        /// Every seat from 2 to 8 that is locked to exactly one job, and what it should become.
        ///
        /// The three cases a seat can be in, and why only one of them is an edit:
        ///
        ///   * MASK OF ZERO - the seat is closed. An omitted seat looks like this, and so does a
        ///     seat past the end of a light-party listing. Both are seats the listing is not
        ///     recruiting for, and neither is distinguishable from the other from here, so neither
        ///     is written to. That is the whole of the omit fix: there is no code path in this file
        ///     that can put a job mask into a seat that currently holds zero.
        ///   * MORE THAN ONE BIT - the seat already asks for a role, a sub-category or a hand-picked
        ///     set of jobs. That is either what the preset said or what a previous run of this made
        ///     it, and in both cases it is already right.
        ///   * EXACTLY ONE BIT - the seat is pinned to a single job. This is what the game leaves
        ///     behind when the person standing in it goes, and it is the only thing here worth
        ///     fixing: a listing asking for one specific White Mage fills far more slowly than one
        ///     asking for a healer.
        /// </summary>
        private unsafe List<SlotEdit> ScoutListingSlots()
        {
            var plan = new List<SlotEdit>();

            var agent = AgentLookingForGroup.Instance();
            if (agent == null)
                return plan;

            ulong* pSlotFlags = (ulong*)((byte*)&agent->StoredRecruitmentInfo + OffsetSlotFlags);

            for (int i = FirstAdjustableSlot; i < 8; i++)
            {
                ulong mask = pSlotFlags[i];

                if (mask == 0)
                    continue;                            // closed or omitted - never touched
                if ((mask & (mask - 1)) != 0)
                    continue;                            // already broader than one job

                ulong widened = WidenSingleJobMask(mask, out string reason);
                if (widened == 0 || widened == mask)
                    continue;

                plan.Add(new SlotEdit(i, widened, reason));
            }

            if (plan.Count > 0)
            {
                var seats = new List<string>(plan.Count);
                foreach (var edit in plan)
                    seats.Add($"slot {edit.Index + 1} ({edit.Reason})");
                pluginLog.Information($"[SlotAdjuster] Scouted {plan.Count} seat(s) to widen: {string.Join(", ", seats)}.");
            }

            return plan;
        }

        /// <summary>
        /// What a seat locked to one job opens up to: that job's sub-category - White Mage to regen
        /// healers, Red Mage to casters, Viper to melee.
        ///
        /// The sub-category rather than the plain role, because it is strictly more specific and
        /// keeps the pure/barrier healer split the composition cares about.
        ///
        /// A bit that maps to no job we know - a crafter somebody was standing on, or a job added
        /// by a patch this build predates - opens the seat to every job instead. It is still one
        /// seat being changed and no other, which is the rule that matters; the alternative is a
        /// seat pinned to a job nobody is going to apply on, stuck that way for the life of the
        /// listing.
        /// </summary>
        private static ulong WidenSingleJobMask(ulong mask, out string reason)
        {
            int gameBit = BitOperations.TrailingZeroCount(mask);
            var job = JobData.FindById(JobMasks.GetJobIdFromGameBit(gameBit));

            if (job == null)
            {
                reason = $"unknown job (bit {gameBit}) -> any job";
                return JobMasks.ToGameMask(JobMasks.AllJobsMask);
            }

            reason = $"{job.Name} -> {DisplayNames.GetCategoryName(job.Category)}";
            return JobMasks.ToGameMask(JobMasks.GetCategoryMask(job.Category));
        }

        // ══════════════════════════════════════════════════════════
        //  STEP 2 - EDIT AND RE-POST
        // ══════════════════════════════════════════════════════════

        /// <summary>
        /// Opens the listing, presses Edit, applies the scouted widenings and re-posts. Runs as a
        /// background task (the windows take time to open); every game interaction still happens on
        /// the framework thread. Returns false without starting if a refresh/adjust is already
        /// running or the plugin is unloading.
        /// </summary>
        private bool ExecuteSlotAdjustTask(List<SlotEdit> plan)
        {
            if (isRefreshExecuting || disposed) return false;
            isRefreshExecuting = true;

            Task.Run(async () =>
            {
                try
                {
                    pluginLog.Information($"[SlotAdjuster] Widening {plan.Count} locked seat(s)...");

                    // 1. Open the player's own recruitment detail window.
                    if (!await OpenOwnListing()) return;

                    // 2. Wait for the detail window, then press Edit (button 109).
                    if (!await WaitForAddonAndClickButton("LookingForGroupDetail", 109, "Edit"))
                        return;

                    // 3. Wait for the recruitment criteria window, apply the widenings in memory,
                    //    then press Recruit/Apply (button 113) on the same frame to re-post.
                    if (!await WaitForConditionAndAdjustThenRecruit())
                        return;

                    // 4. Confirm the party-composition warning if the game raises it.
                    await ConfirmCompositionDialogAsync();

                    pluginLog.Information("[SlotAdjuster] Listing re-posted with the widened seat(s).");
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
        /// enabled, then - on that same framework frame - applies the widenings and clicks Recruit.
        /// Writing the slot flags on the click frame (mirroring how the apply flow re-asserts the
        /// duty id at submit) ensures the game reads the widened masks.
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

                        // RE-SCOUTED HERE, NOT REPLAYED. Several seconds pass between the scout
                        // and this frame - the criteria window has to open, and somebody can join
                        // or leave in the meantime - so the plan is rebuilt against the listing
                        // as it is right now rather than against the one that triggered the run.
                        int changed = ApplyScoutedEdits();
                        pluginLog.Information($"[SlotAdjuster] Applied {changed} slot change(s); re-posting.");
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
        /// Writes the widened masks into StoredRecruitmentInfo and returns how many seats moved.
        ///
        /// Nothing but the seats the scout named, and the scout only ever names seats that already
        /// hold a single job. Every other seat in the listing - closed, omitted, role-filtered, or
        /// the leader's own - is left exactly as the game and the preset left it.
        /// </summary>
        private unsafe int ApplyScoutedEdits()
        {
            var agent = AgentLookingForGroup.Instance();
            if (agent == null) return 0;

            ulong* pSlotFlags = (ulong*)((byte*)&agent->StoredRecruitmentInfo + OffsetSlotFlags);

            int changed = 0;
            foreach (var edit in ScoutListingSlots())
            {
                pSlotFlags[edit.Index] = edit.Mask;
                changed++;
                pluginLog.Information($"[SlotAdjuster] Slot {edit.Index + 1}: {edit.Reason}.");
            }

            return changed;
        }
    }
}
