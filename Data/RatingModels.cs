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
    /// Which way a vote went. There is no middle value on purpose: after one duty with a stranger
    /// you know whether you'd happily play with them again or not, and little else. Two options
    /// also give a brigade far less room to nudge a score without it being obvious.
    /// </summary>
    public enum VoteDirection
    {
        /// <summary>Only for history entries recovered from the cooldown list, where the fact of
        /// the rating survived but its direction didn't.</summary>
        Unknown = 0,

        Down = -1,
        Up = 1,
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

    internal sealed class SubmitRatingRequest
    {
        /// <summary>
        /// This vote's id, minted by the client and reused on every attempt.
        ///
        /// The server remembers ids it has processed, so a vote whose reply was lost is recognised
        /// on the retry rather than counted twice. It is not a proof of anything - the client
        /// generates it, and anything the client can compute an attacker can compute too - it just
        /// has to be unique.
        /// </summary>
        public string VoteId { get; set; } = string.Empty;

        public CharacterIdentity Target { get; set; } = new();

        /// <summary>+1 or -1. Serialised as a number because that is what the column stores.</summary>
        public int Score { get; set; }
        public int Tags { get; set; }
        public int DutyRowId { get; set; }
        public SocialLink SocialLink { get; set; }

        /// <summary>When the duty that prompted this rating finished. Context for abuse review;
        /// the server stores only the day, never this precise value.</summary>
        public DateTime MetAt { get; set; }

        /// <summary>
        /// Sealed proof that this vote came out of a duty both players were in.
        ///
        /// The server refuses a vote without it. Built by a component that is not in this
        /// repository, so a vote cannot be mimicked by reading the source - though the checks that
        /// actually stop abuse are the server's, and hold whether or not the format is known.
        /// </summary>
        public string Evidence { get; set; } = string.Empty;
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
            (ReportReason.RatingAbuse, "Rating abuse"),
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

    internal sealed class EncounterRef
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
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

        /// <summary>"P3 59%", or "59%" when the fight has no phases worth naming.</summary>
        [JsonIgnore]
        public string ProgLabel => Phase > 0
            ? $"P{Phase} {Percent:0.#}%"
            : $"{Percent:0.#}%";
    }

    /// <summary>Community-wide totals, for the analytics view.</summary>
    public sealed class RatingStats
    {
        /// <summary>How many distinct characters have received at least one rating.</summary>
        public int PlayersRated { get; set; }

        /// <summary>How many ratings have been submitted in total.</summary>
        public int RatingsSubmitted { get; set; }

        /// <summary>Community-wide positive and negative totals.</summary>
        public int Upvotes { get; set; }
        public int Downvotes { get; set; }

        public DateTime? UpdatedAt { get; set; }
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
        /// and FC members less, a repeat voter on the same person far less. A percentage cannot
        /// express that, because three unanimous friends and thirty unanimous strangers both read
        /// 100%, and only one of those is worth anything.
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

    internal sealed class SubmitRatingResponse
    {
        /// <summary>The weight the server actually applied, after its own social and repeat
        /// discounts. Shown back to the voter so the weighting is never a black box.</summary>
        public double WeightApplied { get; set; }

        public DateTime? NextEligibleAt { get; set; }
    }

    /// <summary>
    /// Server-owned tunables, fetched once per session. Keeping these on the server means the
    /// weights and the display gate can be retuned without shipping a plugin update, and the
    /// client's copy is only ever used to explain the rules in the UI - never to enforce them.
    /// </summary>
    public sealed class RatingPolicy
    {
        public double BaseWeight { get; set; } = 1.0;
        public double SocialLinkWeight { get; set; } = 0.5;
        public double RepeatVoteWeight { get; set; } = 0.1;
        public int MinVotesToDisplay { get; set; } = 3;
        public int CooldownHours { get; set; } = 24;
        public int BatchMax { get; set; } = 32;

        public static RatingPolicy Default => new();
    }

    // ══════════════════════════════════════════════════════════════
    //  FEEDBACK TAGS
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// The structured feedback options offered alongside the score. Deliberately all positive:
    /// a fixed list of compliments cannot be turned into a harassment vector the way free text
    /// or negative labels can, and it keeps the plugin out of the business of hosting insults.
    /// </summary>
    public static class RatingTags
    {
        public const int Communication = 1 << 0;
        public const int Punctual = 1 << 1;
        public const int Patient = 1 << 2;
        public const int GoodMechanics = 1 << 3;
        public const int Helpful = 1 << 4;
        public const int Friendly = 1 << 5;

        public static readonly (int Bit, string Label)[] All =
        {
            (Communication, "Good communication"),
            (Punctual, "Punctual"),
            (Patient, "Patient"),
            (GoodMechanics, "Solid mechanics"),
            (Helpful, "Helpful"),
            (Friendly, "Friendly"),
        };

        /// <summary>Every bit we know about, used to drop unknown bits from a server response.</summary>
        public const int KnownMask = Communication | Punctual | Patient | GoodMechanics | Helpful | Friendly;
    }
}
#endif
