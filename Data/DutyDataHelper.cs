using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using Lumina.Excel;

namespace PfPresets
{
    /// <summary>
    /// Provides duty name lookups and searchable duty lists using Lumina Excel data.
    /// Caches the full duty list on first access for fast repeated lookups.
    /// </summary>
    public class DutyDataHelper
    {
        private readonly IDataManager dataManager;
        private readonly IPluginLog pluginLog;

        private List<DutyEntry>? cachedDuties;
        private Dictionary<uint, DutyEntry>? dutyById;

        /// <summary>Instance territory row id -> duty, so the duty the player is currently inside can
        /// be identified from the client's TerritoryType when there's no listing to read it from.</summary>
        private Dictionary<uint, DutyEntry>? dutyByTerritory;

        /// <summary>
        /// Row ids at or above this are synthetic: entries we add for high-end duties missing from
        /// the ContentFinderCondition sheet. They are assigned in list order, so they can shift
        /// between game versions and must never be written to game memory or stored in a preset as
        /// a stable reference - the duty name stays authoritative for those.
        /// </summary>
        public const uint SyntheticRowIdStart = 1000000;

        /// <summary>True for the synthetic high-end entries described on <see cref="SyntheticRowIdStart"/>.</summary>
        public static bool IsSyntheticRowId(uint rowId) => rowId >= SyntheticRowIdStart;

        private static readonly string[] HighEndDutyNames = new string[]
        {
            "The Cloud of Darkness (Chaotic)",
            "Dancing Mad (Ultimate)",
            "Futures Rewritten (Ultimate)",
            "The Omega Protocol (Ultimate)",
            "Dragonsong's Reprise (Ultimate)",
            "The Epic of Alexander (Ultimate)",
            "The Weapon's Refrain (Ultimate)",
            "The Unending Coil of Bahamut (Ultimate)",
            "The Unmaking (Extreme)",
            "Shinryu's Domain (Unreal)",
            "AAC Heavyweight M4 (Savage)",
            "AAC Heavyweight M3 (Savage)",
            "AAC Heavyweight M2 (Savage)",
            "AAC Heavyweight M1 (Savage)"
        };

        public DutyDataHelper(IDataManager dataManager, IPluginLog pluginLog)
        {
            this.dataManager = dataManager;
            this.pluginLog = pluginLog;
        }

