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

        /// <summary>The roulettes, in the game's own display order. Kept apart from the duty list
        /// because they are sorted by the sheet's SortKey rather than alphabetically.</summary>
        private List<DutyEntry>? cachedRoulettes;

        /// <summary>Crystalline Conflict, as the single entry the game offers it as. It lives in
        /// ContentRoulette rather than with the maps it rolls, which is exactly why the maps had to
        /// be dropped from the PvP list.</summary>
        private List<DutyEntry>? cachedPvpQueues;

        /// <summary>"All locations" and the field zones, for the FATEs category.</summary>
        private List<DutyEntry>? cachedFateZones;

        /// <summary>The four deep dungeons, as the game offers them - one entry each, not one per
        /// floor set. See <see cref="LoadDeepDungeons"/>.</summary>
        private List<DutyEntry>? cachedDeepDungeons;

        /// <summary>"All Levels", then the treasure maps. See <see cref="LoadTreasureMaps"/>.</summary>
        private List<DutyEntry>? cachedTreasureMaps;

        /// <summary>The Gold Saucer's fifteen, from three different places. See
        /// <see cref="LoadGoldSaucer"/>.</summary>
        private List<DutyEntry>? cachedGoldSaucer;

        /// <summary>
        /// Row ids at or above this are synthetic: entries we add for high-end duties missing from
        /// the ContentFinderCondition sheet. They are assigned in list order, so they can shift
        /// between game versions and must never be written to game memory or stored in a preset as
        /// a stable reference - the duty name stays authoritative for those.
        /// </summary>
        public const uint SyntheticRowIdStart = 1000000;

        /// <summary>True for the synthetic high-end entries described on <see cref="SyntheticRowIdStart"/>.</summary>
        public static bool IsSyntheticRowId(uint rowId) => rowId >= SyntheticRowIdStart;

        /// <summary>
        /// Roulettes live in their own row-id space, offset up here so they cannot collide with the
        /// duties.
        ///
        /// A roulette is not a ContentFinderCondition - it is a ContentRoulette, numbered from 1,
        /// and ContentFinderCondition rows 1 to 17 are real dungeons. One id space had to give, and
        /// this is the one with a translation step at the single point it reaches the game.
        /// Deliberately BELOW <see cref="SyntheticRowIdStart"/>: a roulette id is real and does get
        /// written, it just gets subtracted first - see PfAutomation.WriteDutyIdToMemory.
        /// </summary>
        public const uint RouletteRowIdStart = 500000;

        /// <summary>
        /// FATE locations, in their own space again - these are TerritoryType rows, which collide
        /// with ContentFinderCondition just as thoroughly as the roulettes do.
        /// </summary>
        public const uint FateZoneRowIdStart = 600000;

        /// <summary>
        /// The deep dungeons, in their own space for the same reason as the two above: these are
        /// DeepDungeon rows, numbered from 1, and 1 to 4 are real dungeons in
        /// ContentFinderCondition.
        /// </summary>
        public const uint DeepDungeonRowIdStart = 700000;

        /// <summary>
        /// The treasure maps, in their own space again. These are TreasureHuntRank rows, numbered
        /// from 1, and 1 to 30 are real dungeons in ContentFinderCondition.
        /// </summary>
        public const uint TreasureMapRowIdStart = 800000;

        /// <summary>
        /// The Gold Saucer's entries, in their own space. They come from three sheets and one of
        /// them is not a sheet at all, so there is no single row space they could share.
        /// </summary>
        public const uint GoldSaucerRowIdStart = 900000;

        /// <summary>True for a roulette, per <see cref="RouletteRowIdStart"/>.</summary>
        public static bool IsRouletteRowId(uint rowId) =>
            rowId >= RouletteRowIdStart && rowId < FateZoneRowIdStart;

        /// <summary>True for a FATE location, per <see cref="FateZoneRowIdStart"/>.</summary>
        public static bool IsFateZoneRowId(uint rowId) =>
            rowId >= FateZoneRowIdStart && rowId < DeepDungeonRowIdStart;

        /// <summary>True for a deep dungeon, per <see cref="DeepDungeonRowIdStart"/>.</summary>
        public static bool IsDeepDungeonRowId(uint rowId) =>
            rowId >= DeepDungeonRowIdStart && rowId < TreasureMapRowIdStart;

        /// <summary>True for a treasure map, per <see cref="TreasureMapRowIdStart"/>.</summary>
        public static bool IsTreasureMapRowId(uint rowId) =>
            rowId >= TreasureMapRowIdStart && rowId < GoldSaucerRowIdStart;

        /// <summary>True for a Gold Saucer entry, per <see cref="GoldSaucerRowIdStart"/>.</summary>
        public static bool IsGoldSaucerRowId(uint rowId) =>
            rowId >= GoldSaucerRowIdStart && rowId < SyntheticRowIdStart;

        /// <summary>
        /// The number the game wants in its SelectedDutyId field for this entry, or 0 for "any duty
        /// in the category".
        ///
        /// One field, four id spaces, and which one applies is said by the byte beside it rather
        /// than by the category - see <see cref="SpecificDutyFlag"/>. This is the single place the
        /// plugin's own offsets come back off.
        ///
        /// ALWAYS A NUMBER NOW. It used to be able to answer "I cannot encode this - leave whatever
        /// the client has", which sounded careful and was not: the client's field is not updated by
        /// the selection this plugin makes, so what it holds is the duty the LAST listing used. Two
        /// categories shipped that way and both silently posted the previous listing's duty. Zero -
        /// the whole category - is the honest version of not knowing, and every id space is now
        /// known anyway.
        /// </summary>
        public static ushort GameDutyId(DutyEntry duty)
        {
            if (IsSyntheticRowId(duty.RowId))
                return 0;

            // A FATE LOCATION SENDS ITS TERRITORY, which is the one thing nobody tried.
            //
            // This used to send nothing, on the reasoning that a TerritoryType id in the duty field
            // would be read as a ContentFinderCondition id and file the listing under some dungeon.
            // The client says otherwise: set a zone by hand and the field holds the TerritoryType
            // row itself - 134 for Middle La Noscea, 1192 for Living Memory, the first and last of
            // the forty-eight. Like the deep dungeons and the treasure maps, this category numbers
            // its own content, and the byte beside it stays 0 to say so.
            //
            // "All locations" carries the base of the band, so this arithmetic gives it 0 without
            // needing a case of its own - and 0 is what the client puts there for it.
            if (IsFateZoneRowId(duty.RowId))
            {
                uint territory = duty.RowId - FateZoneRowIdStart;
                return territory <= ushort.MaxValue ? (ushort)territory : (ushort)0;
            }

            // A DEEP DUNGEON HAS AN ID OF ITS OWN, AND IT IS NOT IN ANY SHEET. See
            // DeepDungeonDutyId - the number is read off the running client, one dungeon at a time,
            // and the ones nobody has read yet fall back to leaving the client's own selection
            // alone rather than sending something invented.
            if (IsDeepDungeonRowId(duty.RowId))
                return DeepDungeonDutyId(duty.RowId - DeepDungeonRowIdStart);

            // A TREASURE MAP IS NUMBERED BY WHERE IT SITS IN THE LIST, which is why it carries the
            // number rather than having one worked out from its row - see DutyEntry.ListingDutyId
            // and LoadTreasureMaps.
            if (IsTreasureMapRowId(duty.RowId))
                return duty.ListingDutyId;

            // THE GOLD SAUCER COUNTS FROM 12, for no reason anybody can see - GATEs is 12 and the
            // last Mahjong table is 26, fifteen entries apart in both. Carried on the entry for the
            // same reason as a treasure map's: it is a property of the list, not of the row.
            if (IsGoldSaucerRowId(duty.RowId))
                return duty.ListingDutyId;

            // A ROULETTE SENDS ITS ROULETTE ROW, UNDER EITHER CATEGORY. This used to send it only
            // under Duty Roulette, because 40 read against PvP is ContentFinderCondition 40 - an
            // unrelated duty, which took the category with it - and Crystalline Conflict is a
            // ContentRoulette row posted under PvP.
            //
            // Which sheet the number is read against is not decided by the category at all. It is
            // the byte beside it, and Crystalline Conflict set by hand is SelectedDutyId 40 with
            // that byte at 1 - see SpecificDutyFlag. There was never a conflict to resolve, only a
            // second number that had not been noticed.
            if (IsRouletteRowId(duty.RowId))
                return (ushort)(duty.RowId - RouletteRowIdStart);

            return duty.RowId <= ushort.MaxValue ? (ushort)duty.RowId : (ushort)0;
        }

        /// <summary>
        /// The game ContentType names behind the Party Finder categories that do not share a name
        /// with one.
        ///
        /// The PF category list and the ContentType sheet are two different namings of overlapping
        /// things, and most of them happen to line up word for word. These do not: the Diadem and
        /// Ocean Fishing are filed under "Disciples of the Land", and the field operations are
        /// split three ways by the expansion that added them. Without this the editor found nothing
        /// for either category and fell back to a text box.
        /// </summary>
        private static readonly Dictionary<string, string[]> CategoryContentTypes =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["Gathering Forays"] = new[] { "Disciples of the Land" },
                ["Field Operations"] = new[] { "Eureka", "Save the Queen", "Occult Crescent" },
            };

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
                        IsInDutyFinder = row.IsInDutyFinder,
                        UiCategoryId = row.ContentUICategory.RowId,
                        ContentLinkType = row.ContentLinkType,
                        ContentRowId = row.Content.RowId,
                        QueueMaxPlayers = row.QueueMaxPlayers,
                        ContentMemberType = row.ContentMemberType.RowId,
                        SortKey = row.SortKey,

                        // UnlockType names which sheet UnlockCriteria points into, and 1 is Quest.
                        // The other values (2, 3, 4 - one row each) point somewhere this plugin
                        // cannot read, so they are left at 0 and the content check decides alone.
                        UnlockQuestId = row.UnlockType == 1 ? row.UnlockCriteria.RowId : 0,
                        UnlockQuestId2 = row.UnlockType2 == 1 ? row.UnlockCriteria2.RowId : 0,
                        Roulettes = RouletteMembership(row),
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

                LoadRoulettes();
                LoadFateZones();
                LoadDeepDungeons();
                LoadTreasureMaps();
                LoadGoldSaucer();

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

        /// <summary>
        /// Reads the roulettes out of ContentRoulette and files them under the "Duty Roulette"
        /// category, in the order the game lists them.
        ///
        /// They are not in ContentFinderCondition and never were - which fight a roulette rolls
        /// isn't decided until it pops - so the editor had nothing to offer for the whole category
        /// and put up a text box instead. Chocobo races and the Crystalline Conflict queues live in
        /// this sheet too and belong to other categories, so both are filtered out by content type.
        ///
        /// Called from inside <see cref="GetAllDuties"/> with the caches already built.
        /// </summary>
        private void LoadRoulettes()
        {
            cachedRoulettes = new List<DutyEntry>();

            try
            {
                var sheet = dataManager.GetExcelSheet<Lumina.Excel.Sheets.ContentRoulette>();
                if (sheet == null)
                {
                    pluginLog.Warning("ContentRoulette sheet not found.");
                    return;
                }

                var roulettes = new List<(byte Sort, DutyEntry Entry)>();
                var pvp = new List<(byte Sort, DutyEntry Entry)>();

                foreach (var row in sheet)
                {
                    // Gold Saucer's chocobo races are in this sheet too and belong to their own
                    // category. Content type 1 is Duty Roulette; 6 is PvP, which is where the
                    // Crystalline Conflict queues live.
                    if (!row.IsInDutyFinder || row.IsGoldSaucer)
                        continue;

                    uint contentType = row.ContentType.RowId;
                    if (contentType != 1 && contentType != 6)
                        continue;

                    // Ranked takes one player. It is a solo queue with no party to recruit for, so
                    // there is nothing a listing could be advertising.
                    if (contentType == 6 && row.QueueMaxPlayers == 1)
                        continue;

                    // Mentor, for the same reason. It is entered alone - the whole point of it is
                    // that a mentor fills somebody else's party - so a listing for it could never
                    // be joined.
                    if (RouletteKindOf(row.RowId) == RouletteKind.Mentor)
                        continue;

                    if (IsSoloQueue(row.QueueMaxPlayers))
                        continue;

                    string name = row.Name.ToString();
                    if (string.IsNullOrWhiteSpace(name))
                        continue;

                    var entry = new DutyEntry
                    {
                        RowId = RouletteRowIdStart + row.RowId,
                        Name = name,
                        ContentTypeId = contentType,
                        ContentTypeName = contentType == 1 ? "Duty Roulette" : "PvP",
                        ClassJobLevelRequired = row.RequiredLevel,
                        ItemLevelRequired = row.ItemLevelRequired,
                        IsInDutyFinder = true,
                        Roulettes = RouletteKindOf(row.RowId),
                        RequiredLevel = row.RequiredLevel,
                    };

                    (contentType == 1 ? roulettes : pvp).Add((row.SortKey, entry));
                }

                cachedRoulettes = roulettes.OrderBy(f => f.Sort).Select(f => f.Entry).ToList();
                cachedPvpQueues = pvp.OrderBy(f => f.Sort).Select(f => f.Entry).ToList();

                // Also into the by-id lookup, so a preset that stored one resolves its name back.
                foreach (var entry in cachedRoulettes.Concat(cachedPvpQueues))
                {
                    cachedDuties!.Add(entry);
                    dutyById![entry.RowId] = entry;
                }

                pluginLog.Information($"Loaded {cachedRoulettes.Count} roulettes and {cachedPvpQueues.Count} PvP queue(s) from Lumina data.");
            }
            catch (Exception ex)
            {
                pluginLog.Error(ex, "Failed to load roulette data from Lumina.");
            }
        }

        /// <summary>
        /// Builds the FATEs list: "All locations", then every field zone in the game.
        ///
        /// FATEs are not instances and have no ContentFinderCondition rows, so the editor had
        /// nothing to show for the category at all. What the game asks for instead is a place - the
        /// listing says which zone you are farming in - and the field zones are the TerritoryType
        /// rows whose intended use is the overworld. Forty-odd of them, ARR through the current
        /// expansion, already in expansion order in the sheet.
        ///
        /// "All locations" comes first and is the default, the same as in the client. It carries
        /// the base of the id band, so <see cref="GameDutyId"/> resolves it to 0 - which is the
        /// game's own way of saying "anywhere".
        /// </summary>
        private void LoadFateZones()
        {
            cachedFateZones = new List<DutyEntry>
            {
                new DutyEntry
                {
                    RowId = FateZoneRowIdStart,
                    Name = "All locations",
                    ContentTypeId = 0,
                    ContentTypeName = "FATEs",
                },
            };

            RegisterCategoryWideEntry(cachedFateZones[0]);

            try
            {
                var sheet = dataManager.GetExcelSheet<Lumina.Excel.Sheets.TerritoryType>();
                if (sheet == null)
                {
                    pluginLog.Warning("TerritoryType sheet not found.");
                    return;
                }

                // Territory -> its main aetherytes, built once. IsAetheryte separates the big
                // crystals you teleport to from the aethernet shards inside a city, which are not
                // destinations and are not attuned separately.
                var aetherytesByTerritory = new Dictionary<uint, List<uint>>();
                var aetheryteSheet = dataManager.GetExcelSheet<Lumina.Excel.Sheets.Aetheryte>();
                if (aetheryteSheet != null)
                {
                    foreach (var a in aetheryteSheet)
                    {
                        if (!a.IsAetheryte || a.Territory.RowId == 0)
                            continue;

                        if (!aetherytesByTerritory.TryGetValue(a.Territory.RowId, out var list))
                            aetherytesByTerritory[a.Territory.RowId] = list = new List<uint>();

                        list.Add(a.RowId);
                    }
                }

                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var row in sheet)
                {
                    // Row 0 is skipped for the reason the treasure maps skip theirs: this band's
                    // base id belongs to "All locations", and a sheet row numbered 0 would land on
                    // it and quietly replace it. Nothing in TerritoryType row 0 is a field zone, so
                    // this changes no answer - it just makes the collision impossible rather than
                    // improbable.
                    if (row.RowId == 0)
                        continue;

                    // 1 is the open world. Everything else is a city, an instance, a housing ward
                    // or an inn room, and none of those has a FATE in it.
                    if (row.TerritoryIntendedUse.RowId != 1)
                        continue;

                    string name = row.PlaceName.ValueNullable?.Name.ToString() ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(name) || !seen.Add(name))
                        continue;

                    var entry = new DutyEntry
                    {
                        RowId = FateZoneRowIdStart + row.RowId,
                        Name = name,
                        ContentTypeId = 0,
                        ContentTypeName = "FATEs",
                        ExVersionId = row.ExVersion.RowId,
                        AetheryteIds = aetherytesByTerritory.TryGetValue(row.RowId, out var zoneAetherytes)
                            ? zoneAetherytes.ToArray()
                            : Array.Empty<uint>(),
                    };

                    cachedFateZones.Add(entry);
                    cachedDuties!.Add(entry);
                    RegisterById(entry);
                }

                pluginLog.Information($"Loaded {cachedFateZones.Count - 1} FATE location(s) from Lumina data.");
            }
            catch (Exception ex)
            {
                pluginLog.Error(ex, "Failed to load FATE locations from Lumina.");
            }
        }

        /// <summary>
        /// Builds the Deep Dungeons list: one entry per dungeon, which is what the game asks for.
        ///
        /// THE SHEET IS SPLIT BY FLOOR AND THE PARTY FINDER IS NOT. ContentFinderCondition carries
        /// a row per floor set - "the Palace of the Dead (Floors 51-60)", forty-odd of them across
        /// the four dungeons - because that is what the Duty Finder queues you for. The recruitment
        /// window offers four choices: Pilgrim's Traverse, Eureka Orthos, Heaven-on-High, the Palace
        /// of the Dead. The editor was listing the sheet, so every name it offered was a name the
        /// game's own dropdown does not have, the by-name selection found nothing, and the id left
        /// behind meant some unrelated dungeon.
        ///
        /// The four names come from the DeepDungeon sheet, which is the game's own naming of
        /// exactly these four things and is localised with the client. Newest first, which is the
        /// order the recruitment window lists them in and the reverse of the sheet's.
        ///
        /// EVERY NAME IS CHECKED AGAINST THE SHEET IT HAS TO MATCH. A dungeon is only offered once
        /// its floor rows have been found by it - ContentFinderCondition names them "&lt;dungeon&gt;
        /// &lt;floors&gt;" in every language, so the dungeon's own name is their prefix. That join is
        /// what the unlock check needs anyway, and it doubles as proof that the name is the one the
        /// game uses: a name that matches no floor is a name the dropdown will not have either, and
        /// it is dropped rather than offered.
        /// </summary>
        private void LoadDeepDungeons()
        {
            cachedDeepDungeons = new List<DutyEntry>();

            try
            {
                var sheet = dataManager.GetExcelSheet<Lumina.Excel.Sheets.DeepDungeon>();
                if (sheet == null)
                {
                    pluginLog.Warning("DeepDungeon sheet not found.");
                    return;
                }

                // The floor rows, as they already stand in the duty list. Read once: this runs
                // inside GetAllDuties, where cachedDuties is built but not yet sorted.
                var floors = cachedDuties!
                    .Where(d => string.Equals(d.ContentTypeName, DeepDungeonContentType, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                foreach (var row in sheet.OrderByDescending(r => r.RowId))
                {
                    string name = row.Name.ToString();
                    if (string.IsNullOrWhiteSpace(name))
                        continue;

                    var mine = floors
                        .Where(f => f.Name.StartsWith(name, StringComparison.OrdinalIgnoreCase))
                        .ToList();

                    if (mine.Count == 0)
                    {
                        pluginLog.Warning($"Deep dungeon '{name}' matches no floor in ContentFinderCondition; not offering it.");
                        continue;
                    }

                    var entry = new DutyEntry
                    {
                        RowId = DeepDungeonRowIdStart + row.RowId,
                        Name = name,
                        ContentTypeId = mine[0].ContentTypeId,
                        ContentTypeName = DeepDungeonContentType,
                        ExVersionId = mine[0].ExVersionId,
                        ClassJobLevelRequired = mine[0].ClassJobLevelRequired,
                        IsInDutyFinder = true,
                        FloorRowIds = mine.Select(f => f.RowId).ToArray(),
                    };

                    cachedDeepDungeons.Add(entry);
                    cachedDuties!.Add(entry);
                    dutyById![entry.RowId] = entry;
                }

                pluginLog.Information($"Loaded {cachedDeepDungeons.Count} deep dungeon(s) from Lumina data.");
            }
            catch (Exception ex)
            {
                pluginLog.Error(ex, "Failed to load deep dungeons from Lumina.");
            }
        }

        /// <summary>The ContentType name the deep dungeon floors are filed under, which is also the
        /// Party Finder category's name.</summary>
        private const string DeepDungeonContentType = "Deep Dungeons";

        /// <summary>The Party Finder category name for treasure hunts.</summary>
        private const string TreasureHuntContentType = "Treasure Hunt";

        /// <summary>The Party Finder category name for the Gold Saucer.</summary>
        private const string GoldSaucerContentType = "Gold Saucer";

        /// <summary>The Addon row holding the "GATEs" label, localised with the client.</summary>
        private const uint GatesAddonRowId = 2308;

        /// <summary>ContentMemberType for the Lord of Verminion battles.</summary>
        private const uint VerminionMemberType = 10;

        /// <summary>What the client's first Gold Saucer entry is numbered. GATEs is 12 and the last
        /// Mahjong table is 26, which is fifteen entries later in a list of fifteen.</summary>
        private const ushort GoldSaucerDutyIdBase = 12;

        /// <summary>Sub-band for the chocobo races, which are ContentRoulette rows.</summary>
        private const uint GoldSaucerRaceOffset = 1000;

        /// <summary>Sub-band for the Gold Saucer's ContentFinderCondition rows.</summary>
        private const uint GoldSaucerDutyOffset = 2000;

        /// <summary>
        /// Builds the Gold Saucer list: GATEs, the chocobo races, then the tables.
        ///
        /// THREE SOURCES, AND ONE OF THEM IS NOT A DUTY SHEET. This is the only category whose list
        /// cannot be read out of one place:
        ///
        ///   GATEs         - not in ContentFinderCondition at all. The whole sheet has nothing by
        ///                   that name; the label is a UI string, Addon row 2308, which is at least
        ///                   localised with the client the way every other name here is.
        ///   Chocobo races - ContentRoulette rows flagged IsGoldSaucer, the same rows LoadRoulettes
        ///                   deliberately skips. Eight of them: four courses, each with a no-rewards
        ///                   twin.
        ///   The tables    - ContentFinderCondition rows, the Triple Triad pair and the four
        ///                   Four-player Mahjong tables.
        ///
        /// THE ORDER IS LOAD-BEARING HERE IN A WAY IT IS NOWHERE ELSE. The duty field carries the
        /// entry's position in this list, not any row id - so an entry too many, one missing, or two
        /// in the wrong order does not misname one duty, it shifts every duty after it and posts
        /// listings for the wrong game entirely. Both the sources and the sort are chosen to
        /// reproduce the window exactly: GATEs first, then the races by their roulette SortKey, then
        /// the tables by theirs.
        ///
        /// WHAT IS LEFT OUT, and why the count comes to fifteen. Every Verminion battle: five are
        /// solo queues that IsSoloQueue already drops, and the sixth - Player Battle (Non-RP) -
        /// carries no limit but is the same one-on-one content, so the group goes by its member
        /// type. The ranked Mahjong tables go as solo queues. The training races and the Verminion
        /// stages are not in the duty finder at all.
        /// </summary>
        private void LoadGoldSaucer()
        {
            cachedGoldSaucer = new List<DutyEntry>();

            try
            {
                string gates = dataManager.GetExcelSheet<Lumina.Excel.Sheets.Addon>()
                    ?.GetRowOrDefault(GatesAddonRowId)?.Text.ToString() ?? string.Empty;

                if (!string.IsNullOrWhiteSpace(gates))
                {
                    AddGoldSaucerEntry(new DutyEntry
                    {
                        RowId = GoldSaucerRowIdStart,
                        Name = gates,
                        ContentTypeName = GoldSaucerContentType,
                        IsInDutyFinder = true,
                    });
                }
                else
                {
                    pluginLog.Warning("The GATEs label is missing; every Gold Saucer entry after it would be numbered one short, so the category is left empty.");
                    return;
                }

                var races = new List<(byte Sort, DutyEntry Entry)>();
                var roulettes = dataManager.GetExcelSheet<Lumina.Excel.Sheets.ContentRoulette>();
                if (roulettes != null)
                {
                    var seenRace = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var row in roulettes)
                    {
                        if (!row.IsInDutyFinder || !row.IsGoldSaucer || IsSoloQueue(row.QueueMaxPlayers))
                            continue;

                        string name = row.Name.ToString();
                        if (string.IsNullOrWhiteSpace(name) || !seenRace.Add(name))
                            continue;

                        races.Add((row.SortKey, new DutyEntry
                        {
                            RowId = GoldSaucerRowIdStart + GoldSaucerRaceOffset + row.RowId,
                            Name = name,
                            ContentTypeName = GoldSaucerContentType,
                            IsInDutyFinder = true,
                        }));
                    }
                }

                foreach (var race in races.OrderBy(r => r.Sort))
                    AddGoldSaucerEntry(race.Entry);

                var tables = new List<DutyEntry>();
                foreach (var duty in cachedDuties!)
                {
                    if (!string.Equals(duty.ContentTypeName, GoldSaucerContentType, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (!duty.IsInDutyFinder || IsSoloQueue(duty.QueueMaxPlayers))
                        continue;
                    if (duty.ContentMemberType == VerminionMemberType)
                        continue;

                    tables.Add(duty);
                }

                foreach (var table in tables.OrderBy(d => d.SortKey).ThenBy(d => d.RowId))
                {
                    AddGoldSaucerEntry(new DutyEntry
                    {
                        RowId = GoldSaucerRowIdStart + GoldSaucerDutyOffset + table.RowId,
                        Name = table.Name,
                        ContentTypeName = GoldSaucerContentType,
                        IsInDutyFinder = true,
                    });
                }

                pluginLog.Information($"Loaded {cachedGoldSaucer.Count} Gold Saucer entries (ids {GoldSaucerDutyIdBase} to {GoldSaucerDutyIdBase + cachedGoldSaucer.Count - 1}).");
            }
            catch (Exception ex)
            {
                pluginLog.Error(ex, "Failed to load Gold Saucer entries from Lumina.");
            }
        }

        /// <summary>Appends an entry and stamps it with the number its position earns it.</summary>
        private void AddGoldSaucerEntry(DutyEntry entry)
        {
            entry.ListingDutyId = (ushort)(GoldSaucerDutyIdBase + cachedGoldSaucer!.Count);
            cachedGoldSaucer.Add(entry);
            cachedDuties!.Add(entry);
            RegisterById(entry);
        }

        /// <summary>
        /// Puts a band's "the whole category" entry into the same two lookups every other entry
        /// goes into.
        ///
        /// IT WAS IN NEITHER. "All Levels" and "All locations" are built in a list initialiser and
        /// the registrations all sit inside the loops that follow, so these two alone were absent
        /// from dutyById - which meant every preset that named one failed its id lookup and fell
        /// through to matching by name. That path works right up until it does not: its last resort
        /// is a substring match in either direction, so a name that has moved or been translated
        /// resolves to whichever entry happens to contain it rather than to nothing.
        ///
        /// The id is the thing that is meant to be authoritative here. It could not be, for the two
        /// entries whose whole job is to be the safe answer.
        /// </summary>
        private void RegisterCategoryWideEntry(DutyEntry entry)
        {
            cachedDuties!.Add(entry);
            RegisterById(entry);
        }

        /// <summary>
        /// Puts an entry in the by-id lookup, and says so when it displaces a different one.
        /// 
        /// SILENCE HERE COST A CRASH. Two entries landed on the same id - a sheet row numbered from
        /// 0 inside a band whose base is already spoken for - and the second simply replaced the
        /// first. Everything downstream then worked perfectly on the wrong duty: the preset resolved,
        /// the name looked plausible, the number was real, and the only visible symptom was the
        /// wrong map being posted. Nothing in the lookup is supposed to be ambiguous, so a
        /// replacement is a bug every time and now leaves a line saying which two.
        /// </summary>
        private void RegisterById(DutyEntry entry)
        {
            if (dutyById!.TryGetValue(entry.RowId, out var existing) && existing.Name != entry.Name)
                pluginLog.Warning($"Duty row id {entry.RowId} is claimed twice: '{existing.Name}' replaced by '{entry.Name}'.");

            dutyById[entry.RowId] = entry;
        }

        /// <summary>
        /// Whether this entry is a band's "the whole category" option - "All Levels", "All
        /// locations" - rather than one of the things in it.
        ///
        /// Each band puts that option on its base row, so a zero duty id from one of these is the
        /// answer and not a gap. Without this the log warns about an unmeasured number every time
        /// somebody deliberately asks for all of them.
        /// </summary>
        public static bool IsCategoryWideEntry(uint rowId) =>
            rowId == TreasureMapRowIdStart || rowId == FateZoneRowIdStart;

        /// <summary>
        /// Builds the Treasure Hunt list: "All Levels", then the maps.
        ///
        /// THE MAP, NOT THE DUNGEON IT OPENS. The editor was listing ContentFinderCondition again -
        /// the Aquapolis, the Excitatron 6000, the Shifting Oubliettes of Lyhe Ghiah - which are the
        /// instances a map can lead into. The recruitment window asks which map you are holding:
        /// Leather, Goatskin, Gargantuaskin, the special ones. Not one name overlapped, so nothing
        /// the editor offered could be found in the game's own dropdown.
        ///
        /// The names come from TreasureHuntRank, through KeyItemName into EventItem - the deciphered
        /// map, the key item, which is the wording the window uses. The Item link on the same row is
        /// the undeciphered map and is named differently ("Timeworn Leather Map"), so it is the
        /// wrong one of the two to follow.
        ///
        /// TWO ROWS ARE LEFT OUT, and the sheet says which: TreasureHuntTexture is 0 for an ordinary
        /// map and the Alexandrite Map and the Thorne Dynasty Map are 1 and 2. Those two are quest
        /// maps rather than treasure hunt content and the game does not offer them. Texture 4 - the
        /// Fabled Thief's Map - IS offered, so this excludes the two rather than keeping only the
        /// zero. Observed against the game's own list, which the remaining rows then match in order,
        /// name for name.
        ///
        /// "All Levels" comes first and is 0 - the game's own way of saying "any of them", and the
        /// wording the window uses for it.
        ///
        /// THE NUMBER IS THE POSITION, not the row. The client sends 1 for the first map, 2 for the
        /// second, and 24 for Gargantuaskin, which is the twenty-fourth and last - not 30, which is
        /// its TreasureHuntRank row. Read off the client at both ends of the list.
        ///
        /// That is also what proves the two exclusions above are the right two. The count has to
        /// come out exactly: leave the Alexandrite and Thorne Dynasty maps in and the last map is
        /// the twenty-sixth, drop the Fabled Thief's Map as well and it is the twenty-third. Only
        /// this list puts it on 24, so the filter is checked by the same measurement that gives the
        /// numbering rather than resting on the screenshot it came from.
        /// </summary>
        private void LoadTreasureMaps()
        {
            cachedTreasureMaps = new List<DutyEntry>
            {
                new DutyEntry
                {
                    RowId = TreasureMapRowIdStart,
                    Name = "All Levels",
                    ContentTypeId = 0,
                    ContentTypeName = TreasureHuntContentType,
                },
            };

            RegisterCategoryWideEntry(cachedTreasureMaps[0]);

            try
            {
                var sheet = dataManager.GetExcelSheet<Lumina.Excel.Sheets.TreasureHuntRank>();
                if (sheet == null)
                {
                    pluginLog.Warning("TreasureHuntRank sheet not found.");
                    return;
                }

                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var row in sheet)
                {
                    // ROW 0 IS A DUPLICATE OF ROW 1, and it is the one that must go. Both hold the
                    // Leather Treasure Map's key item, so the dedupe below drops one of them either
                    // way - but it drops whichever it meets first, and meeting row 0 first put
                    // Leather on TreasureMapRowIdStart + 0, which is the id "All Levels" occupies.
                    // Leather then replaced it in the lookup, and every All Levels preset resolved
                    // to Leather and posted as Leather.
                    if (row.RowId == 0)
                        continue;

                    if (row.TreasureHuntTexture is QuestMapTexture or DynastyMapTexture)
                        continue;

                    string name = row.KeyItemName.ValueNullable?.Name.ToString() ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(name) || !seen.Add(name))
                        continue;

                    var entry = new DutyEntry
                    {
                        RowId = TreasureMapRowIdStart + row.RowId,
                        Name = name,
                        ContentTypeId = 0,
                        ContentTypeName = TreasureHuntContentType,
                        IsInDutyFinder = true,

                        // Its place in this list IS the number the client wants - and "All Levels"
                        // already occupies index 0, which is the number that means all of them, so
                        // the count so far is the answer at the moment of adding.
                        ListingDutyId = (ushort)cachedTreasureMaps.Count,
                    };

                    cachedTreasureMaps.Add(entry);
                    cachedDuties!.Add(entry);
                    RegisterById(entry);
                }

                pluginLog.Information($"Loaded {cachedTreasureMaps.Count - 1} treasure map(s) from Lumina data.");
            }
            catch (Exception ex)
            {
                pluginLog.Error(ex, "Failed to load treasure maps from Lumina.");
            }
        }

        /// <summary>TreasureHuntRank.TreasureHuntTexture for the Alexandrite Map, a relic quest map
        /// the Party Finder does not offer.</summary>
        private const byte QuestMapTexture = 1;

        /// <summary>TreasureHuntRank.TreasureHuntTexture for the Thorne Dynasty Map, the other one
        /// the Party Finder does not offer.</summary>
        private const byte DynastyMapTexture = 2;

        /// <summary>
        /// What the client puts in SelectedDutyId for a deep dungeon, keyed by its DeepDungeon row.
        ///
        /// MEASURED, NOT DERIVED, because there is nothing to derive it from. Deep Dungeons is a
        /// fourth id space in that one field: there is no ContentFinderCondition row for a deep
        /// dungeon at all - the sheet has only its fifty floor sets - and the numbers are not the
        /// DeepDungeon rows, nor ContentUICategory, which is 0 for every floor. 29 read against any
        /// other category is Amdapor Keep (Hard).
        ///
        /// Read with "/pfpdebug criteria snap", picking the dungeon from the game's own dropdown,
        /// then "/pfpdebug criteria diff". The byte that moves is +0x10, which is SelectedDutyId.
        ///
        /// THE NUMBERS RUN 29 TO 32 IN SHEET ORDER, AND ARE STILL WRITTEN OUT ONE BY ONE. They
        /// are the DeepDungeon row plus 28, and 28 is a number with no known meaning - it is
        /// wherever this category's block happens to start, not a rule anybody has seen stated.
        /// Deriving from it would quietly answer for a fifth deep dungeon that does not exist yet,
        /// and the sheet is not reliably arithmetic about these things: the same four rows carry
        /// 9, 10, 11 and then 22 in the column beside their names.
        ///
        /// A DUNGEON THAT IS NOT LISTED HERE SENDS 0, not null, and the difference matters. Null
        /// means "leave whatever the client has", which is what the unmeasured three returned first
        /// and it was worse than a wrong number being obvious. The client's field is not updated by
        /// the selection this plugin makes - that turns out to be cosmetic - so what it holds is the
        /// number the LAST apply put there. All three posted as the Palace of the Dead, silently,
        /// because 29 was still sitting in the field from the one dungeon that had a number.
        ///
        /// Zero is "any duty in this category": visibly not what the preset asked for, and the one
        /// wrong answer that cannot be mistaken for a right one. A fifth deep dungeon gets that,
        /// plus a log line naming the command that would fix it.
        /// </summary>
        private static ushort DeepDungeonDutyId(uint deepDungeonRowId) => deepDungeonRowId switch
        {
            1 => (ushort)29,   // the Palace of the Dead
            2 => (ushort)30,   // Heaven-on-High
            3 => (ushort)31,   // Eureka Orthos
            4 => (ushort)32,   // Pilgrim's Traverse
            _ => (ushort)0,    // measured 2026-08-25; anything newer has not been read back yet
        };

        /// <summary>
        /// What belongs in the specific-duty byte for this entry - see PfAutomation's
        /// OffsetSpecificDutyFlag.
        ///
        /// NOT A BOOLEAN. It was read as one for a long time, because for every category the plugin
        /// could post it behaves like one: 2 whenever a duty is picked, 0 for "the whole category".
        /// A deep dungeon says otherwise. The client's own state for the Palace of the Dead is
        /// SelectedDutyId 29 with this byte at 0 - a specific duty, and a zero - and writing the 2
        /// that every other category wants blanked the duty on the listing.
        ///
        /// It says which sheet the number beside it is a row of. Three values, all read off the
        /// client:
        ///
        ///   0 - the category's own numbering. The deep dungeons (29 to 32), the treasure maps (1 to
        ///       24, by position), a FATE's TerritoryType row.
        ///   1 - a ContentRoulette row. Crystalline Conflict set by hand is 40 with a 1.
        ///   2 - a ContentFinderCondition row. Every dungeon, raid, trial and high-end duty, and the
        ///       Frontline and Rival Wings maps, which really are ordinary duty rows.
        ///
        /// 29 with a 2 beside it is Amdapor Keep (Hard), which is not in that category, and the game
        /// shows nothing at all. That is the whole failure this byte causes when it is wrong: the
        /// number is read out of the wrong sheet and names something that cannot be there.
        ///
        /// A ZERO ID ALWAYS TAKES A ZERO BYTE, and this one crashed the game to establish it. 2
        /// tells the client the number beside it is a ContentFinderCondition row; there is no row 0,
        /// and "All Levels" briefly sent 2 on the theory that it needed marking as deliberate. The
        /// window looked right and the client took the listing, then faulted the moment it tried to
        /// populate that listing's detail - inside the game's own code, reached through this
        /// plugin's ListingXray hook. A number the field cannot resolve is not a display problem.
        ///
        /// The theory was wrong anyway: what actually ailed "All Levels" was a row id collision, not
        /// this byte. See LoadTreasureMaps.
        ///
        /// <paramref name="dutyId"/> rather than reading it back off the entry, because the caller
        /// has already resolved it and the two must not disagree - an entry whose number could not
        /// be worked out sends 0 and must not be marked as carrying one.
        /// </summary>
        public static byte SpecificDutyFlag(DutyEntry duty, ushort dutyId)
        {
            if (dutyId == 0)
                return 0;

            if (IsRouletteRowId(duty.RowId))
                return 1;

            return IsDeepDungeonRowId(duty.RowId) || IsTreasureMapRowId(duty.RowId)
                    || IsFateZoneRowId(duty.RowId) || IsGoldSaucerRowId(duty.RowId)
                ? (byte)0
                : (byte)2;
        }

        /// <summary>
        /// "All Levels", for a preset still pointing at one of the treasure DUNGEONS, or null when
        /// the entry is not one.
        ///
        /// THE SAME PROBLEM AS THE DEEP DUNGEON FLOOR SETS, without the same answer available. The
        /// editor used to offer the Aquapolis, the Excitatron 6000 and the rest - the instances a
        /// map opens into - and a preset saved then stores one of their ContentFinderCondition rows,
        /// somewhere between 179 and 1060. Written into the duty field under this category that is
        /// not a map at all, and not a number the field has any meaning for.
        ///
        /// A floor set can be traced back to its dungeon; a treasure instance cannot be traced back
        /// to a map, because several maps open into the same one and which of them the preset meant
        /// was never recorded. So it falls to "All Levels", which is a listing that works and says
        /// what it is, and the preset can be pointed at a specific map by hand.
        /// </summary>
        private DutyEntry? TreasureMapFallback(DutyEntry entry)
        {
            if (cachedTreasureMaps == null || cachedTreasureMaps.Count == 0 || IsTreasureMapRowId(entry.RowId))
                return null;

            if (!string.Equals(entry.ContentTypeName, TreasureHuntContentType, StringComparison.OrdinalIgnoreCase))
                return null;

            return cachedTreasureMaps[0];
        }

        /// <summary>
        /// The dungeon a floor set belongs to, or null when the entry is not one.
        ///
        /// FOR THE PRESETS THAT WERE SAVED BEFORE THE LIST WAS RIGHT. The editor used to offer the
        /// floor sets themselves, so a preset from then stores "the Palace of the Dead (Floors
        /// 1-10)" and its row id - a name the recruitment window has never had, and a number that
        /// means an unrelated dungeon when it is read against this category. Those presets resolve
        /// to the dungeon now, which is the thing they were always for; nothing is rewritten on
        /// disk, so one that predates this still opens in the editor on the entry it now means.
        ///
        /// The floor rows stay in the lookup and are deliberately not removed: they are how the
        /// plugin names the duty someone else's listing is for, and how it works out which floor
        /// the player is currently standing on.
        /// </summary>
        private DutyEntry? DeepDungeonOf(DutyEntry entry)
        {
            if (cachedDeepDungeons == null || IsDeepDungeonRowId(entry.RowId))
                return null;

            if (!string.Equals(entry.ContentTypeName, DeepDungeonContentType, StringComparison.OrdinalIgnoreCase))
                return null;

            foreach (var dungeon in cachedDeepDungeons)
            {
                if (Array.IndexOf(dungeon.FloorRowIds, entry.RowId) >= 0)
                    return dungeon;
            }

            return null;
        }

        /// <summary>
        /// Whether this character can enter a duty.
        ///
        /// The composed answer, and the one everything outside this file should ask.
        /// <see cref="DutyUnlocks"/> holds the questions the client will answer directly - the
        /// unlock quest and the content record - and they cover every entry that has a
        /// ContentFinderCondition row behind it. The two that do not are handled here, because both
        /// need the duty list itself:
        ///
        /// A ROULETTE is open when there is something for it to give you. It has no unlock of its
        /// own; "Duty Roulette: Expert" exists for a character who has never seen an expert dungeon
        /// and would hand them nothing. The sheet marks every duty with the roulettes that can roll
        /// it, so the question becomes "is any of them unlocked", which is an answer already known.
        ///
        /// A FATE LOCATION is open when you can get there, which the teleport menu already knows.
        /// A zone has no unlock row, but an attuned aetheryte is proof of having stood in it.
        /// </summary>
        public bool IsDutyUnlocked(DutyEntry? duty)
        {
            if (duty == null)
                return true;

            if (duty.Roulettes != RouletteKind.None && IsRouletteRowId(duty.RowId))
                return IsRouletteUnlocked(duty);

            if (IsFateZoneRowId(duty.RowId))
                return IsFateZoneReachable(duty);

            if (IsDeepDungeonRowId(duty.RowId))
                return IsDeepDungeonUnlocked(duty);

            // A TREASURE MAP HAS NO UNLOCK TO READ. What gates one is having the map in your bags,
            // which is a thing you hold rather than a thing you have done, and it changes hourly.
            // The category's own system gate - the treasure hunt quest - is checked separately by
            // IsCategoryUnlocked and is the real answer to "can this character do this at all".
            if (IsTreasureMapRowId(duty.RowId))
                return true;

            // Nor does a Gold Saucer entry. Getting into the Gold Saucer at all is the only gate,
            // and IsCategoryUnlocked asks that separately.
            if (IsGoldSaucerRowId(duty.RowId))
                return true;

            return DutyUnlocks.IsUnlocked(duty);
        }

        /// <summary>
        /// Whether this character has been to the zone a FATE location names.
        ///
        /// "All locations" carries no aetherytes and is always available - it is the category's own
        /// default and asks for nowhere in particular. Nor does the Dravanian Hinterlands, whose
        /// crystal is in Idyllshire, a territory of its own; a zone with nothing to check is left
        /// alone rather than guessed at.
        /// </summary>
        private static bool IsFateZoneReachable(DutyEntry zone)
        {
            if (zone.AetheryteIds.Length == 0)
                return true;

            return DutyUnlocks.AnyAetheryteAttuned(zone.AetheryteIds);
        }

        /// <summary>
        /// Whether a deep dungeon is open, which is whether any of its floors is.
        ///
        /// The dungeon itself has no unlock record - it is not a ContentFinderCondition row at all,
        /// see <see cref="LoadDeepDungeons"/> - but its floors each have one, and the first of them
        /// is the quest that opens the place. The later ones are gated behind clearing the earlier
        /// ones, so "any" and "the first" are the same answer; any is the one that survives a floor
        /// set being added or renumbered.
        /// </summary>
        private bool IsDeepDungeonUnlocked(DutyEntry dungeon)
        {
            foreach (uint rowId in dungeon.FloorRowIds)
            {
                var floor = GetDutyEntry(rowId);
                if (floor != null && DutyUnlocks.IsUnlocked(floor))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Whether a roulette is open. Two conditions, and both were letting a fresh alt through.
        ///
        /// THE LEVEL IS PART OF THE UNLOCK HERE, which it is not for an ordinary duty. A dungeon
        /// you are too low for is a dungeon you will be able to enter later on this same character
        /// and this same job; the sheet says so by keeping the level and the unlock in separate
        /// columns. A roulette has no unlock column at all - ContentRoulette.RequiredLevel IS the
        /// gate, and the Duty Finder greys the roulette below it exactly the way it greys content
        /// nobody has bought.
        ///
        /// Read against the job you are currently on, because that is what the game reads. Mentor
        /// wants more than a level - the mentor status behind it cannot be read from here - but the
        /// level 100 it also wants is enough to keep it off a character who has cleared Sastasha.
        /// </summary>
        private bool IsRouletteUnlocked(DutyEntry roulette)
        {
            int level = DutyUnlocks.CurrentJobLevel();
            if (level > 0 && roulette.RequiredLevel > 0 && level < roulette.RequiredLevel)
                return false;

            EnsureLoaded();
            if (cachedDuties == null)
                return true;

            // TWO, NOT ONE. A roulette is a lottery, and a lottery with a single ticket in it is
            // just that duty with extra steps - so the game will not open one until there are two
            // things it could hand you. A character who has unlocked only Sastasha was being told
            // Leveling was available, because Sastasha is flagged for it and one was enough.
            int available = 0;
            foreach (var candidate in cachedDuties)
            {
                if ((candidate.Roulettes & roulette.Roulettes) == 0)
                    continue;
                if (IsRouletteRowId(candidate.RowId))
                    continue;
                if (!DutyUnlocks.IsUnlocked(candidate))
                    continue;

                if (++available >= RouletteMinimumDuties)
                    return true;
            }

            return false;
        }

        /// <summary>How many of a roulette's duties have to be unlocked before it opens.</summary>
        private const int RouletteMinimumDuties = 2;

        /// <summary>
        /// Whether a preset can actually be posted: its category has to be open AND its duty
        /// unlocked.
        ///
        /// BOTH, and the category half is what was missing. A preset for The Hunt has no duty
        /// behind it at all - a hunt train is not an instance - so the duty check waved it through
        /// and it could be applied on a character with no hunt boards, while the picker that made
        /// it had correctly refused to offer the category. Two answers to one question, given in
        /// two places, disagreeing.
        /// </summary>
        public bool IsPresetUnlocked(PfPresetData preset) => DescribePresetLock(preset) == null;

        /// <summary>
        /// Why a preset cannot be posted, or null when it can.
        ///
        /// One answer, phrased once. The button's tooltip and the chat line that /pfp apply prints
        /// were saying "this duty is not unlocked" for a category that has no duty in it, which is
        /// true of nothing and unhelpful to everyone.
        /// </summary>
        public string? DescribePresetLock(PfPresetData preset)
        {
            // Checked before anything about the character. /pfp apply names a preset directly and
            // does not go through the list, so hiding the card is not on its own enough.
            if (!DutyComposition.IsOffered(preset.DutyCategoryId))
            {
                string category = preset.DutyCategoryId > 0 && preset.DutyCategoryId < DutyCategories.Names.Length
                    ? DutyCategories.Names[preset.DutyCategoryId]
                    : preset.DutyCategoryName;

                return $"{category} listings are not supported yet";
            }

            if (!IsCategoryUnlocked(preset.DutyCategoryId))
            {
                string category = preset.DutyCategoryId > 0 && preset.DutyCategoryId < DutyCategories.Names.Length
                    ? DutyCategories.Names[preset.DutyCategoryId]
                    : preset.DutyCategoryName;

                return $"{category} is not unlocked on this character";
            }

            var duty = ResolvePresetDuty(preset);
            if (!IsDutyUnlocked(duty))
                return $"{duty?.Name ?? preset.DutyName} is not unlocked on this character";

            return null;
        }

        /// <summary>
        /// Whether a whole Party Finder category is open to this character.
        ///
        /// Two gates. The system behind the category has to be unlocked at all - the Wolves' Den
        /// quest, the Gold Saucer quest, the hunt boards - which is the only thing in this plugin
        /// that is hardcoded, for the reason set out on
        /// <see cref="DutyUnlocks.IsCategorySystemUnlocked"/>. And then something inside it has to
        /// be reachable, which is derived: a category with duties in it, none of them unlocked, is
        /// shut, and opens by itself the moment that stops being true.
        ///
        /// A category with no duties at all - The Hunt has none, because a hunt train is not an
        /// instance - rests on the system gate alone.
        /// </summary>
        public bool IsCategoryUnlocked(int categoryId)
        {
            if (categoryId <= 0 || categoryId >= DutyCategories.Names.Length)
                return true;

            if (!DutyUnlocks.IsCategorySystemUnlocked(categoryId))
                return false;

            var duties = GetDutiesByType(DutyCategories.Names[categoryId]);
            if (duties.Count == 0)
                return true;

            foreach (var duty in duties)
            {
                if (IsDutyUnlocked(duty))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Whether a queue takes exactly one player, and so cannot be recruited for.
        ///
        /// ONE, NOT "FEWER THAN TWO". Zero is the common case and means no limit at all - every duty
        /// roulette carries it, and reading zero as "too small" dropped all ten of them at once. The
        /// column is only filled in where the queue itself refuses a party.
        ///
        /// Where it is filled in, it is decisive and it is the same answer in both sheets that have
        /// the column. Crystalline Conflict Ranked is 1 and Casual is 2, because ranked sorts by
        /// tier and will not take a premade. Every Lord of Verminion battle and every ranked Mahjong
        /// table is 1, and the recruitment window offers none of them. A Party Finder listing is a
        /// party by definition, so a queue with one seat has nothing to list.
        /// </summary>
        private static bool IsSoloQueue(int queueMaxPlayers) => queueMaxPlayers == 1;

        /// <summary>The ContentRoulette row id of the mentor roulette.</summary>
        private const uint MentorRouletteRowId = 9;

        /// <summary>
        /// Whether a saved preset is for the mentor roulette, which the plugin no longer offers.
        ///
        /// Checked by stored row id first, because that is the thing that cannot be misread. The
        /// name is the fallback for presets written before roulettes had ids of their own, and it
        /// is only consulted inside the Duty Roulette category - somebody's preset called "Mentor
        /// runs" for a dungeon is not this.
        /// </summary>
        public static bool IsMentorRoulette(PfPresetData preset)
        {
            if (preset.DutyCategoryId != 1)
                return false;

            if (IsRouletteRowId(preset.DutyRowId))
                return preset.DutyRowId - RouletteRowIdStart == MentorRouletteRowId;

            return preset.DutyName?.IndexOf("Mentor", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>Which roulettes can roll this duty, read off the sheet's per-roulette
        /// booleans.</summary>
        private static RouletteKind RouletteMembership(Lumina.Excel.Sheets.ContentFinderCondition row)
        {
            RouletteKind kind = RouletteKind.None;

            if (row.LevelingRoulette) kind |= RouletteKind.Leveling;
            if (row.HighLevelRoulette) kind |= RouletteKind.HighLevel;
            if (row.MSQRoulette) kind |= RouletteKind.MainScenario;
            if (row.GuildHestRoulette) kind |= RouletteKind.Guildhest;
            if (row.ExpertRoulette) kind |= RouletteKind.Expert;
            if (row.TrialRoulette) kind |= RouletteKind.Trial;
            if (row.DailyFrontlineChallenge) kind |= RouletteKind.Frontline;
            if (row.LevelCapRoulette) kind |= RouletteKind.LevelCap;
            if (row.MentorRoulette) kind |= RouletteKind.Mentor;
            if (row.AllianceRoulette) kind |= RouletteKind.Alliance;
            if (row.NormalRaidRoulette) kind |= RouletteKind.NormalRaid;
            if (row.CrystallineConflictCasualRoulette) kind |= RouletteKind.CrystallineConflict;

            return kind;
        }

        /// <summary>Which roulette a ContentRoulette row is. The ids are stable and the sheet has
        /// no other handle on them - the names are localised and the sort key moves.</summary>
        private static RouletteKind RouletteKindOf(uint contentRouletteRowId) => contentRouletteRowId switch
        {
            1 => RouletteKind.Leveling,
            2 => RouletteKind.HighLevel,
            3 => RouletteKind.MainScenario,
            4 => RouletteKind.Guildhest,
            5 => RouletteKind.Expert,
            6 => RouletteKind.Trial,
            7 => RouletteKind.Frontline,
            8 => RouletteKind.LevelCap,
            9 => RouletteKind.Mentor,
            15 => RouletteKind.Alliance,
            17 => RouletteKind.NormalRaid,
            40 => RouletteKind.CrystallineConflict,
            _ => RouletteKind.None,
        };

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
        /// <summary>
        /// Where a territory is, by name - "Limsa Lominsa Lower Decks", "The Rising Stones".
        ///
        /// Read straight off the sheet rather than cached with the duties: this is asked once a
        /// frame at most, only while the player is standing around doing nothing, and a zone the
        /// player is not in is a zone nobody is going to ask about.
        /// </summary>
        /// <summary>
        /// Which of <see cref="DutyCategories.Names"/> a duty belongs to, or 0 when it cannot be
        /// placed.
        ///
        /// Matched on the content type's NAME rather than its row id, which is how the preset
        /// editor already maps the two - see GetDutiesByType. The game's ContentType ids and this
        /// plugin's category list are separate numberings that happen to describe the same things,
        /// and the string is the only thing they genuinely share.
        /// </summary>
        public int GetCategoryIdForDuty(uint dutyRowId, string? dutyName = null)
        {
            // THREE ASKS, IN ORDER, AND THE LAST ONE IS NOT A GUESS.
            //
            // The row id is the good answer and it is unavailable for exactly the fights somebody
            // is most likely to be looking at: the ultimates and the current savage tier are not in
            // ContentFinderCondition at all, which is why HighEndDutyNames exists and gets
            // synthetic rows appended to the list.
            //
            // The name is the second ask, and it covers a duty carrying a row id whose content type
            // this plugin does not have a category for - the first version only reached for the
            // name when the id lookup returned NOTHING, so a duty that resolved to a real row with
            // an unmapped type came back uncategorised and never got a second chance.
            //
            // The third is the same hardcoded list the editor's High-end Duty dropdown is built
            // from. If a fight is on that list it is a high-end duty by definition, whatever the
            // sheet does or does not say about it.
            int byId = CategoryOfEntry(dutyRowId != 0 ? GetDutyEntry(dutyRowId) : null);
            if (byId != 0)
                return byId;

            if (string.IsNullOrWhiteSpace(dutyName))
                return 0;

            int byName = CategoryOfEntry(GetDutyByExactName(dutyName!));
            if (byName != 0)
                return byName;

            return IsHighEndDutyName(dutyName!) ? HighEndDutyCategoryId : 0;
        }

        /// <summary>Which of <see cref="DutyCategories.Names"/> an entry's content type is, or 0.
        /// </summary>
        private static int CategoryOfEntry(DutyEntry? duty)
        {
            if (duty == null || string.IsNullOrWhiteSpace(duty.ContentTypeName))
                return 0;

            for (int i = 0; i < DutyCategories.Names.Length; i++)
            {
                if (DutyCategories.Names[i].Equals(duty.ContentTypeName, StringComparison.OrdinalIgnoreCase))
                    return i;
            }

            return 0;
        }

        /// <summary>True for a fight on the hardcoded high-end list - the same list the editor's
        /// High-end Duty category is populated from.</summary>
        public static bool IsHighEndDutyName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return false;

            string trimmed = name.Trim();
            foreach (string known in HighEndDutyNames)
            {
                if (known.Equals(trimmed, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        /// <summary>Index of "High-end Duty" in <see cref="DutyCategories.Names"/>, found rather
        /// than written down - the list is edited from time to time and a literal 6 in here would
        /// quietly start meaning PvP.</summary>
        private static readonly int HighEndDutyCategoryId = Array.FindIndex(
            DutyCategories.Names, n => n.Equals("High-end Duty", StringComparison.OrdinalIgnoreCase));

        /// <summary>What the three asks in <see cref="GetCategoryIdForDuty"/> each came back with,
        /// for /pfpdebug. The icon being blank is invisible from the code - every step of it looks
        /// correct in isolation - and this says which one actually answered.</summary>
        public string DescribeCategoryLookup(uint dutyRowId, string? dutyName)
        {
            var byIdEntry = dutyRowId != 0 ? GetDutyEntry(dutyRowId) : null;
            var byNameEntry = string.IsNullOrWhiteSpace(dutyName) ? null : GetDutyByExactName(dutyName!);

            return $"rowId={dutyRowId} name='{dutyName}' "
                + $"byId={(byIdEntry == null ? "none" : $"'{byIdEntry.ContentTypeName}'->{CategoryOfEntry(byIdEntry)}")} "
                + $"byName={(byNameEntry == null ? "none" : $"'{byNameEntry.ContentTypeName}'->{CategoryOfEntry(byNameEntry)}")} "
                + $"highEndList={(dutyName != null && IsHighEndDutyName(dutyName))} "
                + $"result={GetCategoryIdForDuty(dutyRowId, dutyName)}";
        }

        public string GetPlaceName(uint territoryTypeRowId)
        {
            if (territoryTypeRowId == 0)
                return string.Empty;

            try
            {
                var sheet = dataManager.GetExcelSheet<Lumina.Excel.Sheets.TerritoryType>();
                var row = sheet?.GetRowOrDefault(territoryTypeRowId);
                return row?.PlaceName.Value.Name.ToString() ?? string.Empty;
            }
            catch (Exception ex)
            {
                pluginLog.Debug($"Place name lookup failed for territory {territoryTypeRowId}: {ex.Message}");
                return string.Empty;
            }
        }

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

        /// <summary>
        /// What the editor offers under one Party Finder category. Empty for the categories the
        /// game itself has nothing to list - see <see cref="HasDutyList"/>.
        /// </summary>
        public List<DutyEntry> GetDutiesByType(string contentTypeName)
        {
            EnsureLoaded();
            if (cachedDuties == null)
                return new List<DutyEntry>();

            // Named order, not the sheet's: this list is what the plugin knows about high-end
            // content, some of it not in the sheet at all.
            if (string.Equals(contentTypeName, "High-end Duty", StringComparison.OrdinalIgnoreCase))
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

            // The game's own order, which is by difficulty rather than by name.
            if (string.Equals(contentTypeName, "Duty Roulette", StringComparison.OrdinalIgnoreCase))
                return cachedRoulettes != null ? new List<DutyEntry>(cachedRoulettes) : new List<DutyEntry>();

            // Fifteen entries out of three sheets - see LoadGoldSaucer.
            if (string.Equals(contentTypeName, GoldSaucerContentType, StringComparison.OrdinalIgnoreCase))
                return cachedGoldSaucer != null ? new List<DutyEntry>(cachedGoldSaucer) : new List<DutyEntry>();

            // Maps, not the dungeons the maps lead to - see LoadTreasureMaps.
            if (string.Equals(contentTypeName, TreasureHuntContentType, StringComparison.OrdinalIgnoreCase))
                return cachedTreasureMaps != null ? new List<DutyEntry>(cachedTreasureMaps) : new List<DutyEntry>();

            // One entry per dungeon rather than the sheet's row per floor set - see
            // LoadDeepDungeons. Without this the generic path below returns all forty of them,
            // because each floor set is a name of its own and the dedupe has nothing to collapse.
            if (string.Equals(contentTypeName, DeepDungeonContentType, StringComparison.OrdinalIgnoreCase))
                return cachedDeepDungeons != null ? new List<DutyEntry>(cachedDeepDungeons) : new List<DutyEntry>();

            // Places, not duties.
            if (string.Equals(contentTypeName, "FATEs", StringComparison.OrdinalIgnoreCase))
                return cachedFateZones != null ? new List<DutyEntry>(cachedFateZones) : new List<DutyEntry>();

            if (string.Equals(contentTypeName, "PvP", StringComparison.OrdinalIgnoreCase))
                return BuildPvpList();

            // One PF category can draw on several content types, and the names it does share with
            // the sheet are not always spelled the same way - "FATEs" against "FATEs" is fine, but
            // the comparison has to be case-insensitive for it to stay that way.
            string[] types = CategoryContentTypes.TryGetValue(contentTypeName, out var mapped)
                ? mapped
                : new[] { contentTypeName };

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = new List<DutyEntry>();

            foreach (var duty in cachedDuties)
            {
                if (!types.Any(t => string.Equals(duty.ContentTypeName, t, StringComparison.OrdinalIgnoreCase)))
                    continue;

                // A queue that takes one player is not something a party can be recruited for, and
                // the window does not offer it. Confined in practice to the Gold Saucer's solo
                // content - the Verminion battles and the ranked Mahjong tables - which is the only
                // content in the game with the column filled in at all.
                if (IsSoloQueue(duty.QueueMaxPlayers))
                    continue;

                // One name, one entry. The sheet carries a row per instance rather than per duty -
                // Ocean Fishing alone is fifteen of them, one for each route - and a dropdown with
                // the same words fifteen times over is not a choice anyone can make.
                if (!seen.Add(duty.Name))
                    continue;

                result.Add(duty);
            }

            return result;
        }

        /// <summary>
        /// The PvP list, which the game assembles differently from every other category.
        ///
        /// Crystalline Conflict is ONE entry, not seven. The maps it rotates through are real rows
        /// in the sheet - the Palaistra, Cloud Nine, the Red Sands - but you do not queue for a map
        /// and no listing can ask for one, so the game offers only "Crystalline Conflict (Casual
        /// Match)" and rolls the map itself. Those rows are marked as not being in the Duty Finder,
        /// which is what drops them here; the single entry that replaces them comes from
        /// ContentRoulette, alongside the duty roulettes.
        ///
        /// Custom Match is excluded outright. It is a separate tab in the Party Finder with its own
        /// rules, and this plugin does not post to it - listing its maps here would offer a choice
        /// that goes somewhere the preset cannot follow.
        ///
        /// What survives is Frontline and Rival Wings, which really are picked map by map.
        /// </summary>
        /// <summary>
        /// Astragalos, the Rival Wings map that was retired and left in the sheet.
        ///
        /// NAMED BY ROW BECAUSE THE SHEET WILL NOT SAY. Every column that could separate it from
        /// Hidden Gorge, the map that replaced it, holds the same value: the same content member
        /// type, the same link type, the same level, no unlock on either, and IsInDutyFinder true on
        /// both. The recruitment window offers Hidden Gorge and not this, and nothing in the data
        /// accounts for the difference - so this is the one exclusion here that is observed rather
        /// than derived, and it is written as a single row id so that is obvious.
        /// </summary>
        private const uint RetiredRivalWingsMap = 277;

        private List<DutyEntry> BuildPvpList()
        {
            var result = new List<DutyEntry>();

            // THE DEDUPE HAS TO COVER THE QUEUES TOO. It used to start empty at the loop below, so
            // the queue entries added just above were invisible to it - and the queues are in
            // cachedDuties as well, being duties like any other. Crystalline Conflict came out
            // twice, once from each pass, which is exactly what the dedupe was there to stop.
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (cachedPvpQueues != null)
            {
                foreach (var queue in cachedPvpQueues)
                {
                    if (seen.Add(queue.Name))
                        result.Add(queue);
                }
            }

            if (cachedDuties == null)
                return result;

            // The maps, in the window's own order: Frontline before Rival Wings, and each by its
            // row. Sorted rather than taken as they come, because cachedDuties is alphabetical by
            // then and the window is not.
            var maps = new List<DutyEntry>();
            foreach (var duty in cachedDuties)
            {
                if (!string.Equals(duty.ContentTypeName, "PvP", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!duty.IsInDutyFinder || duty.UiCategoryId == CrystallineConflictCustomMatchUiCategory)
                    continue;
                if (duty.RowId == RetiredRivalWingsMap)
                    continue;
                if (!seen.Add(duty.Name))
                    continue;

                maps.Add(duty);
            }

            result.AddRange(maps.OrderBy(d => d.UiCategoryId).ThenBy(d => d.RowId));
            return result;
        }

        /// <summary>ContentUICategory for "Crystalline Conflict (Custom Match)".</summary>
        private const uint CrystallineConflictCustomMatchUiCategory = 44;

        /// <summary>
        /// Whether this category has any duties to choose between.
        ///
        /// The Hunt has none anywhere in the game data, and that is not an oversight: a hunt train
        /// is not an instance, so there is nothing for a listing to point at and the game does not
        /// ask. The editor hides the duty row entirely rather than offering a text box, which only
        /// ever produced a name that resolved to nothing.
        /// </summary>
        public bool HasDutyList(string contentTypeName) => GetDutiesByType(contentTypeName).Count > 0;

        /// <summary>
        /// The duty a preset is for, or null when it cannot be identified.
        ///
        /// The stored row id is authoritative and is language- and rename-proof; the display name
        /// is only consulted for presets saved before row ids existed, and for the synthetic
        /// high-end entries, which have no stable id to store. Callers that want to say something
        /// about a failure do the logging themselves - this is asked once a frame from the preset
        /// list and must stay quiet.
        /// </summary>
        public DutyEntry? ResolvePresetDuty(PfPresetData preset)
        {
            if (preset.DutyRowId != 0 && !IsSyntheticRowId(preset.DutyRowId))
            {
                var byId = GetDutyEntry(preset.DutyRowId);
                if (byId != null)
                    return DeepDungeonOf(byId) ?? TreasureMapFallback(byId) ?? byId;
            }

            return FindDutyByName(preset.DutyCategoryName, preset.DutyName);
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
    /// The roulettes, as flags, so a duty can say which ones it belongs to.
    ///
    /// ContentFinderCondition carries a boolean per roulette - LevelingRoulette, ExpertRoulette and
    /// the rest - which is the only link between a roulette and the duties it rolls. It is also the
    /// only way to tell whether a roulette is open to somebody: a roulette has no unlock of its own
    /// and is available exactly when there is something for it to give you.
    /// </summary>
    [Flags]
    public enum RouletteKind : uint
    {
        None = 0,
        Leveling = 1 << 0,
        HighLevel = 1 << 1,
        MainScenario = 1 << 2,
        Guildhest = 1 << 3,
        Expert = 1 << 4,
        Trial = 1 << 5,
        Frontline = 1 << 6,
        LevelCap = 1 << 7,
        Mentor = 1 << 8,
        Alliance = 1 << 9,
        NormalRaid = 1 << 10,
        CrystallineConflict = 1 << 11,
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

        /// <summary>Whether the game offers this duty in the Duty Finder at all. False for content
        /// reached some other way - deep dungeons, treasure maps, the ultimates - so it is only a
        /// useful filter within a category, never across all of them.</summary>
        public bool IsInDutyFinder { get; set; }

        /// <summary>Which section of the duty list the game files this under - "Frontline",
        /// "Crystalline Conflict (Custom Match)". Finer-grained than the content type, and the only
        /// thing separating the PvP modes from each other.</summary>
        public uint UiCategoryId { get; set; }

        /// <summary>Which sheet <see cref="ContentRowId"/> points into: 1 is InstanceContent and 3
        /// is PublicContent, the two the client can be asked about. Anything else is content whose
        /// unlock state this plugin cannot read, and is treated as unlocked.</summary>
        public byte ContentLinkType { get; set; }

        /// <summary>The content row behind the duty, which is what an unlock is recorded against -
        /// the ContentFinderCondition row itself is not. Zero for the entries with no instance
        /// behind them at all: roulettes and FATE locations.</summary>
        public uint ContentRowId { get; set; }

        /// <summary>
        /// The quest that opens this duty, or 0 when it is not gated behind one.
        ///
        /// A hundred rows carry this and they are the ones the content check is worst at - the
        /// Occult Crescent, Eureka, the deep dungeons, Ocean Fishing, the Diadem. Two slots because
        /// a handful of duties name two, and both have to be done.
        /// </summary>
        public uint UnlockQuestId { get; set; }

        /// <summary>The second quest, per <see cref="UnlockQuestId"/>. Usually 0.</summary>
        public uint UnlockQuestId2 { get; set; }

        /// <summary>
        /// The level a roulette opens at, from ContentRoulette.RequiredLevel. Zero for everything
        /// else.
        ///
        /// This is a real unlock, not a suggestion: Leveling opens at 16, Guildhests at 10,
        /// Trials and Main Scenario at 50, Normal Raids at 60, and Expert, Level Cap and Mentor at
        /// 100. A roulette below its level is greyed in the Duty Finder exactly the way an unbought
        /// dungeon is.
        /// </summary>
        public int RequiredLevel { get; set; }

        /// <summary>
        /// The zone's main aetherytes, for a FATE location. Empty for everything else.
        ///
        /// A zone has no unlock record of its own, so the question becomes the one the teleport
        /// menu already answers: can you get there. An attuned aetheryte is proof you have been,
        /// and a zone with none of its aetherytes attuned is one this character has not reached.
        /// </summary>
        public uint[] AetheryteIds { get; set; } = Array.Empty<uint>();

        /// <summary>
        /// The number the client puts in SelectedDutyId for this entry, when that number is the
        /// entry's place in a list rather than anything about its row. Zero for everything else,
        /// which is also the right answer for the "all of them" entry that leads such a list.
        ///
        /// Carried on the entry because it cannot be recovered from <see cref="RowId"/>: the row id
        /// is the sheet row, deliberately, so a preset stays pointed at the same map when the sheet
        /// grows. The position is a property of the list this entry was built into. See
        /// <see cref="DutyDataHelper.LoadTreasureMaps"/>.
        /// </summary>
        public ushort ListingDutyId { get; set; }

        /// <summary>
        /// For a deep dungeon: the ContentFinderCondition rows of its floor sets. Empty for
        /// everything else.
        ///
        /// The dungeon is offered as one entry and has no row of its own, so this is where its
        /// unlock is read from - see <see cref="DutyDataHelper.IsDutyUnlocked"/>.
        /// </summary>
        public uint[] FloorRowIds { get; set; } = Array.Empty<uint>();

        /// <summary>How many players the queue itself accepts, where the sheet says. Zero means no
        /// limit, which is the common case; one means a solo queue that cannot be recruited for -
        /// see <see cref="DutyDataHelper.IsSoloQueue"/>.</summary>
        public int QueueMaxPlayers { get; set; }

        /// <summary>What shape of party the content takes, from ContentFinderCondition. Only read
        /// to tell the Lord of Verminion battles apart from the rest of the Gold Saucer.</summary>
        public uint ContentMemberType { get; set; }

        /// <summary>The sheet's own display order within a content type. The Gold Saucer needs it
        /// because its list has to reproduce the window's order exactly - see
        /// <see cref="DutyDataHelper.LoadGoldSaucer"/>.</summary>
        public ushort SortKey { get; set; }

        /// <summary>For a duty: every roulette it can be rolled by. For a roulette: the single
        /// roulette it is. This is what lets a roulette be told locked or open - see
        /// <see cref="DutyDataHelper.IsDutyUnlocked"/>.</summary>
        public RouletteKind Roulettes { get; set; }

        public int ClassJobLevelRequired { get; set; }
        public int ItemLevelRequired { get; set; }
    }
}
