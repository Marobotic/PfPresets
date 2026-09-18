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

        /// <summary>ultimate_first | ultimate_reclear | savage_first | savage_reclear | savage_tier.
        ///
        /// `savage_tier` is the old shape of a savage first clear - one post for clearing the tier,
        /// back when only the last floor qualified. Kept rather than migrated: those rows mean
        /// exactly what they meant when they were written, and the feed reads them beside the new
        /// ones because a first clear is a first clear.</summary>
        public string Kind { get; set; } = string.Empty;

        /// <summary>
        /// Where this character's own face lives, relative to the API's base - or empty for
        /// somebody whose portrait the server has not got.
        ///
        /// A PATH, NOT A URL, and it is addressed by the hash of its own bytes. Both halves of that
        /// matter: the path is joined against whatever server this client is talking to, so a
        /// cached feed does not carry a hostname around with it; and the hash means the image at an
        /// address can never change, so it is downloaded once per machine and then kept forever
        /// with nothing to invalidate. See PluginUI.Portraits.cs.
        /// </summary>
        public string Portrait { get; set; } = string.Empty;

        /// <summary>
        /// The fight's own art, relative to the API's base - or empty for a fight the server has no
        /// picture for.
        ///
        /// Addressed by content hash exactly like <see cref="Portrait"/>, and fetched and cached by
        /// the same code. It replaces the eight jpgs that used to ship inside the plugin: those
        /// covered the Ultimates and one savage boss, so the moment savage began posting every floor
        /// most of the feed had no picture and the fix was a plugin release per tier. The server
        /// derives the art from the roster's own FFLogs encounter ids, so a fight added next patch
        /// arrives with a picture and nobody has to ship anything.
        /// </summary>
        public string Art { get; set; } = string.Empty;

        [JsonProperty("cleared_at")]
        public DateTime ClearedAt { get; set; }

        /// <summary>
        /// Where this post sits in the feed's own order, in the server's clock - or null from a
        /// server too old to send it.
        ///
        /// NOT THE SAME AS <see cref="ClearedAt"/>, and the difference is the whole reason this
        /// field exists. The feed is ordered by this; it is set when the post is written and again
        /// when somebody shares it. A clear posted late - a party of eight whose clients report
        /// seconds apart, a retry, a queue that was busy - has an older ClearedAt than a post
        /// already on the feed and a NEWER rank, so the two orders genuinely disagree.
        ///
        /// Anything tracking "how far down this feed have I got" has to be kept in this clock. See
        /// Configuration.ClearAnnouncementRankMark for what keeping it in the other one cost.
        /// </summary>
        [JsonProperty("rank_at")]
        public DateTime? RankAt { get; set; }

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

        /// <summary>
        /// A clear somebody will remember, as opposed to an evening's work they have had before.
        ///
        /// The three kinds that mean "the first time": an Ultimate's, a savage floor's, and the
        /// tier clears posted under the older scheme. It decides which of the two lists a post
        /// belongs to, so it is one predicate rather than a comparison written out at each of the
        /// places that ask - the sort of thing that ends up disagreeing with itself.
        /// </summary>
        [JsonIgnore]
        public bool IsFirstClear
            => Kind is "ultimate_first" or "savage_first" or "savage_tier";

        /// <summary>
        /// Worth a banner across somebody's screen: every Ultimate clear, and a savage floor only
        /// the first time.
        ///
        /// Everything is recorded - this decides what makes noise. A savage reclear is the weekly
        /// farm, dozens a night, and it lives on the Savage tab and nowhere that interrupts anybody.
        /// </summary>
        [JsonIgnore]
        public bool IsAnnounceable
            => Kind is "ultimate_first" or "ultimate_reclear" or "savage_first" or "savage_tier";

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
            "savage_first" => "First clear",
            "ultimate_reclear" => "Reclear",
            "savage_reclear" => "Reclear",
            "savage_tier" => "Tier cleared",
            _ => string.Empty,
        };
    }

    internal sealed class AchievementFeedRequest
    {
        /// <summary>
        /// Which half of the feed: "first" | "ultimate", or null for all of it.
        ///
        /// Null is what every build before the split sent, and the server still answers it with the
        /// undivided feed - so this is a narrowing a client asks for rather than a shape it is
        /// given. The announcer's own read leaves it null on purpose: it is watching for anything
        /// worth a banner, and a scoped read would be watching half the table.
        /// </summary>
        [JsonProperty("scope", NullValueHandling = NullValueHandling.Ignore)]
        public string? Scope { get; set; }

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

        /// <summary>
        /// Which half of the feed the badge is counting. This client asks for "first".
        ///
        /// The badge counts first clears and nothing else, and that is a decision about what a
        /// number on the navigation is FOR. It interrupts a reading to say something happened; an
        /// Ultimate reclear happens most evenings, several times, and a badge that rang for each
        /// would be a badge people learn to ignore. It also keeps the mark honest: the count covers
        /// exactly the list that reading the First clears tab shows somebody, so being shown it is
        /// what earns clearing the number.
        /// </summary>
        [JsonProperty("scope", NullValueHandling = NullValueHandling.Ignore)]
        public string? Scope { get; set; }
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
