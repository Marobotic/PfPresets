#if PFP_RATINGS
using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace PfPresets
{
    // ══════════════════════════════════════════════════════════════
    //  CLEARS
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// A request for one character's high-end clears.
    ///
    /// The region travels with the name for the same reason it does on a progression request: the
    /// server has no world table, and the client already computes the slug for the FFLogs links.
    /// </summary>
    internal sealed class ClearsRequest
    {
        public string Name { get; set; } = string.Empty;
        public string World { get; set; } = string.Empty;
        public string Region { get; set; } = string.Empty;

        /// <summary>
        /// False reads whatever the server already knows; true asks it to look this character up,
        /// if their stored copy is past its hour.
        ///
        /// Opening a card must never spend a provider call, so only the refresh button sets this.
        /// </summary>
        public bool Refresh { get; set; }
    }

    /// <summary>
    /// One character's clears, as the server reports them.
    ///
    /// The sections arrive from the server rather than being compiled in - which fights count as
    /// "the current tier" is decided from the providers' own catalogues, so a patch changes what
    /// every card lists without a plugin update. A client that has never heard of a new tier still
    /// draws it correctly, because it is being told the fights rather than remembering them.
    /// </summary>
    internal sealed class ClearsResponse
    {
        public string Name { get; set; } = string.Empty;
        public string World { get; set; } = string.Empty;

        /// <summary>When the server last read this character from the providers, or null when
        /// nobody ever has. Null is an invitation to fetch, not a player who has cleared nothing -
        /// the two look identical on a card that doesn't keep them apart.</summary>
        public DateTime? FetchedAt { get; set; }

        /// <summary>How old the server's copy was when it answered, in seconds.</summary>
        public int AgeSec { get; set; }

        /// <summary>unfetched | ok | hidden | notfound. "hidden" is a player who has switched
        /// their activity off at the provider, which is theirs to set and not a failure.</summary>
        public string Status { get; set; } = string.Empty;

        /// <summary>Which providers answered, comma separated. Only ever shown in a tooltip - it
        /// explains a thin card ("clears but no parses" is FFLogs having been unreachable).</summary>
        public string Sources { get; set; } = string.Empty;

        /// <summary>True while this character is sitting on the server's clears queue.</summary>
        public bool Queued { get; set; }

        /// <summary>The server's per-character cooldown in seconds - an hour, at time of writing.
        /// Read from the response rather than assumed, so it can be retuned server-side.</summary>
        public int RefreshAfterSec { get; set; }

        public List<ClearsSection> Sections { get; set; } = new();

        /// <summary>When this answer reached the client, so <see cref="AgeSec"/> can keep counting
        /// after the response that carried it. Client-side only, never sent anywhere.</summary>
        [JsonIgnore]
        public DateTime AppliedAt { get; set; } = DateTime.UtcNow;

        /// <summary>How old the server's copy is now, taking the wait since it answered into
        /// account. What the footer's "2h ago" is drawn from.</summary>
        [JsonIgnore]
        public TimeSpan Age => TimeSpan.FromSeconds(AgeSec) + (DateTime.UtcNow - AppliedAt);

        /// <summary>True once anything is known about this character - the card shows sections
        /// only after somebody has fetched them.</summary>
        [JsonIgnore]
        public bool Fetched => FetchedAt != null;

        /// <summary>Whether any section has a clear in it. A fetched character with nothing to
        /// show gets a line saying so rather than four empty headings.</summary>
        [JsonIgnore]
        public bool AnyClears
        {
            get
            {
                foreach (var section in Sections)
                {
                    if (section.Cleared > 0)
                        return true;
                }
                return false;
            }
        }
    }

    /// <summary>One heading and the fights under it: Ultimates, Savage, Extremes, Unreal.</summary>
    internal sealed class ClearsSection
    {
        /// <summary>ultimate | savage | extreme | unreal. Drives the icon, not the wording.</summary>
        public string Key { get; set; } = string.Empty;

        public string Label { get; set; } = string.Empty;

        /// <summary>How many of these they've cleared, and how many there are - the "3 / 7" beside
        /// the heading. Counted by the server so the two figures cannot disagree with the pills.</summary>
        public int Cleared { get; set; }
        public int Total { get; set; }

        public List<ClearedFight> Fights { get; set; } = new();
    }

    /// <summary>
    /// One fight on one character's card.
    ///
    /// <see cref="Cleared"/> is false when NO CLEAR IS ON RECORD, which is not the same as not
    /// having cleared: it means neither a Lodestone achievement nor a logged kill exists, and
    /// plenty of people clear without either. Nothing in the UI may word it as an accusation.
    /// </summary>
    internal sealed class ClearedFight
    {
        /// <summary>Stable identifier for the fight, ours rather than either provider's.</summary>
        public string Slug { get; set; } = string.Empty;

        /// <summary>What the pill says: "UCOB" for an Ultimate, and the boss's own name for
        /// everything else - "Lindwurm II", "Red Hot / Deep Blue", "Zoraal Ja".</summary>
        public string Label { get; set; } = string.Empty;

        /// <summary>The in-game duty name, for the tooltip - "AAC Heavyweight M4 (Savage)". Two
        /// fights can share one: a floor with a boss either side of a checkpoint is two pills and
        /// one duty, and the tooltip is where the floor they belong to is said.</summary>
        public string Duty { get; set; } = string.Empty;

        public bool Cleared { get; set; }

        /// <summary>Kills on record at FFLogs. Zero alongside a clear is ordinary: it means the
        /// clear is proven by an achievement and they never uploaded a log.</summary>
        public int Kills { get; set; }

        /// <summary>Best parse on the fight's current listing, or -1 when unknown. Only ever set
        /// alongside a clear - there is no parse without a kill.</summary>
        public double Percentile { get; set; } = -1;

        /// <summary>When they cleared it, from the Lodestone achievement. Null when only FFLogs
        /// knew about the clear, which cannot say when.</summary>
        public DateTime? ClearedAt { get; set; }

        [JsonIgnore]
        public bool HasParse => Cleared && Percentile >= 0;
    }
}
#endif
