#if PFP_RATINGS
using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;

namespace PfPresets
{
    /// <summary>
    /// The one time the plugin asks you to go and vote.
    ///
    /// WHY IT IS ALLOWED TO INTERRUPT AT ALL. This poll decides whether the rating system survives,
    /// and a vote that only the people who happen to open the right tab hear about is a vote decided
    /// by who was curious rather than by what the community wants. So it is put in front of
    /// everybody once.
    ///
    /// AND WHY IT IS SO HEAVILY RESTRAINED. Nagging people inside their game about the developer's
    /// poll is exactly the behaviour that makes somebody uninstall a plugin, and this one is already
    /// unpopular with a slice of the people who use it. So:
    ///
    ///   once per session         it never reappears after it has been dismissed
    ///   never after voting       the moment the vote lands it has nothing to ask for
    ///   never without a poll     no poll running, no window, no code path that can show one
    ///   after, never during      it waits for a preset to be applied or a rating to be cast,
    ///                            so it lands on a finished action rather than across one
    ///
    /// The two moments are deliberate. Applying a preset is the end of a task, and casting the last
    /// rating after a duty is the end of another - both are the point where somebody looks up, which
    /// is the only honest time to ask for thirty seconds.
    /// </summary>
    public partial class PluginUI
    {
        private bool nudgeOpen;
        private bool nudgeSpent;

        private const float NudgeWidth = 340f;

        /// <summary>
        /// Offers the nudge, if this is a moment worth offering it in.
        ///
        /// Called from the two places a task finishes. Silent in every case that is not exactly
        /// right, which is most of them.
        /// </summary>
        internal void OfferVoteNudge()
        {
            if (nudgeSpent || nudgeOpen) return;

            // Said once, meant for good. Both of these outlive the session: the setting is on disk,
            // and pollVoted is set from the server's own answer every time the poll is read, so a
            // vote cast anywhere - here, the website, another machine on this connection - stops
            // this asking forever rather than until the next restart.
            if (config.VotePromptSilenced) return;
            if (pollVoted) return;

            if (poll is not { Open: true }) return;

            nudgeOpen = true;
        }

        private void DrawVoteNudge()
        {
            if (!nudgeOpen)
                return;

            var p = poll;

            // The poll can close, or the vote can land on another surface, while this is on screen.
            if (p is not { Open: true } || pollVoted)
            {
                nudgeOpen = false;
                nudgeSpent = true;
                return;
            }

            bool open = nudgeOpen;

            if (BeginDialog("Have your say", "PfPresetsVoteNudge", NudgeWidth, ref open))
            {
                ImGui.PushTextWrapPos(ImGui.GetContentRegionMax().X - 16);

                using (UiBodyFont.Push())
                    ImGui.TextColored(Ink, p.Question);

                ImGui.Dummy(new Vector2(0, 6));

                ImGui.TextColored(Dim,
                    "The community is deciding what happens to the rating system, and the option "
                    + "with the most votes ships in the next update.");

                ImGui.PopTextWrapPos();

                if (p.ClosesAt.HasValue)
                {
                    ImGui.Dummy(new Vector2(0, 6));
                    using (UiHelpFont.Push())
                        ImGui.TextColored(Faint,
                            $"One vote per person   ·   Closes {p.ClosesAt.Value.ToLocalTime():d MMMM}");
                }

                ImGui.Dummy(new Vector2(0, 14));

                if (DrawPrimaryButton("Have a look##nudgevote", new Vector2(140, ButtonHeight)))
                {
                    activeTab = MainTab.Vote;
                    isMainWindowVisible = true;
                    Dismiss();
                }

                ImGui.SameLine(0, 10);

                if (!string.IsNullOrWhiteSpace(p.PostUrl)
                    && DrawAccentOutlineButton("Read the post ↗##nudgepost",
                        new Vector2(150, ButtonHeight)))
                {
                    Dalamud.Utility.Util.OpenLink(p.PostUrl);
                    Dismiss();
                }

                ImGui.Dummy(new Vector2(0, 10));

                // NOT A "REMIND ME LATER". There is no later - this poll is asked once and then
                // never again, and an option promising to come back would be a promise to
                // interrupt. What this button adds is the harder promise: not for this poll, and
                // not for any future one either.
                if (DrawNeutralButton("Don't ask again##nudgenever", new Vector2(150, ButtonHeight)))
                {
                    config.VotePromptSilenced = true;
                    config.Save();
                    Dismiss();
                }

                ImGui.SameLine(0, 10);
                ImGui.AlignTextToFramePadding();

                using (UiHelpFont.Push())
                    ImGui.TextColored(Faint, "Closing this also stops it for now.");

                EndDialog();
            }

            // Closing it by the title bar spends it too. Somebody who shuts a window has answered.
            if (!open)
                Dismiss();

            void Dismiss()
            {
                nudgeOpen = false;
                nudgeSpent = true;
            }
        }
    }
}
#endif
