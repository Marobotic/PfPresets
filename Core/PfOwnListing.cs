#if PFP_RATINGS
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;

namespace PfPresets
{
    /// <summary>
    /// Puts the recruiter's own listing on the board, with its party.
    ///
    /// WHY IT EXISTS. The board used to know a listing only once some plugin user's Party Finder
    /// window had been handed it, so a recruiter whose data centre nobody else was browsing never
    /// appeared - not even to their own second client. The recruiter's plugin knows its listing
    /// better than anybody, so it says so itself.
    ///
    /// WHEN IT SENDS, and only then: the moment the listing goes up (part of applying a preset, or a
    /// post by hand), whenever somebody joins or leaves, whenever the listing is re-posted - by the
    /// Auto Refresher or by Edit then Recruit - and once more, as a withdrawal, when it comes down.
    /// No timer: nothing changes between those moments, and the board keeps a listing until its
    /// own clock or its withdrawal says it is gone.
    ///
    /// Only while this character leads the party and is recruiting, and only for somebody who
    /// shares their Party Finder data. Framework thread, twice a second.
    /// </summary>
    internal sealed class PfOwnListing
    {
        private static readonly TimeSpan TickEvery = TimeSpan.FromMilliseconds(500);
        private static readonly TimeSpan EndedAfter = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan RetryAfter = TimeSpan.FromSeconds(30);

        private readonly PfApiClient api;
        private readonly IPluginLog log;
        private readonly PfAutomation automation;
        private readonly WorldHelper worlds;
        private readonly PfBoard board;
        private readonly Func<CharacterIdentity?> identity;
        private readonly Func<string> currentWorld;
        private readonly ConcurrentQueue<Action> onFramework = new();

        private DateTime nextTick = DateTime.MinValue;
        private DateTime nextRetry = DateTime.MinValue;
        private DateTime? notRecruitingSince;
        private bool inFlight;

        /// <summary>The listing last reported, for the withdrawal; null when nothing is up.</summary>
        private PfBoardUpload? reported;
        private string reportedSignature = string.Empty;

        /// <summary>What the reporter is doing, for /pfa coord. Logged when it changes.</summary>
        public string Status { get; private set; } = "Not recruiting.";

        private void SetStatus(string status)
        {
            if (status == Status)
                return;
            Status = status;
            log.Information($"[PF Board] Own listing: {status}");
        }

        public PfOwnListing(PfApiClient api, IPluginLog log, PfAutomation automation, WorldHelper worlds,
            PfBoard board, Func<CharacterIdentity?> identity, Func<string> currentWorld)
        {
            this.api = api;
            this.log = log;
            this.automation = automation;
            this.worlds = worlds;
            this.board = board;
            this.identity = identity;
            this.currentWorld = currentWorld;
        }

        public void Tick()
        {
            while (onFramework.TryDequeue(out var action))
                action();

            var now = DateTime.UtcNow;
            if (now < nextTick)
                return;
            nextTick = now + TickEvery;

            // Runs whether or not anything is shared, so the listing's clock is known either way.
            automation.TrackOwnListing();

            if (inFlight)
                return;

            var who = identity();
            bool recruiting = who != null && automation.IsRecruiting() && automation.IsPartyLeader();

            if (!recruiting || !board.Uploading)
            {
                if (reported == null)
                {
                    SetStatus(!board.Uploading ? "Not shared: Party Finder sharing is off."
                        : who == null ? "Not recruiting (not logged in)."
                        : $"Not recruiting ({automation.DescribeRecruitingState()}).");
                    return;
                }

                // A zone change hides the recruiting status for a moment; only a sustained absence
                // means the listing is really gone. Sharing turned off withdraws at once.
                notRecruitingSince ??= now;
                if (board.Uploading && now - notRecruitingSince < EndedAfter)
                    return;

                Send(new PfBoardOwnRequest { Listing = reported, Ended = true }, string.Empty, ended: true);
                return;
            }

            notRecruitingSince = null;
            if (now < nextRetry)
                return;

            var built = Build(who!);
            if (built == null)
            {
                // Usually the listing id, which the game only hands over in the listing's own
                // detail window. Read it once, the way the Auto Refresher reads the clock.
                SetStatus("Waiting to learn the listing's id.");
                automation.ProbeOwnListingIfUnknown();
                return;
            }

            var (request, signature) = built.Value;

            // A different listing replaces the old one; the server drops the old row itself.
            if (signature == reportedSignature)
                return;

            Send(request, signature, ended: false);
        }

