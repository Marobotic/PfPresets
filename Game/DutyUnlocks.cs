using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;

namespace PfPresets
{
    /// <summary>
    /// Whether the character sitting in front of you has actually unlocked a duty.
    ///
    /// A preset is a saved intention, not a claim about your progress, so nothing stops you writing
    /// one for a fight you have not reached. Posting it is another matter: the game will not let a
    /// listing go up for content the character cannot enter, and the plugin used to find that out
    /// by driving the whole automation into a Party Finder window that then refused it.
    ///
    /// UNKNOWN MEANS UNLOCKED, EVERYWHERE IN HERE. The two questions the client will answer are
    /// about InstanceContent and PublicContent rows; roulettes, FATE locations and Gold Saucer
    /// content have neither, and the honest answer for those is "no idea". Blocking on "no idea"
    /// would take away a listing somebody can legitimately post, which is a far worse failure than
    /// letting a doomed one through - that one only costs the trip to the window and back.
    /// </summary>
    public static class DutyUnlocks
    {
        /// <summary>ContentFinderCondition.ContentLinkType for a row whose Content points into
        /// InstanceContent - every dungeon, trial, raid and deep dungeon.</summary>
        private const byte LinkInstanceContent = 1;

        /// <summary>...and into PublicContent: Eureka, Bozja, the Occult Crescent, the Diadem.
        /// The field operations, in other words, which are not instances in the same sense.</summary>
        private const byte LinkPublicContent = 3;

        /// <summary>
        /// The quests that open a whole Party Finder category, where the category is a system
        /// rather than a list of instances.
        ///
        /// HARDCODED, AND THERE IS NO WAY AROUND IT. Every other gate in this plugin is read out of
        /// the sheets, because the sheets carry it: a duty names its unlock quest and its content
        /// row, and a roulette is open when something it rolls is. These four are different. The
        /// Wolves' Den, the Gold Saucer, treasure maps and the hunt boards are unlocked by a quest
        /// that is attached to nothing the Party Finder can see - there is no row anywhere joining
        /// "the PvP category" to "A Pup No Longer".
        ///
        /// So the ids are written down, with the quest names beside them, and any one of a group
        /// counts: the three Grand Companies each have their own copy of the same quest and nobody
        /// does more than one. Quest row ids do not get renumbered, so these age well - but they are
        /// the first thing to check if a category starts claiming to be locked for everybody.
        /// </summary>
        private static readonly (int CategoryId, uint[] AnyOf)[] CategorySystemQuests =
        {
            // PvP - "A Pup No Longer", one per Grand Company.
            (7, new uint[] { 66640, 66641, 66642 }),

            // Gold Saucer - "It Could Happen to You".
            (8, new uint[] { 65970 }),

            // Treasure Hunt - "Treasures and Tribulations".
            (10, new uint[] { 66747 }),

            // FATEs - "It Could Happen to You", the level 15 quest.
            (9, new uint[] { 65970 }),

            // The Hunt - "Let the Hunt Begin", one per Grand Company.
            (11, new uint[] { 67099, 67100, 67101 }),
        };

        /// <summary>Whether any of a zone's aetherytes is attuned - the teleport menu's own answer
        /// to "have you been here".</summary>
        public static unsafe bool AnyAetheryteAttuned(uint[] aetheryteIds)
        {
            var state = UIState.Instance();
            if (state == null)
                return true;

            foreach (uint id in aetheryteIds)
            {
                if (state->IsAetheryteUnlocked(id))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Whether the system behind a category is open at all.
        ///
        /// Separate from whether anything in the category is unlocked, and asked first: a character
        /// who has never set foot in the Gold Saucer has every Gold Saucer duty reading as available
        /// because none of them is individually gated. The gate is on the door, not the rooms.
        /// </summary>
        public static bool IsCategorySystemUnlocked(int categoryId)
        {
            foreach (var (id, quests) in CategorySystemQuests)
            {
                if (id != categoryId)
                    continue;

                return AnyQuestDone(quests);
            }

            return true;
        }

        /// <summary>
        /// The level of the job the character is on right now, or 0 when there is nobody to ask.
        ///
        /// The job you are on, not the highest you have - the Duty Finder greys a roulette against
        /// the class you are currently holding, and a preset is posted on the job you are holding
        /// too. Zero means "no answer", and callers treat that the way everything else in here
        /// treats an unknown: it does not lock anything.
        /// </summary>
        public static unsafe int CurrentJobLevel()
        {
            var state = PlayerState.Instance();
            if (state == null || !state->IsLoaded)
                return 0;

            return state->CurrentLevel;
        }

        private static bool AnyQuestDone(uint[] quests)
        {
            foreach (uint q in quests)
            {
                if (QuestManager.IsQuestComplete(q))
                    return true;
            }

            return false;
        }

        public static unsafe bool IsUnlocked(DutyEntry? duty)
        {
            if (duty == null)
                return true;

            // Logged out there is no character to ask about, and "not unlocked" would be a lie
            // about a character rather than a fact about one. The instance is only checked for
            // that; the questions themselves are static.
            if (UIState.Instance() == null)
                return true;

            // BOTH GATES, AND EITHER ONE CAN SAY NO.
            //
            // The content check alone was letting things through - the Occult Crescent among them.
            // A duty that names an unlock quest is not open until that quest is done whatever the
            // content record says, and the two are answered from different places in the save, so
            // agreeing with each other is not something to rely on.
            return QuestDone(duty.UnlockQuestId)
                && QuestDone(duty.UnlockQuestId2)
                && ContentUnlocked(duty);
        }

        /// <summary>A quest gate: satisfied when there is no quest, or when it is complete.</summary>
        private static bool QuestDone(uint questId) =>
            questId == 0 || QuestManager.IsQuestComplete(questId);

        /// <summary>The content record. True for anything with no content row behind it at all -
        /// roulettes and FATE locations - and for the link types the client will not answer for.
        /// </summary>
        private static unsafe bool ContentUnlocked(DutyEntry duty)
        {
            if (duty.ContentRowId == 0)
                return true;

            return duty.ContentLinkType switch
            {
                LinkInstanceContent => UIState.IsInstanceContentUnlocked(duty.ContentRowId),
                LinkPublicContent => UIState.IsPublicContentUnlocked(duty.ContentRowId),
                _ => true,
            };
        }
    }
}
