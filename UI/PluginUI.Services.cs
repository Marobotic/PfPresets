#if PFP_RATINGS
using System;

namespace PfPresets
{
    public partial class PluginUI
    {
        /// <summary>Set by Plugin after construction.</summary>
        internal RatingService? Ratings { get; set; }
        internal EncounterStore? Encounters { get; set; }

        /// <summary>Everyone ever met, kept permanently. Backs the Recent players list and is the
        /// whole of what search looks through.</summary>
        internal PlayerHistory? Players { get; set; }

        /// <summary>The character the plugin is acting as, or null when nobody is logged in. Set by
        /// Plugin, so the UI doesn't need its own copy of the game-state plumbing.</summary>
        internal Func<CharacterIdentity?>? LocalIdentity { get; set; }
    }
}
#endif
