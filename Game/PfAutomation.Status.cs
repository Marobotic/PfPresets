using System;
using System.Collections.Generic;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Arrays;
using FFXIVClientStructs.FFXIV.Client.UI.Info;

namespace PfPresets
{
    /// <summary>What the player is currently doing, as far as recruiting is concerned. Ordered
    /// roughly by how strongly it blocks applying a preset.</summary>
    public enum PfActivity
    {
        /// <summary>Free to recruit - nothing to report.</summary>
        Idle,
        /// <summary>A Party Finder listing is up (ours or the party leader's).</summary>
        Recruiting,
        /// <summary>In a party, but not the leader, so recruitment isn't ours to start.</summary>
        InPartyNotLeader,
        /// <summary>Registered in the Duty Finder queue.</summary>
        InQueue,
        /// <summary>Inside a duty.</summary>
        InDuty,
        /// <summary>Not logged in.</summary>
        NotLoggedIn,
    }

    /// <summary>One filled seat in the current listing.</summary>
    public readonly struct FilledSeat
    {
        public readonly uint JobId;
        public readonly string Name;
        public readonly bool IsYou;

        public FilledSeat(uint jobId, string name, bool isYou)
        {
            JobId = jobId;
            Name = name;
            IsYou = isYou;
        }
    }

    /// <summary>One seat still being recruited for, described by what it will accept.</summary>
    public readonly struct OpenSeat
    {
        /// <summary>Role the seat resolves to, for the icon.</summary>
        public readonly RoleType Role;
        /// <summary>Set when the seat is locked to exactly one job.</summary>
        public readonly uint? JobId;
        /// <summary>Human-readable description ("Any healer", "White Mage", "Any job"…).</summary>
        public readonly string Label;

        public OpenSeat(RoleType role, uint? jobId, string label)
        {
            Role = role;
            JobId = jobId;
            Label = label;
        }
    }

    /// <summary>
    /// A read-only picture of the current recruitment/party situation, rebuilt at most once per
    /// frame for the status box. Everything here is derived from live game memory, so nothing is
    /// cached beyond the frame.
    /// </summary>
    public sealed class RecruitmentSnapshot
    {
        public PfActivity Activity { get; init; } = PfActivity.Idle;

        /// <summary>True when a Party Finder listing is currently up.</summary>
        public bool IsRecruiting { get; init; }

        /// <summary>True when the local player leads the party (and so owns the listing).</summary>
        public bool IsLeader { get; init; }

        /// <summary>
        /// True when this is a cross-world party and the local player leads it.
        ///
        /// Distinct from <see cref="IsLeader"/> because leaving means something different here. In
        /// an ordinary party a leader who leaves hands the party on and it carries on without them;
        /// a cross-world party is the listing, so the leader walking out is the party ending -
        /// which makes "Leave Party" a description of the mechanic rather than of the outcome.
        /// </summary>
        public bool IsCrossWorldLeader { get; init; }

        /// <summary>
        /// True when there is anyone else in the party right now, recruiting or not.
        ///
        /// An ordinary party - one with no listing up - otherwise classified as Idle with nothing
        /// to say, so <see cref="HasAnythingToShow"/> was false and the card was suppressed, which
        /// took the embedded party list down with it. This keeps the card alive for any party.
        /// </summary>
        public bool InParty { get; init; }

        public string DutyName { get; init; } = string.Empty;

        /// <summary>
        /// ContentFinderCondition row for the listed duty, or 0 when none is set.
        ///
        /// Carried alongside the name because the name alone can't be reasoned about: "None" and
        /// "Unknown duty (1234)" are both strings, and anything deciding whether a duty is worth
        /// looking up needs the id to ask what kind of content it is.
        /// </summary>
        public uint DutyRowId { get; init; }
        public string Comment { get; init; } = string.Empty;

        /// <summary>Name of the listing's leader, when someone else owns it and we've seen it.</summary>
        public string LeaderName { get; init; } = string.Empty;

        /// <summary>
        /// The leader's home world row id, beside their name, because a name on its own is not a
        /// character - two worlds can hold the same one. An id rather than a name because turning
        /// one into the other needs the world sheet, which lives in WorldHelper and not here.
        ///
        /// ONE ANSWER FOR EVERY CONSUMER. The recruit tab names the leader on screen and the
        /// crowdsourcing publishes a party under that same leader; those two must agree exactly or
        /// they are describing different listings. They used to work it out separately - the tab
        /// from the listing the game last showed it, the reporting half from the party list - and
        /// separately is how they came to disagree.
        /// </summary>
        public uint LeaderWorldId { get; init; }

