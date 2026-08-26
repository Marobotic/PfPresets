using System;
using System.Collections.Generic;

namespace PfPresets
{
    /// <summary>
    /// What a party for a given kind of content is normally made of.
    ///
    /// Every preset used to start as eight Free slots whatever it was for, so a dungeon preset
    /// advertised eight seats and a Crystalline Conflict one advertised eight seats, and the person
    /// making it had to know to fix that by hand every single time. The game knows the answer for
    /// its own content and so can we.
    ///
    /// ROLES, NEVER JOBS. A light party here is "a tank, a healer, and two of anything that does
    /// damage" - not four named jobs. Locking a slot to a specific job is a decision about who you
    /// want, and that belongs to the person writing the listing; this only fills in how many seats
    /// there are and what shape they are.
    ///
    /// Slot 1 is the player's own and is set alongside the rest: the editor and the auto-adjuster
    /// reconcile it with whatever job they are actually on when the listing goes up.
    /// </summary>
    public static class DutyComposition
    {
        /// <summary>Every job that deals damage, as one mask - the same union the job selector's
        /// "DPS" row selects. There is no single DPS role in <see cref="RoleType"/>, so a slot that
        /// takes any of the three carries the role of one and the jobs of all.</summary>
        private static ulong AllDps =>
            JobMasks.GetRoleMask(RoleType.MeleeDPS)
            | JobMasks.GetRoleMask(RoleType.PhysRangedDPS)
            | JobMasks.GetRoleMask(RoleType.MagicRangedDPS);

        /// <summary>
        /// The three categories the game's own "Seek Job Distributions" is offered for, and the
        /// only ones this plugin leaves the checkbox live on.
        ///
        /// Auto-adjust hands the whole composition to the game client, which fills it around
        /// whoever is already standing in your party. That is what you want for a savage reclear
        /// and actively unhelpful for a dungeon, where it takes the slot editor away - the grid
        /// greys out while auto-adjust is on - in exchange for a shape you already know.
        /// </summary>
        public static bool SupportsAutoAdjust(int categoryId) =>
            categoryId == 4      // Trials
            || categoryId == 5   // Raids
            || categoryId == 6;  // High-end Duty

        /// <summary>
        /// The one category the plugin does not post listings for yet.
        ///
        /// BROKEN, NOT UNWANTED. Gold Saucer fails where the other five used to: the recruitment
        /// window's duty field is a single number, and most of what this category holds is a
        /// GoldSaucerContent row rather than a ContentFinderCondition one. Until somebody reads back
        /// what the client actually puts there, offering it is offering a listing that does not say
        /// what it claims to.
        ///
        /// It needs exactly what the other five needed, and the recipe is now known:
        ///
        ///   1. Set the criteria by hand in the game's own window.
        ///   2. "/pfpdebug criteria" - it prints SelectedDutyId AND the byte beside it.
        ///   3. Read both. The byte says which sheet the number is a row of; see
        ///      DutyDataHelper.SpecificDutyFlag.
        ///   4. Endpoints first. The first and last entry in a category's list usually settle
        ///      whether the numbering is a sheet row or a position, and the count has to come out
        ///      exactly - that is what checked the treasure map list as well as its numbering.
        ///
        /// THE FIVE THAT CAME BACK, 2026-08-25, all failed on the same misreading. The duty field is
        /// not interpreted against the category at all; the byte beside it says which sheet the
        /// number belongs to, and it has three values rather than two:
        ///
        ///   Duty Roulette  - a ContentRoulette row, byte 1. "The roulette id is written but the
        ///                    client does not keep it" was the byte saying 2, so the client read the
        ///                    number out of ContentFinderCondition and discarded it.
        ///   PvP            - Crystalline Conflict is that same ContentRoulette case, row 40 byte 1;
        ///                    the Frontline and Rival Wings maps are ordinary duty rows, byte 2.
        ///                    There was never a collision between the two id spaces to work around.
        ///   FATEs          - the TerritoryType row after all, byte 0. 134 for Middle La Noscea,
        ///                    1192 for Living Memory.
        ///   Treasure Hunt  - the map's position in the list, 1 to 24, byte 0. The list was the
        ///                    bigger error: it offered the dungeons a map opens into.
        ///   Deep Dungeons  - the category's own numbering, 29 to 32, byte 0, in no sheet anywhere.
        ///                    Its list was per floor set where the window asks for the dungeon.
        ///
        /// A LIST CAN BE AS WRONG AS A NUMBER, and it is quieter about it. Three of those five were
        /// offering names the recruitment window has never had, so no selection could match them.
        /// Check the list against the game's own dropdown before trusting anything measured through
        /// it.
        /// </summary>
        public static bool IsSupported(int categoryId) => categoryId switch
        {
            8 => false,   // Gold Saucer
            _ => true,
        };

