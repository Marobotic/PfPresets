using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Configuration;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace PfPresets
{
    [Serializable]
    public class Configuration : IPluginConfiguration
    {
        /// <summary>Schema version of the saved config. See <see cref="Migrate"/>.</summary>
        public int Version { get; set; } = 0;

        /// <summary>The schema this build writes. Bump alongside a new case in <see cref="Migrate"/>.</summary>
        public const int CurrentVersion = 1;

        // ── Preset Storage ────────────────────────────────────────
        public List<PfPresetData> Presets { get; set; } = new();

        // ── UI Preferences ────────────────────────────────────────
        public int PanelWidth { get; set; } = 420;
        public int PanelHeight { get; set; } = 520;

        // ── Auto Refresher ────────────────────────────────────────
        public bool AutoRefresherEnabled { get; set; } = false;

        /// <summary>How often the Auto Refresher re-posts the listing, in minutes. Free-form since
        /// 3.0.0.1 (was 15 or 30); clamped to <see cref="PfAutomation.MinRefreshMinutes"/>..
        /// <see cref="PfAutomation.MaxRefreshMinutes"/> when read.</summary>
        public int AutoRefresherIntervalMinutes { get; set; } = 30;

        /// <summary>How many hours the Auto Refresher keeps re-posting before it stops on its own.
        /// 0 means no limit. The listing is never cancelled - it just stops being renewed and
        /// expires the way an unattended one normally would.</summary>
        public int AutoRefresherMaxHours { get; set; } = 0;

        /// <summary>When enabled, a party member leaving while you recruit as leader broadens any
        /// slot locked to a single job to that job's role, so the freed seat is easier to fill.</summary>
        public bool AutoAdjustLockedJobsEnabled { get; set; } = false;

        [NonSerialized]
        private IDalamudPluginInterface? pluginInterface;

        public void Initialize(IDalamudPluginInterface pi)
        {
            this.pluginInterface = pi;
        }

        public void Save()
        {
            this.pluginInterface?.SavePluginConfig(this);
        }

        // ── Migration ─────────────────────────────────────────────

        /// <summary>
        /// Brings a config saved by an older build up to <see cref="CurrentVersion"/>. Called once at
        /// startup, after the duty data is available. Saves only when something actually changed, so
        /// a current config costs nothing.
        /// </summary>
        public void Migrate(DutyDataHelper dutyDataHelper, IPluginLog log)
        {
            if (Version >= CurrentVersion)
                return;

            int startVersion = Version;

            // v0 -> v1: presets referenced their duty by name only. Back-fill the ContentFinderCondition
            // row id so applying a preset no longer depends on matching display strings.
            if (Version < 1)
            {
                int resolved = 0, unresolved = 0;
                foreach (var preset in Presets)
                {
                    if (preset.DutyRowId != 0 || preset.DutyCategoryId == 0)
                        continue;

                    var duty = dutyDataHelper.FindDutyByName(preset.DutyCategoryName, preset.DutyName);
                    if (duty == null)
                    {
                        unresolved++;
                        continue;
                    }

                    // Synthetic entries have unstable ids, so those keep using the name.
                    if (!DutyDataHelper.IsSyntheticRowId(duty.RowId))
                    {
                        preset.DutyRowId = duty.RowId;
                        resolved++;
                    }
                }
                log.Information($"[Migration] v0 -> v1: resolved {resolved} preset duty id(s), {unresolved} left on name lookup.");
                Version = 1;
            }

            log.Information($"[Migration] Configuration upgraded from v{startVersion} to v{Version}.");
            Save();
        }

        // ── CRUD Operations ───────────────────────────────────────

        public PfPresetData AddPreset(string? name = null)
        {
            var preset = new PfPresetData
            {
                Name = name ?? $"Preset {Presets.Count + 1}",
                LangJapanese = true,
                LangEnglish = true,
                LangGerman = true,
                LangFrench = true,
            };
            Presets.Add(preset);
            Save();
            return preset;
        }

        /// <summary>Adds a preset that came from a share code, giving it a name that doesn't collide
        /// with an existing one. The caller has already validated and sanitized it.</summary>
        public PfPresetData AddImportedPreset(PfPresetData preset)
        {
            string baseName = preset.Name;
            int suffix = 2;
            while (Presets.Any(p => p.Name.Equals(preset.Name, StringComparison.OrdinalIgnoreCase)))
                preset.Name = $"{baseName} ({suffix++})";

            Presets.Add(preset);
            Save();
            return preset;
        }

        /// <summary>Finds a preset by name for the <c>/pfp apply</c> command: an exact
        /// case-insensitive match wins, otherwise a unique prefix or substring match. Returns null
        /// when nothing matches or when the name is ambiguous (<paramref name="ambiguous"/> tells
        /// the caller which it was).</summary>
        public PfPresetData? FindPresetByName(string name, out bool ambiguous)
        {
            ambiguous = false;
            if (string.IsNullOrWhiteSpace(name))
                return null;

            string query = name.Trim();

            var exact = Presets.Where(p => p.Name.Equals(query, StringComparison.OrdinalIgnoreCase)).ToList();
            if (exact.Count > 0)
                return exact[0];

            foreach (var candidates in new[]
            {
                Presets.Where(p => p.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase)).ToList(),
                Presets.Where(p => p.Name.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList(),
            })
            {
                if (candidates.Count == 1)
                    return candidates[0];
                if (candidates.Count > 1)
                {
                    ambiguous = true;
                    return null;
                }
            }

            return null;
        }

        public bool UpdatePreset(PfPresetData updated)
        {
            int idx = Presets.FindIndex(p => p.Id == updated.Id);
            if (idx < 0)
                return false;

            Presets[idx] = updated;
            Save();
            return true;
        }

        public PfPresetData? DuplicatePreset(string id)
        {
            var original = Presets.Find(p => p.Id == id);
            if (original == null)
                return null;

            var copy = original.Duplicate();
            Presets.Add(copy);
            Save();
            return copy;
        }

        public bool DeletePreset(string id)
        {
            int removed = Presets.RemoveAll(p => p.Id == id);
            if (removed > 0)
            {
                Save();
                return true;
            }
            return false;
        }

        public PfPresetData? GetPreset(string id)
        {
            return Presets.Find(p => p.Id == id);
        }

        public void MarkPresetUsed(string id)
        {
            var preset = Presets.Find(p => p.Id == id);
            if (preset != null)
            {
                preset.LastUsedAt = DateTime.UtcNow;
                Save();
            }
        }

        /// <summary>Moves a preset up in the list order.</summary>
        public void MovePresetUp(string id)
        {
            int idx = Presets.FindIndex(p => p.Id == id);
            if (idx > 0)
            {
                (Presets[idx], Presets[idx - 1]) = (Presets[idx - 1], Presets[idx]);
                Save();
            }
        }

        /// <summary>Moves a preset down in the list order.</summary>
        public void MovePresetDown(string id)
        {
            int idx = Presets.FindIndex(p => p.Id == id);
            if (idx >= 0 && idx < Presets.Count - 1)
            {
                (Presets[idx], Presets[idx + 1]) = (Presets[idx + 1], Presets[idx]);
                Save();
            }
        }
    }
}