        /// <summary>True when someone else is recruiting and we have no trustworthy read of their
        /// listing, so the card should say so instead of showing our own stale settings.</summary>
        public bool DetailsUnavailable { get; init; }

        public IReadOnlyList<FilledSeat> Filled { get; init; } = Array.Empty<FilledSeat>();
        public IReadOnlyList<OpenSeat> Open { get; init; } = Array.Empty<OpenSeat>();

        public int SlotsTotal { get; init; }
        public int SlotsFilled => Filled.Count;

        /// <summary>Time until the listing expires, or null when we have no idea.</summary>
        public TimeSpan? TimeLeft { get; init; }

        /// <summary>False when <see cref="TimeLeft"/> is our own estimate rather than a value read
        /// from the game, so the UI can mark it approximate instead of implying precision.</summary>
        public bool TimeLeftIsExact { get; init; }

        /// <summary>Why applying a preset is currently refused, or empty when it isn't.</summary>
        public string BlockedReason { get; init; } = string.Empty;

        /// <summary>True when every seat in the listing is taken.</summary>
        public bool IsPartyFull => IsRecruiting && SlotsTotal > 0 && SlotsFilled >= SlotsTotal;

        /// <summary>True when there is anything worth showing the user.</summary>
        public bool HasAnythingToShow =>
            IsRecruiting || Activity != PfActivity.Idle || BlockedReason.Length > 0 || InParty;
    }

    public partial class PfAutomation
    {
        /// <summary>A Party Finder listing lives for one hour.</summary>
        private static readonly TimeSpan ListingLifetime = TimeSpan.FromMinutes(60);

        // Time-remaining tracking. The game only exposes a listing's real TimeLeft while a detail
        // window is open, so we take an exact reading whenever one happens to be open on our own
        // listing (the Auto Refresher opens it every cycle) and fall back to counting down from
        // when we first saw recruitment start.
        private DateTime? recruitingSince;
        private TimeSpan? capturedTimeLeft;
        private DateTime capturedAt;

        /// <summary>
        /// The duty of the most recent listing we saw up, kept after recruitment ends.
        ///
        /// A party that fills stops recruiting, and the game then has no listing left to read the
        /// duty from - so the progress readout for the fight they just filled for would vanish the
        /// instant it mattered most. Holding it here lets a full party still show its prog point.
        /// Always a real game duty id (never a synthetic one), so <see cref="DutyHasProgress"/>
        /// downstream can reason about it.
        ///
        /// <c>Slots</c> is the listing's seat count, carried because "the party filled it" is the
        /// only thing that keeps this alive once recruitment stops - see
        /// <see cref="RecruitedDutyStillApplies"/> - and eight is not the answer for every listing.
        /// </summary>
        private (uint RowId, string Name, int Slots)? lastRecruitedDuty;

        private RecruitmentSnapshot? snapshotThisFrame;
        private int snapshotFrame = -1;

        /// <summary>Drops the per-frame snapshot cache so the next read rebuilds it.</summary>
        public void InvalidateSnapshot() => snapshotFrame = -1;

        /// <summary>
        /// Builds (or returns this frame's cached) picture of the current situation. Reads native
        /// party and agent memory, so the UI must not call it per-widget.
        /// </summary>
        public unsafe RecruitmentSnapshot GetSnapshot(int frameCount)
        {
            if (snapshotThisFrame != null && snapshotFrame == frameCount)
                return snapshotThisFrame;

            snapshotFrame = frameCount;
            snapshotThisFrame = BuildSnapshot();
            return snapshotThisFrame;
        }

