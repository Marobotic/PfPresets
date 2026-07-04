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

        // ── Duty ──────────────────────────────────────────────────
        /// <summary>Duty category index matching the in-game dropdown; see
        /// <see cref="DutyCategories.Names"/> (0 = None).</summary>
        public int DutyCategoryId { get; set; } = 0;

        /// <summary>Cached display name for the duty (so we don't need Lumina to show it).</summary>
        public string DutyName { get; set; } = "None";

        /// <summary>Cached display name for the duty category.</summary>
        public string DutyCategoryName { get; set; } = "None";

        // ── Objective ─────────────────────────────────────────────
        /// <summary>0=None, 1=Duty Completion, 2=Practice, 3=Loot</summary>
        public int ObjectiveId { get; set; } = 0;

        // ── Comment ───────────────────────────────────────────────
        /// <summary>Free-text comment, max 191 characters (192-byte buffer in game memory).</summary>
        public string Comment { get; set; } = string.Empty;

        // ── Role Slots ────────────────────────────────────────────
        /// <summary>The 8 role slots of a normal party.</summary>
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
        public bool OnePlayerPerJob { get; set; } = false;

        // ── Search Area ───────────────────────────────────────────
        public bool LimitRecruitingToWorld { get; set; } = false;
        public bool FormPrivateParty { get; set; } = false;

        /// <summary>Numeric password 0-9999. Always displayed as 4 zero-padded digits (e.g. 0001, 0056).</summary>
        public string PrivatePartyPassword { get; set; } = string.Empty;

        /// <summary>Returns the password as a 4-digit zero-padded string.</summary>
        [JsonIgnore]
        public string PasswordDisplay => int.TryParse(PrivatePartyPassword, out int val) ? val.ToString("D4") : "0000";

        // ── Conditions ────────────────────────────────────────────
        public bool CompletionStatusEnabled { get; set; } = false;

        /// <summary>0 = Duty Complete, 1 = Duty Complete (Weekly Reward Unclaimed), 2 = Duty Incomplete</summary>
        public int CompletionStatusType { get; set; } = 0;

        public bool AvgItemLvEnabled { get; set; } = false;
        public int AvgItemLv { get; set; } = 1;

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
        /// Each bit corresponds to a job's <see cref="JobInfo.BitIndex"/>.
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
        Omit = 6,
    }
}
