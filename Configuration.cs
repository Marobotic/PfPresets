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

        /// <summary>The schema this build writes. Bump alongside a new case in <see cref="Migrate"/>.
        /// Deliberately the same number in the ratings and non-ratings builds: the v2 fields are
        /// inert data, so a config written by one build must load cleanly in the other.</summary>
        public const int CurrentVersion = 2;

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

        // ── Anonymous usage stats ─────────────────────────────────

        /// <summary>Whether to send anonymous usage counts. See <see cref="AnalyticsInstallId"/>
        /// for exactly what that covers.</summary>
        public bool AnalyticsEnabled { get; set; } = true;

        /// <summary>
        /// A random id generated once on this machine, used only to avoid counting the same install
        /// twice. It is not derived from anything - not your character, account, world or hardware -
        /// so it identifies nothing beyond "one copy of the plugin". Clearing it makes this install
        /// count as new.
        /// </summary>
        public string AnalyticsInstallId { get; set; } = string.Empty;

        /// <summary>Presets created on this install, ever. Not reduced by deleting presets, so the
        /// total reflects use rather than what happens to be saved right now.</summary>
        public int LifetimePresetsCreated { get; set; } = 0;

        /// <summary>Presets applied on this install, ever.</summary>
        public int LifetimePresetsApplied { get; set; } = 0;

        /// <summary>When enabled, a party member leaving while you recruit as leader broadens any
        /// slot locked to a single job to that job's role, so the freed seat is easier to fill.</summary>
        public bool AutoAdjustLockedJobsEnabled { get; set; } = false;

        /// <summary>
        /// How much of another player's name to draw, everywhere the plugin shows one - the
        /// recruitment card, the party panel, ratings, recent players and profiles.
        ///
        /// Display only: the full name is still what gets looked up, stored and sent. Lives outside
        /// the ratings block because the recruitment card shows names in either build.
        /// </summary>
        public PlayerNameStyle PlayerNameStyle { get; set; } = PlayerNameStyle.FullName;

#if PFP_RATINGS
        // ── Community ratings ─────────────────────────────────────

        /// <summary>
        /// Master switch for every rating feature. Opt-in, not opt-out: the plugin does not look
        /// up, record or send anything about other players until this is deliberately turned on,
        /// because the feature involves data about people who never installed it.
        /// </summary>
        public bool RatingsEnabled { get; set; } = true;

        /// <summary>Whether to offer the rating prompt after a duty finishes.</summary>
        public bool PostDutyPromptEnabled { get; set; } = true;

        /// <summary>Whether to show ratings beside the members of the party you are actually in.
        /// Deliberately not offered while browsing the Party Finder: a score attached to a listing
        /// you haven't joined turns the feature into a screening tool for strangers, which is not
        /// what it is for.</summary>
        public bool PartyRatingsEnabled { get; set; } = true;

        /// <summary>Whether to keep the local log of players met in duties. Turning this off
        /// empties the rateable list and with it the ability to rate anyone, since rating is gated
        /// on having actually played with the person. Lookup still works.</summary>
        public bool TrackEncounters { get; set; } = true;

        /// <summary>
        /// Override for the rating server, for development and for anyone self-hosting. Empty
        /// means the built-in endpoint.
        ///
        /// Read once when the client is constructed, so a change needs a plugin reload to take
        /// effect. Whatever is set here receives your character name and the names you look up, so
        /// it should only ever point somewhere you control.
        /// </summary>
        public string RatingApiBaseUrl { get; set; } = string.Empty;

        /// <summary>
        /// The last time this install rated each player, keyed by the canonical "name@world".
        /// Drives the instant client-side half of the 24h cooldown; the server enforces the real
        /// one. Entries are pruned once expired, so this stays small.
        /// </summary>
        public Dictionary<string, DateTime> LocalCooldowns { get; set; } = new();

        /// <summary>
        /// When this install last reported each player, keyed by the canonical "name@world".
        ///
        /// Serves two limits at once: an entry inside the repeat window blocks reporting the same
        /// person again, and the number of entries inside the last hour is how many reports have
        /// been filed in that hour. Both are client-side courtesy checks - the server enforces its
        /// own, and is authoritative if the two disagree.
        /// </summary>
        public Dictionary<string, DateTime> ReportCooldowns { get; set; } = new();
#endif

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

            // v1 -> v2: added the community rating settings. Every new field has a usable default
            // and Newtonsoft fills them in on load, so there is nothing to convert - this only
            // repairs a collection that an older hand-edited config could have left null.
            if (Version < 2)
            {
#if PFP_RATINGS
                LocalCooldowns ??= new Dictionary<string, DateTime>();
#endif
                Version = 2;
            }

            // v2 -> v3: added the report cooldown list. Same shape as the v1 -> v2 step - the field
            // has a usable default, so this only repairs a config that predates it or that a hand
            // edit left null.
            if (Version < 3)
            {
#if PFP_RATINGS
                ReportCooldowns ??= new Dictionary<string, DateTime>();
#endif
                Version = 3;
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
            LifetimePresetsCreated++;
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
            LifetimePresetsCreated++;
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
            LifetimePresetsCreated++;
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
                LifetimePresetsApplied++;
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