        private unsafe RecruitmentSnapshot BuildSnapshot()
        {
            if (!clientState.IsLoggedIn)
            {
                ResetTimeTracking();
                return new RecruitmentSnapshot
                {
                    Activity = PfActivity.NotLoggedIn,
                    BlockedReason = "You are not logged in.",
                };
            }

            // IsRecruiting() only covers OUR listing - the UsingPartyFinder flag belongs to whoever
            // owns it. Joining someone's Party Finder leaves it false, so also treat "the leader is
            // flagged as recruiting" as an active listing.
            bool ownListing = IsRecruiting();
            bool recruiting = ownListing || IsPartyLeaderRecruiting();
            TrackRecruitmentWindow(ownListing);

            // CanRecruit owns the "may I apply?" rules; the box just reports what it decided so the
            // two can never disagree.
            bool canRecruit = CanRecruit(out string reason);

            if (!recruiting)
            {
                int partySize = CurrentPartySize();
                bool inParty = partySize > 0;

                // The listing's duty only outlives the listing while the party still stands for
                // it. Once that stops being true - someone left, the group broke up, we're off
                // doing something else - the fight is forgotten rather than trailed behind us.
                if (!RecruitedDutyStillApplies(partySize))
                    lastRecruitedDuty = null;

                var (ctxName, ctxRow) = ResolveIdleContextDuty();

                return new RecruitmentSnapshot
                {
                    Activity = ClassifyIdleActivity(),
                    IsLeader = IsPartyLeader(),
                    IsCrossWorldLeader = IsCrossWorldPartyLeader(),
                    InParty = inParty,
                    DutyName = ctxName,
                    DutyRowId = ctxRow,
                    BlockedReason = canRecruit ? string.Empty : reason,
                };
            }

            var agent = AgentLookingForGroup.Instance();
            if (agent == null)
            {
                return new RecruitmentSnapshot
                {
                    Activity = PfActivity.Recruiting,
                    IsRecruiting = true,
                    IsLeader = IsPartyLeader(),
                    IsCrossWorldLeader = IsCrossWorldPartyLeader(),
                    BlockedReason = canRecruit ? string.Empty : reason,
                };
            }

            bool isLeader = IsPartyLeader();

            // Filled seats: you first, then the rest of the party.
            var filled = new List<FilledSeat>();
            filled.Add(new FilledSeat(GetLocalPlayerJobId(), "You", true));
            foreach (var (jobId, name) in GetOtherPartyMembers())
                filled.Add(new FilledSeat(jobId, name, false));

            var common = new
            {
                Activity = PfActivity.Recruiting,
                IsRecruiting = true,
                IsLeader = isLeader,
                IsCrossWorldLeader = IsCrossWorldPartyLeader(),
                Blocked = canRecruit ? string.Empty : reason,
            };

            if (isLeader)
            {
                // Our own listing: StoredRecruitmentInfo is what we posted.
                var info = &agent->StoredRecruitmentInfo;
                int total = info->NumberOfSlotsInMainParty;
                if (total <= 0 || total > 8)
                    total = 8;

                string ownDutyName = ResolveListedDutyName(info->SelectedDutyId);
                RememberRecruitedDuty(info->SelectedDutyId, ownDutyName, total);

                return new RecruitmentSnapshot
                {
                    Activity = common.Activity,
                    IsRecruiting = common.IsRecruiting,
                    IsLeader = true,
                    DutyName = ownDutyName,
                    DutyRowId = info->SelectedDutyId,
                    // Read as SeString, not as text: CommentString decodes an auto-translate
                    // phrase's payload bytes as if they were characters and hands back junk.
                    Comment = CommentText.Decode((byte*)info + OffsetComment, MaxCommentLength + 1),
                    Filled = filled,
                    Open = BuildOpenSeatsFromMasks(SlotMasksOf(info), total, filled.Count),
                    SlotsTotal = total,
                    TimeLeft = ComputeTimeLeft(out bool exactOwn),
                    TimeLeftIsExact = exactOwn,
                    BlockedReason = common.Blocked,
                };
            }

            // Someone else leads. StoredRecruitmentInfo holds OUR last-configured settings, not
            // their listing, so reading it here would show a confidently wrong duty and comment.
            // The only trustworthy source is a detail window that has actually been opened on this
            // party's listing - the watcher does that by itself, and until it succeeds the card
            // stays quiet about what it can't know rather than guessing.
            var viewed = agent->LastViewedListing;
            ulong partyLeaderId = GetPartyLeaderContentId();

            // WHO LEADS THIS PARTY IS KNOWN EVEN WHEN ITS LISTING IS NOT. The party itself says so,
            // and it says so without any window having been opened - unlike LastViewedListing
            // below, which is only the leader's listing while it happens to be the one the game is
            // holding. Read first, so both branches below carry the same answer.
            bool havePartyLeader = TryGetPartyLeader(out string partyLeaderName, out uint partyLeaderWorldId);

            if (!CapturedListingIsUsable(partyLeaderId))
            {
                return new RecruitmentSnapshot
                {
                    Activity = common.Activity,
                    IsRecruiting = common.IsRecruiting,
                    IsLeader = false,

                    // Named even here. The details of the listing are what is unavailable; the
                    // person leading the party is not, and the reporting half needs only them.
                    LeaderName = havePartyLeader ? partyLeaderName : string.Empty,
                    LeaderWorldId = havePartyLeader ? partyLeaderWorldId : 0,

                    DutyName = string.Empty,
                    Filled = filled,
                    SlotsTotal = filled.Count,
                    BlockedReason = common.Blocked,
                    DetailsUnavailable = true,
                };
            }

            int viewedTotal = viewed.TotalSlots > 0 && viewed.TotalSlots <= 8 ? viewed.TotalSlots : 8;
            var viewedMasks = new ulong[8];
            for (int i = 0; i < 8; i++)
                viewedMasks[i] = viewed.SlotFlags[i];

            string viewedDutyName = ResolveListedDutyName(viewed.DutyId);
            RememberRecruitedDuty(viewed.DutyId, viewedDutyName, viewedTotal);

            var capturedLeft = CapturedListingTimeLeft();

            return new RecruitmentSnapshot
            {
                Activity = common.Activity,
                IsRecruiting = common.IsRecruiting,
                IsLeader = false,
                // The party's answer wins over the listing's: same character, but the party is
                // current and the captured listing is a copy of a moment.
                LeaderName = havePartyLeader
                    ? partyLeaderName
                    : viewed.LeaderString.ToString() ?? string.Empty,
                LeaderWorldId = havePartyLeader ? partyLeaderWorldId : 0,
                DutyName = viewedDutyName,
                DutyRowId = viewed.DutyId,
                Comment = CommentText.Decode(viewed.Comment),
                Filled = filled,
                Open = BuildOpenSeatsFromMasks(viewedMasks, viewedTotal, viewed.SlotsFilled),
                SlotsTotal = viewedTotal,

                // The listing carried a real countdown when it was read, but the stored value is
                // frozen at that moment and never moves again - so it is aged against the clock
                // here. Still exact when it's there: a known reading plus known elapsed time,
                // rather than the hour-long guess our own listing falls back to.
                TimeLeft = capturedLeft,
                TimeLeftIsExact = capturedLeft.HasValue,
                BlockedReason = common.Blocked,
            };
        }

