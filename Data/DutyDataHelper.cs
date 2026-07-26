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
                        ClassJobLevelRequired = row.ClassJobLevelRequired,
                        ItemLevelRequired = row.ItemLevelRequired,
                    };

                    cachedDuties.Add(entry);
                    dutyById[row.RowId] = entry;
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
        public int ClassJobLevelRequired { get; set; }
        public int ItemLevelRequired { get; set; }
    }
}
