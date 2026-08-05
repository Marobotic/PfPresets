using System.Numerics;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace PfPresets
{
    /// <summary>
    /// Where the Party Finder window's "Recruit Members" button is on screen, so the plugin can
    /// park its own button against it.
    ///
    /// Measured off the game's node rather than off the window: the button sits in the window's
    /// bottom bar, and anything derived from the window's own corner would drift the moment the
    /// bar's layout changes or the player rescales the UI.
    /// </summary>
    public partial class PfAutomation
    {
        /// <summary>
        /// Component button id of "Recruit Members" inside LookingForGroup. The same id the apply
        /// sequence clicks to open the recruitment window (see StepOpeningLfg), so if a patch ever
        /// moves it, both break together and there is one place to look.
        /// </summary>
        private const uint RecruitMembersButtonId = 46;

        /// <summary>
        /// Screen rectangle of the Party Finder window, or false when it isn't open. Used as the
        /// bounds the plugin's button has to stay inside.
        /// </summary>
        public unsafe bool TryGetPartyFinderWindowRect(out Vector2 position, out Vector2 size)
        {
            position = default;
            size = default;

            var addon = GetVisibleAddon("LookingForGroup");
            if (addon == null || addon->RootNode == null)
                return false;

            float scale = addon->Scale <= 0f ? 1f : addon->Scale;
            position = new Vector2(addon->X, addon->Y);
            size = new Vector2(addon->RootNode->Width * scale, addon->RootNode->Height * scale);
            return size.X > 0f && size.Y > 0f;
        }

        /// <summary>
        /// Screen rectangle of the "Recruit Members" button, or false when the Party Finder is
        /// closed or the button isn't there.
        ///
        /// ScreenX/ScreenY rather than the node's own X/Y: those are relative to whatever component
        /// the button is nested in, while the screen values are what the game itself resolved for
        /// this frame - already carrying the window's position, the UI scale and any parent offset.
        /// </summary>
        public unsafe bool TryGetRecruitButtonRect(out Vector2 position, out Vector2 size)
        {
            position = default;
            size = default;

            var addon = GetVisibleAddon("LookingForGroup");
            if (addon == null)
                return false;

            var button = addon->GetComponentButtonById(RecruitMembersButtonId);
            if (button == null)
                return false;

            var node = button->OwnerNode;
            if (node == null || !node->AtkResNode.IsVisible())
                return false;

            float scale = addon->Scale <= 0f ? 1f : addon->Scale;
            position = new Vector2(node->AtkResNode.ScreenX, node->AtkResNode.ScreenY);
            size = new Vector2(node->AtkResNode.Width * scale, node->AtkResNode.Height * scale);
            return size.X > 0f && size.Y > 0f;
        }
    }
}
