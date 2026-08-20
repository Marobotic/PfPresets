using System;

namespace PfPresets
{
    /// <summary>
    /// How much of another player's name to show on screen.
    ///
    /// Numbered explicitly and starting at the full name, so a config written before this setting
    /// existed - and any config where the value is missing or zero - reads back as the old
    /// behaviour rather than silently abbreviating everyone.
    /// </summary>
    public enum PlayerNameStyle
    {
        /// <summary>"John Smith" - the default.</summary>
        FullName = 0,

        /// <summary>"J.S."</summary>
        InitialsOnly = 1,

        /// <summary>"J. Smith"</summary>
        FirstInitialFullLast = 2,

        /// <summary>"John S."</summary>
        FirstNameLastInitial = 3,
    }

    /// <summary>
    /// Abbreviates player names for display.
    ///
    /// Display only. Nothing here touches the name used to look a character up, build a Lodestone
    /// or FFLogs URL, key a stored rating, call the game's kick, or go to the rating API - those
    /// need the real name, and an abbreviated one would quietly break them.
    /// </summary>
    public static class PlayerNameFormat
    {
        /// <summary>
        /// The name as it should appear on screen under the given style.
        ///
        /// A character name in FFXIV is exactly two words. Anything else - a placeholder like
        /// "Unknown", an NPC, a name we failed to read - has no halves to abbreviate, so it is
        /// returned untouched instead of being reduced to a letter that identifies nobody.
        /// </summary>
        public static string Apply(string name, PlayerNameStyle style)
        {
            if (style == PlayerNameStyle.FullName || string.IsNullOrWhiteSpace(name))
                return name;

            string[] parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
                return name;

            string first = parts[0];
            string last = parts[parts.Length - 1];

            return style switch
            {
                PlayerNameStyle.InitialsOnly => Initial(first) + Initial(last),
                PlayerNameStyle.FirstInitialFullLast => $"{Initial(first)} {last}",
                PlayerNameStyle.FirstNameLastInitial => $"{first} {Initial(last)}",
                _ => name,
            };
        }

        /// <summary>
        /// The label shown for each style in settings, with a worked example - the wording alone
        /// ("initials only") doesn't tell you what you are about to get.
        ///
        /// The example is a placeholder name on purpose. Anything shipped in the UI is read by every
        /// user, so it must not be a real character - least of all the author's own.
        /// </summary>
        public static string[] StyleLabels { get; } = new[]
        {
            "Full name  (John Smith)",
            "Initials only  (J.S.)",
            "First initial, full last name  (J. Smith)",
            "First name, last initial  (John S.)",
        };

        /// <summary>
        /// The same four styles as short labels, for a segmented control.
        ///
        /// THE EXAMPLE IS THE LABEL. "First initial, full last name" is a description of a format;
        /// "J. Smith" is the format, and it is four characters instead of twenty-nine - which is
        /// what lets all four sit in one track without any of them being cut. The wording that used
        /// to be here is on the help mark beside the control.
        /// </summary>
        public static string[] StyleLabelsShort { get; } = new[]
        {
            "John Smith",
            "J.S.",
            "J. Smith",
            "John S.",
        };

        private static string Initial(string part)
            => part.Length == 0 ? string.Empty : char.ToUpperInvariant(part[0]) + ".";
    }
}
