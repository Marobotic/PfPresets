#if PFP_RATINGS
using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;

namespace PfPresets
{
    /// <summary>
    /// The listing leader's score, printed beside their name on the game's own listing window.
    ///
    /// This is the moment the score is actually worth something. Everywhere else the plugin shows
    /// a rating you are already in a party with the person; here you are deciding whether to join
    /// at all, and the decision is made on that window, in the seconds before pressing Join. A
    /// number that only appears afterwards is a number that arrives too late to be used.
    ///
    /// Drawn as an overlay in the same manner as "Save as Preset": a transparent, borderless host
    /// window holding nothing but the score, following the name and vanishing with the window.
    /// Anchored to the name's text node rather than to the window, so it stays put at any UI scale
    /// and through any patch that moves the window's furniture around.
    ///
    /// Read-only, deliberately. Voting belongs where you have met the person; this is a glance.
    /// </summary>
    public partial class PluginUI
    {
        /// <summary>Kept off the name by a hair, so the two read as a name and its score rather
        /// than as one run-on string.</summary>
        private const float LeaderRatingGap = 8f;

        /// <summary>Breathing room inside the host window, so a hover target isn't pixel-tight.</summary>
        private const float LeaderRatingPadX = 4f;

        private void DrawListingLeaderRatingOverlay()
        {
            if (!config.RatingsEnabled || !config.ShowListingLeaderRating)
                return;

            if (Ratings == null || Worlds == null)
                return;

            if (!pfAutomation.TryGetViewedListingLeader(out string leaderName, out uint homeWorldId))
                return;

            // No world, no identity: a rating is keyed on name@world, and asking about a bare name
            // would be asking about whoever on any world happens to share it.
            string world = Worlds.GetWorldName(homeWorldId);
            if (string.IsNullOrWhiteSpace(world))
                return;

            var identity = new CharacterIdentity(leaderName, world);
            if (!identity.IsValid)
                return;

            if (!pfAutomation.TryGetListingLeaderNameRect(leaderName, out var namePos, out var nameSize))
                return;

            // Reads and requests together, the same way the party rows do - one name on screen is
            // one request, and the service caches and de-duplicates behind this.
            var rating = Ratings.Get(identity);

            bool hasScore = rating is { Gated: false, OptedOut: false } && rating.Count > 0;
            bool loading = rating == null && Ratings.IsLoading(identity);

            // Nothing for the many people nobody has voted on, and nothing for a score being
            // withheld. An empty space next to a name says "no opinion recorded", which is true;
            // a dash or a zero next to a name says something about the person, which would not be.
            if (!hasScore && !loading)
                return;

            const string dots = "···";
            int shown = hasScore ? Math.Abs(rating!.Score) : 0;
            float contentW = hasScore ? ArrowCountWidth(shown) : ImGui.CalcTextSize(dots).X;
            float height = MathF.Max(nameSize.Y, ImGui.GetTextLineHeightWithSpacing());

            var size = new Vector2(contentW + LeaderRatingPadX * 2f, height);

            // Centred on the name's line rather than aligned to its top: the score's own line
            // height is not the game's, and matching tops would leave it sitting high.
            var anchor = new Vector2(
                namePos.X + nameSize.X + LeaderRatingGap,
                namePos.Y + (nameSize.Y - height) * 0.5f);

            ImGui.SetNextWindowPos(GameToScreen(anchor), ImGuiCond.Always);
            ImGui.SetNextWindowSize(size, ImGuiCond.Always);

            ImGui.PushStyleColor(ImGuiCol.WindowBg, new Vector4(0f, 0f, 0f, 0f));
            ImGui.PushStyleColor(ImGuiCol.Border, new Vector4(0f, 0f, 0f, 0f));
            ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 0f);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(LeaderRatingPadX, 0f));

            const ImGuiWindowFlags flags =
                ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove |
                ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse |
                ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoFocusOnAppearing |
                ImGuiWindowFlags.NoNavFocus | ImGuiWindowFlags.NoBackground |
                ImGuiWindowFlags.NoSavedSettings;

            try
            {
                if (ImGui.Begin("##PfPresetsListingLeaderRating", flags))
                {
                    if (hasScore)
                    {
                        DrawArrowCount(
                            rating!.Score >= 0 ? FontAwesomeIcon.CaretUp : FontAwesomeIcon.CaretDown,
                            shown, NetScoreColor(rating.Score));

                        if (ImGui.IsItemHovered())
                        {
                            PaddedTooltip($"{identity}\n\n"
                                + $"Rating {rating.Score}\n"
                                + $"{rating.Upvotes} up, {rating.Downvotes} down, {rating.Count} votes");
                        }
                    }
                    else
                    {
                        ImGui.AlignTextToFramePadding();
                        ImGui.TextColored(TextMuted, dots);
                    }
                }
            }
            finally
            {
                ImGui.End();
                ImGui.PopStyleVar(3);
                ImGui.PopStyleColor(2);
            }
        }
    }
}
#endif
