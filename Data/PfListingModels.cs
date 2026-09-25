#if PFP_RATINGS
using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace PfPresets
{
    // ══════════════════════════════════════════════════════════════
    //  PF CROWDSOURCING
    //
    //  The wire format for "this is the party I am sitting in" and "who is in this listing".
    //
    //  A REPORT CARRIES THE WHOLE PARTY, NOT JUST THE SENDER. One person running this plugin is
    //  enough to describe an eight-person party, which is the only way the panel is ever useful -
    //  a report that named only its sender told a reader nothing they could not already see, and
    //  needed every seat to be running the plugin before it said anything at all.
    //
    //  What is still enforced: the SENDER has to be who they say they are. `Name`/`World` name the
    //  reporting character and the server checks them against the session, so a report can only
    //  come from somebody actually holding that character - and therefore, in practice, from
    //  somebody actually in that party.
    // ══════════════════════════════════════════════════════════════

    /// <summary>"The listing led by X currently holds these people, and I am one of them."</summary>
    internal sealed class PfReportRequest
    {
        [JsonProperty("leaderName")]
        public string LeaderName { get; set; } = string.Empty;

        [JsonProperty("leaderWorld")]
        public string LeaderWorld { get; set; } = string.Empty;

        /// <summary>The sender's own character. Checked against the session server-side.</summary>
        public string Name { get; set; } = string.Empty;

        public string World { get; set; } = string.Empty;

        /// <summary>ClassJob row id, or 0 when it could not be read.</summary>
        public int Job { get; set; }

        [JsonProperty("dutyId")]
        public int DutyId { get; set; }

        /// <summary>The world the sender is standing on, which decides which data centre's board
        /// the party belongs to. Not the leader's home world: a travelling party is listed where
        /// it is, not where it came from.</summary>
        [JsonProperty("currentWorld")]
        public string CurrentWorld { get; set; } = string.Empty;

        /// <summary>
        /// The party as this client sees it, the sender included.
        ///
        /// Sent whole every time rather than as a diff, and it REPLACES whatever this sender said
        /// last. That is what makes somebody leaving the party disappear from the panel: there is
        /// no remove message, only a shorter roster next heartbeat.
        /// </summary>
        [JsonProperty("members")]
        public List<PfMember> Members { get; set; } = new();
    }

    /// <summary>"Who has said they are in the listing led by X?"</summary>
    internal sealed class PfLookupRequest
    {
        [JsonProperty("leaderName")]
        public string LeaderName { get; set; } = string.Empty;

        [JsonProperty("leaderWorld")]
        public string LeaderWorld { get; set; } = string.Empty;
    }

    internal sealed class PfMember
    {
        public string Name { get; set; } = string.Empty;
        public string World { get; set; } = string.Empty;
        public int Job { get; set; }
    }

    internal sealed class PfLookupResponse
    {
        public bool Ok { get; set; }

        /// <summary>
        /// Everyone reported into this listing and not yet aged out, merged across reporters.
        ///
        /// Usually the whole party: it takes one member running this plugin to describe all of it.
        /// Where several members are running it, the server merges their rosters and the most
        /// recent report wins for anybody they disagree about - a job change lands rather than
        /// bouncing between two versions of the same character.
        /// </summary>
        public List<PfMember> Members { get; set; } = new();
    }

    internal sealed class PfReportResponse
    {
        public bool Ok { get; set; }
    }

    // ══════════════════════════════════════════════════════════════
    //  PF BOARD
    //
    //  The Party Finder tab: every recruitment listing a plugin user's Party Finder window has been
    //  handed by the game - the public board, copied - answered per data centre, because the
    //  game's board is per data centre. Recruitment notices only: no roster, no member names, no
    //  jobs beyond what the listing itself shows. A listing whose host runs a coordination-capable
    //  build also carries its coordination id, which is what the Join button hangs off.
    // ══════════════════════════════════════════════════════════════

    /// <summary>One listing as the Party Finder window received it.</summary>
    internal sealed class PfBoardUpload
    {
        /// <summary>The game's listing id. A ulong, so sent as a string - JSON numbers stop being
        /// exact at 2^53.</summary>
        public string Id { get; set; } = string.Empty;

        public string LeaderName { get; set; } = string.Empty;

        /// <summary>The leader's home world.</summary>
        public string LeaderWorld { get; set; } = string.Empty;

        /// <summary>The world the listing was put up on, which is what decides its data centre.</summary>
        public string CreatedWorld { get; set; } = string.Empty;

        public int DutyId { get; set; }
        public int DutyType { get; set; }
        public int Category { get; set; }
        public string Description { get; set; } = string.Empty;
        public int MinIlvl { get; set; }
        public bool Beginners { get; set; }
        public int Objective { get; set; }
        public int Conditions { get; set; }
        public int LootRules { get; set; }
        public int SearchArea { get; set; }
        public int DutySettings { get; set; }
        public int Parties { get; set; }
        public List<PfBoardSlot> Slots { get; set; } = new();
        public int SlotsFilled { get; set; }
        public int SlotsTotal { get; set; }
        public int SecondsRemaining { get; set; }
    }

    internal sealed class PfBoardSlot
    {
        /// <summary>Dalamud's JobFlags mask for the jobs this seat takes, as a decimal string.</summary>
        public string Accepting { get; set; } = "0";

        /// <summary>ClassJob row id of whoever holds the seat, or 0 while it is open.</summary>
        public int Job { get; set; }
    }

    internal sealed class PfBoardUploadRequest
    {
        public List<PfBoardUpload> Listings { get; set; } = new();
    }

    internal sealed class PfBoardUploadResponse
    {
        public bool Ok { get; set; }
        public int Stored { get; set; }
    }

    internal sealed class PfBoardRequest
    {
        /// <summary>A data centre by name. Takes precedence over <see cref="World"/>.</summary>
        public string? Dc { get; set; }

        /// <summary>Any world; the server answers for its data centre.</summary>
        public string? World { get; set; }

        /// <summary>Without this the server leaves every coordination id off the board.</summary>
        public int CoordProtocol { get; set; } = PfCoordinationProtocol.Version;

        /// <summary>The reader's home world, which decides what they can travel to - and so what
        /// goes on the Joinable tab.</summary>
        public string? HomeWorld { get; set; }
    }

    /// <summary>One person in a recruiting party, as the board shows them: shown only while the
    /// party is recruiting, and with nothing saying who runs the plugin.</summary>
    internal sealed class PfBoardMember
    {
        public string Name { get; set; } = string.Empty;
        public string World { get; set; } = string.Empty;
        public int Job { get; set; }
    }

    /// <summary>Watched listings, asked for by leader - a listing's id changes every time it is
    /// re-posted, and a watch follows the party.</summary>
    internal sealed class PfBoardWatchRequest
    {
        public List<PfBoardMember> Leaders { get; set; } = new();
        public int CoordProtocol { get; set; } = PfCoordinationProtocol.Version;
    }

    internal sealed class PfBoardWatchResponse
    {
        public bool Ok { get; set; }
        public List<PfBoardListing> Listings { get; set; } = new();
    }

    /// <summary>
    /// The recruiter's own listing. Sent the moment it goes up, whenever somebody joins or leaves,
    /// whenever it is re-posted, and with <see cref="Ended"/> when it comes down.
    /// </summary>
    internal sealed class PfBoardOwnRequest
    {
        public PfBoardUpload Listing { get; set; } = new();
        public List<PfMember> Members { get; set; } = new();
        public bool Ended { get; set; }
    }

    internal sealed class PfBoardListing
    {
        /// <summary>When this client last read this listing from the game itself - a Join press
        /// does - so the card can say it is current. Never sent or received.</summary>
        [JsonIgnore]
        public DateTime? CheckedAt { get; set; }

        /// <summary>The game's listing id.</summary>
        public string? Id { get; set; }

        /// <summary>Set when the host runs a build that can take applicants for this listing.</summary>
        public string? CoordinationId { get; set; }

        /// <summary>open or private, when coordinated.</summary>
        public string? CoordinationState { get; set; }

        /// <summary>handoff (omitted seats, private repost) or direct (join the public listing).</summary>
        public string? CoordinationMode { get; set; }

        /// <summary>The data centre and region the listing is on.</summary>
        public string Dc { get; set; } = string.Empty;
        public string? Region { get; set; }

        public int CoordinationFilled { get; set; }
        public int CoordinationTotal { get; set; }

        /// <summary>Applications waiting, whether or not they hold a seat yet.</summary>
        public int CoordinationApplicants { get; set; }

        /// <summary>Came off the game's board. Always true now the board is recruitment only.</summary>
        public bool OnBoard { get; set; }

        public string LeaderName { get; set; } = string.Empty;
        public string LeaderWorld { get; set; } = string.Empty;
        public string? CreatedWorld { get; set; }

        public int DutyId { get; set; }
        public int DutyType { get; set; }
        public int Category { get; set; }
        public string Description { get; set; } = string.Empty;
        public int MinIlvl { get; set; }
        public bool Beginners { get; set; }
        public int Objective { get; set; }
        public int Conditions { get; set; }
        public int LootRules { get; set; }
        public int SearchArea { get; set; }
        public int DutySettings { get; set; }
        public int Parties { get; set; }
        public List<PfBoardSlot> Slots { get; set; } = new();
        public int SlotsFilled { get; set; }
        public int SlotsTotal { get; set; }

        /// <summary>Time left on the listing, as of the server's answer.</summary>
        public int SecondsRemaining { get; set; }

        /// <summary>How long ago somebody last saw it.</summary>
        public int SeenAgo { get; set; }

        /// <summary>The party, where a plugin user is in it and it is recruiting. Empty otherwise.</summary>
        public List<PfBoardMember> Members { get; set; } = new();

        /// <summary>Applicants the host is holding a seat for. Their seats are omitted from the
        /// game's listing, so this is the only place they show.</summary>
        public List<PfBoardMember> Applicants { get; set; } = new();
    }

    internal sealed class PfBoardResponse
    {
        public bool Ok { get; set; }
        public string Dc { get; set; } = string.Empty;

        /// <summary>Seconds since anybody last read this data centre's Party Finder in full, as of
        /// the answer; null when nobody has since the server started.</summary>
        public int? LastFullReadAgo { get; set; }
        public int? LastFullReadCount { get; set; }
        public List<string> Datacentres { get; set; } = new();
        public List<PfBoardListing> Listings { get; set; } = new();

        /// <summary>Every listing taking applications that the reader can travel to, from every
        /// data centre. Only sent to a client speaking the current protocol.</summary>
        public List<PfBoardListing> Joinable { get; set; } = new();
    }

    /// <summary>
    /// The coordination protocol this build speaks. The server only registers a host, and only
    /// shows a board reader a coordination id, when both sides agree on it - which is what keeps an
    /// older build from ever drawing a Join button it cannot follow through on.
    /// </summary>
    internal static class PfCoordinationProtocol
    {
        public const int Version = 2;
    }

    internal sealed class PfCoordinationMember
    {
        public string Name { get; set; } = string.Empty;
        public string World { get; set; } = string.Empty;
        public int Job { get; set; }

        /// <summary>interested, joining, joined, left, removed (off the host's roster), or expired
        /// (promised a seat and never arrived).</summary>
        public string Status { get; set; } = "interested";

        /// <summary>Has been offered their seat (only once the listing is full and private).</summary>
        public bool AuthorisedPrivate { get; set; }

        /// <summary>The seat the host is holding for them, if any.</summary>
        public int? Slot { get; set; }

        /// <summary>Asked to be let in now ("Join party now"), having arrived on the host's data
        /// centre - ahead of the listing filling.</summary>
        public bool ReadyNow { get; set; }

        public DateTime UpdatedAt { get; set; }
    }

    /// <summary>What the server knows about this client's own place in a coordination.</summary>
    internal sealed class PfCoordinationMine
    {
        public string Status { get; set; } = string.Empty;
        public bool AuthorisedPrivate { get; set; }
        public int? Slot { get; set; }
    }

    internal sealed class PfCoordinationListing
    {
        public string CoordinationId { get; set; } = string.Empty;
        public string ListingId { get; set; } = string.Empty;
        public string HostName { get; set; } = string.Empty;
        public string HostWorld { get; set; } = string.Empty;
        public string CreatedWorld { get; set; } = string.Empty;
        public int DutyId { get; set; }

        /// <summary>open, private, filled or cancelled.</summary>
        public string State { get; set; } = "open";

        /// <summary>handoff or direct.</summary>
        public string Mode { get; set; } = "handoff";

        public int Filled { get; set; }
        public int Total { get; set; }
        public List<int> OmittedSlots { get; set; } = new();

        /// <summary>The party seat by seat, as the host's plugin sees it. Index = seat.</summary>
        public List<PfCoordinationSeatLayout> Layout { get; set; } = new();

        public long StateVersion { get; set; }
        public DateTime UpdatedAt { get; set; }

        /// <summary>Applicants waiting without a seat, and applicants holding one.</summary>
        public int Interested { get; set; }
        public int Promised { get; set; }

        /// <summary>Null for the host.</summary>
        public PfCoordinationMine? Mine { get; set; }

        /// <summary>The party. Only sent to the host, to members, and to applicants holding a seat -
        /// a queued applicant sees counts and nothing else.</summary>
        public List<PfCoordinationMember> Members { get; set; } = new();

        /// <summary>Only sent while private, and only to the host and to applicants holding a seat.</summary>
        public int? Password { get; set; }

        /// <summary>This client holds a seat and should go and take it.</summary>
        public bool Ready { get; set; }
    }

    internal sealed class PfCoordinationHostRequest
    {
        public int Protocol { get; set; } = PfCoordinationProtocol.Version;
        public string CoordinationId { get; set; } = string.Empty;
        public string ListingId { get; set; } = string.Empty;
        public string HostName { get; set; } = string.Empty;
        public string HostWorld { get; set; } = string.Empty;
        public string CreatedWorld { get; set; } = string.Empty;
        public int DutyId { get; set; }
        public string State { get; set; } = "open";
        public string Mode { get; set; } = "handoff";
        public int? Password { get; set; }
        public int Filled { get; set; }
        public int Total { get; set; }
        public List<int> OmittedSlots { get; set; } = new();
        public List<PfMember> Members { get; set; } = new();

        /// <summary>Applicants holding a seat, and which (an index into the listing as posted).</summary>
        public List<PfCoordinationSeat> Reservations { get; set; } = new();

        /// <summary>Applicants no seat fits.</summary>
        public List<PfCoordinationSeat> Rejections { get; set; } = new();

        /// <summary>Who is offered their seat now. Always sent: private no longer means full - a
        /// listing posted private is private from the start - so the host says who, rather than the
        /// server offering everybody the moment it goes private.</summary>
        public List<PfCoordinationSeat> Offer { get; set; } = new();

        /// <summary>Every seat, by its index in the listing as first posted.</summary>
        public List<PfCoordinationSeatLayout> Layout { get; set; } = new();
    }

    /// <summary>One seat of a coordinated listing, as the host's plugin sees it: jobs only.</summary>
    internal sealed class PfCoordinationSeatLayout
    {
        /// <summary>filled (a member's job), held (the applicant's job) or open.</summary>
        public string State { get; set; } = "open";
        public int Job { get; set; }

        /// <summary>For an open seat, the game's accepted-jobs bitfield; "0" for a seat the listing
        /// never offered.</summary>
        public string Mask { get; set; } = "0";
    }

    internal sealed class PfCoordinationSeat
    {
        public string Name { get; set; } = string.Empty;
        public string World { get; set; } = string.Empty;
        public int? Slot { get; set; }
    }

    internal sealed class PfCoordinationHostResponse
    {
        public bool Ok { get; set; }
        public PfCoordinationListing Listing { get; set; } = new();

        /// <summary>Everybody who has applied and is not in the party, in the order they applied.</summary>
        public List<PfCoordinationMember> Applicants { get; set; } = new();

        public int Interested { get; set; }
        public int Promised { get; set; }
    }

    internal sealed class PfCoordinationInterestRequest
    {
        public int Protocol { get; set; } = PfCoordinationProtocol.Version;
        public string? CoordinationId { get; set; }
        public string? ListingId { get; set; }
        public string Name { get; set; } = string.Empty;
        public string World { get; set; } = string.Empty;
        public int Job { get; set; }

        /// <summary>interested (apply), joining (on the way to a promised seat), or left (withdraw).</summary>
        public string Status { get; set; } = "interested";

        /// <summary>A private listing's password, when applying to one. Left out otherwise.</summary>
        public int? Password { get; set; }
    }

    internal sealed class PfCoordinationInterestResponse
    {
        public bool Ok { get; set; }
        public string CoordinationId { get; set; } = string.Empty;
        public string State { get; set; } = string.Empty;
        public long StateVersion { get; set; }
        public string Status { get; set; } = string.Empty;
    }

    internal sealed class PfCoordinationPollRequest
    {
        public string CoordinationId { get; set; } = string.Empty;
    }

    internal sealed class PfCoordinationPollResponse
    {
        public bool Ok { get; set; }
        public PfCoordinationListing Listing { get; set; } = new();
    }

    /// <summary>Held open by the server until the other side changes something.</summary>
    internal sealed class PfCoordinationWaitRequest
    {
        public string CoordinationId { get; set; } = string.Empty;

        /// <summary>host (woken by applicants) or applicant (woken by the host).</summary>
        public string As { get; set; } = "applicant";

        /// <summary>The version last seen, or -1 to just learn it.</summary>
        public int Since { get; set; } = -1;
    }

    internal sealed class PfCoordinationWaitResponse
    {
        public bool Ok { get; set; }
        public int Version { get; set; }
        public bool Changed { get; set; }
    }

    internal sealed class PfCoordinationCancelRequest
    {
        public string CoordinationId { get; set; } = string.Empty;
    }

    /// <summary>After a Party Finder read: every listing it was handed, the game's own count, and
    /// whether it reached the last page.</summary>
    internal sealed class PfBoardSweepRequest
    {
        public string World { get; set; } = string.Empty;
        public List<string> Seen { get; set; } = new();
        public int Total { get; set; }
        public bool Complete { get; set; }
    }

    internal sealed class PfBoardSweepResponse
    {
        public bool Ok { get; set; }
        public int Removed { get; set; }
    }
}
#endif
