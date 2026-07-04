using System;

namespace PfPresets
{
    /// <summary>
    /// Represents a single FFXIV combat job for the job selector.
    /// </summary>
    public class JobInfo
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Abbreviation { get; set; } = string.Empty;
        public JobCategory Category { get; set; }

        /// <summary>The bit position for this job in the AcceptedJobFlags bitfield.</summary>
        public int BitIndex { get; set; }
    }

    public enum JobCategory
    {
        Tank,
        PureHealer,
        BarrierHealer,
        MeleeDPS,
        PhysRangedDPS,
        MagicRangedDPS,
    }

    /// <summary>
    /// Static data for all FFXIV combat jobs, matching the in-game job selector layout.
    /// </summary>
    public static class JobData
    {
        // ── Tanks ─────────────────────────────────────────────────
        public static readonly JobInfo PLD = new() { Id = 19, Name = "Paladin", Abbreviation = "PLD", Category = JobCategory.Tank, BitIndex = 0 };
        public static readonly JobInfo WAR = new() { Id = 21, Name = "Warrior", Abbreviation = "WAR", Category = JobCategory.Tank, BitIndex = 2 };
        public static readonly JobInfo DRK = new() { Id = 32, Name = "Dark Knight", Abbreviation = "DRK", Category = JobCategory.Tank, BitIndex = 4 };
        public static readonly JobInfo GNB = new() { Id = 37, Name = "Gunbreaker", Abbreviation = "GNB", Category = JobCategory.Tank, BitIndex = 5 };

        public static readonly JobInfo GLA = new() { Id = 1, Name = "Gladiator", Abbreviation = "GLA", Category = JobCategory.Tank, BitIndex = 1 };
        public static readonly JobInfo MRD = new() { Id = 3, Name = "Marauder", Abbreviation = "MRD", Category = JobCategory.Tank, BitIndex = 3 };

        // ── Pure Healers ──────────────────────────────────────────
        public static readonly JobInfo WHM = new() { Id = 24, Name = "White Mage", Abbreviation = "WHM", Category = JobCategory.PureHealer, BitIndex = 10 };
        public static readonly JobInfo AST = new() { Id = 33, Name = "Astrologian", Abbreviation = "AST", Category = JobCategory.PureHealer, BitIndex = 13 };

        public static readonly JobInfo CNJ = new() { Id = 6, Name = "Conjurer", Abbreviation = "CNJ", Category = JobCategory.PureHealer, BitIndex = 11 };

        // ── Barrier Healers ───────────────────────────────────────
        public static readonly JobInfo SCH = new() { Id = 28, Name = "Scholar", Abbreviation = "SCH", Category = JobCategory.BarrierHealer, BitIndex = 12 };
        public static readonly JobInfo SGE = new() { Id = 40, Name = "Sage", Abbreviation = "SGE", Category = JobCategory.BarrierHealer, BitIndex = 14 };

        // ── Melee DPS ─────────────────────────────────────────────
        public static readonly JobInfo MNK = new() { Id = 20, Name = "Monk", Abbreviation = "MNK", Category = JobCategory.MeleeDPS, BitIndex = 20 };
        public static readonly JobInfo DRG = new() { Id = 22, Name = "Dragoon", Abbreviation = "DRG", Category = JobCategory.MeleeDPS, BitIndex = 22 };
        public static readonly JobInfo NIN = new() { Id = 30, Name = "Ninja", Abbreviation = "NIN", Category = JobCategory.MeleeDPS, BitIndex = 24 };
        public static readonly JobInfo SAM = new() { Id = 34, Name = "Samurai", Abbreviation = "SAM", Category = JobCategory.MeleeDPS, BitIndex = 26 };
        public static readonly JobInfo RPR = new() { Id = 39, Name = "Reaper", Abbreviation = "RPR", Category = JobCategory.MeleeDPS, BitIndex = 27 };
        public static readonly JobInfo VPR = new() { Id = 41, Name = "Viper", Abbreviation = "VPR", Category = JobCategory.MeleeDPS, BitIndex = 28 };
        public static readonly JobInfo BST = new() { Id = 43, Name = "Beastmaster", Abbreviation = "BST", Category = JobCategory.MeleeDPS, BitIndex = 29 };

        public static readonly JobInfo PGL = new() { Id = 2, Name = "Pugilist", Abbreviation = "PGL", Category = JobCategory.MeleeDPS, BitIndex = 21 };
        public static readonly JobInfo LNC = new() { Id = 4, Name = "Lancer", Abbreviation = "LNC", Category = JobCategory.MeleeDPS, BitIndex = 23 };
        public static readonly JobInfo ROG = new() { Id = 29, Name = "Rogue", Abbreviation = "ROG", Category = JobCategory.MeleeDPS, BitIndex = 25 };

        // ── Physical Ranged DPS ───────────────────────────────────
        public static readonly JobInfo BRD = new() { Id = 23, Name = "Bard", Abbreviation = "BRD", Category = JobCategory.PhysRangedDPS, BitIndex = 30 };
        public static readonly JobInfo MCH = new() { Id = 31, Name = "Machinist", Abbreviation = "MCH", Category = JobCategory.PhysRangedDPS, BitIndex = 32 };
        public static readonly JobInfo DNC = new() { Id = 38, Name = "Dancer", Abbreviation = "DNC", Category = JobCategory.PhysRangedDPS, BitIndex = 33 };

        public static readonly JobInfo ARC = new() { Id = 5, Name = "Archer", Abbreviation = "ARC", Category = JobCategory.PhysRangedDPS, BitIndex = 31 };

        // ── Magical Ranged DPS ────────────────────────────────────
        public static readonly JobInfo BLM = new() { Id = 25, Name = "Black Mage", Abbreviation = "BLM", Category = JobCategory.MagicRangedDPS, BitIndex = 40 };
        public static readonly JobInfo SMN = new() { Id = 27, Name = "Summoner", Abbreviation = "SMN", Category = JobCategory.MagicRangedDPS, BitIndex = 42 };
        public static readonly JobInfo RDM = new() { Id = 35, Name = "Red Mage", Abbreviation = "RDM", Category = JobCategory.MagicRangedDPS, BitIndex = 44 };
        public static readonly JobInfo PCT = new() { Id = 42, Name = "Pictomancer", Abbreviation = "PCT", Category = JobCategory.MagicRangedDPS, BitIndex = 45 };
        public static readonly JobInfo BLU = new() { Id = 36, Name = "Blue Mage", Abbreviation = "BLU", Category = JobCategory.MagicRangedDPS, BitIndex = 46 };

        public static readonly JobInfo THM = new() { Id = 7, Name = "Thaumaturge", Abbreviation = "THM", Category = JobCategory.MagicRangedDPS, BitIndex = 41 };
        public static readonly JobInfo ACN = new() { Id = 26, Name = "Arcanist", Abbreviation = "ACN", Category = JobCategory.MagicRangedDPS, BitIndex = 43 };

        /// <summary>All jobs in display order.</summary>
        public static readonly JobInfo[] AllJobs = new[]
        {
            PLD, WAR, DRK, GNB,
            WHM, AST,
            SCH, SGE,
            MNK, DRG, NIN, SAM, RPR, VPR, BST,
            BRD, MCH, DNC,
            BLM, SMN, RDM, PCT, BLU,
        };

        /// <summary>All jobs and base classes in a single array.</summary>
        public static readonly JobInfo[] AllJobsAndClasses = new[]
        {
            PLD, WAR, DRK, GNB,
            WHM, AST,
            SCH, SGE,
            MNK, DRG, NIN, SAM, RPR, VPR, BST,
            BRD, MCH, DNC,
            BLM, SMN, RDM, PCT, BLU,
            GLA, MRD, CNJ, PGL, LNC, ROG, ARC, THM, ACN,
        };

        /// <summary>Finds a job/class by its ClassJob row ID, or null if unknown.</summary>
        public static JobInfo? FindById(uint jobId)
        {
            foreach (var job in AllJobsAndClasses)
            {
                if (job.Id == (int)jobId)
                    return job;
            }
            return null;
        }

        /// <summary>Maps a job sub-category to its Party Finder slot role.</summary>
        public static RoleType GetRoleForCategory(JobCategory category) => category switch
        {
            JobCategory.Tank => RoleType.Tank,
            JobCategory.PureHealer or JobCategory.BarrierHealer => RoleType.Healer,
            JobCategory.MeleeDPS => RoleType.MeleeDPS,
            JobCategory.PhysRangedDPS => RoleType.PhysRangedDPS,
            JobCategory.MagicRangedDPS => RoleType.MagicRangedDPS,
            _ => RoleType.Free,
        };
    }

    /// <summary>
    /// The in-game duty categories exactly as shown in the PF duty dropdown.
    /// </summary>
    public static class DutyCategories
    {
        public static readonly string[] Names = new[]
        {
            "None",
            "Duty Roulette",
            "Dungeons",
            "Guildhests",
            "Trials",
            "Raids",
            "High-end Duty",
            "PvP",
            "Gold Saucer",
            "FATEs",
            "Treasure Hunt",
            "The Hunt",
            "Gathering Forays",
            "Deep Dungeons",
            "Field Operations",
            "V&C Dungeon Finder",
        };
    }
}