        /// <summary>
        /// True when the party leader is advertising on the Party Finder.
        ///
        /// Read from their online status ("Recruiting Party Members", row 26) - the same signal the
        /// party list draws its icon from - because the UsingPartyFinder condition flag belongs
        /// only to the player who owns the listing, not to everyone who joined it.
        ///
        /// When the leader isn't loaded on this client there is no status to read, and that is the
        /// ordinary case in a cross-world party: the answer then comes from a listing the watcher
        /// has actually fetched for this party, aged against its own clock so an ended listing
        /// can't keep reporting itself as live.
        /// </summary>
        /// <summary>
        /// Whether this is a cross-world party led by the local player.
        ///
        /// Both halves are read from the same proxy in one go: a same-world party has no cross-world
        /// leader to be, and asking the cross-realm proxy about leadership when
        /// <c>IsInCrossRealmParty</c> is false answers about a party that isn't there.
        /// </summary>
        public unsafe bool IsCrossWorldPartyLeader()
        {
            try
            {
                var proxy = InfoProxyCrossRealm.Instance();
                return proxy != null
                    && proxy->IsInCrossRealmParty
                    && InfoProxyCrossRealm.IsLocalPlayerPartyLeader();
            }
            catch (Exception)
            {
                // A status read must never throw into the draw loop; not knowing means the button
                // stays "Leave Party", which is the safe answer either way.
                return false;
            }
        }

        public bool IsPartyLeaderRecruiting()
        {
            ulong leaderId = GetPartyLeaderContentId();
            if (leaderId == 0)
                return false;

            // When we're the leader, our own state is authoritative and always readable - there's
            // nothing to infer. Falling through would reach the captured-listing fallback below,
            // which is stale by nature: it holds whatever listing was last opened in a detail
            // window, with a TimeLeft frozen from when it was read. Opening your own listing once
            // and then ending it left that check reporting us as recruiting indefinitely, which is
            // why "End Recruitment" kept appearing with no listing up.
            if (leaderId == playerState.ContentId)
                return IsRecruiting();

            var state = ReadLeaderRecruitState(leaderId);
            if (state != LeaderRecruitState.Unknown)
                return state == LeaderRecruitState.Recruiting;

            return CapturedListingIsUsable(leaderId);
        }