        /// <summary>
        /// Returns all known duties, grouped by content type.
        /// Results are cached after first call.
        /// </summary>
        public List<DutyEntry> GetAllDuties()
        {
            if (cachedDuties != null)
                return cachedDuties;

            cachedDuties = new List<DutyEntry>();
            dutyById = new Dictionary<uint, DutyEntry>();
            dutyByTerritory = new Dictionary<uint, DutyEntry>();

            try
            {
                var sheet = dataManager.GetExcelSheet<Lumina.Excel.Sheets.ContentFinderCondition>();
                if (sheet == null)
                {
                    pluginLog.Warning("ContentFinderCondition sheet not found.");
                    return cachedDuties;
                }

                foreach (var row in sheet)
                {
                    string name = row.Name.ToString();
                    if (string.IsNullOrWhiteSpace(name))
                        continue;

                    var entry = new DutyEntry
                    {
                        RowId = row.RowId,
                        Name = name,
                        ContentTypeId = row.ContentType.RowId,
                        ContentTypeName = row.ContentType.Value.Name.ToString(),
                        ExVersionId = row.RequiredExVersion.RowId,
                        ClassJobLevelRequired = row.ClassJobLevelRequired,
                        ItemLevelRequired = row.ItemLevelRequired,
                    };

                    cachedDuties.Add(entry);
                    dutyById[row.RowId] = entry;

                    // Map the duty's instance territory back to it. First non-zero territory wins:
                    // a handful of rows share one (e.g. a fight and its unreal re-run), and the
                    // first is as good a guess as any for "what am I standing in".
                    uint territoryId = row.TerritoryType.RowId;
                    if (territoryId != 0 && !dutyByTerritory.ContainsKey(territoryId))
                        dutyByTerritory[territoryId] = entry;
                }

                // Add missing High-end duties to cachedDuties for lookup & search consistency
                uint mockIdStart = SyntheticRowIdStart;
                foreach (var name in HighEndDutyNames)
                {
                    if (!cachedDuties.Any(d => d.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                    {
                        var entry = new DutyEntry
                        {
                            RowId = mockIdStart++,
                            Name = name,
                            ContentTypeId = 6,
                            ContentTypeName = "High-end Duty",
                            ClassJobLevelRequired = 100,
                            ItemLevelRequired = 0
                        };
                        cachedDuties.Add(entry);
                        dutyById[entry.RowId] = entry;
                    }
                }

                // Sort alphabetically within each content type (excluding our ordered High-end Duty type which is handled in GetDutiesByType)
                cachedDuties = cachedDuties
                    .OrderBy(d => d.ContentTypeName)
                    .ThenBy(d => d.Name)
                    .ToList();

                pluginLog.Information($"Loaded {cachedDuties.Count} duties from Lumina data.");
            }
            catch (Exception ex)
            {
                pluginLog.Error(ex, "Failed to load duty data from Lumina.");
            }

            return cachedDuties;
        }

        /// <summary>Looks up a duty name by its ContentFinderCondition row ID.</summary>
        public string GetDutyName(uint rowId)
        {
            if (rowId == 0)
                return "None";

            EnsureLoaded();
            if (dutyById != null && dutyById.TryGetValue(rowId, out var entry))
                return entry.Name;

            return $"Unknown ({rowId})";
        }

        /// <summary>Looks up a full DutyEntry by row ID.</summary>
        public DutyEntry? GetDutyEntry(uint rowId)
        {
            EnsureLoaded();
            if (dutyById != null && dutyById.TryGetValue(rowId, out var entry))
                return entry;
            return null;
        }

        /// <summary>The duty whose instance territory matches, or null. Identifies the duty the
        /// player is currently inside from the client's TerritoryType, which the idle status
        /// snapshot has no listing to read the duty from.</summary>
        public DutyEntry? GetDutyByTerritoryType(uint territoryTypeRowId)
        {
            if (territoryTypeRowId == 0)
                return null;

            EnsureLoaded();
            if (dutyByTerritory != null && dutyByTerritory.TryGetValue(territoryTypeRowId, out var entry))
                return entry;
            return null;
        }

        /// <summary>
        /// The duty with exactly this name, or null. Used to recover a duty from a name the game
        /// gave us as text rather than as a row id - the Duty Finder does not expose the condition
        /// id of a queue that hasn't popped yet, but it does put the name on screen.
        /// </summary>
        public DutyEntry? GetDutyByExactName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return null;

            EnsureLoaded();
            if (cachedDuties == null)
                return null;

            string trimmed = name.Trim();
            foreach (var entry in cachedDuties)
            {
                if (entry.Name.Equals(trimmed, StringComparison.OrdinalIgnoreCase))
                    return entry;
            }
            return null;
        }

        /// <summary>
        /// The display name of a Duty Roulette ("Duty Roulette: Expert", "Frontline"), or empty.
        ///
        /// Deliberately separate from the duty lookups: a roulette is not a duty. Which fight it
        /// rolls isn't decided until it pops, so it never has a row id and never carries a prog
        /// point - it only needs a name to show.
        /// </summary>
        public string GetRouletteName(uint rouletteId)
        {
            if (rouletteId == 0)
                return string.Empty;

            try
            {
                var sheet = dataManager.GetExcelSheet<Lumina.Excel.Sheets.ContentRoulette>();
                var row = sheet?.GetRowOrDefault(rouletteId);
                return row?.Name.ToString() ?? string.Empty;
            }
            catch (Exception ex)
            {
                pluginLog.Debug($"Roulette name lookup failed for {rouletteId}: {ex.Message}");
                return string.Empty;
            }
        }

        /// <summary>
        /// Searches duties by name substring (case-insensitive).
        /// Returns up to maxResults matches.
        /// </summary>
        public List<DutyEntry> SearchDuties(string query, int maxResults = 20)
        {
            EnsureLoaded();

            if (string.IsNullOrWhiteSpace(query) || cachedDuties == null)
                return cachedDuties?.Take(maxResults).ToList() ?? new List<DutyEntry>();

            string lowerQuery = query.ToLowerInvariant();
            return cachedDuties
                .Where(d => d.Name.ToLowerInvariant().Contains(lowerQuery))
                .Take(maxResults)
                .ToList();
        }

        /// <summary>Returns all distinct content type names for category filtering.</summary>
        public List<string> GetContentTypes()
        {
            EnsureLoaded();
            if (cachedDuties == null)
                return new List<string>();

            return cachedDuties
                .Select(d => d.ContentTypeName)
                .Where(n => !string.IsNullOrEmpty(n))
                .Distinct()
                .OrderBy(n => n)
                .ToList();
        }

        /// <summary>Returns duties filtered by content type name.</summary>
        public List<DutyEntry> GetDutiesByType(string contentTypeName)
        {
            EnsureLoaded();
            if (cachedDuties == null)
                return new List<DutyEntry>();

            if (contentTypeName == "High-end Duty")
            {
                var list = new List<DutyEntry>();
                foreach (var name in HighEndDutyNames)
                {
                    var match = cachedDuties.FirstOrDefault(d => d.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                    if (match != null)
                    {
                        list.Add(match);
                    }
                }
                return list;
            }

            return cachedDuties
                .Where(d => d.ContentTypeName == contentTypeName)
                .ToList();
        }

        /// <summary>
        /// Finds a duty by display name within a content-type category, exactly first and then by
        /// substring either way round. Returns null when nothing matches - callers must treat that
        /// as "no duty" rather than guessing, so a renamed duty never posts the wrong listing.
        /// </summary>
        public DutyEntry? FindDutyByName(string contentTypeName, string dutyName)
        {
            if (string.IsNullOrWhiteSpace(dutyName))
                return null;

            var duties = GetDutiesByType(contentTypeName);
            if (duties.Count == 0)
                return null;

            return duties.FirstOrDefault(d => d.Name.Equals(dutyName, StringComparison.OrdinalIgnoreCase))
                ?? duties.FirstOrDefault(d => d.Name.Contains(dutyName, StringComparison.OrdinalIgnoreCase))
                ?? duties.FirstOrDefault(d => dutyName.Contains(d.Name, StringComparison.OrdinalIgnoreCase));
        }

        private void EnsureLoaded()
        {
            if (cachedDuties == null)
                GetAllDuties();
        }
    }

    /// <summary>
    /// Represents a single duty entry from the ContentFinderCondition Excel sheet.
    /// </summary>
    public class DutyEntry
    {
        public uint RowId { get; set; }
        public string Name { get; set; } = string.Empty;
        public uint ContentTypeId { get; set; }
        public string ContentTypeName { get; set; } = string.Empty;

        /// <summary>Expansion the duty belongs to, as an ExVersion row id: 0 is A Realm Reborn and
        /// the highest present in the sheet is the current one. Used to tell current content from
        /// everything that came before without hardcoding an expansion name.</summary>
        public uint ExVersionId { get; set; }
        public int ClassJobLevelRequired { get; set; }
        public int ItemLevelRequired { get; set; }
    }
}
