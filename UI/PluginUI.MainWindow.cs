using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;

namespace PfPresets
{
    /// <summary>
    /// The main window: title bar, search, the preset card list, and the footer
    /// (Auto Refresher toggle + Create button), plus the delete confirmation.
    /// </summary>
    public partial class PluginUI
    {
        private string searchQuery = string.Empty;

        /// <summary>Preset whose private-party PIN is currently revealed, or empty. Held only while
        /// the mouse is down on it, and never persisted.</summary>
        private string revealedPinId = string.Empty;

        /// <summary>
        /// The window: one of two fixed shapes, drawn as a phone or as a tablet.
        ///
        /// SIZE IS SET EVERY FRAME, not once. ImGuiCond.Always rather than FirstUseEver because the
        /// player can switch layouts in Settings while looking at the window, and because ImGui
        /// restores a remembered size from its own ini file on the first frame of every session -
        /// which for anybody upgrading is whatever they had dragged the old resizable window to.
        ///
        /// The position is left alone. Where somebody parked it is theirs, and unlike the size it
        /// cannot make the layout wrong.
        /// </summary>
        private void DrawMainWindow()
        {
            if (!isMainWindowVisible)
                return;

            Vector2 screen = DeviceMetrics.SizeOf(config.Device);
            float radius = DeviceMetrics.ScreenRadius(config.Device);

            ImGui.SetNextWindowSize(screen, ImGuiCond.Always);

            // NO OUTER BORDER.
            //
            // ImGui strokes the window border along the rounded rect, but the fills inside it -
            // the header at the top, the tab bar at the bottom - are drawn by us into the same
            // corners, and the two do not land on the same pixels. At a 30px radius that gap shows
            // as a nicked corner on all four sides: the outline reads as a frame the content has
            // been cut out of. A device does not have a hairline around it, so the border is gone
            // and the shape comes from the fill.
            ImGui.PushStyleColor(ImGuiCol.WindowBg, Ground);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, radius);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(0, 0));
            ImGui.PushStyleVar(ImGuiStyleVar.WindowMinSize, screen);

            // NoResize and no collapse arrow, and the title bar was already gone. There is no
            // minimise either: the button used to shrink the window to a 52px strip, which was a
            // third layout with its own title bar, its own badge rules and no navigation at all.
            ImGuiWindowFlags flags = ImGuiWindowFlags.NoTitleBar
                | ImGuiWindowFlags.NoScrollbar
                | ImGuiWindowFlags.NoResize
                | ImGuiWindowFlags.NoCollapse
                | ImGuiWindowFlags.NoScrollWithMouse;

            // WHILE A PROMPT IS UP, THIS WINDOW CANNOT COME FORWARD. THIS IS THE WHOLE FIX FOR A
            // PROMPT THAT VANISHES AND CANNOT BE GOT BACK.
            //
            // The scrim and the sheet are raised above this window once, on the frame the sheet
            // opens - re-focusing them every frame would tear the keyboard focus out of whatever
            // field the sheet contains, on every keystroke. That leaves a hole: anything that
            // afterwards gives this window focus raises it above both, and since it is the same
            // size as the scrim it covers the prompt completely. The prompt is still there, still
            // modal, still eating the clicks aimed at the window now drawn on top of it - which is
            // exactly as stuck as it sounds, with no way to bring it back.
            //
            // NoBringToFrontOnFocus puts this window in ImGui's background layer for as long as the
            // prompt lives, so focusing it does nothing to the z-order and the prompt stays where
            // it was put. Costs nothing: there is nothing to raise it above.
            if (activeSheet != SheetKind.None)
                flags |= ImGuiWindowFlags.NoBringToFrontOnFocus;

