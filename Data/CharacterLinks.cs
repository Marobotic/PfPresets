#if PFP_RATINGS
using System;

namespace PfPresets
{
    /// <summary>
    /// Builds the external character-site URLs the profile card links out to.
    ///
    /// Both live here rather than inline at the call site so that when a site changes its paths -
    /// which they do, and which we can't detect from inside the game - there is exactly one place
    /// to fix, and it's a one-line change.
    /// </summary>
    public static class CharacterLinks
    {
        /// <summary>
        /// FFLogs character page. The region slug comes from the world's Region byte via
        /// <see cref="WorldHelper.GetFfLogsRegion"/>; this path shape has been stable for years.
        /// </summary>
        public static string FfLogs(string name, string world, string region)
            => $"https://www.fflogs.com/character/{Esc(region)}/{Esc(world)}/{Esc(name)}";

        /// <summary>
        /// Tomestone character page.
        ///
        /// UNVERIFIED: tomestone.gg blocks automated requests, so this path was not confirmed
        /// against the live site. Check it once in-game and correct this single line if it is
        /// wrong - everything else about the link button is independent of the format.
        /// </summary>
        public static string Tomestone(string name, string world)
            => $"https://tomestone.gg/character-name/{Esc(world)}/{Esc(name)}";

        /// <summary>
        /// The Lodestone character search, used as the always-correct fallback. Unlike the two
        /// above this needs no id and no guessed path, so it is what the UI offers when a world
        /// can't be resolved.
        /// </summary>
        public static string LodestoneSearch(string name, string world)
            => "https://na.finalfantasyxiv.com/lodestone/character/"
             + $"?q={Uri.EscapeDataString(name)}&worldname={Uri.EscapeDataString(world)}";

        private static string Esc(string s) => Uri.EscapeDataString(s.Trim());
    }
}
#endif