        /// <summary>ClassJob-independent online status row for "Recruiting Party Members".</summary>
        private const uint OnlineStatusRecruiting = 26;

        /// <summary>What the player is doing when not recruiting, for the box's heading.</summary>
        private PfActivity ClassifyIdleActivity()
        {
            if (IsInDuty()) return PfActivity.InDuty;
            if (IsInDutyQueue()) return PfActivity.InQueue;
            if (!IsPartyLeader()) return PfActivity.InPartyNotLeader;
            return PfActivity.Idle;
        }

        /// <summary>Copies the eight per-slot job masks out of our own stored recruitment info.</summary>
        private unsafe ulong[] SlotMasksOf(AgentLookingForGroup.RecruitmentSub* info)
        {
            ulong* slotFlags = (ulong*)((byte*)info + OffsetSlotFlags);
            var masks = new ulong[8];
            for (int i = 0; i < 8; i++)
                masks[i] = slotFlags[i];
            return masks;
        }

        /// <summary>The party leader's content id, or 0 when solo or unavailable. Used to tell
        /// whether a viewed listing belongs to the party we're actually in.</summary>
        /// <summary>
        /// The party leader's name and home world, read from the party itself.
        ///
        /// NOT FROM THE LISTING, and that is the whole point of it existing. The snapshot's
        /// <c>LeaderName</c> comes from <c>agent-&gt;LastViewedListing</c> - whatever listing this
        /// client last opened in a detail window - so it is populated only while the party's own
        /// listing happens to still be the captured one. Somebody who joined a party through the
        /// browser and then went about their business has an empty LeaderName within minutes, and
        /// anything keyed on it silently stops working for them.
        ///
        /// The party is always there and always current, so this answers whenever there is a party
        /// with a leader who is not us.
        /// </summary>
        /// <returns>False when solo, when the leader cannot be identified, or when the leader is
        /// the local player - the caller knows its own name and world already.</returns>
        public bool TryGetPartyLeader(out string name, out uint homeWorldId)
        {
            name = string.Empty;
            homeWorldId = 0;

            try
            {
                ulong leaderId = GetPartyLeaderContentId();
                if (leaderId == 0 || leaderId == playerState.ContentId)
                    return false;

                foreach (var member in GetOtherPartyMemberDetails())
                {
                    if (member.ContentId != leaderId || member.IsSupportNpc)
                        continue;

                    name = member.Name;
                    homeWorldId = member.HomeWorldId;
                    return name.Length > 0 && homeWorldId != 0;
                }
            }
            catch (Exception)
            {
                // A party read must never throw into the caller's loop; not knowing is an answer.
            }

            return false;
        }

        private unsafe ulong GetPartyLeaderContentId()
        {
            var crossRealmProxy = FFXIVClientStructs.FFXIV.Client.UI.Info.InfoProxyCrossRealm.Instance();
            if (crossRealmProxy != null && crossRealmProxy->IsInCrossRealmParty)
            {
                for (int i = 0; i < crossRealmProxy->GroupCount; i++)
                {
                    var group = crossRealmProxy->CrossRealmGroups[i];
                    for (int c = 0; c < group.GroupMemberCount; c++)
                    {
                        var member = group.GroupMembers[c];
                        if (member.IsPartyLeader)
                            return member.ContentId;
                    }
                }
                return 0;
            }

            var groupManager = FFXIVClientStructs.FFXIV.Client.Game.Group.GroupManager.Instance();
            if (groupManager == null || groupManager->MainGroup.MemberCount == 0)
                return 0;

            var leader = groupManager->MainGroup.GetPartyMemberByIndex((int)groupManager->MainGroup.PartyLeaderIndex);
            return leader != null ? leader->ContentId : 0;
        }

