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

        /// <summary>
        /// A page of settings: what the list on the left calls it, and what the right draws.
        ///
        /// GROUPED BY WHAT SOMEBODY CAME HERE TO CHANGE, not by which file the code lives in. The
        /// old layout dealt seven sections into two columns by height, so the Party Finder buttons
        /// sat above the radar and beside the accent picker for no reason anybody could name, and
        /// three of the seven were four lines long - a page of half-empty boxes.
        /// </summary>
        private readonly record struct SettingsPage(string Label, FontAwesomeIcon Icon, Action Draw, string Lucide = "info");

        /// <summary>The tab Back in the settings sidebar returns to: whichever was on screen before
        /// Settings was opened.</summary>
        private MainTab settingsReturnTab = MainTab.Presets;

        private int settingsPage;

        private List<SettingsPage> SettingsPages() => new()
        {
            // Everything that changes what the plugin puts in front of you while you are using the
            // Party Finder: the two buttons it adds, and the radar that reads listings.
            new("Party Finder", FontAwesomeIcon.UserFriends, () =>
            {
                DrawPartyFinderSettings();
                DrawPfRadarSettings();
            }, "sliders-horizontal"),

            // The community half and the data behind it, together.
            //
            // They were two pages and should not have been: every question on this page is the same
            // question in a different form - what the plugin knows about other people, what it tells
            // them about you, and what leaves the machine at all. Splitting "community" from "data"
            // meant somebody turning the system off had to visit two places to find out what that
            // actually did.
            new("Community & privacy", FontAwesomeIcon.UserShield, () =>
            {
                DrawRatingsSettings();

                // Under the section that ends on "broadcast my clears", because this is the other
                // half of that question: one decides whether the feed hears about you, the other
                // whether you hear about the feed. Somebody deciding one is usually deciding both,
                // and putting the display half on the Appearance page would have split a single
                // question across two screens.
                DrawClearAnnounceSettings();

                DrawDataSettings();
            }, "shield-check"),

            // How the plugin looks, including how names are written in it.
            //
            // Player names sat with the community settings for a while on the reasoning that it is a setting about
            // other players. It is not - it is a setting about typography. Nothing about it changes
            // what is sent, stored or shown to anybody else; it changes how a name is drawn on this
            // screen, which is the same kind of choice as the accent colour beside it.
            new("Appearance", FontAwesomeIcon.Palette, () =>
            {
                DrawAppearanceSettings();
                DrawPlayerNameSettings();
            }, "palette"),

            // Version, changelog, the ask.
            new("About", FontAwesomeIcon.InfoCircle, DrawAboutSettings, "info"),
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

            // The mockup's content pane: generous padding round the page.
            const float pagePad = 20f;
            ImGui.SetCursorPosX(pagePad);
            ImGui.BeginChild("SettingsBody",
                new Vector2(ImGui.GetWindowWidth() - pagePad * 2f, -1), false);
            try
            {
                // THE PAGE LIST IS THE SIDEBAR NOW. Opening Settings swaps the sidebar's tabs for
                // its pages and a way back, as the system this is modelled on does - a list of
                // pages inside the body beside a list of tabs outside it was two navigations for
                // one screen. The phone's bottom bar does the same. So the body is the page.
                ImGui.Dummy(new Vector2(0, pagePad));
                pages[settingsPage].Draw();

                // Erased entirely in an ordinary build - see PluginUI.AdminHooks.cs.
                if (settingsPage == pages.Count - 1)
                    DrawPanelSettings();
                return;
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
                int interval = pfAutomation.RefreshAtMinutesLeft;
                int maxHours = Math.Clamp(config.AutoRefresherMaxHours, 0,
                    PfAutomation.MaxRefreshDurationHours);

                // ONE LINE UNDER THE SWITCH THEY BELONG TO. They were a row each, and two rows for
                // "how often" and "until when" made the pair look like two unrelated settings that
                // happened to follow the one they qualify. They read as a sentence: refresh every
                // this, stop after that.
                // Two iOS value rows under the switch: the name on the left, and a stepper on the
                // right - minus, the value, plus. Double-click the value to type any number.
                bool changedA = DrawStepperRow("Refresh at", ref interval,
                    PfAutomation.MinRefreshAtMinutesLeft, PfAutomation.MaxRefreshAtMinutesLeft, 5,
                    "min left", null,
                    "Re-posts your listing once this many minutes or fewer are left on it. A "
                    + "listing lasts 60 minutes, so 30 refreshes it every half hour. Each "
                    + "refresh is checked 30 seconds later and tried again if it didn't take "
                    + $"({PfAutomation.MinRefreshAtMinutesLeft}-{PfAutomation.MaxRefreshAtMinutesLeft} minutes).");
                bool changedB = DrawStepperRow("Stop after", ref maxHours,
                    0, PfAutomation.MaxRefreshDurationHours, 1, "h", "Never",
                    "Stops auto-refreshing after this long, so a listing does not stay up all "
                    + "night unattended. Your listing is not cancelled - it just expires "
                    + "normally. Zero means never stop.");
                if (changedA || changedB)
                {
                    config.AutoRefreshAtMinutesLeft = interval;
                    config.AutoRefresherMaxHours = maxHours;
                    config.Save();
                }
            }

            DrawSetting("Auto-adjust locked slots", () => config.AutoAdjustLockedJobsEnabled,
                v => config.AutoAdjustLockedJobsEnabled = v,
                "Reopens a seat left locked to one job when its player leaves. "
                + "Omitted slots stay omitted, and nothing is re-posted unless a seat needs it.",
                last: true);

            EndSettingsSection();
        }

        private void DrawPlayerNameSettings()
        {
            BeginSettingsSection("Player names");

            DrawChoiceSetting("Show names as", PlayerNameFormat.StyleLabels,
                () => (int)config.PlayerNameStyle,
                v => config.PlayerNameStyle = (PlayerNameStyle)v,
                "Applies everywhere a name appears - the recruitment card, your party, the Party "
                + "Finder board, recent players and profiles. Lookups and links still use the full name.");

            EndSettingsSection();
        }

        private void DrawRatingsSettings()
        {
            BeginSettingsSection("Community");

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
                DrawStatusRow("Log in to a character to change this.");
                DrawBroadcastSetting();

                // EVERY EXIT FROM THIS SECTION CLOSES IT. These three early returns did not, and
                // the section is not a heading - it is a card built by splitting the window's draw
                // list in two and merging it back in EndSettingsSection. Returning past that leaves
                // the list split: no card is painted behind the rows, the indent it applied is
                // never taken off, and the next section to open would split an already-split list.
                EndSettingsSection();
                return;
            }

            // LOCKED OFF BELOW FULL, and said out loud rather than left as a switch that springs
            // back. Anonymous usage stats below Full opts you out (see Configuration.CommunityEnabled),
            // so a live toggle here would be offering something the other setting has already
            // decided - press it and watch it refuse, with the reason two sections away.
            if (config.AnalyticsMode != AnalyticsMode.Full)
            {
                DrawStatusRow("Community: off. Taking part needs \"Anonymous usage stats\" set "
                    + "to Full - see Data below. Below that, this install sends nothing and you are "
                    + "opted out of the clears feed and progress lookups.");
                DrawBroadcastSetting();

                EndSettingsSection();
                return;
            }

            DrawSetting("Take part in the community", () => config.RatingsEnabled,
                AskThenSetRatingsEnabled,
                "Disabling this opts you out of the community: the Clears feed, progress lookups "
                + "and your profile card. Nothing about your duties is sent. It stays opted out "
                + "even after uninstalling the plugin, until you enable this option again.",
                last: !config.RatingsEnabled);

            if (ratingOptOutNote.Length > 0)
                DrawStatusRow(ratingOptOutNote, ratingOptOutFailed ? Negative : null, last: !config.RatingsEnabled);

            if (!config.RatingsEnabled)
            {
                // Broadcasting is a different system and does not go with it - somebody who wants
                // nothing else from the community may still want their Ultimate clear celebrated,
                // and burying that setting behind this one would decide for them.
                DrawBroadcastSetting();

                EndSettingsSection();
                return;
            }

            DrawSetting("Show your party's progress", () => config.PartyRatingsEnabled,
                v => config.PartyRatingsEnabled = v,
                "Shows each party member's prog point beside their name.");

            DrawBroadcastSetting();

            EndSettingsSection();
        }

        // ── The opt-out ───────────────────────────────────────────

        private string ratingOptOutNote = string.Empty;
        private bool ratingOptOutFailed;

        // ── Asking first ──────────────────────────────────────────
        //
        // THE THREE SETTINGS THAT MOVE DATA ASK BEFORE THEY MOVE IT: the community, usage stats and
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
        /// Confirms before the community toggle actually moves.
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
                    "Take part in the community?",
                    "Your clears can be posted to the feed, and your progress looked up.",
                    "Turn it on",
                    () => SetRatingsEnabled(true),
                    detail: "Sent: who you finished a duty with, and clears worth posting to the feed. "
                        + AnonymityLine,
                    danger: false);
                return;
            }

            AskConfirm(
                "Leave the community?",
                "You will be opted out of the Clears feed and progress lookups.",
                "Turn it off",
                () => SetRatingsEnabled(false),
                detail: "You lose: the Players tab and progress on your party panel. Nothing about "
                    + "your duties is sent, and your clear posts are hidden. This survives "
                    + "uninstalling, until you turn it back on.");
        }

        /// <summary>
        /// Turning the community off opts this character out, on the server.
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
                        ? "Opted back in. Your profile and clears are visible again, and any request "
                          + "you had waiting has been withdrawn."
                        : "Request filed. The Players tab is hidden now; your profile and clears "
                          + "stop being visible to others once it is approved.";
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
                DrawStatusRow("Broadcasting is unavailable while you are opted out. Your existing posts "
                    + "are hidden, not deleted - opting back in restores them.", last: true);
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
                    + "community, which stays off until you turn it on yourself."),

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
                detail += " You will also be opted out of the community and clears, and that "
                    + "opt-out is filed with the server - dragging this back up later does not "
                    + "turn the community back on by itself.";
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
                    : "Still opted out. Turn \"Take part in the community\" back on in the "
                      + "Community section to take part again.";
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
                    analyticsOptOutNote = "Opted out. The community and clears are hidden now; your "
                        + "profile stops being visible to others once the request is approved.";
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
        /// this instead" is the entire explanation - so it is said in four words on the row it
        /// applies to, with the reasoning behind the row's own question mark for anybody who wants
        /// it. It was a paragraph under the whole section, attached to the wrong setting.
        /// </summary>
        private void DrawPfRadarSettings()
        {
            BeginSettingsSection("PF Radar settings");

            // THE SHARING SWITCH IS NOT NESTED UNDER THE PANEL ANY MORE. It used to be drawn only
            // while "Show listing details" was on, which read sensibly enough when sharing existed
            // to make that panel worth reading. It is a trap now that publishing runs on its own:
            // turning the panel off would hide the switch while the party went on being published,
            // and a setting somebody cannot see is one they cannot turn off either.
#if PFP_RATINGS
            const bool sharingRow = true;
#else
            const bool sharingRow = false;
#endif

            // THE CONFLICT IS A TAG ON THIS ROW, because this row is the only thing it affects.
            // Only while the setting is on: with it off, "disabled" is what the switch already
            // says, and naming PFRadar as the reason would be untrue.
            bool takenOver = Listings?.SuppressedByPfRadar == true && config.ListingDetailsEnabled;

            DrawSetting("Show listing details", () => config.ListingDetailsEnabled,
                v => config.ListingDetailsEnabled = v,
                "Shows a panel beside a party finder listing with the jobs already in the party, "
                + "the leader and the item level. All of it comes from what the game has already "
                + "loaded to draw that window - nothing is fetched and nobody is asked."
                + (takenOver
                    ? "\n\nPFRadar is running and does the same job from the same part of the game, "
                      + "so this stays out of its way. Sharing your own listing is unaffected."
                    : string.Empty),
                last: !sharingRow,
                tag: takenOver ? "Disabled - PFRadar active" : string.Empty);

#if PFP_RATINGS
            DrawSetting("Share who is in my party finder listing",
                () => config.PfCrowdsourceEnabled,
                AskThenSetCrowdsource,
                "While your party is listed, publishes the party - each member's name, world "
                + "and job - so other people running this plugin can see who is already in "
                + "that listing. One person sharing is enough to describe the whole party, "
                + "which is what makes the panel above worth reading. Only a listed party "
                + "is ever sent; it comes down when the listing ends, and the server forgets "
                + "it within the hour either way.");

            // The Party Finder tab's two options, moved here from the top of the tab: they are
            // decisions made once, and the tab is for the board.
            DrawSetting("Share my party finder data actively",
                () => config.PfActiveShareEnabled,
                v =>
                {
                    config.PfActiveShareEnabled = v;
                    if (v)
                        BoardFetch?.ReadSoon();
                },
                "On: PF Analysis reads the Party Finder every 10 minutes and shares what it finds, "
                + "whether or not you are at the keyboard.\n\n"
                + "Off (the default): it only does so once you have been away (no mouse or keyboard) "
                + "for an hour, then every 10 minutes until you are back.\n\n"
                + "Either way, never in an instance, in combat, or while you have the Party Finder "
                + "open, and a read never holds the window for more than a few seconds.");

            DrawSetting("Coordinated joining",
                () => config.PfCoordinationEnabled,
                v => config.PfCoordinationEnabled = v,
                "Apply to listings with the Join button, and take applications on your own.\n\n"
                + "Any listing you lead takes applications. Plugin users press Join, travel to your "
                + "data centre with /li (Lifestream) if needed, and join it.\n\n"
                + "To keep a listing up without strangers walking in, post a preset with its open "
                + "seats omitted. When every omitted seat has an applicant, PF Analysis reposts it "
                + "as a private listing and moves the applicants in with a password.",
                last: true);
#endif

            EndSettingsSection();
        }

        private void DrawDataSettings()
        {
            BeginSettingsSection("Data");

            var stops = AnalyticsModeInfo.Labels;
            int mode = AnalyticsModeInfo.IndexOf(config.AnalyticsMode);
            string explain = string.Join("\n\n", stops.Select((stop, i) =>
                $"{stop} - {AnalyticsModeInfo.Explain(AnalyticsModeInfo.FromIndex(i))}"));
            if (DrawDropdownRow("Anonymous usage stats", stops, ref mode, explain, last: analyticsOptOutNote.Length == 0))
            {
                var was = config.AnalyticsMode;
                config.AnalyticsMode = AnalyticsModeInfo.FromIndex(mode);
                config.Save();
                CommitAnalyticsMode(was);
            }

            if (analyticsOptOutNote.Length > 0)
                DrawStatusRow(analyticsOptOutNote, analyticsOptOutFailed ? Negative : null, last: true);

            EndSettingsSection();
        }

        private void DrawAppearanceSettings()
        {
            BeginSettingsSection("Appearance");

            // THE WINDOW FIRST, above the accent: it reshapes the window, the accent recolours it.
            int device = (int)config.Device;
            string[] deviceLabels = { "Portrait", "Landscape" };
            if (DrawSegmentRow("Window", "device", deviceLabels, ref device,
                    $"Portrait ({DeviceMetrics.SizeLabel(DeviceLayout.Portrait)}) is one column with a tab bar "
                    + "along the bottom, and fits beside the game. Landscape "
                    + $"({DeviceMetrics.SizeLabel(DeviceLayout.Landscape)}) is wider, with a sidebar and "
                    + "two-column pages, and wants a big monitor. Neither can be resized - each is drawn "
                    + "for the size it is."))
            {
                config.Device = (DeviceLayout)device;
                config.Save();
            }

            DrawSwatchRow("Accent colour",
                "Colours the primary action, the page you are on and the countdown. Role "
                + "colours never change.", last: true);

            EndSettingsSection();
        }

        private void DrawAboutSettings()
        {
            BeginSettingsSection("About PF Analysis");

            // The settings mockup's About card: the name and version in bold, a line of what it is,
            // then the two things you can do from here.
            ImGui.Dummy(new Vector2(0, 12f));
            using (UiTitleFont.Push())
                ImGui.TextColored(new Vector4(1, 1, 1, 1), $"PF Analysis {VersionLabel}");
            ImGui.Dummy(new Vector2(0, 2f));
            using (UiHelpFont.Push())
            {
                ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + SettingsContentWidth());
                ImGui.TextColored(PfSlate300, "Party Finder presets, recruitment and clears for Final Fantasy XIV.");
                ImGui.PopTextWrapPos();
            }
            ImGui.Dummy(new Vector2(0, 10f));

            float w = MathF.Min(200f, (SettingsContentWidth() - 8f) * 0.5f);
            if (DrawIosButton("View changelog", "##OpenChangelog", FontAwesomeIcon.FileAlt, new Vector2(w, 32f), primary: false))
            {
                // A sheet only appears once it is opened as one - setting the flag alone left the
                // button doing nothing at all.
                isChangelogVisible = true;
                OpenSheet(SheetKind.Changelog);
            }
            ImGui.SameLine(0, 8f);
            // The whole run again, from the fork - where the window shape, the accent and the
            // announcements were first asked about, and a way to re-read the tour.
            if (DrawIosButton("Replay onboarding", "##ReplayOnboarding", FontAwesomeIcon.Redo, new Vector2(w, 32f), primary: false))
                ReplayOnboarding();

            ImGui.Dummy(new Vector2(0, 14f));
            EndSettingsSection();

            // Room so the last card clears the bottom of the scroll region.
            ImGui.Dummy(new Vector2(0, 24));
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
        /// <param name="tag">A short status word at the right end of the row, or empty for none.
        ///
        /// FOR "THIS IS ON BUT SOMETHING ELSE HAS TAKEN IT OVER", which a switch cannot say by
        /// itself - it reads as on, and the feature does nothing. It goes ON the row it describes
        /// and it is a few words, because the version of this that was a sentence under the whole
        /// section sat beneath a different setting than the one it was about, ran off the side of
        /// the page, and explained at paragraph length something the reader only needed named.</param>
        /// <summary>Settings rows whose explanation is open under them, by label.</summary>
        private readonly HashSet<string> settingsHelpOpen = new();

        // ── The row every setting is ──────────────────────────────
        //
        // THE SETTINGS MOCKUP'S ROW, AND EVERY SETTING IS ONE: 48px, the name in white on the left
        // with a round info button after it that opens the explanation on a darker panel under the
        // row, the control at the right edge, and a hairline across the whole card between rows.
        // The switch, the stepper, the menu, the swatches and the rest differ only in what sits on
        // the right.

        private readonly record struct SettingsRow(Vector2 Min, float CentreY, float Width, bool Open, string Key, string Help);

        /// <summary>Starts a row: the name and its info button. <paramref name="rightReserve"/> is
        /// the room the control on the right takes, so the name is fitted short of it.</summary>
        private SettingsRow BeginSettingsRow(string label, string help, float rightReserve)
        {
            const float info = 20f;
            var dl = ImGui.GetWindowDrawList();
            float width = SettingsContentWidth();
            Vector2 min = ImGui.GetCursorScreenPos();
            float cy = min.Y + SettingRowHeight * 0.5f;
            bool open = settingsHelpOpen.Contains(label);

            string shown;
            float labelW;
            using (UiBodyFont.Push())
            {
                float lh = ImGui.GetTextLineHeight();
                shown = Fit(label, MathF.Max(40f, width - rightReserve - info - 24f));
                labelW = ImGui.CalcTextSize(shown).X;
                dl.AddText(new Vector2(min.X, cy - lh * 0.5f), ImGui.ColorConvertFloat4ToU32(new Vector4(1, 1, 1, 1)), shown);
            }

            if (help.Length > 0)
            {
                var infoMin = new Vector2(min.X + labelW + 8f, cy - info * 0.5f);
                ImGui.SetCursorScreenPos(infoMin);
                if (ImGui.InvisibleButton($"##rowhelp{label}", new Vector2(info)))
                {
                    if (!settingsHelpOpen.Remove(label))
                        settingsHelpOpen.Add(label);
                    open = !open;
                }
                bool hot = ImGui.IsItemHovered();
                if (hot)
                    ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                DrawInfoCircle(dl, infoMin, hot, open);
            }

            ImGui.SetCursorScreenPos(min);
            return new SettingsRow(min, cy, width, open, label, help);
        }

        /// <summary>Ends a row: its explanation when open, the hairline, and the cursor past it.</summary>
        private void EndSettingsRow(SettingsRow row, bool last)
        {
            var dl = ImGui.GetWindowDrawList();
            float bottom = row.Min.Y + SettingRowHeight;

            if (row.Open && row.Help.Length > 0)
            {
                const float pad = 10f;
                using (UiHelpFont.Push())
                {
                    float lh = ImGui.GetTextLineHeight() + 2f;
                    var lines = new List<string>();
                    foreach (string para in row.Help.Replace("\r", string.Empty).Split('\n'))
                    {
                        if (para.Trim().Length == 0)
                            lines.Add(string.Empty);
                        else
                            lines.AddRange(FbWrap(para.Trim(), row.Width - pad * 2f));
                    }
                    var hMin = new Vector2(row.Min.X, bottom);
                    var hMax = hMin + new Vector2(row.Width, pad * 2f + lines.Count * lh);
                    dl.AddRectFilled(hMin, hMax, ImGui.ColorConvertFloat4ToU32(new Vector4(0, 0, 0, 0.4f)), Radius.Card);
                    dl.AddRect(hMin, hMax, ImGui.ColorConvertFloat4ToU32(new Vector4(1, 1, 1, 0.05f)), Radius.Card, ImDrawFlags.None, 1f);
                    for (int i = 0; i < lines.Count; i++)
                        dl.AddText(hMin + new Vector2(pad, pad + i * lh), ImGui.ColorConvertFloat4ToU32(FbSlate400), lines[i]);
                    bottom = hMax.Y + 10f;
                }
            }

            if (!last)
                dl.AddRectFilled(new Vector2(settingsCardMin.X, bottom), new Vector2(settingsCardMin.X + settingsCardWidth, bottom + 1f),
                    ImGui.ColorConvertFloat4ToU32(new Vector4(1, 1, 1, 0.08f)));
            ImGui.SetCursorScreenPos(new Vector2(row.Min.X, bottom + (last ? 0f : 1f)));
        }

        /// <summary>
        /// A choice as an iOS menu: the current value in grey at the right with an up-down chevron,
        /// and a menu of the options, the chosen one ticked, opening under it. True when changed.
        /// </summary>
        private bool DrawDropdownRow(string label, string[] options, ref int value, string help, bool last = false)
        {
            if (options.Length == 0)
                return false;
            value = Math.Clamp(value, 0, options.Length - 1);

            float valueW;
            using (UiBodyFont.Push())
                valueW = options.Max(o => ImGui.CalcTextSize(o).X) + 28f;
            var row = BeginSettingsRow(label, help, valueW);
            var dl = ImGui.GetWindowDrawList();

            string current = options[value];
            float curW;
            using (UiBodyFont.Push())
                curW = ImGui.CalcTextSize(current).X;
            float boxW = curW + 34f;
            var bMin = new Vector2(row.Min.X + row.Width - boxW, row.CentreY - 15f);
            var bSize = new Vector2(boxW, 30f);
            ImGui.SetCursorScreenPos(bMin);
            string popup = $"##dropdown{label}";
            if (ImGui.InvisibleButton($"##dropbtn{label}", bSize))
                ImGui.OpenPopup(popup);
            bool hot = ImGui.IsItemHovered();
            bool isOpen = ImGui.IsPopupOpen(popup);
            if (hot)
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            if (hot || isOpen)
                dl.AddRectFilled(bMin, bMin + bSize, ImGui.ColorConvertFloat4ToU32(new Vector4(1, 1, 1, 0.08f)), Radius.Small);

            using (UiBodyFont.Push())
            {
                float lh = ImGui.GetTextLineHeight();
                dl.AddText(new Vector2(bMin.X + 8f, row.CentreY - lh * 0.5f),
                    ImGui.ColorConvertFloat4ToU32(hot || isOpen ? new Vector4(1, 1, 1, 1) : FbSlate400), current);
            }
            // The up-down chevron iOS puts on a menu button.
            var cx = bMin.X + boxW - 13f;
            uint ink = ImGui.ColorConvertFloat4ToU32(hot || isOpen ? new Vector4(1, 1, 1, 1) : FbSlate400);
            dl.AddLine(new Vector2(cx - 3.5f, row.CentreY - 1.5f), new Vector2(cx, row.CentreY - 5f), ink, 1.4f);
            dl.AddLine(new Vector2(cx, row.CentreY - 5f), new Vector2(cx + 3.5f, row.CentreY - 1.5f), ink, 1.4f);
            dl.AddLine(new Vector2(cx - 3.5f, row.CentreY + 1.5f), new Vector2(cx, row.CentreY + 5f), ink, 1.4f);
            dl.AddLine(new Vector2(cx, row.CentreY + 5f), new Vector2(cx + 3.5f, row.CentreY + 1.5f), ink, 1.4f);

            bool changed = false;
            ImGui.SetNextWindowPos(bMin + new Vector2(boxW, bSize.Y + 4f), ImGuiCond.Always, new Vector2(1f, 0f));
            PushIosMenuStyle();
            if (ImGui.BeginPopup(popup))
            {
                ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, Vector2.Zero);
                for (int i = 0; i < options.Length; i++)
                {
                    if (IosMenuCheckItem(options[i], i == value) && i != value)
                    {
                        value = i;
                        changed = true;
                    }
                }
                ImGui.PopStyleVar();
                ImGui.EndPopup();
            }
            PopIosMenuStyle();

            EndSettingsRow(row, last);
            return changed;
        }

        /// <summary>The segmented picker at the right of a row, sized to its labels.</summary>
        private bool DrawSegmentRow(string label, string id, string[] options, ref int value, string help, bool last = false)
        {
            float fit = IosSegmentedFitWidth(options, SettingsContentWidth() * 0.62f);
            var row = BeginSettingsRow(label, help, fit);
            ImGui.SetCursorScreenPos(new Vector2(row.Min.X + row.Width - fit, row.CentreY - 15f));
            bool changed = DrawIosSegmented(id, options, ref value, fit);
            EndSettingsRow(row, last);
            return changed;
        }

        /// <summary>The accent colours as circles at the right of a row.</summary>
        private void DrawSwatchRow(string label, string help, bool last = false)
        {
            float swatchesW = AccentChoices.Length * 28f + (AccentChoices.Length - 1) * 10f;
            var row = BeginSettingsRow(label, help, swatchesW);
            ImGui.SetCursorScreenPos(new Vector2(row.Min.X + row.Width - swatchesW, row.CentreY - 14f));
            ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(ImGui.GetStyle().ItemSpacing.X, 0f));
            DrawAccentSwatches();
            ImGui.PopStyleVar();
            EndSettingsRow(row, last);
        }

        /// <summary>A row whose control is a small button at the right - "Play" for the preview.</summary>
        private bool DrawButtonRow(string label, string help, string button, FontAwesomeIcon icon, bool last = false)
        {
            float bw;
            using (UiBodyFont.Push())
                bw = ImGui.CalcTextSize(button).X + 44f;
            var row = BeginSettingsRow(label, help, bw);
            ImGui.SetCursorScreenPos(new Vector2(row.Min.X + row.Width - bw, row.CentreY - 15f));
            bool clicked = DrawIosButton(button, $"##rowbtn{label}", icon, new Vector2(bw, 30f), primary: true);
            EndSettingsRow(row, last);
            return clicked;
        }

        /// <summary>A number changed by dragging sideways across its value, or typed on a
        /// double-click - for offsets, which are found by moving and looking.</summary>
        private bool DrawDragRow(string label, ref int value, int min, int max, string suffix, string help, bool last = false)
        {
            const float pillW = 96f, pillH = 28f;
            var row = BeginSettingsRow(label, help, pillW);
            bool changed = DrawDragNumberChip($"drag{label}", ref value, suffix, min, max,
                new Vector2(row.Min.X + row.Width - pillW, row.CentreY - pillH * 0.5f), new Vector2(pillW, pillH));
            EndSettingsRow(row, last);
            return changed;
        }

        /// <summary>A line of status on its own row: grey, or tinted when it is a result.</summary>
        private void DrawStatusRow(string text, Vector4? tint = null, bool last = false)
        {
            var dl = ImGui.GetWindowDrawList();
            float width = SettingsContentWidth();
            Vector2 min = ImGui.GetCursorScreenPos();
            float lh;
            List<string> lines;
            using (UiHelpFont.Push())
            {
                lh = ImGui.GetTextLineHeight() + 2f;
                lines = FbWrap(text, width);
            }
            float h = MathF.Max(SettingRowHeight, lines.Count * lh + 24f);
            float top = min.Y + (h - lines.Count * lh) * 0.5f;
            using (UiHelpFont.Push())
                for (int i = 0; i < lines.Count; i++)
                    dl.AddText(new Vector2(min.X, top + i * lh), ImGui.ColorConvertFloat4ToU32(tint ?? FbSlate400), lines[i]);
            float bottom = min.Y + h;
            if (!last)
                dl.AddRectFilled(new Vector2(settingsCardMin.X, bottom), new Vector2(settingsCardMin.X + settingsCardWidth, bottom + 1f),
                    ImGui.ColorConvertFloat4ToU32(new Vector4(1, 1, 1, 0.08f)));
            ImGui.SetCursorScreenPos(new Vector2(min.X, bottom + (last ? 0f : 1f)));
        }

        private void DrawSetting(string label, Func<bool> get, Action<bool> set, string explanation,
            bool last = false, bool joinNext = false, string tag = "")
        {
            bool value = get();
            float tagW = 0f;
            if (tag.Length > 0)
                using (UiHelpFont.Push())
                    tagW = ImGui.CalcTextSize(tag).X + 12f;

            var row = BeginSettingsRow(label, explanation, FbSwitchW + tagW);
            var dl = ImGui.GetWindowDrawList();

            // The whole row toggles; the info button, submitted first, keeps its own clicks.
            bool rowClicked = ImGui.InvisibleButton($"##setrow{label}", new Vector2(row.Width, SettingRowHeight));
            if (ImGui.IsItemHovered())
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);

            float switchX = row.Min.X + row.Width - FbSwitchW;
            if (tag.Length > 0)
                using (UiHelpFont.Push())
                {
                    Vector2 ts = ImGui.CalcTextSize(tag);
                    dl.AddText(new Vector2(switchX - 12f - ts.X, row.CentreY - ts.Y * 0.5f),
                        ImGui.ColorConvertFloat4ToU32(AccentYellow), tag);
                }

            float t = switchAnim.TryGetValue($"set{label}", out float anim) ? anim : (value ? 1f : 0f);
            DrawFbSwitch(dl, new Vector2(switchX, row.CentreY - FbSwitchH * 0.5f), value, ref t);
            switchAnim[$"set{label}"] = t;

            if (rowClicked)
            {
                set(!value);
                config.Save();
            }

            EndSettingsRow(row, last);
        }


        /// <summary>A settings row's height and the gap between its control and its words - 8px of
        /// padding either side of a 20px switch, and 11px across, from the mockup.</summary>
        private const float SettingRowHeight = 48f;


        /// <summary>
        /// A number as an iOS row: the name and its info button on the left, and on the right the
        /// value beside a stepper - a small grey pill split into minus and plus. The value is typed
        /// by double-clicking it, as it always could be; the stepper nudges it by <paramref name="step"/>.
        /// </summary>
        private bool DrawStepperRow(string label, ref int value, int min, int max, int step,
            string suffix, string? zeroLabel, string help, bool last = false)
        {
            const float pillW = 84f, pillH = 28f, valueW = 86f;
            var row = BeginSettingsRow(label, help, pillW + valueW + 8f);
            var dl = ImGui.GetWindowDrawList();
            bool changed = false;

            // The stepper: a grey pill split into minus and plus.
            var pMin = new Vector2(row.Min.X + row.Width - pillW, row.CentreY - pillH * 0.5f);
            dl.AddRectFilled(pMin, pMin + new Vector2(pillW, pillH), ImGui.ColorConvertFloat4ToU32(new Vector4(1, 1, 1, 0.1f)), 9f);
            dl.AddRectFilled(new Vector2(pMin.X + pillW * 0.5f - 0.5f, pMin.Y + 6f), new Vector2(pMin.X + pillW * 0.5f + 0.5f, pMin.Y + pillH - 6f),
                ImGui.ColorConvertFloat4ToU32(new Vector4(1, 1, 1, 0.15f)));
            for (int i = 0; i < 2; i++)
            {
                bool minus = i == 0;
                var sMin = new Vector2(pMin.X + pillW * 0.5f * i, pMin.Y);
                var sSize = new Vector2(pillW * 0.5f, pillH);
                bool enabled = minus ? value > min : value < max;
                ImGui.SetCursorScreenPos(sMin);
                bool clicked = ImGui.InvisibleButton($"##step{label}{i}", sSize) && enabled;
                bool hot = ImGui.IsItemHovered() && enabled;
                if (hot)
                {
                    ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                    dl.AddRectFilled(sMin, sMin + sSize, ImGui.ColorConvertFloat4ToU32(new Vector4(1, 1, 1, 0.08f)), 9f,
                        minus ? ImDrawFlags.RoundCornersLeft : ImDrawFlags.RoundCornersRight);
                }
                // Drawn strokes, sharp at any size.
                var c = sMin + sSize * 0.5f;
                uint ink = ImGui.ColorConvertFloat4ToU32(enabled ? new Vector4(1, 1, 1, 1) : FbSlate500);
                dl.AddLine(c - new Vector2(5f, 0f), c + new Vector2(5f, 0f), ink, 1.6f);
                if (!minus)
                    dl.AddLine(c - new Vector2(0f, 5f), c + new Vector2(0f, 5f), ink, 1.6f);
                if (clicked)
                {
                    value = Math.Clamp(minus ? value - step : value + step, min, max);
                    changed = true;
                }
            }

            // The value, just left of the stepper; double-click to type it.
            string id = $"step{label}";
            var vMin = new Vector2(pMin.X - 8f - valueW, row.CentreY - pillH * 0.5f);
            if (chipEditingId == id)
            {
                changed |= DrawEditableNumberChip(id, ref value, suffix, zeroLabel, min, max, vMin,
                    new Vector2(valueW, pillH), "Type a number, then Enter.");
            }
            else
            {
                ImGui.SetCursorScreenPos(vMin);
                ImGui.InvisibleButton($"##stepvalue{label}", new Vector2(valueW, pillH));
                bool vHot = ImGui.IsItemHovered();
                if (vHot)
                {
                    PaddedTooltip("Double-click to type a number.");
                    if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                    {
                        chipEditingId = id;
                        chipEditValue = value;
                        chipEditFocusPending = true;
                    }
                }
                string shown = value <= 0 && zeroLabel != null ? zeroLabel : $"{value} {suffix}";
                using (UiBodyFont.Push())
                {
                    Vector2 ts = ImGui.CalcTextSize(shown);
                    dl.AddText(new Vector2(vMin.X + valueW - ts.X, row.CentreY - ts.Y * 0.5f),
                        ImGui.ColorConvertFloat4ToU32(vHot ? new Vector4(1, 1, 1, 1) : FbSlate400), shown);
                }
            }

            EndSettingsRow(row, last);
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
            string explanation, bool last = false)
        {
            int value = Math.Clamp(get(), 0, options.Length - 1);
            if (DrawDropdownRow(label, options, ref value, explanation, last))
            {
                set(value);
                config.Save();
            }
        }

        // ── Appearance ────────────────────────────────────────────

        // DrawAccentSwatches used to live here. It is in PluginUI.Theme.cs now: the accent is a
        // theme setting, not a rating one, and the onboarding - which is compiled into every build
        // - needs the same control. See the note on it there.

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
