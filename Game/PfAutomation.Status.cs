using System;
using System.Collections.Generic;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

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
            IsRecruiting || Activity != PfActivity.Idle || BlockedReason.Length > 0;
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
                return new RecruitmentSnapshot
                {
                    Activity = ClassifyIdleActivity(),
                    IsLeader = IsPartyLeader(),
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
                Blocked = canRecruit ? string.Empty : reason,
            };

            if (isLeader)
            {
                // Our own listing: StoredRecruitmentInfo is what we posted.
                var info = &agent->StoredRecruitmentInfo;
                int total = info->NumberOfSlotsInMainParty;
                if (total <= 0 || total > 8)
                    total = 8;

                return new RecruitmentSnapshot
                {
                    Activity = common.Activity,
                    IsRecruiting = common.IsRecruiting,
                    IsLeader = true,
                    DutyName = ResolveListedDutyName(info->SelectedDutyId),
                    DutyRowId = info->SelectedDutyId,
                    Comment = info->CommentString ?? string.Empty,
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
            // The only trustworthy source is a detail window we've actually viewed for this
            // party's listing - use it when it matches, and stay quiet about what we can't know.
            var viewed = agent->LastViewedListing;
            bool viewedIsOurParty = viewed.ListingId != 0
                                    && viewed.LeaderContentId != 0
                                    && viewed.LeaderContentId == GetPartyLeaderContentId();

            if (!viewedIsOurParty)
            {
                return new RecruitmentSnapshot
                {
                    Activity = common.Activity,
                    IsRecruiting = common.IsRecruiting,
                    IsLeader = false,
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

            return new RecruitmentSnapshot
            {
                Activity = common.Activity,
                IsRecruiting = common.IsRecruiting,
                IsLeader = false,
                LeaderName = viewed.LeaderString.ToString() ?? string.Empty,
                DutyName = ResolveListedDutyName(viewed.DutyId),
                DutyRowId = viewed.DutyId,
                Comment = viewed.CommentString.ToString() ?? string.Empty,
                Filled = filled,
                Open = BuildOpenSeatsFromMasks(viewedMasks, viewedTotal, viewed.SlotsFilled),
                SlotsTotal = viewedTotal,
                // The viewed listing carries a real countdown, so no estimating needed.
                TimeLeft = viewed.TimeLeft > 0 ? TimeSpan.FromSeconds(viewed.TimeLeft) : null,
                TimeLeftIsExact = viewed.TimeLeft > 0,
                BlockedReason = common.Blocked,
            };
        }

        /// <summary>
        /// True when the party leader is advertising on the Party Finder. Detected from their
        /// online status ("Recruiting Party Members", row 26) - the same signal the party list uses
        /// for its icon - because the UsingPartyFinder condition flag only belongs to the player who
        /// owns the listing, not to everyone who joined it.
        /// </summary>
        public unsafe bool IsPartyLeaderRecruiting()
        {
            ulong leaderId = GetPartyLeaderContentId();
            if (leaderId == 0)
                return false;

            // When we're the leader, our own state is authoritative and always readable - there's
            // nothing to infer. Falling through would reach the LastViewedListing fallback below,
            // which is stale by nature: it holds whatever listing was last opened in a detail
            // window, with a TimeLeft frozen from when it was read. Opening your own listing once
            // and then ending it left that check reporting you as recruiting indefinitely, which
            // is why "End Recruitment" kept appearing with no listing up.
            if (leaderId == playerState.ContentId)
                return IsRecruiting();

            // The leader has to be loaded for us to read their status; when they aren't, fall back
            // to a listing we've already fetched for this party.
            foreach (var obj in objectTable)
            {
                if (obj is not Dalamud.Game.ClientState.Objects.SubKinds.IPlayerCharacter player)
                    continue;

                if (player.OnlineStatus.RowId != OnlineStatusRecruiting)
                    continue;

                var character = (FFXIVClientStructs.FFXIV.Client.Game.Character.Character*)player.Address;
                if (character != null && character->ContentId == leaderId)
                    return true;
            }

            var agent = AgentLookingForGroup.Instance();
            if (agent != null)
            {
                var viewed = agent->LastViewedListing;
                if (viewed.ListingId != 0 && viewed.LeaderContentId == leaderId && viewed.TimeLeft > 0)
                    return true;
            }

            return false;
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

            capturedTimeLeft = TimeSpan.FromSeconds(seconds);
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
