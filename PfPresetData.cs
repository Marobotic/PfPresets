using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace PfPresets
{
    /// <summary>
    /// Represents a single saved Party Finder recruitment preset.
    /// All fields mirror the in-game Recruitment Criteria window.
    /// </summary>
    [Serializable]
    public class PfPresetData
    {
        // ── Identity ──────────────────────────────────────────────
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; } = "New Preset";
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime LastUsedAt { get; set; } = DateTime.MinValue;

        // ── Group Type ────────────────────────────────────────────
        /// <summary>0 = Normal, 1 = Alliance, 2 = Custom Match</summary>
        public int GroupType { get; set; } = 0;

        // ── Duty ──────────────────────────────────────────────────
        /// <summary>Duty category index matching the in-game dropdown.
        /// 0=None, 1=Duty Roulette, 2=Dungeons, 3=Guildhests, 4=Trials, 5=Raids,
        /// 6=High-end Duty, 7=PvP, 8=Gold Saucer, 9=FATEs, 10=Treasure Hunt,
        /// 11=The Hunt, 12=Gathering Forays, 13=Deep Dungeons, 14=Field Operations,
        /// 15=V&amp;C Dungeon Finder</summary>
        public int DutyCategoryId { get; set; } = 0;

        /// <summary>Index within the selected duty category. 0 = first duty in category.</summary>
        public int DutyIndex { get; set; } = 0;

        /// <summary>Cached display name for the duty (so we don't need Lumina to show it).</summary>
        public string DutyName { get; set; } = "None";

        /// <summary>Cached display name for the duty category.</summary>
        public string DutyCategoryName { get; set; } = "None";

        // ── Objective ─────────────────────────────────────────────
        /// <summary>0=None, 1=Duty Completion, 2=Practice, 3=Loot</summary>
        public int ObjectiveId { get; set; } = 0;

        // ── Comment ───────────────────────────────────────────────
        /// <summary>Free-text comment. Max 76 characters, displayed as up to 2 lines in-game.</summary>
        public string Comment { get; set; } = string.Empty;

        // ── Role Slots ────────────────────────────────────────────
        /// <summary>Up to 8 role slots for Normal parties (more for Alliance).</summary>
        [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
        public List<RoleSlot> Slots { get; set; } = new()
        {
            new RoleSlot { SlotIndex = 0, Role = RoleType.Free },
            new RoleSlot { SlotIndex = 1, Role = RoleType.Free },
            new RoleSlot { SlotIndex = 2, Role = RoleType.Free },
            new RoleSlot { SlotIndex = 3, Role = RoleType.Free },
            new RoleSlot { SlotIndex = 4, Role = RoleType.Free },
            new RoleSlot { SlotIndex = 5, Role = RoleType.Free },
            new RoleSlot { SlotIndex = 6, Role = RoleType.Free },
            new RoleSlot { SlotIndex = 7, Role = RoleType.Free },
        };

        // ── Role Options ──────────────────────────────────────────
        public bool AutoAdjustRoles { get; set; } = false;
        public bool RemoveRoleRestrictions { get; set; } = false;
        public bool UnselectClasses { get; set; } = false;
        public bool OnePlayerPerJob { get; set; } = false;

        // ── Search Area ───────────────────────────────────────────
        public bool LimitRecruitingToWorld { get; set; } = false;
        public bool FormPrivateParty { get; set; } = false;

        /// <summary>Numeric password 0-9999. Always displayed as 4 zero-padded digits (e.g. 0001, 0056).</summary>
        public string PrivatePartyPassword { get; set; } = string.Empty;

        /// <summary>Returns the password as a 4-digit zero-padded string.</summary>
        [JsonIgnore]
        public string PasswordDisplay => string.IsNullOrEmpty(PrivatePartyPassword) ? "0000" : (int.TryParse(PrivatePartyPassword, out int val) ? val.ToString("D4") : "0000");

        // ── Conditions ────────────────────────────────────────────
        public bool CompletionStatusEnabled { get; set; } = false;

        /// <summary>0 = Duty Complete, 1 = Duty Complete (Weekly Reward Unclaimed), 2 = Duty Incomplete</summary>
        public int CompletionStatusType { get; set; } = 0;

        public bool AvgItemLvEnabled { get; set; } = false;
        public int AvgItemLv { get; set; } = 1;

        /// <summary>true = "or Above", false = exact.</summary>
        public bool AvgItemLvOrAbove { get; set; } = true;

        // ── Duty Finder Settings ──────────────────────────────────
        public bool UnrestrictedParty { get; set; } = false;
        public bool MinimumIL { get; set; } = false;
        public bool SilenceEcho { get; set; } = false;

        // ── Loot Rules ────────────────────────────────────────────
        /// <summary>0 = Normal, 1 = Greed Only, 2 = Lootmaster</summary>
        public int LootRules { get; set; } = 0;

        // ── Language ──────────────────────────────────────────────
        public bool LangJapanese { get; set; } = true;
        public bool LangEnglish { get; set; } = true;
        public bool LangGerman { get; set; } = true;
        public bool LangFrench { get; set; } = true;

        /// <summary>Creates a deep copy of this preset with a new ID.</summary>
        public PfPresetData Duplicate()
        {
            var json = JsonConvert.SerializeObject(this);
            var copy = JsonConvert.DeserializeObject<PfPresetData>(json)!;
            copy.Id = Guid.NewGuid().ToString("N");
            copy.Name = this.Name + " (Copy)";
            copy.CreatedAt = DateTime.UtcNow;
            copy.LastUsedAt = DateTime.MinValue;
            return copy;
        }

        /// <summary>Returns a short summary string for display in the preset list.</summary>
        public string GetRoleSummary()
        {
            if (AutoAdjustRoles) return "Auto-Adjusted";

            int tanks = 0, healers = 0, dps = 0, free = 0;
            foreach (var slot in Slots)
            {
                switch (slot.Role)
                {
                    case RoleType.Tank: tanks++; break;
                    case RoleType.Healer: healers++; break;
                    case RoleType.MeleeDPS:
                    case RoleType.PhysRangedDPS:
                    case RoleType.MagicRangedDPS: dps++; break;
                    case RoleType.Free: free++; break;
                }
            }

            var parts = new List<string>();
            if (tanks > 0) parts.Add($"{tanks}T");
            if (healers > 0) parts.Add($"{healers}H");
            if (dps > 0) parts.Add($"{dps}D");
            if (free > 0) parts.Add($"{free}F");
            return parts.Count > 0 ? string.Join(" ", parts) : "No roles";
        }
    }

    /// <summary>
    /// Represents a single role slot in a party recruitment preset.
    /// </summary>
    [Serializable]
    public class RoleSlot
    {
        public int SlotIndex { get; set; } = 0;
        public RoleType Role { get; set; } = RoleType.Free;

        /// <summary>
        /// Bitfield of accepted job IDs for this slot.
        /// Each bit corresponds to a job from the JobData.AllJobs list.
        /// 0 = accept all jobs for the role (no restriction).
        /// </summary>
        public ulong AcceptedJobFlags { get; set; } = 0;
    }

    /// <summary>
    /// Role types matching the in-game Party Finder slot categories.
    /// </summary>
    public enum RoleType
    {
        Free = 0,
        Tank = 1,
        Healer = 2,
        MeleeDPS = 3,
        PhysRangedDPS = 4,
        MagicRangedDPS = 5,
        Nullify = 6,
    }

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

        /// <summary>Returns all jobs that belong to the given role type.</summary>
        public static JobInfo[] GetJobsForRole(RoleType role)
        {
            return role switch
            {
                RoleType.Tank => new[] { PLD, WAR, DRK, GNB },
                RoleType.Healer => new[] { WHM, AST, SCH, SGE },
                RoleType.MeleeDPS => new[] { MNK, DRG, NIN, SAM, RPR, VPR, BST },
                RoleType.PhysRangedDPS => new[] { BRD, MCH, DNC },
                RoleType.MagicRangedDPS => new[] { BLM, SMN, RDM, PCT, BLU },
                _ => AllJobs,
            };
        }

        /// <summary>Returns jobs grouped by their sub-category for the full job selector UI.</summary>
        public static (string Label, JobInfo[] Jobs)[] GetJobGroups()
        {
            return new[]
            {
                ("Tank", new[] { PLD, WAR, DRK, GNB }),
                ("Pure Healer", new[] { WHM, AST }),
                ("Barrier Healer", new[] { SCH, SGE }),
                ("Melee DPS", new[] { MNK, DRG, NIN, SAM, RPR, VPR, BST }),
                ("Physical Ranged DPS", new[] { BRD, MCH, DNC }),
                ("Magical Ranged DPS", new[] { BLM, SMN, RDM, PCT, BLU }),
            };
        }
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

    /// <summary>
    /// Static helper for standard party compositions.
    /// Used by the "Seek Job Distributions" button.
    /// </summary>
    public static class StandardCompositions
    {
        /// <summary>Standard Full Party composition: 2T 2H 4D</summary>
        public static readonly RoleType[] FullParty = new[]
        {
            RoleType.Tank,
            RoleType.Tank,
            RoleType.Healer,
            RoleType.Healer,
            RoleType.MeleeDPS,
            RoleType.MeleeDPS,
            RoleType.PhysRangedDPS,
            RoleType.MagicRangedDPS,
        };

        /// <summary>Standard Light Party composition: 1T 1H 2D</summary>
        public static readonly RoleType[] LightParty = new[]
        {
            RoleType.Tank,
            RoleType.Healer,
            RoleType.MeleeDPS,
            RoleType.PhysRangedDPS,
        };

        /// <summary>Applies the given composition template to a preset's slots.</summary>
        public static void ApplyComposition(PfPresetData preset, RoleType[] composition)
        {
            preset.Slots.Clear();
            for (int i = 0; i < composition.Length; i++)
            {
                preset.Slots.Add(new RoleSlot
                {
                    SlotIndex = i,
                    Role = composition[i],
                    AcceptedJobFlags = 0,
                });
            }
        }
    }
}
