using System;
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
        // The window is resizable between these. The floor is what the narrow layout needs before
        // its rows start truncating; the ceiling is generous because the wide layout is a two-column
        // reading surface and someone with the screen for it should be able to use it.
        private const float MinWindowWidth = 400f;
        private const float MinWindowHeight = 560f;
        private const float MaxWindowWidth = 1600f;
        private const float MaxWindowHeight = 1200f;
        private const float DefaultWindowWidth = 980f;
        private const float DefaultWindowHeight = 640f;
        private const float CollapsedHeight = 52f;

        private bool isMinimized = false;
        private bool shouldRestoreSize = false;
        private int restoreFramesDelay = 0;
        private string searchQuery = string.Empty;
        private string hoveredPresetId = string.Empty;

        /// <summary>Preset whose private-party PIN is currently revealed, or empty. Held only while
        /// the mouse is down on it, and never persisted.</summary>
        private string revealedPinId = string.Empty;

        private void DrawMainWindow()
        {
            if (!isMainWindowVisible)
                return;

            if (isMinimized)
            {
                ImGui.SetNextWindowSizeConstraints(new Vector2(MinWindowWidth, CollapsedHeight),
                    new Vector2(MaxWindowWidth, CollapsedHeight));
                ImGui.SetNextWindowSize(new Vector2(config.PanelWidth, CollapsedHeight), ImGuiCond.Always);
            }
            else if (shouldRestoreSize)
            {
                ImGui.SetNextWindowSizeConstraints(new Vector2(MinWindowWidth, MinWindowHeight),
                    new Vector2(MaxWindowWidth, MaxWindowHeight));
                ImGui.SetNextWindowSize(new Vector2(config.PanelWidth, config.PanelHeight), ImGuiCond.Always);
                shouldRestoreSize = false;
                restoreFramesDelay = 3;
            }
            else
            {
                ImGui.SetNextWindowSizeConstraints(new Vector2(MinWindowWidth, MinWindowHeight),
                    new Vector2(MaxWindowWidth, MaxWindowHeight));
            }

            ImGui.PushStyleColor(ImGuiCol.WindowBg, Ground);
            ImGui.PushStyleColor(ImGuiCol.Border, RuleStrong);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 1.0f);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 0f);
            ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 0f);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(0, 0));
            ImGui.PushStyleVar(ImGuiStyleVar.WindowMinSize, new Vector2(MinWindowWidth, CollapsedHeight));

            ImGuiWindowFlags flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoScrollbar;
            if (isMinimized)
                flags |= ImGuiWindowFlags.NoResize;

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

                        if (!isMinimized)
                            PersistWindowSize();

                        // Checked here rather than at construction: on a first run the plugin is
                        // built before there is any UI to put a window on.
                        MaybeShowWelcome();

                        if (isMinimized)
                        {
                            DrawNarrowTitleBar();
                        }
                        else if (ImGui.GetWindowWidth() >= WideLayoutMinWidth)
                        {
                            if (ChromeDiagnosticRequested) ReportChromeDiagnostic($"wide layout: windowW={ImGui.GetWindowWidth():F0} "
                                + $"windowH={ImGui.GetWindowHeight():F0}");
                            // Rail and body are siblings on one row: the rail owns its own scroll
                            // and its own background, and the body's layout code goes on measuring
                            // "the window" - which, inside the child, is the column it fills.
                            DrawRail();
                            ImGui.SameLine(0, 0);

                            Vector2 seam = ImGui.GetCursorScreenPos();
                            ImGui.GetWindowDrawList().AddRectFilled(seam,
                                new Vector2(seam.X + 2f, seam.Y + ImGui.GetContentRegionAvail().Y),
                                ImGui.ColorConvertFloat4ToU32(RuleStrong));
                            ImGui.SameLine(0, 2);

                            ImGui.BeginChild("BodyColumn", new Vector2(0, -1), false,
                                ImGuiWindowFlags.NoScrollbar);
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
                        else
                        {
                            if (ChromeDiagnosticRequested) ReportChromeDiagnostic($"narrow layout: windowW={ImGui.GetWindowWidth():F0} "
                                + $"(wide needs {WideLayoutMinWidth:F0})");
                            DrawNarrowTitleBar();
#if PFP_RATINGS
                            DrawNavStrip();
#endif
                            DrawActiveTab();
                            ChromeDiagnosticRequested = false;
                        }
                    }
                }
                finally
                {
                    ImGui.End();
                }
            }
            finally
            {
                ImGui.PopStyleVar(5);
                ImGui.PopStyleColor(2);
            }
        }

        /// <summary>
        /// Whichever tab is on screen. Extracted from the window itself because both layouts show
        /// the same bodies and only differ in what surrounds them - the rail and header strip when
        /// there is room, the title bar and top tabs when there is not.
        /// </summary>
        private void DrawActiveTab()
        {
#if PFP_RATINGS
            if (activeTab == MainTab.Ratings && config.RatingsEnabled)
            {
                DrawRatingsTab();
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

            float statusHeight = showCard ? MeasureStatusCard(snapshot) : 0f;

            // The card is the only thing that needs the party leader's listing, so the watcher that
            // fetches it only runs while the card is up.
            if (showCard)
                pfAutomation.MarkListingCardVisible();

            DrawSearchBar();

            // Recruitment card, party list and presets scroll together as one region. They used to
            // be siblings, with the preset list sizing itself from whatever space was left - so once
            // the card and the party list were both on screen there was nothing left and the presets
            // were simply cut off with no way to reach them.
            ImGui.SetCursorPosX(8);
            float scrollH = ImGui.GetContentRegionAvail().Y - GetFooterHeight();
            if (ImGui.BeginChild("MainScroll", new Vector2(ImGui.GetWindowWidth() - 16, scrollH), false))
            {
                try
                {
                    if (showCard)
                        DrawStatusCard(snapshot, statusHeight);
#if PFP_RATINGS
                    DrawPartyPanel(snapshot);
#endif
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

            DrawFooter();
        }

        /// <summary>Saves user resizes of the main window into the configuration.</summary>
        private void PersistWindowSize()
        {
            if (restoreFramesDelay > 0)
            {
                restoreFramesDelay--;
                return;
            }

            int currentWidth = (int)ImGui.GetWindowWidth();
            int currentHeight = (int)ImGui.GetWindowHeight();
            bool sizeChanged = false;
            // Bounded by the window's own limits, not by numbers from the old 480-wide layout.
            // Those said a width over 600 or a height over 900 was not a real size and refused to
            // record it - so after the window grew to 980x640, every resize was dropped and
            // restoring from minimised put back whatever stale size was last written.
            if (currentWidth != config.PanelWidth
                && currentWidth >= MinWindowWidth && currentWidth <= MaxWindowWidth)
            {
                config.PanelWidth = currentWidth;
                sizeChanged = true;
            }
            if (currentHeight != config.PanelHeight
                && currentHeight >= MinWindowHeight && currentHeight <= MaxWindowHeight)
            {
                config.PanelHeight = currentHeight;
                sizeChanged = true;
            }
            if (sizeChanged)
                config.Save();
        }

        /// <summary>
        /// The Recruit tab's toolbar: find a preset, or make one.
        ///
        /// New preset and Import used to live at the bottom of the window, under the auto-refresh
        /// controls. They are the two things a first-time user needs and the footer is where the
        /// settings live, so they moved up here beside the search that shares their subject.
        /// </summary>
        private void DrawSearchBar()
        {
            const float barHeight = 52f;
            const float importW = ButtonHeight;
            const float gap = 6f;
            const float controlH = ButtonHeight;

            Vector2 origin = ImGui.GetCursorScreenPos();
            float width = ImGui.GetWindowWidth();
            float winX = ImGui.GetWindowPos().X;
            var dl = ImGui.GetWindowDrawList();

            bool wide = width >= 560f;
            string newLabel = wide ? "New preset" : "New";
            float newW = wide ? 138f : 86f;
            float actionsW = newW + gap + importW;

            float controlY = origin.Y + (barHeight - controlH) * 0.5f;

            float fieldX = winX + 12f;
            float fieldW = MathF.Max(120f, width - 12f - actionsW - 24f);

            ImGui.SetCursorScreenPos(new Vector2(fieldX, controlY));
            DrawSearchField("SearchPresets", "Search presets", ref searchQuery, fieldW, controlH);

            // Both actions are meaningless while the editor owns the preset being edited.
            bool editingInProgress = isEditorWindowVisible;
            if (editingInProgress) ImGui.BeginDisabled();

            Vector2 newPos = new(winX + width - actionsW - 12f, controlY);
            var newSize = new Vector2(newW, controlH);
            ImGui.SetCursorScreenPos(newPos);
            if (DrawNeutralButton("##CreatePreset", newSize))
            {
                var created = config.AddPreset();
                OpenEditor(created, true);
            }
            bool createHovered = ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled);
            // Drawn by hand so the glyph comes from the icon font, which a plain Button label
            // cannot reach.
            DrawIconLabelLeft(FontAwesomeIcon.Plus, newLabel, newPos, newSize, Ground, 12f);

            Vector2 importPos = new(newPos.X + newW + gap, controlY);
            var importSize = new Vector2(importW, controlH);
            ImGui.SetCursorScreenPos(importPos);
            if (DrawSecondaryButton("##ImportPreset", importSize))
                OpenShareImport();
            bool importHovered = ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled);
            DrawGlyphCentered(FontAwesomeIcon.FileImport, importPos,
                new Vector2(importPos.X + importW, importPos.Y + controlH), Dim);

            if (editingInProgress) ImGui.EndDisabled();

            if (editingInProgress && (createHovered || importHovered))
                PaddedTooltip("Finish or close the current preset first.");
            else if (importHovered)
                PaddedTooltip("Import a preset from a share code.");

            ImGui.SetCursorScreenPos(new Vector2(winX, origin.Y + barHeight));
            DrawRuleStrong();
            ImGui.SetCursorScreenPos(new Vector2(winX + 8f, origin.Y + barHeight + 8f));
        }

        /// <summary>
        /// The preset cards. Draws inline rather than into its own scroll child: it shares one
        /// scroll region with the recruitment card and the party list above it, so the whole
        /// column moves together instead of nesting a scrollbar inside a scrollbar.
        /// </summary>
        private void DrawPresetList()
        {
            ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(0, 6));

            // Snapshot the list so a card action (Duplicate / Move / Delete) can safely mutate
            // config.Presets while we're rendering. Enumerating the live list and mutating it in
            // the same frame throws "Collection was modified" (List<T> bumps its version on Add AND
            // on the indexer-set the Move swap uses), which — mid-render — leaves the shared ImGui
            // stack unbalanced. Any re-order/addition simply shows on the next frame.
            var filteredPresets = config.Presets.ToList();
            if (!string.IsNullOrWhiteSpace(searchQuery))
            {
                string q = searchQuery.ToLowerInvariant();
                filteredPresets = config.Presets
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
                ImGui.ColorConvertFloat4ToU32(new Vector4(color.X, color.Y, color.Z, 0.18f)), 4f);
            dl.AddText(new Vector2(topLeft.X + padX, topLeft.Y + (h - ts.Y) * 0.5f), ImGui.ColorConvertFloat4ToU32(color), text);
            return w;
        }

        /// <summary>How long the eye button holds a PIN open before it re-masks itself.</summary>
        private const double PinRevealSeconds = 5.0;

        private double pinRevealedAt;

        /// <summary>
        /// One preset, as a row in a ruled list rather than a card.
        ///
        /// Cards gave every preset a border, a fill and a corner radius, which at eight presets is
        /// eight boxes competing with the one thing that matters - which preset is which. A row
        /// separated by a hair rule spends nothing on chrome and puts every name on the same left
        /// edge, so the list is read by scanning rather than by looking.
        ///
        /// The grid is: duty icon, then the flexible information column, then a fixed meta column,
        /// then the actions. Below <see cref="RowNarrowWidth"/> the meta column folds into a single
        /// line under the name, because 128px of "Loot Normal" beside a 200px name is not a column.
        /// </summary>
        private void DrawPresetRow(PfPresetData preset)
        {
            const float iconCell = 34f;
            const float metaCell = 128f;
            const float actionsCell = 150f;
            const float padX = 12f;

            float width = ImGui.GetContentRegionAvail().X;
            bool wide = width >= RowNarrowWidth;

            string comment = preset.Comment ?? string.Empty;
            bool hasComment = !string.IsNullOrEmpty(comment);

            // Everything is measured before anything is drawn: the row's height decides the hover
            // fill and the hit area, and both have to be right on the first frame the row appears.
            float infoWidth = width - iconCell - padX * 3f - actionsCell - (wide ? metaCell + padX : 0f);
            infoWidth = MathF.Max(120f, infoWidth);

            // The height is summed from the same pieces the drawing walks through, in the same
            // order, rather than from a formula standing beside them. The first version left the
            // narrow layout's meta line out of the total, so at small widths the comment ran under
            // the slot strip - the strip was pinned to the bottom of a row that was too short for
            // what was above it. Nothing is pinned to the bottom now: the strip is laid where the
            // text leaves off, and the row is as tall as its contents.
            float lineH = ImGui.GetTextLineHeight();

            // Measured in the face the comment is actually drawn in. The reservation used the
            // default font's line height while the text renders in UiBodyFont, so every comment was
            // short by the difference and the last line lost its descenders. The extra 4px is
            // slack: wrapped text can take one pixel more than ceil() predicts at some widths, and
            // a row that is a pixel too tall costs nothing.
            float commentLineH;
            using (UiBodyFont.Push())
                commentLineH = ImGui.GetTextLineHeight();

            // Always reserve both lines when the comment needs more than one, and add a full line
            // of slack on top. Measuring wrapped text by dividing its unwrapped width by the wrap
            // width is an estimate - it is right about "one line or two" and wrong about the last
            // few pixels, and the cost of being wrong low is the comment printing over the job
            // icons underneath it.
            float commentH = hasComment
                ? MathF.Min(2f, CommentLineCount(comment, infoWidth)) * commentLineH + commentLineH * 0.5f
                : 0f;

            const float padTop = 12f, padBottom = 12f;
            const float nameH = 20f, chipsH = 18f, slotStripH = 20f, gap = 6f;
            float metaLineH = wide ? 0f : lineH + 4f;

            const float stripGap = 10f;

            float contentH = padTop + nameH + gap + chipsH + gap
                + metaLineH
                + (hasComment ? commentH + gap : 0f)
                + stripGap + slotStripH + padBottom;

            // The actions column has its own stack (apply, then the small buttons) and is the
            // taller side on a preset with nothing but a name.
            const float actionsH = padTop + 28f + gap + 24f + padBottom;

            float rowHeight = MathF.Max(contentH, actionsH);

            Vector2 origin = ImGui.GetCursorScreenPos();
            var dl = ImGui.GetWindowDrawList();

            bool hovered = IsMouseOver(origin, new Vector2(origin.X + width, origin.Y + rowHeight))
                && ImGui.IsWindowHovered(ImGuiHoveredFlags.AllowWhenBlockedByActiveItem);

            if (hovered)
            {
                hoveredPresetId = preset.Id;
                dl.AddRectFilled(origin, new Vector2(origin.X + width, origin.Y + rowHeight),
                    ImGui.ColorConvertFloat4ToU32(Raised));
            }
            else if (hoveredPresetId == preset.Id)
            {
                hoveredPresetId = string.Empty;
            }

            float x = origin.X + padX;
            float top = origin.Y + 12f;

            // ── Duty icon ─────────────────────────────────────────
            uint categoryIcon = GetCategoryIcon(preset.DutyCategoryId);
            if (categoryIcon != 0 && TryGetIconHandle(categoryIcon, out var iconHandle))
            {
                dl.AddImage(iconHandle, new Vector2(x, top), new Vector2(x + iconCell, top + iconCell));
            }
            else
            {
                // The lettered fallback, and the only place a letter stands in for an icon: a
                // missing sheet entry must not leave a hole where the row's leading mark should be.
                dl.AddRect(new Vector2(x, top), new Vector2(x + iconCell, top + iconCell),
                    ImGui.ColorConvertFloat4ToU32(BorderControl), 0f, 0, 1f);
                string initial = string.IsNullOrEmpty(preset.DutyName) ? "?" : preset.DutyName[..1].ToUpperInvariant();
                Vector2 ts = ImGui.CalcTextSize(initial);
                dl.AddText(new Vector2(x + (iconCell - ts.X) * 0.5f, top + (iconCell - ts.Y) * 0.5f),
                    ImGui.ColorConvertFloat4ToU32(Dim), initial);
            }

            float infoX = x + iconCell + padX;
            float cursorY = top;

            // ── Name, and the duty it is for ──────────────────────
            {
                string name = string.IsNullOrEmpty(preset.Name) ? "Unnamed" : preset.Name;
                float nameW;
                using (UiNameFont.Push())
                {
                    string shown = Fit(name, infoWidth * 0.6f);
                    nameW = ImGui.CalcTextSize(shown).X;
                    dl.AddText(new Vector2(infoX, cursorY), ImGui.ColorConvertFloat4ToU32(Ink), shown);
                }

                string duty = preset.DutyName ?? string.Empty;
                if (!string.IsNullOrEmpty(duty))
                {
                    using (UiHelpFont.Push())
                    {
                        float room = infoWidth - nameW - 10f;
                        dl.AddText(new Vector2(infoX + nameW + 10f, cursorY + 5f),
                            ImGui.ColorConvertFloat4ToU32(Faint), Fit(duty, room));
                    }
                }

                cursorY += 20f + 6f;
            }

            // ── Objective chips, and the narrow layout's meta line ─
            {
                float chipX = infoX;
                if (preset.ObjectiveId != 0)
                    chipX += DrawChip(new Vector2(chipX, cursorY),
                        DisplayNames.GetObjectiveName(preset.ObjectiveId), Dim) + 6f;
                if (preset.CompletionStatusEnabled)
                    chipX += DrawChip(new Vector2(chipX, cursorY),
                        DisplayNames.GetCompletionStatusName(preset.CompletionStatusType), Faint) + 6f;
                if (preset.OnePlayerPerJob)
                    DrawChip(new Vector2(chipX, cursorY), "One per job", Faint);

                cursorY += 18f + 6f;
            }

            if (!wide)
            {
                using (UiHelpFont.Push())
                {
                    string line = DisplayNames.GetLootRuleName(preset.LootRules)
                        + (HasPin(preset) ? " · PIN ••••" : string.Empty);
                    dl.AddText(new Vector2(infoX, cursorY), ImGui.ColorConvertFloat4ToU32(Faint),
                        Fit(line, infoWidth));
                }
                cursorY += lineH + 4f;
            }

            // ── Comment, clamped to two lines ─────────────────────
            if (hasComment)
            {
                ImGui.SetCursorScreenPos(new Vector2(infoX, cursorY));
                ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + infoWidth);
                using (UiBodyFont.Push())
                    ImGui.TextColored(Dim, ClampToLines(comment, infoWidth, 2));
                ImGui.PopTextWrapPos();
                cursorY += commentH + 6f;
            }

            // ── Slot strip ────────────────────────────────────────
            {
                const float slot = 20f, slotGap = 3f;
                float sx = infoX;
                float sy = cursorY + stripGap;

                if (preset.AutoAdjustRoles)
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

            // ── Meta column ───────────────────────────────────────
            float actionsX = origin.X + width - actionsCell - padX;
            if (wide)
            {
                float metaX = actionsX - metaCell - padX;
                float metaY = top + 2f;

                DrawMetaLine(dl, metaX, ref metaY, "Loot", DisplayNames.GetLootRuleName(preset.LootRules));

                if (HasPin(preset))
                    DrawPinCell(dl, preset, metaX, ref metaY, metaCell);
            }

            // ── Actions ───────────────────────────────────────────
            {
                const float applyH = ButtonHeight, smallW = 30f, smallGap = 6f;
                float applyW = actionsCell;
                float applyY = top;

                bool canRecruit = CanRecruitCached(out var reason);
                // When a non-battle job (crafter/gatherer) is in the party, applying a battle-duty
                // listing makes the game raise a composition warning. Recruiting still works (the
                // plugin auto-confirms it), so the button carries a warning colour rather than
                // being disabled.
                bool compWarn = canRecruit && PartyHasNonBattleJobCached();

                var applyPos = new Vector2(actionsX, applyY);
                var applySize = new Vector2(applyW, applyH);

                Vector4 fill = !canRecruit ? Field : compWarn ? AccentYellow : Accent;
                Vector4 hover = !canRecruit ? Field : compWarn ? Lighten(AccentYellow, 0.12f) : AccentHover;
                Vector4 label = !canRecruit ? Faint : OnAccent;

                ImGui.SetCursorScreenPos(applyPos);
                ImGui.PushStyleColor(ImGuiCol.Button, fill);
                ImGui.PushStyleColor(ImGuiCol.ButtonHovered, hover);
                ImGui.PushStyleColor(ImGuiCol.ButtonActive, hover);
                bool applyClicked = ImGui.Button($"##apply_{preset.Id}", applySize);
                ImGui.PopStyleColor(3);

                if (applyClicked && canRecruit)
                    pfAutomation.ApplyPreset(preset);

                if (ImGui.IsItemHovered())
                {
                    if (!canRecruit)
                        PaddedTooltip($"Cannot recruit: {reason}");
                    else if (compWarn)
                        PaddedTooltip("A non-battle job (crafter/gatherer) is in your party.\nThe game will warn about party composition - the listing still\nposts, and PF Analysis confirms the warning for you.");
                }

                DrawIconLabelLeft(FontAwesomeIcon.Play, "Apply preset", applyPos, applySize, label, 10f);

                float smallY = applyY + applyH + smallGap;
                float sx = actionsX;

                ImGui.SetCursorScreenPos(new Vector2(sx, smallY));
                if (DrawSecondaryButton($"##edit_{preset.Id}", new Vector2(smallW, 24f)))
                    OpenEditor(preset, false);
                DrawGlyphCentered(FontAwesomeIcon.Pen, new Vector2(sx, smallY),
                    new Vector2(sx + smallW, smallY + 24f), Dim);
                if (ImGui.IsItemHovered()) PaddedTooltip("Edit");
                sx += smallW + smallGap;

                ImGui.SetCursorScreenPos(new Vector2(sx, smallY));
                if (DrawSecondaryButton($"##share_{preset.Id}", new Vector2(smallW, 24f)))
                    OpenShareExport(preset);
                DrawGlyphCentered(FontAwesomeIcon.Share, new Vector2(sx, smallY),
                    new Vector2(sx + smallW, smallY + 24f), Dim);
                if (ImGui.IsItemHovered()) PaddedTooltip("Share");
                sx += smallW + smallGap;

                ImGui.SetCursorScreenPos(new Vector2(sx, smallY));
                if (DrawSecondaryButton($"##kebab_{preset.Id}", new Vector2(smallW, 24f)))
                    ImGui.OpenPopup($"presetmenu_{preset.Id}");
                DrawGlyphCentered(FontAwesomeIcon.EllipsisH, new Vector2(sx, smallY),
                    new Vector2(sx + smallW, smallY + 24f), Dim);

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

            // The row's own space, then the rule that separates it from the next one.
            ImGui.SetCursorScreenPos(new Vector2(origin.X, origin.Y + rowHeight));
            DrawRuleHair();
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
        /// The PIN, masked, with the whole cell as the reveal target.
        ///
        /// Masked by default and shown for a few seconds at a time: a password rendered in the
        /// clear in a window somebody streams is the one mistake this list can make that the player
        /// cannot undo. The eye used to be an 18px hit box floating at the right of the cell, which
        /// is a small target for the one control here people actually go looking for - the label,
        /// the digits and the glyph are all one button now.
        /// </summary>
        private void DrawPinCell(ImDrawListPtr dl, PfPresetData preset, float x, ref float y,
            float cellWidth)
        {
            bool revealed = revealedPinId == preset.Id
                && ImGui.GetTime() - pinRevealedAt < PinRevealSeconds;
            if (!revealed && revealedPinId == preset.Id)
                revealedPinId = string.Empty;

            Vector2 cell = new(x, y);
            ImGui.SetCursorScreenPos(cell);
            ImGui.InvisibleButton($"##pin_{preset.Id}", new Vector2(cellWidth, 28f));

            bool hot = ImGui.IsItemHovered();
            if (ImGui.IsItemClicked())
            {
                revealedPinId = revealed ? string.Empty : preset.Id;
                pinRevealedAt = ImGui.GetTime();
            }

            using (UiLabelFont.Push())
                dl.AddText(new Vector2(x, y),
                    ImGui.ColorConvertFloat4ToU32(hot ? Dim : Faint), "PIN");

            using (UiBodyFont.Push())
                dl.AddText(new Vector2(x, y + 11f),
                    ImGui.ColorConvertFloat4ToU32(revealed ? Accent : Ink),
                    revealed ? preset.PasswordDisplay : "••••");

            float glyphX = x + cellWidth - 22f;
            DrawGlyphAt(revealed ? FontAwesomeIcon.EyeSlash : FontAwesomeIcon.Eye,
                new Vector2(glyphX, y + 9f), 14f, hot ? Ink : Faint);

            if (hot)
                PaddedTooltip(revealed
                    ? "Hide the PIN."
                    : $"Show the PIN for {(int)PinRevealSeconds} seconds.");

            y += 30f;
        }

        /// <summary>Below this the meta column folds into a line under the name.</summary>
        private const float RowNarrowWidth = 640f;

        /// <summary>A label and its value, stacked in the meta column, advancing the cursor.</summary>
        private void DrawMetaLine(ImDrawListPtr dl, float x, ref float y, string label, string value)
        {
            using (UiLabelFont.Push())
                dl.AddText(new Vector2(x, y), ImGui.ColorConvertFloat4ToU32(Faint), label.ToUpperInvariant());

            using (UiBodyFont.Push())
                dl.AddText(new Vector2(x, y + 11f), ImGui.ColorConvertFloat4ToU32(Ink), value);

            y += 30f;
        }

        /// <summary>
        /// A bordered uppercase chip. Returns its width so a row of them can be laid out.
        ///
        /// Outlined rather than filled: these mark facts about a preset, and a row of filled pills
        /// reads as a row of buttons.
        /// </summary>
        private float DrawChip(Vector2 topLeft, string text, Vector4 color)
        {
            var dl = ImGui.GetWindowDrawList();
            string shown = text.ToUpperInvariant();

            Vector2 ts;
            using (UiLabelFont.Push())
                ts = ImGui.CalcTextSize(shown);

            float w = ts.X + 12f;
            const float h = 17f;

            dl.AddRect(topLeft, new Vector2(topLeft.X + w, topLeft.Y + h),
                ImGui.ColorConvertFloat4ToU32(color with { W = 0.55f }), 0f, 0, 1f);

            using (UiLabelFont.Push())
                dl.AddText(new Vector2(topLeft.X + 6f, topLeft.Y + (h - ts.Y) * 0.5f),
                    ImGui.ColorConvertFloat4ToU32(color), shown);

            return w;
        }

        /// <summary>How many lines a comment wraps to at this width, capped where the caller caps it.</summary>
        private float CommentLineCount(string comment, float width)
        {
            if (width <= 0f)
                return 1f;

            using (UiBodyFont.Push())
                return MathF.Max(1f, MathF.Ceiling(ImGui.CalcTextSize(comment).X / width));
        }

        /// <summary>
        /// Cuts a comment to the given number of wrapped lines, with an ellipsis if it was cut.
        ///
        /// The row reserves height for two lines; text longer than that would otherwise overrun the
        /// rule beneath it and print over the next preset.
        /// </summary>
        private string ClampToLines(string comment, float width, int lines)
        {
            if (width <= 0f)
                return comment;

            using (UiBodyFont.Push())
            {
                float budget = width * lines;
                if (ImGui.CalcTextSize(comment).X <= budget)
                    return comment;

                for (int len = comment.Length - 1; len > 0; len--)
                {
                    string cut = comment[..len];
                    if (ImGui.CalcTextSize(cut).X <= budget - ImGui.CalcTextSize("…").X)
                        return cut + "…";
                }
                return "…";
            }
        }

        /// <summary>The live bar, when there is a listing for it to be about.</summary>
        private const float FooterBarHeight = 56f;

        /// <summary>The bar's stand-in when there isn't: just enough strip to hold the caret.</summary>
        private const float FooterCaretRowHeight = 44f;

        /// <summary>
        /// Whether the footer has anything live to report.
        ///
        /// Without a listing there is no countdown, no seat count and nothing for Refresh now to
        /// refresh - the bar was three dashes and a disabled button taking 56px of the preset list
        /// to say "nothing is happening", which the empty strip says by itself.
        /// </summary>
        private bool FooterHasLiveState()
            => pfAutomation.GetSnapshot(ImGui.GetFrameCount()).IsRecruiting;

        private float GetFooterHeight()
        {
            float h = FooterHasLiveState() ? FooterBarHeight : FooterCaretRowHeight;

            if (!config.FooterExpanded)
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
            dl.AddRectFilled(new Vector2(winX, origin.Y),
                new Vector2(winX + width, origin.Y + stripHeight),
                ImGui.ColorConvertFloat4ToU32(Panel));
            dl.AddRectFilled(new Vector2(winX, origin.Y), new Vector2(winX + width, origin.Y + 2f),
                ImGui.ColorConvertFloat4ToU32(RuleStrong));

            bool live = FooterHasLiveState();
            float barHeight = live ? FooterBarHeight : FooterCaretRowHeight;

            if (live)
                DrawFooterBar(dl, winX, origin.Y, width);
            else
                DrawFooterCaretRow(winX, origin.Y, width);

            if (config.FooterExpanded)
                DrawFooterSettings(winX, origin.Y + barHeight + 10f, width);
        }

        /// <summary>The always-visible line: mark, duty, live status, Refresh now, and the caret.</summary>
        private void DrawFooterBar(ImDrawListPtr dl, float winX, float top, float width)
        {
            var snap = pfAutomation.GetSnapshot(ImGui.GetFrameCount());

            const float mark = 26f;
            float midY = top + FooterBarHeight * 0.5f;

            // The mark, so the strip reads as the plugin's own rather than as part of the list.
            var markMin = new Vector2(winX + 12f, midY - mark * 0.5f);
            dl.AddRectFilled(markMin, new Vector2(markMin.X + mark, markMin.Y + mark),
                ImGui.ColorConvertFloat4ToU32(Accent));
            using (pluginInterface.UiBuilder.IconFontHandle.Push())
            {
                string glyph = LogoIcon.ToIconString();
                Vector2 gs = ImGui.CalcTextSize(glyph);
                dl.AddText(new Vector2(markMin.X + (mark - gs.X) * 0.5f, markMin.Y + (mark - gs.Y) * 0.5f),
                    ImGui.ColorConvertFloat4ToU32(OnAccent), glyph);
            }

            // Right-hand controls first: the text has to know how much room is left before it can
            // be fitted, and a duty name is the one thing here long enough to need the answer.
            const float caretW = ButtonHeight;
            float refreshW = 128f;
            float caretX = winX + width - 12f - caretW;
            float refreshX = caretX - 8f - refreshW;

            float textX = markMin.X + mark + 12f;
            float textRoom = MathF.Max(80f, refreshX - 12f - textX);

            string title = snap.IsRecruiting && !string.IsNullOrEmpty(snap.DutyName)
                ? snap.DutyName
                : snap.InParty ? "In a party" : "Not recruiting";

            using (UiNameFont.Push())
                dl.AddText(new Vector2(textX, top + 10f),
                    ImGui.ColorConvertFloat4ToU32(Ink), Fit(title, textRoom));

            DrawFooterStatusLine(dl, snap, textX, top + 30f, textRoom);

            // Refresh now: the listing's own action, and the only reason to look at this strip in
            // a hurry. Disabled rather than hidden when there is nothing up, so the bar keeps its
            // shape while a listing comes and goes.
            bool canRefresh = snap.IsLeader;
            ImGui.SetCursorScreenPos(new Vector2(refreshX, midY - ButtonHeight * 0.5f));
            ImGui.BeginDisabled(!canRefresh);
            if (DrawPrimaryButton("Refresh now##FooterRefreshNow", new Vector2(refreshW, ButtonHeight)))
                pfAutomation.ExecuteRefreshTask();
            ImGui.EndDisabled();
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                PaddedTooltip(canRefresh
                    ? "Re-post your listing now, and restart the timer."
                    : "Only the party leader can refresh the listing.");

            // The caret, which is the whole reason the settings can be out of the way.
            DrawFooterCaret(caretX, midY - ButtonHeight * 0.5f, caretW);
        }

        /// <summary>
        /// The footer with no listing up: the caret, and a line saying what the settings are for.
        ///
        /// The strip does not disappear entirely, because the two switches behind the caret are
        /// exactly what somebody sets *before* they start recruiting.
        /// </summary>
        private void DrawFooterCaretRow(float winX, float top, float width)
        {
            float midY = top + FooterCaretRowHeight * 0.5f;
            var dl = ImGui.GetWindowDrawList();

            using (UiBodyFont.Push())
            {
                string label = config.AutoRefresherEnabled && !IsRecruitmentRefresherActive()
                    ? "Auto-refresh is on. It starts when your listing goes up."
                    : "Recruitment settings";

                float lineH = ImGui.GetTextLineHeight();
                dl.AddText(new Vector2(winX + 12f, midY - lineH * 0.5f),
                    ImGui.ColorConvertFloat4ToU32(Faint),
                    Fit(label, width - 24f - ButtonHeight - 12f));
            }

            DrawFooterCaret(winX + width - 12f - ButtonHeight, midY - ButtonHeight * 0.5f,
                ButtonHeight);
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

                if (!config.AutoRefresherEnabled || IsRecruitmentRefresherActive())
                    return;

                const string joiner = " · refresh in ";
                dl.AddText(new Vector2(cursor, y), dim, joiner);
                cursor += ImGui.CalcTextSize(joiner).X;

                (string text, Vector4 colour) = FooterCountdown();
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
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, ColorFromHex("#1c2230"));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, ColorFromHex("#243a54"));
            ImGui.PushStyleColor(ImGuiCol.Text, AccentBlue);
            ImGui.PushStyleColor(ImGuiCol.Border, BorderDefault);
            ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 1f);
            ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 0f);
            ImGui.Button($"{label}##chip_{id}", size);
            ImGui.PopStyleVar(2);
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
