using System.Collections.Generic;

namespace PfPresets
{
    /// <summary>
    /// Conversions between the plugin's job bitfield (each job's <see cref="JobInfo.BitIndex"/>)
    /// and the game's per-slot job mask (jobs ordered by ClassJob row ID, 1-based).
    /// </summary>
    public static class JobMasks
    {
        /// <summary>Combat ClassJob row IDs in ascending order; the game's slot mask assigns
        /// bit (index + 1) to each.</summary>
        private static readonly List<uint> SortedCombatJobIds = new()
        {
            1, 2, 3, 4, 5, 6, 7, // GLA, PGL, MRD, LNC, ARC, CNJ, THM
            19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32, 33, 34, 35, 36, 37, 38, 39, 40, 41, 42, 43 // PLD to BST
        };

        /// <summary>Bitfield (BitIndex space) accepting every job and class.</summary>
        public static readonly ulong AllJobsMask = BuildAllJobsMask();

        /// <summary>The game-side bit for a ClassJob row ID (1-based; e.g. GLA → bit 1), or -1 if unknown.</summary>
        public static int GetGameJobBitIndex(uint jobId)
        {
            int idx = SortedCombatJobIds.IndexOf(jobId);
            return idx == -1 ? -1 : idx + 1;
        }

        /// <summary>Converts a plugin bitfield (BitIndex space) to the game's slot mask.</summary>
        public static ulong ToGameMask(ulong bitIndexMask)
        {
            if (bitIndexMask == 0) return 0;
            ulong gameMask = 0;
            foreach (var job in JobData.AllJobsAndClasses)
            {
                if ((bitIndexMask & (1UL << job.BitIndex)) != 0)
                {
                    int gameBit = GetGameJobBitIndex((uint)job.Id);
                    if (gameBit != -1)
                        gameMask |= 1UL << gameBit;
                }
            }
            return gameMask;
        }

        /// <summary>Bitfield (BitIndex space) of every job and class belonging to the given role.
        /// <see cref="RoleType.Free"/> yields all jobs; <see cref="RoleType.Omit"/> yields 0.</summary>
        public static ulong GetRoleMask(RoleType role)
        {
            if (role == RoleType.Free) return AllJobsMask;
            ulong mask = 0;
            foreach (var job in JobData.AllJobsAndClasses)
            {
                if (JobData.GetRoleForCategory(job.Category) == role)
                    mask |= 1UL << job.BitIndex;
            }
            return mask;
        }

        private static ulong BuildAllJobsMask()
        {
            ulong mask = 0;
            foreach (var job in JobData.AllJobsAndClasses)
                mask |= 1UL << job.BitIndex;
            return mask;
        }
    }
}
