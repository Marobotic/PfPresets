#if PFP_RATINGS
using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace PfPresets
{
    /// <summary>
    /// The Settings tab.
    ///
    /// Deliberately short. The previous version carried three paragraphs of privacy text, two
    /// destructive buttons, a character counter and an advanced section - most of it explaining
    /// things the toggles already say. What survives is the switches, one line about what leaves
    /// the machine with the detail behind a hover, and the one control that deletes data.
    /// </summary>
    public partial class PluginUI
    {
        private void DrawSettingsTab()
        {
            ImGui.BeginChild("SettingsBody", new Vector2(ImGui.GetWindowWidth() - 16, -1), false);
            try
            {
                ImGui.Dummy(new Vector2(0, 6));
                ImGui.Indent(8);

                DrawSectionLabel("PARTY FINDER");
                DrawSetting("Auto-refresh listing", () => config.AutoRefresherEnabled,
                    v => config.AutoRefresherEnabled = v,
                    IsRecruitmentRefresherActive()
                        ? "Handled by RecruitmentRefresher while that plugin is running."
                        : "Re-posts your listing before it expires so it stays near the top.");

                DrawSetting("Auto-adjust locked slots", () => config.AutoAdjustLockedJobsEnabled,
                    v => config.AutoAdjustLockedJobsEnabled = v,
                    "Keeps one-job slots in step with who has already joined.");

                ImGui.Dummy(new Vector2(0, 10));
                DrawSectionLabel("RATINGS");

                DrawSetting("Enable ratings system", () => config.RatingsEnabled,
                    v => config.RatingsEnabled = v,
                    "Rate players you finish duties with and look anyone up. Off removes the "
                    + "Ratings tab and every score; presets are unaffected.");

                if (config.RatingsEnabled)
                {
                    DrawSetting("Ask after a duty", () => config.PostDutyPromptEnabled,
                        v => config.PostDutyPromptEnabled = v,
                        "When a duty ends, a small window offers to rate the group once.");

                    DrawSetting("Show ratings on your party", () => config.PartyRatingsEnabled,
                        v => config.PartyRatingsEnabled = v,
                        "Shows each party member's rating and prog point beside their name.");

                }

                ImGui.Dummy(new Vector2(0, 10));
                DrawSectionLabel("DATA");
                DrawSetting("Anonymous usage stats", () => config.AnalyticsEnabled,
                    v => config.AnalyticsEnabled = v,
                    "Sends a count of installs. No names, no duties, nothing you do.");
                DrawClearData();

                ImGui.Dummy(new Vector2(0, 12));
                DrawSectionLabel("ABOUT");
                if (DrawSecondaryButton("View changelog##OpenChangelog", new Vector2(150, 24)))
                    isChangelogVisible = true;

                ImGui.Unindent(8);
            }
            finally
            {
                ImGui.EndChild();
            }
        }

        // The rating server override has no settings UI on purpose.
        //
        // It is a development affordance - pointing a dev build at a local API - and it is the
        // one setting where a wrong value silently breaks every rating, report and progress
        // lookup at once. Nobody running the plugin normally has a reason to change it, and
        // showing the address invites exactly that. RatingApiBaseUrl is still read from the
        // config file for anyone who genuinely needs it.

        /// <summary>
        /// A toggle with its explanation printed underneath it.
        ///
        /// Inline rather than on hover. A tooltip hides the one thing that makes a setting
        /// decidable behind an action the reader has to guess is worth taking, and on a page with
        /// six switches that is six things to go hunting for.
        /// </summary>
        private void DrawSetting(string label, Func<bool> get, Action<bool> set, string explanation)
        {
            bool value = get();
            if (DrawStyledCheckbox($"{label}##set{label}", ref value))
            {
                set(value);
                config.Save();
            }

            ImGui.Indent(22);
            ImGui.PushTextWrapPos(ImGui.GetContentRegionMax().X - 8);
            ImGui.TextColored(TextMuted, explanation);
            ImGui.PopTextWrapPos();
            ImGui.Unindent(22);

            ImGui.Dummy(new Vector2(0, 8));
        }

        /// <summary>A checkbox with an optional hint, saving on change.</summary>
        private void DrawToggle(string label, Func<bool> get, Action<bool> set, string? hint)
        {
            bool value = get();
            if (DrawStyledCheckbox($"{label}##set{label}", ref value))
            {
                set(value);
                config.Save();
            }
            if (hint != null && ImGui.IsItemHovered())
                PaddedTooltip(hint);
        }

        /// <summary>Deletes everything the plugin holds about other people, plus the server-side
        /// cooldown ledger. One button and one question, through the shared dialog.</summary>
        private void DrawClearData()
        {
            ImGui.Dummy(new Vector2(0, 2));

            ImGui.PushTextWrapPos(ImGui.GetContentRegionMax().X - 8);
            ImGui.TextColored(TextMuted,
                "Forgets everyone you have met and rated. Presets are not touched.");
            ImGui.PopTextWrapPos();

            ImGui.Dummy(new Vector2(0, 6));

            if (!DrawSecondaryButton("Clear local data##ClearData", new Vector2(150, 24)))
                return;

            AskConfirm("Clear local data", "Forget everyone you've met and rated?", "Clear it",
                () =>
                {
                    Encounters?.Clear();
                    History?.Clear();
                    config.LocalCooldowns.Clear();
                    config.Save();
                    _ = Ratings?.ForgetServerHistoryAsync();
                },
                detail: "Also clears your rating cooldowns on the server.");
        }
    }
}
#endif
