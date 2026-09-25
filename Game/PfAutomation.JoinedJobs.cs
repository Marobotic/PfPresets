using System.Collections.Generic;
using System.Linq;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

namespace PfPresets
{
    /// <summary>
    /// The job each party member joined the listing as.
    ///
    /// A Party Finder seat is taken on a job and the listing keeps saying that job, whatever its
    /// holder switches to afterwards - a healer who wanders off to craft is still the healer as
    /// far as the listing is concerned. So whatever describes a listing to other players (the
    /// recruiter's own report, a member's party report, the coordination layout) reports the job
    /// joined as, never the job worn right now.
    ///
    /// TWO SOURCES. Each member's job the first time they are seen in the party - which, with the
    /// plugin running, is the moment they joined. And, overriding it, the listing's own detail
    /// window whenever it has shown this party: it lists each seat's member and job, straight from
    /// the game. Forgotten per member when they leave, and entirely when the party is gone.
    /// </summary>
    public partial class PfAutomation
    {
        private readonly Dictionary<ulong, uint> joinedJobs = new();

        /// <summary>The job this member joined as, or <paramref name="current"/> when it is not
        /// known.</summary>
        public uint JoinedJob(ulong contentId, uint current)
            => contentId != 0 && joinedJobs.TryGetValue(contentId, out uint job) ? job : current;

        /// <summary>This character's own joined-as job.</summary>
        public uint LocalJoinedJob() => JoinedJob(playerState.ContentId, GetLocalPlayerJobId());

        /// <summary>Framework thread, a couple of times a second.</summary>
        private unsafe void TrackJoinedJobs()
        {
            var others = GetOtherPartyMemberDetails().Where(m => !m.IsSupportNpc && m.ContentId != 0).ToList();
            if (others.Count == 0)
            {
                joinedJobs.Clear();
                return;
            }

            ulong me = playerState.ContentId;
            var present = new HashSet<ulong>(others.Select(m => m.ContentId)) { me };
            foreach (ulong gone in joinedJobs.Keys.Where(k => !present.Contains(k)).ToList())
                joinedJobs.Remove(gone);

            // First sight: the job they came in on. Only a combat job - a seat is never a crafter's.
            uint mine = GetLocalPlayerJobId();
            if (me != 0 && !joinedJobs.ContainsKey(me) && JobData.FindById(mine) != null)
                joinedJobs[me] = mine;
            foreach (var m in others)
            {
                if (!joinedJobs.ContainsKey(m.ContentId) && JobData.FindById(m.JobId) != null)
                    joinedJobs[m.ContentId] = m.JobId;
            }

            // The listing's own word, when its detail window has shown this party.
            var agent = AgentLookingForGroup.Instance();
            if (agent == null)
                return;

            var viewed = agent->LastViewedListing;
            if (viewed.ListingId == 0 || !present.Contains(viewed.LeaderContentId))
                return;

            var ids = viewed.MemberContentIds;
            var jobs = viewed.Jobs;
            for (int i = 0; i < ids.Length && i < jobs.Length; i++)
            {
                ulong id = ids[i];
                uint job = jobs[i];
                if (id != 0 && job != 0 && present.Contains(id) && JobData.FindById(job) != null)
                    joinedJobs[id] = job;
            }
        }
    }
}
