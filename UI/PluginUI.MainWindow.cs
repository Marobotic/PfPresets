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
        private bool isMinimized = false;
        private bool shouldRestoreSize = false;
        private int restoreFramesDelay = 0;
        private string searchQuery = string.Empty;
        private string hoveredPresetId = string.Empty;
        private int hoveredHeaderBtn = 0;

        /// <summary>Preset whose private-party PIN is currently revealed, or empty. Held only while
        /// the mouse is down on it, and never persisted.</summary>
        private string revealedPinId = string.Empty;

        private void DrawMainWindow()
        {
            if (!isMainWindowVisible)
                return;

            if (isMinimized)
            {
                ImGui.SetNextWindowSizeConstraints(new Vector2(480, 52), new Vector2(900, 52));
                ImGui.SetNextWindowSize(new Vector2(config.PanelWidth, 52), ImGuiCond.Always);
            }
            else if (shouldRestoreSize)
            {
                ImGui.SetNextWindowSizeConstraints(new Vector2(480, 200), new Vector2(900, 900));
                ImGui.SetNextWindowSize(new Vector2(config.PanelWidth, config.PanelHeight), ImGuiCond.Always);
                shouldRestoreSize = false;
                restoreFramesDelay = 3;
            }
            else
            {
                ImGui.SetNextWindowSizeConstraints(new Vector2(480, 200), new Vector2(900, 900));
            }

            ImGui.PushStyleColor(ImGuiCol.WindowBg, BgOuter);
            ImGui.PushStyleColor(ImGuiCol.Border, BorderDefault);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 1.0f);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 8.0f);
            ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 4.0f);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(0, 0));
            ImGui.PushStyleVar(ImGuiStyleVar.WindowMinSize, new Vector2(480, 52));

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

                        DrawTitleBar();

                        if (!isMinimized)
                        {
#if PFP_RATINGS
                            DrawNavStrip();

                            if (activeTab == MainTab.Ratings && config.RatingsEnabled)
                            {
                                DrawRatingsTab();
                            }
                            else if (activeTab == MainTab.Settings)
                            {
                                DrawSettingsTab();
                            }
                            else
#endif
                            {
                                // Built once per frame and shared by the card and the list's height
                                // reservation, so both agree on how much space it takes.
                                var snapshot = pfAutomation.GetSnapshot(ImGui.GetFrameCount());

                                // Inside a duty the card has nothing true to say: there is no
                                // listing to end, no one to recruit, and none of its actions apply.
                                // The party list stands alone in there instead.
                                bool showCard = !pfAutomation.IsInDuty();

                                // Advanced once, before measuring, so both passes agree on how far
                                // through the expand animation this frame is.
                                UpdatePartyExpand(snapshot);
                                float statusHeight = showCard ? MeasureStatusCard(snapshot) : 0f;

                                DrawSearchBar();

                                // Recruitment card, party list and presets scroll together as one
                                // region. They used to be siblings, with the preset list sizing
                                // itself from whatever space was left - so once the card and the
                                // party list were both on screen there was nothing left and the
                                // presets were simply cut off with no way to reach them.
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
            if (currentWidth != config.PanelWidth && currentWidth >= 320 && currentWidth <= 600)
            {
                config.PanelWidth = currentWidth;
                sizeChanged = true;
            }
            if (currentHeight != config.PanelHeight && currentHeight >= 200 && currentHeight <= 900)
            {
                config.PanelHeight = currentHeight;
                sizeChanged = true;
            }
            if (sizeChanged)
                config.Save();
        }

        private void DrawTitleBar()
        {
            Vector2 cursorScreenPos = ImGui.GetCursorScreenPos();
            float width = ImGui.GetWindowWidth();

            // Logo box
            Vector2 logoBoxMin = new Vector2(cursorScreenPos.X + 12, cursorScreenPos.Y + 10);
            Vector2 logoBoxMax = new Vector2(logoBoxMin.X + 32, logoBoxMin.Y + 32);
            ImGui.GetWindowDrawList().AddRectFilled(logoBoxMin, logoBoxMax, ImGui.ColorConvertFloat4ToU32(ColorFromHex("#1e2a40")), 6.0f);

            ImGui.PushFont(UiBuilder.IconFont);
            string logoIcon = FontAwesomeIcon.ClipboardList.ToIconString();
            Vector2 iconSize = ImGui.CalcTextSize(logoIcon);
            Vector2 iconPos = new Vector2(logoBoxMin.X + (32 - iconSize.X) / 2f, logoBoxMin.Y + (32 - iconSize.Y) / 2f);
            ImGui.SetCursorScreenPos(iconPos);
            ImGui.TextColored(AccentBlue, logoIcon);
            ImGui.PopFont();

            ImGui.SetCursorScreenPos(new Vector2(cursorScreenPos.X + 52, cursorScreenPos.Y + 11));
            ImGui.TextColored(TextPrimary, "PF Presets");

            ImGui.SetCursorScreenPos(new Vector2(cursorScreenPos.X + 52, cursorScreenPos.Y + 27));
            ImGui.TextColored(TextMuted, VersionLabel);

            float buttonsStart = width - 114;
            ImGui.PushStyleColor(ImGuiCol.Button, BgCard);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, ColorFromHex("#1c2230"));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, ColorFromHex("#243a54"));
            ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 1.0f);
            ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 6.0f);

            // Ko-fi support (red). Sized to fit the heart glyph + label; anchored so its right edge
            // keeps the 6px gap before Minimize and it grows leftward. The label mixes the icon font
            // (heart) with the normal font (text), which a plain Button can't do, so the button is
            // drawn empty and DrawIconLabelCentered paints the heart + text on top.
            const string kofiLabel = "Support me on Ko-Fi";
            float kofiHeartW;
            using (pluginInterface.UiBuilder.IconFontHandle.Push())
                kofiHeartW = ImGui.CalcTextSize(FontAwesomeIcon.Heart.ToIconString()).X;
            float kofiW = kofiHeartW + 8f + ImGui.CalcTextSize(kofiLabel).X + 24f;
            float kofiRightEdge = buttonsStart + 30f; // matches the previous 52px button's right edge
            Vector2 kofiPos = new Vector2(cursorScreenPos.X + kofiRightEdge - kofiW, cursorScreenPos.Y + 11);
            Vector2 kofiSize = new Vector2(kofiW, 30);
            ImGui.SetCursorScreenPos(kofiPos);
            ImGui.PushStyleColor(ImGuiCol.Button, AccentRed);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, ColorFromHex("#e8806f"));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, ColorFromHex("#c75446"));
            ImGui.PushStyleColor(ImGuiCol.Border, hoveredHeaderBtn == 1 ? BorderHover : BorderDefault);
            if (ImGui.Button("##HeaderKofi", kofiSize))
                Dalamud.Utility.Util.OpenLink("https://ko-fi.com/marobotic");
            ImGui.PopStyleColor(4);
            if (ImGui.IsItemHovered()) hoveredHeaderBtn = 1;
            DrawIconLabelCentered(FontAwesomeIcon.Heart, kofiLabel, kofiPos, kofiSize, TextPrimary);

            // Minimize
            ImGui.SetCursorScreenPos(new Vector2(cursorScreenPos.X + buttonsStart + 36, cursorScreenPos.Y + 13));
            ImGui.PushStyleColor(ImGuiCol.Border, hoveredHeaderBtn == 2 ? BorderHover : BorderDefault);
            ImGui.PushStyleColor(ImGuiCol.Text, hoveredHeaderBtn == 2 ? TextPrimary : TextSecondary);
            ImGui.PushFont(UiBuilder.IconFont);
            if (ImGui.Button($"{(isMinimized ? FontAwesomeIcon.Expand.ToIconString() : FontAwesomeIcon.Minus.ToIconString())}##HeaderMinimize", new Vector2(30, 26)))
            {
                isMinimized = !isMinimized;
                if (!isMinimized) shouldRestoreSize = true;
            }
            ImGui.PopFont();
            ImGui.PopStyleColor(2);
            if (ImGui.IsItemHovered()) { hoveredHeaderBtn = 2; PaddedTooltip(isMinimized ? "Restore" : "Minimize"); }

            // Close
            ImGui.SetCursorScreenPos(new Vector2(cursorScreenPos.X + buttonsStart + 72, cursorScreenPos.Y + 13));
            ImGui.PushStyleColor(ImGuiCol.Border, hoveredHeaderBtn == 3 ? BorderHover : BorderDefault);
            ImGui.PushStyleColor(ImGuiCol.Text, hoveredHeaderBtn == 3 ? TextPrimary : TextSecondary);
            ImGui.PushFont(UiBuilder.IconFont);
            if (ImGui.Button($"{FontAwesomeIcon.Times.ToIconString()}##HeaderClose", new Vector2(30, 26)))
                isMainWindowVisible = false;
            ImGui.PopFont();
            ImGui.PopStyleColor(2);
            if (ImGui.IsItemHovered()) { hoveredHeaderBtn = 3; PaddedTooltip("Close"); }

            if (!ImGui.IsAnyItemHovered()) hoveredHeaderBtn = 0;

            ImGui.PopStyleVar(2);
            ImGui.PopStyleColor(3);

            Vector2 winPos = ImGui.GetWindowPos();
            ImGui.GetWindowDrawList().AddLine(new Vector2(winPos.X, cursorScreenPos.Y + 52), new Vector2(winPos.X + width, cursorScreenPos.Y + 52), ImGui.ColorConvertFloat4ToU32(BorderDefault), 1.0f);
            ImGui.SetCursorScreenPos(new Vector2(winPos.X + 8, cursorScreenPos.Y + 58));
        }

        private void DrawSearchBar()
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
            ImGui.InputTextWithHint("##SearchPresets", "Search presets...", ref searchQuery, 128);
            ImGui.PopStyleVar(2);
            ImGui.PopStyleColor(2);

            ImGui.PopStyleVar(); // FramePadding

            float divY = curPos.Y + 50;
            ImGui.GetWindowDrawList().AddLine(new Vector2(ImGui.GetWindowPos().X, divY), new Vector2(ImGui.GetWindowPos().X + width, divY), ImGui.ColorConvertFloat4ToU32(BorderDefault), 1.0f);
            ImGui.SetCursorScreenPos(new Vector2(ImGui.GetWindowPos().X + 8, curPos.Y + 56));
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
                    DrawPresetCard(preset);
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

        private void DrawPresetCard(PfPresetData preset)
        {
            bool isHovered = (hoveredPresetId == preset.Id);
            float cardWidth = ImGui.GetContentRegionAvail().X;
            bool hasComment = !string.IsNullOrEmpty(preset.Comment);
            float cardHeight = hasComment ? 150f : 110f;

            ImGui.PushStyleColor(ImGuiCol.ChildBg, BgCard);
            ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 8.0f);
            ImGui.PushStyleVar(ImGuiStyleVar.ChildBorderSize, 1.0f);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(0, 0));
            ImGui.PushStyleColor(ImGuiCol.Border, isHovered ? BorderHover : BorderDefault);

            if (ImGui.BeginChild($"PresetCard_{preset.Id}", new Vector2(cardWidth, cardHeight), true, ImGuiWindowFlags.NoScrollbar))
            {
                Vector2 cardPos = ImGui.GetWindowPos();
                float cw = ImGui.GetWindowWidth();
                var dl = ImGui.GetWindowDrawList();

                Vector2 mousePos = ImGui.GetMousePos();
                bool cardHovered = mousePos.X >= cardPos.X && mousePos.X <= cardPos.X + cw &&
                                   mousePos.Y >= cardPos.Y && mousePos.Y <= cardPos.Y + cardHeight &&
                                   ImGui.IsWindowHovered(ImGuiHoveredFlags.AllowWhenBlockedByActiveItem);
                if (cardHovered)
                {
                    hoveredPresetId = preset.Id;
                    ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                    ImGui.PushClipRect(cardPos, new Vector2(cardPos.X + cw, cardPos.Y + cardHeight), true);
                    dl.AddRectFilled(cardPos, new Vector2(cardPos.X + cw, cardPos.Y + cardHeight), ImGui.ColorConvertFloat4ToU32(ColorFromHex("#ffffff06")), 8.0f);
                    ImGui.PopClipRect();
                }

                const float leftX = 16f;
                const float rightW = 154f;
                float rightEdge = cardPos.X + cw - 12f;
                float rightX = rightEdge - rightW;
                float leftMaxX = rightX - 10f;
                uint accentU = ImGui.ColorConvertFloat4ToU32(AccentBlue);

                // ── Name bubble (top-left) ──
                {
                    string name = string.IsNullOrEmpty(preset.Name) ? "Unnamed" : preset.Name;
                    Vector2 nts = ImGui.CalcTextSize(name);
                    const float bubbleH = 22f;
                    float bubbleW = MathF.Min(nts.X + 18f, MathF.Max(60f, leftMaxX - cardPos.X - leftX));
                    Vector2 bp = new Vector2(cardPos.X + leftX, cardPos.Y + 12f);
                    Vector2 bpe = new Vector2(bp.X + bubbleW, bp.Y + bubbleH);
                    dl.AddRectFilled(bp, bpe, ImGui.ColorConvertFloat4ToU32(new Vector4(AccentBlue.X, AccentBlue.Y, AccentBlue.Z, 0.18f)), 6f);
                    dl.AddRect(bp, bpe, ImGui.ColorConvertFloat4ToU32(new Vector4(AccentBlue.X, AccentBlue.Y, AccentBlue.Z, 0.45f)), 6f, ImDrawFlags.None, 1f);
                    dl.PushClipRect(bp, bpe, true);
                    dl.AddText(new Vector2(bp.X + 9f, bp.Y + (bubbleH - nts.Y) * 0.5f), ImGui.ColorConvertFloat4ToU32(TextPrimary), name);
                    dl.PopClipRect();

                    // Private-party password, masked. Shown in the clear only while you hold the
                    // click on it - awkward to read over someone's shoulder, and impossible to
                    // leave revealed by accident, which a toggle wouldn't be.
                    if (preset.FormPrivateParty)
                    {
                        float lx = bpe.X + 10f;
                        DrawGlyphAt(FontAwesomeIcon.Lock, new Vector2(lx, bp.Y + (bubbleH - 15f) * 0.5f), 15f, AccentYellow);

                        bool revealed = revealedPinId == preset.Id;
                        string shown = revealed ? preset.PasswordDisplay : "••••";

                        Vector2 pwts = ImGui.CalcTextSize(shown);
                        var pinMin = new Vector2(lx + 19f, bp.Y + (bubbleH - pwts.Y) * 0.5f);
                        dl.AddText(pinMin, ImGui.ColorConvertFloat4ToU32(revealed ? TextPrimary : TextMuted), shown);

                        // Hit area over the masked text. Drawn with the draw list, so the click has
                        // to be tested by hand rather than with an ImGui button.
                        var pinMax = new Vector2(pinMin.X + Math.Max(pwts.X, 30f), pinMin.Y + pwts.Y);
                        if (IsMouseOver(new Vector2(lx, bp.Y), new Vector2(pinMax.X + 2f, bp.Y + bubbleH)))
                        {
                            if (ImGui.IsMouseDown(ImGuiMouseButton.Left))
                                revealedPinId = preset.Id;
                            else if (revealedPinId == preset.Id)
                                revealedPinId = string.Empty;

                            if (!revealed)
                                PaddedTooltip("Hold to show");
                        }
                        else if (revealed)
                        {
                            revealedPinId = string.Empty;
                        }
                    }
                }

                // ── Duty (category icon + name in blue) ──
                {
                    float dy = cardPos.Y + 42f;
                    float tx = cardPos.X + leftX;
                    uint cicon = GetCategoryIcon(preset.DutyCategoryId);
                    if (cicon != 0 && TryGetIconHandle(cicon, out var ch))
                    {
                        dl.AddImage(ch, new Vector2(tx, dy), new Vector2(tx + 18f, dy + 18f));
                        tx += 24f;
                    }
                    Vector2 dts = ImGui.CalcTextSize(preset.DutyName);
                    dl.PushClipRect(new Vector2(cardPos.X + leftX, dy), new Vector2(leftMaxX, dy + 18f), true);
                    dl.AddText(new Vector2(tx, dy + (18f - dts.Y) * 0.5f), accentU, preset.DutyName);
                    dl.PopClipRect();
                }

                // ── Tags (objective / completion status / one-player) ──
                {
                    float ty = cardPos.Y + 66f;
                    float tx = cardPos.X + leftX;
                    dl.PushClipRect(new Vector2(cardPos.X + leftX, ty), new Vector2(leftMaxX, ty + 18f), true);
                    if (preset.ObjectiveId != 0)
                        tx += DrawTagPill(new Vector2(tx, ty), DisplayNames.GetObjectiveName(preset.ObjectiveId), GetObjectiveColor(preset.ObjectiveId)) + 5f;
                    if (preset.CompletionStatusEnabled)
                        tx += DrawTagPill(new Vector2(tx, ty), DisplayNames.GetCompletionStatusName(preset.CompletionStatusType), TextMuted) + 5f;
                    if (preset.OnePlayerPerJob)
                        DrawTagPill(new Vector2(tx, ty), "One Player per Job", TextMuted);
                    dl.PopClipRect();
                }

                // ── Comment (white, wrapped) ──
                if (hasComment)
                {
                    ImGui.SetCursorScreenPos(new Vector2(cardPos.X + leftX, cardPos.Y + 90f));
                    // PushTextWrapPos expects a window-local X, but leftMaxX is a screen
                    // coordinate - passing it directly meant the comment never wrapped and ran
                    // under the Apply button. Convert to local and cap the width at 275px so
                    // long comments wrap cleanly regardless of window width.
                    float commentWrapX = MathF.Min(leftMaxX - cardPos.X, leftX + 275f);
                    ImGui.PushTextWrapPos(commentWrapX);
                    ImGui.TextColored(TextPrimary, preset.Comment);
                    ImGui.PopTextWrapPos();
                }

                // ── Right column: composition icons ──
                // For Auto-Adjust presets, show the game's auto-sought comp.
                const float misz = 16f, mgap = 2f;
                float ix = rightX;
                float iy = cardPos.Y + 14f;
                if (preset.AutoAdjustRoles)
                {
                    var autoSlots = pfAutomation.GetAutoAdjustedSlots();
                    int n = Math.Min(autoSlots.Count, 8);
                    for (int s = 0; s < n; s++)
                    {
                        DrawAutoSlotMiniIcon(autoSlots[s].Role, autoSlots[s].JobId, new Vector2(ix, iy), misz);
                        ix += misz + mgap;
                    }
                }
                else
                {
                    int show = Math.Min(preset.Slots.Count, 8);
                    for (int s = 0; s < show; s++)
                    {
                        DrawSlotMiniIcon(preset.Slots[s], new Vector2(ix, iy), misz);
                        ix += misz + mgap;
                    }
                }
                // Loot rule.
                dl.AddText(new Vector2(rightX, cardPos.Y + 40f), ImGui.ColorConvertFloat4ToU32(TextSecondary), "Loot:");
                float lootValX = rightX + ImGui.CalcTextSize("Loot:").X + 6f;
                dl.AddText(new Vector2(lootValX, cardPos.Y + 40f), ImGui.ColorConvertFloat4ToU32(TextPrimary), DisplayNames.GetLootRuleName(preset.LootRules));

                // ── Apply Preset button + kebab menu (bottom-right) ──
                const float btnH = 30f, kebabW = 30f, btnGap = 6f;
                float applyW = rightW - kebabW - btnGap;
                float btnRowY = cardPos.Y + cardHeight - btnH - 8f;
                bool canRecruit = CanRecruitCached(out var reason);
                // When a non-battle job (crafter/gatherer) is in the party, applying a battle-duty
                // listing makes the game raise a composition warning. Recruiting still works (the
                // plugin auto-confirms it), so the button turns yellow as a heads-up rather than
                // being disabled.
                bool compWarn = canRecruit && PartyHasNonBattleJobCached();

                Vector2 applyPos = new Vector2(rightX, btnRowY);
                Vector4 applyBg = !canRecruit ? ColorFromHex("#3a4456") : (compWarn ? AccentYellow : JsOkBg);
                Vector4 applyHover = !canRecruit ? applyBg : (compWarn ? ColorFromHex("#e8a91e") : JsOkHover);
                Vector4 applyText = !canRecruit ? new Vector4(0.85f, 0.89f, 0.96f, 0.45f) : (compWarn ? ColorFromHex("#3a2c00") : JsOkText);
                ImGui.SetCursorScreenPos(applyPos);
                ImGui.PushStyleColor(ImGuiCol.Button, applyBg);
                ImGui.PushStyleColor(ImGuiCol.ButtonHovered, applyHover);
                ImGui.PushStyleColor(ImGuiCol.ButtonActive, applyHover);
                ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 6f);
                bool applyClicked = ImGui.Button($"##apply_{preset.Id}", new Vector2(applyW, btnH));
                ImGui.PopStyleVar();
                ImGui.PopStyleColor(3);
                if (applyClicked && canRecruit) pfAutomation.ApplyPreset(preset);
                if (ImGui.IsItemHovered())
                {
                    if (!canRecruit)
                        PaddedTooltip($"Cannot recruit: {reason}");
                    else if (compWarn)
                        PaddedTooltip("A non-battle job (crafter/gatherer) is in your party.\nThe game will warn about party composition - the listing still\nposts, and PF Presets confirms the warning for you.");
                }

                // Play icon + label drawn together as a centered group (no overlap).
                {
                    const string label = "Apply Preset";
                    string playStr = FontAwesomeIcon.Play.ToIconString();
                    Vector2 lts = ImGui.CalcTextSize(label);
                    Vector2 pts;
                    using (pluginInterface.UiBuilder.IconFontHandle.Push()) pts = ImGui.CalcTextSize(playStr);
                    const float iconGap = 7f;
                    float gx = applyPos.X + (applyW - (pts.X + iconGap + lts.X)) * 0.5f;
                    float cy = applyPos.Y + btnH * 0.5f;
                    uint tcol = ImGui.ColorConvertFloat4ToU32(applyText);
                    using (pluginInterface.UiBuilder.IconFontHandle.Push())
                        dl.AddText(new Vector2(gx, cy - pts.Y * 0.5f), tcol, playStr);
                    dl.AddText(new Vector2(gx + pts.X + iconGap, cy - lts.Y * 0.5f), tcol, label);
                }

                Vector2 kebabPos = new Vector2(rightX + applyW + btnGap, btnRowY);
                ImGui.SetCursorScreenPos(kebabPos);
                ImGui.PushStyleColor(ImGuiCol.Button, JsCancelBg);
                ImGui.PushStyleColor(ImGuiCol.ButtonHovered, JsCancelHover);
                ImGui.PushStyleColor(ImGuiCol.ButtonActive, JsCancelHover);
                ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 6f);
                bool kebabClicked = ImGui.Button($"##kebab_{preset.Id}", new Vector2(kebabW, btnH));
                ImGui.PopStyleVar();
                ImGui.PopStyleColor(3);
                DrawGlyphCentered(FontAwesomeIcon.EllipsisV, kebabPos, new Vector2(kebabPos.X + kebabW, kebabPos.Y + btnH), TextSecondary);
                if (kebabClicked) ImGui.OpenPopup($"presetmenu_{preset.Id}");

                ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(6, 6));
                if (ImGui.BeginPopup($"presetmenu_{preset.Id}"))
                {
                    if (ImGui.Selectable("  Edit")) OpenEditor(preset, false);
                    // Safe to mutate config.Presets here: DrawPresetList iterates a snapshot.
                    if (ImGui.Selectable("  Duplicate")) config.DuplicatePreset(preset.Id);
                    if (ImGui.Selectable("  Share")) OpenShareExport(preset);
                    ImGui.Separator();
                    if (ImGui.Selectable("  Move Up")) config.MovePresetUp(preset.Id);
                    if (ImGui.Selectable("  Move Down")) config.MovePresetDown(preset.Id);
                    ImGui.Separator();
                    ImGui.PushStyleColor(ImGuiCol.Text, AccentRed);
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
            ImGui.EndChild();
            ImGui.PopStyleVar(3);
            ImGui.PopStyleColor(2);

            if (!ImGui.IsAnyItemHovered() && hoveredPresetId == preset.Id)
                hoveredPresetId = string.Empty;
        }

        private float GetFooterHeight()
        {
            float h = 50f; // Create button + padding
            // The Auto Refresher toggle is hidden when the standalone RecruitmentRefresher plugin
            // is doing the refreshing; the interval selector adds a second row when it's enabled.
            if (!IsRecruitmentRefresherActive())
            {
                h += 44f; // auto-refresh checkbox row
                if (config.AutoRefresherEnabled)
                    h += 30f; // interval selector row
            }
            h += 34f; // "auto-adjust locked jobs" checkbox row (always shown)
            return h;
        }

        // Which editable chip (if any) is currently in text-entry mode, and its in-progress value.
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
            ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 6f);
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

        private void DrawFooter()
        {
            Vector2 curPos = ImGui.GetCursorScreenPos();
            float width = ImGui.GetWindowWidth();
            float winX = ImGui.GetWindowPos().X;
            ImGui.GetWindowDrawList().AddLine(new Vector2(winX, curPos.Y), new Vector2(winX + width, curPos.Y), ImGui.ColorConvertFloat4ToU32(BorderDefault), 1.0f);

            // Auto Refresher toggle - shown above the Create button, but only when the
            // standalone RecruitmentRefresher plugin is NOT active. If that plugin is
            // installed and enabled it handles refreshing, so this toggle disappears.
            float buttonY = curPos.Y + 8;
            if (!IsRecruitmentRefresherActive())
            {
                float rowY = curPos.Y + 10;
                ImGui.SetCursorScreenPos(new Vector2(winX + 12, rowY));
                bool autoRefresh = config.AutoRefresherEnabled;
                if (DrawStyledCheckbox("Auto-refresh recruitment##FooterAutoRefresher", ref autoRefresh))
                {
                    config.AutoRefresherEnabled = autoRefresh;
                    config.Save();
                }
                if (ImGui.IsItemHovered())
                    PaddedTooltip("Automatically re-posts your Party Finder listing on a timer.\nThe countdown starts once your Party Finder is up.");
                float afterCheckboxY = ImGui.GetCursorScreenPos().Y;
                buttonY = afterCheckboxY + 8;

                if (autoRefresh)
                {
                    var dl = ImGui.GetWindowDrawList();

                    // Countdown to the next auto-refresh, right-aligned on the toggle's row.
                    // It only ticks while a Party Finder is up (preset-made or manual), mirroring
                    // the refresher itself; otherwise it shows a dormant "--:--".
                    string timerText;
                    Vector4 timerColor;
                    if (pfAutomation.IsRefreshTimerRunning)
                    {
                        double secs = pfAutomation.SecondsUntilNextRefresh;
                        timerText = $"{(int)(secs / 60):D2}:{(int)(secs % 60):D2}";
                        timerColor = AccentBlue;
                    }
                    else if (pfAutomation.HasReachedMaxDuration)
                    {
                        // The cap ended refreshing; the listing is still up but is no longer renewed.
                        timerText = "stopped";
                        timerColor = AccentYellow;
                    }
                    else
                    {
                        timerText = "--:--";
                        timerColor = TextMuted;
                    }
                    float centerY = rowY + ImGui.GetFrameHeight() * 0.5f;
                    Vector2 tsz = ImGui.CalcTextSize(timerText);
                    const float clockSz = 13f, gap = 5f;
                    float clockStartX = winX + width - 12f - (clockSz + gap + tsz.X);
                    DrawGlyphAt(FontAwesomeIcon.Clock, new Vector2(clockStartX, centerY - clockSz * 0.5f), clockSz, timerColor);
                    dl.AddText(new Vector2(clockStartX + clockSz + gap, centerY - tsz.Y * 0.5f),
                        ImGui.ColorConvertFloat4ToU32(timerColor), timerText);

                    // Interval + duration-cap row. Both values are free-form: the chip shows the
                    // number and double-clicking it turns the chip into a text field.
                    const float chipH = 22f, chipW = 62f;
                    float intervalRowY = afterCheckboxY + 2f;
                    float lblY = intervalRowY + (chipH - ImGui.GetTextLineHeight()) * 0.5f;
                    uint labelCol = ImGui.ColorConvertFloat4ToU32(TextSecondary);

                    const string everyLabel = "Refresh every";
                    dl.AddText(new Vector2(winX + 12, lblY), labelCol, everyLabel);
                    float intervalChipX = winX + 12 + ImGui.CalcTextSize(everyLabel).X + 8;

                    int interval = Math.Clamp(config.AutoRefresherIntervalMinutes,
                        PfAutomation.MinRefreshMinutes, PfAutomation.MaxRefreshMinutes);
                    if (DrawEditableNumberChip(
                            "interval", ref interval, "min", null,
                            PfAutomation.MinRefreshMinutes, PfAutomation.MaxRefreshMinutes,
                            new Vector2(intervalChipX, intervalRowY), new Vector2(chipW, chipH),
                            $"How often to re-post your listing.\nDouble-click to change ({PfAutomation.MinRefreshMinutes}-{PfAutomation.MaxRefreshMinutes} minutes).\nA listing expires after 60 minutes."))
                    {
                        config.AutoRefresherIntervalMinutes = interval;
                        config.Save();
                    }

                    const string stopLabel = "Stop after";
                    float stopLabelX = intervalChipX + chipW + 14;
                    dl.AddText(new Vector2(stopLabelX, lblY), labelCol, stopLabel);
                    float hoursChipX = stopLabelX + ImGui.CalcTextSize(stopLabel).X + 8;

                    int maxHours = Math.Clamp(config.AutoRefresherMaxHours, 0, PfAutomation.MaxRefreshDurationHours);
                    if (DrawEditableNumberChip(
                            "maxhours", ref maxHours, "h", "Never",
                            0, PfAutomation.MaxRefreshDurationHours,
                            new Vector2(hoursChipX, intervalRowY), new Vector2(chipW, chipH),
                            $"Stop auto-refreshing after this long, so a listing doesn't\nstay up all night unattended. Your listing isn't cancelled -\nit just expires normally.\nDouble-click to change (0 = never stop, max {PfAutomation.MaxRefreshDurationHours}h)."))
                    {
                        config.AutoRefresherMaxHours = maxHours;
                        config.Save();
                    }

                    buttonY = intervalRowY + chipH + 8;
                }
            }

            // Auto-adjust locked jobs toggle. Independent of the Auto Refresher (and of the
            // external RecruitmentRefresher plugin), so it's shown regardless. When a party member
            // leaves while recruiting, any slot locked to a single job is broadened to that job's
            // role so the freed seat is easier to fill.
            ImGui.SetCursorScreenPos(new Vector2(winX + 12, buttonY));
            bool autoAdjust = config.AutoAdjustLockedJobsEnabled;
            if (DrawStyledCheckbox("Automatically adjust 1-slot locked jobs during recruitment##FooterAutoAdjust", ref autoAdjust))
            {
                config.AutoAdjustLockedJobsEnabled = autoAdjust;
                config.Save();
            }
            if (ImGui.IsItemHovered())
                PaddedTooltip("While you're recruiting as party leader, if a member leaves, any Party Finder\nslot locked to a single job is widened to that job's role (e.g. White Mage ->\nregen healers, Viper -> melee) so the seat is easier to fill.");
            buttonY = ImGui.GetCursorScreenPos().Y + 8;

            // Create button, with the Import button sharing its row. Both are disabled while a
            // preset is being created/edited.
            const float importW = 34f, importGap = 6f;
            ImGui.SetCursorScreenPos(new Vector2(ImGui.GetWindowPos().X + 12, buttonY));
            bool editingInProgress = isEditorWindowVisible;
            if (editingInProgress) ImGui.BeginDisabled();
            Vector2 createBtnPos = ImGui.GetCursorScreenPos();
            Vector2 createBtnSize = new Vector2(width - 24 - importW - importGap, 30);
            if (DrawPrimaryButton("##CreatePreset", createBtnSize))
            {
                var newPreset = config.AddPreset();
                OpenEditor(newPreset, true);
            }
            // The label is drawn manually so the icon uses the FontAwesome icon font; a
            // plain Button label renders in the normal font, which lacks the glyph.
            DrawIconLabelCentered(FontAwesomeIcon.PlusCircle, "Create New Preset", createBtnPos, createBtnSize,
                JsOkText, editingInProgress ? 0.5f : 1.0f);
            bool createHovered = ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled);

            Vector2 importPos = new Vector2(createBtnPos.X + createBtnSize.X + importGap, buttonY);
            ImGui.SetCursorScreenPos(importPos);
            if (DrawSecondaryButton("##ImportPreset", new Vector2(importW, 30)))
                OpenShareImport();
            DrawGlyphCentered(FontAwesomeIcon.FileImport, importPos,
                new Vector2(importPos.X + importW, importPos.Y + 30), JsText);
            bool importHovered = ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled);
            if (editingInProgress) ImGui.EndDisabled();

            if (editingInProgress && (createHovered || importHovered))
                PaddedTooltip("Finish or close the current preset first.");
            else if (importHovered)
                PaddedTooltip("Import a preset from a share code.");
            ImGui.Dummy(new Vector2(0, 12));
        }
    }
}