        /// <summary>The listing as the board stores it, and a signature of everything whose change
        /// is worth a re-send: the listing, its revision (every post), and who is in the party.</summary>
        private (PfBoardOwnRequest Request, string Signature)? Build(CharacterIdentity who)
        {
            var own = automation.ReadOwnListing();
            if (own == null)
                return null;

            string created = currentWorld();
            if (created.Length == 0)
                return null;

            var members = new List<PfMember>
            {
                new() { Name = who.Name, World = who.World, Job = (int)automation.LocalJoinedJob() },
            };
            foreach (var m in automation.GetOtherPartyMemberDetails())
            {
                if (m.IsSupportNpc)
                    continue;
                string world = worlds.GetWorldName(m.HomeWorldId);
                if (world.Length > 0)
                    members.Add(new PfMember { Name = m.Name, World = world, Job = (int)automation.JoinedJob(m.ContentId, m.JobId) });
            }

            // The seats the listing actually offers. An omitted seat is not a seat on the public
            // listing - the game shows the party without it - so it is left out here too.
            var seats = own.GameMasks.Take(own.Total).Where(mask => mask != 0).ToList();
            int total = Math.Max(seats.Count, members.Count);

            // Everybody already in sits in the first seat their job fits, the way the game fills them.
            var slots = seats.Select(mask => new PfBoardSlot { Accepting = board.JobFlagsFromGameMask(mask).ToString() }).ToList();
            var taken = new bool[slots.Count];
            foreach (var m in members)
            {
                int bit = JobMasks.GetGameJobBitIndex((uint)m.Job);
                int seat = -1;
                for (int i = 0; i < slots.Count && seat < 0; i++)
                {
                    if (!taken[i] && bit > 0 && (seats[i] & (1UL << bit)) != 0)
                        seat = i;
                }
                for (int i = 0; i < slots.Count && seat < 0; i++)
                {
                    if (!taken[i])
                        seat = i;
                }
                if (seat < 0)
                    continue;
                taken[seat] = true;
                slots[seat].Job = m.Job;
            }

            int seconds = (int)Math.Clamp(automation.OwnListingTimeLeft()?.TotalSeconds ?? 3600, 1, 3600);

            var listing = new PfBoardUpload
            {
                Id = own.Id,
                LeaderName = who.Name,
                LeaderWorld = who.World,
                CreatedWorld = created,
                DutyId = (int)own.DutyId,
                DutyType = own.DutyType,
                Category = own.Category,
                Description = own.Comment.Length > 400 ? own.Comment[..400] : own.Comment,
                Beginners = own.Beginners,
                Objective = own.Objective,
                LootRules = own.LootRule,
                Conditions = own.Conditions,
                DutySettings = own.DutySettings,
                MinIlvl = own.MinIlvl,
                // The same flags a browser's read carries: posted to the data centre, and private
                // when it has a password - which is what puts the lock on it and has Join and Apply
                // ask for the password. Left at zero, a private listing reported by its own recruiter
                // read as open to anybody.
                SearchArea = 1 | (own.Private ? 2 : 0),
                // An alliance says so, even though this report only holds its first party - so the
                // board draws it as an alliance, and knows not to shrink the seats it already has.
                Parties = automation.PartiesForDuty(own.DutyId),
                Slots = slots,
                SlotsFilled = Math.Min(members.Count, total),
                SlotsTotal = Math.Max(1, total),
                SecondsRemaining = seconds,
            };

            string signature = string.Join("|",
                own.Id, automation.ListingRevision, own.DutyId, own.Comment, created, own.Private,
                string.Join(",", seats),
                string.Join(",", members.Select(m => $"{m.Name}@{m.World}:{m.Job}")));

            return (new PfBoardOwnRequest { Listing = listing, Members = members }, signature);
        }

        private void Send(PfBoardOwnRequest request, string signature, bool ended)
        {
            inFlight = true;
            _ = Task.Run(async () =>
            {
                var result = await api.ReportOwnPfListingAsync(request).ConfigureAwait(false);
                onFramework.Enqueue(() =>
                {
                    inFlight = false;
                    if (!result.IsOk)
                    {
                        // Never remembered as sent: an older server, an outage or a refusal all get
                        // another go, so a deploy or a fix is picked up without waiting for the
                        // listing to change.
                        SetStatus($"Report {(ended ? "withdrawal " : "")}refused ({result.Status}{(result.Message.Length > 0 ? ": " + result.Message : "")}); retrying.");
                        nextRetry = DateTime.UtcNow + RetryAfter;
                        if (ended)
                            reported = null;
                        return;
                    }

                    SetStatus(ended ? "Withdrawn from the board." : $"On the board (listing {request.Listing.Id}).");

                    if (ended)
                    {
                        reported = null;
                        reportedSignature = string.Empty;
                        notRecruitingSince = null;
                    }
                    else
                    {
                        reported = request.Listing;
                        reportedSignature = signature;
                    }

                    board.Refresh();
                });
            });
        }

        /// <summary>On unload: take the listing off the board rather than leave it describing a party
        /// nobody is reporting on any more. Best effort.</summary>
        public void Withdraw()
        {
            if (reported != null)
                _ = api.ReportOwnPfListingAsync(new PfBoardOwnRequest { Listing = reported, Ended = true });
        }
    }
}
#endif
