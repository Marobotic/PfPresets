#if PFP_RATINGS
using System.Collections.Generic;
using Newtonsoft.Json;

namespace PfPresets
{
    // ══════════════════════════════════════════════════════════════
    //  PF CROWDSOURCING
    //
    //  The wire format for "I am in this listing" and "who is in this listing".
    //
    //  ONE CHARACTER PER REPORT, AND IT HAS TO BE YOURS. The report names the listing's leader as
    //  the key and exactly one character - the one sending it. The server refuses a report whose
    //  character does not match the session's own, so this cannot carry a roster of other people
    //  even if a client tried to put one in it.
    // ══════════════════════════════════════════════════════════════

    /// <summary>"I am in the listing led by X, on this job."</summary>
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
        /// Everyone who has reported themselves into this listing and not yet aged out.
        ///
        /// Expected to be a subset of the party, usually a small one - only people running this
        /// plugin with the setting on are ever in here. The panel says as much rather than
        /// implying the rest of the party is empty.
        /// </summary>
        public List<PfMember> Members { get; set; } = new();
    }

    internal sealed class PfReportResponse
    {
        public bool Ok { get; set; }
    }
}
#endif