            bool isOpen = isMainWindowVisible;
            // Begin/End and the style pushes above live on ImGui's process-global stacks, shared
            // with Dalamud and every other plugin. These try/finally blocks guarantee End() still
            // runs (so ImGui recovers the window's stack) and our pushes are always popped, so a
            // throw inside the window can never leave the stacks unbalanced and bleed into other
            // plugins' UI.
            try
            {
                bool visible = ImGui.Begin("PfPresets##Main", ref isOpen, flags);
                try
                {
                    if (visible)
                    {
                        isMainWindowVisible = isOpen;

                        // Recorded for the sheets, which are separate ImGui windows pinned inside
                        // this rectangle so a prompt looks like part of the phone rather than a
                        // second window floating beside it. See PluginUI.Sheets.cs.
                        RecordScreenRect();

                        // Checked here rather than at construction: on a first run the plugin is
                        // built before there is any UI to put a window on.
                        MaybeShowWelcome();

                        if (config.Device == DeviceLayout.Landscape)
                            DrawLandscapeShell();
                        else
                            DrawPortraitShell();

                        ChromeDiagnosticRequested = false;
                    }
                }
                finally
                {
                    ImGui.End();
                }
            }
            finally
            {
                ImGui.PopStyleVar(4);
                ImGui.PopStyleColor();
            }
        }

        /// <summary>
        /// The tablet: a rail down the left, a titled header over the body.
        ///
        /// Rail and body are siblings on one row - the rail owns its own scroll and its own
        /// background, and the body's layout code goes on measuring "the window", which inside the
        /// child is the column it fills.
        /// </summary>
        private void DrawLandscapeShell()
        {
            if (ChromeDiagnosticRequested)
                ReportChromeDiagnostic($"tablet layout: {ImGui.GetWindowWidth():F0}x{ImGui.GetWindowHeight():F0}");

            DrawRail();
            ImGui.SameLine(0, 0);

            // A hairline, not the 2px rule it was. Sidebar and body used to be two shades of the
            // same near-black and needed a stroke to be told apart; against #000000 the panel tone
            // separates them on its own, and a bright 2px seam down the middle of the window is the
            // loudest thing on screen.
            Vector2 seam = ImGui.GetCursorScreenPos();
            ImGui.GetWindowDrawList().AddRectFilled(seam,
                new Vector2(seam.X + 1f, seam.Y + ImGui.GetContentRegionAvail().Y),
                ImGui.ColorConvertFloat4ToU32(RuleHair));
            ImGui.SameLine(0, 1);

            // Square-cornered, deliberately. The body's own right edge is the window's, and the
            // header strip paints that corner itself; a rounded child would round the seam side too
            // and leave the same wedge the sidebar had.
            ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 0f);
            ImGui.BeginChild("BodyColumn", new Vector2(0, -1), false,
                ImGuiWindowFlags.NoScrollbar);
            ImGui.PopStyleVar();
            try
            {
                DrawHeaderStrip();
                DrawActiveTab();
            }
            finally
            {
                ImGui.EndChild();
            }
        }

        /// <summary>
        /// The phone: brand header, body, tab bar, home indicator.
        ///
        /// The body is given exactly the height left between the header and the tab bar rather than
        /// being allowed to size itself. A tab bar pushed off the bottom edge by a long body is a
        /// phone with no navigation, and the bodies here are lists that grow with the player's data.
        /// </summary>
        private void DrawPortraitShell()
        {
            if (ChromeDiagnosticRequested)
                ReportChromeDiagnostic($"phone layout: {ImGui.GetWindowWidth():F0}x{ImGui.GetWindowHeight():F0}");

            DrawPortraitHeader();

            // THE TAB LIST IS BUILT BEFORE THE BODY, even though the bar is drawn after it.
            //
            // TabList is not a query - it is what notices that the tab you are on has stopped
            // existing (the poll ended, ratings were switched off) and moves you somewhere real.
            // Built after the body, that correction would land a frame late and the body would draw
            // once for a tab that is no longer in the navigation. The tablet's sidebar gets this for
            // free by being on the left; the phone's bar has to ask for it.
#if PFP_RATINGS
            var tabs = TabList();
#endif

            // The tab bar is placed from the bottom of the screen, and the body is given whatever is
            // left above it.
            //
            // NOT by subtracting the bar's height from the remaining space, which is what this did
            // first and what was wrong. ImGui adds ItemSpacing.Y after every item it lays out, the
            // body child included, so the bar ended up a spacing's worth below where the arithmetic
            // said - far enough to push the home indicator off the bottom edge of the window. An
            // absolute position cannot drift, whatever the style has to say about gaps.
            float tabTop = ImGui.GetWindowHeight() - PortraitTabBarHeight();
            float bodyHeight = tabTop - ImGui.GetCursorPosY();

            ImGui.BeginChild("BodyColumn", new Vector2(0, MathF.Max(1f, bodyHeight)), false,
                ImGuiWindowFlags.NoScrollbar);
            try
            {
                DrawActiveTab();
            }
            finally
            {
                ImGui.EndChild();
            }

            ImGui.SetCursorPos(new Vector2(0, tabTop));

#if PFP_RATINGS
            DrawPortraitTabBar(tabs);
#else
            DrawPortraitTabBar();
#endif
        }

        /// <summary>
        /// Whichever tab is on screen. Extracted from the window itself because both layouts show
        /// the same bodies and only differ in what surrounds them - the rail and header strip when
        /// there is room, the title bar and top tabs when there is not.
        /// </summary>
        private void DrawActiveTab()
        {
            // Erased entirely in an ordinary build - see PluginUI.AdminHooks.cs.
            bool extraHandled = false;
            DrawPanelTabBody(ref extraHandled);
            if (extraHandled)
                return;

#if PFP_RATINGS
            if (activeTab == MainTab.Ratings && config.CommunityEnabled)
            {
                DrawRatingsTab();
                return;
            }

            if (activeTab == MainTab.Achievements)
            {
                DrawAchievementsTab();
                return;
            }

            if (activeTab == MainTab.Vote)
            {
                DrawVoteTab();
                return;
            }

            if (activeTab == MainTab.Settings)
            {
                DrawSettingsTab();
                return;
            }
#endif

            // Built once per frame and shared by the card and the list's height reservation, so
            // both agree on how much space it takes.
            var snapshot = pfAutomation.GetSnapshot(ImGui.GetFrameCount());

            // Inside a duty the card has nothing true to say: there is no listing to end, no one to
            // recruit, and none of its actions apply. The party list stands alone in there instead.
            // Logged out it has less than nothing to say - and used to render a stale "Not logged
            // in" card with the last party still listed under it.
            bool showCard = !pfAutomation.IsInDuty()
                && snapshot.Activity != PfActivity.NotLoggedIn;

            // Decided before the measure, because the party list counts its rows against this and
            // the card's height comes from that count. Measuring with one answer and drawing with
            // another is how a row ends up outside the card's clip rect.
#if PFP_RATINGS
            cardOwnsProgressAction = showCard && InAParty();
#endif

            // The card is the only thing that needs the party leader's listing, so the watcher that
            // fetches it only runs while the card is up.
            if (showCard)
                pfAutomation.MarkListingCardVisible();

            // Two columns when there is room and something to put in the left one: who you are
            // recruiting on the left, what you can post on the right.
            //
            // Stacked, the presets - the thing this tab is for - started below the fold on any
            // evening where the card and a full party were both on screen, and stayed there. Side
            // by side, neither half can push the other off.
            //
            // Only when the card is up. Inside a duty there is no card and the party list is the
            // whole of the left column, and half a tablet given over to eight names with the
            // presets squeezed into the rest is worse than one column.
            // No longer conditional on the card. Idling solo used to leave the left column empty,
            // so the split was suppressed and the presets ran the full width; the left column has
            // something to say in every state now - see LeftColumnTitle - so the tab keeps one
            // shape instead of rearranging itself when a party breaks up.
            bool split = ImGui.GetContentRegionAvail().X >= RecruitSplitMinWidth;

            // THE TOOLBAR SPANS BOTH COLUMNS. It lived inside the presets column for a day, which
            // put a search field over one half of the tab and nothing over the other, and left the
            // whole top-right of the window looking like it had lost something. Searching is the
            // tab's own control, not the preset list's.
            DrawSearchBar();

            if (split)
            {
                float bodyH = ImGui.GetContentRegionAvail().Y - GetFooterHeight();
                float colW = (ImGui.GetWindowWidth() - BodyGutter * 2f - ColumnGap) * 0.5f;

                ImGui.SetCursorPosX(BodyGutter);
                if (ImGui.BeginChild("RecruitLeft", new Vector2(colW, bodyH), false))
                {
                    try
                    {
                        // A heading naming what the column is about, the way the profile tab names
                        // its two. Which words those are is the whole state of the tab in three
                        // words - see LeftColumnTitle.
                        DrawListHeading(LeftColumnTitle(snapshot));

                        // THE PARTY LIST IS NOT PART OF THE CARD'S CONDITION.
                        //
                        // It draws whenever there is a party, and the card draws when it has
                        // something to say - two different questions, and they were briefly the
                        // same one. Inside a duty showCard is false by design (there is no listing
                        // to end and nobody to recruit), so an if/else that put the party panel on
                        // the card's branch took the party list down with it: eight people in an
                        // ultimate, and the tab said "idling solo".
                        if (showCard && snapshot.HasAnythingToShow)
                            DrawStatusCard(snapshot, MeasureStatusCard(snapshot));

#if PFP_RATINGS
                        DrawPartyPanel(snapshot);
#endif

                        // Only when there is genuinely nothing else: no card and no party.
                        if (!(showCard && snapshot.HasAnythingToShow) && !InAPartyForColumn())
                            DrawSoloCard();
                    }
                    finally { ImGui.EndChild(); }
                }
                else ImGui.EndChild();

                ImGui.SameLine(0, ColumnGap);

                if (ImGui.BeginChild("RecruitRight", new Vector2(colW, bodyH), false))
                {
                    try
                    {
                        DrawListHeading("My presets");
                        DrawPresetList();
                    }
                    finally { ImGui.EndChild(); }
                }
                else ImGui.EndChild();
            }
            else
            {
                // Recruitment card, party list and presets scroll together as one region. They used
                // to be siblings, with the preset list sizing itself from whatever space was left -
                // so once the card and the party list were both on screen there was nothing left and
                // the presets were simply cut off with no way to reach them.
                ImGui.SetCursorPosX(BodyGutter);
                float scrollH = ImGui.GetContentRegionAvail().Y - GetFooterHeight();
                if (ImGui.BeginChild("MainScroll",
                        new Vector2(ImGui.GetWindowWidth() - BodyGutter * 2f, scrollH), false))
                {
                    try
                    {
                        // The same two headings the wide layout has. One column instead of two, so
                        // they are stacked rather than side by side, but a phone should not be a
                        // different tab from a tablet - only a narrower one.
                        DrawListHeading(LeftColumnTitle(snapshot));

                        // THE PARTY LIST IS NOT PART OF THE CARD'S CONDITION.
                        //
                        // It draws whenever there is a party, and the card draws when it has
                        // something to say - two different questions, and they were briefly the
                        // same one. Inside a duty showCard is false by design (there is no listing
                        // to end and nobody to recruit), so an if/else that put the party panel on
                        // the card's branch took the party list down with it: eight people in an
                        // ultimate, and the tab said "idling solo".
                        if (showCard && snapshot.HasAnythingToShow)
                            DrawStatusCard(snapshot, MeasureStatusCard(snapshot));

#if PFP_RATINGS
                        DrawPartyPanel(snapshot);
#endif

                        // Only when there is genuinely nothing else: no card and no party.
                        if (!(showCard && snapshot.HasAnythingToShow) && !InAPartyForColumn())
                            DrawSoloCard();

                        DrawListHeading("My presets");
                        DrawPresetList();
                    }
                    finally
                    {
                        ImGui.EndChild();
                    }
                }
                else
                {
                    ImGui.EndChild();
                }
            }

            DrawFooter();
        }

        /// <summary>
        /// What the left column is about, in the fewest words that are true.
        ///
        /// Four states, and the last of them is the reason this exists: standing around on your own
        /// is not "nothing", it is the state most people have the plugin open in, and the column was
        /// simply blank for it. Naming where you are turns an empty half of the tab into a line
        /// that tells you the plugin is awake and knows who and where you are.
        /// </summary>
        private string LeftColumnTitle(RecruitmentSnapshot snap)
        {
            if (snap.IsRecruiting)
                return "Your recruitment";

            if (pfAutomation.IsInDuty())
            {
                // THE FIGHT, NOT THE MAP. Dancing Mad is fought in Sigmascape V4.0, and the plugin
                // announcing "In Sigmascape V4.0" to somebody eight minutes into an ultimate is
                // technically true and useless - it is the zone the duty happens to be built on,
                // and nobody in there calls it that. The duty sheet knows which fight a territory
                // belongs to; the place name is the fallback for a zone that is not a duty at all.
                string duty = pfAutomation.CurrentDutyName();
                if (duty.Length > 0)
                    return $"In {duty}";

                string zone = pfAutomation.CurrentZoneName();
                return zone.Length > 0 ? $"In {zone}" : "In a duty";
            }

            if (snap.InParty)
                return "In a party";

            string where = pfAutomation.CurrentZoneName();
            return where.Length > 0 ? $"Idling solo at {where}" : "Idling solo";
        }

        /// <summary>Whether the left column already has a party list in it, so the solo card knows
        /// to stay out of the way. Without ratings there is no party panel at all, so the answer is
        /// always no and the card stands in for it.</summary>
        private bool InAPartyForColumn()
        {
#if PFP_RATINGS
            return PartyMemberCount() > 0;
#else
            return false;
#endif
        }

        /// <summary>
        /// The left column when there is no listing and no party: who you are, and nothing else.
        ///
        /// A card rather than a line of grey text, because the column beside it is a column of
        /// cards and one half of a tab going flat when a party breaks up reads as something having
        /// failed to load. What it says is deliberately small - your name and your world, with the
        /// heading above it carrying where you are - because that is the plugin confirming it can
        /// see you, and there is nothing else to confirm.
        /// </summary>
        private void DrawSoloCard()
        {
            var dl = ImGui.GetWindowDrawList();
            Vector2 cardMin = ImGui.GetCursorScreenPos();
            float cardWidth = ImGui.GetContentRegionAvail().X;

            const float pad = CardPadding;

            float nameH;
            using (UiPersonFont.Push())
                nameH = ImGui.GetTextLineHeight();
            float bodyH;
            using (UiBodyFont.Push())
                bodyH = ImGui.GetTextLineHeight();

            float cardHeight = pad * 2f + nameH + 4f + bodyH;

            var soloMax = new Vector2(cardMin.X + cardWidth, cardMin.Y + cardHeight);
            dl.AddRectFilled(cardMin, soloMax, ImGui.ColorConvertFloat4ToU32(Field), Radius.Card);
            dl.AddRect(cardMin, soloMax, ImGui.ColorConvertFloat4ToU32(CardBorder),
                Radius.Card, ImDrawFlags.None, 1f);

            string name = "Not logged in";
            string second = "The plugin is waiting for a character.";

            // THE SAME LINE THE PROFILE CARD BUILDS, from the same two sources - see
            // DrawProfileIdentity. Your own job and level are read off the game rather than the
            // network, because for the one character we can always answer for there is no reason to
            // ask anybody, and "@Malboro" on its own says less than the game already knows.
            var (jobId, jobLevel) = pfAutomation.GetLocalJobAndLevel();
            string jobName = jobId != 0 ? JobData.FindById(jobId)?.Name ?? string.Empty : string.Empty;
            bool hasJob = jobId != 0 && jobName.Length > 0;

            string job = !hasJob
                ? string.Empty
                : jobLevel > 0 ? $" · Level {jobLevel} {jobName}" : $" · {jobName}";

#if PFP_RATINGS
            var me = LocalIdentity?.Invoke();
            if (me != null)
            {
                name = DisplayName(me.Name);
                second = $"@{me.World}{job}";
            }
#else
            if (hasJob)
                second = job.TrimStart(' ', '\u00b7', ' ');
#endif

            float textX = cardMin.X + pad;
            float room = cardWidth - pad * 2f;

            // The job icon beside the name at the name's own size, exactly as the profile card sets
            // it - the icon is how the game says who somebody is, and it belongs at reading size.
            using (UiPersonFont.Push())
            {
                if (hasJob && TryGetIconHandle(IconJobBase + jobId, out var jobHandle))
                {
                    dl.AddImage(jobHandle, new Vector2(textX, cardMin.Y + pad),
                        new Vector2(textX + nameH, cardMin.Y + pad + nameH));
                    textX += nameH + 8f;
                    room -= nameH + 8f;
                }

                dl.AddText(new Vector2(textX, cardMin.Y + pad),
                    ImGui.ColorConvertFloat4ToU32(Ink), Fit(name, room));
            }

            using (UiBodyFont.Push())
                dl.AddText(new Vector2(cardMin.X + pad, cardMin.Y + pad + nameH + 4f),
                    ImGui.ColorConvertFloat4ToU32(Dim), Fit(second, cardWidth - pad * 2f));

            ImGui.Dummy(new Vector2(cardWidth, cardHeight));
        }

        /// <summary>Where the Recruit tab has room for two columns. Above the tablet's body width
        /// and well above the phone's, so this is a tablet-only arrangement in practice.</summary>
        private const float RecruitSplitMinWidth = 780f;

        /// <summary>The space a body leaves either side of itself, and between two columns.
        ///
        /// Both went up with the black ground. Cards on a near-black field could sit close together
        /// and still read as separate things; on true black the gap between them IS the separation,
        /// and 8px of it looked like the cards had been pushed against the glass.</summary>
        private const float BodyGutter = Space.Gutter;
        private const float ColumnGap = Space.Gutter;

        /// <summary>How far the Recruit toolbar sets itself in from the surface it is drawn on.
        /// Named because the preset cards have to line up with it.</summary>
        private const float SearchBarInset = Space.Gutter;

        /// <summary>
        /// The Recruit tab's toolbar: find a preset, or make one.
        ///
        /// New preset and Import used to live at the bottom of the window, under the auto-refresh
        /// controls. They are the two things a first-time user needs and the footer is where the
        /// settings live, so they moved up here beside the search that shares their subject.
        /// </summary>
        /// <summary>
        /// The Recruit toolbar: search, and the two things you can do with a preset you do not have
        /// yet.
        ///
        /// SEARCHING TAKES OVER THE ROW. The moment there is a query the two buttons go and the
        /// field runs the full width with a cross on its end. Searching and creating are not things
        /// anybody does in the same breath - while you are looking for a preset, "New preset" is a
        /// button you are not going to press and a hundred and fifty pixels the results could have
        /// had. Clearing brings them straight back, which is what the cross is for.
        /// </summary>
        private void DrawSearchBar()
        {
            const float barHeight = ToolbarHeight;
            const float gap = Space.Tight;
            const float controlH = ToolbarButtonHeight;

            Vector2 origin = ImGui.GetCursorScreenPos();
            float width = ImGui.GetWindowWidth();
            float winX = ImGui.GetWindowPos().X;

            // Both buttons say what they do. Import was an icon on its own - a tray with an arrow
            // in it - which is a picture of "download" as often as it is a picture of "import".
            const string newLabel = "New preset";
            const string importLabel = "Import preset";

            float glyphW;
            using (UiIconSmall.Push())
                glyphW = ImGui.CalcTextSize(FontAwesomeIcon.Plus.ToIconString()).X;

            // New preset is the wider of the two on purpose. It is the one action on this tab that
            // somebody with no presets has to find, and sized to its own label it came out a shade
            // narrower than Import - the less important button looking like the more important one.
            //
            // WIDTH ONLY. It was set a size up as well for a day, which put a 15px semibold label
            // beside Import's 13px regular one - two typefaces stacked on the same row, and the
            // pair read as a mistake rather than as a hierarchy. Extra padding says "this one" in a
            // way that does not require the eye to notice a font change to understand it.
            float newW = NewPresetPadX + glyphW + 8f + ImGui.CalcTextSize(newLabel).X + NewPresetPadX;
            float importW = SearchBarInset + glyphW + 8f + ImGui.CalcTextSize(importLabel).X + SearchBarInset;

            float actionsW = newW + gap + importW;

            float controlY = origin.Y + (barHeight - controlH) * 0.5f;
            float fieldX = winX + SearchBarInset;

            bool searching = searchQuery.Length > 0;
            float fieldW = searching
                ? width - SearchBarInset * 2f
                : MathF.Max(120f, width - SearchBarInset * 2f - actionsW - gap);

            ImGui.SetCursorScreenPos(new Vector2(fieldX, controlY));
            DrawSearchFieldClearable("SearchPresets", "Search presets", ref searchQuery, fieldW,
                out _, controlH);

            if (searching)
            {
                ImGui.SetCursorScreenPos(new Vector2(winX, origin.Y + barHeight));
                return;
            }

            // Both actions are meaningless while the editor owns the preset being edited.
            bool editingInProgress = isEditorWindowVisible;
            if (editingInProgress) ImGui.BeginDisabled();

            Vector2 newPos = new(winX + width - SearchBarInset - actionsW, controlY);
            var newSize = new Vector2(newW, controlH);
            ImGui.SetCursorScreenPos(newPos);
            if (DrawNeutralButton("##CreatePreset", newSize))
            {
                // Detached: it is not in the list, not counted and not on disk until Save is
                // pressed. See Configuration.CreateDetachedPreset.
                var created = config.CreateDetachedPreset();
                OpenEditor(created, true);
            }
            bool createHovered = ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled);

            // Drawn by hand so the glyph comes from the icon font, which a plain Button label
            // cannot reach. Same face and same icon size as Import beside it.
            DrawIconLabelLeft(FontAwesomeIcon.Plus, newLabel, newPos, newSize, Ground,
                NewPresetPadX, UiIconSmall);

            Vector2 importPos = new(newPos.X + newW + gap, controlY);
            var importSize = new Vector2(importW, controlH);
            ImGui.SetCursorScreenPos(importPos);
            if (DrawSecondaryButton("##ImportPreset", importSize))
                OpenShareImport();
            bool importHovered = ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled);
            DrawIconLabelLeft(FontAwesomeIcon.FileImport, importLabel, importPos, importSize, Ink,
                SearchBarInset, UiIconSmall);

            if (editingInProgress) ImGui.EndDisabled();

            if (editingInProgress && (createHovered || importHovered))
                PaddedTooltip("Finish or close the current preset first.");
            else if (importHovered)
                PaddedTooltip("Import a preset from a share code.");

            // NO RULE UNDER IT. The toolbar sits on the ground with the cards below it, and the
            // gap between them is the separation - the same way every other surface in the plugin
            // is separated now. A full-width line across the top of the body was left over from the
            // ruled design and read as an edge the content had been pushed under.
            ImGui.SetCursorScreenPos(new Vector2(winX, origin.Y + barHeight));
        }

        /// <summary>New preset's own padding, wider than everything else's. The only thing that
        /// distinguishes it from Import - same face, same icon, more room.</summary>
        private const float NewPresetPadX = 22f;

        /// <summary>
        /// The preset cards. Draws inline rather than into its own scroll child: it shares one
        /// scroll region with the recruitment card and the party list above it, so the whole
        /// column moves together instead of nesting a scrollbar inside a scrollbar.
        /// </summary>
        private void DrawPresetList()
        {
            ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(0, 8));

            // Snapshot the list so a card action (Duplicate / Move / Delete) can safely mutate
            // config.Presets while we're rendering. Enumerating the live list and mutating it in
            // the same frame throws "Collection was modified" (List<T> bumps its version on Add AND
            // on the indexer-set the Move swap uses), which — mid-render — leaves the shared ImGui
            // stack unbalanced. Any re-order/addition simply shows on the next frame.
            // Presets for a category the plugin cannot post are not shown. They are not deleted -
            // the category is expected back - but a card offering an Apply button that would put up
            // a wrong listing is worse than the preset being out of sight for a patch.
            //
            // IsOffered rather than IsSupported so the developer override can bring them back to be
            // fixed - see DutyComposition.OfferUnsupported. The card says (Unsupported) on its title
            // line when it is only here because of that.
            var visible = config.Presets
                .Where(p => DutyComposition.IsOffered(p.DutyCategoryId))
                .ToList();

            var filteredPresets = visible;
            if (!string.IsNullOrWhiteSpace(searchQuery))
            {
                string q = searchQuery.ToLowerInvariant();
                filteredPresets = visible
                    .Where(p => p.Name.ToLowerInvariant().Contains(q) || p.DutyName.ToLowerInvariant().Contains(q) || p.Comment.ToLowerInvariant().Contains(q))
                    .ToList();
            }

            if (filteredPresets.Count == 0)
            {
                ImGui.Dummy(new Vector2(0, 20));
                float tw = ImGui.CalcTextSize("No presets yet").X;
                ImGui.SetCursorPosX((ImGui.GetContentRegionAvail().X - tw) / 2f);
                ImGui.TextColored(TextMuted, "No presets yet");
                float sw = ImGui.CalcTextSize("Click + to create your first preset").X;
                ImGui.SetCursorPosX((ImGui.GetContentRegionAvail().X - sw) / 2f);
                ImGui.TextColored(TextMuted, "Click + to create your first preset");
            }
            else
            {
                foreach (var preset in filteredPresets)
                    DrawPresetRow(preset);
            }

            ImGui.Dummy(new Vector2(0, 4));
            ImGui.PopStyleVar();
        }

        /// <summary>Tag color for an objective: Loot=yellow, Duty Completion=blue, Practice=green, else grey.</summary>
        private static Vector4 GetObjectiveColor(int objectiveId) => objectiveId switch
        {
            1 => AccentBlue,    // Duty Completion
            2 => AccentGreen,   // Practice
            3 => AccentYellow,  // Loot
            _ => TextMuted,
        };

        /// <summary>Draws a small rounded tag pill and returns its width.</summary>
        private float DrawTagPill(Vector2 topLeft, string text, Vector4 color)
        {
            var dl = ImGui.GetWindowDrawList();
            Vector2 ts = ImGui.CalcTextSize(text);
            const float padX = 6f, h = 17f;
            float w = ts.X + padX * 2f;
            dl.AddRectFilled(topLeft, new Vector2(topLeft.X + w, topLeft.Y + h),
                ImGui.ColorConvertFloat4ToU32(new Vector4(color.X, color.Y, color.Z, 0.18f)),
                Radius.Chip);
            dl.AddText(new Vector2(topLeft.X + padX, topLeft.Y + (h - ts.Y) * 0.5f), ImGui.ColorConvertFloat4ToU32(color), text);
            return w;
        }

        /// <summary>
        /// One preset, as a card.
        ///
        /// STACKED, NOT COLUMNED. It used to be three columns - text, a meta block, an action stack
        /// - each sized from a constant, and the whole thing only held together at one width. The
        /// comment was clamped to two lines and clipped mid-sentence, the loot rule and the PIN had
        /// a column to themselves for two short words, and Apply preset sat in a 150px gutter that
        /// was empty on every card without a comment long enough to reach it.
        ///
        /// The card reads top to bottom now: what it is, what it filters for, who it seats, what
        /// you can do with it. The comment - the one part that is genuinely variable in length -
        /// folds away behind a chevron instead of being cut off, so a card is a fixed height until
        /// somebody asks it not to be.
        /// </summary>
        private void DrawPresetRow(PfPresetData preset)
        {
            const float pad = CardPadding;
            const float tile = 48f;
            const float tileGap = 12f;
            const float chevron = 28f;
            // The strip has the whole card width to itself in this layout rather than sharing a
            // column with the text, so the tiles can be read at a glance instead of squinted at.
            const float slot = 28f, slotGap = 5f;
            const float actionBtn = ButtonHeight;
            const float gap = 10f;

            float width = ImGui.GetContentRegionAvail().X;
            float inner = width - pad * 2f;

            string comment = preset.Comment ?? string.Empty;
            bool hasComment = !string.IsNullOrEmpty(comment);
            bool expanded = hasComment && expandedPresets.Contains(preset.Id);

            // ── Measure, before anything is drawn ─────────────────
            //
            // The card's height decides its background, its hover fill and its hit area, and all
            // three have to be right on the frame it first appears.
            float textLeft = pad + tile + tileGap;
            float textRoom = width - textLeft - chevron - pad - 8f;

            float nameLineH, subLineH;
            using (UiNameFont.Push()) nameLineH = ImGui.GetTextLineHeight();
            using (UiHelpFont.Push()) subLineH = ImGui.GetTextLineHeight();

            var chips = PresetChips(preset);
            float chipsH = 0f;
            if (chips.Count > 0)
            {
                int chipRows = Math.Max(1, ChipRowCount(chips, textRoom));
                chipsH = 6f + chipRows * ChipRowHeight + (chipRows - 1) * ChipRowGap;
            }

            float textBlockH = nameLineH + 2f + subLineH + chipsH;
            float headerH = MathF.Max(tile, textBlockH);

            float commentPanelH = 0f;
            List<string>? commentLines = null;
            float commentLabelH = 0f;
            if (expanded)
            {
                using (UiLabelFont.Push()) commentLabelH = ImGui.GetTextLineHeight();
                commentLines = WrapCommentInFace(comment, inner - CommentPanelPad * 2f, 5);
                commentPanelH = CommentPanelPad * 2f + commentLabelH + 4f
                    + commentLines.Count * CommentLineHeight();
            }

            float cardH = pad + headerH + gap + slot + gap + actionBtn
                + (expanded ? gap + commentPanelH : 0f) + pad;

            Vector2 origin = ImGui.GetCursorScreenPos();
            var dl = ImGui.GetWindowDrawList();

            // ONE COLOUR, ALWAYS. The card used to lighten to Raised under the cursor, which is
            // why it "still isn't #1c1c1e" - it is, right up until the mouse crosses it, and a card
            // is nearly always under the mouse when somebody is looking at it. A hover tint belongs
            // on something you press; this is a container, and everything inside it that IS
            // pressable lights up on its own.
            var cardMax = new Vector2(origin.X + width, origin.Y + cardH);
            dl.AddRectFilled(origin, cardMax, ImGui.ColorConvertFloat4ToU32(Field), Radius.Card);
            dl.AddRect(origin, cardMax, ImGui.ColorConvertFloat4ToU32(CardBorder),
                Radius.Card, ImDrawFlags.None, 1f);

            float top = origin.Y + pad;

            // ── The duty's tile ───────────────────────────────────
            var tileMin = new Vector2(origin.X + pad, top);
            var tileMax = new Vector2(tileMin.X + tile, tileMin.Y + tile);

            uint categoryIcon = GetCategoryIcon(
                dutyDataHelper.GetCategoryIdForDuty(preset.DutyRowId, preset.DutyName));

            if (categoryIcon == 0)
                categoryIcon = GetCategoryIcon(preset.DutyCategoryId);

            if (categoryIcon != 0 && TryGetIconHandle(categoryIcon, out var iconHandle))
            {
                dl.AddImage(iconHandle, tileMin, tileMax);
            }
            else
            {
                // The lettered fallback, and the only place a letter stands in for an icon: a
                // missing sheet entry must not leave a hole where the card's leading mark should be.
                dl.AddRectFilled(tileMin, tileMax, ImGui.ColorConvertFloat4ToU32(Panel), Radius.Tile);
                string initial = string.IsNullOrEmpty(preset.DutyName)
                    ? "?" : preset.DutyName[..1].ToUpperInvariant();
                using (UiNameFont.Push())
                {
                    Vector2 ts = ImGui.CalcTextSize(initial);
                    dl.AddText(new Vector2(tileMin.X + (tile - ts.X) * 0.5f,
                            tileMin.Y + (tile - ts.Y) * 0.5f),
                        ImGui.ColorConvertFloat4ToU32(Dim), initial);
                }
            }

            // ── Title, subtitle, chips ────────────────────────────
            float tx = origin.X + textLeft;
            float ty = top;

            // WHAT THE CARD IS FOR, AND WHETHER YOU CAN GET IN.
            //
            // A locked duty says so on the line that names it, because that line is the answer to
            // "why is Apply greyed out" and a tooltip on a disabled button is a poor place to keep
            // it. With HideLockedDuties on the name goes entirely and the card only admits that
            // something is locked - the point of the setting is not to be shown the fight.
            bool titleLocked = IsPresetLocked(preset);
            string dutyTitle = string.IsNullOrEmpty(preset.DutyName) ? "No duty set" : preset.DutyName;
            Vector4 titleColour = Ink;

            if (titleLocked)
            {
                if (config.HideLockedDuties)
                {
                    dutyTitle = "(Locked duty)";
                    titleColour = Faint;
                }
                else
                {
                    dutyTitle = $"{dutyTitle} (Locked)";
                }
            }

            // AND WHETHER THE PLUGIN CAN POST IT AT ALL. A card that is only on screen because the
            // developer override is on has to say so on the same line, or the six broken categories
            // become six cards indistinguishable from the working ones - which is exactly the
            // confusion the card was hidden to avoid, just moved somewhere harder to notice.
            if (DutyComposition.OfferUnsupported && !DutyComposition.IsSupported(preset.DutyCategoryId))
                dutyTitle = $"{dutyTitle} (Unsupported)";

            using (UiNameFont.Push())
                dl.AddText(new Vector2(tx, ty), ImGui.ColorConvertFloat4ToU32(titleColour),
                    Fit(dutyTitle, textRoom));

            ty += nameLineH + 2f;

            DrawPresetSubtitle(dl, preset, new Vector2(tx, ty), textRoom, subLineH);
            ty += subLineH;

            if (chips.Count > 0)
            {
                float chipX = tx;
                float chipY = ty + 6f;

                foreach (var (text, colour) in chips)
                {
                    float w = ChipWidth(text);
                    if (chipX > tx && chipX + w > tx + textRoom)
                    {
                        chipX = tx;
                        chipY += ChipRowHeight + ChipRowGap;
                    }

                    DrawChip(new Vector2(chipX, chipY), text, colour);
                    chipX += w + ChipGap;
                }
            }

            // ── The chevron, when there is something folded away ──
            if (hasComment)
            {
                var chevPos = new Vector2(origin.X + width - pad - chevron, top);
                ImGui.SetCursorScreenPos(chevPos);
                if (DrawIconSquareButton(
                        expanded ? FontAwesomeIcon.ChevronUp : FontAwesomeIcon.ChevronDown,
                        $"presetfold_{preset.Id}", chevron))
                {
                    if (!expandedPresets.Remove(preset.Id))
                        expandedPresets.Add(preset.Id);
                }
                if (ImGui.IsItemHovered())
                    PaddedTooltip(expanded ? "Hide the comment" : "Show the comment");
            }

            // ── Slot strip ────────────────────────────────────────
            {
                float sx = origin.X + pad;
                float sy = top + headerH + gap;

                if (preset.UsesAutoAdjust)
                {
                    var autoSlots = pfAutomation.GetAutoAdjustedSlots();
                    int n = Math.Min(autoSlots.Count, 8);
                    for (int i = 0; i < n; i++)
                    {
                        DrawAutoSlotMiniIcon(autoSlots[i].Role, autoSlots[i].JobId, new Vector2(sx, sy), slot);
                        sx += slot + slotGap;
                    }
                }
                else
                {
                    int n = Math.Min(preset.Slots.Count, 8);
                    for (int i = 0; i < n; i++)
                    {
                        DrawSlotMiniIcon(preset.Slots[i], new Vector2(sx, sy), slot);
                        sx += slot + slotGap;
                    }
                }
            }

            // ── Actions ───────────────────────────────────────────
            float actionsY = top + headerH + gap + slot + gap;
            DrawPresetActions(dl, preset, origin.X + pad, actionsY, inner, actionBtn);

            // ── The comment, when it is asked for ─────────────────
            if (expanded && commentLines != null)
            {
                float panelY = actionsY + actionBtn + gap;
                var panelMin = new Vector2(origin.X + pad, panelY);
                var panelMax = new Vector2(panelMin.X + inner, panelY + commentPanelH);

                // A step LIGHTER than the card, not darker. Recessed was the instinct and it was
                // wrong: this panel is the thing you just asked to see, and a surface that sinks
                // away from the card holding it reads as disabled rather than as revealed.
                dl.AddRectFilled(panelMin, panelMax,
                    ImGui.ColorConvertFloat4ToU32(Raised), Radius.Small);

                using (UiLabelFont.Push())
                    dl.AddText(new Vector2(panelMin.X + CommentPanelPad, panelMin.Y + CommentPanelPad),
                        ImGui.ColorConvertFloat4ToU32(Dim), "COMMENT");

                float cy = panelMin.Y + CommentPanelPad + commentLabelH + 4f;
                ImGui.SetCursorScreenPos(new Vector2(panelMin.X + CommentPanelPad, cy));

                // Drawn line by line rather than as one wrapped block, because an auto-translate
                // phrase's brackets have to be tinted separately from the words between them, and
                // ImGui's own wrapping colours a whole call at once.
                using (CommentFont.Push())
                    DrawCommentLines(commentLines, Ink, inner - CommentPanelPad * 2f);
            }

            ImGui.SetCursorScreenPos(new Vector2(origin.X, origin.Y + cardH));
            ImGui.Dummy(new Vector2(width, 0f));
        }

        /// <summary>Padding inside the folded-out comment panel.</summary>
        private const float CommentPanelPad = 12f;

        /// <summary>Which presets have their comment folded out. Not persisted: it is a way of
        /// looking at the list rather than a property of the preset.</summary>
        private readonly HashSet<string> expandedPresets = new();

        /// <summary>
        /// The line under a preset's duty: what the preset is called, its loot rule, and its PIN.
        ///
        /// The PIN is hidden until the line is hovered, which is the same bargain the old meta
        /// column struck - a private party's password is on screen in a window somebody might be
        /// streaming, and it should take an act to show it.
        /// </summary>
        private void DrawPresetSubtitle(ImDrawListPtr dl, PfPresetData preset, Vector2 at,
            float room, float lineH)
        {
            string name = string.IsNullOrEmpty(preset.Name) ? "Unnamed" : preset.Name;
            string line = $"{name} · {DisplayNames.GetLootRuleName(preset.LootRules)}";

            if (HasPin(preset))
            {
                bool shown = revealedPinId == preset.Id;
                line += shown ? $" · PIN {preset.PrivatePartyPassword}" : " · PIN ••••";
            }

            using (UiHelpFont.Push())
                dl.AddText(at, ImGui.ColorConvertFloat4ToU32(Faint), Fit(line, room));

            if (!HasPin(preset))
                return;

            bool over = IsMouseOver(at, new Vector2(at.X + room, at.Y + lineH));
            if (over)
            {
                revealedPinId = preset.Id;
                PaddedTooltip("Private party password.");
            }
            else if (revealedPinId == preset.Id)
            {
                revealedPinId = string.Empty;
            }
        }

        /// <summary>
        /// Apply, and the three things you can do to the preset itself.
        ///
        /// Apply takes whatever the row does not need, so it is the widest control on the card at
        /// every width rather than a fixed 150px that was too wide on a phone and marooned on a
        /// tablet.
        /// </summary>
        private void DrawPresetActions(ImDrawListPtr dl, PfPresetData preset,
            float left, float y, float room, float h)
        {
            const float smallGap = 8f;
            float small = h;
            float applyW = room - (small * 3f + smallGap * 3f);
            applyW = MathF.Max(90f, applyW);

            bool canRecruit = CanRecruitCached(out var reason);

            // The game refuses a listing for content the character has not unlocked, so the button
            // refuses first. Same treatment as "you are not the party leader": outlined, unclickable
            // and it says why - rather than driving the whole automation into a window that then
            // turns it down.
            bool locked = IsPresetLocked(preset);
            if (locked)
            {
                canRecruit = false;
                reason = PresetLockReason(preset);
            }

            // When a non-battle job (crafter/gatherer) is in the party, applying a battle-duty
            // listing makes the game raise a composition warning. Recruiting still works (the
            // plugin auto-confirms it), so the button carries a warning colour rather than being
            // disabled.
            bool compWarn = canRecruit && PartyHasNonBattleJobCached();

            var applyPos = new Vector2(left, y);
            var applySize = new Vector2(applyW, h);

            // DISABLED IS AN OUTLINE, NOT A FILL. Filled with the card's own colour the button
            // vanished, and "you cannot recruit right now" looked like a rendering fault.
            Vector4 fill = !canRecruit ? new Vector4(0, 0, 0, 0) : compWarn ? AccentYellow : Accent;
            Vector4 hover = !canRecruit ? new Vector4(0, 0, 0, 0)
                : compWarn ? Lighten(AccentYellow, 0.12f) : AccentHover;
            Vector4 label = !canRecruit ? Faint : OnAccent;

            ImGui.SetCursorScreenPos(applyPos);
            ImGui.PushStyleColor(ImGuiCol.Button, fill);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, hover);
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, hover);
            bool applyClicked = ImGui.Button($"##apply_{preset.Id}", applySize);
            ImGui.PopStyleColor(3);

            if (!canRecruit)
                dl.AddRect(applyPos, applyPos + applySize,
                    ImGui.ColorConvertFloat4ToU32(BorderControl),
                    Radius.Control, ImDrawFlags.None, 1f);

            if (applyClicked && canRecruit)
            {
                pfAutomation.ApplyPreset(preset);
#if PFP_RATINGS
                // The end of a task, which is the only honest moment to ask for thirty seconds.
                // Silent unless a poll is open and this install has not voted.
                OfferVoteNudge();
#endif
            }

            if (ImGui.IsItemHovered())
            {
                if (!canRecruit)
                    PaddedTooltip($"Cannot recruit: {reason}");
                else if (compWarn)
                    PaddedTooltip("A non-battle job (crafter/gatherer) is in your party.\nThe game will warn about party composition - the listing still\nposts, and PF Analysis confirms the warning for you.");
            }

            DrawIconLabelCentered(FontAwesomeIcon.Play, "Apply preset", applyPos, applySize, label,
                1f, UiIconSmall);

            float sx = left + applyW + smallGap;

            ImGui.SetCursorScreenPos(new Vector2(sx, y));
            if (DrawRowActionButton($"##edit_{preset.Id}", new Vector2(small, small)))
                OpenEditor(preset, false);
            DrawGlyphCentered(FontAwesomeIcon.Pen, new Vector2(sx, y),
                new Vector2(sx + small, y + small), Dim, UiIconRow);
            if (ImGui.IsItemHovered()) PaddedTooltip("Edit");
            sx += small + smallGap;

            ImGui.SetCursorScreenPos(new Vector2(sx, y));
            if (DrawRowActionButton($"##share_{preset.Id}", new Vector2(small, small)))
                OpenShareExport(preset);
            DrawGlyphCentered(FontAwesomeIcon.Share, new Vector2(sx, y),
                new Vector2(sx + small, y + small), Dim, UiIconRow);
            if (ImGui.IsItemHovered()) PaddedTooltip("Share");
            sx += small + smallGap;

            ImGui.SetCursorScreenPos(new Vector2(sx, y));
            if (DrawRowActionButton($"##kebab_{preset.Id}", new Vector2(small, small)))
                ImGui.OpenPopup($"presetmenu_{preset.Id}");
            DrawGlyphCentered(FontAwesomeIcon.EllipsisV, new Vector2(sx, y),
                new Vector2(sx + small, y + small), Dim, UiIconRow);

            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(6, 6));
            if (ImGui.BeginPopup($"presetmenu_{preset.Id}"))
            {
                // Safe to mutate config.Presets here: DrawPresetList iterates a snapshot.
                if (ImGui.Selectable("  Duplicate")) config.DuplicatePreset(preset.Id);
                ImGui.Separator();
                if (ImGui.Selectable("  Move up")) config.MovePresetUp(preset.Id);
                if (ImGui.Selectable("  Move down")) config.MovePresetDown(preset.Id);
                ImGui.Separator();
                ImGui.PushStyleColor(ImGuiCol.Text, KoFi);
                if (ImGui.Selectable("  Delete"))
                {
                    var doomed = preset;
                    AskConfirm("Delete preset", $"Delete \"{doomed.Name}\"?", "Delete",
                        () => config.DeletePreset(doomed.Id),
                        detail: "This cannot be undone.");
                }
                ImGui.PopStyleColor();
                ImGui.EndPopup();
            }
            ImGui.PopStyleVar();
        }

        /// <summary>
        /// Whether this preset carries a private-party PIN.
        ///
        /// A saved password counts even when the private-party flag is off - the number is still on
        /// disk and still the thing somebody wants to read off the row, and hiding it because a
        /// checkbox happens to be unticked just makes it look lost.
        /// </summary>
        private static bool HasPin(PfPresetData preset)
            => preset.FormPrivateParty || !string.IsNullOrWhiteSpace(preset.PrivatePartyPassword);



        /// <summary>
        /// A bordered uppercase chip. Returns its width so a row of them can be laid out.
        ///
        /// Outlined rather than filled: these mark facts about a preset, and a row of filled pills
        /// reads as a row of buttons.
        /// </summary>
        /// <summary>Gap between two chips on the same line.</summary>
        private const float ChipGap = 6f;

        /// <summary>One chip's line box, and the gap between two wrapped lines of them. The box is
        /// a pixel taller than the chip itself draws, which is the breathing room the single-line
        /// layout always had.</summary>
        private const float ChipRowHeight = 18f;
        private const float ChipRowGap = 4f;

        /// <summary>
        /// The chips a preset earns, in the order they are drawn.
        ///
        /// Pulled out of the drawing because the row's height has to know how many there are and
        /// how wide, one pass before anything is on screen.
        /// </summary>
        private List<(string Text, Vector4 Colour)> PresetChips(PfPresetData preset)
        {
            var chips = new List<(string, Vector4)>(3);
            Vector4 colour = ObjectiveColour(preset.ObjectiveId);

            if (preset.ObjectiveId != 0)
                chips.Add((DisplayNames.GetObjectiveName(preset.ObjectiveId), colour));
            if (preset.CompletionStatusEnabled)
                chips.Add((DisplayNames.GetCompletionStatusName(preset.CompletionStatusType), colour));
            if (preset.OnePlayerPerJob)
                chips.Add(("One per job", colour));

            return chips;
        }

        /// <summary>
        /// What every chip on a preset is tinted with: the colour of what the preset is FOR.
        ///
        /// ONE COLOUR PER CARD, not one per chip. The chips used to be two shades of grey - the
        /// objective slightly brighter than the filters - which said nothing except that one of
        /// them was more important, and the important one was already first. Read as a set they now
        /// answer the question somebody scans a preset list to answer: is this a practice group, a
        /// clear, or a loot run.
        ///
        /// Data colours, so they do not follow the player's accent - the same rule the role and
        /// vote colours live under. Green for practice because it is the permissive one, blue for a
        /// completion run, amber for loot; the reds in this plugin are reserved for destruction and
        /// none of these three is that.
        /// </summary>
        private static Vector4 ObjectiveColour(int objectiveId) => objectiveId switch
        {
            1 => ColorFromHex("#4a9be0"),   // Duty Completion
            2 => Positive,                  // Practice
            3 => AccentYellow,              // Loot
            _ => Dim,                       // None - a preset with no stated objective
        };

        /// <summary>Width one chip occupies, measured exactly the way <see cref="DrawChip"/> will
        /// draw it - same font, same casing, same padding - so the two cannot disagree.</summary>
        private float ChipWidth(string text)
        {
            using (UiLabelFont.Push())
                return ImGui.CalcTextSize(text.ToUpperInvariant()).X + ChipPadX * 2f;
        }

        /// <summary>How many lines the chips wrap to at this width. Walks them the same way the
        /// drawing does, so the reserved height is the height actually used.</summary>
        private int ChipRowCount(List<(string Text, Vector4 Colour)> chips, float width)
        {
            if (chips.Count == 0)
                return 0;

            int rows = 1;
            float x = 0f;

            foreach (var (text, _) in chips)
            {
                float w = ChipWidth(text);
                if (x > 0f && x + w > width)
                {
                    rows++;
                    x = 0f;
                }
                x += w + ChipGap;
            }

            return rows;
        }

        /// <summary>
        /// THE chip. Every small capitalised tag in the plugin is this one object.
        ///
        /// They were three: the preset card's outlined tags, the Clears feed's kind chip and the
        /// BETA mark on the sidebar, each with its own padding, its own height and its own idea of
        /// how far the words sit from the edge. Same face, same words, three sizes - which reads as
        /// three different fonts even though only one is loaded.
        /// </summary>
        /// <param name="filled">A chip that states the card's own nature rather than tagging it -
        /// FIRST CLEAR on a first clear - takes the colour as a fill and writes on it.</param>
        private float DrawChip(Vector2 topLeft, string text, Vector4 color, bool filled = false)
        {
            var dl = ImGui.GetWindowDrawList();
            string shown = text.ToUpperInvariant();

            Vector2 ts;
            using (UiLabelFont.Push())
                ts = ImGui.CalcTextSize(shown);

            float w = ts.X + ChipPadX * 2f;
            var max = new Vector2(topLeft.X + w, topLeft.Y + ChipHeight);

            if (filled)
                dl.AddRectFilled(topLeft, max, ImGui.ColorConvertFloat4ToU32(color), Radius.Chip);
            else
                dl.AddRect(topLeft, max, ImGui.ColorConvertFloat4ToU32(color with { W = 0.55f }),
                    Radius.Chip, ImDrawFlags.None, 1f);

            using (UiLabelFont.Push())
                dl.AddText(new Vector2(topLeft.X + ChipPadX, topLeft.Y + (ChipHeight - ts.Y) * 0.5f),
                    ImGui.ColorConvertFloat4ToU32(filled ? OnAccent : color), shown);

            return w;
        }

        /// <summary>The one chip geometry, shared by everything that draws one.</summary>
        private const float ChipPadX = 6f;
        private const float ChipHeight = 17f;



        /// <summary>The live bar, when there is a listing for it to be about.</summary>
        private const float FooterBarHeight = 56f;

        /// <summary>The bar's stand-in when there isn't: just enough strip to hold the caret.</summary>
        private const float FooterCaretRowHeight = 44f;

        /// <summary>How much of the bar the duty name and status line keep before the Refresh
        /// button is allowed to take any of it. Roughly "Recruiting · 7 / 8 · refresh in 22:32".</summary>
        private const float FooterMinTextRoom = 210f;

        /// <summary>
        /// Whether the footer has anything live to report.
        ///
        /// Without a listing there is no countdown, no seat count and nothing for Refresh now to
        /// refresh - the bar was three dashes and a disabled button taking 56px of the preset list
        /// to say "nothing is happening", which the empty strip says by itself.
        /// </summary>
        private bool FooterHasLiveState()
            => pfAutomation.GetSnapshot(ImGui.GetFrameCount()).IsRecruiting;

        /// <summary>
        /// Whether the footer offers the auto-refresh settings at all.
        ///
        /// ONLY WHILE A LISTING IS UP. Auto-refresh re-posts a Party Finder listing before it
        /// expires; with no listing there is nothing to re-post, and inside a duty there is not
        /// going to be one. The strip was carrying two toggles, an interval and a stop-after
        /// through an entire ultimate prog night - settings nobody can act on, taking a hundred
        /// pixels off the preset list to say so.
        ///
        /// They are not lost: the same four controls live on the Party Finder settings page, which
        /// is where a decision you make once and live with belongs anyway. See
        /// DrawAutoRefreshControls - it is one function drawn in both places.
        /// </summary>
        private bool FooterOffersSettings()
        {
            var snap = pfAutomation.GetSnapshot(ImGui.GetFrameCount());

            // AND ONLY IF THE LISTING IS OURS. Somebody else's recruitment is still a live listing,
            // so this said yes for it - and offered auto-refresh, an interval, a stop-after and a
            // Refresh now for a listing this client has no ability to re-post. Four controls that
            // could not work, on the state where you are least able to do anything about it.
            return snap.IsRecruiting && snap.IsLeader;
        }

        private float GetFooterHeight()
        {
            float h = FooterHasLiveState() ? FooterBarHeight : FooterCaretRowHeight;

            if (!FooterOffersSettings() || !config.FooterExpanded)
                return h;

            h += 10f;                                   // gap under the bar
            if (!IsRecruitmentRefresherActive())
            {
                h += 40f;                               // auto-refresh row
                if (config.AutoRefresherEnabled)
                    h += 30f;                           // interval and stop-after chips
            }
            h += 40f;                                   // auto-adjust row
            return h;
        }

        /// <summary>
        /// The Recruit footer: what your listing is doing, and the one button you press about it.
        ///
        /// The settings that used to fill this strip - two toggles, an interval and a stop-after -
        /// are decisions people make once and then live with, while the countdown is the thing they
        /// come back to look at. So the live state is the bar and the settings are behind the
        /// caret, which is the arrangement the mockup describes and the reason the footer no longer
        /// costs a hundred pixels of the preset list to say nothing.
        /// </summary>
        private void DrawFooter()
        {
            Vector2 origin = ImGui.GetCursorScreenPos();
            float width = ImGui.GetWindowWidth();
            float winX = ImGui.GetWindowPos().X;

            var dl = ImGui.GetWindowDrawList();
            float stripHeight = ImGui.GetContentRegionAvail().Y;

            // THE STRIP HAS TO FOLLOW THE CORNER IT SITS IN.
            //
            // On the tablet this is the last thing in the body column and its bottom edge IS the
            // window's, so a square fill put a hard point in the one corner the window rounds - the
            // strip looked like it had been laid over the device rather than being part of it. On
            // the phone the tab bar is underneath and the same fill is correctly square.
            //
            // Measured against the recorded screen rect rather than the current window: this draws
            // inside whatever child the tab body happens to be, and that child's own rectangle says
            // nothing about where the device's edges are.
            ImDrawFlags corners = ImDrawFlags.RoundCornersNone;
            if (screenRectValid && origin.Y + stripHeight >= screenPos.Y + screenSize.Y - 1f)
            {
                if (winX + width >= screenPos.X + screenSize.X - 1f)
                    corners |= ImDrawFlags.RoundCornersBottomRight;
                if (winX <= screenPos.X + 1f)
                    corners |= ImDrawFlags.RoundCornersBottomLeft;
            }

            float cornerRadius = corners == ImDrawFlags.RoundCornersNone
                ? 0f
                : DeviceMetrics.ScreenRadius(config.Device);

            dl.AddRectFilled(new Vector2(winX, origin.Y),
                new Vector2(winX + width, origin.Y + stripHeight),
                ImGui.ColorConvertFloat4ToU32(Panel), cornerRadius, corners);
            dl.AddRectFilled(new Vector2(winX, origin.Y), new Vector2(winX + width, origin.Y + 2f),
                ImGui.ColorConvertFloat4ToU32(RuleStrong));

            bool live = FooterHasLiveState();
            float barHeight = live ? FooterBarHeight : FooterCaretRowHeight;

            if (live)
                DrawFooterBar(dl, winX, origin.Y, width);
            else
                DrawFooterCaretRow(winX, origin.Y, width);

            if (FooterOffersSettings() && config.FooterExpanded)
                DrawFooterSettings(winX, origin.Y + barHeight + 10f, width);
        }

        /// <summary>The always-visible line: mark, duty, live status, Refresh now, and the caret.</summary>
        private void DrawFooterBar(ImDrawListPtr dl, float winX, float top, float width)
        {
            var snap = pfAutomation.GetSnapshot(ImGui.GetFrameCount());

            const float mark = 26f;
            float midY = top + FooterBarHeight * 0.5f;

            // THE DUTY'S OWN ICON, not the plugin's mark.
            //
            // This carried the accent square with the chart-line logo in it, on the reasoning that
            // the strip should read as the plugin's rather than as part of the list. It does not
            // need saying: the strip is inside the plugin's window, the sidebar two inches away has
            // the same mark on it, and the one place a piece of chrome is worth spending on a
            // picture is where the picture can say something the words cannot. Beside "Dancing Mad
            // (Ultimate)" the useful picture is the kind of content it is - which the preset rows
            // in the list above already draw, from the same sheet.
            var markMin = new Vector2(winX + 12f, midY - mark * 0.5f);
            var markMax = new Vector2(markMin.X + mark, markMin.Y + mark);

            int dutyCategory = dutyDataHelper.GetCategoryIdForDuty(snap.DutyRowId, snap.DutyName);
            uint dutyIcon = GetCategoryIcon(dutyCategory);

            if (ChromeDiagnosticRequested)
                ReportChromeDiagnostic("footer icon: "
                    + dutyDataHelper.DescribeCategoryLookup(snap.DutyRowId, snap.DutyName)
                    + $" icon={dutyIcon} loaded={(dutyIcon != 0 && TryGetIconHandle(dutyIcon, out _))}");

            if (dutyIcon != 0 && TryGetIconHandle(dutyIcon, out var dutyHandle))
            {
                dl.AddImage(dutyHandle, markMin, markMax);
            }
            else
            {
                // A duty whose category will not resolve - a synthetic high-end entry, or a listing
                // read before the sheet was ready. An outlined tile rather than the logo: an empty
                // frame reads as "no icon for this", where the plugin's own mark would read as a
                // deliberate statement about the wrong thing.
                dl.AddRect(markMin, markMax, ImGui.ColorConvertFloat4ToU32(BorderControl),
                    Radius.Small, ImDrawFlags.None, 1f);
            }

            // Right-hand controls first: the text has to know how much room is left before it can
            // be fitted, and a duty name is the one thing here long enough to need the answer.
            const float caretW = ButtonHeight;
            float caretX = winX + width - 12f - caretW;
            float textX = markMin.X + mark + 12f;

            // The button used to be a flat 128px, and the text room under it was clamped to a
            // minimum rather than measured - so at the window's smallest width the button simply
            // sat on top of "refresh in 22:32". It is sized against what is actually left instead,
            // and gives up its label before it gives up the countdown, because the countdown is
            // the thing you glance at the strip to read.
            // NOT OUR LISTING, NOT OUR CONTROLS. Refresh now re-posts a listing this client does
            // not own, and the caret opens settings for the same. On somebody else's recruitment
            // the strip is a readout, and the room the two controls were taking goes to the text.
            bool ownListing = snap.IsLeader;

            float roomForButton = caretX - 8f - (textX + FooterMinTextRoom + 12f);
            string refreshLabel = "Refresh now";
            float refreshW = ImGui.CalcTextSize(refreshLabel).X + ButtonPadX * 2f;
            if (refreshW > roomForButton)
            {
                refreshLabel = "Refresh";
                refreshW = MathF.Max(ButtonHeight,
                    ImGui.CalcTextSize(refreshLabel).X + ButtonPadX * 2f);
            }

            float refreshX = caretX - 8f - refreshW;
            float textRoom = ownListing
                ? MathF.Max(60f, refreshX - 12f - textX)
                : MathF.Max(60f, winX + width - 12f - textX);

            string title = snap.IsRecruiting && !string.IsNullOrEmpty(snap.DutyName)
                ? snap.DutyName
                : snap.InParty ? "In a party" : "Not recruiting";

            using (UiNameFont.Push())
                dl.AddText(new Vector2(textX, top + 10f),
                    ImGui.ColorConvertFloat4ToU32(Ink), Fit(title, textRoom));

            DrawFooterStatusLine(dl, snap, textX, top + 30f, textRoom);

            if (!ownListing)
                return;

            // Refresh now: the listing's own action, and the only reason to look at this strip in
            // a hurry. Only reached when the listing is ours, so it is never disabled.
            ImGui.SetCursorScreenPos(new Vector2(refreshX, midY - ButtonHeight * 0.5f));
            if (DrawPrimaryButton($"{refreshLabel}##FooterRefreshNow", new Vector2(refreshW, ButtonHeight)))
                pfAutomation.ExecuteRefreshTask();
            if (ImGui.IsItemHovered())
                PaddedTooltip("Re-post your listing now, and restart the timer.");

            // The caret, which is the whole reason the settings can be out of the way.
            DrawFooterCaret(caretX, midY - ButtonHeight * 0.5f, caretW);
        }

        /// <summary>
        /// The footer with no listing up: the caret, and a line saying what the settings are for.
        ///
        /// The strip does not disappear entirely, because the two switches behind the caret are
        /// exactly what somebody sets *before* they start recruiting.
        /// </summary>
        /// <summary>
        /// The footer with no listing up: where you are, and nothing you can press.
        ///
        /// It used to carry the caret and a line about auto-refresh - controls for a listing that
        /// does not exist, offered most loudly during a duty, which is the one state where they are
        /// certainly not wanted. What is worth saying with no listing is the same thing the header
        /// says: which fight you are in. Same icon, same name, from the same lookup.
        /// </summary>
        private void DrawFooterCaretRow(float winX, float top, float width)
        {
            float midY = top + FooterCaretRowHeight * 0.5f;
            var dl = ImGui.GetWindowDrawList();

            var snap = pfAutomation.GetSnapshot(ImGui.GetFrameCount());

            string duty = pfAutomation.IsInDuty()
                ? pfAutomation.CurrentDutyName()
                : snap.DutyName ?? string.Empty;

            if (string.IsNullOrWhiteSpace(duty)
                || duty.Equals("None", StringComparison.OrdinalIgnoreCase))
            {
                using (UiBodyFont.Push())
                {
                    float lineH = ImGui.GetTextLineHeight();
                    dl.AddText(new Vector2(winX + BodyGutter, midY - lineH * 0.5f),
                        ImGui.ColorConvertFloat4ToU32(Faint),
                        Fit("No listing up.", width - BodyGutter * 2f));
                }
                return;
            }

            const float mark = 18f;
            float textX = winX + BodyGutter;

            uint dutyIcon = GetCategoryIcon(
                dutyDataHelper.GetCategoryIdForDuty(snap.DutyRowId, duty));

            if (dutyIcon != 0 && TryGetIconHandle(dutyIcon, out var dutyHandle))
            {
                var markMin = new Vector2(textX, midY - mark * 0.5f);
                dl.AddImage(dutyHandle, markMin, new Vector2(markMin.X + mark, markMin.Y + mark));
                textX += mark + 10f;
            }

            using (UiBodyFont.Push())
            {
                float lineH = ImGui.GetTextLineHeight();
                dl.AddText(new Vector2(textX, midY - lineH * 0.5f),
                    ImGui.ColorConvertFloat4ToU32(Dim),
                    Fit(duty, winX + width - BodyGutter - textX));
            }
        }

        /// <summary>The expand/collapse control, identical in both footer states.</summary>
        private void DrawFooterCaret(float x, float y, float size)
        {
            ImGui.SetCursorScreenPos(new Vector2(x, y));
            Vector2 pos = ImGui.GetCursorScreenPos();

            if (DrawSecondaryButton("##FooterExpand", new Vector2(size, ButtonHeight)))
            {
                config.FooterExpanded = !config.FooterExpanded;
                config.Save();
            }

            DrawGlyphCentered(config.FooterExpanded ? FontAwesomeIcon.ChevronDown : FontAwesomeIcon.ChevronUp,
                pos, new Vector2(pos.X + size, pos.Y + ButtonHeight), Ink);

            if (ImGui.IsItemHovered())
                PaddedTooltip(config.FooterExpanded ? "Hide recruitment settings" : "Show recruitment settings");
        }

        /// <summary>
        /// "Recruiting · 5 / 8 · refresh in 12:41", with the countdown in the accent.
        ///
        /// Assembled piece by piece rather than as one string because the countdown is the only
        /// part that is coloured, and it is the part being watched.
        /// </summary>
        private void DrawFooterStatusLine(ImDrawListPtr dl, RecruitmentSnapshot snap,
            float x, float y, float room)
        {
            uint dim = ImGui.ColorConvertFloat4ToU32(Dim);

            using (UiBodyFont.Push())
            {
                string head = snap.IsRecruiting
                    ? snap.SlotsTotal > 0
                        ? $"Recruiting · {snap.SlotsFilled} / {snap.SlotsTotal}"
                        : "Recruiting"
                    : snap.InParty ? "No listing up" : "Nothing listed";

                dl.AddText(new Vector2(x, y), dim, head);
                float cursor = x + ImGui.CalcTextSize(head).X;

                // SOMEBODY ELSE'S LISTING HAS NO REFRESH TO COUNT DOWN TO.
                //
                // The refresher only ever re-posts our own listing, so on another leader's it had
                // nothing to report and said so with "refresh in --:--" - a clock with no time on
                // it, sitting under a party we cannot refresh. What is worth knowing there is how
                // long their listing has left, which is the one number the game will actually tell
                // us about it.
                if (snap.IsRecruiting && !snap.IsLeader)
                {
                    if (!snap.TimeLeft.HasValue)
                        return;

                    string expiry = $" · expires in {FormatTimeLeft(snap.TimeLeft.Value)}";
                    if (cursor + ImGui.CalcTextSize(expiry).X <= x + room)
                        dl.AddText(new Vector2(cursor, y), dim, expiry);

                    return;
                }

                if (!config.AutoRefresherEnabled || IsRecruitmentRefresherActive())
                    return;

                (string text, Vector4 colour) = FooterCountdown();

                // This line is drawn as a run rather than laid out, so it has to check the room it
                // was given: a narrow window drops the wording before it drops the clock.
                float limit = x + room;
                float timeW = ImGui.CalcTextSize(text).X;
                string joiner = " · refresh in ";
                if (cursor + ImGui.CalcTextSize(joiner).X + timeW > limit)
                    joiner = " · ";
                if (cursor + ImGui.CalcTextSize(joiner).X + timeW > limit)
                    return;

                dl.AddText(new Vector2(cursor, y), dim, joiner);
                cursor += ImGui.CalcTextSize(joiner).X;

                dl.AddText(new Vector2(cursor, y), ImGui.ColorConvertFloat4ToU32(colour), text);
            }
        }

        /// <summary>The refresher's own clock: running, capped out, or dormant.</summary>
        private (string Text, Vector4 Colour) FooterCountdown()
        {
            if (pfAutomation.IsRefreshTimerRunning)
            {
                double secs = pfAutomation.SecondsUntilNextRefresh;
                return ($"{(int)(secs / 60):D2}:{(int)(secs % 60):D2}", Accent);
            }

            // The cap ended refreshing; the listing is still up but is no longer renewed.
            if (pfAutomation.HasReachedMaxDuration)
                return ("stopped", AccentYellow);

            return ("--:--", Faint);
        }

        /// <summary>The two switches and the refresher's numbers, once the caret asks for them.</summary>
        private void DrawFooterSettings(float winX, float top, float width)
        {
            var dl = ImGui.GetWindowDrawList();
            float y = top;

            if (!IsRecruitmentRefresherActive())
            {
                ImGui.SetCursorScreenPos(new Vector2(winX + 12f, y));
                bool autoRefresh = config.AutoRefresherEnabled;
                if (DrawStyledCheckbox("Auto-refresh recruitment##FooterAutoRefresher", ref autoRefresh))
                {
                    config.AutoRefresherEnabled = autoRefresh;
                    config.Save();
                }
                SameLineHelpDot("FooterAutoRefresher",
                    "Re-posts your Party Finder listing on a timer, so it stays near the top of the "
                    + "list. The countdown starts once your listing is up.");

                y += 40f;

                if (autoRefresh)
                {
                    // Interval and duration cap. Both values are free-form: the chip shows the
                    // number and double-clicking it turns the chip into a text field.
                    const float chipH = 22f, chipW = 62f;
                    float lblY = y + (chipH - ImGui.GetTextLineHeight()) * 0.5f;
                    uint labelCol = ImGui.ColorConvertFloat4ToU32(Dim);

                    const string everyLabel = "Refresh every";
                    dl.AddText(new Vector2(winX + 12f, lblY), labelCol, everyLabel);
                    float intervalChipX = winX + 12f + ImGui.CalcTextSize(everyLabel).X + 8f;

                    int interval = Math.Clamp(config.AutoRefresherIntervalMinutes,
                        PfAutomation.MinRefreshMinutes, PfAutomation.MaxRefreshMinutes);
                    if (DrawEditableNumberChip(
                            "interval", ref interval, "min", null,
                            PfAutomation.MinRefreshMinutes, PfAutomation.MaxRefreshMinutes,
                            new Vector2(intervalChipX, y), new Vector2(chipW, chipH),
                            $"How often to re-post your listing.\nDouble-click to change ({PfAutomation.MinRefreshMinutes}-{PfAutomation.MaxRefreshMinutes} minutes).\nA listing expires after 60 minutes."))
                    {
                        config.AutoRefresherIntervalMinutes = interval;
                        config.Save();
                    }

                    const string stopLabel = "Stop after";
                    float stopLabelX = intervalChipX + chipW + 14f;
                    dl.AddText(new Vector2(stopLabelX, lblY), labelCol, stopLabel);
                    float hoursChipX = stopLabelX + ImGui.CalcTextSize(stopLabel).X + 8f;

                    int maxHours = Math.Clamp(config.AutoRefresherMaxHours, 0, PfAutomation.MaxRefreshDurationHours);
                    if (DrawEditableNumberChip(
                            "maxhours", ref maxHours, "h", "Never",
                            0, PfAutomation.MaxRefreshDurationHours,
                            new Vector2(hoursChipX, y), new Vector2(chipW, chipH),
                            $"Stop auto-refreshing after this long, so a listing doesn't\nstay up all night unattended. Your listing isn't cancelled -\nit just expires normally.\nDouble-click to change (0 = never stop, max {PfAutomation.MaxRefreshDurationHours}h)."))
                    {
                        config.AutoRefresherMaxHours = maxHours;
                        config.Save();
                    }

                    y += 30f;
                }
            }

            ImGui.SetCursorScreenPos(new Vector2(winX + 12f, y));
            bool autoAdjust = config.AutoAdjustLockedJobsEnabled;
            if (DrawStyledCheckbox("Adjust 1-slot locked jobs while recruiting##FooterAutoAdjust", ref autoAdjust))
            {
                config.AutoAdjustLockedJobsEnabled = autoAdjust;
                config.Save();
            }
            SameLineHelpDot("FooterAutoAdjust",
                "While you're recruiting as party leader, if a member leaves, any Party Finder slot "
                + "locked to a single job is widened to that job's role - White Mage to regen "
                + "healers, Viper to melee - so the freed seat is easier to fill.");
        }

        private string chipEditingId = string.Empty;
        private int chipEditValue = 0;
        private bool chipEditFocusPending = false;

        /// <summary>
        /// A small chip showing a number that turns into a text field on double-click. Used for the
        /// refresh interval and the stop-after cap, which are free-form numbers but are read far more
        /// often than they're changed - so the resting state stays a compact label, not an input box.
        /// Returns true on the frame the value is committed.
        /// </summary>
        private bool DrawEditableNumberChip(
            string id, ref int value, string suffix, string? zeroLabel,
            int min, int max, Vector2 pos, Vector2 size, string tooltip)
        {
            ImGui.SetCursorScreenPos(pos);

            if (chipEditingId == id)
            {
                ImGui.SetNextItemWidth(size.X);
                PushFramedInput();
                if (chipEditFocusPending)
                {
                    ImGui.SetKeyboardFocusHere();
                    chipEditFocusPending = false;
                }
                // step/step_fast of 0 hides InputInt's +/- buttons so it stays chip-sized.
                bool entered = ImGui.InputInt($"##chipedit_{id}", ref chipEditValue, 0, 0, "%d",
                    ImGuiInputTextFlags.EnterReturnsTrue | ImGuiInputTextFlags.AutoSelectAll);
                // Committing on deactivate too means clicking away saves rather than silently
                // discarding what was typed.
                bool deactivated = ImGui.IsItemDeactivated();
                PopFramedInput();

                if (entered || deactivated)
                {
                    value = Math.Clamp(chipEditValue, min, max);
                    chipEditingId = string.Empty;
                    return true;
                }
                return false;
            }

            string label = (value <= 0 && zeroLabel != null) ? zeroLabel : $"{value} {suffix}";
            ImGui.PushStyleColor(ImGuiCol.Button, BgCard);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Raised);
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, BorderControl);
            ImGui.PushStyleColor(ImGuiCol.Text, AccentBlue);
            ImGui.PushStyleColor(ImGuiCol.Border, BorderDefault);
            ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 1f);
            ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, Radius.Control);

            // CENTRED, against the theme's default. Every other button in the plugin sets its label
            // flush left, which is right for a button whose width comes from a layout and wrong for
            // one whose width comes from the longest word it might ever hold: "30 min" and "Never"
            // are both short, both sat against the left edge of a chip sized for neither, and both
            // looked like they had slipped.
            ImGui.PushStyleVar(ImGuiStyleVar.ButtonTextAlign, new Vector2(0.5f, 0.5f));

            ImGui.Button($"{label}##chip_{id}", size);
            ImGui.PopStyleVar(3);
            ImGui.PopStyleColor(5);

            if (ImGui.IsItemHovered())
            {
                PaddedTooltip(tooltip);
                if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                {
                    chipEditingId = id;
                    chipEditValue = value;
                    chipEditFocusPending = true;
                }
            }
            return false;
        }

    }
}
