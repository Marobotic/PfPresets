namespace PfPresets
{
    /// <summary>
    /// Human-readable names for the numeric/enum values stored in presets.
    /// </summary>
    public static class DisplayNames
    {
        public static string GetObjectiveName(int objectiveId) => objectiveId switch
        {
            0 => "None",
            1 => "Duty Completion",
            2 => "Practice",
            3 => "Loot",
            _ => $"Unknown ({objectiveId})",
        };

        public static string GetLootRuleName(int lootRules) => lootRules switch
        {
            0 => "Normal",
            1 => "Greed Only",
            2 => "Lootmaster",
            _ => $"Unknown ({lootRules})",
        };

        public static string GetCompletionStatusName(int type) => type switch
        {
            0 => "Duty Complete",
            1 => "Duty Complete (Weekly)",
            2 => "Duty Incomplete",
            _ => $"Unknown ({type})",
        };

        public static string GetRoleName(RoleType role) => role switch
        {
            RoleType.Free => "Free",
            RoleType.Tank => "Tank",
            RoleType.Healer => "Healer",
            RoleType.MeleeDPS => "Melee DPS",
            RoleType.PhysRangedDPS => "Physical Ranged DPS",
            RoleType.MagicRangedDPS => "Magical Ranged DPS",
            RoleType.Omit => "Omit",
            _ => "Unknown",
        };

        public static string GetCategoryName(JobCategory category) => category switch
        {
            JobCategory.Tank => "Tank",
            JobCategory.PureHealer => "Pure Healer",
            JobCategory.BarrierHealer => "Barrier Healer",
            JobCategory.MeleeDPS => "Melee DPS",
            JobCategory.PhysRangedDPS => "Physical Ranged DPS",
            JobCategory.MagicRangedDPS => "Magical Ranged DPS",
            _ => category.ToString(),
        };
    }
}
