#if PFP_RATINGS
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;

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
        /// <summary>Below this there is no room for a list beside a page, and the categories become
        /// headings on one scrolling column instead.</summary>
        private const float SettingsTwoColumnWidth = 720f;

        /// <summary>
        /// A page of settings: what the list on the left calls it, and what the right draws.
        ///
        /// GROUPED BY WHAT SOMEBODY CAME HERE TO CHANGE, not by which file the code lives in. The
        /// old layout dealt seven sections into two columns by height, so the Party Finder buttons
        /// sat above the radar and beside the accent picker for no reason anybody could name, and
        /// three of the seven were four lines long - a page of half-empty boxes.
        /// </summary>
        private readonly record struct SettingsPage(string Label, FontAwesomeIcon Icon, Action Draw);

        private int settingsPage;

        private List<SettingsPage> SettingsPages() => new()
        {
            // Everything that changes what the plugin puts in front of you while you are using the
            // Party Finder: the two buttons it adds, and the radar that reads listings.
            new("Party Finder", FontAwesomeIcon.Users, () =>
            {
                DrawPartyFinderSettings();
                DrawPfRadarSettings();
            }),

            // The community half and the data behind it, together.
            //
            // They were two pages and should not have been: every question on this page is the same
            // question in a different form - what the plugin knows about other people, what it tells
            // them about you, and what leaves the machine at all. Splitting "ratings" from "data"
            // meant somebody turning the system off had to visit two places to find out what that
            // actually did.
            new("Ratings & privacy", FontAwesomeIcon.Shield, () =>
            {
                DrawRatingsSettings();
                DrawDataSettings();
            }),

            // How the plugin looks, including how names are written in it.
            //
            // Player names sat with ratings for a while on the reasoning that it is a setting about
            // other players. It is not - it is a setting about typography. Nothing about it changes
            // what is sent, stored or shown to anybody else; it changes how a name is drawn on this
            // screen, which is the same kind of choice as the accent colour beside it.
            new("Appearance", FontAwesomeIcon.Palette, () =>
            {
                DrawAppearanceSettings();
                DrawPlayerNameSettings();
            }),

            // Version, changelog, the ask.
            new("About", FontAwesomeIcon.InfoCircle, DrawAboutSettings),
        };

        /// <summary>
        /// The Settings tab: a list of pages on the left, the chosen page on the right.
        ///
        /// TWO SCROLLBARS FOR ONE PAGE WAS THE PROBLEM. The old layout put four sections in one
        /// scrolling column and three in another, side by side, so the two halves of a single page
        /// slid independently under the cursor and nothing lined up with anything for more than a
        /// moment. It is also not how a settings screen works on the system this design is modelled
        /// on: there, the left is a list of places and only the right scrolls.
        /// </summary>
        private void DrawSettingsTab()
        {
            var pages = SettingsPages();
            if (pages.Count == 0)
                return;

            settingsPage = Math.Clamp(settingsPage, 0, pages.Count - 1);

            ImGui.SetCursorPosX(Space.Gutter);
            ImGui.BeginChild("SettingsBody",
                new Vector2(ImGui.GetWindowWidth() - Space.Gutter * 2f, -1), false);
            try
            {
                // The page LIST needs the top margin - it is rows, not headings, so nothing gives
                // it one. The page body opposite gets its own from the first DrawListHeading in it.
                float avail = ImGui.GetContentRegionAvail().X;

                if (avail < SettingsTwoColumnWidth)
                {
                    // No room for a list beside a page, so every page is drawn in order down one
                    // column. A phone has no left-hand rail anywhere else in the plugin either.
                    foreach (var page in pages)
                        page.Draw();

                    // Erased entirely in an ordinary build - see PluginUI.AdminHooks.cs.
                    DrawPanelSettings();
                    return;
                }

                float listW = MathF.Min(232f, avail * 0.28f);

                ImGui.BeginChild("SettingsPageList", new Vector2(listW, -1), false,
                    ImGuiWindowFlags.NoScrollbar);
                try
                {
                    ImGui.Dummy(new Vector2(0, Space.Gutter));

                    for (int i = 0; i < pages.Count; i++)
                        DrawSettingsPageRow(pages[i], i);
                }
                finally
                {
                    ImGui.EndChild();
                }

                ImGui.SameLine(0, Space.Gutter);

                // The only thing on this tab that scrolls.
                ImGui.BeginChild("SettingsPageBody", new Vector2(0, -1), false);
                try
                {
                    // Room above the first heading. It sat against the top edge of the panel, which
                    // made the page look like it had been scrolled to rather than opened.
                    ImGui.Dummy(new Vector2(0, Space.Gutter));
                    pages[settingsPage].Draw();

                    // Erased entirely in an ordinary build - see PluginUI.AdminHooks.cs.
                    if (settingsPage == pages.Count - 1)
                        DrawPanelSettings();
                }
                finally
                {
                    ImGui.EndChild();
                }
            }
            finally
            {
                ImGui.EndChild();
            }
        }

        /// <summary>
        /// One row in the settings page list: an icon, a name, and a fill when it is the page on
        /// screen.
        ///
        /// The same shape as the sidebar's navigation rows, because it is the same idea one level
        /// down - a list of places, with the one you are in marked.
        /// </summary>
        private void DrawSettingsPageRow(SettingsPage page, int index)
        {
            const float rowH = 38f;
            bool active = settingsPage == index;

            Vector2 p = ImGui.GetCursorScreenPos();
            float width = ImGui.GetContentRegionAvail().X;
            var dl = ImGui.GetWindowDrawList();

            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0, 0, 0, 0));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0, 0, 0, 0));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0, 0, 0, 0));
            if (ImGui.Button($"##settingspage{index}", new Vector2(width, rowH)))
                settingsPage = index;
            ImGui.PopStyleColor(3);

            bool hovered = ImGui.IsItemHovered();

            if (active || hovered)
                dl.AddRectFilled(p, new Vector2(p.X + width, p.Y + rowH),
                    ImGui.ColorConvertFloat4ToU32(active ? Raised : Field), Radius.Small);

            Vector4 colour = active || hovered ? Ink : Dim;
            string glyph = page.Icon.ToIconString();

            using (UiIconRow.Push())
            {
                Vector2 gs = ImGui.CalcTextSize(glyph);
                dl.AddText(new Vector2(p.X + 12f, p.Y + (rowH - gs.Y) * 0.5f),
                    ImGui.ColorConvertFloat4ToU32(active ? Accent : colour), glyph);
            }

            using (UiBodyFont.Push())
            {
                Vector2 ts = ImGui.CalcTextSize(page.Label);
                dl.AddText(new Vector2(p.X + 36f, p.Y + (rowH - ts.Y) * 0.5f),
                    ImGui.ColorConvertFloat4ToU32(colour), Fit(page.Label, width - 44f));
            }

            ImGui.Dummy(new Vector2(0, 2));
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
        /// <summary>
        /// Zero, and kept only so the branches that indent and unindent inside a section do not
        /// have to be unpicked one by one.
        ///
        /// It used to step a section's rows six pixels in from a heading that had no box around it.
        /// The section is a card now and the card's own padding does that job, at the same distance
        /// as every other card in the plugin - a second inset inside it would put settings rows
        /// further in than anything else on any tab.
        /// </summary>

        private void DrawPartyFinderSettings()
        {
            BeginSettingsSection("Party Finder");

            DrawSetting("\"Apply a recruitment preset\" button", () => config.ShowPartyFinderButton,
                v => config.ShowPartyFinderButton = v,
                "Puts the button next to Recruit Members, so the plugin opens from where you "
                + "already are. Off leaves the Party Finder untouched; /pfa still opens it.");

            DrawSetting("\"Save as Preset\" button", () => config.ShowSaveListingButton,
                v => config.ShowSaveListingButton = v,
                "Puts a button under a listing you're viewing that keeps it as one of your "
                + "presets. Off leaves listings untouched.");

            DrawSetting("Hide locked duties from the presets", () => config.HideLockedDuties,
                v => config.HideLockedDuties = v,
                "Keeps content this character hasn't unlocked out of the duty picker, and shows "
                + "such a preset as \"(Locked duty)\" instead of naming the fight. Off shows "
                + "everything and marks the locked ones. Either way a locked preset can't be "
                + "applied - the game won't take the listing.");

            // Its setter goes through the confirmation rather than straight at the config - see
            // AskThenSetShowLockedDutyNames. The toggle still reads the config, so declining the
            // warning leaves the switch where it was without any undo step.
            DrawSetting("Show names of locked duties in party finder", () => config.ShowLockedDutyNames,
                AskThenSetShowLockedDutyNames,
                "Party Finder listings for content you haven't unlocked say \"Locked Duty\" instead "
                + "of naming the fight. On replaces that with the duty's real name, still marked "
                + "\"(Locked Duty)\". SPOILERS - the game hides these names on purpose.");

            DrawSetting("Leader's score on a listing", () => config.ShowListingLeaderRating,
                v => config.ShowListingLeaderRating = v,
                "Shows the listing leader's community score beside their name while you're "
                + "viewing it, so you see it before joining rather than after. Needs ratings on.");

            // No hairline under it when the two numbers follow: they are the rest of this setting,
            // not the next one.
            bool numbersFollow = config.AutoRefresherEnabled && !IsRecruitmentRefresherActive();

            DrawSetting("Auto-refresh listing", () => config.AutoRefresherEnabled,
                v => config.AutoRefresherEnabled = v,
                IsRecruitmentRefresherActive()
                    ? "Handled by RecruitmentRefresher while that plugin is running."
                    : "Re-posts your listing before it expires so it stays near the top.",
                joinNext: numbersFollow);

            // THE TWO NUMBERS THAT USED TO LIVE ONLY IN THE FOOTER.
            //
            // The footer's copy is hidden unless a listing is actually up - see
            // FooterOffersSettings - which is right for a strip that also carries a countdown, and
            // would otherwise mean these two were unreachable for most of an evening. A number you
            // set once and live with belongs on a settings page regardless; the footer's copy is
            // the convenience, not the home.
            if (config.AutoRefresherEnabled && !IsRecruitmentRefresherActive())
            {
                int interval = Math.Clamp(config.AutoRefresherIntervalMinutes,
                    PfAutomation.MinRefreshMinutes, PfAutomation.MaxRefreshMinutes);
                int maxHours = Math.Clamp(config.AutoRefresherMaxHours, 0,
                    PfAutomation.MaxRefreshDurationHours);

                // ONE LINE UNDER THE SWITCH THEY BELONG TO. They were a row each, and two rows for
                // "how often" and "until when" made the pair look like two unrelated settings that
                // happened to follow the one they qualify. They read as a sentence: refresh every
                // this, stop after that.
                if (DrawInlinePairRow(
                        "Refresh every", ref interval,
                        PfAutomation.MinRefreshMinutes, PfAutomation.MaxRefreshMinutes, "min", null,
                        "How often to re-post your listing while it is up. A listing expires after "
                        + $"60 minutes on its own ({PfAutomation.MinRefreshMinutes}-"
                        + $"{PfAutomation.MaxRefreshMinutes} minutes).",
                        "Stop after", ref maxHours,
                        0, PfAutomation.MaxRefreshDurationHours, "h", "Never",
                        "Stops auto-refreshing after this long, so a listing does not stay up all "
                        + "night unattended. Your listing is not cancelled - it just expires "
                        + "normally. Zero means never stop."))
                {
                    config.AutoRefresherIntervalMinutes = interval;
                    config.AutoRefresherMaxHours = maxHours;
                    config.Save();
                }
            }

            DrawSetting("Auto-adjust locked slots", () => config.AutoAdjustLockedJobsEnabled,
                v => config.AutoAdjustLockedJobsEnabled = v,
                "Keeps one-job slots in step with who has already joined.", last: true);

            EndSettingsSection();
        }

        private void DrawPlayerNameSettings()
        {
            BeginSettingsSection("Player names");

            DrawChoiceSetting("Show names as", PlayerNameFormat.StyleLabels,
                () => (int)config.PlayerNameStyle,
                v => config.PlayerNameStyle = (PlayerNameStyle)v,
                "Applies everywhere a name appears - the recruitment card, your party, ratings, "
                + "recent players and profiles. Lookups and links still use the full name.");

            EndSettingsSection();
        }

        private void DrawRatingsSettings()
        {
            BeginSettingsSection("Ratings");

            // THIS TOGGLE IS AN OPT-OUT, not a local preference, and the wording says so because
            // the consequence outlives the plugin: once approved the server holds the flag, so
            // uninstalling does not quietly put somebody back into a system they left.
            //
            // It only appears while logged in. The request names the character it is for, and
            // there is no character to name from the title screen - a toggle that files nothing is
            // worse than one that is not there.
            // Kept current while the tab is open, so a decision on somebody's request shows up
            // without them relogging. Throttled inside; safe from a draw call.
            Ratings?.EnsureOptOutSynced();

            bool loggedIn = LocalIdentity?.Invoke() is { IsValid: true };

            if (!loggedIn)
            {
                ImGui.TextColored(Dim, "Log in to a character to change this.");
                ImGui.Dummy(new Vector2(0, 8));
                DrawRuleHair(padAbove: 0f, padBelow: 10f);
                DrawBroadcastSetting();
                    return;
            }

            // LOCKED OFF BELOW FULL, and said out loud rather than left as a switch that springs
            // back. Anonymous usage stats below Full opts you out (see Configuration.CommunityEnabled),
            // so a live toggle here would be offering something the other setting has already
            // decided - press it and watch it refuse, with the reason two sections away.
            if (config.AnalyticsMode != AnalyticsMode.Full)
            {
                using (UiBodyFont.Push())
                    ImGui.TextColored(Dim, "Ratings system: off");
                SameLineHelpDot("ratingslocked",
                    "Taking part needs \"Anonymous usage stats\" set to Full - see the Data "
                    + "section. Below that, this install sends nothing and you are opted out of "
                    + "ratings and clears.");

                ImGui.Dummy(new Vector2(0, 8));
                DrawRuleHair(padBelow: 8f);
                DrawBroadcastSetting();

                    return;
            }

            DrawSetting("Enable ratings system", () => config.RatingsEnabled,
                AskThenSetRatingsEnabled,
                "Disabling this opts you out of the rating system and the Clears feed: "
                + "players cannot view your ratings, or rate you, and nothing about your duties is "
                + "sent. It stays opted out even after uninstalling the plugin, until you enable "
                + "this option again.",
                last: !config.RatingsEnabled);

            if (ratingOptOutNote.Length > 0)
            {
                    using (UiHelpFont.Push())
                    ImGui.TextColored(ratingOptOutFailed ? Negative : Faint, ratingOptOutNote);
                    ImGui.Dummy(new Vector2(0, 6));
            }

            if (!config.RatingsEnabled)
            {
                // Broadcasting is a different system and does not go with it - somebody who wants
                // nothing to do with ratings may still want their Ultimate clear celebrated, and
                // burying that setting behind this one would decide for them.
                DrawRuleHair(padAbove: 10f, padBelow: 10f);
                DrawBroadcastSetting();

                    return;
            }

            DrawSetting("Ask after a duty", () => config.PostDutyPromptEnabled,
                v => config.PostDutyPromptEnabled = v,
                "When a duty ends, a small window offers to rate the group once.");

            DrawSetting("Show ratings on your party", () => config.PartyRatingsEnabled,
                v => config.PartyRatingsEnabled = v,
                "Shows each party member's rating and prog point beside their name.");

            DrawBroadcastSetting();

            EndSettingsSection();
        }

        // ── The opt-out ───────────────────────────────────────────

        private string ratingOptOutNote = string.Empty;
        private bool ratingOptOutFailed;

        // ── Asking first ──────────────────────────────────────────
        //
        // THE THREE SETTINGS THAT MOVE DATA ASK BEFORE THEY MOVE IT: ratings, usage stats and
        // broadcasting. Not because a checkbox is hard to undo, but because the thing being decided
        // is not the checkbox - it is what leaves this machine, and nobody should learn that from a
        // help dot they did not hover.
        //
        // Short on purpose. Each one says what is sent, what is lost, and that it is anonymous and
        // hashed. The full text lives in the help dots and the About tab; a dialog long enough to
        // need scrolling is a dialog people dismiss to make it go away, which is the opposite of
        // consent.

        /// <summary>The sentence every one of these dialogs ends on. One copy, so the promise cannot
        /// drift into three slightly different promises.</summary>
        private const string AnonymityLine =
            "Everything sent is anonymous: names are hashed before they are stored, and no chat, "
            + "combat or hardware data is ever included.";

        /// <summary>
        /// Confirms before the ratings toggle actually moves.
        ///
        /// Both directions ask. Turning it off is the consequential one - it files a server-side
        /// opt-out that outlives the install - but turning it on starts sending duty results, and a
        /// feature that only checks before taking something away is a feature that treats
        /// switching data collection ON as the safe default. It is not.
        /// </summary>
        private void AskThenSetRatingsEnabled(bool enabled)
        {
            // The dialog is a window, not a modal, so the settings behind it stay clickable. The
            // checkbox reads its state from the config every frame and the config has not moved
            // yet, so refusing here simply leaves it where it was - no second question, no stack.
            if (IsConfirming)
                return;

            if (enabled)
            {
                AskConfirm(
                    "Turn on the ratings system?",
                    "Your duty results start being sent so you can rate other players and be rated.",
                    "Turn it on",
                    () => SetRatingsEnabled(true),
                    detail: "Sent: who you finished a duty with, a 1-5 score you choose, and clears "
                        + "worth posting to the feed. " + AnonymityLine,
                    danger: false);
                return;
            }

            AskConfirm(
                "Turn off the ratings system?",
                "You will be opted out of ratings and the Clears feed.",
                "Turn it off",
                () => SetRatingsEnabled(false),
                detail: "You lose: your ratings tab, ratings on your party panel, and the ability to "
                    + "rate anyone. Nothing about your duties is sent, and your clear posts "
                    + "are hidden. This survives uninstalling, until you turn it back on.");
        }

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
        /// Whether broadcasting is even on offer.
        ///
        /// Being opted out means being out of all of it. This used to be the other way round - the
        /// toggle was drawn from every branch on the grounds that somebody who wants nothing to do
        /// with ratings might still want their Ultimate celebrated. That reasoning does not survive
        /// contact with what opting out actually does: the server hides an opted-out character's
        /// posts, so the switch was offering to broadcast into a feed that would not show them.
        /// A control that cannot do what it says is worse than an absent one.
        /// </summary>
        private bool BroadcastAvailable
            => config.RatingsEnabled && config.AnalyticsMode == AnalyticsMode.Full;

        /// <summary>
        /// The achievements feed, which is its own system - but not a way around the opt-out.
        ///
        /// Gone rather than greyed out while opted out, with a line saying why. A disabled checkbox
        /// invites people to work out what unlocks it; a sentence tells them, and points at the one
        /// setting that does.
        /// </summary>
        private void DrawBroadcastSetting()
        {
            if (!BroadcastAvailable)
            {
                using (UiHelpFont.Push())
                    ImGui.TextColored(Faint,
                        "Broadcasting is unavailable while you are opted out. Your existing posts "
                        + "are hidden, not deleted - opting back in restores them.");

                ImGui.Dummy(new Vector2(0, 8));
                return;
            }

            // Wording as the author wrote it. Left alone deliberately - it is the sentence people
            // will read when deciding whether to be in the feed, and it says what it does.
            DrawSetting("Broadcast my ultimate and savage clears",
                () => config.BroadcastAchievements,
                AskThenSetBroadcast,
                "This options allows other raiders to celebrate your clears, turn this off and "
                + "your clears won't be broadcasted anymore.", last: true);
        }

        /// <summary>
        /// Confirms before the feed gains or loses this character's clears.
        ///
        /// The off direction is the one worth spelling out: it does not only stop future posts, it
        /// takes down the ones already up. People expect a broadcast switch to be about what
        /// happens next, so the dialog says plainly that the back catalogue goes too - and that it
        /// comes back, because "hidden" and "deleted" are very different promises.
        /// </summary>
        /// <summary>
        /// Confirms before this client starts or stops publishing the party it is sitting in.
        ///
        /// Asked in both directions like the others, and the ON dialog is specific about the thing
        /// people will not guess: this publishes the PARTY, not only you. Anybody who would rather
        /// it did not has to be able to find that out here, before they turn it on, rather than
        /// from somebody else's screen afterwards.
        /// </summary>
        private void AskThenSetCrowdsource(bool enabled)
        {
            if (IsConfirming)
                return;

            if (enabled)
            {
                AskConfirm(
                    "Share who is in your party finder listing?",
                    "While your party is listed, other people running this plugin will see who is "
                    + "in it - the whole party, not only you.",
                    "Share it",
                    () =>
                    {
                        config.PfCrowdsourceEnabled = true;
                        config.Save();
                    },
                    detail: "Sent: the name, world and job of everybody in your party, filed "
                        + "against the listing's leader. It is only ever sent while your party is "
                        + "publicly listed, never for a private one; it is withdrawn when the "
                        + "listing ends, and the server forgets it within the hour regardless.",
                    danger: false);
                return;
            }

            AskConfirm(
                "Stop sharing your listing's party?",
                "Your party will no longer be shown to other people looking at its listing.",
                "Stop sharing",
                () =>
                {
                    config.PfCrowdsourceEnabled = false;
                    config.Save();

                    // Down now rather than at the next expiry: somebody who just turned this off
                    // should not still be on somebody else's screen for the next hour.
                    Crowd?.Withdraw();
                },
                detail: "Anything this client has published is taken down straight away - though "
                    + "another member of the same party who is also sharing will still be "
                    + "describing it. You can still see listings other people are sharing.");
        }

        private void AskThenSetBroadcast(bool enabled)
        {
            if (IsConfirming)
                return;

            if (enabled)
            {
                AskConfirm(
                    "Broadcast your clears?",
                    "Your ultimate and savage clears will appear in the Clears feed.",
                    "Broadcast them",
                    () => SetBroadcast(true),
                    detail: "Others see the fight, your job, and your character name and world on "
                        + "the card. Only clears the server can verify are ever posted. " + AnonymityLine,
                    danger: false);
                return;
            }

            AskConfirm(
                "Stop broadcasting your clears?",
                "Your clears will be taken out of the Clears feed.",
                "Stop broadcasting",
                () => SetBroadcast(false),
                detail: "Posts already on the feed come down too. Nothing is deleted - turning this "
                    + "back on puts them where they were.");
        }

        /// <summary>
        /// Confirms before locked duty names appear, and does not confirm when they stop.
        ///
        /// ASYMMETRIC ON PURPOSE, and it is the one setting in here where that is the whole point.
        /// The warning exists because turning this on cannot be undone in the head of whoever read
        /// the spoiler - so the question is asked while there is still something to protect, and
        /// asked in front of the answer rather than in a tooltip beside it. Turning it back off
        /// takes something away that the player asked for and does not need guarding.
        ///
        /// No OnCancel, because nothing has moved yet: DrawSetting reads the config back every
        /// frame, so a switch whose setter declined to write simply stays where it was.
        /// </summary>
        private void AskThenSetShowLockedDutyNames(bool enabled)
        {
            if (IsConfirming)
                return;

            if (!enabled)
            {
                config.ShowLockedDutyNames = false;
                config.Save();
                return;
            }

            // SHORT ON PURPOSE. The first draft explained the mechanism, the marking, the way
            // back and the reason to decline - five sentences, which in a centred alert is a wall
            // nobody reads before pressing something. What a warning has to land is the risk and
            // the fact that it does not come back; the detail belongs on the setting's own help
            // mark, where somebody deciding at leisure will find it.
            AskConfirm(
                "Spoilers",
                "Party Finder will name duties you haven't unlocked.",
                "Show names",
                () =>
                {
                    config.ShowLockedDutyNames = true;
                    config.Save();
                },
                detail: "You can turn this back off, but you can't unread a name.",
                danger: true);
        }

        private void SetBroadcast(bool enabled)
        {
            config.BroadcastAchievements = enabled;
            config.Save();

            // Tells the server too, which is what hides clears that are already up. Turning
            // this off and leaving last week's posts on the feed would not be honest.
            Ratings?.PushBroadcastSetting(enabled);
        }

        private string analyticsOptOutNote = string.Empty;
        private bool analyticsOptOutFailed;

        /// <summary>The value a slider held when its handle was picked up. One field for all of
        /// them: ImGui has one active item, so two sliders cannot be mid-drag at once.</summary>
        private object? sliderGrabbedValue;

        /// <summary>
        /// The opt-out that rides along with dropping the stats slider below Full.
        ///
        /// ONE SETTING, ONE CONSEQUENCE. Anything below Full means this install takes part in
        /// nothing, and taking part in nothing has a server half - the enrolment - or it is a
        /// promise that ends at the config file. Somebody who turns the stats off and uninstalls
        /// would otherwise still be rateable by everybody else, which is precisely the thing the
        /// rating toggle exists to prevent.
        ///
        /// It is filed the same way and through the same route as the toggle's own opt-out, so it
        /// lands in the queue a moderator already reads, and the note underneath says what
        /// happened.
        ///
        /// ON RELEASE, NOT ON EVERY STOP THE HANDLE CROSSES. The slider snaps continuously while
        /// dragged, so a drag from Full to Off passes through Basic and a drag that ends up back
        /// where it started passes through everything - and each crossing would otherwise file a
        /// moderator request for a setting the person never actually chose. What is compared is
        /// where the handle was picked up against where it was put down.
        ///
        /// ONLY EVER IN ONE DIRECTION. Dragging back up unlocks the ratings toggle and leaves it
        /// off: opting somebody back INTO a system they left, on the strength of a settings change
        /// they made about something else, would be the plugin choosing for them.
        /// </summary>
        /// <summary>
        /// Asks about the slider's new position before letting it stand.
        ///
        /// THE ONE CONTROL WHERE "NO" HAS WORK TO DO. A checkbox is asked before it moves; a slider
        /// has already moved by the time the handle is released, so declining here has to put it
        /// back - which is what <see cref="ConfirmRequest.OnCancel"/> exists for. Without that, the
        /// track would sit showing a setting the person had just refused.
        /// </summary>
        private void CommitAnalyticsMode(AnalyticsMode was)
        {
            var mode = config.AnalyticsMode;
            if (mode == was)
                return;

            // Unlike the checkboxes, the slider has already moved - so refusing a second question
            // means putting it back, not just declining to ask one.
            if (IsConfirming)
            {
                config.AnalyticsMode = was;
                config.Save();
                return;
            }

            // Whether saying yes here also files a server-side opt-out. Worth its own sentence in
            // the dialog: somebody dragging a stats slider is not expecting to leave the rating
            // system, and finding out afterwards is exactly the surprise this dialog is for.
            bool alsoOptsOut = was == AnalyticsMode.Full && mode != AnalyticsMode.Full
                && config.RatingsEnabled;

            (string title, string question, string detail) = mode switch
            {
                AnalyticsMode.Full => (
                    "Send full usage stats?",
                    "This install starts sending counts of which plugin features get used.",
                    "Added to the random install id and version already sent. It also unlocks the "
                    + "ratings system, which stays off until you turn it on yourself."),

                AnalyticsMode.Basic => (
                    "Send basic usage stats only?",
                    "Only a random install id and the plugin version will be sent.",
                    "Feature-use counts stop."),

                _ => (
                    "Turn off usage stats?",
                    "Nothing will be sent from this install at all.",
                    "This install stops being counted."),
            };

            if (alsoOptsOut)
            {
                detail += " You will also be opted out of ratings and clears, and that "
                    + "opt-out is filed with the server - dragging this back up later does not "
                    + "turn ratings back on by itself.";
            }

            detail += " The install id is random and is never your character name; no chat, combat "
                + "or hardware data is included.";

            AskConfirm(
                title, question,
                mode == AnalyticsMode.Full ? "Send them" : "Apply",
                () => ApplyAnalyticsMode(was),
                detail: detail,
                danger: mode != AnalyticsMode.Full,
                onCancel: () =>
                {
                    config.AnalyticsMode = was;
                    config.Save();
                });
        }

        private void ApplyAnalyticsMode(AnalyticsMode was)
        {
            var mode = config.AnalyticsMode;
            if (mode == was)
                return;

            if (mode == AnalyticsMode.Full)
            {
                analyticsOptOutFailed = false;
                analyticsOptOutNote = config.RatingsEnabled
                    ? string.Empty
                    : "Still opted out. Turn \"Enable ratings system\" back on in the Ratings "
                      + "section to take part again.";
                return;
            }

            if (was != AnalyticsMode.Full || !config.RatingsEnabled)
                return;

            config.RatingsEnabled = false;
            config.Save();

            analyticsOptOutFailed = false;
            analyticsOptOutNote = "Filing the opt-out request...";

            var service = Ratings;
            if (service == null)
            {
                analyticsOptOutNote = string.Empty;
                return;
            }

            service.RequestOptOut(true, (error) =>
            {
                if (error.Length == 0)
                {
                    analyticsOptOutFailed = false;
                    analyticsOptOutNote = "Opted out. Ratings and clears are hidden now; your "
                        + "score stops being visible to others once the request is approved.";
                    return;
                }

                // The server is the record and the plugin must not claim otherwise - but the local
                // half stays off regardless. Somebody who moved this slider has said what they
                // want, and continuing to send their duties while a request fails to file would be
                // the one outcome nobody asked for.
                analyticsOptOutFailed = true;
                analyticsOptOutNote = error;
            });
        }

        /// <summary>
        /// The listing panel's settings.
        ///
        /// It reports the conflict rather than only obeying it. A toggle that is on while the
        /// feature visibly does nothing is a bug report waiting to happen, and "PFRadar is doing
        /// this instead" is the entire explanation.
        /// </summary>
        private void DrawPfRadarSettings()
        {
            BeginSettingsSection("PF Radar settings");

            DrawSetting("Show listing details", () => config.ListingDetailsEnabled,
                v => config.ListingDetailsEnabled = v,
                "Shows a panel beside a party finder listing with the jobs already in the party, "
                + "the leader and the item level. All of it comes from what the game has already "
                + "loaded to draw that window - nothing is fetched and nobody is asked.",
                last: !config.ListingDetailsEnabled);

#if PFP_RATINGS
            if (config.ListingDetailsEnabled)
            {
                DrawSetting("Share who is in my party finder listing",
                    () => config.PfCrowdsourceEnabled,
                    AskThenSetCrowdsource,
                    "While your party is listed, publishes the party - each member's name, world "
                    + "and job - so other people running this plugin can see who is already in "
                    + "that listing. One person sharing is enough to describe the whole party, "
                    + "which is what makes the panel opposite worth reading. Only a listed party "
                    + "is ever sent; it comes down when the listing ends, and the server forgets "
                    + "it within the hour either way.",
                    last: true);
            }
#endif

            var xray = Listings;
            if (xray?.SuppressedByPfRadar == true)
            {
                using (UiHelpFont.Push())
                    ImGui.TextColored(AccentYellow,
                        "PFRadar is running, so this is turned off. Both read the same part of the "
                        + "game and only one of them should.");

                ImGui.Dummy(new Vector2(0, 6));
            }

            EndSettingsSection();
        }

        private void DrawDataSettings()
        {
            BeginSettingsSection("Data");

            DrawSliderSetting("Anonymous usage stats", AnalyticsModeInfo.Labels,
                () => AnalyticsModeInfo.IndexOf(config.AnalyticsMode),
                v => config.AnalyticsMode = AnalyticsModeInfo.FromIndex(v),
                i => AnalyticsModeInfo.Explain(AnalyticsModeInfo.FromIndex(i)),
                grabbed: () => config.AnalyticsMode,
                released: CommitAnalyticsMode);

            if (analyticsOptOutNote.Length > 0)
            {
                using (UiHelpFont.Push())
                    ImGui.TextColored(analyticsOptOutFailed ? Negative : Faint, analyticsOptOutNote);
                ImGui.Dummy(new Vector2(0, 6));
            }

            EndSettingsSection();
        }

        private void DrawAppearanceSettings()
        {
            BeginSettingsSection("Appearance");

            // THE DEVICE SWITCH GOES FIRST, above the accent, because it is the setting on this
            // surface that changes the most. Everything else on the Appearance section recolours
            // the window; this one reshapes it.
            float width = SettingsContentWidth();

            DrawSettingLabelRow("Window",
                "Portrait is one column with a tab bar along the bottom, and fits beside the game. "
                + "Landscape is wider, with a sidebar and two-column pages, and wants a big "
                + "monitor. Neither can be resized - each is drawn for the size it is.", width);

            int device = (int)config.Device;
            string[] deviceLabels =
            {
                $"Portrait · {DeviceMetrics.SizeLabel(DeviceLayout.Portrait)}",
                $"Landscape · {DeviceMetrics.SizeLabel(DeviceLayout.Landscape)}",
            };

            // The track is measured back to the card's padding like every other control here.
            // Content-region-avail alone runs to the window and put its right edge outside the card.
            if (DrawSegmentedControl("device", deviceLabels, ref device, width))
            {
                config.Device = (DeviceLayout)device;
                config.Save();
            }

            ImGui.Dummy(new Vector2(0, Space.Gutter));

            DrawSettingLabelRow("Accent colour",
                "Colours the primary action, active tab and countdown. Role and vote colours "
                + "never change.", width);

            ImGui.Dummy(new Vector2(0, 4f));
            DrawAccentSwatches();
            ImGui.Dummy(new Vector2(0, Space.Gutter));
            ImGui.Dummy(new Vector2(0, 12));

            EndSettingsSection();
        }

        private void DrawAboutSettings()
        {
            BeginSettingsSection("About");

            if (DrawNeutralButton("View changelog##OpenChangelog", new Vector2(180, ButtonHeight)))
                isChangelogVisible = true;

            ImGui.Dummy(new Vector2(0, 8));
            if (DrawNeutralButton("Show welcome again##OpenWelcome", new Vector2(180, ButtonHeight)))
                ShowWelcomeAgain();

            // Trailing room so the last control clears the bottom of the scroll region. Without it
            // the tab scrolls to exactly the end of the button and stops, leaving it sitting half in
            // the clip rect with nothing below to scroll to.
            ImGui.Dummy(new Vector2(0, 24));

            EndSettingsSection();
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
        /// <param name="joinNext">Suppresses the hairline under this row because what follows
        /// belongs to it. A switch and the numbers that qualify it are one setting written on two
        /// lines, and a line between them says they are two.</param>
        private void DrawSetting(string label, Func<bool> get, Action<bool> set, string explanation,
            bool last = false, bool joinNext = false)
        {
            bool value = get();
            var dl = ImGui.GetWindowDrawList();
            float width = SettingsContentWidth();

            bool rowClicked = BeginListRow($"set{label}", width, out Vector2 min, SettingRowHeight);
            float centreY = min.Y + SettingRowHeight * 0.5f;

            // Switch, gap, sentence, question mark - the mockup's row, in that order. The switch is
            // the state you scan the column for, so it leads and every one of them sits on one x.
            ImGui.SetCursorScreenPos(new Vector2(min.X,
                                                 centreY - ImGui.GetTextLineHeight() * 0.5f));
            bool changed = DrawSquareToggle($"set{label}", ref value);

            float textX = min.X + ToggleTrackWidth + SettingRowGap;
            float textRoom = width - (textX - min.X) - HelpMarkColumn;

            float labelWidth;
            using (UiBodyFont.Push())
            {
                float lineH = ImGui.GetTextLineHeight();
                string shown = Fit(label, textRoom);
                labelWidth = ImGui.CalcTextSize(shown).X;
                dl.AddText(new Vector2(textX, centreY - lineH * 0.5f),
                    ImGui.ColorConvertFloat4ToU32(Ink), shown);
            }

            // BESIDE THE WORDS, not at the end of the row. It marks that sentence, and a column of
            // question marks a hand's breadth from the sentences they belong to reads as a column
            // of its own - which is what it looked like.
            DrawRowHelpMark($"set{label}", explanation,
                new Vector2(textX + labelWidth + HelpMarkGap, centreY));

            // The whole row is the target. The switch and the mark are submitted first and take the
            // clicks that land on them; everything between falls through to here.
            if (rowClicked)
            {
                value = !value;
                changed = true;
            }

            if (changed)
            {
                set(value);
                config.Save();
            }

            if (!last && !joinNext)
                DrawRowSeparator(dl, min, SettingRowHeight, 0f, min.X + width);

            ImGui.SetCursorScreenPos(new Vector2(min.X, min.Y + SettingRowHeight));
        }

        /// <summary>Room kept at the end of a row for its question mark, and the gap before it.
        /// </summary>
        private const float HelpMarkColumn = 18f + 10f;

        /// <summary>A settings row's height and the gap between its control and its words - 8px of
        /// padding either side of a 20px switch, and 11px across, from the mockup.</summary>
        private const float SettingRowHeight = 38f;
        private const float SettingRowGap = 11f;

        /// <summary>Width of the switch, so a row can reserve its trailing edge without measuring
        /// it.</summary>
        private const float ToggleTrackWidth = 36f;

        /// <summary>
        /// A number with a minus and a plus either side of it, laid out like the toggle rows above.
        ///
        /// Stepped rather than typed. The footer's copy of these is a chip you double-click into a
        /// text field, which is a fine trick on a strip with no room for anything else and a poor
        /// one on a settings page, where a control you have to discover is a control most people
        /// never find.
        /// </summary>
        /// <param name="zeroLabel">What zero is called, when it means something other than nought -
        /// "Stop after 0 h" is "Never".</param>
        /// <summary>
        /// Two numbers on one line, each a word and a chip, flowing from the left.
        ///
        /// Left-aligned rather than pushed to the right edge. A value pinned to the far side of a
        /// wide card is a long way from the word that names it, and with two of them the row read
        /// as four separate things instead of one sentence.
        /// </summary>
        private bool DrawInlinePairRow(
            string labelA, ref int valueA, int minA, int maxA, string suffixA, string? zeroA, string helpA,
            string labelB, ref int valueB, int minB, int maxB, string suffixB, string? zeroB, string helpB)
        {
            var dl = ImGui.GetWindowDrawList();
            float width = SettingsContentWidth();

            Vector2 rowMin = ImGui.GetCursorScreenPos();
            float centreY = rowMin.Y + SettingRowHeight * 0.5f;
            const float chipW = 74f;
            const float chipH = 26f;

            float x = rowMin.X;
            bool changed = false;

            for (int i = 0; i < 2; i++)
            {
                string label = i == 0 ? labelA : labelB;
                string help = i == 0 ? helpA : helpB;

                using (UiBodyFont.Push())
                {
                    Vector2 ts = ImGui.CalcTextSize(label);
                    dl.AddText(new Vector2(x, centreY - ts.Y * 0.5f),
                        ImGui.ColorConvertFloat4ToU32(Dim), label);
                    x += ts.X + Space.Tight;
                }

                var chipAt = new Vector2(x, centreY - chipH * 0.5f);

                if (i == 0)
                    changed |= DrawEditableNumberChip($"pair{labelA}", ref valueA, suffixA, zeroA,
                        minA, maxA, chipAt, new Vector2(chipW, chipH), "Double-click to type a number.");
                else
                    changed |= DrawEditableNumberChip($"pair{labelB}", ref valueB, suffixB, zeroB,
                        minB, maxB, chipAt, new Vector2(chipW, chipH), "Double-click to type a number.");

                x += chipW + Space.Tight;
                DrawRowHelpMark($"pair{label}", help, new Vector2(x, centreY));
                x += 18f + Space.Gutter;
            }

            DrawRowSeparator(dl, rowMin, SettingRowHeight, 0f, rowMin.X + width);
            ImGui.SetCursorScreenPos(new Vector2(rowMin.X, rowMin.Y + SettingRowHeight));

            return changed;
        }

        /// <summary>
        /// How wide a row inside a settings card is.
        ///
        /// THE RIGHT EDGE IS THE CARD'S, and the width is whatever is left between the cursor and
        /// it. Taking the card's width and backing off two paddings assumes the row starts exactly
        /// one padding in, which it did not: the sections called ImGui.Indent(SectionInset) with
        /// SectionInset at zero, and ImGui reads a zero there as "use IndentSpacing" - twenty-one
        /// pixels of it. So every row began 21px right of where the arithmetic thought and ended
        /// 21px past the card, which is the line hanging out of the box.
        ///
        /// Measuring back from the card cannot drift like that, whatever any caller indents by.
        /// </summary>
        private float SettingsContentWidth()
            => settingsCardMin.X + settingsCardWidth - CardPadding - ImGui.GetCursorScreenPos().X;

        /// <summary>
        /// A dropdown with its label above it and its explanation below, matching the toggle rows.
        ///
        /// The label sits above the control rather than beside it: ImGui puts a combo's label on the
        /// right, which would leave it dangling off the end of a full-width dropdown.
        /// </summary>
        private void DrawChoiceSetting(string label, string[] options, Func<int> get, Action<int> set,
            string explanation)
        {
            int value = Math.Clamp(get(), 0, options.Length - 1);
            float width = SettingsContentWidth();

            DrawSettingLabelRow(label, explanation, width);

            if (DrawChoiceRows($"set{label}", options, ref value, width))
            {
                set(value);
                config.Save();
            }
        }

        /// <summary>
        /// A column of mutually exclusive choices, one per row, on the card they already sit on.
        ///
        /// NO TRACK AROUND THEM AND NO SURFACE UNDER THEM. They were drawn as a bordered box
        /// containing four filled boxes, which is two more edges than the choice needs and reads as
        /// a control bolted onto the card rather than as part of it. A settings card is already a
        /// surface; these are rows on it, like the switches above them, separated the same way.
        ///
        /// The mark is a ring that fills with the accent when chosen - the same colour the switches
        /// use for on, so "this one" means the same thing everywhere on the page.
        /// </summary>
        private bool DrawChoiceRows(string id, string[] options, ref int value, float width)
        {
            const float rowH = 34f;
            const float mark = 16f;

            var dl = ImGui.GetWindowDrawList();
            bool changed = false;

            for (int i = 0; i < options.Length; i++)
            {
                Vector2 rowMin = ImGui.GetCursorScreenPos();

                ImGui.SetCursorScreenPos(rowMin);
                ImGui.InvisibleButton($"##{id}row{i}", new Vector2(width, rowH));
                bool hot = ImGui.IsItemHovered();

                if (ImGui.IsItemClicked() && value != i)
                {
                    value = i;
                    changed = true;
                }

                bool active = value == i;
                var centre = new Vector2(rowMin.X + mark * 0.5f, rowMin.Y + rowH * 0.5f);

                // A ring, filled when chosen. Not a box, and nothing behind the row: the card is
                // the surface and the ring is the only thing that has to change.
                dl.AddCircle(centre, mark * 0.5f,
                    ImGui.ColorConvertFloat4ToU32(active ? Accent : BorderControl), 24,
                    active ? 1.8f : 1.4f);

                if (active)
                    dl.AddCircleFilled(centre, mark * 0.5f - 4f,
                        ImGui.ColorConvertFloat4ToU32(Accent), 24);

                using (UiBodyFont.Push())
                {
                    float textX = rowMin.X + mark + 12f;
                    Vector2 ts = ImGui.CalcTextSize(options[i]);
                    dl.AddText(new Vector2(textX, rowMin.Y + (rowH - ts.Y) * 0.5f),
                        ImGui.ColorConvertFloat4ToU32(active ? Ink : hot ? Ink : Dim),
                        Fit(options[i], width - (textX - rowMin.X)));
                }

                if (i < options.Length - 1)
                    DrawRowSeparator(dl, rowMin, rowH, mark + 12f, rowMin.X + width);

                ImGui.SetCursorScreenPos(new Vector2(rowMin.X, rowMin.Y + rowH));
            }

            return changed;
        }

        /// <summary>
        /// The line that names a control sitting under it, on the same grid the toggle rows use.
        ///
        /// A control that needs a whole row of its own - a stepper, a colour, a column of choices -
        /// still needs saying what it is, and that line has to sit at the same height and the same
        /// left edge as the labels above and below it or the card reads as two different lists
        /// stacked on each other.
        /// </summary>
        private void DrawSettingLabelRow(string label, string? explanation, float width)
        {
            var dl = ImGui.GetWindowDrawList();
            Vector2 p = ImGui.GetCursorScreenPos();
            float centreY = p.Y + SettingRowHeight * 0.5f;

            float labelWidth;
            using (UiBodyFont.Push())
            {
                float lineH = ImGui.GetTextLineHeight();
                string shown = Fit(label, width - HelpMarkColumn);
                labelWidth = ImGui.CalcTextSize(shown).X;
                dl.AddText(new Vector2(p.X, centreY - lineH * 0.5f),
                    ImGui.ColorConvertFloat4ToU32(Ink), shown);
            }

            if (!string.IsNullOrEmpty(explanation))
                DrawRowHelpMark($"lbl{label}", explanation!,
                    new Vector2(p.X + labelWidth + HelpMarkGap, centreY));

            ImGui.SetCursorScreenPos(new Vector2(p.X, p.Y + SettingRowHeight));
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
        /// <param name="grabbed">Read once, the frame the handle is picked up, and handed back to
        /// <paramref name="released"/> when it is put down. For a setting whose change has a
        /// consequence beyond the config file: the stops crossed mid-drag are not choices, so a
        /// caller needs the two ends of the gesture rather than every value in between.</param>
        /// <param name="released">Called once when the handle is let go, after the save.</param>
        private void DrawSliderSetting<T>(string label, string[] stops, Func<int> get, Action<int> set,
            Func<int, string> explain, Func<T>? grabbed = null, Action<T>? released = null)
        {
            if (stops.Length < 2)
                return;

            float rowWidth = SettingsContentWidth();

            // Every stop in the one hover, not just the selected one: choosing here means comparing
            // the options, and a reader who has to drag the handle to find out what each one sends
            // has already changed the setting.
            DrawSettingLabelRow(label, string.Join("\n\n",
                stops.Select((stop, i) => $"{stop} - {explain(i)}")), rowWidth);

            ImGui.Dummy(new Vector2(0, 4f));

            int value = Math.Clamp(get(), 0, stops.Length - 1);

            const float trackH = 6f;
            const float handleW = 12f;
            const float handleH = 18f;

            float width = SettingsContentWidth();
            float height = handleH;

            Vector2 origin = ImGui.GetCursorScreenPos();
            ImGui.InvisibleButton($"##slider{label}", new Vector2(width, height));

            bool active = ImGui.IsItemActive();
            bool hot = active || ImGui.IsItemHovered();

            // Where the handle was picked up, kept for the release below. Boxed into a field shared
            // by every slider, which is safe because only one control can be active at a time.
            if (ImGui.IsItemActivated() && grabbed != null)
                sliderGrabbedValue = grabbed();

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
            {
                config.Save();

                if (released != null && sliderGrabbedValue is T before)
                    released(before);

                sliderGrabbedValue = null;
            }

            var dl = ImGui.GetWindowDrawList();
            float handleX = left + step * value;

            dl.AddRectFilled(new Vector2(left, trackY - trackH * 0.5f),
                new Vector2(right, trackY + trackH * 0.5f),
                ImGui.ColorConvertFloat4ToU32(Field), Radius.Pill);
            dl.AddRectFilled(new Vector2(left, trackY - trackH * 0.5f),
                new Vector2(handleX, trackY + trackH * 0.5f),
                ImGui.ColorConvertFloat4ToU32(Accent), Radius.Pill);

            // A notch at every stop, so the track shows where the handle can land before anyone
            // drags it and finds out.
            for (int i = 0; i < stops.Length; i++)
            {
                float x = left + step * i;
                dl.AddRectFilled(new Vector2(x - 1f, trackY - trackH * 0.5f - 3f),
                    new Vector2(x + 1f, trackY + trackH * 0.5f + 3f),
                    ImGui.ColorConvertFloat4ToU32(i <= value ? Accent : RuleStrong), Radius.Pill);
            }

            dl.AddRectFilled(new Vector2(handleX - handleW * 0.5f, trackY - handleH * 0.5f),
                new Vector2(handleX + handleW * 0.5f, trackY + handleH * 0.5f),
                ImGui.ColorConvertFloat4ToU32(hot ? AccentHover : Accent), Radius.Pill);

            ImGui.Dummy(new Vector2(0, 6));

            // The stop's name in words as well as in position: the handle alone says how far along
            // the scale you are, not what you have chosen.
            using (UiBodyFont.Push())
                ImGui.TextColored(Ink, stops[value]);

            ImGui.Dummy(new Vector2(0, 12));
        }

        // ── Appearance ────────────────────────────────────────────

        /// <summary>
        /// The seven accents as round swatches, the chosen one ringed in Ink.
        ///
        /// A ring rather than a tick: the swatch is the colour, and a mark drawn in the colour's own
        /// contrast is the one thing guaranteed to be legible on all seven.
        ///
        /// A ROUNDED SQUARE, not a circle and not a square. It is the shape the system this
        /// follows gives anything that is purely its own colour, and the mockup sets it at 28px
        /// with an 8px corner - which is the app-icon proportion, about two-sevenths of the side.
        /// </summary>
        private const float AccentSwatchRadius = 8f;

        private void DrawAccentSwatches()
        {
            // 28px, 8px corner, 10px apart - the mockup's numbers.
            const float swatch = 28f;
            const float gap = 10f;

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

                var max = new Vector2(p.X + swatch, p.Y + swatch);
                dl.AddRectFilled(p, max, ImGui.ColorConvertFloat4ToU32(ColorFromHex(hex)),
                    AccentSwatchRadius);

                // TWO RINGS, WITH THE GROUND BETWEEN THEM. The mockup selects a chip with a 2px
                // halo in the background colour and a 2px ring in the ink outside that, so the mark
                // never touches the colour it is marking - which matters most on the pale ones,
                // where a ring drawn against the fill is the one thing you cannot see.
                if (chosen)
                {
                    dl.AddRect(new Vector2(p.X - 2f, p.Y - 2f), new Vector2(max.X + 2f, max.Y + 2f),
                        ImGui.ColorConvertFloat4ToU32(Field), AccentSwatchRadius + 2f,
                        ImDrawFlags.None, 2f);
                    dl.AddRect(new Vector2(p.X - 4f, p.Y - 4f), new Vector2(max.X + 4f, max.Y + 4f),
                        ImGui.ColorConvertFloat4ToU32(Ink), AccentSwatchRadius + 4f,
                        ImDrawFlags.None, 2f);
                }
                else if (hot)
                {
                    dl.AddRect(new Vector2(p.X - 3f, p.Y - 3f), new Vector2(max.X + 3f, max.Y + 3f),
                        ImGui.ColorConvertFloat4ToU32(BorderControl), AccentSwatchRadius + 3f,
                        ImDrawFlags.None, 1f);
                }

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
