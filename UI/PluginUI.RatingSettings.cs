#if PFP_RATINGS
using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace PfPresets
{
    /// <summary>
    /// The Settings tab: ruled sections of toggle rows, each row one switch and one line saying
    /// what it does.
    ///
    /// Every explanation lives on the "?" beside its control. Printed underneath, the same
    /// sentences turned a page of six decisions into a wall of prose where the switches were the
    /// hardest thing to find; on the dot, the page reads as a list of settings and the reasoning is
    /// one hover away.
    /// </summary>
    public partial class PluginUI
    {
        /// <summary>Below this the sections stack into one column. Two columns of settings in a
        /// narrow window is two columns of wrapped fragments.</summary>
        private const float SettingsTwoColumnWidth = 720f;

        private void DrawSettingsTab()
        {
            ImGui.BeginChild("SettingsBody", new Vector2(ImGui.GetWindowWidth() - 16, -1), false);
            try
            {
                ImGui.Dummy(new Vector2(0, 8));
                ImGui.Indent(12);

                float avail = ImGui.GetContentRegionAvail().X - 12;
                if (avail >= SettingsTwoColumnWidth)
                {
                    float columnWidth = (avail - 18f) * 0.5f;

                    ImGui.BeginChild("SettingsColLeft", new Vector2(columnWidth, -1), false);
                    DrawPartyFinderSettings();
                    DrawPlayerNameSettings();
                    DrawRatingsSettings();
                    ImGui.EndChild();

                    ImGui.SameLine(0, 18);

                    ImGui.BeginChild("SettingsColRight", new Vector2(columnWidth, -1), false);
                    DrawDataSettings();
                    DrawAppearanceSettings();
                    DrawAboutSettings();
                    ImGui.EndChild();
                }
                else
                {
                    DrawPartyFinderSettings();
                    DrawPlayerNameSettings();
                    DrawRatingsSettings();
                    DrawDataSettings();
                    DrawAppearanceSettings();
                    DrawAboutSettings();
                }

                // Erased entirely in an ordinary build - see PluginUI.AdminHooks.cs.
                DrawAdminSettings();

                ImGui.Unindent(12);
            }
            finally
            {
                ImGui.EndChild();
            }
        }

        // ── Sections ──────────────────────────────────────────────

        /// <summary>
        /// How far section content sits inside the column.
        ///
        /// Not decoration: the swatches' selected ring and the outlined buttons' borders are drawn
        /// outside their own rects, and a child window clips anything left of its content origin -
        /// so at zero inset the purple swatch lost the whole left side of its ring and the buttons
        /// lost their left border. The rules still span the full column width; only the content
        /// steps in.
        /// </summary>
        private const float SectionInset = 6f;

        private void DrawPartyFinderSettings()
        {
            DrawSectionLabel("Party Finder");
            ImGui.Indent(SectionInset);

            DrawSetting("\"Apply a recruitment preset\" button", () => config.ShowPartyFinderButton,
                v => config.ShowPartyFinderButton = v,
                "Puts the button next to Recruit Members, so the plugin opens from where you "
                + "already are. Off leaves the Party Finder untouched; /pfa still opens it.");

            DrawSetting("\"Save as Preset\" button", () => config.ShowSaveListingButton,
                v => config.ShowSaveListingButton = v,
                "Puts a button under a listing you're viewing that keeps it as one of your "
                + "presets. Off leaves listings untouched.");

            DrawSetting("Leader's score on a listing", () => config.ShowListingLeaderRating,
                v => config.ShowListingLeaderRating = v,
                "Shows the listing leader's community score beside their name while you're "
                + "viewing it, so you see it before joining rather than after. Needs ratings on.");

            DrawSetting("Auto-refresh listing", () => config.AutoRefresherEnabled,
                v => config.AutoRefresherEnabled = v,
                IsRecruitmentRefresherActive()
                    ? "Handled by RecruitmentRefresher while that plugin is running."
                    : "Re-posts your listing before it expires so it stays near the top.");

            DrawSetting("Auto-adjust locked slots", () => config.AutoAdjustLockedJobsEnabled,
                v => config.AutoAdjustLockedJobsEnabled = v,
                "Keeps one-job slots in step with who has already joined.", last: true);

            ImGui.Unindent(SectionInset);
        }

        private void DrawPlayerNameSettings()
        {
            DrawSectionLabel("Player names");
            ImGui.Indent(SectionInset);

            DrawChoiceSetting("Show names as", PlayerNameFormat.StyleLabels,
                () => (int)config.PlayerNameStyle,
                v => config.PlayerNameStyle = (PlayerNameStyle)v,
                "Applies everywhere a name appears - the recruitment card, your party, ratings, "
                + "recent players and profiles. Lookups and links still use the full name.");

            ImGui.Unindent(SectionInset);
        }

        private void DrawRatingsSettings()
        {
            DrawSectionLabel("Ratings");
            ImGui.Indent(SectionInset);

            // THIS TOGGLE IS AN OPT-OUT, not a local preference, and the wording says so because
            // the consequence outlives the plugin: once approved the server holds the flag, so
            // uninstalling does not quietly put somebody back into a system they left.
            //
            // It only appears while logged in. The request names the character it is for, and
            // there is no character to name from the title screen - a toggle that files nothing is
            // worse than one that is not there.
            bool loggedIn = LocalIdentity?.Invoke() is { IsValid: true };

            if (!loggedIn)
            {
                ImGui.TextColored(Dim, "Log in to a character to change this.");
                ImGui.Dummy(new Vector2(0, 8));
                DrawRuleHair(padAbove: 0f, padBelow: 10f);
                DrawBroadcastSetting();
                ImGui.Unindent(SectionInset);
                return;
            }

            DrawSetting("Enable ratings system", () => config.RatingsEnabled,
                SetRatingsEnabled,
                "Disabling this opts you out of the rating system: players cannot view your "
                + "ratings, or rate you. It stays opted out even after uninstalling the plugin, "
                + "until you enable this option again.",
                last: !config.RatingsEnabled);

            if (ratingOptOutNote.Length > 0)
            {
                ImGui.Indent(SectionInset);
                using (UiHelpFont.Push())
                    ImGui.TextColored(ratingOptOutFailed ? Negative : Faint, ratingOptOutNote);
                ImGui.Unindent(SectionInset);
                ImGui.Dummy(new Vector2(0, 6));
            }

            if (!config.RatingsEnabled)
            {
                // Broadcasting is a different system and does not go with it - somebody who wants
                // nothing to do with ratings may still want their Ultimate clear celebrated, and
                // burying that setting behind this one would decide for them.
                DrawRuleHair(padAbove: 10f, padBelow: 10f);
                DrawBroadcastSetting();

                ImGui.Unindent(SectionInset);
                return;
            }

            DrawSetting("Ask after a duty", () => config.PostDutyPromptEnabled,
                v => config.PostDutyPromptEnabled = v,
                "When a duty ends, a small window offers to rate the group once.");

            DrawSetting("Show ratings on your party", () => config.PartyRatingsEnabled,
                v => config.PartyRatingsEnabled = v,
                "Shows each party member's rating and prog point beside their name.");

            DrawBroadcastSetting();

            ImGui.Unindent(SectionInset);
        }

        // ── The opt-out ───────────────────────────────────────────

        private string ratingOptOutNote = string.Empty;
        private bool ratingOptOutFailed;

        /// <summary>
        /// Turning ratings off opts this character out, on the server.
        ///
        /// The local flag moves first so the UI answers immediately, and the server is told
        /// straight after. If it refuses - a machine that has not been signing in as this character
        /// long enough, most likely - the flag goes back and it says why. A toggle that reports
        /// success while the server holds the opposite view is worse than one that fails out loud,
        /// because the thing it is promising is that the opt-out survives a reinstall.
        /// </summary>
        private void SetRatingsEnabled(bool enabled)
        {
            // The local half happens now, unconditionally. Hiding the tab on your own machine is
            // your business, and making somebody wait on a moderator before their own plugin stops
            // showing them a feature would be absurd.
            config.RatingsEnabled = enabled;
            ratingOptOutFailed = false;

            var service = Ratings;
            if (service == null)
            {
                ratingOptOutNote = string.Empty;
                return;
            }

            ratingOptOutNote = enabled ? "Opting back in..." : "Filing the request...";

            service.RequestOptOut(!enabled, (error) =>
            {
                if (error.Length == 0)
                {
                    ratingOptOutFailed = false;
                    ratingOptOutNote = enabled
                        ? "Opted back in. Your ratings are visible again, and any request you had "
                          + "waiting has been withdrawn."
                        : "Request filed. Your ratings tab is hidden now; your score stops being "
                          + "visible to others once it is approved.";
                    return;
                }

                // Put it back. The server is the record, and the checkbox must not disagree with it.
                config.RatingsEnabled = !enabled;
                ratingOptOutFailed = true;
                ratingOptOutNote = error;
            });
        }

        /// <summary>
        /// The achievements feed, which is its own system.
        ///
        /// Drawn from both branches deliberately: it is not part of the rating system and does not
        /// disappear with it. Somebody who wants nothing to do with ratings may still want their
        /// Ultimate clear celebrated, and hiding this behind that would be deciding for them.
        /// </summary>
        private void DrawBroadcastSetting()
        {
            // Wording as the author wrote it. Left alone deliberately - it is the sentence people
            // will read when deciding whether to be in the feed, and it says what it does.
            DrawSetting("Broadcast my ultimate and savage achievements",
                () => config.BroadcastAchievements,
                v =>
                {
                    config.BroadcastAchievements = v;

                    // Tells the server too, which is what hides clears that are already up. Turning
                    // this off and leaving last week's posts on the feed would not be honest.
                    Ratings?.PushBroadcastSetting(v);
                },
                "This options allows other raiders to celebrate your clears, turn this off and "
                + "your clears won't be broadcasted anymore.", last: true);
        }

        private void DrawDataSettings()
        {
            DrawSectionLabel("Data");
            ImGui.Indent(SectionInset);

            DrawSliderSetting("Anonymous usage stats", AnalyticsModeInfo.Labels,
                () => AnalyticsModeInfo.IndexOf(config.AnalyticsMode),
                v => config.AnalyticsMode = AnalyticsModeInfo.FromIndex(v),
                i => AnalyticsModeInfo.Explain(AnalyticsModeInfo.FromIndex(i)));


            ImGui.Unindent(SectionInset);
        }

        private void DrawAppearanceSettings()
        {
            DrawSectionLabel("Appearance");
            ImGui.Indent(SectionInset);

            using (UiBodyFont.Push())
                ImGui.TextColored(Ink, "Accent colour");
            SameLineHelpDot("accent",
                "Colours the primary action, active tab and countdown. Role and vote colours "
                + "never change.");

            ImGui.Dummy(new Vector2(0, 10));
            DrawAccentSwatches();
            ImGui.Dummy(new Vector2(0, 12));

            ImGui.Unindent(SectionInset);
        }

        private void DrawAboutSettings()
        {
            DrawSectionLabel("About");
            ImGui.Indent(SectionInset);

            if (DrawNeutralButton("View changelog##OpenChangelog", new Vector2(180, ButtonHeight)))
                isChangelogVisible = true;

            ImGui.Dummy(new Vector2(0, 8));
            if (DrawNeutralButton("Show welcome again##OpenWelcome", new Vector2(180, ButtonHeight)))
                ShowWelcomeAgain();

            // Trailing room so the last control clears the bottom of the scroll region. Without it
            // the tab scrolls to exactly the end of the button and stops, leaving it sitting half in
            // the clip rect with nothing below to scroll to.
            ImGui.Dummy(new Vector2(0, 24));

            ImGui.Unindent(SectionInset);
        }

        // The rating server override has no settings UI on purpose.
        //
        // It is a development affordance - pointing a dev build at a local API - and it is the
        // one setting where a wrong value silently breaks every rating, report and progress
        // lookup at once. Nobody running the plugin normally has a reason to change it, and
        // showing the address invites exactly that. RatingApiBaseUrl is still read from the
        // config file for anyone who genuinely needs it.

        // ── Row shapes ────────────────────────────────────────────

        /// <summary>
        /// A toggle row: switch, name, and the line that explains it, closed by a hair rule.
        ///
        /// The rule is what separates rows - never whitespace on its own, or a column of settings
        /// turns into a paragraph of switches with no edges to scan by.
        /// </summary>
        private void DrawSetting(string label, Func<bool> get, Action<bool> set, string explanation,
            bool last = false)
        {
            bool value = get();
            if (DrawStyledCheckbox($"{label}##set{label}", ref value))
            {
                set(value);
                config.Save();
            }
            SameLineHelpDot($"set{label}", explanation);

            ImGui.Dummy(new Vector2(0, 8));
            if (!last)
                DrawRuleHair(padBelow: 8f);
        }

        /// <summary>
        /// A dropdown with its label above it and its explanation below, matching the toggle rows.
        ///
        /// The label sits above the control rather than beside it: ImGui puts a combo's label on the
        /// right, which would leave it dangling off the end of a full-width dropdown.
        /// </summary>
        private void DrawChoiceSetting(string label, string[] options, Func<int> get, Action<int> set,
            string explanation)
        {
            using (UiBodyFont.Push())
                ImGui.TextColored(Ink, label);
            SameLineHelpDot($"set{label}", explanation);
            ImGui.Dummy(new Vector2(0, 4));

            int value = Math.Clamp(get(), 0, options.Length - 1);
            PushFramedInput();
            ImGui.SetNextItemWidth(ImGui.GetContentRegionMax().X - 16);
            if (ImGui.Combo($"##set{label}", ref value, options, options.Length))
            {
                set(value);
                config.Save();
            }
            PopFramedInput();

            ImGui.Dummy(new Vector2(0, 12));
        }

        /// <summary>
        /// A setting as a handle you drag along a flat track that stops at fixed points, with the
        /// name of the current stop above it and its meaning printed underneath.
        ///
        /// Built by hand rather than from ImGui's slider because a settings choice is not a number:
        /// SliderInt would show "1/2", accept anything in between while dragging, and give the
        /// three states no names. Here the handle can only ever be at a stop - the drag snaps
        /// continuously, so there is no in-between state to read or to save.
        ///
        /// The value is written on every snap but the config is only saved when the handle is let
        /// go. Dragging across three stops otherwise writes the file once per crossing, and the
        /// in-memory value is what the rest of the plugin reads anyway.
        /// </summary>
        private void DrawSliderSetting(string label, string[] stops, Func<int> get, Action<int> set,
            Func<int, string> explain)
        {
            if (stops.Length < 2)
                return;

            using (UiBodyFont.Push())
                ImGui.TextColored(Ink, label);

            // Every stop in the one hover, not just the selected one: choosing here means
            // comparing the options, and a reader who has to drag the handle to find out what each
            // one sends has already changed the setting.
            SameLineHelpDot($"set{label}", string.Join("\n\n",
                stops.Select((stop, i) => $"{stop} - {explain(i)}")));

            ImGui.Dummy(new Vector2(0, 8));

            int value = Math.Clamp(get(), 0, stops.Length - 1);

            const float trackH = 6f;
            const float handleW = 12f;
            const float handleH = 18f;

            float width = ImGui.GetContentRegionMax().X - 16;
            float height = handleH;

            Vector2 origin = ImGui.GetCursorScreenPos();
            ImGui.InvisibleButton($"##slider{label}", new Vector2(width, height));

            bool active = ImGui.IsItemActive();
            bool hot = active || ImGui.IsItemHovered();

            // Inset by half a handle at both ends so the handle stays inside the control when it
            // sits on the first or last stop.
            float trackY = origin.Y + height * 0.5f;
            float left = origin.X + handleW * 0.5f;
            float right = origin.X + width - handleW * 0.5f;
            float step = (right - left) / (stops.Length - 1);

            if (active)
            {
                int nearest = (int)MathF.Round(
                    Math.Clamp((ImGui.GetIO().MousePos.X - left) / step, 0, stops.Length - 1));

                if (nearest != value)
                {
                    set(nearest);
                    value = nearest;
                }
            }

            if (ImGui.IsItemDeactivated())
                config.Save();

            var dl = ImGui.GetWindowDrawList();
            float handleX = left + step * value;

            dl.AddRectFilled(new Vector2(left, trackY - trackH * 0.5f),
                new Vector2(right, trackY + trackH * 0.5f),
                ImGui.ColorConvertFloat4ToU32(Field));
            dl.AddRectFilled(new Vector2(left, trackY - trackH * 0.5f),
                new Vector2(handleX, trackY + trackH * 0.5f),
                ImGui.ColorConvertFloat4ToU32(Accent));

            // A notch at every stop, so the track shows where the handle can land before anyone
            // drags it and finds out.
            for (int i = 0; i < stops.Length; i++)
            {
                float x = left + step * i;
                dl.AddRectFilled(new Vector2(x - 1f, trackY - trackH * 0.5f - 3f),
                    new Vector2(x + 1f, trackY + trackH * 0.5f + 3f),
                    ImGui.ColorConvertFloat4ToU32(i <= value ? Accent : RuleStrong));
            }

            dl.AddRectFilled(new Vector2(handleX - handleW * 0.5f, trackY - handleH * 0.5f),
                new Vector2(handleX + handleW * 0.5f, trackY + handleH * 0.5f),
                ImGui.ColorConvertFloat4ToU32(hot ? AccentHover : Accent));

            ImGui.Dummy(new Vector2(0, 6));

            // The stop's name in words as well as in position: the handle alone says how far along
            // the scale you are, not what you have chosen.
            using (UiBodyFont.Push())
                ImGui.TextColored(Ink, stops[value]);

            ImGui.Dummy(new Vector2(0, 12));
        }

        // ── Appearance ────────────────────────────────────────────

        /// <summary>
        /// The seven accents as square swatches, the chosen one ringed in Ink.
        ///
        /// A ring rather than a tick: the swatch is the colour, and a mark drawn in the colour's own
        /// contrast is the one thing guaranteed to be legible on all seven.
        /// </summary>
        private void DrawAccentSwatches()
        {
            const float swatch = 26f;
            const float gap = 8f;

            var dl = ImGui.GetWindowDrawList();
            string current = string.IsNullOrWhiteSpace(config.AccentColorHex)
                ? DefaultAccentHex
                : config.AccentColorHex.Trim();

            for (int i = 0; i < AccentChoices.Length; i++)
            {
                var (hex, name) = AccentChoices[i];
                if (i > 0)
                    ImGui.SameLine(0, gap);

                Vector2 p = ImGui.GetCursorScreenPos();
                ImGui.InvisibleButton($"##accent{hex}", new Vector2(swatch, swatch));

                bool chosen = string.Equals(hex, current, StringComparison.OrdinalIgnoreCase);
                bool hot = ImGui.IsItemHovered();

                if (ImGui.IsItemClicked())
                {
                    config.AccentColorHex = hex;
                    config.Save();
                }

                dl.AddRectFilled(p, new Vector2(p.X + swatch, p.Y + swatch),
                    ImGui.ColorConvertFloat4ToU32(ColorFromHex(hex)));

                if (chosen)
                    dl.AddRect(new Vector2(p.X - 3f, p.Y - 3f),
                        new Vector2(p.X + swatch + 3f, p.Y + swatch + 3f),
                        ImGui.ColorConvertFloat4ToU32(Ink), 0f, 0, 2f);
                else if (hot)
                    dl.AddRect(new Vector2(p.X - 3f, p.Y - 3f),
                        new Vector2(p.X + swatch + 3f, p.Y + swatch + 3f),
                        ImGui.ColorConvertFloat4ToU32(BorderControl), 0f, 0, 1f);

                if (hot)
                    PaddedTooltip(name);
            }
        }

        // The "Clear local data" button used to live here, and it was the worst hole in the
        // whole rating system.
        //
        // It forgot your local history AND called DELETE /me/ledger, which erased your cooldown
        // rows on the server. The cooldown is what stops you rating the same person twice, and the
        // repeat-vote discount is computed from the vote_count on those very rows - so clearing
        // them did not just let you vote again, it made every repeat count at FULL weight. Vote,
        // click, vote, click. No script, no Tor, no skill: a supported button in the settings that
        // handed anybody an unlimited supply of full-weight votes against one person.
        //
        // The endpoint is gone too. A button removed from one build is still an endpoint anybody
        // can call, and it took a claimed name - so it could be used on somebody else's ledger.

    }
}
#endif
