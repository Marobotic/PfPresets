#if PFP_RATINGS
using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;

namespace PfPresets
{
    /// <summary>
    /// Which section of the main window is showing.
    ///
    /// The names are the original ones and stay that way: they are persisted in the window state
    /// and referenced across half the UI, and renaming an enum to match a label is churn that buys
    /// nothing. What the tabs are *called* lives in the nav strip - "Recruit" and "Players".
    /// </summary>
    public enum MainTab
    {
        Presets = 0,
        Ratings = 1,
        Settings = 2,
    }

    /// <summary>
    /// The Ratings tab: the people you can still vote on, and a lookup for anyone else.
    ///
    /// Voting lives here and in the post-duty prompt, nowhere else. Contacts is a record of who you
    /// met; the lookup can show you anyone's score but can rate nobody, because you may only vote
    /// on someone you actually finished a duty with in the last 24 hours.
    /// </summary>
    public partial class PluginUI
    {
        private MainTab activeTab = MainTab.Presets;

        private string ratingSearchInput = string.Empty;
        private CharacterIdentity? ratingSearchTarget;
        private string ratingSearchError = string.Empty;

        /// <summary>
        /// Name matches from people this install has actually met, for the current input.
        ///
        /// Local by necessity, not by preference: the server stores every character as
        /// HMAC(pepper, "name@world"), so it cannot answer "who is called Kat" - a keyed hash has
        /// no prefix to match on. Anything typed here that isn't someone you've met still has to
        /// be given in full as Name@World.
        /// </summary>
        private readonly List<CharacterIdentity> ratingSearchSuggestions = new();

        /// <summary>Input the suggestion list was last built for, so it rebuilds on change rather
        /// than every frame. Starts at a value no input can equal.</summary>
        private string ratingSearchSuggestFor = "￿";

        private const int MaxSearchSuggestions = 6;

        /// <summary>The top tab strip's height, and the rail row's, are the button height - a tab
        /// is a control you press, and there is no reason for it to be a different size from the
        /// controls beside it.</summary>
        private const float NavStripHeight = ButtonHeight;

        // ══════════════════════════════════════════════════════════
        //  NAV STRIP
        // ══════════════════════════════════════════════════════════

        /// <summary>
        /// The tab strip: plain labels with an underline that lights on the active one. Drawn by
        /// hand rather than with ImGui's tab bar, whose filled-chrome look fights the rest of the
        /// window.
        /// </summary>
        private void DrawNavStrip()
        {
            Vector2 start = ImGui.GetCursorScreenPos();
            float winX = ImGui.GetWindowPos().X;
            float width = ImGui.GetWindowWidth();

            // Ratings is absent, not disabled, when the feature is off. A tab that exists only to
            // explain why it does nothing is worse than no tab: the setting already says what it
            // does, and the strip should describe the plugin you are actually running.
            var tabList = new List<(string Label, FontAwesomeIcon Icon, MainTab Tab)>
            {
                ("Recruit", FontAwesomeIcon.Users, MainTab.Presets),
            };

            if (config.RatingsEnabled)
                tabList.Add(("My Profile", FontAwesomeIcon.Star, MainTab.Ratings));

            tabList.Add(("Settings", FontAwesomeIcon.Cog, MainTab.Settings));

            var tabs = tabList.ToArray();

            // Turning ratings off while looking at them would otherwise leave the window on a tab
            // that is no longer in the strip.
            if (activeTab == MainTab.Ratings && !config.RatingsEnabled)
                activeTab = MainTab.Presets;

            float segWidth = (width - 16f) / tabs.Length;

            if (ChromeDiagnosticRequested) ReportChromeDiagnostic($"nav strip: {tabs.Length} tabs, seg={segWidth:F0} "
                + $"at y={start.Y:F0}, winX={winX:F0}, width={width:F0}");
            var dl = ImGui.GetWindowDrawList();

            // A faint baseline under the whole strip, so the inactive tabs still read as a row
            // rather than three loose labels floating in space.
            float baselineY = start.Y + NavStripHeight - 2f;
            dl.AddLine(new Vector2(winX, baselineY), new Vector2(winX + width, baselineY),
                ImGui.ColorConvertFloat4ToU32(BorderDefault), 1.0f);

            for (int i = 0; i < tabs.Length; i++)
            {
                var (label, icon, tab) = tabs[i];
                var pos = new Vector2(winX + 8f + segWidth * i, start.Y);
                var size = new Vector2(segWidth, NavStripHeight - 2f);
                DrawNavTab(dl, label, icon, tab, pos, size, baselineY);
            }

            ImGui.SetCursorScreenPos(new Vector2(winX + 8, start.Y + NavStripHeight + 2));
        }

