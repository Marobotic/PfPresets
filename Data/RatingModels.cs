#if PFP_RATINGS
using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace PfPresets
{
    // ══════════════════════════════════════════════════════════════
    //  IDENTITY
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// A character as the rating system addresses them: name plus home world. This is the only
    /// form of identity that ever leaves the client - the server turns it into a keyed hash and
    /// stores that, so the database holds no character names at all.
    /// </summary>
    public sealed class CharacterIdentity : IEquatable<CharacterIdentity>
    {
        public string Name { get; set; } = string.Empty;
        public string World { get; set; } = string.Empty;

        public CharacterIdentity() { }

        public CharacterIdentity(string name, string world)
        {
            Name = name ?? string.Empty;
            World = world ?? string.Empty;
        }

        /// <summary>Whether this looks like a real character reference. Guards against the empty
        /// names the game briefly reports during zone transitions.</summary>
        [JsonIgnore]
        public bool IsValid => !string.IsNullOrWhiteSpace(Name) && !string.IsNullOrWhiteSpace(World);

        /// <summary>The canonical "name@world" form, lowercased. Used as the client-side cache key
        /// and normalised exactly the way the server normalises before hashing, so the two agree
        /// on what counts as the same character.</summary>
        [JsonIgnore]
        public string Key => $"{Name.Trim().ToLowerInvariant()}@{World.Trim().ToLowerInvariant()}";

        /// <summary>How the character is shown in the UI.</summary>
        public override string ToString() => $"{Name}@{World}";

        public bool Equals(CharacterIdentity? other) => other != null && Key == other.Key;
        public override bool Equals(object? obj) => Equals(obj as CharacterIdentity);
        public override int GetHashCode() => Key.GetHashCode(StringComparison.Ordinal);
    }

    /// <summary>
    /// The voter's relationship to the person being rated, which decides the collusion discount.
    /// Asserted by the client because the server cannot see anyone's friend list; the server
    /// still applies the weight itself rather than trusting a number off the wire.
    /// </summary>
    public enum SocialLink
    {
        None = 0,
        FreeCompany = 1,
        Friend = 2,
    }

    // ══════════════════════════════════════════════════════════════
    //  REQUESTS
    // ══════════════════════════════════════════════════════════════

    internal sealed class SessionRequest
    {
        public string InstallId { get; set; } = string.Empty;
        public CharacterIdentity Character { get; set; } = new();
        public string PluginVersion { get; set; } = string.Empty;
    }

    internal sealed class SessionResponse
    {
        public string Token { get; set; } = string.Empty;

        /// <summary>Token lifetime in seconds. The client refreshes early rather than waiting for
        /// a 401, so a submit never fails on an expiry it could have seen coming.</summary>
        public int ExpiresIn { get; set; }
    }

    internal sealed class LookupRequest
    {
        public string Name { get; set; } = string.Empty;
        public string World { get; set; } = string.Empty;
    }

    internal sealed class BatchLookupRequest
    {
        public List<LookupRequest> Players { get; set; } = new();
    }

    // ══════════════════════════════════════════════════════════════
    //  REPORTS
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// Why a player is being reported. Reports go to the plugin author, not to Square Enix - the
    /// UI says so plainly, because a report that quietly goes nowhere official is worse than no
    /// report button at all.
    /// </summary>
    public enum ReportReason
    {
        Harassment = 0,
        RatingAbuse = 1,
        Griefing = 2,
        Impersonation = 3,
        Other = 4,
    }

    public static class ReportReasons
    {
        public static readonly (ReportReason Reason, string Label)[] All =
        {
            (ReportReason.Harassment, "Harassment"),
            (ReportReason.Griefing, "Griefing"),
            (ReportReason.Impersonation, "Impersonation"),
            (ReportReason.Other, "Other"),
        };

        public static string Label(ReportReason reason)
        {
            foreach (var (r, label) in All)
            {
                if (r == reason)
                    return label;
            }
            return "Other";
        }
    }

    internal sealed class SubmitReportRequest
    {
        public CharacterIdentity Target { get; set; } = new();

        /// <summary>
        /// Who is reporting. Sent so the notification names them, and stored nowhere - the
        /// database keeps the same one-way hash it always did.
        ///
        /// Named by default, because a report the reader can follow up on is worth more than one
        /// they can't. Null when the reporter ticked "Send anonymously", and null here means the
        /// property is omitted from the request body entirely rather than sent empty - see
        /// <c>NullValueHandling.Ignore</c> in PfApiClient's write settings. The server's
        /// abuse detection is unaffected either way: it keys on the session's voter hash, not
        /// on this.
        /// </summary>
        public CharacterIdentity? Reporter { get; set; }

        public ReportReason Reason { get; set; }

        /// <summary>Free text from the reporter. Length-capped client-side and again on the
        /// server; it is shown to a human, so it is never interpreted as anything but text.</summary>
        public string Note { get; set; } = string.Empty;

        public int DutyRowId { get; set; }
    }

    /// <summary>A message from the Feedback tab. Goes to the plugin author's Discord through the
    /// server, never straight to a webhook - see <see cref="PfApiClient.SubmitReportAsync"/>.</summary>
    internal sealed class SubmitFeedbackRequest
    {
        /// <summary>0 bug, 1 suggestion, 2 help, 3 thoughts - the order of FeedbackKinds.</summary>
        public int Kind { get; set; }

        public string Message { get; set; } = string.Empty;

        /// <summary>Where the author can answer, if the sender wants an answer. Optional.</summary>
        public string Contact { get; set; } = string.Empty;

        /// <summary>Null when the sender left their name off; omitted from the body then.</summary>
        public CharacterIdentity? From { get; set; }

        public string PluginVersion { get; set; } = string.Empty;
    }

    internal sealed class SubmitReportResponse
    {
        public bool Ok { get; set; }
    }

    /// <summary>Character details for a profile view, served from our own cache after the first
    /// lookup. Deliberately small - a job and a level, nothing about where they've been.</summary>
    internal sealed class CharacterInfo
    {
        public string Name { get; set; } = string.Empty;
        public string World { get; set; } = string.Empty;
        public uint JobId { get; set; }
        public string JobName { get; set; } = string.Empty;
        public int JobLevel { get; set; }
    }

    // ══════════════════════════════════════════════════════════
    //  PROGRESSION
    // ══════════════════════════════════════════════════════════

    /// <summary>
    /// A party's progression on the duty they're listed for.
    ///
    /// The region travels with each character because the server can't work it out: it has no
    /// world table, and the client already computes the slug for the FFLogs character links.
    /// </summary>
    internal sealed class ProgressRequest
    {
        /// <summary>
        /// False reads whatever the server already knows; true asks it to refresh anyone whose
        /// entry is older than its own per-character cooldown.
        ///
        /// Opening a panel must never spend a provider call, so only the button sets this.
        /// </summary>
        public bool Refresh { get; set; }

        public string DutyName { get; set; } = string.Empty;
        public List<ProgressPlayer> Players { get; set; } = new();
    }

    internal sealed class ProgressPlayer
    {
        public string Name { get; set; } = string.Empty;
        public string World { get; set; } = string.Empty;
        public string Region { get; set; } = string.Empty;
    }

    internal sealed class EncounterRef
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
    }

    internal sealed class ProgressResponse
    {
        /// <summary>Null when the duty isn't an FFLogs encounter at all, which is most content
        /// and is an answer rather than a failure.</summary>
        public EncounterRef? Encounter { get; set; }

        public List<PlayerProgress> Players { get; set; } = new();

        /// <summary>How the server's shared lookup queue is getting on with this party. Null from
        /// a server that predates the queue, which reads the same as nothing being queued.</summary>
        public ProgressQueueInfo? Queue { get; set; }

        /// <summary>The server's per-character cooldown, in seconds - how long before asking again
        /// could return anything different.</summary>
        public int RefreshAfterSec { get; set; }
    }

    /// <summary>
    /// The state of the server's global progression queue, as it concerns this party.
    ///
    /// The server fetches one character at a time on behalf of every user of the plugin at once,
    /// so a press is an admission to a queue rather than a lookup. These fields are what let the
    /// button say "Queued" and know when to stop waiting.
    /// </summary>
    internal sealed class ProgressQueueInfo
    {
        /// <summary>Party members still waiting on the queue - including any put there by somebody
        /// else's press, which is the point of the queue being global.</summary>
        public int Pending { get; set; }

        /// <summary>How many this particular press added. Zero when everyone was either already
        /// queued or still inside their own cooldown.</summary>
        public int Accepted { get; set; }

        /// <summary>Characters queued across the whole service, ours included.</summary>
        public int Size { get; set; }

        /// <summary>How long the server suggests waiting before reading again.</summary>
        public int PollAfterSec { get; set; }
    }

    /// <summary>
    /// One character's standing on one encounter.
    ///
    /// <see cref="Status"/> is the server's vocabulary: cleared, noclear, hidden, notfound,
    /// unknown. "noclear" means no clear is on RECORD - people clear without ever logging, and
    /// nothing here can tell the two apart, so nothing here may claim they haven't cleared.
    /// </summary>
    internal sealed class PlayerProgress
    {
        public string Name { get; set; } = string.Empty;
        public string World { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public int Kills { get; set; }

        /// <summary>How old the server's copy is, in seconds. 0 for something just fetched.</summary>
        public int AgeSec { get; set; }

        /// <summary>Furthest phase reached on their best logged pull. 0 when unknown.</summary>
        public int Phase { get; set; }

        /// <summary>Boss HP remaining in that phase, so lower is further. Only meaningful
        /// alongside <see cref="Phase"/>, and only for "progging".</summary>
        public double Percent { get; set; }

        /// <summary>Unix ms of the most recent logged pull, or 0.</summary>
        public long LastSeenMs { get; set; }

        /// <summary>
        /// True while this character is sitting on the server's lookup queue.
        ///
        /// Orthogonal to <see cref="Status"/>: somebody being re-read still carries whatever was
        /// last known about them, and the badge keeps showing it rather than blanking. Only a
        /// character nobody has ever looked up is both queued and statusless.
        /// </summary>
        public bool Queued { get; set; }

        /// <summary>
        /// Best FFLogs parse percentile, for players who have cleared. -1 when unknown.
        ///
        /// Only ever set alongside "cleared" - there is no parse without a kill. Comes from
        /// FFLogs even when the progression came from Tomestone, because Tomestone has no parse
        /// data: its rankPercent measures how early someone cleared, not how well they played.
        /// </summary>
        public double Percentile { get; set; } = -1;

        [JsonIgnore]
        public string Key => $"{Name.Trim().ToLowerInvariant()}@{World.Trim().ToLowerInvariant()}";

        /// <summary>
        /// When this answer reached the client, so <see cref="AgeSec"/> can keep counting after
        /// the response that carried it. Client-side only, and never sent anywhere.
        /// </summary>
        [JsonIgnore]
        public DateTime AppliedAt { get; set; } = DateTime.UtcNow;

        /// <summary>When the server fetched this from the provider: the moment it reached the client,
        /// less how old the server's copy already was.</summary>
        [JsonIgnore]
        public DateTime FetchedAt => AppliedAt - TimeSpan.FromSeconds(Math.Max(0, AgeSec));

        /// <summary>"P3 59%", or "59%" when the fight has no phases worth naming.</summary>
        [JsonIgnore]
        public string ProgLabel => Phase > 0
            ? $"P{Phase} {Percent:0.#}%"
            : $"{Percent:0.#}%";
    }

    // ══════════════════════════════════════════════════════════════
    //  RESPONSES
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// A player's public rating as the server reports it. Scores are null until the vote count
    /// clears the display threshold - one grudge vote must never be able to define someone's
    /// reputation, so below the gate the client is told nothing at all.
    /// </summary>
    public sealed class PlayerRating
    {
        public string Name { get; set; } = string.Empty;
        public string World { get; set; } = string.Empty;

        /// <summary>
        /// The rating: a net weighted tally, not a share.
        ///
        /// An upvote is +1 and a downvote -1, each scaled by how much that voter counts - friends
        /// and FC members less than strangers. A percentage cannot express that, because three
        /// unanimous friends and thirty unanimous strangers both read 100%, and only one of those
        /// is worth anything.
        ///
        /// Everybody counts ONCE. Rating the same person again used to add a discounted vote on top
        /// of the last one; it now replaces it, so a number here is a number of people rather than
        /// a number of times they pressed the button.
        /// </summary>
        public int Score { get; set; }

        /// <summary>The same tally with every vote counted equally, for comparison.</summary>
        public int RawScore { get; set; }

        /// <summary>Share of votes that are positive, 0-100, before any weighting. Null while
        /// <see cref="Gated"/>.</summary>
        public double? RawPercent { get; set; }

        /// <summary>Share of votes that are positive after the trust weights, 0-100. This is the
        /// number the UI leads with. Null while <see cref="Gated"/>.</summary>
        public double? WeightedPercent { get; set; }

        /// <summary>How many votes exist. Reported even when gated, so the UI can say
        /// "2 of 3 ratings needed" rather than showing nothing.</summary>
        public int Count { get; set; }

        /// <summary>Positive votes. Zero while gated - the split would give the score away.</summary>
        public int Upvotes { get; set; }

        /// <summary>Negative votes. Zero while gated.</summary>
        public int Downvotes { get; set; }

        /// <summary>Counts per feedback tag, keyed by tag bit index.</summary>
        public Dictionary<string, int> Tags { get; set; } = new();

        /// <summary>True when too few ratings exist to show a score.</summary>
        public bool Gated { get; set; }

        /// <summary>
        /// True when this player chose to leave the community features.
        ///
        /// SAID OUT LOUD, unlike a ban. An opt-out is a decision somebody made and can undo: the
        /// plugin shows "this player has opted out" in place of their score, and everything -
        /// score, votes, clears, their place in the feed - comes back the moment they opt back in.
        /// Nothing about them is deleted while they are out, only moved out of reach.
        ///
        /// Never true for a banned character, even though a ban sets the same column server-side.
        /// See the note in the lookup route: announcing a banned player as having opted out would
        /// be a favour they have not earned, and announcing them as banned is a punishment nobody
        /// decided on.
        /// </summary>
        public bool OptedOut { get; set; }

        /// <summary>
        /// True when this player is not part of the community half at all - opted out, or banned.
        ///
        /// What everything that OFFERS something reads, because both states refuse it identically:
        /// no votes in either direction, no profile, no score. What everything that SAYS something
        /// reads is <see cref="OptedOut"/>, which is true for only one of the two.
        ///
        /// So a banned character is `Hidden` without being `OptedOut`, and the plugin draws exactly
        /// what it draws for a name nobody has ever rated. That is the whole of what a ban looks
        /// like from outside: nothing.
        /// </summary>
        public bool Hidden { get; set; }

        /// <summary>When the local voter may next rate this player, or null if they may now.
        /// Folded into the lookup so the UI can disable the button without a second call.</summary>
        public DateTime? YouCanRateAt { get; set; }

        [JsonIgnore]
        public bool CanRateNow =>
            !Hidden && !OptedOut && (YouCanRateAt == null || YouCanRateAt <= DateTime.UtcNow);
    }

    // ── The community poll ────────────────────────────────────────────────
    //
    // A survey about the plugin, not about a player, so nothing here touches a rating. The one
    // property worth noticing is what is ABSENT: no counts arrive while a poll is open, because
    // the server does not send any until it is published.

    internal sealed class PollOption
    {
        public string Id { get; set; } = string.Empty;
        public string Label { get; set; } = string.Empty;
        public string Detail { get; set; } = string.Empty;
    }

    internal sealed class PollResponse
    {
        public string Slug { get; set; } = string.Empty;
        public string Question { get; set; } = string.Empty;
        public string Blurb { get; set; } = string.Empty;

        [Newtonsoft.Json.JsonProperty("postUrl")]
        public string PostUrl { get; set; } = string.Empty;

        [Newtonsoft.Json.JsonProperty("closesAt")]
        public DateTime? ClosesAt { get; set; }

        public bool Open { get; set; }

        /// <summary>
        /// Whether this connection has already voted, anywhere.
        ///
        /// The server's answer, keyed on the same address the write is, so the plugin knows about a
        /// vote cast on the website and the website knows about one cast here. Without it each
        /// surface only knew its own history and would offer a vote it was going to be refused.
        /// </summary>
        public bool Voted { get; set; }

        public string Token { get; set; } = string.Empty;
        public List<PollOption> Options { get; set; } = new();

        /// <summary>Only ever present once the poll is published. Absent means sealed.</summary>
        public PollResults? Results { get; set; }
    }

    internal sealed class PollResults
    {
        public int Total { get; set; }
        public List<PollResultRow> Options { get; set; } = new();
    }

    internal sealed class PollResultRow
    {
        public string Id { get; set; } = string.Empty;
        public string Label { get; set; } = string.Empty;
        public int Votes { get; set; }
    }

    internal sealed class PollVoteRequest
    {
        public string Slug { get; set; } = string.Empty;
        public string Option { get; set; } = string.Empty;
        public string Token { get; set; } = string.Empty;

        [Newtonsoft.Json.JsonProperty("fromPlugin")]
        public bool FromPlugin { get; set; }
    }

    internal sealed class PollVoteResponse
    {
        public bool Ok { get; set; }
        public string? Error { get; set; }
    }

    internal sealed class BatchLookupResponse
    {
        public List<PlayerRating> Players { get; set; } = new();
    }

    // ══════════════════════════════════════════════════════════════
    //  FEEDBACK TAGS
    // ══════════════════════════════════════════════════════════════

}
#endif
