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
        private string presetToDeleteId = string.Empty;

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
            if (ImGui.Begin("PfPresets##Main", ref isOpen, flags))
            {
                isMainWindowVisible = isOpen;

                if (!isMinimized)
                    PersistWindowSize();

                DrawTitleBar();

                if (!isMinimized)
                {
                    DrawSearchBar();
                    DrawPresetList();
                    DrawFooter();
                }

                DrawDeleteConfirmModal();
            }
            ImGui.End();
            ImGui.PopStyleVar(5);
            ImGui.PopStyleColor(2);
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
            ImGui.TextColored(TextMuted, $"{config.Presets.Count} presets");

            float buttonsStart = width - 114;
            ImGui.PushStyleColor(ImGuiCol.Button, BgCard);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, ColorFromHex("#1c2230"));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, ColorFromHex("#243a54"));
            ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 1.0f);
            ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 6.0f);

            // Ko-fi support (red). Wider than the icon buttons so it reads as a support button;
            // extends leftward to keep the 6px gap before Minimize.
            ImGui.SetCursorScreenPos(new Vector2(cursorScreenPos.X + buttonsStart - 22, cursorScreenPos.Y + 11));
            ImGui.PushStyleColor(ImGuiCol.Button, AccentRed);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, ColorFromHex("#e8806f"));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, ColorFromHex("#c75446"));
            ImGui.PushStyleColor(ImGuiCol.Border, hoveredHeaderBtn == 1 ? BorderHover : BorderDefault);
            ImGui.PushStyleColor(ImGuiCol.Text, TextPrimary);
            ImGui.PushFont(UiBuilder.IconFont);
            if (ImGui.Button($"{FontAwesomeIcon.Heart.ToIconString()}##HeaderKofi", new Vector2(52, 30)))
                Dalamud.Utility.Util.OpenLink("https://ko-fi.com/marobotic");
            ImGui.PopFont();
            ImGui.PopStyleColor(5);
            if (ImGui.IsItemHovered()) { hoveredHeaderBtn = 1; PaddedTooltip("Support me on Ko-fi!"); }

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

        private void DrawPresetList()
        {
            ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(0, 6));
            float heightRemaining = ImGui.GetContentRegionAvail().Y - GetFooterHeight();
            ImGui.SetCursorPosX(8);
            ImGui.BeginChild("PresetListScroll", new Vector2(ImGui.GetWindowWidth() - 16, heightRemaining), false, ImGuiWindowFlags.None);

            var filteredPresets = config.Presets;
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

            ImGui.EndChild();
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
            float cardHeight = hasComment ? 128f : 110f;

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

                    // Private-party password shown next to the name.
                    if (preset.FormPrivateParty)
                    {
                        float lx = bpe.X + 10f;
                        DrawGlyphAt(FontAwesomeIcon.Lock, new Vector2(lx, bp.Y + (bubbleH - 15f) * 0.5f), 15f, AccentYellow);
                        Vector2 pwts = ImGui.CalcTextSize(preset.PasswordDisplay);
                        dl.AddText(new Vector2(lx + 19f, bp.Y + (bubbleH - pwts.Y) * 0.5f),
                            ImGui.ColorConvertFloat4ToU32(TextPrimary), preset.PasswordDisplay);
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

                Vector2 applyPos = new Vector2(rightX, btnRowY);
                Vector4 applyBg = canRecruit ? JsOkBg : ColorFromHex("#3a4456");
                Vector4 applyText = canRecruit ? JsOkText : new Vector4(0.85f, 0.89f, 0.96f, 0.45f);
                ImGui.SetCursorScreenPos(applyPos);
                ImGui.PushStyleColor(ImGuiCol.Button, applyBg);
                ImGui.PushStyleColor(ImGuiCol.ButtonHovered, canRecruit ? JsOkHover : applyBg);
                ImGui.PushStyleColor(ImGuiCol.ButtonActive, canRecruit ? JsOkHover : applyBg);
                ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 6f);
                bool applyClicked = ImGui.Button($"##apply_{preset.Id}", new Vector2(applyW, btnH));
                ImGui.PopStyleVar();
                ImGui.PopStyleColor(3);
                if (applyClicked && canRecruit) pfAutomation.ApplyPreset(preset);
                if (!canRecruit && ImGui.IsItemHovered())
                    PaddedTooltip($"Cannot recruit: {reason}");

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
                    if (ImGui.Selectable("  Duplicate")) config.DuplicatePreset(preset.Id);
                    ImGui.Separator();
                    if (ImGui.Selectable("  Move Up")) config.MovePresetUp(preset.Id);
                    if (ImGui.Selectable("  Move Down")) config.MovePresetDown(preset.Id);
                    ImGui.Separator();
                    ImGui.PushStyleColor(ImGuiCol.Text, AccentRed);
                    if (ImGui.Selectable("  Delete")) presetToDeleteId = preset.Id;
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

        /// <summary>Confirmation dialog shown before a preset is deleted (deletion is
        /// irreversible). Opened whenever <see cref="presetToDeleteId"/> is set.</summary>
        private void DrawDeleteConfirmModal()
        {
            if (string.IsNullOrEmpty(presetToDeleteId))
                return;

            var preset = config.GetPreset(presetToDeleteId);
            if (preset == null)
            {
                presetToDeleteId = string.Empty;
                return;
            }

            const string popupId = "Delete Preset##DeleteConfirm";
            if (!ImGui.IsPopupOpen(popupId))
                ImGui.OpenPopup(popupId);

            var vp = ImGui.GetMainViewport();
            ImGui.SetNextWindowPos(
                new Vector2(vp.WorkPos.X + vp.WorkSize.X * 0.5f, vp.WorkPos.Y + vp.WorkSize.Y * 0.5f),
                ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));

            // The main window uses zero WindowPadding; give the modal real padding.
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(18, 14));
            bool open = true;
            if (ImGui.BeginPopupModal(popupId, ref open, ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoCollapse))
            {
                ImGui.TextColored(TextPrimary, $"Delete \"{preset.Name}\"?");
                ImGui.TextColored(TextMuted, "This cannot be undone.");
                ImGui.Dummy(new Vector2(0, 8));

                ImGui.PushStyleColor(ImGuiCol.Button, AccentRed);
                ImGui.PushStyleColor(ImGuiCol.ButtonHovered, ColorFromHex("#e8806f"));
                ImGui.PushStyleColor(ImGuiCol.ButtonActive, ColorFromHex("#c75446"));
                ImGui.PushStyleColor(ImGuiCol.Text, TextPrimary);
                ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 6f);
                bool confirmed = ImGui.Button("Delete##ConfirmDelete", new Vector2(110, 28));
                ImGui.PopStyleVar();
                ImGui.PopStyleColor(4);
                if (confirmed)
                {
                    config.DeletePreset(presetToDeleteId);
                    presetToDeleteId = string.Empty;
                    ImGui.CloseCurrentPopup();
                }

                ImGui.SameLine(0, 10);
                if (DrawSecondaryButton("Cancel##CancelDelete", new Vector2(110, 28)))
                {
                    presetToDeleteId = string.Empty;
                    ImGui.CloseCurrentPopup();
                }
                ImGui.EndPopup();
            }
            ImGui.PopStyleVar();

            if (!open)
                presetToDeleteId = string.Empty;
        }

        // ══════════════════════════════════════════════════════════
        //  FOOTER (Auto Refresher + Create button)
        // ══════════════════════════════════════════════════════════

        /// <summary>Vertical space the footer needs at the bottom of the main window. The preset
        /// list reserves this much so the footer (Create button, plus the optional Auto Refresher
        /// toggle above it) is never pushed off-screen.</summary>
        private float GetFooterHeight()
        {
            if (IsRecruitmentRefresherActive()) return 50f;
            // The interval selector adds a second row when auto-refresh is enabled.
            return config.AutoRefresherEnabled ? 124f : 94f;
        }

        /// <summary>Small segmented-style toggle chip used to pick the refresh interval.</summary>
        private bool DrawIntervalChip(string label, bool active, Vector2 pos, Vector2 size)
        {
            ImGui.SetCursorScreenPos(pos);
            ImGui.PushStyleColor(ImGuiCol.Button, active ? AccentBlue : BgCard);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, active ? AccentBlue : ColorFromHex("#1c2230"));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, active ? AccentBlue : ColorFromHex("#243a54"));
            ImGui.PushStyleColor(ImGuiCol.Text, active ? ColorFromHex("#0d1117") : TextSecondary);
            ImGui.PushStyleColor(ImGuiCol.Border, active ? AccentBlue : BorderDefault);
            ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 1f);
            ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 6f);
            bool clicked = ImGui.Button($"{label}##interval_{label}", size);
            ImGui.PopStyleVar(2);
            ImGui.PopStyleColor(5);
            return clicked;
        }

        private void DrawFooter()
        {
            Vector2 curPos = ImGui.GetCursorScreenPos();
            float width = ImGui.GetWindowWidth();
            ImGui.GetWindowDrawList().AddLine(new Vector2(ImGui.GetWindowPos().X, curPos.Y), new Vector2(ImGui.GetWindowPos().X + width, curPos.Y), ImGui.ColorConvertFloat4ToU32(BorderDefault), 1.0f);

            // Auto Refresher toggle - shown above the Create button, but only when the
            // standalone RecruitmentRefresher plugin is NOT active. If that plugin is
            // installed and enabled it handles refreshing, so this toggle disappears.
            float buttonY = curPos.Y + 8;
            if (!IsRecruitmentRefresherActive())
            {
                float winX = ImGui.GetWindowPos().X;
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

                    // Interval selector row: 15 / 30 minutes.
                    const float chipH = 22f;
                    float intervalRowY = afterCheckboxY + 2f;
                    float lblY = intervalRowY + (chipH - ImGui.GetTextLineHeight()) * 0.5f;
                    dl.AddText(new Vector2(winX + 12, lblY), ImGui.ColorConvertFloat4ToU32(TextSecondary), "Refresh every");
                    float chipsX = winX + 12 + ImGui.CalcTextSize("Refresh every").X + 10;
                    const float chipW = 58f, chipGap = 6f;
                    int interval = config.AutoRefresherIntervalMinutes == 15 ? 15 : 30;
                    if (DrawIntervalChip("15 min", interval == 15, new Vector2(chipsX, intervalRowY), new Vector2(chipW, chipH)))
                    { config.AutoRefresherIntervalMinutes = 15; config.Save(); }
                    if (DrawIntervalChip("30 min", interval == 30, new Vector2(chipsX + chipW + chipGap, intervalRowY), new Vector2(chipW, chipH)))
                    { config.AutoRefresherIntervalMinutes = 30; config.Save(); }

                    buttonY = intervalRowY + chipH + 8;
                }
            }

            // Create button. Disabled while a preset is being created/edited.
            ImGui.SetCursorScreenPos(new Vector2(ImGui.GetWindowPos().X + 12, buttonY));
            bool editingInProgress = isEditorWindowVisible;
            if (editingInProgress) ImGui.BeginDisabled();
            Vector2 createBtnPos = ImGui.GetCursorScreenPos();
            Vector2 createBtnSize = new Vector2(width - 24, 30);
            if (DrawPrimaryButton("##CreatePreset", createBtnSize))
            {
                var newPreset = config.AddPreset();
                OpenEditor(newPreset, true);
            }
            // The label is drawn manually so the icon uses the FontAwesome icon font; a
            // plain Button label renders in the normal font, which lacks the glyph.
            DrawIconLabelCentered(FontAwesomeIcon.PlusCircle, "Create New Preset", createBtnPos, createBtnSize,
                JsOkText, editingInProgress ? 0.5f : 1.0f);
            if (editingInProgress) ImGui.EndDisabled();
            if (editingInProgress && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                PaddedTooltip("Finish or close the current preset first.");
            ImGui.Dummy(new Vector2(0, 12));
        }
    }
}
