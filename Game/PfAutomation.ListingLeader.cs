using System;
using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace PfPresets
{
    /// <summary>
    /// Who leads the listing being viewed, and where the game prints their name.
    ///
    /// Both halves are needed to put a score beside a name: the identity to ask about, and a
    /// rectangle to sit against. The rectangle is found by looking for the name rather than by
    /// node id - the same reasoning as <see cref="AtkHelpers.GetButtonLabel"/>. We already know
    /// exactly what the leader is called, so the node that says it identifies itself, and a patch
    /// that renumbers the window's nodes changes nothing here.
    /// </summary>
    public partial class PfAutomation
    {
        /// <summary>The listing detail window, the one a player opens on somebody's listing.</summary>
        private const string ListingDetailAddon = "LookingForGroupDetail";

        /// <summary>
        /// Name and home world of the viewed listing's leader, or false when no listing is open.
        /// </summary>
        public unsafe bool TryGetViewedListingLeader(out string name, out uint homeWorldId)
        {
            name = string.Empty;
            homeWorldId = 0;

            var agent = AgentLookingForGroup.Instance();
            if (agent == null)
                return false;

            var listing = agent->LastViewedListing;
            if (listing.ListingId == 0)
                return false;

            name = listing.LeaderString.ToString() ?? string.Empty;
            homeWorldId = listing.HomeWorld;

            return !string.IsNullOrWhiteSpace(name);
        }

        /// <summary>
        /// Screen rectangle of the text node printing the leader's name in the listing window, or
        /// false when the window is closed or the name isn't on it.
        ///
        /// ScreenX/ScreenY for the same reason the Recruit anchor uses them: they are what the game
        /// resolved this frame, already carrying the window position, the UI scale and every parent
        /// offset, while a node's own X/Y is relative to whatever component holds it.
        /// </summary>
        public unsafe bool TryGetListingLeaderNameRect(string leaderName, out Vector2 position, out Vector2 size)
        {
            position = default;
            size = default;

            if (string.IsNullOrWhiteSpace(leaderName))
                return false;

            var addon = GetVisibleAddon(ListingDetailAddon);
            if (addon == null)
                return false;

            int best = 0;
            var node = FindTextNode(&addon->UldManager, leaderName, ref best);
            if (node == null)
                return false;

            var res = &node->AtkResNode;
            float scale = addon->Scale <= 0f ? 1f : addon->Scale;

            // The width of the text, not of the node. A text node is as wide as the space the
            // window gave it, which for a name field is a good deal wider than the name - anchoring
            // to the node's edge would leave the score stranded out to the right of it. The node's
            // own width stays as the fallback for a game that declines to measure.
            ushort drawnW = 0, drawnH = 0;
            node->GetTextDrawSize(&drawnW, &drawnH);
            float textWidth = drawnW > 0 ? drawnW * scale : res->Width * scale;

            position = new Vector2(res->ScreenX, res->ScreenY);
            size = new Vector2(textWidth, res->Height * scale);
            return size.Y > 0f;
        }

        /// <summary>
        /// The text node that best matches a name, searching a node list and any components nested
        /// in it.
        ///
        /// Scored rather than first-match because the name can appear more than once on the window
        /// - a comment mentioning the leader would otherwise win by being earlier in the list. An
        /// exact match beats a line that merely starts with the name, which beats one that only
        /// contains it, so the node that is the name is preferred over the node that mentions it.
        /// </summary>
        private static unsafe AtkTextNode* FindTextNode(AtkUldManager* uld, string needle, ref int bestScore)
        {
            if (uld == null || uld->NodeList == null)
                return null;

            AtkTextNode* found = null;

            for (int i = 0; i < uld->NodeListCount; i++)
            {
                var node = uld->NodeList[i];
                if (node == null || !node->IsVisible())
                    continue;

                // Component nodes hold their own node list; the name usually lives inside one.
                if ((ushort)node->Type >= 1000)
                {
                    var component = ((AtkComponentNode*)node)->Component;
                    if (component == null)
                        continue;

                    var inner = FindTextNode(&component->UldManager, needle, ref bestScore);
                    if (inner != null)
                        found = inner;
                    continue;
                }

                if (node->Type != NodeType.Text)
                    continue;

                var text = ((AtkTextNode*)node)->NodeText.ToString();
                if (string.IsNullOrEmpty(text))
                    continue;

                int score =
                    string.Equals(text, needle, StringComparison.Ordinal) ? 3 :
                    text.StartsWith(needle, StringComparison.Ordinal) ? 2 :
                    text.Contains(needle, StringComparison.Ordinal) ? 1 : 0;

                if (score > bestScore)
                {
                    bestScore = score;
                    found = (AtkTextNode*)node;
                }
            }

            return found;
        }
    }
}
