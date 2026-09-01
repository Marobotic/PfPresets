#if PFP_RATINGS
using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace PfPresets
{
    // ══════════════════════════════════════════════════════════════
    //  ACHIEVEMENTS
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// One clear, as the feed reports it.
    ///
    /// Nothing here is computed on this side. Which fights are worth a post, whether a clear is a
    /// first or a reclear, how many hearts it has and whether the person reading has already given
    /// one - all of it is decided by the server and drawn as told. A client that has never heard of
    /// next expansion's Ultimate still renders it correctly, because it is being handed a name and
    /// a job rather than remembering a list.
    /// </summary>
    internal sealed class AchievementPost
    {
        public string Id { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;
        public string World { get; set; } = string.Empty;

        /// <summary>ClassJob row id, or 0 when the clear predates the plugin sending it.</summary>
        public uint Job { get; set; }

        /// <summary>The short form, as the roster derives it: UCOB and FRU for the Ultimates, and
        /// the boss's own name for a savage tier clear - "Lindwurm II".</summary>
        [JsonProperty("fight_label")]
        public string FightLabel { get; set; } = string.Empty;

        /// <summary>What the duty is actually called. The card leads with this; the label is the
        /// fallback for rows recorded before the server carried it.</summary>
        [JsonProperty("fight_name")]
        public string FightName { get; set; } = string.Empty;

        /// <summary>Which art to draw, and the only thing the fight is identified by.</summary>
        [JsonProperty("fight_slug")]
        public string FightSlug { get; set; } = string.Empty;

        /// <summary>ultimate_first | ultimate_reclear | savage_tier.</summary>
        public string Kind { get; set; } = string.Empty;

        [JsonProperty("cleared_at")]
        public DateTime ClearedAt { get; set; }

        public int Hearts { get; set; }

        /// <summary>Whether this reader has hearted it. Includes a heart of theirs that is still
        /// being held, which is the point - it looks the same to them either way.</summary>
        public bool Hearted { get; set; }

        /// <summary>
        /// A heart on this post already came from this connection, but not from this character.
        ///
        /// A heart is one per post per IP, which is what stops somebody walking their alts down the
        /// feed hearting their own friend's clear eight times. The consequence is that the other
        /// seven characters have to be told *why* the button will not move: without this they would
        /// see an empty heart, press it, and watch it quietly do nothing - the same failure the
        /// self-heart rule produced on the feed's first day.
        ///
        /// Drawn as hearted, because it is: the post genuinely has this household's heart on it.
        /// It is only <see cref="Hearted"/> that carries the right to take it back, which is why
        /// these are two fields and not one tri-state - the owner is the one who can undo, and
        /// nobody else, however much the same person they happen to be.
        /// </summary>
        [JsonProperty("heart_locked")]
        public bool HeartLocked { get; set; }

        /// <summary>Whether the one share this post gets has been used, by anybody.</summary>
        public bool Reshared { get; set; }

        [JsonIgnore]
        public bool IsFirstClear => Kind == "ultimate_first";

        [JsonIgnore]
        public CharacterIdentity Identity => new(Name, World);

        /// <summary>The headline. Falls back to the short label rather than to nothing.</summary>
        [JsonIgnore]
        public string Title =>
            !string.IsNullOrWhiteSpace(FightName) ? FightName : FightLabel;

        /// <summary>What the chip says. Wording lives here so the three places that draw a post
        /// cannot end up describing the same clear three ways.</summary>
        [JsonIgnore]
        public string KindLabel => Kind switch
        {
            "ultimate_first" => "First clear",
            "ultimate_reclear" => "Reclear",
            "savage_tier" => "Tier cleared",
            _ => string.Empty,
        };
    }

    internal sealed class AchievementFeedRequest
    {
        /// <summary>Which page, zero-based. Pages rather than a cursor: the feed is short enough to
        /// number, and somebody who wants page four wants page four.</summary>
        public int Page { get; set; }

        /// <summary>
        /// Unix ms; asks for posts ranked before this one. Null is the top of the feed.
        ///
        /// Nullable so that "the top" is an absent field rather than a zero. It was a plain long,
        /// which serialises as 0, and the server read that as a real timestamp - the epoch - and
        /// answered with everything older than 1970. Which is nothing. Every user saw an empty
        /// feed while the table had posts in it, and every test passed, because the tests sent no
        /// cursor at all.
        /// </summary>
        public long? Before { get; set; }
    }

    internal sealed class AchievementFeedResponse
    {
        public List<AchievementPost> Posts { get; set; } = new();

        public int Page { get; set; }

        /// <summary>How many pages there are. Counted by the server rather than guessed from
        /// whether the last page came back full - which is wrong exactly when the total is a
        /// multiple of the page size.</summary>
        public int Pages { get; set; } = 1;

        public int Total { get; set; }

        /// <summary>
        /// The server's clock when it answered, in unix ms. Stored as the unread mark.
        ///
        /// Somebody holding this response has seen everything that existed when it was built, so
        /// this is the honest "read up to here". It is the server's own number and goes back to the
        /// server unchanged - the badge is never a comparison between two machines' clocks, which
        /// is the mistake that would show a player with a fast PC nothing and a player with a slow
        /// one the same three posts every hour.
        ///
        /// Zero from a server that predates it, which reads as "no mark to take" and leaves the
        /// stored one alone.
        /// </summary>
        public long Now { get; set; }
    }

    internal sealed class AchievementUnseenRequest
    {
        /// <summary>The mark from the last feed the reader was shown, in unix ms. Never zero - a
        /// client with no mark has never opened the tab and does not ask.</summary>
        public long Since { get; set; }
    }

    internal sealed class AchievementUnseenResponse
    {
        /// <summary>How many posts have appeared since, capped by the server.</summary>
        public int Count { get; set; }

        /// <summary>The cap was hit, so the badge says "99+" rather than a number that is
        /// wrong.</summary>
        public bool More { get; set; }
    }

    /// <summary>A clear being offered to the feed. The sealed payload is the whole of it - the
    /// server reads the fight, the job and the character out of that and takes nothing else on
    /// trust.</summary>
    internal sealed class AchievementPostRequest
    {
        public string Evidence { get; set; } = string.Empty;
    }

    internal sealed class AchievementPostResponse
    {
        public bool Ok { get; set; }

        /// <summary>False for the ordinary case of a duty that is not worth a post, which is most
        /// of them. Not an error and never surfaced.</summary>
        public bool Posted { get; set; }

        public string Kind { get; set; } = string.Empty;
        public string Fight { get; set; } = string.Empty;
    }

    internal sealed class AchievementReactRequest
    {
        public string Id { get; set; } = string.Empty;
    }

    internal sealed class AchievementReactResponse
    {
        public bool Ok { get; set; }

        /// <summary>
        /// Whether this reader's heart is on the post now.
        ///
        /// This used to be "always true, confirmation of receipt", because the heart route answered
        /// the same whatever it decided underneath. It cannot stay that way now that a heart can be
        /// taken back: an undo whose reply is hard-coded to "hearted" tells the client the one
        /// thing it must not believe. The route answers honestly in both directions, and the client
        /// applies what it is told rather than what it hoped.
        /// </summary>
        public bool Hearted { get; set; }

        /// <summary>
        /// The heart on this post belongs to a different character on this connection, so this
        /// request changed nothing. See <see cref="AchievementPost.HeartLocked"/>.
        /// </summary>
        [JsonProperty("heart_locked")]
        public bool HeartLocked { get; set; }

        /// <summary>
        /// The post's heart count as the server now has it, or null from a server that does not
        /// send one.
        ///
        /// Nullable on purpose. A plain int would arrive as 0 from any server that omits the field
        /// and the client would obediently wipe a real count to zero the moment anybody pressed the
        /// button - the same shape of bug as the Before cursor that asked for everything older than
        /// 1970. Null means "not told", and not-told leaves the local number alone.
        /// </summary>
        public int? Hearts { get; set; }

        /// <summary>Unlike a heart, this one is real: a post gets one share ever, and false means
        /// somebody else got there first.</summary>
        public bool Reshared { get; set; }
    }

    internal sealed class BroadcastSettingRequest
    {
        public CharacterRef Character { get; set; } = new();
        public bool Broadcast { get; set; }
    }

    internal sealed class BroadcastSettingResponse
    {
        public bool Ok { get; set; }
        public bool Broadcast { get; set; }
    }

    /// <summary>Named for the setting rather than the queue: OptOutResponse is already the
    /// moderator's list of requests, and these two are not the same thing.</summary>
    internal sealed class OptOutSelfResponse
    {
        public bool Ok { get; set; }

        /// <summary>The request was filed. Nothing has changed yet - a moderator decides.</summary>
        public bool Requested { get; set; }
    }

    internal sealed class OptOutStateResponse
    {
        /// <summary>Approved and in force. This is the only one that hides anything.</summary>
        public bool OptedOut { get; set; }

        /// <summary>A request is filed and undecided.</summary>
        public bool Pending { get; set; }

        /// <summary>False when the server has no session character to answer about - logged out,
        /// or a lookup that has not happened yet. The toggle waits rather than guessing.</summary>
        public bool Known { get; set; }
    }

    internal sealed class CharacterRef
    {
        public string Name { get; set; } = string.Empty;
        public string World { get; set; } = string.Empty;
    }
}
#endif
