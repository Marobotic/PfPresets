#if PFP_RATINGS
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
}
#endif