        /// <summary>
        /// Developer override: offer the categories above anyway.
        ///
        /// NOTHING IN AN ORDINARY BUILD WRITES THIS, and that is deliberate rather than an
        /// oversight to be tidied up. The only setter lives in the moderator files, which are
        /// gitignored and exist on the two machines that hold a key - see UI/PluginUI.AdminHooks.cs
        /// for why that is the mechanism. A released assembly carries this property and never
        /// assigns it, so every category the table calls broken stays out of reach.
        ///
        /// It exists because the six above cannot be fixed without being usable. Each one needs the
        /// number the client itself writes into the recruitment window's duty field, and reading it
        /// back means making a preset for that category, applying it, and looking at what went up -
        /// none of which is possible while the picker refuses the category, the list hides the card
        /// and Apply is greyed out with "not supported yet".
        /// </summary>
        public static bool OfferUnsupported { get; set; }

        /// <summary>
        /// Whether the plugin should offer this category to the person using it: normally the same
        /// answer as <see cref="IsSupported"/>, and everything else when the developer override is
        /// on.
        ///
        /// THE SPLIT IS THE POINT. <see cref="IsSupported"/> keeps saying what is true - whether a
        /// listing for this category goes up correctly - and nothing about the override changes
        /// that. This says what the UI does about it, which is the question the preset list, the
        /// category picker and the Apply button are all actually asking.
        /// </summary>
        public static bool IsOffered(int categoryId) => OfferUnsupported || IsSupported(categoryId);

        /// <summary>
        /// Content that only exists on the world you are standing on.
        ///
        /// A FATE is a thing happening in a zone right now and the hunt is a train walking through
        /// one, so neither survives the trip: somebody who travels to you arrives after it. A
        /// treasure hunt is the same bargain with a shorter fuse - the portal is dug in an overworld
        /// zone on one world and the party has to be standing there when it opens.
        ///
        /// The game will happily post any of the three across a data centre and the listing is
        /// simply wrong, so the plugin does not offer the choice - "limit recruiting to my world" is
        /// forced on for them, in the editor and again when the listing is written, which covers
        /// presets saved before this existed.
        /// </summary>
        public static bool RequiresHomeWorld(int categoryId) =>
            categoryId == 9       // FATEs
            || categoryId == 10   // Treasure Hunt
            || categoryId == 11;  // The Hunt