        /// <summary>Describes the seats still being recruited for, from the listing's per-slot job
        /// masks. Seats before <paramref name="filledCount"/> are taken, so only the rest matter.</summary>
        private List<OpenSeat> BuildOpenSeatsFromMasks(ulong[] slotFlags, int total, int filledCount)
        {
            var open = new List<OpenSeat>();

            for (int i = Math.Max(0, filledCount); i < Math.Min(total, slotFlags.Length); i++)
            {
                ulong gameMask = slotFlags[i];

                if (gameMask == 0)
                {
                    open.Add(new OpenSeat(RoleType.Free, null, "Any job"));
                    continue;
                }

                // Exactly one bit: the seat is locked to a single job.
                if ((gameMask & (gameMask - 1)) == 0)
                {
                    int bit = System.Numerics.BitOperations.TrailingZeroCount(gameMask);
                    uint jobId = JobMasks.GetJobIdFromGameBit(bit);
                    var job = JobData.FindById(jobId);
                    if (job != null)
                    {
                        open.Add(new OpenSeat(JobData.GetRoleForCategory(job.Category), jobId, job.Name));
                        continue;
                    }
                }

                open.Add(new OpenSeat(DescribeMaskRole(gameMask, out string label), null, label));
            }

            return open;
        }

        /// <summary>Reduces a multi-job slot mask to a single role plus a readable label.</summary>
        private static RoleType DescribeMaskRole(ulong gameMask, out string label)
        {
            bool tank = false, healer = false, dps = false;
            int jobCount = 0;

            foreach (var job in JobData.AllJobsAndClasses)
            {
                int bit = JobMasks.GetGameJobBitIndex((uint)job.Id);
                if (bit == -1 || (gameMask & (1UL << bit)) == 0)
                    continue;

                jobCount++;
                switch (JobData.GetRoleForCategory(job.Category))
                {
                    case RoleType.Tank: tank = true; break;
                    case RoleType.Healer: healer = true; break;
                    default: dps = true; break;
                }
            }

            int groups = (tank ? 1 : 0) + (healer ? 1 : 0) + (dps ? 1 : 0);
            if (groups > 1)
            {
                label = jobCount > 0 ? $"{jobCount} jobs" : "Any job";
                return RoleType.Free;
            }
            if (tank) { label = "Any tank"; return RoleType.Tank; }
            if (healer) { label = "Any healer"; return RoleType.Healer; }
            if (dps) { label = "Any DPS"; return RoleType.MeleeDPS; }

            label = "Any job";
            return RoleType.Free;
        }

        /// <summary>Duty name for the listed duty id, tolerating "no specific duty".</summary>
        private string ResolveListedDutyName(ushort dutyId)
        {
            if (dutyId == 0)
                return "None";

            var entry = dutyDataHelper.GetDutyEntry(dutyId);
            return entry?.Name ?? $"Unknown duty ({dutyId})";
        }

        /// <summary>
        /// Records the listing's duty so a party that later fills still knows which fight it
        /// filled for.
        ///
        /// A ZERO DOES NOT CLEAR THE MEMORY, and that is the whole repair.
        ///
        /// It used to, on the reading that a listing with no specific duty has nothing worth
        /// remembering. The flaw is that zero is not only what a duty-less listing looks like -
        /// it is also what a listing looks like while it is being torn down. Filling the last
        /// seat ends the recruitment, and on the tick where the game has zeroed
        /// StoredRecruitmentInfo but still reports us as recruiting, this was called with a duty
        /// id of nothing and wiped the fight the party had just spent an hour filling for. The
        /// readout went blank at 8/8, which is precisely the moment it matters, and the party had
        /// no way to get it back. LastViewedListing does the same thing to members - it is stale
        /// by design, so a zero from it says nothing about the party either.
        ///
        /// So a zero is now read as "this tells us nothing", not as "forget what you knew".
        /// Forgetting has one home, <see cref="RecruitedDutyStillApplies"/>, which asks whether
        /// the party still stands for that fight - filled and holding together, queued for it, or
        /// inside it - and that is a question about the party rather than about one frame's read
        /// of an agent that is mid-teardown.
        /// </summary>
        private void RememberRecruitedDuty(ushort dutyId, string name, int slots)
        {
            if (dutyId == 0)
                return;

            lastRecruitedDuty = (dutyId, name, slots);
        }

        /// <summary>How many people are in the party, counting yourself, or 0 when solo. Duty
        /// Support NPCs fill seats in the game's own list but are not company, and a party of you
        /// and three Trust NPCs has never filled a Party Finder listing.</summary>
        private int CurrentPartySize()
        {
            int players = 0;
            foreach (var m in GetOtherPartyMemberDetails())
            {
                if (!m.IsSupportNpc)
                    players++;
            }

            return players > 0 ? players + 1 : 0;
        }

