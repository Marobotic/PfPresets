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

        /// <summary>True for a roulette, per <see cref="RouletteRowIdStart"/>.</summary>
        public static bool IsRouletteRowId(uint rowId) =>
            rowId >= RouletteRowIdStart && rowId < FateZoneRowIdStart;

        /// <summary>True for a FATE location, per <see cref="FateZoneRowIdStart"/>.</summary>
        public static bool IsFateZoneRowId(uint rowId) =>
            rowId >= FateZoneRowIdStart && rowId < SyntheticRowIdStart;

        /// <summary>
        /// The number the game wants in its SelectedDutyId field for this entry, or 0 for "any duty
        /// in the category".
        ///
        /// One field, three id spaces, and which one applies is decided by the category the listing
        /// is posted under - so this is the single place the plugin's offsets come back off. Zero
        /// for the synthetic high-end entries, whose ids are ours alone and mean nothing to the
        /// game, and zero for "All locations", which is what that option is.
        /// </summary>
        public static ushort? GameDutyId(DutyEntry duty)
        {
            if (IsSyntheticRowId(duty.RowId))
                return 0;

            // A FATE LOCATION HAS NO ID THE LISTING CAN CARRY, so it sends none.
            //
            // These entries are TerritoryType rows, and a TerritoryType id written into the duty
            // field is read as a ContentFinderCondition id - a number that means a zone becomes a
            // number that means some dungeon. The client then takes the category from the duty it
            // was handed, so a FATE listing came out filed under whatever that dungeon was. Zero
            // is "anywhere in this category", which is what the category's own default says
            // anyway, and it keeps the category right.
            if (IsFateZoneRowId(duty.RowId))
                return null;

            // A roulette id is only a roulette id under Duty Roulette. Crystalline Conflict is a
            // ContentRoulette row like the duty roulettes are, but it is posted under PvP - and 40
            // read against PvP is ContentFinderCondition 40, an unrelated duty, which took the
            // category with it exactly as above. Only content type 1 may send its roulette number;
            // the rest keep whatever the dropdown selection put there.
            if (IsRouletteRowId(duty.RowId))
                return duty.ContentTypeId == 1
                    ? (ushort)(duty.RowId - RouletteRowIdStart)
                    : null;

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
                    dutyById![entry.RowId] = entry;
                }

                pluginLog.Information($"Loaded {cachedFateZones.Count - 1} FATE location(s) from Lumina data.");
            }
            catch (Exception ex)
            {
                pluginLog.Error(ex, "Failed to load FATE locations from Lumina.");
            }
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
            if (!DutyComposition.IsSupported(preset.DutyCategoryId))
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
        private List<DutyEntry> BuildPvpList()
        {
            var result = new List<DutyEntry>();

            if (cachedPvpQueues != null)
                result.AddRange(cachedPvpQueues);

            if (cachedDuties == null)
                return result;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var duty in cachedDuties)
            {
                if (!string.Equals(duty.ContentTypeName, "PvP", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!duty.IsInDutyFinder || duty.UiCategoryId == CrystallineConflictCustomMatchUiCategory)
                    continue;
                if (!seen.Add(duty.Name))
                    continue;

                result.Add(duty);
            }

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
                    return byId;
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

        /// <summary>For a duty: every roulette it can be rolled by. For a roulette: the single
        /// roulette it is. This is what lets a roulette be told locked or open - see
        /// <see cref="DutyDataHelper.IsDutyUnlocked"/>.</summary>
        public RouletteKind Roulettes { get; set; }

        public int ClassJobLevelRequired { get; set; }
        public int ItemLevelRequired { get; set; }
    }
}
