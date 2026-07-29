#if PFP_RATINGS
using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;

namespace PfPresets
{
    /// <summary>Which section of the main window is showing.</summary>
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

        private const float NavStripHeight = 36f;

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
                ("Presets", FontAwesomeIcon.ClipboardList, MainTab.Presets),
            };

            if (config.RatingsEnabled)
                tabList.Add(("Ratings", FontAwesomeIcon.Star, MainTab.Ratings));

            tabList.Add(("Settings", FontAwesomeIcon.Cog, MainTab.Settings));

            var tabs = tabList.ToArray();

            // Turning ratings off while looking at them would otherwise leave the window on a tab
            // that is no longer in the strip.
            if (activeTab == MainTab.Ratings && !config.RatingsEnabled)
                activeTab = MainTab.Presets;

            float segWidth = (width - 16f) / tabs.Length;
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

        private void DrawRatingsTab()
        {
            if (!config.RatingsEnabled)
            {
                DrawRatingsDisabledNotice();
                return;
            }

            DrawRatingSearchBar();

            ImGui.BeginChild("RatingsBody", new Vector2(ImGui.GetWindowWidth() - 16, 0), false);
            try
            {
                if (!string.IsNullOrEmpty(ratingSearchError))
                {
                    ImGui.Indent(8);
                    ImGui.TextColored(AccentYellow, ratingSearchError);
                    ImGui.Unindent(8);
                    return;
                }

                if (ratingSearchTarget != null)
                {
                    DrawProfileCard(ratingSearchTarget);
                    return;
                }

                // While a partial name is matching someone you've met, the matches take the body.
                // Showing the eligible list underneath them would put two lists of names on screen
                // at once, only one of which responds to what was just typed.
                if (ratingSearchSuggestions.Count > 0)
                {
                    DrawSearchSuggestions();
                    return;
                }

                DrawEligibleList();
            }
            finally
            {
                ImGui.EndChild();
            }
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

            if (DrawSecondaryButton("Skip all##SkipVotes", new Vector2(w, 22)))
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

        /// <summary>Space the pinned own-rating card needs, or 0 when nobody is logged in.</summary>
        private float OwnRatingHeight()
            => LocalIdentity?.Invoke() == null ? 0f : HoverRowHeight() + 14f;

        /// <summary>
        /// Your own card, pinned to the bottom of the tab.
        ///
        /// The whole feature is other people rating you, so seeing where you stand shouldn't mean
        /// typing your own name into the lookup. Same row shape as everyone else's, because it is
        /// the same information.
        /// </summary>
        private void DrawOwnRating()
        {
            var me = LocalIdentity?.Invoke();
            if (me == null)
                return;

            float width = ImGui.GetContentRegionAvail().X;

            // Drawn inside a fixed-height child of its own.
            //
            // Without one, the row's items extend the parent's content size by a few pixels more
            // than the space reserved for it - which makes the scrollbar appear, which narrows the
            // content, which changes the content size again. That oscillation is the wobble; a
            // child with an explicit height can't take part in it.
            if (!ImGui.BeginChild("OwnRatingFooter", new Vector2(width, OwnRatingHeight()), false,
                    ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
            {
                ImGui.EndChild();
                return;
            }

            try
            {
                var dl = ImGui.GetWindowDrawList();
                Vector2 origin = ImGui.GetCursorScreenPos();

                // A divider rather than a card: this is a footer, not another list item.
                dl.AddLine(new Vector2(origin.X, origin.Y + 2f),
                    new Vector2(origin.X + width, origin.Y + 2f),
                    ImGui.ColorConvertFloat4ToU32(BorderDefault), 1f);

                ImGui.SetCursorScreenPos(new Vector2(origin.X, origin.Y + 9f));

                DrawHoverRow("ownRating", rightEdge =>
                {
                    Vector2 start = ImGui.GetCursorScreenPos();

                    uint jobId = 0;
                    short level = 0;
                    try
                    {
                        var ps = pfAutomation.PlayerState;
                        jobId = ps.ClassJob.RowId;
                        level = ps.Level;
                    }
                    catch (Exception)
                    {
                        // Mid zone change; the row still renders, just without the job.
                    }

                    float iconSize = ImGui.GetTextLineHeight() + 4f;
                    DrawJobIconInline(jobId, iconSize);
                    ImGui.SameLine(0, 7);

                    string label = $"{me.Name}  @{me.World}";
                    ImGui.AlignTextToFramePadding();
                    ImGui.TextColored(TextPrimary,
                        Fit(label, Math.Max(40f, rightEdge - start.X - iconSize - 130f)));

                    if (level > 0)
                    {
                        ImGui.SameLine(0, 6);
                        ImGui.TextColored(TextMuted, $"Lv{level}");
                    }

                    ImGui.SameLine();
                    ImGui.SetCursorScreenPos(new Vector2(rightEdge - RatingChipWidth, start.Y));
                    DrawRatingChip(me);
                }, width: width - 12f, originX: origin.X);
            }
            finally
            {
                ImGui.EndChild();
            }
        }

        /// <summary>
        /// Everyone you can still rate. This is the tab's real content - the search box above it
        /// looks people up, it doesn't rate them.
        /// </summary>
        private void DrawEligibleList()
        {
            var eligible = Ratings?.EligibleToRate();

            ImGui.Dummy(new Vector2(0, 6));

            if (eligible == null || eligible.Count == 0)
            {
                // Nothing to vote on, so the tab leads with your own card - compact, sized to its
                // own contents, and sitting above the recent players exactly where the vote rows
                // would be. It is hidden entirely during voting: a card about you above a list of
                // people waiting to be rated competes with what you opened the tab to do.
                var me = LocalIdentity?.Invoke();
                if (me != null)
                {
                    DrawProfileCard(me, showBack: false, fixedHeight: CompactProfileHeight());
                    ImGui.Dummy(new Vector2(0, 8));
                    DrawRecentPlayers();
                    return;
                }

                ImGui.Indent(8);
                ImGui.PushTextWrapPos(ImGui.GetContentRegionMax().X - 8);
                ImGui.TextColored(TextMuted,
                    "Run a duty to rate the players you meet.");
                ImGui.PopTextWrapPos();
                ImGui.Unindent(8);
                ImGui.Dummy(new Vector2(0, 4));

                DrawRecentPlayers();
                return;
            }

            DrawSkipAll(eligible.Count);

            if (Ratings != null)
            {
                var identities = new List<CharacterIdentity>(eligible.Count);
                foreach (var c in eligible)
                    identities.Add(c.Identity);
                Ratings.Prefetch(identities);
            }

            // No heading. It's the top of the tab and every row has buttons on it, which says
            // everything a label would.
            foreach (var contact in eligible)
                DrawRateRow(contact);

            DrawRatingStatusLine();
            DrawRecentPlayers();
        }

        /// <summary>
        /// Everyone this install has rated, most recent first. This is what the Contacts tab used
        /// to be, and it is also the fix for rated players vanishing: the eligible list above drops
        /// anyone already rated, so without this there was no trace that the rating had worked.
        /// </summary>
        private void DrawRecentPlayers()
        {
            var recent = History?.Recent();
            if (recent == null || recent.Count == 0)
                return;

            ImGui.Dummy(new Vector2(0, 6));
            ImGui.Indent(8);
            DrawSectionLabel("RECENT PLAYERS");
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

        private void DrawRecentPlayerRow(RatingGiven entry)
        {
            DrawHoverRow($"recent{entry.Identity.Key}", rightEdge =>
            {
                Vector2 start = ImGui.GetCursorScreenPos();

                string ago = Ago(entry.RatedUtc);

                // Fixed columns, measured from the row's right edge.
                //
                // The block used to be sized from the time string, so "9h" and "3w ago" pushed
                // the arrow to different places and the list never lined up down the page. The
                // time now right-aligns inside a reserved slot and the arrow sits at a constant
                // offset, whatever either of them says.
                const float timeColumn = 52f;
                float arrowLeft = rightEdge - timeColumn - 22f;

                DrawRowIdentity(entry.JobId, entry.Name, entry.World, start.X, arrowLeft - 8f);

                ImGui.SameLine();
                ImGui.SetCursorScreenPos(new Vector2(arrowLeft, start.Y));

                if (entry.Direction == VoteDirection.Unknown)
                {
                    ImGui.AlignTextToFramePadding();
                    ImGui.TextColored(TextMuted, "\u00b7");
                    if (ImGui.IsItemHovered())
                        PaddedTooltip("Rated, but this install no longer remembers which way.");
                }
                else
                {
                    bool up = entry.Direction == VoteDirection.Up;
                    ImGui.PushFont(UiBuilder.IconFont);
                    ImGui.AlignTextToFramePadding();
                    ImGui.TextColored(up ? AccentGreen : AccentRed,
                        (up ? FontAwesomeIcon.CaretUp : FontAwesomeIcon.CaretDown).ToIconString());
                    ImGui.PopFont();
                }

                ImGui.SameLine();
                float agoW = ImGui.CalcTextSize(ago).X;
                ImGui.SetCursorScreenPos(new Vector2(rightEdge - agoW, start.Y));
                ImGui.AlignTextToFramePadding();
                ImGui.TextColored(TextMuted, ago);

                // Clicking a recent player opens their profile. The party list deliberately does
                // not do this - rows there carry buttons, and a click that lands anywhere else
                // opening a window would fight them.
                if (ImGui.IsMouseClicked(ImGuiMouseButton.Left) && IsRowClicked(start, rightEdge))
                    clickedProfile = entry.Identity;
            }, contextMenu: () => DrawPlayerMenuItems(entry.Identity));
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
        private void DrawPlayerMenuItems(CharacterIdentity who)
        {
            if (ImGui.Selectable("  View profile"))
                OpenProfile(who);

            ImGui.Separator();

            if (ImGui.Selectable("  Report"))
                OpenReportDialog(who);

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

            if (DrawPrimaryButton("Turn on community ratings", new Vector2(240, 30)))
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
            Vector2 curPos = ImGui.GetCursorScreenPos();
            float width = ImGui.GetWindowWidth();

            ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(10, 7));
            ImGui.SetCursorScreenPos(new Vector2(ImGui.GetWindowPos().X + 14, curPos.Y + 8));
            ImGui.AlignTextToFramePadding();
            ImGui.PushFont(UiBuilder.IconFont);
            ImGui.TextColored(TextMuted, FontAwesomeIcon.Search.ToIconString());
            ImGui.PopFont();
            ImGui.SameLine(0, 8);

            ImGui.PushStyleColor(ImGuiCol.FrameBg, BgCardExpanded);
            ImGui.PushStyleColor(ImGuiCol.Border, BorderDefault);
            ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 1.0f);
            ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 6.0f);
            ImGui.SetNextItemWidth(width - 52);

            if (ImGui.InputTextWithHint("##SearchPlayer", "Search someone you've met, or Name@World",
                    ref ratingSearchInput, 64, ImGuiInputTextFlags.EnterReturnsTrue))
            {
                RunPlayerSearch();
            }

            RefreshSearchSuggestions();

            ImGui.PopStyleVar(2);
            ImGui.PopStyleColor(2);
            ImGui.PopStyleVar(); // FramePadding

            float divY = curPos.Y + 50;
            ImGui.GetWindowDrawList().AddLine(
                new Vector2(ImGui.GetWindowPos().X, divY),
                new Vector2(ImGui.GetWindowPos().X + width, divY),
                ImGui.ColorConvertFloat4ToU32(BorderDefault), 1.0f);
            ImGui.SetCursorScreenPos(new Vector2(ImGui.GetWindowPos().X + 8, curPos.Y + 56));
        }

        /// <summary>
        /// Rebuilds <see cref="ratingSearchSuggestions"/> when the input changes.
        ///
        /// Matches on the name only, and on a substring rather than a prefix, so a surname finds
        /// someone whose first name you've forgotten - which is the case that made typing
        /// Name@World feel like work.
        /// </summary>
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

        /// <summary>Everyone this install can name: people met in a duty first (most recent first,
        /// which is who you are most likely to be looking up), then anyone rated.</summary>
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
                DrawHoverRow($"suggest{target.Key}", rightEdge =>
                {
                    Vector2 start = ImGui.GetCursorScreenPos();
                    DrawRowIdentity(0, target.Name, target.World, start.X, rightEdge);

                    if (ImGui.IsMouseClicked(ImGuiMouseButton.Left) && IsRowClicked(start, rightEdge))
                        picked = target;
                }, contextMenu: () => DrawPlayerMenuItems(target));
            }

            if (picked != null)
                SelectSearchResult(picked);
        }

        /// <summary>Whether the click landed on this row's strip. The rows are drawn rects rather
        /// than ImGui items, so there is no IsItemClicked to lean on.</summary>
        private static bool IsRowClicked(Vector2 start, float rightEdge)
        {
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
                ratingSearchError = "Enter the character as Name@World, e.g. Maro Botic@Zodiark.";
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