        /// <summary>
        /// Whether the finished listing's duty is still this party's business.
        ///
        /// A listing's duty used to survive on any party at all, which meant a group that recruited
        /// for an Extreme in the evening was still being told who had cleared it an hour later,
        /// stood in a city, two people, nothing queued. The readout was true and completely beside
        /// the point, and there was no way to make it go away short of disbanding.
        ///
        /// So it now survives in exactly the three situations where the fight is still ahead of the
        /// party or under their feet: they filled the listing and are holding together, they are in
        /// the queue for it, or they are inside it. Anything else - and losing a member outside the
        /// duty is the ordinary case - drops it.
        ///
        /// Seat count comes from the listing rather than a fixed eight, because a light party
        /// listing is full at four and would otherwise never qualify.
        /// </summary>
        private bool RecruitedDutyStillApplies(int partySize)
        {
            if (!lastRecruitedDuty.HasValue)
                return false;

            uint rowId = lastRecruitedDuty.Value.RowId;

            // Standing in it. Compared rather than assumed: a party that went off to a roulette
            // together is in *a* duty, just not this one, and that is a stale reading either way.
            if (IsInDuty())
                return dutyDataHelper.GetDutyByTerritoryType(clientState.TerritoryType)?.RowId == rowId;

            // Queued for it. Same comparison, same reason - a roulette resolves to no row id at
            // all, so it can never accidentally match.
            if (IsInDutyQueue())
                return ResolveQueueContext().RowId == rowId;

            // Neither: only a party that actually filled the listing still stands for it.
            int seats = lastRecruitedDuty.Value.Slots;
            if (seats <= 0 || seats > 8)
                seats = 8;

            return partySize >= seats;
        }

