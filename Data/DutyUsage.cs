using System;

namespace PfPresets
{
    /// <summary>
    /// How often a preset for one duty has been applied on this install.
    ///
    /// Counted per duty rather than per preset: three presets for the same fight are three ways of
    /// asking the same question, and what is worth knowing is which content people bring the plugin
    /// to - not how they organise their own list.
    /// </summary>
    [Serializable]
    public class DutyUsage
    {
        /// <summary>ContentFinderCondition row, or 0 for duties that only have a name - synthetic
        /// entries and anything saved before ids were recorded.</summary>
        public uint DutyRowId { get; set; }

        /// <summary>The duty as the game names it, kept so the list reads without a lookup and
        /// still says something when a row id stops resolving after a patch.</summary>
        public string DutyName { get; set; } = string.Empty;

        /// <summary>The category the duty sits under in the Party Finder ("High-end Duty",
        /// "Raids"…). This is the part worth aggregating - a single fight is a fashion, a category
        /// is what the plugin is for.</summary>
        public string CategoryName { get; set; } = string.Empty;

        public int Applied { get; set; }

        public DateTime LastAppliedUtc { get; set; }

        /// <summary>Duties with no id are matched on name, so both routes need a stable key.</summary>
        public string Key => DutyRowId != 0
            ? $"#{DutyRowId}"
            : DutyName.Trim().ToLowerInvariant();
    }
}
