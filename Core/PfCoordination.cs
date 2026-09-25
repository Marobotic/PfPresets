#if PFP_RATINGS
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace PfPresets
{
    /// <summary>
    /// Coordinated Party Finder: plugin users applying to each other's listings, and the host's
    /// plugin handing the party over through a private listing.
    ///
    /// THE HOST posts a listing from a preset with seats omitted, so it stays on the board without
    /// anybody walking into it. This class registers it with the server and sends a heartbeat
    /// carrying the party roster. Once the party plus the applicants cover every seat, it takes the
    /// public listing down and reposts it privately with a random four-digit password. If a seat
    /// later opens with nobody waiting for it - a joiner was kicked, or left - it goes back to the
    /// public form so the recruitment can be found again. When the party is full it prompts the
    /// host to queue.
    ///
    /// AN APPLICANT presses Join on the board, which registers interest and nothing else. When the
    /// server promises them a seat (the listing is private and they are next in line), the password
    /// arrives in a poll; the client travels to the host's data centre with /li if it has to, waits
    /// until it has arrived, and joins.
    ///
    /// ONE THREAD. <see cref="Tick"/> runs on the framework thread and is the only place session
    /// state changes. Network calls and long game flows run as tasks, and hand their results back
    /// through <see cref="onFramework"/> to be applied on the next tick. The UI draws on the same
    /// thread and so reads the sessions without locks.
    ///
    /// NOTHING IS PERSISTED. Both game clients may share one configuration file; a coordination id
    /// or password stored there would be picked up by the wrong client.
    /// </summary>
    internal sealed class PfCoordination : IDisposable
    {
        private static readonly TimeSpan ActiveBeat = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan IdleBeat = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan ActivePoll = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan PromisedPoll = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan IdlePoll = TimeSpan.FromMinutes(15);
        private static readonly TimeSpan InactiveAfter = TimeSpan.FromHours(1);
        private static readonly TimeSpan NotRecruitingGrace = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan TravelTimeout = TimeSpan.FromMinutes(15);
        private static readonly TimeSpan ManualPasswordWindow = TimeSpan.FromMinutes(2);
        private static readonly TimeSpan LeftPartyGrace = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan FilledLinger = TimeSpan.FromMinutes(30);
        private const int MaxJoinAttempts = 3;

        public enum HostPhase { Open, Switching, Private, Filled }

        /// <summary>
        /// Applying → Applied (queued) → Offered (the listing is full and a seat is held; waiting for
        /// the player to accept) → Promised (accepted; moving) → Travelling → Joining → Joined →
        /// Filled. Nothing moves the character before Accept.
        /// </summary>
        public enum ApplicantPhase { Applying, Applied, Offered, Promised, Travelling, Joining, Joined, Filled, Failed, Ended }

        /// <summary>An applicant holding a seat: the seat is an index into the listing as first posted.</summary>
        internal sealed record SeatHold(string Name, string World, int Job, int Slot);

        internal sealed class HostSession
        {
            public string CoordinationId { get; } = Guid.NewGuid().ToString();
            public string IdentityKey { get; init; } = string.Empty;

            /// <summary>The preset the listing was posted from, and its seats exactly as the game
            /// took them (zero = a seat the host omitted, never recruited for). Every repost is
            /// rebuilt from these two, so holding and releasing seats never drifts.</summary>
            public PfPresetData BasePreset { get; init; } = new();
            public ulong[] BaseMasks { get; init; } = Array.Empty<ulong>();

            public string ListingId { get; set; } = string.Empty;
            public uint DutyId { get; init; }
            public string CreatedWorld { get; init; } = string.Empty;

            /// <summary>Seats in the listing at all, including the host's own.</summary>
            public int Seats => BaseMasks.Count(m => m != 0);

            public HostPhase Phase { get; set; } = HostPhase.Open;
            public int Filled { get; set; }

            /// <summary>Who holds which seat, by name@world. Kept between heartbeats so a seat does
            /// not move under somebody who already holds it.</summary>
            public Dictionary<string, SeatHold> Holds { get; } = new();
            public List<PfCoordinationMember> Rejected { get; } = new();

            /// <summary>What is posted right now: private or not, and which seats are omitted to
            /// hold them. And what should be.</summary>
            public bool PostedPrivate { get; set; }
            public int[] PostedOmitted { get; set; } = Array.Empty<int>();
            public int? Password { get; set; }
            public bool WantPrivate { get; set; }

            /// <summary>The player posted the listing private, with their own password. It stays
            /// private for the whole coordination: applicants give that password to apply, and
            /// nothing is ever reposted public.</summary>
            public bool UserPrivate { get; init; }
            public int[] WantOmitted { get; set; } = Array.Empty<int>();

            /// <summary>The seat holders offered their seat now, by "name@world": all of them once
            /// the party is full, otherwise only those who asked to join now (or accepted and are
            /// on their way).</summary>
            public HashSet<string> Offered { get; } = new();
            public DateTime? ChangeWantedSince { get; set; }

            /// <summary>The party seat by seat, for the applicants' windows; and the roster it was
            /// worked out from, so a change in the party re-sends it at once.</summary>
            public List<PfCoordinationSeatLayout> Layout { get; set; } = new();
            public string RosterSignature { get; set; } = string.Empty;

            /// <summary>The insurance check: a few seconds after every post, whoever made it - but only
            /// once coordination has reposted the listing itself. The host's own listing, untouched,
            /// is not coordination's to "fix".</summary>
            public DateTime? VerifyAt { get; set; }
            public bool Reposted { get; set; }
            public int SeenRevision { get; set; }
            public int FixAttempts { get; set; }

            public PfCoordinationHostResponse? Last { get; set; }
            public DateTime LastBeatAt { get; set; } = DateTime.MinValue;
            public string LastError { get; set; } = string.Empty;
            public bool Registered => Last != null;

            public DateTime NextBeat { get; set; } = DateTime.MinValue;
            public bool BeatInFlight { get; set; }
            public DateTime? NotRecruitingSince { get; set; }
            public DateTime? UnderfilledSince { get; set; }
            public DateTime SwitchBlockedUntil { get; set; } = DateTime.MinValue;
            public DateTime FilledAt { get; set; }
            public bool QueuePromptOpen { get; set; }
        }

        internal sealed class ApplicantSession
        {
            public string CoordinationId { get; set; } = string.Empty;
            public string IdentityKey { get; init; } = string.Empty;
            public string HostName { get; init; } = string.Empty;
            public string HostWorld { get; init; } = string.Empty;
            public string CreatedWorld { get; set; } = string.Empty;
            public string DutyLabel { get; init; } = string.Empty;
            public uint DutyId { get; set; }
            public ApplicantPhase Phase { get; set; } = ApplicantPhase.Applying;
            public string Status { get; set; } = "Applying...";
            public PfCoordinationListing? Last { get; set; }
            public string ListingId { get; set; } = string.Empty;
            public int? Password { get; set; }

            /// <summary>A direct listing: no password, join the public listing.</summary>
            public bool Direct { get; set; }

            /// <summary>"Join party now" was pressed: travel over, tell the host we are here, and
            /// take the seat the moment it is opened - no second prompt.</summary>
            public bool JoinNow { get; set; }

            /// <summary>Arrived on the host's data centre and the host has been told.</summary>
            public bool ReadySent { get; set; }

            public DateTime NextPoll { get; set; } = DateTime.MinValue;
            public bool PollInFlight { get; set; }
            public DateTime NextJoinAttempt { get; set; } = DateTime.MinValue;
            public int JoinAttempts { get; set; }
            public bool JoinInFlight { get; set; }
            public DateTime? ManualDeadline { get; set; }
            public DateTime? AloneSince { get; set; }
            public DateTime FilledAt { get; set; }
            public bool OverlayHidden { get; set; }

            /// <summary>The job applied as. The seat held is one this job fits, so it is the job
            /// to arrive on.</summary>
            public int Job { get; init; }
        }

        private readonly PfApiClient api;
        private readonly Configuration config;
        private readonly IPluginLog log;
        private readonly IChatGui chatGui;
        private readonly PfAutomation automation;
        private readonly WorldHelper worlds;
        private readonly DutyDataHelper duties;
        private readonly Func<CharacterIdentity?> identity;
        private readonly Func<string> currentWorld;
        private readonly PfBoard board;
        private readonly InputActivity activity;
        private readonly ICommandManager commands;
        private readonly IFramework framework;
        private readonly IDalamudPluginInterface pluginInterface;

        /// <summary>Results of background work, applied on the next tick.</summary>
        private readonly ConcurrentQueue<Action> onFramework = new();

        private DateTime nextEligibilityCheck = DateTime.MinValue;
        private DateTime nextSessionTick = DateTime.MinValue;

        private volatile bool disposed;

        public HostSession? Host { get; private set; }
        public ApplicantSession? Applicant { get; private set; }

        /// <summary>Why the current recruitment is not taking applicants, for the diagnostics
        /// command and the tab. Empty while registered.</summary>
        public string HostStatus { get; private set; } = "You are not recruiting.";

        public bool HostOverlayHidden { get; set; }

        /// <summary>The own-listing reporter's status, for the diagnostics command.</summary>
        public Func<string>? OwnListingStatus { get; set; }

        public PfCoordination(
            PfApiClient api,
            Configuration config,
            IPluginLog log,
            IChatGui chatGui,
            PfAutomation automation,
            WorldHelper worlds,
            DutyDataHelper duties,
            Func<CharacterIdentity?> identity,
            Func<string> currentWorld,
            PfBoard board,
            InputActivity activity,
            ICommandManager commands,
            IFramework framework,
            IDalamudPluginInterface pluginInterface)
        {
            this.api = api;
            this.config = config;
            this.log = log;
            this.chatGui = chatGui;
            this.automation = automation;
            this.worlds = worlds;
            this.duties = duties;
            this.identity = identity;
            this.currentWorld = currentWorld;
            this.board = board;
            this.activity = activity;
            this.commands = commands;
            this.framework = framework;
            this.pluginInterface = pluginInterface;
        }

        public bool Enabled => config.CommunityEnabled && config.PfCoordinationEnabled;

        /// <summary>A name as the player has chosen to see names (the initials setting). For text
        /// on screen only - never for anything compared, stored or sent.</summary>
        private string Shown(string name) => PlayerNameFormat.Apply(name, config.PlayerNameStyle);

        private bool Inactive => activity.IdleFor >= InactiveAfter;

        // ══════════════════════════════════════════════════════════
        //  TICK
        // ══════════════════════════════════════════════════════════

        /// <summary>Every frame, on the framework thread.</summary>
        public void Tick()
        {
            if (disposed)
                return;

            while (onFramework.TryDequeue(out var action))
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    log.Error(ex, "[PF Coordination] A result could not be applied.");
                }
            }

            if (!Enabled)
            {
                if (Host != null)
                    EndHost(string.Empty, cancel: true);
                if (Applicant is { Phase: not ApplicantPhase.Ended })
                    Withdraw(silent: true);
                HostStatus = "Coordinated joining is turned off.";
                return;
            }

            if (config.PfBoardBackgroundPollingEnabled)
                board.EnsureFresh(Inactive);

            // Twice a second is plenty for everything below, and spares a party-list read a frame.
            var now = DateTime.UtcNow;
            if (now < nextSessionTick)
                return;
            nextSessionTick = now + TimeSpan.FromMilliseconds(500);

            var who = identity();

            HostTick(now, who);
            ApplicantTick(now, who);
        }

        /// <summary>
        /// Keeps one request open to the server that it answers the moment the other side of the
        /// coordination changes something, and pulls the next heartbeat or poll forward when it
        /// does. This is what makes a withdrawal reach the host in well under a second instead of
        /// at its next half-minute heartbeat.
        /// </summary>
        private void StartWaitLoop(string coordinationId, bool asHost, Func<bool> alive, Action onChange)
        {
            _ = Task.Run(async () =>
            {
                int since = -1;
                while (!disposed && alive())
                {
                    var result = await api.WaitPfCoordinationAsync(new PfCoordinationWaitRequest
                    {
                        CoordinationId = coordinationId,
                        As = asHost ? "host" : "applicant",
                        Since = since,
                    }).ConfigureAwait(false);

                    if (!result.IsOk)
                    {
                        await Task.Delay(5000).ConfigureAwait(false);
                        continue;
                    }

                    if (result.Value!.Changed)
                        onFramework.Enqueue(onChange);
                    since = result.Value.Version;
                }
            });
        }

        // ══════════════════════════════════════════════════════════
        //  HOST
        // ══════════════════════════════════════════════════════════

        private static readonly TimeSpan ChangeSettle = TimeSpan.FromSeconds(1);
        private static readonly TimeSpan VerifyAfter = TimeSpan.FromSeconds(5);
        private const int MaxFixAttempts = 2;

        private void HostTick(DateTime now, CharacterIdentity? who)
        {
            var h = Host;
            if (h == null)
            {
                if (now >= nextEligibilityCheck)
                {
                    nextEligibilityCheck = now + TimeSpan.FromSeconds(2);
                    TryStartHosting(who);
                }
                return;
            }

            if (who == null || who.Key != h.IdentityKey)
            {
                EndHost(string.Empty, cancel: true);
                return;
            }

            if (h.Phase == HostPhase.Switching)
                return;

            bool recruiting = automation.IsRecruiting() && automation.IsPartyLeader();
            h.Filled = Math.Min(PartySize(), h.Seats);

            if (h.Phase == HostPhase.Filled)
            {
                // Done: the party is complete. Linger for the queue prompt until the duty starts, the
                // party breaks up, or it has simply been a long time.
                if (automation.IsInDuty() || now - h.FilledAt > FilledLinger)
                {
                    EndHost(string.Empty, cancel: false);
                    return;
                }

                if (h.Filled < h.Seats)
                {
                    h.UnderfilledSince ??= now;
                    if (now - h.UnderfilledSince > LeftPartyGrace)
                    {
                        EndHost(string.Empty, cancel: false);
                        return;
                    }
                }
                else
                {
                    h.UnderfilledSince = null;
                }

                // Somebody left and the host put a new listing up: that is a new coordination.
                if (recruiting)
                    EndHost(string.Empty, cancel: true);
                return;
            }

            if (!recruiting)
            {
                // The game takes a listing down itself on the join that fills it.
                if (h.Filled >= h.Seats)
                {
                    EnterFilled(h, now);
                    return;
                }

                // It also takes it down when every seat it still offers is taken - the held seats
                // are omitted, so a public listing whose other seats all fill closes on its own.
                // That is the moment to go private, not a listing that has ended.
                if (h.Holds.Count > 0 && h.Filled + h.Holds.Count >= h.Seats)
                {
                    h.WantPrivate = true;
                    h.WantOmitted = Array.Empty<int>();
                    StartRepost(h, now);
                    return;
                }

                // A zone change hides the recruiting status for a moment; only a sustained absence
                // means the listing is really gone.
                h.NotRecruitingSince ??= now;
                if (now - h.NotRecruitingSince >= NotRecruitingGrace)
                    EndHost("[PF Analysis] Your listing is no longer up, so it has stopped taking applications.", cancel: true);
                return;
            }

            h.NotRecruitingSince = null;

            string? id = automation.GetOwnListingId();
            if (id != null && id != h.ListingId)
            {
                // Re-posted - by us, the Auto Refresher, or by hand. Same recruitment if the duty is.
                var state = automation.ReadRecruitmentState();
                if (state != null && state.DutyId != h.DutyId)
                {
                    EndHost("[PF Analysis] Your listing changed duty, so its applicants were released.", cancel: true);
                    return;
                }

                h.ListingId = id;
                h.NextBeat = now;
            }

            // Any post at all - ours, the refresher's, an Edit by hand - gets checked a few seconds
            // later against what should be up.
            if (automation.ListingRevision != h.SeenRevision)
            {
                h.SeenRevision = automation.ListingRevision;
                h.VerifyAt = now + VerifyAfter;
            }

            if (h.VerifyAt is { } verifyAt && now >= verifyAt)
            {
                h.VerifyAt = null;
                VerifyListing(h, now);
                if (h.Phase == HostPhase.Switching)
                    return;
            }

            // Apply a wanted change once it has held still for a moment, so a burst of applications
            // is one repost rather than several.
            if (Differs(h))
            {
                h.ChangeWantedSince ??= now;
                if (now - h.ChangeWantedSince >= ChangeSettle && now >= h.SwitchBlockedUntil)
                {
                    StartRepost(h, now);
                    return;
                }
            }
            else
            {
                h.ChangeWantedSince = null;
            }

            // Somebody joined, left or changed job: re-seat and tell everybody now, so the applicants'
            // windows follow the party as it happens rather than at the next heartbeat.
            var roster = ReadRoster(who);
            string signature = string.Join(",", roster.Select(m => $"{m.Name}@{m.World}:{m.Job}"));
            if (signature != h.RosterSignature)
            {
                h.RosterSignature = signature;
                if (h.Last != null)
                    Seat(h, roster, h.Last.Applicants);
                h.NextBeat = now;
            }

            if (!h.BeatInFlight && now >= h.NextBeat)
                SendBeat(h, now);
        }

        private static bool Differs(HostSession h)
            => h.WantPrivate != h.PostedPrivate || !h.WantOmitted.SequenceEqual(h.PostedOmitted);

        /// <summary>
        /// Registers the current listing when it is one this plugin can see through to the end: up,
        /// led by this character, and posted from a preset - every seat hold is a repost of it.
        /// </summary>
        private void TryStartHosting(CharacterIdentity? who)
        {
            if (who == null)
            {
                HostStatus = "You are not logged in.";
                return;
            }

            if (!automation.IsRecruiting() || !automation.IsPartyLeader())
            {
                HostStatus = "You are not recruiting.";
                return;
            }

            if (automation.IsCoordinationBusy)
                return;

            string? id = automation.GetOwnListingId();
            if (id == null)
            {
                HostStatus = "Waiting for your listing to appear.";
                return;
            }

            var state = automation.ReadRecruitmentState();
            if (state == null)
                return;

            // A LISTING POSTED PRIVATE TAKES APPLICATIONS TOO, behind its own password. It is hosted
            // private from the start: an applicant gives the password to apply (the server checks it
            // against the host's), and a seat offered comes with it, exactly as the handoff's own
            // private phase works. It is never reposted public.
            bool userPrivate = state.Password < 10000;

            // What to repost from: the preset it was posted with, or - for a listing posted from the
            // game's own Recruitment window - the listing's settings as its detail window showed them.
            bool Fits(PfPresetData? p, uint duty)
                => p != null && (userPrivate || !p.FormPrivateParty) && (p.DutyRowId == 0 || p.DutyRowId == duty);

            var preset = Fits(automation.ActivePreset, state.DutyId) ? automation.ActivePreset
                : Fits(automation.OwnListingAsPreset, state.DutyId) ? automation.OwnListingAsPreset
                : null;
            if (preset == null)
            {
                HostStatus = "Reading your listing's settings...";
                automation.ProbeOwnListingIfUnknown(force: true);
                return;
            }

            // The seats as the game holds the live listing - see OwnListingSlotMasks. Read from its
            // detail window; until that has been seen, read it once rather than guess.
            var live = automation.OwnListingSlotMasks;
            if (live == null)
            {
                HostStatus = "Reading your listing's seats...";
                automation.ProbeOwnListingIfUnknown(force: true);
                return;
            }

            // How many seats the party has, omitted ones included: the preset's own count, or for a
            // listing posted by hand, the duty's standard party.
            int partySize = Math.Clamp(preset.Slots.Count, 1, 8);
            var masks = live.Take(partySize).ToArray();
            if (masks.Skip(1).All(m => m == 0))
            {
                HostStatus = "Your listing has no open seats.";
                return;
            }

            // The preset exactly as posted, kept apart from the live one: every repost replaces
            // ActivePreset, and each is rebuilt from this and the seats as the game took them.
            var snapshot = preset.Duplicate();
            snapshot.Name = preset.Name;

            Host = new HostSession
            {
                IdentityKey = who.Key,
                BasePreset = snapshot,
                BaseMasks = masks,
                ListingId = id,
                DutyId = state.DutyId,
                CreatedWorld = currentWorld(),
                SeenRevision = automation.ListingRevision,
                UserPrivate = userPrivate,
                PostedPrivate = userPrivate,
                WantPrivate = userPrivate,
                Password = userPrivate ? state.Password : null,
            };

            // Nothing to watch until somebody applies.
            HostOverlayHidden = true;

            // Woken the moment an applicant applies, withdraws or accepts, rather than at the next
            // heartbeat.
            var session = Host;
            StartWaitLoop(session.CoordinationId, asHost: true,
                alive: () => Host == session,
                onChange: () => { if (Host == session) session.NextBeat = DateTime.UtcNow; });
            HostStatus = string.Empty;
            log.Information($"[PF Coordination] Registering listing {id} as coordination {Host.CoordinationId} "
                + $"({Host.Seats} seats).");
        }

        private int PartySize()
            => 1 + automation.GetOtherPartyMemberDetails().Count(m => !m.IsSupportNpc);

        /// <summary>The party as the heartbeat reports it, the host first. Read on the framework
        /// thread.</summary>
        private List<PfMember> ReadRoster(CharacterIdentity who)
        {
            var members = new List<PfMember>
            {
                new()
                {
                    Name = who.Name,
                    World = who.World,
                    Job = (int)automation.LocalJoinedJob(),
                },
            };

            // Jobs as joined: the seat a member holds is the job they took it on, whatever they
            // are wearing now.
            foreach (var member in automation.GetOtherPartyMemberDetails())
            {
                if (member.IsSupportNpc)
                    continue;

                string world = worlds.GetWorldName(member.HomeWorldId);
                if (world.Length == 0)
                    continue;

                members.Add(new PfMember { Name = member.Name, World = world, Job = (int)automation.JoinedJob(member.ContentId, member.JobId) });
            }

            return members;
        }

        private static bool SeatFits(ulong mask, int job)
        {
            int bit = JobMasks.GetGameJobBitIndex((uint)job);
            return bit > 0 && (mask & (1UL << bit)) != 0;
        }

        /// <summary>
        /// Who sits where. The party first - the host in the first seat, everybody else in the first
        /// free seat their job fits - then the applicants in the order they applied: a holder keeps
        /// their seat while it still fits them, anybody else takes the first free seat their job
        /// fits, and whoever no seat fits is turned away. Then what should be posted: private with
        /// every seat open once every seat is filled or held, else public with the held seats
        /// omitted so nobody else can walk into them.
        /// </summary>
        private void Seat(HostSession h, List<PfMember> roster, List<PfCoordinationMember> applicants)
        {
            var free = new HashSet<int>(Enumerable.Range(0, h.BaseMasks.Length).Where(i => h.BaseMasks[i] != 0));
            free.Remove(0);

            var filledBy = new Dictionary<int, int> { [0] = roster.Count > 0 ? roster[0].Job : 0 };
            foreach (var m in roster.Skip(1))
            {
                int seat = free.Where(i => SeatFits(h.BaseMasks[i], m.Job)).DefaultIfEmpty(-1).Min();
                if (seat < 0)
                    seat = free.DefaultIfEmpty(-1).Min();
                if (seat >= 0)
                {
                    free.Remove(seat);
                    filledBy[seat] = m.Job;
                }
            }

            var holds = new Dictionary<string, SeatHold>();
            h.Rejected.Clear();
            foreach (var a in applicants.Where(a => a.Status is "interested" or "joining"))
            {
                string key = $"{a.Name}@{a.World}".ToLowerInvariant();
                int seat = h.Holds.TryGetValue(key, out var held) && free.Contains(held.Slot)
                        && SeatFits(h.BaseMasks[held.Slot], a.Job)
                    ? held.Slot
                    : free.Where(i => SeatFits(h.BaseMasks[i], a.Job)).DefaultIfEmpty(-1).Min();

                if (seat < 0)
                {
                    h.Rejected.Add(a);
                    continue;
                }

                free.Remove(seat);
                holds[key] = new SeatHold(a.Name, a.World, a.Job, seat);
            }

            bool changed = holds.Count != h.Holds.Count || holds.Any(kv => !h.Holds.TryGetValue(kv.Key, out var o) || o != kv.Value);
            h.Holds.Clear();
            foreach (var kv in holds)
                h.Holds[kv.Key] = kv.Value;

            var heldBy = h.Holds.Values.ToDictionary(v => v.Slot, v => v.Job);
            var layout = Enumerable.Range(0, h.BaseMasks.Length).Select(i =>
                filledBy.TryGetValue(i, out int fj) ? new PfCoordinationSeatLayout { State = "filled", Job = fj }
                : heldBy.TryGetValue(i, out int hj) ? new PfCoordinationSeatLayout { State = "held", Job = hj }
                : new PfCoordinationSeatLayout { State = "open", Mask = h.BaseMasks[i].ToString() }).ToList();
            bool layoutChanged = !layout.Select(x => (x.State, x.Job, x.Mask)).SequenceEqual(h.Layout.Select(x => (x.State, x.Job, x.Mask)));
            h.Layout = layout;
            changed |= layoutChanged;

            // WHO IS LET IN NOW. Once every seat is filled or held, everybody holding one - the
            // listing is full. Before that, only an applicant who has arrived and asked to join now,
            // or one already on the way. Their seat opens and the listing goes private for them (it
            // keeps its own password if it was posted private); every other held seat stays omitted.
            bool full = h.Holds.Count > 0 && free.Count == 0;
            var letIn = h.Holds
                .Where(kv => full || applicants.Any(a => $"{a.Name}@{a.World}".ToLowerInvariant() == kv.Key
                    && (a.ReadyNow || a.Status == "joining")))
                .Select(kv => kv.Key)
                .ToHashSet();
            changed |= !letIn.SetEquals(h.Offered);
            h.Offered.Clear();
            h.Offered.UnionWith(letIn);

            // Private while anybody is being let in, or always for a listing posted private; public
            // again once they are in, so the rest can fill.
            h.WantPrivate = h.UserPrivate || h.Offered.Count > 0;
            h.WantOmitted = h.Holds.Where(kv => !h.Offered.Contains(kv.Key)).Select(kv => kv.Value.Slot).OrderBy(i => i).ToArray();

            // Tell the server straight away: holding a seat and turning somebody away are both news.
            if (changed || h.Rejected.Count > 0)
                h.NextBeat = DateTime.UtcNow;
        }

        private void SendBeat(HostSession h, DateTime now)
        {
            var who = identity();
            if (who == null)
                return;

            var request = new PfCoordinationHostRequest
            {
                CoordinationId = h.CoordinationId,
                ListingId = h.ListingId,
                HostName = who.Name,
                HostWorld = who.World,
                CreatedWorld = h.CreatedWorld,
                DutyId = (int)h.DutyId,
                State = h.Phase switch
                {
                    HostPhase.Filled => "filled",
                    _ when h.PostedPrivate => "private",
                    _ => "open",
                },
                Mode = "handoff",
                Password = h.PostedPrivate && h.Phase != HostPhase.Filled ? h.Password : null,
                Filled = h.Filled,
                Total = h.Seats,
                OmittedSlots = h.PostedOmitted.ToList(),
                Members = ReadRoster(who),
                Reservations = h.Holds.Values.Select(v => new PfCoordinationSeat { Name = v.Name, World = v.World, Slot = v.Slot }).ToList(),
                Rejections = h.Rejected.Select(r => new PfCoordinationSeat { Name = r.Name, World = r.World }).ToList(),
                Offer = h.Holds.Where(kv => h.Offered.Contains(kv.Key))
                    .Select(kv => new PfCoordinationSeat { Name = kv.Value.Name, World = kv.Value.World }).ToList(),
                Layout = h.Layout,
            };

            h.BeatInFlight = true;
            h.NextBeat = now + (Inactive ? IdleBeat : ActiveBeat);

            _ = Task.Run(async () =>
            {
                var result = await api.HostPfCoordinationAsync(request).ConfigureAwait(false);
                onFramework.Enqueue(() => OnBeat(h, request.State, request.Members, result));
            });
        }

        private void OnBeat(HostSession h, string sentState, List<PfMember> roster, ApiResult<PfCoordinationHostResponse> result)
        {
            h.BeatInFlight = false;
            if (Host != h)
                return;

            if (!result.IsOk)
            {
                h.LastError = result.Message.Length > 0 ? result.Message : result.Status.ToString();
                log.Debug($"[PF Coordination] Heartbeat refused: {result.Status} {result.Message}");

                // Somebody else owns the listing id or the coordination; nothing this client does
                // will change that.
                if (result.Status == ApiStatus.Cooldown)
                    EndHost($"[PF Analysis] Your listing could not take applications: {h.LastError}", cancel: false);
                return;
            }

            var response = result.Value!;
            h.Last = response;
            h.LastBeatAt = DateTime.UtcNow;
            h.LastError = string.Empty;

            if (sentState == "filled" || h.Phase is HostPhase.Switching or HostPhase.Filled)
                return;

            int before = h.Holds.Count;
            Seat(h, roster, response.Applicants);

            if (h.Holds.Count > before)
            {
                HostOverlayHidden = false;
                var fresh = h.Holds.Values.Last();
                chatGui.Print($"[PF Analysis] {Shown(fresh.Name)} applied as {JobName(fresh.Job)}. Holding a seat for them "
                    + $"({h.Filled + h.Holds.Count}/{h.Seats} filled or held).");
            }
            else if (h.Holds.Count < before)
            {
                chatGui.Print("[PF Analysis] An applicant withdrew. Their seat is going back on the listing.");
            }
        }

        private static string JobName(int job) => JobData.FindById((uint)job)?.Abbreviation ?? "their job";

        private bool CanSwitchNow(out string why)
        {
            why = string.Empty;
            if (automation.IsInDuty())
                why = "you are in a duty";
            else if (automation.IsInCombat())
                why = "you are in combat";
            else if (automation.IsCoordinationBusy || (automation.ShowChecklist && !automation.IsAutomationDone))
                why = "another listing change is running";
            return why.Length == 0;
        }

        /// <summary>
        /// The listing to post: the base preset with its seats exactly as the game first took them,
        /// the held ones omitted (public) or everything open behind a password (private).
        /// Auto-adjust is off - the seats are already the adjusted ones, and must not move.
        /// </summary>
        private static PfPresetData BuildListing(HostSession h, bool isPrivate, int[] omitted, int? password)
        {
            var preset = h.BasePreset.Duplicate();
            preset.Name = h.BasePreset.Name;
            preset.AutoAdjustRoles = false;
            preset.FormPrivateParty = isPrivate;
            preset.PrivatePartyPassword = isPrivate && password is { } p ? p.ToString() : string.Empty;
            preset.Slots = Enumerable.Range(0, h.BaseMasks.Length).Select(i =>
                h.BaseMasks[i] == 0 || omitted.Contains(i)
                    ? new RoleSlot { SlotIndex = i, Role = RoleType.Omit }
                    : new RoleSlot { SlotIndex = i, Role = RoleType.Free, AcceptedJobFlags = JobMasks.FromGameMask(h.BaseMasks[i]) })
                .ToList();
            return preset;
        }

        /// <summary>The seat masks that posting (private, omitted) should leave in the game, seat 0
        /// aside: the preset writer closes up omitted seats at the end.</summary>
        private static List<ulong> ExpectedMasks(HostSession h, int[] omitted)
            => Enumerable.Range(1, h.BaseMasks.Length - 1)
                .Where(i => h.BaseMasks[i] != 0 && !omitted.Contains(i))
                .Select(i => h.BaseMasks[i])
                .OrderBy(m => m)
                .ToList();

        private void StartRepost(HostSession h, DateTime now)
        {
            if (!CanSwitchNow(out string why))
            {
                log.Debug($"[PF Coordination] Repost waits: {why}.");
                return;
            }

            bool toPrivate = h.WantPrivate;
            int[] omitted = h.WantOmitted;
            int? password = toPrivate
                ? (h.PostedPrivate && h.Password != null ? h.Password : RandomNumberGenerator.GetInt32(1000, 10000))
                : null;

            var target = BuildListing(h, toPrivate, omitted, password);
            var fallback = BuildListing(h, h.PostedPrivate, h.PostedOmitted, h.Password);

            h.Phase = HostPhase.Switching;
            h.ChangeWantedSince = null;
            string what = toPrivate
                ? "Every seat is filled or held. Reposting your listing privately so the applicants can join..."
                : omitted.Length > 0
                    ? $"Reposting your listing with {omitted.Length} seat(s) held for applicants..."
                    : "Reposting your listing with every seat open again...";
            chatGui.Print($"[PF Analysis] {what}");

            _ = Task.Run(async () =>
            {
                bool takenDown = false;
                string? id = await automation.SwitchListingAsync(target, () => takenDown = true).ConfigureAwait(false);
                string? restored = null;
                if (id == null && takenDown)
                    restored = await automation.SwitchListingAsync(fallback).ConfigureAwait(false);

                onFramework.Enqueue(() => FinishRepost(h, toPrivate, omitted, password, id, takenDown, restored));
            });
        }

        private void FinishRepost(HostSession h, bool toPrivate, int[] omitted, int? password,
            string? id, bool takenDown, string? restored)
        {
            if (Host != h)
                return;

            var now = DateTime.UtcNow;
            h.Phase = HostPhase.Open;
            if (id != null)
            {
                h.Reposted = true;
                h.ListingId = id;
                h.PostedPrivate = toPrivate;
                h.PostedOmitted = omitted;
                h.Password = password;
                h.NextBeat = now;
                h.VerifyAt = now + VerifyAfter;
                h.SeenRevision = automation.ListingRevision;
                chatGui.Print(toPrivate
                    ? "[PF Analysis] Your listing is private now. Applicants are being asked to accept and will be moved to you."
                    : "[PF Analysis] Your listing is back up.");
                return;
            }

            h.SwitchBlockedUntil = now + TimeSpan.FromSeconds(30);

            if (!takenDown || restored != null)
            {
                if (restored != null)
                    h.ListingId = restored;
                h.NextBeat = now;
                h.VerifyAt = now + VerifyAfter;
                chatGui.Print("[PF Analysis] Your listing could not be reposted. It is unchanged; PF Analysis will try again shortly.");
                return;
            }

            EndHost("[PF Analysis] Your listing was taken down and could not be put back up. Post it again to keep recruiting.", cancel: true);
        }

        /// <summary>
        /// The insurance check. Reads the recruitment back from the game and compares it with what
        /// should be posted - still up, private or not, the right seats offered. Anything off is
        /// reposted, at most twice in a row before the host is told.
        /// </summary>
        private void VerifyListing(HostSession h, DateTime now)
        {
            // Nothing of ours to protect until coordination has changed the listing.
            if (!h.Reposted || automation.IsCoordinationBusy || !automation.IsRecruiting())
                return;

            var state = automation.ReadRecruitmentState();
            if (state == null)
                return;

            // What coordination controls, and nothing else: private or not, the password, and how many
            // seats are offered. The jobs a seat takes are not compared - they change on their own
            // as people join and as the locked-slot adjuster works, and "fixing" that is how a
            // listing's real seats were once replaced.
            bool isPrivate = state.Password < 10000;
            int offered = state.Masks.Take(state.Total).Skip(1).Count(m => m != 0);
            int expected = ExpectedMasks(h, h.PostedOmitted).Count;

            string? problem = isPrivate != h.PostedPrivate
                ? (h.PostedPrivate ? "it should be private and is not" : "it should be public and is private")
                : offered != expected
                    ? $"it offers {offered} seat(s) where {expected} should be open"
                    : h.PostedPrivate && h.Password is { } pw && state.Password != pw
                        ? "its password is not the one applicants were given"
                        : null;

            if (problem == null)
            {
                h.FixAttempts = 0;
                log.Information($"[PF Coordination] Listing verified: {(isPrivate ? "private" : "public")}, "
                    + $"{offered + 1} seat(s), {h.PostedOmitted.Length} held.");
                return;
            }

            log.Warning($"[PF Coordination] Listing check failed: {problem}.");
            if (h.FixAttempts >= MaxFixAttempts)
            {
                chatGui.Print($"[PF Analysis] Your listing is not set up as it should be ({problem}), and reposting did not fix it. "
                    + "Check it in the Party Finder.");
                return;
            }

            h.FixAttempts++;
            h.WantPrivate = h.PostedPrivate;
            h.WantOmitted = h.PostedOmitted;
            StartRepost(h, now);
        }

        private void EnterFilled(HostSession h, DateTime now)
        {
            h.Phase = HostPhase.Filled;
            h.FilledAt = now;
            h.QueuePromptOpen = true;
            HostOverlayHidden = false;
            SendBeat(h, now);

            chatGui.Print($"[PF Analysis] Your coordinated party is full. Queue for {DutyName(h.DutyId)} from the PF Coordination window when everyone is ready.");
        }

        /// <summary>The host's answer to the full-party prompt.</summary>
        public void QueueHostDuty()
        {
            var h = Host;
            if (h == null)
                return;

            h.QueuePromptOpen = false;
            automation.QueueForDuty(h.DutyId);
        }

        public void DismissQueuePrompt()
        {
            if (Host != null)
                Host.QueuePromptOpen = false;
        }

        private void EndHost(string message, bool cancel)
        {
            var h = Host;
            if (h == null)
                return;

            Host = null;
            if (cancel && h.Registered)
                _ = Task.Run(() => api.CancelPfCoordinationAsync(h.CoordinationId));
            if (message.Length > 0)
                chatGui.Print(message);
            log.Information($"[PF Coordination] Stopped hosting coordination {h.CoordinationId}.");
        }

        // ══════════════════════════════════════════════════════════
        //  APPLICANT
        // ══════════════════════════════════════════════════════════

        /// <summary>Whether the Join button on this card does anything, and what it says.</summary>
        public (bool Enabled, string Label, string Tooltip) JoinState(PfBoardListing listing)
        {
            if (string.IsNullOrWhiteSpace(listing.CoordinationId) || string.IsNullOrWhiteSpace(listing.Id))
                return (false, "Apply", "This listing is not taking applications.");

            if (!Enabled)
                return (false, "Apply", "Turn on coordinated joining to apply.");

            var who = identity();
            if (who == null)
                return (false, "Apply", "Log in to apply.");

            if (who.Key == new CharacterIdentity(listing.LeaderName, listing.LeaderWorld).Key)
                return (false, "Your listing", "This is your own listing. Applicants appear in the PF Coordination window.");

            string target = listing.CreatedWorld ?? string.Empty;
            if (target.Length > 0 && !worlds.CanTravel(who.World, target))
            {
                string from = WorldHelper.RegionLabel(worlds.GetFfLogsRegion(who.World));
                string to = WorldHelper.RegionLabel(worlds.GetFfLogsRegion(target));
                return (false, "Apply", $"{who.World} is in {from} and cannot travel to {listing.Dc} ({to}).");
            }

            var a = Applicant;
            bool mineAlready = a != null && a.Phase != ApplicantPhase.Ended && a.CoordinationId == listing.CoordinationId;
            if (!mineAlready && CheckEligible(listing) is { } ineligible)
                return (false, "Apply", ineligible);

            if (a != null && a.Phase != ApplicantPhase.Ended && a.CoordinationId == listing.CoordinationId)
            {
                string label = a.Phase switch
                {
                    ApplicantPhase.Applying => "Applying...",
                    ApplicantPhase.Applied => "Applied",
                    ApplicantPhase.Offered => "Seat offered",
                    ApplicantPhase.Promised => "Accepted",
                    ApplicantPhase.Travelling => "Travelling...",
                    ApplicantPhase.Joining => "Joining...",
                    ApplicantPhase.Joined or ApplicantPhase.Filled => "Joined",
                    _ => "Apply failed",
                };
                return (false, label, a.Status);
            }

            if (a != null && a.Phase is not (ApplicantPhase.Ended or ApplicantPhase.Failed or ApplicantPhase.Applied or ApplicantPhase.Applying))
                return (false, "Apply", "You are already on your way to another party.");

            if (Host != null)
                return (false, "Apply", "You are hosting a coordinated listing.");

            if (automation.GetOtherPartyMemberDetails().Count > 0)
                return (false, "Apply", "Leave your current party to apply.");

            string tip = "Apply remotely for a seat - from any data centre you can travel to. You stay free until the listing is full - every open seat has an "
                + "applicant. Then you are asked to accept, and once you do, PF Analysis moves you to the "
                + "host's data centre and joins the listing for you.";
            if (a != null && a.Phase is ApplicantPhase.Applied or ApplicantPhase.Applying)
                tip += $"\n\nThis replaces your application to {Shown(a.HostName)}'s listing.";
            return (true, "Apply", tip);
        }

        /// <summary>
        /// Why this character cannot apply to this listing, or null if it can: the job it is on
        /// must be a combat job with an open seat that takes it, it must be high enough level for
        /// the duty, and it must have the duty unlocked. A seat is held by job, so these are
        /// checked before applying rather than discovered after travelling.
        /// </summary>
        private string? CheckEligible(PfBoardListing listing)
        {
            var (jobId, level) = automation.GetLocalJobAndLevel();
            var job = JobData.FindById(jobId);
            if (job == null)
                return "Switch to a combat job to apply.";

            var duty = listing.DutyId > 0 ? duties.GetDutyEntry((uint)listing.DutyId) : null;
            if (duty != null)
            {
                if (duty.ClassJobLevelRequired > 0 && level > 0 && level < duty.ClassJobLevelRequired)
                    return $"{duty.Name} needs level {duty.ClassJobLevelRequired}; your {job.Abbreviation} is level {level}.";

                if (!duties.IsDutyUnlocked(duty))
                    return $"You have not unlocked {duty.Name}.";
            }

            bool seatFits = listing.Slots.Any(slot => slot.Job == 0 && board.JobsAccepted(slot.Accepting).Contains(jobId));
            if (!seatFits)
                return $"No open seat on this listing takes {job.Abbreviation}. Switch to a job one of its open seats accepts.";

            return null;
        }

        /// <summary>Applies to a listing. <paramref name="password"/> is a private listing's, which the
        /// server checks against the host's before taking the application.</summary>
        public void JoinFromBoard(PfBoardListing listing, string dutyLabel, int? password = null)
        {
            if (!JoinState(listing).Enabled)
                return;

            var who = identity();
            if (who == null)
                return;

            if (Applicant is { Phase: not ApplicantPhase.Ended } previous)
                SendWithdraw(previous);

            var a = new ApplicantSession
            {
                CoordinationId = listing.CoordinationId!,
                IdentityKey = who.Key,
                HostName = listing.LeaderName,
                HostWorld = listing.LeaderWorld,
                CreatedWorld = listing.CreatedWorld ?? string.Empty,
                DutyLabel = dutyLabel,
                DutyId = (uint)Math.Max(0, listing.DutyId),
                ListingId = listing.Id!,
                Job = (int)automation.GetLocalJobAndLevel().JobId,
                Direct = listing.CoordinationMode == "direct",
            };
            Applicant = a;

            var request = new PfCoordinationInterestRequest
            {
                CoordinationId = a.CoordinationId,
                ListingId = listing.Id,
                Name = who.Name,
                World = who.World,
                Job = (int)automation.GetLocalJobAndLevel().JobId,
                Status = "interested",
                Password = password,
            };

            _ = Task.Run(async () =>
            {
                var result = await api.ShowPfCoordinationInterestAsync(request).ConfigureAwait(false);
                onFramework.Enqueue(() =>
                {
                    if (Applicant != a)
                        return;

                    if (!result.IsOk)
                    {
                        EndApplicant(result.Message.Length > 0 ? result.Message : "The application could not be sent.", announce: true);
                        return;
                    }

                    a.CoordinationId = result.Value!.CoordinationId;
                    a.Phase = ApplicantPhase.Applied;

                    // Woken the moment the host changes anything - an offer above all.
                    StartWaitLoop(a.CoordinationId, asHost: false,
                        alive: () => Applicant == a && a.Phase != ApplicantPhase.Ended,
                        onChange: () => { if (Applicant == a) a.NextPoll = DateTime.UtcNow; });
                    a.Status = "Applied. You will be asked to travel once the listing is full.";
                    a.NextPoll = DateTime.UtcNow;
                    chatGui.Print($"[PF Analysis] Applied to {Shown(a.HostName)}'s {a.DutyLabel} listing. You'll be asked to travel once it is full.");
                    board.Refresh();
                });
            });
        }

        /// <summary>The overlay's Withdraw button.</summary>
        public void Withdraw(bool silent = false)
        {
            var a = Applicant;
            if (a == null || a.Phase == ApplicantPhase.Ended)
                return;

            SendWithdraw(a);
            EndApplicant(silent ? string.Empty : "You withdrew your application.", announce: !silent);
        }

        /// <summary>The prompt's Accept: only now is the character moved anywhere.</summary>
        public void AcceptOffer()
        {
            var a = Applicant;
            if (a == null || a.Phase != ApplicantPhase.Offered)
                return;

            a.Phase = ApplicantPhase.Promised;
            a.JoinAttempts = 0;
            a.NextJoinAttempt = DateTime.UtcNow;
            a.Status = "Accepted. Moving you to the party...";
        }

        /// <summary>Whether "Join party now" can be pressed, and why not.</summary>
        public (bool Enabled, string Tooltip) JoinNowState()
        {
            var a = Applicant;
            if (a == null || a.Phase != ApplicantPhase.Applied || a.JoinNow)
                return (false, a?.JoinNow == true ? "Already on the way in." : "Only while waiting in the queue.");
            if (!automation.CanJoinParty(out string blocked))
                return (false, blocked);
            return (true, "Go now rather than wait for the listing to fill: PF Analysis moves you to the "
                + "host's data centre, tells the host's plugin you are here, and joins you the moment it opens "
                + "your seat. A public listing goes private just long enough to let you in.");
        }

        /// <summary>
        /// "Join party now": leave the wait. Travel to the host's data centre, tell the host's plugin
        /// we have arrived, and take the seat as soon as it is opened - see the host's Seat, which
        /// lets a ready applicant in before the listing is full.
        /// </summary>
        public void JoinNow()
        {
            var a = Applicant;
            if (a == null || !JoinNowState().Enabled)
                return;

            a.JoinNow = true;
            a.JoinInFlight = true;
            string createdWorld = a.CreatedWorld;
            string destinationDc = worlds.GetDataCentre(createdWorld);
            var who = identity();

            _ = Task.Run(async () =>
            {
                string failure = string.Empty;
                try
                {
                    var here = await automation.ReadLocationAsync().ConfigureAwait(false);
                    bool sameDc = here.Ready && destinationDc.Length > 0
                        && string.Equals(here.DataCentre, destinationDc, StringComparison.OrdinalIgnoreCase);
                    if (!sameDc)
                    {
                        if (destinationDc.Length == 0)
                        {
                            failure = $"PF Analysis does not know which data centre {createdWorld} is on.";
                        }
                        else
                        {
                            onFramework.Enqueue(() =>
                            {
                                if (a.Phase == ApplicantPhase.Ended)
                                    return;
                                a.Phase = ApplicantPhase.Travelling;
                                a.Status = $"Travelling to {createdWorld} ({destinationDc})...";
                            });
                            failure = await TravelAsync(a, createdWorld, destinationDc).ConfigureAwait(false);
                        }
                    }

                    if (failure.Length == 0 && who != null)
                    {
                        var ready = await api.ShowPfCoordinationInterestAsync(new PfCoordinationInterestRequest
                        {
                            CoordinationId = a.CoordinationId,
                            Name = who.Name,
                            World = who.World,
                            Job = a.Job,
                            Status = "ready",
                        }).ConfigureAwait(false);
                        if (!ready.IsOk)
                            failure = ready.Message.Length > 0 ? ready.Message : "The host could not be told you are here.";
                    }
                }
                catch (Exception ex)
                {
                    log.Error(ex, "[PF Coordination] Join now failed.");
                    failure = "Something went wrong on the way. You are still in the queue.";
                }

                onFramework.Enqueue(() =>
                {
                    a.JoinInFlight = false;
                    if (Applicant != a || a.Phase == ApplicantPhase.Ended)
                        return;
                    if (failure.Length > 0)
                    {
                        a.JoinNow = false;
                        a.Phase = ApplicantPhase.Applied;
                        a.Status = failure;
                        chatGui.Print($"[PF Analysis] {failure}");
                        return;
                    }
                    a.ReadySent = true;
                    a.Phase = ApplicantPhase.Applied;
                    a.Status = $"Here and ready. Waiting for {Shown(a.HostName)}'s plugin to open your seat...";
                    a.NextPoll = DateTime.UtcNow;
                });
            });
        }

        /// <summary>The prompt's Decline: gives the seat up, and leaves the queue.</summary>
        public void DeclineOffer()
        {
            var a = Applicant;
            if (a == null || a.Phase != ApplicantPhase.Offered)
                return;

            SendWithdraw(a);
            EndApplicant("You declined the seat.", announce: true);
        }

        /// <summary>The overlay's Retry button, after the automatic attempts ran out.</summary>
        public void RetryJoin()
        {
            var a = Applicant;
            if (a == null || a.Phase != ApplicantPhase.Failed)
                return;

            a.JoinAttempts = 0;
            a.Phase = a.Password != null || a.Direct ? ApplicantPhase.Promised : ApplicantPhase.Applied;
            a.Status = "Trying again...";
            a.NextJoinAttempt = DateTime.UtcNow;
            a.NextPoll = DateTime.UtcNow;
        }

        /// <summary>Clears an ended session from the overlay.</summary>
        public void DismissApplicant()
        {
            if (Applicant is { Phase: ApplicantPhase.Ended })
                Applicant = null;
        }

        private void SendWithdraw(ApplicantSession a)
        {
            var who = identity();
            if (who == null || who.Key != a.IdentityKey)
                return;

            var request = new PfCoordinationInterestRequest
            {
                CoordinationId = a.CoordinationId,
                Name = who.Name,
                World = who.World,
                Status = "left",
            };
            _ = Task.Run(() => api.ShowPfCoordinationInterestAsync(request));
        }

        private void EndApplicant(string reason, bool announce)
        {
            var a = Applicant;
            if (a == null)
                return;

            a.Phase = ApplicantPhase.Ended;
            a.Password = null;
            a.ManualDeadline = null;
            a.Status = reason.Length > 0 ? reason : "Finished.";
            if (announce && reason.Length > 0)
                chatGui.Print($"[PF Analysis] {reason}");
        }

        private void ApplicantTick(DateTime now, CharacterIdentity? who)
        {
            var a = Applicant;
            if (a == null || a.Phase is ApplicantPhase.Ended or ApplicantPhase.Applying)
                return;

            // Another character logged in. Not mid-travel, where the character is briefly nobody.
            if (who != null && who.Key != a.IdentityKey && a.Phase is not (ApplicantPhase.Travelling or ApplicantPhase.Joining))
            {
                EndApplicant(string.Empty, announce: false);
                return;
            }

            if (!a.PollInFlight && now >= a.NextPoll && a.Phase != ApplicantPhase.Filled)
                Poll(a, now);

            switch (a.Phase)
            {
                case ApplicantPhase.Promised:
                    if (!a.JoinInFlight && now >= a.NextJoinAttempt)
                        StartJoin(a);
                    break;

                case ApplicantPhase.Joining when a.ManualDeadline is { } deadline:
                    // Waiting on the player to type the password the prompt would not take.
                    if (automation.GetOtherPartyMemberDetails().Count > 0)
                    {
                        a.ManualDeadline = null;
                        MarkJoined(a);
                    }
                    else if (now > deadline)
                    {
                        a.ManualDeadline = null;
                        Fail(a, "The private listing was not joined in time.");
                    }
                    break;

                case ApplicantPhase.Joined:
                    // Left, or removed before the host's roster has said so.
                    if (automation.GetOtherPartyMemberDetails().Count == 0 && !automation.IsInDuty())
                    {
                        a.AloneSince ??= now;
                        if (now - a.AloneSince > LeftPartyGrace)
                        {
                            SendWithdraw(a);
                            EndApplicant("You are no longer in the coordinated party.", announce: true);
                        }
                    }
                    else
                    {
                        a.AloneSince = null;
                    }
                    break;

                case ApplicantPhase.Filled:
                    if (automation.IsInDuty() || now - a.FilledAt > FilledLinger)
                        EndApplicant(string.Empty, announce: false);
                    break;
            }
        }

        private void Poll(ApplicantSession a, DateTime now)
        {
            a.PollInFlight = true;
            a.NextPoll = now + a.Phase switch
            {
                ApplicantPhase.Offered or ApplicantPhase.Promised or ApplicantPhase.Travelling or ApplicantPhase.Joining => PromisedPoll,
                ApplicantPhase.Applied when Inactive => IdlePoll,
                _ => ActivePoll,
            };

            string id = a.CoordinationId;
            _ = Task.Run(async () =>
            {
                var result = await api.PollPfCoordinationAsync(id).ConfigureAwait(false);
                onFramework.Enqueue(() => OnPoll(a, result));
            });
        }

        private void OnPoll(ApplicantSession a, ApiResult<PfCoordinationPollResponse> result)
        {
            a.PollInFlight = false;
            if (Applicant != a || a.Phase == ApplicantPhase.Ended)
                return;

            if (!result.IsOk)
            {
                // 404 and 403 are answers: the coordination is gone, or this character is not in it.
                if (result.Status is ApiStatus.BadRequest or ApiStatus.Refused)
                    EndApplicant(result.Message.Length > 0 ? result.Message : "That listing is no longer taking applications.", announce: true);
                return;
            }

            var l = result.Value!.Listing;
            a.Last = l;
            if (!string.IsNullOrWhiteSpace(l.CreatedWorld))
                a.CreatedWorld = l.CreatedWorld;
            if (l.DutyId > 0)
                a.DutyId = (uint)l.DutyId;
            a.Direct = l.Mode == "direct";

            string mine = l.Mine?.Status ?? string.Empty;

            if (l.State == "cancelled")
            {
                EndApplicant($"{Shown(a.HostName)} stopped recruiting.", announce: true);
                return;
            }

            if (mine == "removed")
            {
                EndApplicant("The host removed you from the party.", announce: true);
                return;
            }

            if (mine == "rejected")
            {
                EndApplicant($"No seat on {Shown(a.HostName)}'s listing fits your {JobData.FindById((uint)a.Job)?.Abbreviation ?? "job"} any more.", announce: true);
                return;
            }

            if (mine == "expired")
            {
                EndApplicant("Your seat was offered but you did not arrive in time. Apply again from the board.", announce: true);
                return;
            }

            if (mine == "left")
            {
                EndApplicant("Your application was withdrawn.", announce: false);
                return;
            }

            if (mine == "joined")
            {
                if (a.Phase is not (ApplicantPhase.Joined or ApplicantPhase.Filled))
                    MarkJoined(a);

                if (l.State == "filled" && a.Phase != ApplicantPhase.Filled)
                {
                    a.Phase = ApplicantPhase.Filled;
                    a.FilledAt = DateTime.UtcNow;
                    a.Status = $"The party is full. Waiting for {Shown(a.HostName)} to queue.";
                    a.OverlayHidden = false;
                    chatGui.Print($"[PF Analysis] {Shown(a.HostName)}'s party is full. Waiting for the host to queue.");
                }
                return;
            }

            if (l.State == "filled")
            {
                EndApplicant("The party filled before your seat came up.", announce: true);
                return;
            }

            if (l.Ready && (a.Direct || l.Password is >= 0 and <= 9999))
            {
                a.Password = a.Direct ? null : l.Password;
                a.ListingId = l.ListingId;
                if (a.Phase == ApplicantPhase.Applied && a.JoinNow)
                {
                    // Asked for with "Join party now" and already here: straight in, no second prompt.
                    a.Phase = ApplicantPhase.Promised;
                    a.NextJoinAttempt = DateTime.UtcNow;
                    a.Status = $"{Shown(a.HostName)} opened your seat. Joining...";
                }
                else if (a.Phase == ApplicantPhase.Applied)
                {
                    // Full: a seat is held for this player. Ask before moving them anywhere.
                    string dc = worlds.GetDataCentre(a.CreatedWorld);
                    a.Phase = ApplicantPhase.Offered;
                    a.Status = $"{Shown(a.HostName)}'s listing is full and a seat is yours. Accept to be moved"
                        + (dc.Length > 0 ? $" to {dc}" : string.Empty) + " and joined.";
                    a.OverlayHidden = false;
                    chatGui.Print($"[PF Analysis] {Shown(a.HostName)}'s {a.DutyLabel} listing is full and a seat is yours. "
                        + "Accept it in the PF Coordination window.");
                }
                return;
            }

            // No promise (any more): the host went back to public, or the seat went elsewhere.
            if (a.Phase is ApplicantPhase.Offered or ApplicantPhase.Promised or ApplicantPhase.Failed && !a.JoinInFlight)
            {
                if (a.Phase == ApplicantPhase.Offered)
                    chatGui.Print($"[PF Analysis] {Shown(a.HostName)}'s listing is no longer full; your seat offer was withdrawn. You are still in the queue.");
                a.Phase = ApplicantPhase.Applied;
                a.Password = null;
            }

            if (a.Phase == ApplicantPhase.Applied)
            {
                int needed = Math.Max(0, l.Total - l.Filled);
                a.Status = a.ReadySent
                    ? $"Here and ready. Waiting for {Shown(a.HostName)}'s plugin to open your seat..."
                    : $"Applied. {l.Interested + l.Promised} applicant(s) for {needed} open seat(s); "
                        + "you will be asked to travel once the listing is full, or press Join party now.";
            }
        }

        private void MarkJoined(ApplicantSession a)
        {
            a.Phase = ApplicantPhase.Joined;
            a.Password = null;
            a.AloneSince = null;
            a.Status = $"In {Shown(a.HostName)}'s party. Waiting for it to fill.";
            a.NextPoll = DateTime.UtcNow;
        }

        private void Fail(ApplicantSession a, string reason)
        {
            a.Phase = ApplicantPhase.Failed;
            a.Status = reason;
            a.OverlayHidden = false;
            string password = a.Password is { } p ? $" The private password is {p:D4} if you want to join by hand." : string.Empty;
            chatGui.Print($"[PF Analysis] {reason}{password}");
        }

        private void StartJoin(ApplicantSession a)
        {
            int? password = a.Password;
            if ((password == null && !a.Direct) || !ulong.TryParse(a.ListingId, out ulong listingId))
                return;

            // Not while the character is busy: the seat is held for twenty minutes, so waiting for
            // a pull to finish costs nothing.
            if (automation.IsInDuty() || automation.IsInCombat() || automation.IsInDutyQueue())
            {
                a.Status = "A seat is yours. You will be moved once you are out of duty, combat or queue.";
                a.NextJoinAttempt = DateTime.UtcNow + TimeSpan.FromSeconds(5);
                return;
            }

            // The seat was held for the job applied as; arriving on another may not fit it.
            uint current = automation.GetLocalJobAndLevel().JobId;
            if (a.Job > 0 && current != (uint)a.Job)
            {
                a.Status = $"Your seat is for {JobData.FindById((uint)a.Job)?.Abbreviation ?? "the job you applied as"}. "
                    + "Switch back to it and you will be moved in.";
                a.NextJoinAttempt = DateTime.UtcNow + TimeSpan.FromSeconds(3);
                return;
            }

            if (automation.GetOtherPartyMemberDetails().Count > 0)
            {
                a.Status = "A seat is yours. Leave your current party and you will be moved in.";
                a.NextJoinAttempt = DateTime.UtcNow + TimeSpan.FromSeconds(5);
                return;
            }

            a.JoinInFlight = true;
            string createdWorld = a.CreatedWorld;
            string destinationDc = worlds.GetDataCentre(createdWorld);
            var who = identity();

            _ = Task.Run(async () =>
            {
                var outcome = PfAutomation.JoinOutcome.Error;
                try
                {
                    if (who != null)
                    {
                        var joining = await api.ShowPfCoordinationInterestAsync(new PfCoordinationInterestRequest
                        {
                            CoordinationId = a.CoordinationId,
                            Name = who.Name,
                            World = who.World,
                            Status = "joining",
                        }).ConfigureAwait(false);

                        // The promise was withdrawn between the poll and now.
                        if (!joining.IsOk && joining.Status == ApiStatus.Cooldown)
                        {
                            onFramework.Enqueue(() =>
                            {
                                a.JoinInFlight = false;
                                if (a.Phase == ApplicantPhase.Ended)
                                    return;
                                a.Phase = ApplicantPhase.Applied;
                                a.Password = null;
                                a.NextPoll = DateTime.UtcNow;
                            });
                            return;
                        }
                    }

                    var here = await automation.ReadLocationAsync().ConfigureAwait(false);
                    bool sameDc = here.Ready && destinationDc.Length > 0
                        && string.Equals(here.DataCentre, destinationDc, StringComparison.OrdinalIgnoreCase);

                    if (!sameDc)
                    {
                        string failure;
                        if (destinationDc.Length == 0)
                        {
                            failure = $"PF Analysis does not know which data centre {createdWorld} is on.";
                        }
                        else
                        {
                            onFramework.Enqueue(() =>
                            {
                                if (a.Phase == ApplicantPhase.Ended)
                                    return;
                                a.Phase = ApplicantPhase.Travelling;
                                a.Status = $"Travelling to {createdWorld} ({destinationDc})...";
                            });
                            failure = await TravelAsync(a, createdWorld, destinationDc).ConfigureAwait(false);
                        }

                        if (failure.Length > 0)
                        {
                            onFramework.Enqueue(() =>
                            {
                                a.JoinInFlight = false;
                                if (Applicant == a && a.Phase != ApplicantPhase.Ended)
                                    Fail(a, failure);
                            });
                            return;
                        }
                    }

                    onFramework.Enqueue(() =>
                    {
                        if (a.Phase == ApplicantPhase.Ended)
                            return;
                        a.Phase = ApplicantPhase.Joining;
                        a.Status = "Joining the private listing...";
                    });
                    outcome = await automation.JoinListingAsync(listingId, password).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    log.Error(ex, "[PF Coordination] Join flow failed.");
                }

                onFramework.Enqueue(() => FinishJoin(a, outcome));
            });
        }

        /// <summary>
        /// /li to the host's world, then wait until the character is standing on the host's data
        /// centre, logged in, out of any loading screen, and Lifestream has finished. Returns an
        /// empty string on arrival, else why not.
        ///
        /// WITHOUT LIFESTREAM this still waits: the player can travel by hand, and the join carries
        /// on the moment they arrive.
        /// </summary>
        private async Task<string> TravelAsync(ApplicantSession a, string world, string dataCentre)
        {
            bool hasLi = await framework.RunOnFrameworkThread(() => commands.Commands.ContainsKey("/li"));
            if (hasLi)
            {
                if (!await automation.SendChatCommandAsync($"/li {world}").ConfigureAwait(false))
                    return "The /li travel command could not be sent.";
                chatGui.Print($"[PF Analysis] Travelling to {world} ({dataCentre}) for {Shown(a.HostName)}'s party...");
            }
            else
            {
                chatGui.Print($"[PF Analysis] Travel to {dataCentre} to take your seat. /li needs the Lifestream plugin; "
                    + "without it, travel by hand and PF Analysis will join as soon as you arrive.");
            }

            var deadline = DateTime.UtcNow + TravelTimeout;
            int settled = 0;
            while (DateTime.UtcNow < deadline && !disposed)
            {
                await Task.Delay(1000).ConfigureAwait(false);
                if (Applicant != a || a.Phase == ApplicantPhase.Ended)
                    return "Cancelled.";

                var here = await automation.ReadLocationAsync().ConfigureAwait(false);
                bool arrived = here.Ready
                    && string.Equals(here.DataCentre, dataCentre, StringComparison.OrdinalIgnoreCase)
                    && !await LifestreamBusyAsync().ConfigureAwait(false);

                // Three quiet seconds in a row: the arrival zone-in has really finished.
                settled = arrived ? settled + 1 : 0;
                if (settled >= 3)
                    return string.Empty;
            }

            return $"Could not reach {dataCentre} in time.";
        }

        private Task<bool> LifestreamBusyAsync()
            => framework.RunOnFrameworkThread(() =>
            {
                try
                {
                    return pluginInterface.GetIpcSubscriber<bool>("Lifestream.IsBusy").InvokeFunc();
                }
                catch
                {
                    // Not installed, or a Lifestream without the call.
                    return false;
                }
            });

        private void FinishJoin(ApplicantSession a, PfAutomation.JoinOutcome outcome)
        {
            a.JoinInFlight = false;
            if (Applicant != a || a.Phase == ApplicantPhase.Ended)
                return;

            var now = DateTime.UtcNow;
            switch (outcome)
            {
                case PfAutomation.JoinOutcome.Joined:
                    MarkJoined(a);
                    chatGui.Print($"[PF Analysis] Joined {Shown(a.HostName)}'s party.");
                    return;

                case PfAutomation.JoinOutcome.PasswordPrompt:
                    // The prompt could not be filled in automatically; the player can, and this
                    // client alone sees the password.
                    a.Phase = ApplicantPhase.Joining;
                    a.ManualDeadline = now + ManualPasswordWindow;
                    a.Status = $"Enter password {a.Password:D4} in the game's prompt to join.";
                    chatGui.Print($"[PF Analysis] Could not fill in the password automatically. Enter {a.Password:D4} to join {Shown(a.HostName)}'s party.");
                    return;

                case PfAutomation.JoinOutcome.Busy:
                case PfAutomation.JoinOutcome.NotNow:
                case PfAutomation.JoinOutcome.InParty:
                    a.Phase = ApplicantPhase.Promised;
                    a.NextJoinAttempt = now + TimeSpan.FromSeconds(10);
                    return;
            }

            a.JoinAttempts++;
            string why = outcome switch
            {
                PfAutomation.JoinOutcome.ListingMissing => "The private listing could not be opened.",
                PfAutomation.JoinOutcome.JoinUnavailable => "The listing's Join button was not available.",
                PfAutomation.JoinOutcome.NotAccepted => "The listing did not accept the join.",
                _ => "Joining failed.",
            };

            if (a.JoinAttempts >= MaxJoinAttempts)
            {
                Fail(a, why);
                return;
            }

            a.Phase = ApplicantPhase.Promised;
            a.Status = $"{why} Trying again...";
            a.NextJoinAttempt = now + TimeSpan.FromSeconds(20);
        }

        // ══════════════════════════════════════════════════════════
        //  DIAGNOSTICS
        // ══════════════════════════════════════════════════════════

        public string DutyName(uint dutyId)
        {
            string name = dutyId == 0 ? string.Empty : duties.GetDutyName(dutyId);
            return name.Length == 0 || name == "None" || name.StartsWith("Unknown", StringComparison.Ordinal)
                ? "the duty"
                : name;
        }

        /// <summary>What /pfa coord prints.</summary>
        public IEnumerable<string> Describe()
        {
            yield return $"Coordinated joining: {(Enabled ? "on" : "off")}, protocol {PfCoordinationProtocol.Version}.";
            if (OwnListingStatus != null)
                yield return $"Own listing: {OwnListingStatus()}";

            var h = Host;
            if (h == null)
            {
                yield return $"Host: not registered. {HostStatus}";
            }
            else
            {
                string beat = h.LastBeatAt == DateTime.MinValue
                    ? "no heartbeat answered yet"
                    : $"last heartbeat {(int)(DateTime.UtcNow - h.LastBeatAt).TotalSeconds}s ago";
                yield return $"Host: coordination {h.CoordinationId}, listing {h.ListingId}, {h.Phase.ToString().ToLowerInvariant()}, "
                    + $"{h.Filled}/{h.Seats} in party, {beat}.";
                yield return $"  Posted: {(h.PostedPrivate ? "private" : "public")}, "
                    + $"{h.PostedOmitted.Length} seat(s) omitted for applicants. Wanted: "
                    + $"{(h.WantPrivate ? "private" : "public")}, {h.WantOmitted.Length} omitted.";
                foreach (var hold in h.Holds.Values)
                    yield return $"  Seat {hold.Slot + 1} held for {Shown(hold.Name)}@{hold.World} ({JobName(hold.Job)}).";
                foreach (var r in h.Rejected)
                    yield return $"  No seat fits {Shown(r.Name)}@{r.World} ({JobName(r.Job)}).";
                if (h.LastError.Length > 0)
                    yield return $"  Last error: {h.LastError}";
            }

            var a = Applicant;
            yield return a == null
                ? "Applicant: not applied to anything."
                : $"Applicant: coordination {a.CoordinationId} ({Shown(a.HostName)}), {a.Phase.ToString().ToLowerInvariant()}. {a.Status}";
        }

        public void Dispose()
        {
            disposed = true;
            if (Host is { Registered: true } h)
                _ = api.CancelPfCoordinationAsync(h.CoordinationId);
            if (Applicant is { Phase: not ApplicantPhase.Ended } a)
                SendWithdraw(a);
        }
    }
}
#endif