        private void DrawNavTab(ImDrawListPtr dl, string label, FontAwesomeIcon icon, MainTab tab,
            Vector2 pos, Vector2 size, float baselineY)
        {
            bool active = activeTab == tab;

            ImGui.SetCursorScreenPos(pos);

            // Invisible hit area: every bit of chrome is painted below, so the button must add
            // none of its own - no fill, no border, no rounding.
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0, 0, 0, 0));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(1, 1, 1, 0.04f));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(1, 1, 1, 0.07f));
            ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 0f);
            ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 0f);

            if (ImGui.Button($"##Nav{tab}", size))
                activeTab = tab;

            bool hovered = ImGui.IsItemHovered();

            ImGui.PopStyleVar(2);
            ImGui.PopStyleColor(3);

            var color = active ? TextPrimary : hovered ? TextSecondary : TextMuted;
            DrawIconLabelCentered(icon, label, pos, size, color);

            if (!active)
                return;

            // The lit underline. Inset so it reads as belonging to the label rather than running
            // the full width of the cell.
            const float inset = 14f;
            dl.AddRectFilled(
                new Vector2(pos.X + inset, baselineY - 1f),
                new Vector2(pos.X + size.X - inset, baselineY + 1f),
                ImGui.ColorConvertFloat4ToU32(AccentBlue), 1f);
        }

        // ══════════════════════════════════════════════════════════
        //  RATINGS TAB
        // ══════════════════════════════════════════════════════════

        /// <summary>The profile pane's width in the split layout, and the body width the split
        /// needs before it earns its keep.</summary>
        private const float ProfilePaneWidth = 320f;
        private const float RatingsSplitMinWidth = 760f;

        /// <summary>
        /// The My Profile tab: who you can still rate on the left, and a character on the right.
        ///
        /// Split rather than stacked, because the two halves answer different questions and the
        /// tab was previously one column that swapped between them - looking someone up replaced
        /// the list of people waiting to be rated, and going back replaced the person you were
        /// reading. Below <see cref="RatingsSplitMinWidth"/> there is no room for two columns, so
        /// the card goes on top and the list under it.
        /// </summary>
        private void DrawRatingsTab()
        {
            if (!config.RatingsEnabled)
            {
                DrawRatingsDisabledNotice();
                return;
            }

            DrawRatingSearchBar();

            float bodyWidth = ImGui.GetWindowWidth() - 16;
            ImGui.BeginChild("RatingsBody", new Vector2(bodyWidth, 0), false);
            try
            {
                if (!string.IsNullOrEmpty(ratingSearchError))
                {
                    ImGui.Indent(8);
                    ImGui.PushTextWrapPos(ImGui.GetContentRegionMax().X - 8);
                    ImGui.TextColored(KoFi, ratingSearchError);
                    ImGui.PopTextWrapPos();
                    ImGui.Unindent(8);
                    return;
                }

                if (bodyWidth < RatingsSplitMinWidth)
                {
                    // Narrow: the card first, because it is the one thing on this tab that is about
                    // you, and the tab is called My Profile.
                    DrawProfilePane(compact: true);
                    ImGui.Dummy(new Vector2(0, 8));
                    DrawRatingLists();
                    return;
                }

                float listWidth = bodyWidth - ProfilePaneWidth - 18f;

                ImGui.BeginChild("RatingsListColumn", new Vector2(listWidth, -1), false);
                try
                {
                    DrawRatingLists();
                }
                finally
                {
                    ImGui.EndChild();
                }

                ImGui.SameLine(0, 18);

                ImGui.BeginChild("RatingsProfileColumn", new Vector2(ProfilePaneWidth, -1), false);
                try
                {
                    DrawProfilePane(compact: false);
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
        /// The right-hand pane: whoever was looked up, or you.
        ///
        /// Falling back to your own card rather than leaving the pane empty is the whole reason the
        /// tab is named My Profile - with nothing searched, the thing worth showing is your own
        /// standing.
        /// </summary>
        private void DrawProfilePane(bool compact)
        {
            var who = ratingSearchTarget ?? LocalIdentity?.Invoke();
            if (who == null)
                return;

            bool searched = ratingSearchTarget != null;

            // No measured height in either layout: the card sizes itself to its contents now, so
            // "compact" is simply the same card in a narrower column.
            DrawProfileCard(who, showBack: searched);
        }

        /// <summary>
        /// The left-hand column: the people you can still vote on, then everyone else you have met.
        ///
        /// Two sections with headings, rather than one list that changes meaning halfway down. The
        /// first section keeps its rows even when it is empty - "nobody right now" is information,
        /// and a heading that disappears takes the explanation with it.
        /// </summary>
        private void DrawRatingLists()
        {
            // A name being typed matches people you have met; those matches are what the column is
            // for while they exist.
            if (ratingSearchSuggestions.Count > 0)
            {
                DrawSearchSuggestions();
                return;
            }

            var eligible = Ratings?.EligibleToRate();

            // The section is absent when there is nobody in it, heading and all.
            //
            // It used to keep its heading over a line explaining that the group turns up here after
            // a duty, which was both a permanent empty section and untrue - the party appears on
            // Recruit. A heading with nothing under it is a promise the tab does not keep.
            if (eligible is { Count: > 0 })
            {
                ImGui.Indent(8);
                DrawListHeading("You can still rate these");
                ImGui.Unindent(8);

                DrawSkipAll(eligible.Count);

                if (Ratings != null)
                {
                    var identities = new List<CharacterIdentity>(eligible.Count);
                    foreach (var c in eligible)
                        identities.Add(c.Identity);
                    Ratings.Prefetch(identities);
                }

                foreach (var contact in eligible)
                    DrawRateRow(contact);

                DrawRatingStatusLine();
            }

            DrawRecentPlayers();
        }

        /// <summary>
        /// Declines the whole round.
        ///
        /// Right-aligned and quiet: it is the alternative to answering, not an equal option beside
        /// it. Confirmed because it cannot be undone - the votes are not postponed, they are given
        /// up, and a misclick would otherwise cost a whole duty's worth of ratings.
        /// </summary>
        private void DrawSkipAll(int waiting)
        {
            ImGui.Indent(8);

            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(TextMuted, waiting == 1 ? "1 person to rate" : $"{waiting} people to rate");

            const float w = 74f;
            ImGui.SameLine();
            ImGui.SetCursorPosX(ImGui.GetContentRegionMax().X - w - 8f);

            if (DrawSecondaryButton("Skip all##SkipVotes", new Vector2(w, ButtonHeight)))
            {
                AskConfirm("Skip these ratings", $"Skip all {waiting}?", "Skip them",
                    () =>
                    {
                        int skipped = Encounters?.SkipAllVotable() ?? 0;
                        if (skipped > 0)
                            rateStates.Clear();
                    },
                    detail: "They won't come back - you can still look anyone up by name.");
            }

            ImGui.Unindent(8);
            ImGui.Dummy(new Vector2(0, 4));
        }

        private void DrawRecentPlayers()
        {
            var recent = Players?.Recent(PlayerHistory.RecentShown);
            if (recent == null || recent.Count == 0)
                return;

            ImGui.Dummy(new Vector2(0, 6));
            ImGui.Indent(8);

            // How many of them are on screen is a fact about the list, not about the people in it,
            // and search finds anyone below the cut anyway - so it lives on the heading's "?"
            // rather than as a line of its own.
            int total = Players?.MetCount ?? recent.Count;
            bool capped = total > recent.Count;

            DrawListHeading("Everyone you have met",
                capped ? "recentcount" : null,
                capped
                    ? $"Showing the {recent.Count} most recent of {total}. Everyone else is still "
                        + "here - search for them by name."
                    : null);

            ImGui.Unindent(8);
            ImGui.Dummy(new Vector2(0, 2));

            clickedProfile = null;
            foreach (var entry in recent)
                DrawRecentPlayerRow(entry);

            if (clickedProfile != null)
            {
                OpenProfile(clickedProfile);
                clickedProfile = null;
            }
        }

        /// <summary>Set by a row click and acted on after the list is drawn - opening a dialog
        /// mid-enumeration is what crashed the search results.</summary>
        private CharacterIdentity? clickedProfile;

        /// <summary>
        /// Shows a character in the Ratings tab's profile card.
        ///
        /// One place, deliberately. This briefly had its own window as well, which meant two
        /// profiles that could disagree and two things to keep in step.
        /// </summary>
        private void OpenProfile(CharacterIdentity who)
        {
            if (!who.IsValid)
                return;

            activeTab = MainTab.Ratings;
            ratingSearchTarget = who;
            ratingSearchInput = $"{who.Name}@{who.World}";
            ratingSearchSuggestFor = ratingSearchInput;
            ratingSearchSuggestions.Clear();
            ratingSearchError = string.Empty;
        }

        /// <summary>
        /// The height of a row in the "everyone you have met" list.
        ///
        /// A vote row's height plus room to breathe. The two lists are one column and the earlier
        /// players are most of it, so they get the size of a list you are meant to read rather than
        /// the size of a footnote.
        /// </summary>
        private static float RecentRowHeight() => RateRowHeight(withSubline: false) + 8f;

        private void DrawRecentPlayerRow(PlayerSeen entry)
        {
            // What this install thinks of them, when it has an opinion. The list is now everyone
            // met rather than only everyone rated, so most rows have no arrow - and the ones that
            // do are the trace that rating someone worked, which is why the rating history exists.
            var rated = History?.LastRatingFor(entry.Identity);

            // The same height as a vote row above it. The two lists are one column - one at 32px
            // and one at 42px read as a list and a footnote, and the earlier players are not a
            // footnote.
            DrawHoverRow($"recent{entry.Key}", rightEdge =>
            {
                Vector2 start = ImGui.GetCursorScreenPos();

                string ago = Ago(entry.LastSeenUtc);

                // Fixed columns, measured from the row's right edge.
                //
                // The block used to be sized from the time string, so "9h" and "3w ago" pushed
                // the arrow to different places and the list never lined up down the page. The
                // time now right-aligns inside a reserved slot and the arrow sits at a constant
                // offset, whatever either of them says.
                const float timeColumn = 52f;
                const float menuColumn = 26f;
                float timeRight = rightEdge - menuColumn;
                float arrowLeft = timeRight - timeColumn - 22f;

                DrawRowIdentity(entry.JobId, entry.Name, entry.World, start.X, arrowLeft - 8f);

                ImGui.SameLine();
                ImGui.SetCursorScreenPos(new Vector2(arrowLeft, start.Y));

                if (rated == null)
                {
                    // Met but never rated, which is now the common case. Left blank rather than
                    // given a placeholder: a dot in every row would be noise standing in for
                    // nothing, and the column is only interesting where it says something.
                }
                else if (rated.Direction == VoteDirection.Unknown)
                {
                    ImGui.AlignTextToFramePadding();
                    ImGui.TextColored(TextMuted, "\u00b7");
                    if (ImGui.IsItemHovered())
                        PaddedTooltip("Rated, but this install no longer remembers which way.");
                }
                else
                {
                    bool up = rated.Direction == VoteDirection.Up;
                    ImGui.PushFont(UiBuilder.IconFont);
                    ImGui.AlignTextToFramePadding();
                    ImGui.TextColored(up ? AccentGreen : AccentRed,
                        (up ? FontAwesomeIcon.CaretUp : FontAwesomeIcon.CaretDown).ToIconString());
                    ImGui.PopFont();
                    if (ImGui.IsItemHovered())
                        PaddedTooltip($"You rated them {(up ? "up" : "down")} {Ago(rated.RatedUtc)}.");
                }

                ImGui.SameLine();
                float agoW = ImGui.CalcTextSize(ago).X;
                ImGui.SetCursorScreenPos(new Vector2(timeRight - agoW, start.Y));
                ImGui.AlignTextToFramePadding();
                ImGui.TextColored(TextMuted, ago);

                ImGui.SameLine();
                bool onMenu = DrawRowKebab(rightEdge, 22f, "Report, look up");

                // Clicking a recent player opens their profile. The party list deliberately does
                // not do this - rows there carry buttons, and a click that lands anywhere else
                // opening a window would fight them.
                //
                // The menu button is carved out of that: it sits inside the row, so a press on it
                // would otherwise open the profile *and* the menu at once.
                if (!onMenu && ImGui.IsMouseClicked(ImGuiMouseButton.Left)
                    && IsRowClicked(start, timeRight))
                    clickedProfile = entry.Identity;
            }, contextMenu: () => DrawPlayerMenuItems(entry.Identity),
               height: RecentRowHeight());
        }

        /// <summary>
        /// The right-click menu on a player row.
        ///
        /// Item order matches the profile card's link row (FFLogs, Tomestone, Lodestone) rather
        /// than being re-ordered here - the same three destinations in two different orders would
        /// be a small, constant papercut.
        ///
        /// Lodestone is last and deliberately kept even though it is only a search: it is the one
        /// link that cannot be wrong, so it is the fallback when a guessed path misses.
        /// </summary>
        /// <param name="dutyName">The fight the row is being shown against, when there is one.
        /// Only the party list has one: everywhere else this menu appears - Recent players,
        /// search results - the person is long gone from whatever they were doing, and the
        /// progress item simply isn't there.</param>
        /// <param name="kick">The party member this row stands for, when kicking them is something
        /// that can happen from here. Null everywhere else, which is most places.</param>
        private void DrawPlayerMenuItems(CharacterIdentity who, string? dutyName = null,
            uint dutyRowId = 0, PartyMemberInfo? kick = null,
            PartyMemberInfo? member = null, int partySlot = 0)
        {
            if (ImGui.Selectable("  View profile"))
                OpenProfile(who);

            if (CanFetchProgressFor(who, dutyName, dutyRowId))
            {
                ImGui.Separator();

                bool waiting = Ratings?.PlayerProgressPending(who) ?? false;

                // The server's own per-character cooldown, which it applies whether or not the
                // menu knows about it. Offering the press anyway would take it, drop it, and look
                // exactly like a broken menu item.
                TimeSpan cooling = Ratings?.PlayerRefreshWait(who) ?? TimeSpan.Zero;
                bool blocked = waiting || cooling > TimeSpan.Zero;

                // Disabled rather than omitted, unusually for this menu: an item vanishing is
                // exactly what a press looks like from here, and a word in its place is the only
                // thing that says which of the two happened.
                ImGui.BeginDisabled(blocked);

                // The duty name isn't ours - "The Epic of Alexander (Ultimate)" is wider than any
                // menu should be, so it is fitted rather than left to stretch one.
                string what = waiting
                    ? "Queued for update"
                    : cooling > TimeSpan.Zero
                        ? $"Checked recently - again in {ShortWait(cooling)}"
                        : $"Update progress for {Fit(dutyName!, 220f)}";

                if (ImGui.Selectable($"  {what}") && !blocked)
                    RequestOneProgress(who, dutyName);

                ImGui.EndDisabled();
            }

            ImGui.Separator();

            if (ImGui.Selectable("  Report"))
                OpenReportDialog(who);

            // Kick sits with Report because both are things you do *about* someone, and it is only
            // offered where it can actually happen - you lead the party, and it isn't instanced
            // content, where the game refuses it outright.
            if (kick != null)
            {
                var target = kick.Value;
                if (ImGui.Selectable("  Kick from party"))
                {
                    // The real name goes to the game, which is how it finds the member; only the
                    // question put to you is abbreviated.
                    AskConfirm("Kick player", $"Kick {DisplayName(target.Name)} from the party?",
                        "Yes, kick them",
                        () => Party?.Kick(target.Name, target.ContentId),
                        detail: "They won't be told it was you.");
                }
            }

            DrawBlacklistMenuItem(who, member, partySlot);

            ImGui.Separator();

            // Omitted rather than disabled when FFLogs has no region for their world: a greyed-out
            // item invites a hover looking for an explanation that isn't worth writing.
            string? region = Worlds?.GetFfLogsRegion(who.World);
            if (region != null && ImGui.Selectable("  FFLogs"))
                Dalamud.Utility.Util.OpenLink(CharacterLinks.FfLogs(who.Name, who.World, region));
            if (ImGui.Selectable("  Tomestone"))
                Dalamud.Utility.Util.OpenLink(CharacterLinks.Tomestone(who.Name, who.World));
            if (ImGui.Selectable("  Lodestone"))
                Dalamud.Utility.Util.OpenLink(CharacterLinks.LodestoneSearch(who.Name, who.World));
        }

        /// <summary>
        /// The game's blacklist, not one of ours.
        ///
        /// A list kept inside the plugin blocks nothing: it cannot stop a tell, a party invite or a
        /// shout, and it vanishes with the plugin. The game's blacklist does all of that and
        /// outlives us, so this drives that and keeps no list of its own.
        ///
        /// It can only be offered for someone in the party. Inside a duty that goes through the
        /// game's command, which takes their party slot; outside one the party is usually
        /// cross-world, where no placeholder resolves, and it is driven through the Contacts window
        /// instead - the same right-click a person would use by hand.
        ///
        /// A player you are merely looking at in Recent players is in neither, so nothing can be
        /// pointed at them. Rather than pretend, that case opens the blacklist window to finish by
        /// hand.
        /// </summary>
        /// <param name="member">The party member this row stands for, when it is one.</param>
        /// <param name="partySlot">Their 1-based slot in the party list.</param>
        private void DrawBlacklistMenuItem(CharacterIdentity who, PartyMemberInfo? member, int partySlot)
        {
            // Read from the game every time rather than remembered: the list can be changed in the
            // blacklist window, on another character or on another PC, and a cached answer would
            // start lying the moment it was.
            bool blocked = member != null && pfAutomation.IsBlacklisted(member.Value.ContentId);

            if (blocked)
            {
                ImGui.BeginDisabled();
                ImGui.Selectable("  Blacklisted");
                ImGui.EndDisabled();

                if (ImGui.IsItemHovered())
                    PaddedTooltip($"{DisplayName(who.Name)} is on your game blacklist.\n\n"
                        + "Removing someone is done from the game's own blacklist window.");
                return;
            }

            if (member == null || partySlot < 1 || partySlot > 8)
            {
                if (ImGui.Selectable("  Open blacklist"))
                    pfAutomation.OpenGameBlacklist();

                if (ImGui.IsItemHovered())
                    PaddedTooltip("The game can only blacklist someone in your party or under "
                        + "your cursor, so this opens its blacklist window instead.");
                return;
            }

            if (!ImGui.Selectable("  Blacklist"))
                return;

            int slot = partySlot;
            string realName = who.Name;
            ulong id = member.Value.ContentId;
            AskConfirm("Blacklist player", $"Blacklist {DisplayName(who.Name)}?",
                "Yes, blacklist",
                () => RunBlacklist(slot, realName, id),
                detail: "Uses the game's own blacklist, which blocks their tells, invites and "
                    + "chat. It blacklists the account rather than the character, so it covers "
                    + "their alts too. Undo it from the game's blacklist window.");
        }

        /// <summary>
        /// Runs the blacklist and reports what the game was actually able to do.
        ///
        /// Worth reporting because it genuinely differs: in a duty the member's slot names them
        /// outright and it is done before the menu closes, while outside one it is a walk through
        /// the Contacts window that takes a couple of seconds and can still fail at the end.
        /// Saying nothing in either case would look like the button did nothing.
        /// </summary>
        private void RunBlacklist(int partySlot, string name, ulong contentId)
        {
            var outcome = pfAutomation.BlacklistPlayer(partySlot, name, contentId);

            if (outcome == BlacklistAttempt.RunningViaContacts)
            {
                // The Contacts window is about to open and click through itself, which is alarming
                // to watch without a word of warning. The result is printed to chat by the
                // automation once the game has actually answered.
                ratingStatusMessage = $"Blacklisting {DisplayName(name)} through Contacts...";
                ratingStatusExpiresUtc = DateTime.UtcNow.AddSeconds(10);
            }
            else if (outcome == BlacklistAttempt.Unreachable)
            {
                pfAutomation.OpenGameBlacklist();
                ImGui.SetClipboardText(name);
                ratingStatusMessage = $"{DisplayName(name)} isn't nearby - name copied, "
                    + "add them in the blacklist window.";
                ratingStatusExpiresUtc = DateTime.UtcNow.AddSeconds(10);
            }
        }

        /// <summary>Shown until the feature is switched on. It states plainly what turning it on
        /// starts doing, because the feature involves people who never installed this plugin.</summary>
        private void DrawRatingsDisabledNotice()
        {
            ImGui.Dummy(new Vector2(0, 8));
            ImGui.Indent(8);
            ImGui.PushTextWrapPos(ImGui.GetWindowWidth() - 24);

            ImGui.TextColored(TextPrimary, "Community ratings are off.");
            ImGui.Dummy(new Vector2(0, 6));
            ImGui.TextColored(TextSecondary,
                "Look up other players' community ratings, and rate the people you finish duties "
                + "with.\n\n"
                + "While it's on, the plugin keeps a local list of who you met in duties, and "
                + "sends a name and world to the rating server when you search or rate. Ratings are "
                + "stored anonymously - the server keeps no record linking one back to you.");

            ImGui.Dummy(new Vector2(0, 10));
            ImGui.PopTextWrapPos();

            if (DrawPrimaryButton("Turn on community ratings", new Vector2(260, ButtonHeight)))
            {
                config.RatingsEnabled = true;
                config.Save();
            }

            ImGui.Unindent(8);
        }

        private void DrawRatingStatusLine()
        {
            if (string.IsNullOrEmpty(ratingStatusMessage) || DateTime.UtcNow > ratingStatusExpiresUtc)
                return;

            ImGui.Dummy(new Vector2(0, 4));
            ImGui.Indent(8);
            ImGui.PushTextWrapPos(ImGui.GetWindowWidth() - 24);
            ImGui.TextColored(AccentYellow, ratingStatusMessage);
            ImGui.PopTextWrapPos();
            ImGui.Unindent(8);
        }

        private void DrawRatingSearchBar()
        {
            const float barHeight = 52f;

            Vector2 origin = ImGui.GetCursorScreenPos();
            float width = ImGui.GetWindowWidth();
            float winX = ImGui.GetWindowPos().X;

            float controlY = origin.Y + (barHeight - ButtonHeight) * 0.5f;
            ImGui.SetCursorScreenPos(new Vector2(winX + 12f, controlY));

            // The same field the Recruit toolbar uses, and for the same reason it exists as a
            // shared widget at all: two searches that look different are two searches to learn.
            if (DrawSearchFieldSubmit("SearchPlayer", "Search someone you've met, or Name@World",
                    ref ratingSearchInput, width - 24f, ButtonHeight))
            {
                RunPlayerSearch();
            }

            RefreshSearchSuggestions();

            ImGui.SetCursorScreenPos(new Vector2(winX, origin.Y + barHeight));
            DrawRuleStrong();
            ImGui.SetCursorScreenPos(new Vector2(winX + 8f, origin.Y + barHeight + 8f));
        }
        private void RefreshSearchSuggestions()
        {
            string input = ratingSearchInput.Trim();
            if (input == ratingSearchSuggestFor)
                return;

            ratingSearchSuggestFor = input;
            ratingSearchSuggestions.Clear();

            // Typing again after a failed lookup should not leave the old complaint on screen.
            ratingSearchError = string.Empty;

            // Once a world has been given the input is a full address, not a partial name; let it
            // through to the Name@World path instead of second-guessing it.
            int at = input.IndexOf('@');
            string namePart = (at >= 0 ? input.Substring(0, at) : input).Trim();
            if (at >= 0 || namePart.Length < 2)
                return;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var who in KnownCharacters())
            {
                if (who.Name.IndexOf(namePart, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                if (!seen.Add(who.Key))
                    continue;

                ratingSearchSuggestions.Add(who);
                if (ratingSearchSuggestions.Count >= MaxSearchSuggestions)
                    return;
            }
        }

        /// <summary>
        /// Everyone this install can name, most useful first.
        ///
        /// People from the last few days lead, because someone you are looking up is usually
        /// someone you have just played with. Behind them is the whole permanent history - every
        /// character ever met, however long ago - which is what makes the search box able to answer
        /// "who was that healer from months back" instead of only knowing this week.
        ///
        /// Duplicates across the three sources are fine; the caller de-duplicates by key and the
        /// order here decides which copy wins.
        /// </summary>
        private IEnumerable<CharacterIdentity> KnownCharacters()
        {
            var contacts = Encounters?.RecentContacts();
            if (contacts != null)
            {
                foreach (var contact in contacts)
                {
                    if (contact.Member.IsValid)
                        yield return contact.Member.Identity;
                }
            }

            var rated = History?.Recent();
            if (rated != null)
            {
                foreach (var entry in rated)
                    yield return entry.Identity;
            }

            var everyone = Players?.All();
            if (everyone != null)
            {
                foreach (var entry in everyone)
                    yield return entry.Identity;
            }
        }

        /// <summary>The matches, as ordinary player rows - so picking one is the same gesture as
        /// picking anyone else, right-click menu included.</summary>
        private void DrawSearchSuggestions()
        {
            ImGui.Dummy(new Vector2(0, 6));
            ImGui.Indent(8);
            DrawSectionLabel("PLAYERS YOU'VE MET");
            ImGui.Unindent(8);
            ImGui.Dummy(new Vector2(0, 2));

            // The click is recorded here and acted on after the loop.
            //
            // Selecting a result clears this very list, and doing that from inside the row's own
            // draw callback mutates the collection being enumerated - which threw
            // InvalidOperationException and took the whole plugin down with it. Nothing drawn
            // inside this loop may touch ratingSearchSuggestions.
            CharacterIdentity? picked = null;

            foreach (var who in ratingSearchSuggestions)
            {
                var target = who;

                // The job icon, from whatever is already known - the permanent list first, then
                // anything a lookup has since put in memory. Never worth a fetch of its own: a
                // suggestion list is a thing you type past in a second, and firing a network call
                // per keystroke per name to decorate rows nobody will click is the wrong trade.
                // A row with no icon is a row whose job we don't know yet, which is honest.
                uint suggestJob = Players?.JobFor(target) ?? 0;
                if (suggestJob == 0)
                    suggestJob = Ratings?.CharacterFor(target)?.JobId ?? 0;

                DrawHoverRow($"suggest{target.Key}", rightEdge =>
                {
                    Vector2 start = ImGui.GetCursorScreenPos();
                    DrawRowIdentity(suggestJob, target.Name, target.World, start.X, rightEdge);

                    if (ImGui.IsMouseClicked(ImGuiMouseButton.Left) && IsRowClicked(start, rightEdge))
                        picked = target;
                }, contextMenu: () => DrawPlayerMenuItems(target));
            }

            if (picked != null)
                SelectSearchResult(picked);
        }

        /// <summary>
        /// Whether the click landed on this row's strip. The rows are drawn rects rather than ImGui
        /// items, so there is no IsItemClicked to lean on.
        ///
        /// The window check is not optional, and leaving it out was a real bug: a bare mouse-position
        /// test knows nothing about what is drawn *over* the row, so picking an item out of an open
        /// context menu also counted as a click on whichever recent player happened to be behind it.
        /// One click, two actions, and the menu's own choice was the one that lost. IsWindowHovered
        /// is false while a popup covers this window, which is exactly the question being asked.
        /// </summary>
        private static bool IsRowClicked(Vector2 start, float rightEdge)
        {
            if (!ImGui.IsWindowHovered(ImGuiHoveredFlags.AllowWhenBlockedByActiveItem))
                return false;

            Vector2 m = ImGui.GetMousePos();
            return m.X >= start.X && m.X <= rightEdge
                && m.Y >= start.Y - 4f && m.Y <= start.Y + 26f;
        }

        private void SelectSearchResult(CharacterIdentity who)
        {
            ratingSearchTarget = who;
            ratingSearchInput = $"{who.Name}@{who.World}";
            ratingSearchSuggestFor = ratingSearchInput;
            ratingSearchSuggestions.Clear();
            ratingSearchError = string.Empty;
        }

        /// <summary>
        /// Parses "Name@World" and validates the world before anything goes to the network.
        /// Catching a typo here is better than sending it: a bad world would come back with no
        /// ratings, which reads as "unrated" rather than "you misspelled it".
        ///
        /// A bare name is accepted too, and resolved against people you've met. That only works
        /// when it picks out exactly one of them - two Kats on different worlds are two different
        /// people, and guessing which one was meant is the one thing this must not do.
        /// </summary>
        private void RunPlayerSearch()
        {
            ratingSearchError = string.Empty;
            ratingSearchTarget = null;

            string input = ratingSearchInput.Trim();
            if (string.IsNullOrWhiteSpace(input))
                return;

            int at = input.LastIndexOf('@');
            if (at < 0)
            {
                ResolveBareName(input);
                return;
            }

            if (at == 0 || at == input.Length - 1)
            {
                ratingSearchError = "Enter the character as Name@World, e.g. John Smith@Zodiark.";
                return;
            }

            string name = input.Substring(0, at).Trim();
            string world = input.Substring(at + 1).Trim();

            if (name.Length < 2)
            {
                ratingSearchError = "That name is too short.";
                return;
            }

            if (Worlds != null && !Worlds.IsKnownWorld(world))
            {
                ratingSearchError = $"\"{world}\" isn't a world I recognise.";
                return;
            }

            ratingSearchTarget = new CharacterIdentity(name, world);
        }

        /// <summary>
        /// Resolves a name with no world against people this install has met.
        ///
        /// Deliberately refuses to guess when several people match: it names the worlds instead and
        /// lets the search bar's own suggestion list do the picking.
        /// </summary>
        private void ResolveBareName(string name)
        {
            if (name.Length < 2)
            {
                ratingSearchError = "That name is too short.";
                return;
            }

            var matches = new List<CharacterIdentity>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var who in KnownCharacters())
            {
                if (!string.Equals(who.Name, name, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (seen.Add(who.Key))
                    matches.Add(who);
            }

            if (matches.Count == 1)
            {
                SelectSearchResult(matches[0]);
                return;
            }

            if (matches.Count > 1)
            {
                ratingSearchError =
                    $"You've met more than one \"{name}\" - pick a world: "
                    + string.Join(", ", matches.ConvertAll(m => m.World)) + ".";
                return;
            }

            ratingSearchError =
                $"No one you've met is called \"{name}\". For anyone else, give the world too, "
                + "as Name@World.";
        }

        // ══════════════════════════════════════════════════════════
        //  PROFILE CARD
        // ══════════════════════════════════════════════════════════

        /// <summary>A single up/down proportion bar. Two numbers don't need a histogram.</summary>
        private void DrawVoteBar(PlayerRating rating)
        {
            int total = rating.Upvotes + rating.Downvotes;
            if (total == 0)
                return;

            var dl = ImGui.GetWindowDrawList();
            Vector2 pos = ImGui.GetCursorScreenPos();
            float fullWidth = ImGui.GetContentRegionAvail().X;

            // 7, not 10. At ten it read as a filled panel rather than a proportion, and on a
            // unanimous player it became a solid slab of colour across the whole card.
            const float height = 7f;

            float upWidth = fullWidth * ((float)rating.Upvotes / total);

            dl.AddRectFilled(pos, new Vector2(pos.X + fullWidth, pos.Y + height),
                ImGui.ColorConvertFloat4ToU32(ColorFromHex("#e06a5a")), 4f);

            if (rating.Upvotes > 0)
            {
                dl.AddRectFilled(pos, new Vector2(pos.X + upWidth, pos.Y + height),
                    ImGui.ColorConvertFloat4ToU32(ColorFromHex("#3fb56a")), 4f,
                    rating.Downvotes == 0 ? ImDrawFlags.RoundCornersAll : ImDrawFlags.RoundCornersLeft);
            }

            ImGui.Dummy(new Vector2(fullWidth, height));
        }
    }
}
#endif
