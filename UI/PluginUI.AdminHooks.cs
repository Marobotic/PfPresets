namespace PfPresets
{
    /// <summary>
    /// Hook points for the moderator build.
    ///
    /// These are partial methods with no implementation in this repository, and that is the whole
    /// mechanism: a partial method whose body does not exist is erased by the compiler along with
    /// every call to it. So an ordinary build contains no moderator code, no moderator endpoints
    /// and no branch that could be patched to reach any - not disabled at runtime, not compiled out
    /// by a flag somebody could flip, simply absent from the assembly.
    ///
    /// The implementations live in files that are gitignored and exist only on the two machines
    /// that need them. Nothing here reveals what they do; that is not the point of hiding them, but
    /// it is a fair consequence. The point is that thirty thousand people should not be carrying
    /// code that can ban other players, however well guarded it is - the safest version of that
    /// code is the one that was never shipped.
    /// </summary>
    public partial class PluginUI
    {
        /// <summary>The version label in the footer was clicked. Counts toward enrolment.</summary>
        partial void OnVersionLabelClicked();

        /// <summary>
        /// Lets an optional component add to the version string in the corner.
        ///
        /// `ref` rather than a return value for the same reason as everything else here: a partial
        /// method has to return void to be erasable, so anything it produces has to come back
        /// through a parameter. In an ordinary build this is erased and the label is untouched.
        /// </summary>
        partial void DecorateVersionLabel(ref string label);

        /// <summary>Anything that draws over the whole window, before the frame ends.</summary>
        partial void DrawPanelOverlay();

        /// <summary>Appends any tabs an optional component contributes to the shared list.</summary>
        partial void AddExtraTabs(System.Collections.Generic.List<(string, Dalamud.Interface.FontAwesomeIcon, MainTab)> tabs);

        /// <summary>
        /// Draws the body for an extra tab when one is selected, and says so via `handled`.
        ///
        /// `handled` rather than a return value because a partial method must return void to be
        /// erasable - a non-void one has to have an implementation, which would defeat the purpose.
        /// </summary>
        partial void DrawPanelTabBody(ref bool handled);

        /// <summary>An extra block at the foot of the settings tab.</summary>
        partial void DrawPanelSettings();

        /// <summary>
        /// Moderator controls beside the site links on a profile card.
        ///
        /// Given the card's right edge and the row to sit on, and reports back how much room it
        /// took so the site icons can shuffle left by exactly that much.
        /// </summary>
        partial void DrawSubjectActions(CharacterIdentity who, float rowY, ref float right);
    }
}
