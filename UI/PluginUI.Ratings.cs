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
        Achievements = 3,

        // Identities for tabs contributed by optional components. Nothing in this repository adds
        // them to the list or draws them; they exist so that a component which does has a stable
        // value to key on rather than inventing one.
        ExtraOne = 100,
        ExtraTwo = 101,
    }

    /// <summary>
    /// The Ratings tab: the people you can still vote on, and a lookup for anyone else.
    ///
    /// Voting lives here and in the post-duty prompt, nowhere else. Contacts is a record of who you
    /// met; the lookup can show you anyone's score but can rate nobody, because you may only vote
    /// on someone you actually finished a duty with in the last hour.
    /// </summary>
    public partial class PluginUI
    {
        private MainTab activeTab = MainTab.Presets;

        private string ratingSearchInput = string.Empty;
        private CharacterIdentity? ratingSearchTarget;
        private string ratingSearchError = string.Empty;

        /// <summary>
        /// The tab Back returns to, when the profile was opened from somewhere else.
        ///
        /// Looking someone up from the party list on Recruit used to leave you on My Profile with
        /// their card and no way back but the tab strip - Back only cleared the search, which
        /// answers "what am I looking at" and not "where was I". Null means the profile was opened
        /// from this tab, where Back is already a move within it.
        /// </summary>
        private MainTab? profileReturnTab;

        /// <summary>Set when a card is opened, so the narrow layout starts at the top of it rather
        /// than wherever the list underneath was scrolled to.</summary>
        private bool profileScrollPending;

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

            // One list, shared with the rail - see TabList. It used to be built again here, and
            // the two copies drifted: a tab added to one simply did not exist in the other, which
            // is exactly the layout depending on how wide the window happened to be.
            var tabs = TabList().ToArray();

            const float stripPad = 8f;
            float room = width - stripPad * 2f;

            // ── How much of each tab actually fits ──
            //
            // The strip used to divide the width into equal segments and centre an icon and a
            // label in each, measuring neither. At three tabs on a wide window that looked fine.
            // At four - and at six with the moderator build's - every label ran straight through
            // its neighbour, because "Achievements" is wider than a hundred-pixel segment and
            // nothing was checking.
            //
            // So measure first, then choose how much to show. Labels for everything if they fit;
            // otherwise a label on the tab you are actually on and icons for the rest, which is
            // what a phone does and for the same reason; and icons alone when even that is too
            // much. The hit areas are always the full cell, whichever tier is drawn.
            var mode = FitNavLabels(tabs, room);

            float[] widths = new float[tabs.Length];
            float total = 0f;

            for (int i = 0; i < tabs.Length; i++)
            {
                bool labelled = mode == NavFit.AllLabels
                    || (mode == NavFit.ActiveLabelOnly && activeTab == tabs[i].Item3);

                widths[i] = NavTabWidth(tabs[i].Item1, tabs[i].Item3, labelled);
                total += widths[i];
            }

            // Whatever is left over is shared out between the tabs rather than added to the last
            // one, so the row stays evenly spaced instead of bunching at the left.
            float slack = Math.Max(0f, room - total) / tabs.Length;

            var dl = ImGui.GetWindowDrawList();

            float baselineY = start.Y + NavStripHeight - 2f;
            dl.AddLine(new Vector2(winX, baselineY), new Vector2(winX + width, baselineY),
                ImGui.ColorConvertFloat4ToU32(BorderDefault), 1.0f);

            if (ChromeDiagnosticRequested)
                ReportChromeDiagnostic($"nav strip: {tabs.Length} tabs, mode={mode}, "
                    + $"needed={total:F0} of {room:F0} at y={start.Y:F0}, width={width:F0}");

            float x = winX + stripPad;

            for (int i = 0; i < tabs.Length; i++)
            {
                var (label, icon, tab) = tabs[i];

                bool labelled = mode == NavFit.AllLabels
                    || (mode == NavFit.ActiveLabelOnly && activeTab == tab);

                float cell = widths[i] + slack;

                DrawNavTab(dl, label, icon, tab,
                    new Vector2(x, start.Y), new Vector2(cell, NavStripHeight - 2f),
                    baselineY, labelled);

                x += cell;
            }

            ImGui.SetCursorScreenPos(new Vector2(winX + stripPad, start.Y + NavStripHeight + 2));
        }

        /// <summary>How much of a tab the strip has room to show.</summary>
        private enum NavFit
        {
            AllLabels,
            ActiveLabelOnly,
            IconsOnly,
        }

        private NavFit FitNavLabels((string, FontAwesomeIcon, MainTab)[] tabs, float room)
        {
            float all = 0f;
            float activeOnly = 0f;
            float icons = 0f;

            foreach (var (label, _, tab) in tabs)
            {
                all += NavTabWidth(label, tab, withLabel: true);
                activeOnly += NavTabWidth(label, tab, withLabel: activeTab == tab);
                icons += NavTabWidth(label, tab, withLabel: false);
            }

            if (all <= room) return NavFit.AllLabels;
            if (activeOnly <= room) return NavFit.ActiveLabelOnly;
            return NavFit.IconsOnly;
        }

        /// <summary>What one tab needs, including the breathing room either side of it.</summary>
        private float NavTabWidth(string label, MainTab tab, bool withLabel)
        {
            const float pad = 12f;
            const float gap = 8f;

            float glyph;
            using (pluginInterface.UiBuilder.IconFontHandle.Push())
                glyph = ImGui.CalcTextSize(FontAwesomeIcon.Star.ToIconString()).X;

            if (!withLabel)
                return glyph + pad * 2f;

            float text = ImGui.CalcTextSize(label).X;
            float beta = tab == MainTab.Achievements ? BetaChipWidth() + 6f : 0f;

            return glyph + gap + text + beta + pad * 2f;
        }

        /// <summary>
        /// The word beta, as a chip rather than part of the label.
        ///
        /// It was "Achievements (beta)" for a day, which made the longest label in the strip forty
        /// per cent longer than it needed to be and pushed the whole row into its icons-only tier
        /// on any window narrow enough to have a strip at all. The word is a status, not a name.
        /// </summary>
        private float BetaChipWidth()
        {
            using (UiLabelFont.Push())
                return ImGui.CalcTextSize("BETA").X + 8f;
        }

        private void DrawBetaChip(ImDrawListPtr dl, Vector2 pos, float height, float alpha)
        {
            using (UiLabelFont.Push())
            {
                Vector2 size = ImGui.CalcTextSize("BETA");
                var min = new Vector2(pos.X, pos.Y + (height - size.Y - 4f) * 0.5f);
                var max = new Vector2(min.X + size.X + 8f, min.Y + size.Y + 4f);

                dl.AddRect(min, max,
                    ImGui.ColorConvertFloat4ToU32(BorderControl with { W = alpha }), 0f, 0, 1f);
                dl.AddText(new Vector2(min.X + 4f, min.Y + 2f),
                    ImGui.ColorConvertFloat4ToU32(TextMuted with { W = alpha }), "BETA");
            }
        }

        private void DrawNavTab(ImDrawListPtr dl, string label, FontAwesomeIcon icon, MainTab tab,
            Vector2 pos, Vector2 size, float baselineY, bool withLabel)
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
            {
                // Choosing a tab by hand is its own navigation: whatever Back was remembering is
                // no longer where you came from.
                activeTab = tab;
                profileReturnTab = null;
            }

            bool hovered = ImGui.IsItemHovered();

            ImGui.PopStyleVar(2);
            ImGui.PopStyleColor(3);

            var color = active ? TextPrimary : hovered ? TextSecondary : TextMuted;

            if (withLabel)
            {
                float betaRoom = tab == MainTab.Achievements ? BetaChipWidth() + 6f : 0f;

                DrawIconLabelCentered(icon, label,
                    pos, new Vector2(size.X - betaRoom, size.Y), color);

                if (betaRoom > 0f)
                {
                    // Placed against the label's own end rather than the cell's, so it travels with
                    // the word instead of drifting off toward the next tab on a wide window.
                    float glyph;
                    using (pluginInterface.UiBuilder.IconFontHandle.Push())
                        glyph = ImGui.CalcTextSize(icon.ToIconString()).X;

                    float content = glyph + 8f + ImGui.CalcTextSize(label).X;
                    float startX = pos.X + (size.X - betaRoom - content) * 0.5f;

                    DrawBetaChip(dl, new Vector2(startX + content + 6f, pos.Y), size.Y,
                        active ? 1f : 0.7f);
                }
            }
            else
            {
                DrawIconCentered(icon, pos, size, color);

                if (hovered)
                    PaddedTooltip(tab == MainTab.Achievements ? $"{label} (beta)" : label);
            }

            if (!active)
                return;

            // The lit underline, matched to what is actually drawn rather than to a fixed inset -
            // a 14px inset on an icons-only cell left an underline wider than the icon above it.
            float half = Math.Min(size.X * 0.5f - 6f, (withLabel ? size.X * 0.5f - 14f : 14f));
            float mid = pos.X + size.X * 0.5f;

            dl.AddRectFilled(
                new Vector2(mid - half, baselineY - 1f),
                new Vector2(mid + half, baselineY + 1f),
                ImGui.ColorConvertFloat4ToU32(AccentBlue), 1f);
        }

        /// <summary>An icon on its own, centred in a cell.</summary>
        private void DrawIconCentered(FontAwesomeIcon icon, Vector2 rectMin, Vector2 rectSize,
            Vector4 color)
        {
            var dl = ImGui.GetWindowDrawList();

            using (pluginInterface.UiBuilder.IconFontHandle.Push())
            {
                string glyph = icon.ToIconString();
                Vector2 size = ImGui.CalcTextSize(glyph);

                dl.AddText(new Vector2(rectMin.X + (rectSize.X - size.X) * 0.5f,
                                       rectMin.Y + (rectSize.Y - size.Y) * 0.5f),
                    ImGui.ColorConvertFloat4ToU32(color), glyph);
            }
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
            if (!config.CommunityEnabled)
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
                    //
                    // While somebody is looked up, the card is the whole tab. Stacked, the lists
                    // stayed on screen underneath a card that had opened above the fold, so looking
                    // someone up from a list looked like it had done nothing at all - the same
                    // people were still sitting there. Side by side there is no such problem, which
                    // is why the wide layout keeps both.
                    // Typing is the exception: suggestions are the answer to what is being typed
                    // right now, and they belong on screen whoever is on the card.
                    bool lookedUp = ratingSearchTarget != null && ratingSearchSuggestions.Count == 0;
                    if (lookedUp && profileScrollPending)
                    {
                        ImGui.SetScrollY(0f);
                        profileScrollPending = false;
                    }

                    DrawProfilePane(compact: true);

                    if (!lookedUp)
                    {
                        ImGui.Dummy(new Vector2(0, 8));
                        DrawRatingLists();
                    }

                    return;
                }

                // Wide enough for both columns: nothing was ever hidden here, so nothing needs
                // scrolling back either.
                profileScrollPending = false;

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
            recentRatingBatch.Clear();

            foreach (var entry in recent)
                DrawRecentPlayerRow(entry);

            // One request for the rows the reader can actually see, after they have all been
            // drawn. Prefetch skips anything already fresh, so a list held still costs nothing
            // after its first pass, and the service coalesces what is left into a single batched
            // call rather than one per name.
            if (recentRatingBatch.Count > 0)
                Ratings?.Prefetch(recentRatingBatch);

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

            // Where to put you back. Only recorded when the profile is opened from another tab:
            // opening one card from another card's list should not queue up a chain of Backs.
            if (activeTab != MainTab.Ratings)
                profileReturnTab = activeTab;

            activeTab = MainTab.Ratings;
            SelectSearchResult(who);
        }

        /// <summary>
        /// Back: leaves the looked-up card, and puts you back where you opened it from.
        ///
        /// Clearing the search is part of leaving, not a separate act - the field held the name of
        /// somebody you are no longer looking at, and coming back to My Profile later to find a
        /// stale search still in it is the same confusion one step delayed.
        /// </summary>
        private void CloseProfile()
        {
            ratingSearchTarget = null;
            ratingSearchInput = string.Empty;
            ratingSearchSuggestions.Clear();
            ratingSearchError = string.Empty;

            if (profileReturnTab is { } back)
                activeTab = back;
            profileReturnTab = null;
        }

        /// <summary>
        /// The height of a row in the "everyone you have met" list.
        ///
        /// A vote row's height plus room to breathe. The two lists are one column and the earlier
        /// players are most of it, so they get the size of a list you are meant to read rather than
        /// the size of a footnote.
        /// </summary>
        private static float RecentRowHeight() => RateRowHeight(withSubline: false) + 8f;

        /// <summary>
        /// The rows on screen this frame, gathered for one batched lookup after the list is drawn.
        ///
        /// A field rather than a local so the list itself is reused: this runs every frame the tab
        /// is open, and a fresh allocation per frame for a column of numbers is not a trade worth
        /// making.
        /// </summary>
        private readonly List<CharacterIdentity> recentRatingBatch = new();

        /// <summary>
        /// Ceiling on how many players one frame will ask about, however tall the window is.
        ///
        /// Comfortably more than fits on screen at any sane size, so in practice it never binds -
        /// it is here so a bug in the visibility test, or some future layout that draws this list
        /// without a clip rect, cannot turn into a request for four hundred names.
        /// </summary>
        private const int RecentRatingBatchMax = 40;

        private void DrawRecentPlayerRow(PlayerSeen entry)
        {
            // Whether this row is inside the scroll view rather than above or below it. The list is
            // everyone this install has ever met - hundreds of people on an old one - and the
            // reader is looking at maybe fifteen of them. Asking about the rest would make the size
            // of the request the size of the history file.
            //
            // Tested before the row is drawn, while the cursor is still at its top-left, which is
            // what IsRectVisible measures from.
            bool onScreen = ImGui.IsRectVisible(new Vector2(1f, RecentRowHeight()));
            if (onScreen && recentRatingBatch.Count < RecentRatingBatchMax)
                recentRatingBatch.Add(entry.Identity);

            // Read without asking: whatever is already in memory or on disk. The ask for the
            // visible rows is queued above and its answer lands on a later frame.
            var score = Ratings?.Peek(entry.Identity);

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

                // The community score's column, and the last thing on the row before the time.
                //
                // Your own vote used to have a caret of its own immediately right of this one, and
                // the pair did not survive contact with real data. Both are carets, both take their
                // green from the same #4ea36b - AccentGreen is literally defined as Positive - and
                // they sat six pixels apart, so the only thing telling them apart was that one had
                // a number after it and one did not. Nobody reads a difference that fine, and while
                // the score column was blank on most rows the collision stayed invisible; as votes
                // accumulated and the scores filled in, every rated row grew a second green arrow.
                //
                // What everyone thinks is the fact worth scanning down a list. What you did about
                // them is one person's opinion you already know, so it moves to this column's
                // tooltip and stays on their profile card.
                const float scoreColumn = 46f;
                const float scoreGap = 16f;

                float timeRight = rightEdge - menuColumn;
                float scoreLeft = timeRight - timeColumn - scoreGap - scoreColumn;

                DrawRowIdentity(entry.JobId, entry.Name, entry.World, start.X, scoreLeft - 8f);

                ImGui.SameLine();
                ImGui.SetCursorScreenPos(new Vector2(scoreLeft, start.Y));
                DrawScoreColumn(entry.Identity, score, scoreColumn, onScreen, rated);

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
        /// The community's weighted score, right-aligned in a fixed column.
        ///
        /// Takes the rating it was handed rather than fetching one, which is the whole difference
        /// between this and <see cref="DrawRatingChip"/>: that one reads and requests together,
        /// which is right for a party of eight and would make this list's length its request size.
        ///
        /// Blank, not zero and not a dash, for the many people nobody has voted on. A column of
        /// dashes down a list of forty looks like data and is not, and a "0" is a real score that
        /// somebody could have earned.
        /// </summary>
        /// <param name="rated">This install's own vote on them, when there is one. It has no mark
        /// of its own on the row any more, so it rides along in this column's tooltip.</param>
        private void DrawScoreColumn(CharacterIdentity who, PlayerRating? rating, float column,
            bool onScreen, RatingGiven? rated = null)
        {
            ImGui.AlignTextToFramePadding();

            // A hidden player draws nothing here - the same blank as somebody nobody has rated. The
            // column still reserves its width, so the rows above and below stay aligned and the gap
            // says nothing.
            if (IsHidden(rating))
            {
                ImGui.Dummy(new Vector2(column, 0));
                return;
            }

            if (rating is { Gated: false, OptedOut: false } && rating.Count > 0)
            {
                bool up = rating.Score >= 0;
                int shown = Math.Abs(rating.Score);

                float used = ArrowCountWidth(shown);
                if (used < column)
                {
                    ImGui.Dummy(new Vector2(column - used, 0));
                    ImGui.SameLine(0, 0);
                }

                DrawArrowCount(up ? FontAwesomeIcon.CaretUp : FontAwesomeIcon.CaretDown, shown,
                    NetScoreColor(rating.Score));

                if (ImGui.IsItemHovered())
                {
                    PaddedTooltip($"{who}\n\n"
                        + $"Weighted score {rating.Score}\n"
                        + $"{rating.Upvotes} up, {rating.Downvotes} down, {rating.Count} votes"
                        + OwnVoteLine(rated));
                }
                return;
            }

            // The dots are worth the space only while an answer is actually coming. Off screen
            // nothing was ever asked, so a row scrolled past with no score is settled rather than
            // pending, and drawing "···" on it would promise something that isn't on its way.
            bool loading = rating == null && onScreen && Ratings?.IsLoading(who) == true;
            if (!loading)
            {
                ImGui.Dummy(new Vector2(column, 0));
                return;
            }

            const string dots = "···";
            float tw = ImGui.CalcTextSize(dots).X;
            if (tw < column)
            {
                ImGui.Dummy(new Vector2(column - tw, 0));
                ImGui.SameLine(0, 0);
            }
            ImGui.TextColored(TextMuted, dots);
        }

        /// <summary>
        /// The "and here is what you did about them" line appended to the score tooltip.
        ///
        /// Empty when this install never voted, rather than a line saying so. The tooltip is opened
        /// to read the score; "you have not rated them" on every unrated row is a sentence that
        /// never changes and never helps.
        /// </summary>
        private string OwnVoteLine(RatingGiven? rated)
        {
            if (rated == null)
                return string.Empty;

            if (rated.Direction == VoteDirection.Unknown)
                return "\n\nYou rated them, but this install no longer remembers which way.";

            bool up = rated.Direction == VoteDirection.Up;
            return $"\n\nYou rated them {(up ? "up" : "down")} {Ago(rated.RatedUtc)}.";
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
            uint dutyRowId = 0, PartyMemberInfo? kick = null)
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

            // Not on yourself. The server refuses a self-report outright, so the item could only
            // ever open a dialog, take a note, and fail - a control whose entire behaviour is to
            // waste somebody's time is worse than one that isn't there.
            if (!IsSelf(who) && ImGui.Selectable("  Report"))
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

            // No button while the stats slider is below Full - that setting opts you out, and a
            // button here would set a flag the other setting immediately overrules. The Settings
            // tab is where both of them live, so that is where this points.
            if (config.AnalyticsMode != AnalyticsMode.Full)
            {
                ImGui.TextColored(TextSecondary,
                    "Taking part needs \"Anonymous usage stats\" set to Full, under Settings.");
            }
            else if (DrawPrimaryButton("Turn on community ratings", new Vector2(260, ButtonHeight)))
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
            profileScrollPending = true;
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
            profileScrollPending = true;
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
