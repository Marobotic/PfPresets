using System;
using System.Numerics;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace PfPresets
{
    /// <summary>
    /// The game half of coordinated joining: joining a listing by id, typing its private password,
    /// travelling, and swapping the host's listing between its public and private forms.
    ///
    /// EVERY GAME READ AND CLICK HAPPENS ON THE FRAMEWORK THREAD. These flows are long - a data
    /// centre travel takes minutes - so they run as tasks, but nothing in them touches game memory
    /// except inside <c>framework.RunOnFrameworkThread</c>. The first pass read the party list and
    /// the recruitment agent straight from a thread-pool thread, which is a crash waiting for a
    /// zone change to line up with it.
    ///
    /// EVERYTHING HERE FAILS CLOSED. A window that does not appear, or a button that is not where
    /// it was, ends the attempt with a reason; nothing clicks a node it could not identify.
    /// </summary>
    public partial class PfAutomation
    {
        private volatile bool isJoiningListing;
        private volatile bool isSwitchingListing;

        /// <summary>True while a coordinated join or listing swap is driving the game's windows.
        /// The Auto Refresher and the slot adjuster stand down until it is over.</summary>
        public bool IsCoordinationBusy => isJoiningListing || isSwitchingListing;

        /// <summary>Why a join attempt ended.</summary>
        public enum JoinOutcome
        {
            Joined,
            Busy,
            NotNow,
            InParty,
            ListingMissing,
            JoinUnavailable,
            PasswordPrompt,
            NotAccepted,
            Error,

            /// <summary>Read only: the listing was read fresh and nothing was pressed.</summary>
            Checked,
        }

        // ── Reads (framework thread only) ───────────────────────────

        /// <summary>
        /// This character's listing id, as last seen in its own detail window.
        ///
        /// NOT agent->OwnListingId ALONE. That field is often still zero after a post, and is only
        /// trustworthy for a moment around our own submit (see IsRecruiting). The listing's own
        /// detail window is what reliably carries the id - it is where the Auto Refresher reads the
        /// clock from - so the id is remembered whenever that window shows our listing, and
        /// forgotten when the listing ends.
        /// </summary>
        private ulong knownOwnListingId;
        private DateTime nextOwnListingProbe = DateTime.MinValue;

        public unsafe ulong ResolveOwnListingId()
        {
            var agent = AgentLookingForGroup.Instance();
            if (agent == null)
                return 0;

            var viewed = agent->LastViewedListing;
            if (viewed.ListingId != 0 && viewed.LeaderContentId == playerState.ContentId)
            {
                knownOwnListingId = viewed.ListingId;
                CaptureOwnListingPreset(viewed);
            }

            if (knownOwnListingId == 0 && agent->OwnListingId != 0
                && Environment.TickCount64 - listingSubmittedTick <= SubmitGraceMs)
                knownOwnListingId = agent->OwnListingId;

            return knownOwnListingId;
        }

        /// <summary>The inputs to "are we recruiting", for the log when the answer is surprising.</summary>
        public unsafe string DescribeRecruitingState()
        {
            var agent = AgentLookingForGroup.Instance();
            uint status = objectTable.LocalPlayer?.OnlineStatus.RowId ?? 0;
            return $"online status {status}, recruiting set {RecruitingStatusSet()}, leader {IsPartyLeader()}, agent listing {(agent == null ? 0 : agent->OwnListingId)}, "
                + $"known listing {knownOwnListingId}";
        }

        public string? GetOwnListingId()
        {
            ulong id = ResolveOwnListingId();
            return id == 0 ? null : id.ToString();
        }

        /// <summary>
        /// When the listing is up but its id has not been seen, opens its detail window for a moment
        /// and presses Back - the Auto Refresher's own clock reading, which also yields the id. Not
        /// while the player is using the Party Finder or is busy, and at most every 20 seconds.
        /// Framework thread; the round trip itself runs as a task.
        /// </summary>
        public void ProbeOwnListingIfUnknown(bool force = false)
        {
            var now = DateTime.UtcNow;
            if (now < nextOwnListingProbe || disposed)
                return;
            if (knownOwnListingId != 0 && !(force && (OwnListingAsPreset == null || OwnListingSlotMasks == null)))
                return;
            nextOwnListingProbe = now + TimeSpan.FromSeconds(20);

            if (!IsRecruiting() || !IsPartyLeader() || IsCoordinationBusy || isRefreshExecuting
                || isEndingRecruitment || isCapturingListing || !CanTouchTheWindow())
                return;

            pluginLog.Information("[PF Board] Reading our own listing once to learn its id.");
            _ = Task.Run(ReadOwnListingTimeLeftAsync);
        }

        /// <summary>
        /// The duty, seat count and closed seats of the listing this client is recruiting with.
        /// Closed seats are the ones written as a zero job mask - the preset's omitted slots, which
        /// the apply flow moves to the end of the party.
        /// </summary>
        public unsafe (uint DutyId, int Total, List<int> OmittedSlots) GetCurrentListingShape()
        {
            var agent = AgentLookingForGroup.Instance();
            if (agent == null)
                return (0, 8, new List<int>());

            var info = &agent->StoredRecruitmentInfo;
            int total = info->NumberOfSlotsInMainParty;
            if (total <= 0 || total > 8)
                total = 8;

            var masks = SlotMasksOf(info);
            var omitted = new List<int>();
            for (int i = 0; i < total && i < masks.Length; i++)
            {
                if (masks[i] == 0)
                    omitted.Add(i);
            }

            return (info->SelectedDutyId, total, omitted);
        }

        /// <summary>Where the character is, read on the framework thread. Empty names while
        /// logged out or mid-travel.</summary>
        public Task<(bool Ready, string World, string DataCentre)> ReadLocationAsync()
            => framework.RunOnFrameworkThread(() =>
            {
                bool transitioning = condition[ConditionFlag.BetweenAreas]
                    || condition[ConditionFlag.BetweenAreas51]
                    || condition[ConditionFlag.LoggingOut];
                if (!clientState.IsLoggedIn || !playerState.IsLoaded || transitioning)
                    return (false, string.Empty, string.Empty);

                var world = playerState.CurrentWorld.ValueNullable;
                return (world != null,
                    world?.Name.ToString() ?? string.Empty,
                    world?.DataCenter.ValueNullable?.Name.ToString() ?? string.Empty);
            });

        /// <summary>How many people besides this character are in the party, read on the
        /// framework thread.</summary>
        public Task<int> CountOtherPartyMembersAsync()
            => framework.RunOnFrameworkThread(() => GetOtherPartyMemberDetails().Count);

        // ── The recruiter's own listing ─────────────────────────────

        /// <summary>What this character's own listing says, read from the recruitment the game is
        /// holding for it. Framework thread.</summary>
        public sealed record OwnListing(
            string Id,
            uint DutyId,
            int DutyType,
            int Category,
            int Total,
            ulong[] GameMasks,
            string Comment,
            bool Beginners,
            int Objective,
            int LootRule,
            bool Private,
            int Conditions,
            int DutySettings,
            int MinIlvl);

        /// <summary>Bumped every time the listing is (re)posted: the plugin's own apply and refresh,
        /// and an Edit then Recruit done by hand. What the board report keys its re-sends on.</summary>
        public int ListingRevision { get; private set; }

        private bool conditionWasOpen;
        private DateTime? conditionClosedAt;

        /// <summary>
        /// Watches for the listing being posted or re-posted, whoever did it. Every post goes through
        /// the Recruitment Criteria window, so a close of that window with the listing still up
        /// afterwards is a post - which also gives the listing a fresh hour. Framework thread, a
        /// couple of times a second.
        /// </summary>
        public void TrackOwnListing()
        {
            bool recruiting = IsRecruiting();
            TrackRecruitmentWindow(recruiting);
            TrackJoinedJobs();

            bool open;
            unsafe
            {
                open = GetVisibleConditionAddon() != null;
            }

            var now = DateTime.UtcNow;
            if (conditionWasOpen && !open)
                conditionClosedAt = now;
            conditionWasOpen = open;

            // The online status takes a moment to catch up with the post.
            if (conditionClosedAt is { } closed && now - closed > TimeSpan.FromSeconds(2))
            {
                conditionClosedAt = null;
                if (recruiting)
                {
                    capturedTimeLeft = ListingLifetime;
                    capturedAt = now;
                    ListingRevision++;

                    // A post this plugin did not write the seats for - an Edit by hand - may have
                    // changed them, so the remembered layout no longer describes the listing. The
                    // Auto Refresher and the slot adjuster re-post the same seats, so theirs keep it.
                    if (!intendedWritePending && !isRefreshExecuting)
                        intendedSeats = null;
                    intendedWritePending = false;
                }
            }
        }

        /// <summary>The recruitment the game is holding, without needing the listing id: the seat
        /// masks as written (omitted seats are zero), and the password (10000 = none).</summary>
        public sealed record RecruitmentState(uint DutyId, int Total, ulong[] Masks, ushort Password);

        public unsafe RecruitmentState? ReadRecruitmentState()
        {
            var agent = AgentLookingForGroup.Instance();
            if (agent == null)
                return null;

            var info = &agent->StoredRecruitmentInfo;
            int total = info->NumberOfSlotsInMainParty;
            if (total <= 0 || total > 8)
                return null;

            return new RecruitmentState(info->SelectedDutyId, total, SlotMasksOf(info), info->Password);
        }

        /// <summary>
        /// This character's own listing's seats, for drawing every one of them: how many the party
        /// has, the accepted-jobs mask of each seat nobody is sitting in, and how many are closed
        /// (omitted, or held for an applicant). Null when not recruiting as leader.
        /// </summary>
        public unsafe (int Seats, List<ulong> Vacant, int Closed)? OwnListingSeats()
        {
            if (!IsRecruiting() || !IsPartyLeader())
                return null;

            var agent = AgentLookingForGroup.Instance();
            if (agent == null)
                return null;

            var info = &agent->StoredRecruitmentInfo;
            int total = info->NumberOfSlotsInMainParty;
            if (total <= 0 || total > 8)
                return null;

            // Leading it, the joiners are everybody else in the party.
            var joiners = GetOtherPartyMemberDetails()
                .Where(m => !m.IsSupportNpc && m.ContentId != 0)
                .Select(m => (m.ContentId, m.JobId)).ToList();
            return SeatsFrom(total, SlotMasksOf(info), info->MemberContentIds.ToArray(), joiners);
        }

        /// <summary>
        /// The seats of the listing this party is in, for drawing every one of them: your own when
        /// you lead it, otherwise the leader's as the snapshot read it - from the game's copy, or
        /// the board's. Null when there is no listing, or no copy of it to read.
        /// </summary>
        public (int Seats, List<ulong> Vacant, int Closed)? ListingSeats(RecruitmentSnapshot snap)
        {
            if (OwnListingSeats() is { } own)
                return own;

            if (!snap.IsRecruiting || snap.IsLeader || snap.DetailsUnavailable
                || snap.SeatMasks.Count < 8 || snap.SlotsTotal is <= 0 or > 8)
                return null;

            // A member's joiners are everybody but the leader - who sits in the first seat, which is
            // never counted as open - and that includes you. Counting the leader and leaving you out
            // took a seat away for the leader's job and left yours showing as empty.
            ulong leader = GetPartyLeaderContentId();
            var joiners = GetOtherPartyMemberDetails()
                .Where(m => !m.IsSupportNpc && m.ContentId != 0 && m.ContentId != leader)
                .Select(m => (m.ContentId, m.JobId)).ToList();
            joiners.Add((playerState.ContentId, GetLocalPlayerJobId()));
            return SeatsFrom(snap.SlotsTotal, snap.SeatMasks.ToArray(), null, joiners);
        }

        /// <summary>
        /// Which seats of a listing are open and which are closed, given its seat count and each
        /// seat's game job mask. <paramref name="seated"/> is the listing's own record of who sits
        /// where, which only the leader's copy has; without it the count is arithmetic alone.
        /// </summary>
        private (int Seats, List<ulong> Vacant, int Closed) SeatsFrom(int total, ulong[] masks, ulong[]? seated,
            List<(ulong ContentId, uint JobId)> joiners)
        {
            seated ??= Array.Empty<ulong>();
            var present = new HashSet<ulong> { playerState.ContentId };
            foreach (var m in GetOtherPartyMemberDetails())
                if (m.ContentId != 0)
                    present.Add(m.ContentId);

            bool seatingKnown = false;
            for (int i = 0; i < total && i < seated.Length; i++)
                seatingKnown |= seated[i] != 0;

            var vacant = new List<ulong>();
            int closed = 0;
            for (int i = 1; i < total; i++)
            {
                if (masks[i] == 0)
                {
                    closed++;
                    continue;
                }
                if (seatingKnown && i < seated.Length && seated[i] != 0 && present.Contains(seated[i]))
                    continue;
                vacant.Add(masks[i]);
            }

            // THE COUNT IS ARITHMETIC, NOT THE GAME'S SEATING. The listing's record of who sits where
            // does not follow people joining - it still said the five newcomers' seats were empty,
            // and the list grew to eleven rows for a listing of seven. However many open seats there
            // are, it is the seats less the closed ones less the people in the party.
            int expected = Math.Max(0, total - closed - 1 - joiners.Count);
            if (vacant.Count > expected)
            {
                // Which seats the joiners took: for each, the NARROWEST open seat their job fits
                // (the job they joined as). A tank goes in the tank-only seat before the tank-or-DPS
                // one, so the flexible seat is the one left open, as the game would leave it. Any
                // left over come off the end.
                foreach (var (contentId, jobId) in joiners)
                {
                    if (vacant.Count <= expected)
                        break;
                    uint job = JoinedJob(contentId, jobId);
                    int at = -1;
                    for (int i = 0; i < vacant.Count; i++)
                    {
                        if (MaskTakesJob(vacant[i], job)
                            && (at < 0 || BitOperations.PopCount(vacant[i]) < BitOperations.PopCount(vacant[at])))
                            at = i;
                    }
                    if (at >= 0)
                        vacant.RemoveAt(at);
                }
                while (vacant.Count > expected)
                    vacant.RemoveAt(vacant.Count - 1);
            }

            // AN OMITTED SEAT IS NOT IN THE LISTING AT ALL. The game drops it from the seat count
            // rather than posting it with no jobs, so a full party with one seat omitted is a
            // listing of seven - and the list showed seven rows with the omitted one missing. Past
            // four seats the duty is a full party of eight, so whatever the count is short of eight
            // was omitted. (Four or fewer could be a light party or a full one half omitted; with
            // nothing to tell them apart, nothing is added.)
            if (total > 4)
                closed += 8 - total;

            return (total, vacant, closed);
        }

        /// <summary>Whether a seat's game job mask takes this job.</summary>
        internal static bool MaskTakesJob(ulong mask, uint job)
        {
            if (job == 0)
                return false;
            for (int bit = 0; bit < 64; bit++)
            {
                if ((mask & (1UL << bit)) != 0 && JobMasks.GetJobIdFromGameBit(bit) == job)
                    return true;
            }
            return false;
        }

        /// <summary>Time left on this character's own listing: exact when the game has shown it,
        /// else counted from when it was posted or first seen.</summary>
        public TimeSpan? OwnListingTimeLeft() => ComputeTimeLeft(out _);

        public unsafe OwnListing? ReadOwnListing()
        {
            var agent = AgentLookingForGroup.Instance();
            ulong id = ResolveOwnListingId();
            if (agent == null || id == 0)
                return null;

            var info = &agent->StoredRecruitmentInfo;
            int total = info->NumberOfSlotsInMainParty;
            if (total <= 0 || total > 8)
                return null;

            byte* comment = (byte*)info + OffsetComment;
            return new OwnListing(
                id.ToString(),
                info->SelectedDutyId,
                *((byte*)info + OffsetSpecificDutyFlag),
                (int)info->SelectedCategory,
                total,
                SlotMasksOf(info),
                CommentText.Decode(comment, MaxCommentLength + 1),
                info->BeginnerFriendly != 0,
                (int)info->Objective,
                (int)info->LootRule,
                info->Password < 10000,
                (int)info->CompletionStatus,
                (int)info->DutyFinderSettingFlags,
                agent->AvgItemLvEnabled != 0 ? Math.Min((int)agent->AvgItemLv, 999) : 0);
        }

        // ── Chat commands ───────────────────────────────────────────

        /// <summary>
        /// Types a command into the chat box, exactly as if the player had. Plugin commands such as
        /// Lifestream's /li are dispatched by Dalamud from the same path, so this reaches them too.
        /// </summary>
        public Task<bool> SendChatCommandAsync(string command)
        {
            if (string.IsNullOrWhiteSpace(command) || !command.StartsWith('/') || command.Length > 100
                || command.IndexOfAny(new[] { '\r', '\n' }) >= 0)
                return Task.FromResult(false);

            return framework.RunOnFrameworkThread(() =>
            {
                unsafe
                {
                    var ui = UIModule.Instance();
                    if (ui == null)
                        return false;

                    var entry = FFXIVClientStructs.FFXIV.Client.System.String.Utf8String
                        .FromSequence(Encoding.UTF8.GetBytes(command));
                    ui->ProcessChatBoxEntry(entry);
                    entry->Dtor(true);
                    return true;
                }
            });
        }

        /// <summary>
        /// Sends a tell to a character on any world, as if typed: "/tell Name@World message". One
        /// line, and short enough for the game's chat box - it refuses anything longer, and a tell
        /// cut off in the middle is worse than one that is not sent.
        /// </summary>
        public Task<bool> SendTellAsync(string name, string world, string message)
        {
            message = message.Replace('\r', ' ').Replace('\n', ' ').Trim();
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(world)
                || message.Length == 0 || message.Length > 400)
                return Task.FromResult(false);

            string command = $"/tell {name}@{world} {message}";
            return framework.RunOnFrameworkThread(() =>
            {
                unsafe
                {
                    var ui = UIModule.Instance();
                    if (ui == null || !clientState.IsLoggedIn)
                        return false;
                    var entry = FFXIVClientStructs.FFXIV.Client.System.String.Utf8String
                        .FromSequence(Encoding.UTF8.GetBytes(command));
                    ui->ProcessChatBoxEntry(entry);
                    entry->Dtor(true);
                    return true;
                }
            });
        }

        // ── Joining ─────────────────────────────────────────────────

        /// <summary>
        /// Opens a listing by id, presses Join Party, types the private password if one is asked
        /// for, confirms, and waits for the party to change. The character must already be on the
        /// listing's data centre - travelling is the caller's job.
        /// </summary>
        public async Task<JoinOutcome> JoinListingAsync(ulong listingId, int? password)
        {
            if (listingId == 0)
                return JoinOutcome.ListingMissing;
            if (disposed || isJoiningListing || isSwitchingListing || isRefreshExecuting
                || isEndingRecruitment || isCapturingListing)
                return JoinOutcome.Busy;

            isJoiningListing = true;
            try
            {
                var gate = await JoinGateAsync();
                if (gate != JoinOutcome.Joined)
                    return gate;

                if (!await OpenListingDetailAsync(listingId))
                    return JoinOutcome.ListingMissing;

                return await PressJoinAsync(password);
            }
            catch (Exception ex)
            {
                pluginLog.Error(ex, "[PF Join] Failed to join a listing.");
                return JoinOutcome.Error;
            }
            finally
            {
                isJoiningListing = false;
            }
        }

        /// <summary>
        /// A listing exactly as the game has it this moment, read from its own detail window. The
        /// seat arrays hold eight per party - an alliance's second party starts at nine - and a
        /// seat's mask is in the game's bit order.
        /// </summary>
        public sealed record FreshListing(
            ulong Id, uint DutyId, int Total, int Filled, int Parties, ulong[] Masks, uint[] Jobs,
            ushort MinItemLevel, byte Completion, byte JoinConditions, byte Objective,
            TimeSpan TimeLeft, string Comment, ushort CurrentWorld, bool JoinEnabled,
            string LeaderName = "", ushort HomeWorld = 0, uint Category = 0, bool Beginners = false,
            byte LootRule = 0, byte DutySettings = 0);

        /// <summary>
        /// The listing of the party this character is in, as the game still holds it from when it
        /// was opened to join - or null when it holds none, or one that is not this party's. No
        /// window: the agent keeps it. Framework thread only.
        /// </summary>
        public unsafe FreshListing? CapturedLeaderListing()
        {
            ulong leader = GetPartyLeaderContentId();
            if (leader == 0 || !CapturedListingIsUsable(leader))
                return null;
            var agent = AgentLookingForGroup.Instance();
            return agent == null ? null : ReadFreshListing(agent->LastViewedListing.ListingId);
        }

        /// <summary>
        /// Joins any listing on this data centre the way the game's own window would, but checks it
        /// first against the listing as it stands NOW rather than as the board last saw it: opens
        /// it (which has the game fetch it afresh), hands the fresh copy to <paramref name="read"/>
        /// so the caller can show it, and asks <paramref name="decide"/> whether this character can
        /// take a seat - a reason when not, otherwise which alliance party to join (-1 for an
        /// ordinary listing). With <paramref name="readOnly"/> it stops after the read. Returns what
        /// happened, the reason when refused, and the fresh copy (null when the listing is gone).
        /// </summary>
        public async Task<(JoinOutcome Outcome, string? Reason, FreshListing? Fresh)> DirectJoinAsync(
            ulong listingId, Action<FreshListing> read, Func<FreshListing, (string? Why, int Party)> decide,
            bool readOnly = false, int? password = null)
        {
            if (listingId == 0)
                return (JoinOutcome.ListingMissing, null, null);
            if (disposed || isJoiningListing || isSwitchingListing || isRefreshExecuting
                || isEndingRecruitment || isCapturingListing)
                return (JoinOutcome.Busy, null, null);

            isJoiningListing = true;
            try
            {
                if (!readOnly)
                {
                    var gate = await JoinGateAsync(asGroup: true);
                    if (gate != JoinOutcome.Joined)
                        return (gate, null, null);
                }

                if (!await OpenListingDetailAsync(listingId))
                    return (JoinOutcome.ListingMissing, null, null);

                var fresh = await framework.RunOnFrameworkThread(() => ReadFreshListing(listingId));
                if (fresh == null)
                    return (JoinOutcome.ListingMissing, null, null);

                // Both on the framework thread: the check reads the character's gear, job and
                // completion from game memory, and the read updates a board the UI draws from.
                await framework.RunOnFrameworkThread(() => read(fresh));
                if (readOnly)
                    return (JoinOutcome.Checked, null, fresh);

                var (why, party) = await framework.RunOnFrameworkThread(() => decide(fresh));
                if (why != null)
                    return (JoinOutcome.JoinUnavailable, why, fresh);

                return (await PressJoinAsync(password, party), null, fresh);
            }
            catch (Exception ex)
            {
                pluginLog.Error(ex, "[PF Join] Failed to join a listing.");
                return (JoinOutcome.Error, null, null);
            }
            finally
            {
                isJoiningListing = false;
            }
        }

        /// <summary>The listing open in the game's detail window right now, read in full, or null when
        /// none is open. Framework thread only.</summary>
        public unsafe FreshListing? ViewedListing()
        {
            if (!ListingDetailIsOpen())
                return null;
            var agent = AgentLookingForGroup.Instance();
            return agent == null || agent->LastViewedListing.ListingId == 0
                ? null
                : ReadFreshListing(agent->LastViewedListing.ListingId);
        }

        /// <summary>Whether a join may start at all: logged in, not in a duty, queue or fight, and
        /// not already in a party. Joined means "go ahead".</summary>
        private Task<JoinOutcome> JoinGateAsync(bool asGroup = false)
            => framework.RunOnFrameworkThread(() =>
            {
                if (!clientState.IsLoggedIn || IsInDuty() || IsInDutyQueue() || IsInCombat()
                    || RecruitingStatusSet() || IsRecruiting())
                    return JoinOutcome.NotNow;
                if (GetOtherPartyMemberDetails().Count == 0)
                    return JoinOutcome.Joined;
                // In a party: only a leader joining with the whole group may go ahead.
                return asGroup && IsPartyLeader() ? JoinOutcome.Joined : JoinOutcome.InParty;
            });

        /// <summary>Opens a listing's detail window and waits for it to show that listing. Opening
        /// is what has the game ask the server for it, so what it shows is current.</summary>
        private async Task<bool> OpenListingDetailAsync(ulong listingId)
        {
            bool opened = await framework.RunOnFrameworkThread(() =>
            {
                unsafe
                {
                    var agent = AgentLookingForGroup.Instance();
                    return agent != null && agent->OpenListing(listingId);
                }
            });
            if (!opened)
                return false;

            bool shown = false;
            for (int i = 0; i < 80 && !disposed && !shown; i++)
            {
                await Task.Delay(50);
                shown = await framework.RunOnFrameworkThread(() =>
                {
                    unsafe
                    {
                        var agent = AgentLookingForGroup.Instance();
                        return ListingDetailIsOpen() && agent != null
                            && agent->LastViewedListing.ListingId == listingId;
                    }
                });
            }
            if (!shown)
                return false;

            // The detail window fills its buttons a beat after it appears.
            await Task.Delay(300);
            return true;
        }

        private unsafe FreshListing? ReadFreshListing(ulong listingId)
        {
            var agent = AgentLookingForGroup.Instance();
            if (agent == null || agent->LastViewedListing.ListingId != listingId)
                return null;

            ref var l = ref agent->LastViewedListing;
            int parties = Math.Clamp((int)l.NumberOfParties, 1, 6);
            int seats = Math.Min(parties * 8, l.SlotFlags.Length);
            var masks = new ulong[seats];
            var jobs = new uint[seats];
            for (int i = 0; i < seats; i++)
            {
                masks[i] = l.SlotFlags[i];
                jobs[i] = i < l.Jobs.Length ? l.Jobs[i] : 0u;
            }

            var detail = (AddonLookingForGroupDetail*)(nint)gameGui.GetAddonByName("LookingForGroupDetail");
            bool joinEnabled = false;
            if (detail != null)
            {
                if (parties > 1)
                {
                    for (int p = 0; p < parties && p < detail->JoinAllianceButtons.Length; p++)
                    {
                        var b = detail->JoinAllianceButtons[p].Value;
                        joinEnabled |= b != null && b->IsEnabled;
                    }
                }
                else
                {
                    joinEnabled = detail->JoinPartyButton != null && detail->JoinPartyButton->IsEnabled;
                }
            }

            return new FreshListing(
                listingId, l.DutyId, l.TotalSlots, l.SlotsFilled, parties, masks, jobs,
                l.AvgItemLv, (byte)l.CompletionStatus, (byte)l.JoinConditionFlags, (byte)l.Objective,
                TimeSpan.FromSeconds(l.TimeLeft), CommentText.Decode(l.Comment), l.CurrentWorld, joinEnabled,
                l.LeaderString ?? string.Empty, l.HomeWorld, (uint)l.Category, l.BeginnerFriendly != 0,
                (byte)l.LootRule, (byte)l.DutyFinderSettingFlags);
        }

        /// <summary>Presses Join Party on the open detail window, types a private password if one is
        /// asked for, confirms, and waits for the party to change.</summary>
        private async Task<JoinOutcome> PressJoinAsync(int? password, int party = -1)
        {
            // JOINED MEANS THE PARTY GREW. Joining alone, it goes from nobody to somebody; joining as
            // a leader with a group, it goes from the group to the group plus the listing's party.
            int before = await CountOtherPartyMembersAsync();

            // An alliance's window has a Join button per party in place of the one Join Party.
            bool clicked = await framework.RunOnFrameworkThread(() =>
            {
                unsafe
                {
                    var detail = (AddonLookingForGroupDetail*)(nint)gameGui.GetAddonByName("LookingForGroupDetail");
                    if (detail == null)
                        return false;
                    AtkComponentButton* button = party >= 0 && party < detail->JoinAllianceButtons.Length
                        ? detail->JoinAllianceButtons[party].Value
                        : detail->JoinPartyButton;
                    return button != null && button->IsEnabled
                        && AtkHelpers.ClickAddonButton(&detail->AtkUnitBase, button);
                }
            });
            if (!clicked)
                return JoinOutcome.JoinUnavailable;

            // Four digits, leading zeros allowed - "0042" is a password the game accepts.
            if (password is >= 0 and <= 9999)
            {
                string? where = null;
                for (int i = 0; i < 60 && !disposed && where == null; i++)
                {
                    await Task.Delay(50);
                    where = await framework.RunOnFrameworkThread(() => TryEnterPrivatePassword(password.Value));
                }

                if (where == null)
                {
                    pluginLog.Warning("[PF Join] No password prompt with a numeric field appeared after Join Party.");
                    return JoinOutcome.PasswordPrompt;
                }

                pluginLog.Information($"[PF Join] Entered the private password in {where}.");
            }

            // Some joins ask "Join this party?" and some do not; this watches briefly either way.
            await ConfirmYesNoPromptAsync("[PF Join]");

            for (int i = 0; i < 100 && !disposed; i++)
            {
                await Task.Delay(100);
                if (await CountOtherPartyMembersAsync() > before)
                    return JoinOutcome.Joined;
            }

            return JoinOutcome.NotAccepted;
        }

        /// <summary>
        /// This character's average item level, as the game averages it: the twelve gear slots,
        /// with the main hand counted twice when there is nothing in the off hand. Zero when the
        /// gear cannot be read.
        /// </summary>
        public unsafe int AverageItemLevel()
        {
            var inv = FFXIVClientStructs.FFXIV.Client.Game.InventoryManager.Instance();
            if (inv == null)
                return 0;
            var eq = inv->GetInventoryContainer(FFXIVClientStructs.FFXIV.Client.Game.InventoryType.EquippedItems);
            if (eq == null || !eq->IsLoaded)
                return 0;

            int Level(int slot)
            {
                var item = eq->GetInventorySlot(slot);
                return item == null || item->ItemId == 0 ? 0 : dutyDataHelper.ItemLevelOf(item->ItemId);
            }

            int[] slots = { 0, 1, 2, 3, 4, 6, 7, 8, 9, 10, 11, 12 };
            int sum = 0;
            foreach (int slot in slots)
                sum += Level(slot);
            if (Level(1) == 0)
                sum += Level(0);
            return sum / 12;
        }

        /// <summary>How many parties of eight a duty takes: 1 for a light or full party, 3 for an
        /// alliance raid, 6 for a 48-player field operation. From the duty's own queue size.</summary>
        public int PartiesForDuty(uint dutyRowId)
            => dutyDataHelper.GetDutyEntry(dutyRowId) is { QueueMaxPlayers: > 8 } duty
                ? Math.Clamp((int)duty.QueueMaxPlayers / 8, 1, 6)
                : 1;

        /// <summary>Whether this character has completed an instanced duty, by its
        /// ContentFinderCondition row. Null when the duty is not one the game keeps that record for.</summary>
        public unsafe bool? DutyCompleted(uint dutyRowId)
        {
            var duty = dutyDataHelper.GetDutyEntry(dutyRowId);
            if (duty == null || duty.ContentLinkType != 1 || duty.ContentRowId == 0)
                return null;
            return FFXIVClientStructs.FFXIV.Client.Game.UI.UIState.IsInstanceContentCompleted(duty.ContentRowId);
        }

        /// <summary>Labels a password prompt's confirm button may carry.</summary>
        private static readonly string[] PasswordConfirmLabels = { "ok", "join", "enter", "confirm", "submit" };

        /// <summary>
        /// Finds the private-password prompt and fills it in. Returns the addon's name, or null
        /// while no prompt is up.
        ///
        /// FOUND BY WHAT IT CONTAINS, NOT BY NAME. The prompt has not been mapped, so this looks at
        /// every visible Party Finder window (and the game's generic numeric popup) for one holding
        /// a numeric input, which is what a four-digit password field is. The recruitment window is
        /// excluded - it has a password field of its own, for setting one. The confirm button is
        /// then found by its label; if there is no button that reads like one, the value is left
        /// typed in and the player presses it.
        /// </summary>
        private unsafe string? TryEnterPrivatePassword(int password)
        {
            var manager = RaptureAtkUnitManager.Instance();
            if (manager == null)
                return null;

            var list = &manager->AtkUnitManager.AllLoadedUnitsList;
            for (int i = 0; i < list->Count && i < list->Entries.Length; i++)
            {
                AtkUnitBase* addon = list->Entries[i].Value;
                if (addon == null || !addon->IsVisible)
                    continue;

                string name = addon->NameString;
                bool candidate = (name.StartsWith("LookingForGroup", StringComparison.Ordinal)
                        && name != "LookingForGroupCondition")
                    || name == "InputNumeric";
                if (!candidate)
                    continue;

                var input = FindNumericInput(addon->RootNode, 0);
                if (input == null)
                    continue;

                input->SetValue(password);
                input->UpdateTextNode();

                var confirm = FindButtonByLabel(addon, PasswordConfirmLabels);
                if (confirm != null)
                    AtkHelpers.ClickAddonButton(addon, confirm);
                else
                    pluginLog.Warning($"[PF Join] Typed the password into {name} but found no confirm button to press.");

                return name;
            }

            return null;
        }

        private unsafe AtkComponentNumericInput* FindNumericInput(AtkResNode* node, int depth)
        {
            if (node == null || depth > 32)
                return null;

            var input = node->GetAsAtkComponentNumericInput();
            if (input != null)
                return input;

            for (var child = node->ChildNode; child != null; child = child->NextSiblingNode)
            {
                var found = FindNumericInput(child, depth + 1);
                if (found != null)
                    return found;
            }

            return null;
        }

        private static unsafe AtkComponentButton* FindButtonByLabel(AtkUnitBase* addon, string[] labels)
        {
            var nodes = addon->UldManager.Nodes;
            for (int i = 0; i < addon->UldManager.NodeListCount && i < nodes.Length; i++)
            {
                AtkResNode* node = nodes[i].Value;
                if (node == null || (int)node->Type < 1000)
                    continue;

                var button = node->GetAsAtkComponentButton();
                if (button == null || !button->IsEnabled)
                    continue;

                string label = AtkHelpers.GetButtonLabel(button);
                if (label.Length > 0 && Array.Exists(labels, l => label.Contains(l, StringComparison.OrdinalIgnoreCase)))
                    return button;
            }

            return null;
        }

        // ── The host's listing ──────────────────────────────────────

        /// <summary>
        /// Takes the current listing down and posts <paramref name="preset"/> in its place, for the
        /// host's public-to-private handoff and for going back again. Returns the new listing id, or
        /// null if either half failed.
        ///
        /// A FAILURE AFTER THE TAKE-DOWN LEAVES NO LISTING UP. The caller is told the take-down
        /// happened through <paramref name="takenDown"/>, so it can put the old listing back rather
        /// than leaving the host unlisted.
        /// </summary>
        public async Task<string?> SwitchListingAsync(PfPresetData preset, Action? takenDown = null)
        {
            if (disposed || isSwitchingListing || isJoiningListing || isRefreshExecuting || isEndingRecruitment)
                return null;

            isSwitchingListing = true;
            try
            {
                bool ok = await framework.RunOnFrameworkThread(() =>
                    !IsInDuty() && !IsInCombat() && IsPartyLeader()
                    && currentStep is AutomationStep.Idle or AutomationStep.Done);
                if (!ok)
                    return null;

                if (await framework.RunOnFrameworkThread(() => IsRecruiting()))
                {
                    if (!await OpenOwnListing() || !await WaitForEndRecruitmentButton())
                        return null;
                    await ConfirmEndRecruitmentDialogAsync();

                    bool down = false;
                    for (int i = 0; i < 100 && !disposed && !down; i++)
                    {
                        await Task.Delay(100);
                        down = !await framework.RunOnFrameworkThread(() => IsRecruiting());
                    }
                    if (!down)
                        return null;

                    ResetTimeTracking();
                }

                takenDown?.Invoke();

                // The window needs a moment after a take-down before it opens for a new recruitment.
                await Task.Delay(1000);
                await framework.RunOnFrameworkThread(() => ApplyPreset(preset));

                for (int i = 0; i < 400 && !disposed; i++)
                {
                    await Task.Delay(50);
                    if (currentStep == AutomationStep.Done)
                        break;
                }
                if (currentStep != AutomationStep.Done || IsAutomationFailed)
                    return null;

                for (int i = 0; i < 100 && !disposed; i++)
                {
                    string? id = await framework.RunOnFrameworkThread(() => IsRecruiting() ? GetOwnListingId() : null);
                    if (id != null)
                        return id;
                    await Task.Delay(100);
                }

                return null;
            }
            catch (Exception ex)
            {
                pluginLog.Error(ex, "[PF Coordination] Could not switch the listing.");
                return null;
            }
            finally
            {
                isSwitchingListing = false;
            }
        }
    }
}
