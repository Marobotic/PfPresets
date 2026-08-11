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

        /// <summary>The short form, as the roster derives it: UCOB, FRU, M4S.</summary>
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

        /// <summary>Always true. The server answers the same whether a heart was stored, held or
        /// dropped - see the note on the heart route - so this is confirmation of receipt and not
        /// a report on what happened.</summary>
        public bool Hearted { get; set; }

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