        /// <summary>
        /// The composition to start a preset with. Never null: content this plugin has no table
        /// for still gets <see cref="FullParty"/> rather than a row of open seats, because "two
        /// tanks, two healers, four damage" is a party and eight blank slots is a shrug.
        /// </summary>
        /// <param name="categoryId">Index into <see cref="DutyCategories.Names"/>.</param>
        /// <param name="dutyName">The duty itself, which matters inside Duty Roulette: the roulette
        /// list mixes four-player content with eight-player content under one category.</param>
        public static List<RoleSlot> DefaultFor(int categoryId, string dutyName)
        {
            string duty = dutyName ?? string.Empty;

            return categoryId switch
            {
                // Duty Roulette. Light by default because most of the list is dungeons, with the
                // three eight-player roulettes named out of it.
                1 => RouletteIsFullParty(duty) ? FullParty() : LightParty(),

                2 => LightParty(),      // Dungeons
                3 => LightParty(),      // Guildhests
                4 => FullParty(),       // Trials
                5 => FullParty(),       // Raids
                6 => FullParty(),       // High-end Duty

                // PvP. Crystalline Conflict is entered as a pair and everything else - Frontline,
                // Rival Wings - forms as a light party.
                7 => IsCrystallineConflict(duty) ? Duo() : LightParty(),

                // Deep dungeons take whoever turns up. Four seats, no roles asked for: a Palace
                // run is assembled out of whatever four people are queueing, and a listing that
                // demands a tank and a healer is a listing that sits there.
                13 => FreeParty(4),

                // Gathering forays - Ocean Fishing and the Diadem. Eight open seats, because the
                // only jobs that can enter are the gatherers: a tank seat on one of these is a seat
                // nobody in the game is able to fill.
                12 => FreeParty(8),

                15 => LightParty(),     // V&C Dungeon Finder

                // Everything else - Gold Saucer, FATEs, Treasure Hunt, The Hunt, forays, field
                // operations, and "None". Eight seats in the standard shape: these are either
                // eight-player already or genuinely variable, and a full party is the one answer
                // that is never nonsense to start from.
                _ => FullParty(),
            };
        }

        /// <summary>
        /// Brings one preset in line with the rules above: strips auto-adjust where it no longer
        /// applies, and reshapes the seats to the party the duty actually fields. Returns whether
        /// anything moved, so the caller can save only when it did.
        ///
        /// What it will and will not overwrite is the whole of the difficulty here. Presets made
        /// before this existed all carry eight seats, but they carry them for two very different
        /// reasons: some were left at the old default and mean nothing, and some were built slot by
        /// slot and mean everything. Seats that were never reachable - a preset with auto-adjust on
        /// had its whole grid greyed out - or never chosen are replaced outright; seats somebody
        /// actually set are kept and only resized, oldest first, because slot one is the seat the
        /// party leader fills and the order after it is the order the listing shows.
        /// </summary>
        public static bool Normalize(PfPresetData preset)
        {
            bool changed = false;

            // Auto-adjust on content the game will not seek distributions for is dead weight that
            // still suppressed the slot editor. It goes, and its slots go with it: nothing under it
            // was ever editable, so there is nothing there worth keeping.
            bool strippedAuto = preset.AutoAdjustRoles && !SupportsAutoAdjust(preset.DutyCategoryId);
            if (strippedAuto)
            {
                preset.AutoAdjustRoles = false;
                changed = true;
            }

            var wanted = DefaultFor(preset.DutyCategoryId, preset.DutyName);
            var have = preset.Slots;

            if (strippedAuto || have == null || have.Count == 0 || NothingChosen(preset, have))
            {
                if (!SameAs(have, wanted))
                {
                    preset.Slots = wanted;
                    changed = true;
                }
                return changed;
            }

            if (have.Count == wanted.Count)
                return Renumber(have) || changed;

            // Hand-built and the wrong length. Keep as many of the chosen seats as the party has
            // room for; a party that grew takes the standard shape for the seats it gained.
            var resized = new List<RoleSlot>(wanted.Count);
            for (int i = 0; i < wanted.Count; i++)
                resized.Add(i < have.Count ? have[i] : wanted[i]);

            Renumber(resized);
            preset.Slots = resized;
            return true;
        }

        /// <summary>
        /// Whether these seats carry no decision - every one of them open to anyone, or crossed
        /// out. That is what the old eight-Free default looked like, and it is worth nothing.
        ///
        /// Except when the preset says so on purpose: "remove role restrictions" is a listing that
        /// deliberately asks for nothing, and replacing its seats would be overruling it.
        /// </summary>
        private static bool NothingChosen(PfPresetData preset, List<RoleSlot> slots)
        {
            if (preset.RemoveRoleRestrictions)
                return false;

            foreach (var slot in slots)
            {
                if (slot.Role != RoleType.Free && slot.Role != RoleType.Omit)
                    return false;
                if (slot.AcceptedJobFlags != 0)
                    return false;
            }

            return true;
        }

