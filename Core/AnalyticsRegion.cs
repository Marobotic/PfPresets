using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Dalamud.Plugin.Services;

namespace PfPresets
{
    /// <summary>
    /// Resolves the local player's home world to one of four coarse regions - NA, EU, JP, OCE -
    /// plus its data centre and world name, for the analytics ping, and derives a one-way
    /// anonymous per-character key so multiple characters on one install each count once instead
    /// of overwriting each other.
    ///
    /// Deliberately outside the PFP_RATINGS guard: usage analytics ships in every build
    /// configuration (see <see cref="AnalyticsClient"/>), while <see cref="WorldHelper"/>'s
    /// per-world lookups are part of the ratings feature and are not, so this does not depend on
    /// it and stays a tiny, self-contained lookup.
    ///
    /// THE DATA CENTRE AND WORLD TRAVEL WITH THE REGION - see CHANGELOG.md. This was written to
    /// resolve the region alone, on the reasoning that "Balmung" narrows a person down to one
    /// specific server's population in a way "NA" doesn't. That reasoning holds for a single
    /// install; it doesn't hold in aggregate, which is the only shape this data is ever shown in -
    /// the dashboard prints "20 characters on Gilgamesh" next to every other server, never a
    /// character next to a name. The character key that travels with this is still a one-way hash
    /// of nothing but a content id (see <see cref="ResolveCharacterKey"/>), so nothing here
    /// re-identifies which 20.
    ///
    /// Both lookups touch game state (the object table for the home world, the client state for
    /// the content id) and MUST be called from the framework thread - see AnalyticsClient, which
    /// is the only caller and hops there before calling in.
    /// </summary>
    internal static class AnalyticsRegion
    {
        /// <summary>One row per public world: its region bucket, its data centre's name and its
        /// own name. Built once and reused - see EnsureLoaded.</summary>
        private readonly record struct WorldInfo(string Region, string DataCenter, string World);

        private static Dictionary<uint, WorldInfo>? worldInfo;

        /// <summary>
        /// Mixed into the character key before hashing. Not a secret - it doesn't need to be, the
        /// hash is one-way either way - it exists so a raw content id run through plain SHA-256
        /// elsewhere can't be matched against this key. A per-install salt would prevent that too
        /// but would also stop the same character being recognised as the same character after a
        /// reinstall, which is the entire point of hashing the content id instead of a random
        /// value in the first place.
        /// </summary>
        private const string CharacterKeyPepper = "PfPresets-AnalyticsCharacterKey-v1";

        /// <summary>
        /// The local player's region ("NA", "EU", "JP" or "OCE"), data centre ("Aether",
        /// "Chaos", ...) and world ("Gilgamesh", "Balmung", ...) - all three, or all three null
        /// when nobody is logged in yet, the world id hasn't resolved, or the home world's data
        /// centre doesn't map to one of the four regions (the Chinese and Korean services, which
        /// this plugin does not run on).
        /// </summary>
        public static (string? Region, string? DataCenter, string? World) Resolve(
            IDataManager dataManager, IObjectTable objectTable, IPluginLog log)
        {
            try
            {
                uint worldId = objectTable.LocalPlayer?.HomeWorld.RowId ?? 0;
                if (worldId == 0)
                    return (null, null, null);

                EnsureLoaded(dataManager, log);
                return worldInfo != null && worldInfo.TryGetValue(worldId, out var info)
                    ? (info.Region, info.DataCenter, info.World)
                    : (null, null, null);
            }
            catch (Exception ex)
            {
                log.Debug($"[Analytics] Region lookup failed: {ex.Message}");
                return (null, null, null);
            }
        }

        /// <summary>
        /// A one-way, per-character anonymous key, or null when nobody is logged in.
        ///
        /// Built from the character's own content id - which this plugin does not otherwise touch
        /// or send anywhere - run through SHA-256 with a fixed pepper. That makes it stable for
        /// the same character across sessions and even a reinstall (so the server can recognise
        /// "this is the same alt reporting again" and count it once), while being mathematically
        /// irreversible back to the content id, and therefore to the account or character it
        /// names. It identifies nothing on its own; the server only ever stores a region next to
        /// it, never anything else - see AnalyticsClient's header.
        /// </summary>
        public static string? ResolveCharacterKey(IPlayerState playerState)
        {
            try
            {
                ulong contentId = playerState.ContentId;
                if (contentId == 0)
                    return null;

                byte[] input = Encoding.UTF8.GetBytes(
                    CharacterKeyPepper + contentId.ToString(CultureInfo.InvariantCulture));
                byte[] hash = SHA256.HashData(input);

                // 32 hex chars - the same shape as the install id, which keeps both fields
                // validated by the server with one regex and keeps the wire payload small.
                return Convert.ToHexString(hash, 0, 16).ToLowerInvariant();
            }
            catch (Exception)
            {
                // Nothing sensitive to log here; a failure just means this ping carries no key.
                return null;
            }
        }

        private static void EnsureLoaded(IDataManager dataManager, IPluginLog log)
        {
            if (worldInfo != null)
                return;

            var map = new Dictionary<uint, WorldInfo>();

            try
            {
                var sheet = dataManager.GetExcelSheet<Lumina.Excel.Sheets.World>();
                if (sheet == null)
                {
                    log.Debug("[Analytics] World sheet unavailable; region will be blank.");
                    return;
                }

                foreach (var row in sheet)
                {
                    // Non-public rows are internal/test worlds nobody can actually be on.
                    if (!row.IsPublic)
                        continue;

                    // Read from the data centre's WorldRegionGroup, NOT World.Region - that column
                    // reads 1 for every public world (Aether, Chaos, Materia and Elemental alike).
                    // The data centre's own Region row is the real thing:
                    // 1 Japan, 2 North America, 3 Europe, 4 Oceania, 5 China, 6 Korea, 7 NA Cloud.
                    var dataCenter = row.DataCenter.ValueNullable;
                    uint regionId = dataCenter?.Region.RowId ?? 0u;
                    string? region = regionId switch
                    {
                        1 => "JP",
                        2 => "NA",
                        3 => "EU",
                        4 => "OCE",
                        7 => "NA", // NA Cloud beta DC - same service as the rest of NA.
                        _ => null,
                    };

                    if (region == null)
                        continue;

                    string dcName = dataCenter?.Name.ToString() ?? string.Empty;
                    string worldName = row.Name.ToString();

                    if (dcName.Length == 0 || worldName.Length == 0)
                        continue;

                    map[row.RowId] = new WorldInfo(region, dcName, worldName);
                }
            }
            catch (Exception ex)
            {
                log.Debug($"[Analytics] Failed to read the World sheet: {ex.Message}");
            }

            worldInfo = map;
        }
    }
}
