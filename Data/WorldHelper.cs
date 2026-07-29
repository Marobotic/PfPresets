#if PFP_RATINGS
using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;

namespace PfPresets
{
    /// <summary>
    /// Resolves world row ids to world names, and world names to the region slug the external
    /// character sites use in their URLs.
    ///
    /// The party proxies report a home world as a numeric row id, but every part of the rating
    /// system - the cache key, the API, what the user types into the search box - speaks
    /// "name@world", so this sits between the two. Lookups are cached on first use; the World
    /// sheet is small and never changes at runtime.
    /// </summary>
    public sealed class WorldHelper
    {
        private readonly IDataManager dataManager;
        private readonly IPluginLog log;

        private Dictionary<uint, string>? idToName;
        private Dictionary<string, uint>? nameToRegion;

        public WorldHelper(IDataManager dataManager, IPluginLog log)
        {
            this.dataManager = dataManager;
            this.log = log;
        }

        /// <summary>The world's name, or empty when the id is unknown (which happens transiently
        /// during zone changes, and for private/dev worlds).</summary>
        public string GetWorldName(uint worldId)
        {
            EnsureLoaded();
            return idToName != null && idToName.TryGetValue(worldId, out var name) ? name : string.Empty;
        }

        /// <summary>
        /// The region slug FFLogs uses in a character URL, or null when FFLogs has no region for
        /// that world.
        ///
        /// Read from the world's data centre, NOT from World.Region. That column reads 1 for every
        /// public world - Aether, Chaos, Materia and Elemental alike - so mapping it to a region
        /// sent every character link to /jp/ regardless of where they actually play. The data
        /// centre's WorldRegionGroup row is the real thing: 1 Japan, 2 North America, 3 Europe,
        /// 4 Oceania, 5 China, 6 Korea, 7 NA Cloud.
        ///
        /// Null rather than a guess for the Chinese and Korean services, which FFLogs does not
        /// cover at all: a link that is certain to 404 is worse than no link, because the user
        /// cannot tell it apart from the character genuinely having no logs.
        /// </summary>
        public string? GetFfLogsRegion(string worldName)
        {
            EnsureLoaded();

            if (nameToRegion == null || string.IsNullOrWhiteSpace(worldName))
                return null;

            if (!nameToRegion.TryGetValue(worldName.Trim().ToLowerInvariant(), out uint region))
                return null;

            return region switch
            {
                1 => "jp",
                2 => "na",
                3 => "eu",
                4 => "oc",

                // The NA Cloud beta DC is the same service to FFLogs as the rest of NA.
                7 => "na",

                _ => null,
            };
        }

        /// <summary>True when a name matches a real public world, used to reject typos in the
        /// search box before they turn into a pointless request.</summary>
        public bool IsKnownWorld(string worldName)
        {
            EnsureLoaded();
            return nameToRegion != null
                && !string.IsNullOrWhiteSpace(worldName)
                && nameToRegion.ContainsKey(worldName.Trim().ToLowerInvariant());
        }

        /// <summary>Every public world name, for the search box's autocomplete.</summary>
        public IReadOnlyCollection<string> AllWorldNames()
        {
            EnsureLoaded();
            return idToName?.Values ?? (IReadOnlyCollection<string>)Array.Empty<string>();
        }

        private void EnsureLoaded()
        {
            if (idToName != null)
                return;

            idToName = new Dictionary<uint, string>();
            nameToRegion = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);

            try
            {
                var sheet = dataManager.GetExcelSheet<Lumina.Excel.Sheets.World>();
                if (sheet == null)
                {
                    log.Debug("[Ratings] World sheet unavailable; world names will be blank.");
                    return;
                }

                foreach (var row in sheet)
                {
                    // Non-public rows are internal/test worlds nobody can actually be on.
                    if (!row.IsPublic)
                        continue;

                    string name = row.Name.ToString();
                    if (string.IsNullOrWhiteSpace(name))
                        continue;

                    idToName[row.RowId] = name;

                    // The data centre's region, not row.Region - see GetFfLogsRegion. Worlds whose
                    // data centre reference doesn't resolve keep 0, which maps to "no FFLogs".
                    nameToRegion[name.ToLowerInvariant()] =
                        row.DataCenter.ValueNullable?.Region.RowId ?? 0u;
                }
            }
            catch (Exception ex)
            {
                log.Debug($"[Ratings] Failed to read the World sheet: {ex.Message}");
            }
        }
    }
}
#endif