        /// <summary>Whether two seat lists say the same thing, so an already-correct preset is not
        /// reported as changed and does not trigger a save.</summary>
        private static bool SameAs(List<RoleSlot>? a, List<RoleSlot> b)
        {
            if (a == null || a.Count != b.Count)
                return false;

            for (int i = 0; i < a.Count; i++)
            {
                if (a[i].Role != b[i].Role || a[i].AcceptedJobFlags != b[i].AcceptedJobFlags)
                    return false;
            }

            return true;
        }

        /// <summary>Renumbers seats in place. Returns whether any were wrong.</summary>
        private static bool Renumber(List<RoleSlot> slots)
        {
            bool changed = false;
            for (int i = 0; i < slots.Count; i++)
            {
                if (slots[i].SlotIndex == i) continue;
                slots[i].SlotIndex = i;
                changed = true;
            }
            return changed;
        }

        /// <summary>
        /// The roulettes that field a full party: the two raid roulettes, trials, and Main Scenario
        /// - the last of which is an eight-player dungeon and reads like a four-player one.
        ///
        /// Matched on the name. ContentRoulette does carry a QueueMaxPlayers, but it is zero for
        /// every one of these rows: a roulette does not have a party size of its own, only the
        /// duties it can roll do.
        /// </summary>
        private static bool RouletteIsFullParty(string duty) =>
            Mentions(duty, "Trial")
            || Mentions(duty, "Normal Raid")
            || Mentions(duty, "Alliance Raid")
            || Mentions(duty, "Main Scenario");

        private static bool IsCrystallineConflict(string duty) =>
            Mentions(duty, "Crystalline Conflict");

        private static bool Mentions(string duty, string what) =>
            duty.IndexOf(what, StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>A tank, a healer, and two of anything that does damage.</summary>
        private static List<RoleSlot> LightParty() => Build(
            Role(RoleType.Tank),
            Role(RoleType.Healer),
            Dps(),
            Dps());

        /// <summary>
        /// Two of each role, four damage - the shape of every eight-player duty in the game, and
        /// the answer whenever nothing more specific applies.
        /// </summary>
        public static List<RoleSlot> FullParty() => Build(
            Role(RoleType.Tank),
            Role(RoleType.Tank),
            Role(RoleType.Healer),
            Role(RoleType.Healer),
            Dps(),
            Dps(),
            Dps(),
            Dps());

        /// <summary>You, and one other of anything at all.</summary>
        private static List<RoleSlot> Duo() => Build(
            Free(),
            Free());

        private static List<RoleSlot> FreeParty(int seats)
        {
            var slots = new RoleSlot[seats];
            for (int i = 0; i < seats; i++)
                slots[i] = Free();
            return Build(slots);
        }

        /// <summary>A slot for one role, accepting every job in it. AcceptedJobFlags of zero means
        /// "no job restriction" throughout this plugin - see GetSlotDisplayIcon.</summary>
        private static RoleSlot Role(RoleType role) =>
            new() { Role = role, AcceptedJobFlags = 0 };

        /// <summary>A slot for any damage job. Carries the role of one of the three and the jobs of
        /// all three, which is how the job selector expresses the same thing.</summary>
        private static RoleSlot Dps() =>
            new() { Role = RoleType.MeleeDPS, AcceptedJobFlags = AllDps };

        private static RoleSlot Free() =>
            new() { Role = RoleType.Free, AcceptedJobFlags = 0 };

        /// <summary>
        /// Numbers the seats. The list is exactly as long as the party is.
        ///
        /// It used to be padded to eight with Omit, on the theory that a uniform length is easier
        /// to consume. It is not: a four-player duty then carried four seats that had to be drawn,
        /// skipped, and explained everywhere, and the editor showed four crossed-out circles that
        /// meant nothing to anyone. A light party has four slots. The seats past it do not exist,
        /// which is a different statement from "exist, but omitted".
        /// </summary>
        private static List<RoleSlot> Build(params RoleSlot[] seats)
        {
            var slots = new List<RoleSlot>(seats.Length);

            for (int i = 0; i < seats.Length; i++)
            {
                seats[i].SlotIndex = i;
                slots.Add(seats[i]);
            }

            return slots;
        }
    }
}