        /// <summary>
        /// Where the player is standing, by name, or empty when the game has not said yet.
        ///
        /// Exposed for the Recruit tab's idle state, which has no listing and no party to describe
        /// and so describes where you are instead. Deliberately not part of the snapshot: the
        /// snapshot is about recruitment, and a zone name is true whether or not any of that is.
        /// </summary>
        public string CurrentZoneName()
        {
            try
            {
                return dutyDataHelper.GetPlaceName(clientState.TerritoryType);
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        /// <summary>
        /// The duty the player is standing in, by name, or empty when they are not in one.
        ///
        /// Asked of the duty sheet rather than the place-name sheet, which is the whole point: a
        /// territory and the fight staged in it are different things with different names, and the
        /// one worth saying out loud is the fight.
        /// </summary>
        public string CurrentDutyName()
        {
            try
            {
                if (!IsInDuty())
                    return string.Empty;

                return dutyDataHelper.GetDutyByTerritoryType(clientState.TerritoryType)?.Name
                    ?? string.Empty;
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        /// <summary>
        /// The duty to attribute an idle party's progress readout to.
        ///
        /// Inside a duty the current instance is authoritative and is resolved from the territory,
        /// which is the only source there is - there's no listing to read. In the Duty Finder queue
        /// it's whatever we queued for. Outside both, a party that just finished recruiting still
        /// points at the fight it filled for - for as long as it is still a party that filled it,
        /// which <see cref="RecruitedDutyStillApplies"/> decides just before this is called.
        /// Otherwise there is nothing to show, and an empty name is the panel's cue to say so.
        ///
        /// The queue is checked before the recruitment memory on purpose: queueing for a roulette
        /// straight after a Party Finder night was showing the *listing's* prog point - the old
        /// fight's percentage against a party queued for something else entirely.
        /// </summary>
        private (string Name, uint RowId) ResolveIdleContextDuty()
        {
            if (IsInDuty())
            {
                var duty = dutyDataHelper.GetDutyByTerritoryType(clientState.TerritoryType);
                return duty != null ? (duty.Name, duty.RowId) : (string.Empty, 0u);
            }

            if (IsInDutyQueue())
                return ResolveQueueContext();

            if (lastRecruitedDuty.HasValue)
                return (lastRecruitedDuty.Value.Name, lastRecruitedDuty.Value.RowId);

            return (string.Empty, 0u);
        }

        /// <summary>
        /// What we are queued for: a name to show, and a row id only when it is one identifiable
        /// fight. A roulette returns a name with no row id, which is what keeps the prog readout
        /// off a Frontline or Expert queue - there is no single fight to have a percentage in.
        ///
        /// Three sources, most authoritative first. Once the queue pops, the content is settled and
        /// the client says exactly what it is. Before that the queue info holds only the roulette
        /// id, so a specific duty has to be recovered from the name the game already shows in the
        /// ToDo list.
        /// </summary>
        private unsafe (string Name, uint RowId) ResolveQueueContext()
        {
            try
            {
                var ui = UIState.Instance();
                if (ui == null)
                    return (string.Empty, 0u);

                ref var queue = ref ui->ContentsFinder.QueueInfo;

                // Popped: the duty is decided, including which duty a roulette rolled.
                var popped = queue.PoppedQueueEntry;
                if (popped.Id != 0 && popped.ContentType == ContentsType.Regular)
                {
                    var entry = dutyDataHelper.GetDutyEntry(popped.Id);
                    if (entry != null)
                        return (entry.Name, entry.RowId);
                }

                // Still waiting on a roulette: name it, but never give it a row id.
                uint rouletteId = queue.QueuedContentRouletteId;
                if (rouletteId != 0)
                {
                    string roulette = dutyDataHelper.GetRouletteName(rouletteId);
                    return (roulette, 0u);
                }

                // Still waiting on a specific duty. Recovered by name, so it keeps its prog point.
                string queued = ReadQueuedDutyText();
                if (queued.Length > 0)
                {
                    var byName = dutyDataHelper.GetDutyByExactName(queued);
                    return byName != null ? (byName.Name, byName.RowId) : (queued, 0u);
                }

                return (string.Empty, 0u);
            }
            catch (Exception ex)
            {
                // Reading game memory for a label is never worth a crash.
                pluginLog.Debug($"[Queue] Context lookup failed: {ex.Message}");
                return (string.Empty, 0u);
            }
        }

        /// <summary>
        /// The first queued duty as the game itself renders it in the ToDo list. Empty when nothing
        /// is queued or the array isn't populated yet.
        /// </summary>
        private static unsafe string ReadQueuedDutyText()
        {
            var todo = ToDoListStringArray.Instance();
            if (todo == null)
                return string.Empty;

            var queued = todo->QueuedDuties;
            for (int i = 0; i < queued.Length; i++)
            {
                string text = queued[i].ToString();
                if (!string.IsNullOrWhiteSpace(text))
                    return text.Trim();
            }
            return string.Empty;
        }

        // ══════════════════════════════════════════════════════════
        //  TIME REMAINING
        // ══════════════════════════════════════════════════════════

        /// <summary>Notes when recruitment starts and stops so we can estimate the expiry for
        /// listings we never got an exact reading for.</summary>
        private void TrackRecruitmentWindow(bool recruiting)
        {
            if (!recruiting)
            {
                ResetTimeTracking();
                return;
            }

            recruitingSince ??= DateTime.UtcNow;
        }

        private void ResetTimeTracking()
        {
            recruitingSince = null;
            capturedTimeLeft = null;
        }

        /// <summary>
        /// Records the real time-left the game reports for our own listing. Called while a detail
        /// window for our listing is open - the only moment the value exists.
        /// </summary>
        public unsafe void CaptureListingTimeLeft()
        {
            var agent = AgentLookingForGroup.Instance();
            if (agent == null)
                return;

            var listing = agent->LastViewedListing;
            if (listing.LeaderContentId != playerState.ContentId)
                return; // someone else's listing - not ours to measure

            uint seconds = listing.TimeLeft;
            if (seconds == 0 || seconds > ListingLifetime.TotalSeconds)
                return; // implausible; leave the estimate alone

            // Re-stamped every time the reading moves, for the same reason
            // CapturedListingTimeLeft does it: the field ticks while the window is open, and a new
            // value aged against an old stamp subtracts the same minutes twice.
            var value = TimeSpan.FromSeconds(seconds);
            if (capturedTimeLeft == value)
                return;

            capturedTimeLeft = value;
            capturedAt = DateTime.UtcNow;
            pluginLog.Debug($"[Status] Captured exact listing time left: {seconds}s");
        }

        /// <summary>Time until the listing expires. Exact when the game gave us a reading we can
        /// age forward, otherwise counted down from when recruitment was first seen.</summary>
        private TimeSpan? ComputeTimeLeft(out bool exact)
        {
            if (capturedTimeLeft.HasValue)
            {
                var remaining = capturedTimeLeft.Value - (DateTime.UtcNow - capturedAt);
                if (remaining > TimeSpan.Zero)
                {
                    exact = true;
                    return remaining;
                }
            }

            if (recruitingSince.HasValue)
            {
                var remaining = ListingLifetime - (DateTime.UtcNow - recruitingSince.Value);
                exact = false;
                return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
            }

            exact = false;
            return null;
        }
    }
}
