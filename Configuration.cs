using System;
using System.Collections.Generic;
using Dalamud.Configuration;
using Dalamud.Plugin;
using Newtonsoft.Json;

namespace PfPresets
{
    [Serializable]
    public class Configuration : IPluginConfiguration
    {
        public int Version { get; set; } = 0;
        public string PluginVersion { get; set; } = "1.0.4";

        // ── Preset Storage ────────────────────────────────────────
        public List<PfPresetData> Presets { get; set; } = new();

        // ── UI Preferences ────────────────────────────────────────
        public int PanelWidth { get; set; } = 420;
        public int PanelHeight { get; set; } = 520;
        public string LastSelectedPresetId { get; set; } = string.Empty;

        // ── Default Values for New Presets ─────────────────────────
        public bool AutoRefresherEnabled { get; set; } = false;

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
