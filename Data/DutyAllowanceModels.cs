#if PFP_RATINGS
using System.Collections.Generic;
using Newtonsoft.Json;

namespace PfPresets
{
    /// <summary>
    /// The duty report, and the answer to "who may I vote on".
    ///
    /// WHY THE CLIENT ASKS AT ALL. The post-duty window used to open on everybody who was in the
    /// room and find out afterwards whether any of it counted. The rule it was judged against -
    /// that the OTHER person's client also filed this duty - could not possibly be true yet at the
    /// moment the window appeared, because both clients leave the instance within seconds of each
    /// other. A vote cast into that gap was held server-side, and a held vote looks exactly like a
    /// counted one from here. People were being invited to do something that quietly did nothing.
    ///
    /// So the window waits for this instead.
    /// </summary>
    internal sealed class DutyReportRequest
    {
        /// <summary>The same sealed payload the achievement post carries. One duty, described once.</summary>
        public string Evidence { get; set; } = string.Empty;
    }

    /// <summary>Asking again about a duty already filed. The roster goes with it because the
    /// server holds keyed hashes and cannot name anybody back.</summary>
    internal sealed class DutyAllowanceRequest
    {
        public int Duty { get; set; }

        /// <summary>When the duty ended, in unix ms - the same number the evidence carried.</summary>
        public long Ended { get; set; }

        public DutyAllowanceWho Me { get; set; } = new();

        public List<DutyAllowanceWho> Party { get; set; } = new();
    }

    internal sealed class DutyAllowanceWho
    {
        [JsonProperty("n")]
        public string Name { get; set; } = string.Empty;

        [JsonProperty("w")]
        public string World { get; set; } = string.Empty;
    }

    internal sealed class DutyAllowanceResponse
    {
        public bool Ok { get; set; }

        public int Duty { get; set; }

        public long Ended { get; set; }

        /// <summary>
        /// "ready", "pending", "skipped" or "unfiled".
        ///
        /// READY MEANS SETTLED, NOT SUCCESSFUL. An allowance where nobody turned out to be running
        /// the plugin is as final as one where everybody is - there is nobody left to wait for.
        /// Only "pending" is worth asking about again.
        /// </summary>
        public string Status { get; set; } = string.Empty;

        /// <summary>Why a "skipped" allowance was skipped. "undersized" is the only one today.</summary>
        public string? Reason { get; set; }

        /// <summary>When the vote window shuts, in unix ms. The server's number rather than ours,
        /// so the two ends cannot drift apart about it.</summary>
        [JsonProperty("expiresMs")]
        public long ExpiresMs { get; set; }

        public List<DutyAllowanceMember> Members { get; set; } = new();
    }

    internal sealed class DutyAllowanceMember
    {
        [JsonProperty("n")]
        public string Name { get; set; } = string.Empty;

        [JsonProperty("w")]
        public string World { get; set; } = string.Empty;

        /// <summary>"allowed", "pending", "noplugin" or "optout". Only the first is offered a row.</summary>
        public string State { get; set; } = string.Empty;
    }
}
#endif
