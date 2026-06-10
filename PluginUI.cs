using System;
using System.Text;
using System.Collections.Generic;
using System.Numerics;
using System.Linq;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Newtonsoft.Json;

namespace PfPresets
{
    public class PluginUI
    {
        private readonly IDalamudPluginInterface pluginInterface;
        private readonly Configuration config;
        private readonly DutyDataHelper dutyDataHelper;
        private readonly PfAutomation pfAutomation;
        private readonly ITextureProvider textureProvider;

        // ── Window Visibility ─────────────────────────────────────
        private bool isMainWindowVisible = false;
        private bool isSettingsWindowVisible = false;
        private bool isEditorWindowVisible = false;

        // ── Main Window State ─────────────────────────────────────
        private bool isMinimized = false;
        private bool shouldRestoreSize = false;
        private int restoreFramesDelay = 0;
        private string searchQuery = string.Empty;
        private string hoveredPresetId = string.Empty;
        private int hoveredHeaderBtn = 0;
        private string presetToDeleteId = string.Empty;
        private bool showDeleteConfirm = false;

        // ── Editor State ──────────────────────────────────────────
        private PfPresetData? editingPreset = null;
        private bool isNewPreset = false;
        private string editorPresetName = string.Empty;
        private string editorComment = string.Empty;
        private string editorPassword = string.Empty;
        private string editorPasswordStr = "0000";
        private int editorGroupType = 0;
        private int editorDutyCategoryId = 0;
        private int editorDutyIndex = 0;
        private string editorDutyName = "None";
        private string editorDutyCategoryName = "None";
        private int editorObjectiveId = 0;
        private List<RoleSlot> editorSlots = new();
        private bool editorUnselectClasses = false;
        private bool editorOnePlayerPerJob = false;
        private bool editorRemoveRoleRestrictions = false;
        private bool editorAutoAdjustRoles = false;
        private bool editorLimitToWorld = false;
        private bool editorPrivateParty = false;
        private bool editorCompletionStatus = false;
        private int editorCompletionStatusType = 0;
        private bool editorAvgItemLvEnabled = false;
        private int editorAvgItemLv = 1;
        private bool editorAvgItemLvOrAbove = true;
        private bool editorUnrestrictedParty = false;
        private bool editorMinimumIL = false;
        private bool editorSilenceEcho = false;
        private int editorLootRules = 0;
        private bool editorLangJapanese = true;
        private bool editorLangEnglish = true;
        private bool editorLangGerman = true;
        private bool editorLangFrench = true;

        // ── Duty Selector State ───────────────────────────────────
        private string dutySearchQuery = string.Empty;

        // ── Job Selector State ────────────────────────────────────
        private int jobSelectorSlotIndex = -1;
        private bool showJobSelector = false;
        private RoleType tempSelectorRole;
        private ulong tempSelectorJobFlags;

        // ── Color Palette ─────────────────────────────────────────
        private static readonly Vector4 BgOuter = ColorFromHex("#111318");
        private static readonly Vector4 BgCard = ColorFromHex("#151820");
        private static readonly Vector4 BgCardExpanded = ColorFromHex("#0f1218");
        private static readonly Vector4 BgDropdown = ColorFromHex("#161b24");
        private static readonly Vector4 BorderDefault = ColorFromHex("#1e2430");
        private static readonly Vector4 BorderHover = ColorFromHex("#2e3a50");
        private static readonly Vector4 BorderActiveAccent = ColorFromHex("#4a8fd450");
        private static readonly Vector4 TextPrimary = ColorFromHex("#f0f3f8");
        private static readonly Vector4 TextSecondary = ColorFromHex("#a0aec0");
        private static readonly Vector4 TextMuted = ColorFromHex("#718096");
        private static readonly Vector4 TextHover = ColorFromHex("#cbd5e0");
        private static readonly Vector4 AccentBlue = ColorFromHex("#4a8fd4");
        private static readonly Vector4 AccentGreen = ColorFromHex("#27c93f");
        private static readonly Vector4 AccentRed = ColorFromHex("#ff5f5f");
        private static readonly Vector4 AccentYellow = ColorFromHex("#ffbd2e");
        private static readonly Vector4 AccentPurple = ColorFromHex("#9b6dff");

        // ── Role Colors ───────────────────────────────────────────
        private static readonly Vector4 RoleTank = ColorFromHex("#3752d8");
        private static readonly Vector4 RoleHealer = ColorFromHex("#2e8b57");
        private static readonly Vector4 RoleDPS = ColorFromHex("#c43333");
        private static readonly Vector4 RoleFree = ColorFromHex("#808080");

        public PluginUI(
            IDalamudPluginInterface pluginInterface,
            Configuration config,
            DutyDataHelper dutyDataHelper,
            PfAutomation pfAutomation,
            ITextureProvider textureProvider)
        {
            this.pluginInterface = pluginInterface;
            this.config = config;
            this.dutyDataHelper = dutyDataHelper;
            this.pfAutomation = pfAutomation;
            this.textureProvider = textureProvider;
        }

        public void ToggleMainWindow() => isMainWindowVisible = !isMainWindowVisible;
        public void ToggleSettingsWindow() => isSettingsWindowVisible = !isSettingsWindowVisible;

        public void Draw()
        {
            DrawMainWindow();
            DrawEditorWindow();
            DrawSettingsWindow();
            DrawChecklistOverlay();
            DrawJobSelectorWindow();
        }

        // ═══════════════════════════════════════════════════════════
        //  MAIN WINDOW — Preset List
        // ═══════════════════════════════════════════════════════════

        private void DrawMainWindow()
        {
            if (!isMainWindowVisible)
                return;

            if (isMinimized)
            {
                ImGui.SetNextWindowSizeConstraints(new Vector2(320, 52), new Vector2(600, 52));
                ImGui.SetNextWindowSize(new Vector2(config.PanelWidth, 52), ImGuiCond.Always);
            }
            else if (shouldRestoreSize)
            {
                ImGui.SetNextWindowSizeConstraints(new Vector2(320, 200), new Vector2(600, 900));
                ImGui.SetNextWindowSize(new Vector2(config.PanelWidth, config.PanelHeight), ImGuiCond.Always);
                shouldRestoreSize = false;
                restoreFramesDelay = 3;
            }
            else
            {
                ImGui.SetNextWindowSizeConstraints(new Vector2(320, 200), new Vector2(600, 900));
            }

            ImGui.PushStyleColor(ImGuiCol.WindowBg, BgOuter);
            ImGui.PushStyleColor(ImGuiCol.Border, BorderDefault);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 1.0f);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 8.0f);
            ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 4.0f);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(0, 0));
            ImGui.PushStyleVar(ImGuiStyleVar.WindowMinSize, new Vector2(320, 52));

            ImGuiWindowFlags flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoScrollbar;
            if (isMinimized)
                flags |= ImGuiWindowFlags.NoResize;

            bool isOpen = isMainWindowVisible;
            if (ImGui.Begin("PfPresets##Main", ref isOpen, flags))
            {
                isMainWindowVisible = isOpen;

                if (!isMinimized)
                {
                    if (restoreFramesDelay > 0)
                    {
                        restoreFramesDelay--;
                    }
                    else
                    {
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
                }

                DrawTitleBar();

                if (!isMinimized)
                {
                    DrawSearchBar();
                    DrawPresetList();
                    DrawFooter();
                }
            }
            ImGui.End();
            ImGui.PopStyleVar(5);
            ImGui.PopStyleColor(2);

            // Handle deferred delete
            if (!string.IsNullOrEmpty(presetToDeleteId) && !showDeleteConfirm)
            {
                config.DeletePreset(presetToDeleteId);
                presetToDeleteId = string.Empty;
            }
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

            // Settings
            ImGui.SetCursorScreenPos(new Vector2(cursorScreenPos.X + buttonsStart, cursorScreenPos.Y + 13));
            ImGui.PushStyleColor(ImGuiCol.Border, hoveredHeaderBtn == 1 ? BorderHover : BorderDefault);
            ImGui.PushStyleColor(ImGuiCol.Text, hoveredHeaderBtn == 1 ? TextPrimary : TextSecondary);
            ImGui.PushFont(UiBuilder.IconFont);
            if (ImGui.Button($"{FontAwesomeIcon.Cog.ToIconString()}##HeaderSettings", new Vector2(30, 26)))
                isSettingsWindowVisible = !isSettingsWindowVisible;
            ImGui.PopFont();
            ImGui.PopStyleColor(2);
            if (ImGui.IsItemHovered()) { hoveredHeaderBtn = 1; ImGui.SetTooltip("Settings"); }

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
            if (ImGui.IsItemHovered()) { hoveredHeaderBtn = 2; ImGui.SetTooltip(isMinimized ? "Restore" : "Minimize"); }

            // Close
            ImGui.SetCursorScreenPos(new Vector2(cursorScreenPos.X + buttonsStart + 72, cursorScreenPos.Y + 13));
            ImGui.PushStyleColor(ImGuiCol.Border, hoveredHeaderBtn == 3 ? BorderHover : BorderDefault);
            ImGui.PushStyleColor(ImGuiCol.Text, hoveredHeaderBtn == 3 ? TextPrimary : TextSecondary);
            ImGui.PushFont(UiBuilder.IconFont);
            if (ImGui.Button($"{FontAwesomeIcon.Times.ToIconString()}##HeaderClose", new Vector2(30, 26)))
                isMainWindowVisible = false;
            ImGui.PopFont();
            ImGui.PopStyleColor(2);
            if (ImGui.IsItemHovered()) { hoveredHeaderBtn = 3; ImGui.SetTooltip("Close"); }

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

            ImGui.SetCursorScreenPos(new Vector2(ImGui.GetWindowPos().X + 12, curPos.Y + 4));
            ImGui.PushFont(UiBuilder.IconFont);
            ImGui.TextColored(TextMuted, FontAwesomeIcon.Search.ToIconString());
            ImGui.PopFont();
            ImGui.SameLine(0, 8);

            ImGui.PushStyleColor(ImGuiCol.FrameBg, BgCardExpanded);
            ImGui.PushStyleColor(ImGuiCol.Border, BorderDefault);
            ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 1.0f);
            ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 6.0f);
            ImGui.SetNextItemWidth(width - 80);
            ImGui.InputTextWithHint("##SearchPresets", "Search presets...", ref searchQuery, 128);
            ImGui.PopStyleVar(2);
            ImGui.PopStyleColor(2);

            Vector2 divStart = new Vector2(ImGui.GetWindowPos().X, curPos.Y + 34);
            ImGui.GetWindowDrawList().AddLine(divStart, new Vector2(ImGui.GetWindowPos().X + width, curPos.Y + 34), ImGui.ColorConvertFloat4ToU32(BorderDefault), 1.0f);
            ImGui.SetCursorScreenPos(new Vector2(ImGui.GetWindowPos().X + 8, curPos.Y + 40));
        }

        private void DrawPresetList()
        {
            ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(0, 6));
            float heightRemaining = ImGui.GetContentRegionAvail().Y - 50;
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

        private void DrawPresetCard(PfPresetData preset)
        {
            bool isHovered = (hoveredPresetId == preset.Id);
            float cardWidth = ImGui.GetContentRegionAvail().X;
            bool hasComment = !string.IsNullOrEmpty(preset.Comment);
            float cardHeight = hasComment ? 104f : 72f;

            ImGui.PushStyleColor(ImGuiCol.ChildBg, BgCard);
            ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 8.0f);
            ImGui.PushStyleVar(ImGuiStyleVar.ChildBorderSize, 1.0f);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(0, 0));
            ImGui.PushStyleColor(ImGuiCol.Border, isHovered ? BorderHover : BorderDefault);

            if (ImGui.BeginChild($"PresetCard_{preset.Id}", new Vector2(cardWidth, cardHeight), true, ImGuiWindowFlags.NoScrollbar))
            {
                Vector2 cardPos = ImGui.GetWindowPos();
                float cw = ImGui.GetWindowWidth();

                // Hover detection
                Vector2 mousePos = ImGui.GetMousePos();
                bool cardHovered = mousePos.X >= cardPos.X && mousePos.X <= cardPos.X + cw &&
                                   mousePos.Y >= cardPos.Y && mousePos.Y <= cardPos.Y + cardHeight &&
                                   ImGui.IsWindowHovered(ImGuiHoveredFlags.AllowWhenBlockedByActiveItem);
                
                if (cardHovered)
                {
                    hoveredPresetId = preset.Id;
                    ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                    ImGui.PushClipRect(cardPos, new Vector2(cardPos.X + cw, cardPos.Y + cardHeight), true);
                    ImGui.GetWindowDrawList().AddRectFilled(cardPos, new Vector2(cardPos.X + cw, cardPos.Y + cardHeight), ImGui.ColorConvertFloat4ToU32(ColorFromHex("#ffffff06")), 8.0f);
                    ImGui.PopClipRect();
                }

                // Left role color bar
                Vector4 primaryRoleColor = GetPrimaryRoleColor(preset);
                ImGui.PushClipRect(cardPos, new Vector2(cardPos.X + 4, cardPos.Y + cardHeight), true);
                ImGui.GetWindowDrawList().AddRectFilled(cardPos, new Vector2(cardPos.X + 4, cardPos.Y + cardHeight), ImGui.ColorConvertFloat4ToU32(primaryRoleColor), 8.0f, ImDrawFlags.RoundCornersLeft);
                ImGui.PopClipRect();

                ImGui.SetCursorScreenPos(new Vector2(cardPos.X + 14, cardPos.Y + 8));
                ImGui.TextColored(TextPrimary, preset.Name);

                ImGui.SetCursorScreenPos(new Vector2(cardPos.X + 14, cardPos.Y + 26));
                ImGui.TextColored(AccentBlue, preset.DutyName);

                ImGui.SetCursorScreenPos(new Vector2(cardPos.X + 14, cardPos.Y + 44));
                ImGui.TextColored(TextSecondary, $"{preset.GetRoleSummary()}  •  {PfAutomation.GetObjectiveName(preset.ObjectiveId)}");

                if (hasComment)
                {
                    ImGui.SetCursorScreenPos(new Vector2(cardPos.X + 14, cardPos.Y + 60));
                    ImGui.PushTextWrapPos(cardPos.X + cw - 14);
                    ImGui.TextColored(TextMuted, preset.Comment);
                    ImGui.PopTextWrapPos();
                }

                if (cardHovered)
                {
                    float btnY = cardPos.Y + 10;
                    float btnX = cardPos.X + cw - 140;

                    // Apply
                    bool canRecruit = pfAutomation.CanRecruit(out var reason);
                    Vector4 playColor = canRecruit ? AccentGreen : TextMuted;
                    string playTooltip = canRecruit ? "Apply Preset" : $"Cannot recruit: {reason}";
                    string? playBg = canRecruit ? "#1a3a2a" : "#222222";
                    string? playBgHover = canRecruit ? "#224832" : "#222222";
                    string? playBgActive = canRecruit ? "#2a5a3c" : "#222222";
                    string? playBorder = canRecruit ? "#27c93f40" : "#ffffff10";

                    DrawCardActionBtn(btnX, btnY, FontAwesomeIcon.Play, playColor, playBg, playBgHover, playBgActive, playBorder, $"Apply_{preset.Id}", playTooltip,
                        () => {
                            if (canRecruit)
                                pfAutomation.ApplyPreset(preset);
                        });
                    // Edit
                    DrawCardActionBtn(btnX + 32, btnY, FontAwesomeIcon.Edit, TextSecondary, null, null, null, null, $"Edit_{preset.Id}", "Edit",
                        () => OpenEditor(preset, false));
                    // Duplicate
                    DrawCardActionBtn(btnX + 64, btnY, FontAwesomeIcon.Copy, TextSecondary, null, null, null, null, $"Dup_{preset.Id}", "Duplicate",
                        () => config.DuplicatePreset(preset.Id));
                    // Delete
                    DrawCardActionBtn(btnX + 96, btnY, FontAwesomeIcon.Trash, AccentRed, "#3a1a1a", "#4a2222", "#5a2a2a", "#ff5f5f40", $"Del_{preset.Id}", "Delete",
                        () => presetToDeleteId = preset.Id);

                    // Move
                    float moveY = cardPos.Y + 44;
                    ImGui.SetCursorScreenPos(new Vector2(btnX + 64, moveY));
                    ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0, 0, 0, 0));
                    ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0, 0, 0, 0));
                    ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0, 0, 0, 0));
                    ImGui.PushStyleColor(ImGuiCol.Text, TextMuted);
                    ImGui.PushFont(UiBuilder.IconFont);
                    if (ImGui.Button($"{FontAwesomeIcon.ArrowUp.ToIconString()}##Up_{preset.Id}", new Vector2(22, 22)))
                        config.MovePresetUp(preset.Id);
                    ImGui.PopFont();
                    ImGui.PopStyleColor(4);
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip("Move Up");

                    ImGui.SameLine(0, 4);
                    ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0, 0, 0, 0));
                    ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0, 0, 0, 0));
                    ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0, 0, 0, 0));
                    ImGui.PushStyleColor(ImGuiCol.Text, TextMuted);
                    ImGui.PushFont(UiBuilder.IconFont);
                    if (ImGui.Button($"{FontAwesomeIcon.ArrowDown.ToIconString()}##Down_{preset.Id}", new Vector2(22, 22)))
                        config.MovePresetDown(preset.Id);
                    ImGui.PopFont();
                    ImGui.PopStyleColor(4);
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip("Move Down");
                }
            }
            ImGui.EndChild();
            ImGui.PopStyleVar(3);
            ImGui.PopStyleColor(2);

            if (!ImGui.IsAnyItemHovered() && hoveredPresetId == preset.Id)
                hoveredPresetId = string.Empty;
        }

        private void DrawCardActionBtn(float x, float y, FontAwesomeIcon icon, Vector4 textColor,
            string? bg, string? bgHover, string? bgActive, string? border, string id, string tooltip, Action onClick)
        {
            ImGui.SetCursorScreenPos(new Vector2(x, y));
            ImGui.PushStyleColor(ImGuiCol.Button, bg != null ? ColorFromHex(bg) : BgCardExpanded);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, bgHover != null ? ColorFromHex(bgHover) : ColorFromHex("#1c2230"));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, bgActive != null ? ColorFromHex(bgActive) : ColorFromHex("#243a54"));
            ImGui.PushStyleColor(ImGuiCol.Text, textColor);
            ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 1.0f);
            ImGui.PushStyleColor(ImGuiCol.Border, border != null ? ColorFromHex(border) : BorderDefault);
            ImGui.PushFont(UiBuilder.IconFont);
            if (ImGui.Button($"{icon.ToIconString()}##{id}", new Vector2(28, 28)))
                onClick();
            ImGui.PopFont();
            ImGui.PopStyleColor(5);
            ImGui.PopStyleVar();
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(tooltip);
        }

        private void DrawFooter()
        {
            Vector2 curPos = ImGui.GetCursorScreenPos();
            float width = ImGui.GetWindowWidth();
            ImGui.GetWindowDrawList().AddLine(new Vector2(ImGui.GetWindowPos().X, curPos.Y), new Vector2(ImGui.GetWindowPos().X + width, curPos.Y), ImGui.ColorConvertFloat4ToU32(BorderDefault), 1.0f);

            ImGui.SetCursorScreenPos(new Vector2(ImGui.GetWindowPos().X + 12, curPos.Y + 8));
            ImGui.PushStyleColor(ImGuiCol.Button, ColorFromHex("#1a2a3a"));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, ColorFromHex("#224050"));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, ColorFromHex("#2a5060"));
            ImGui.PushStyleColor(ImGuiCol.Text, AccentBlue);
            ImGui.PushStyleColor(ImGuiCol.Border, ColorFromHex("#4a8fd440"));
            ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 1.0f);
            ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 6.0f);

            ImGui.PushFont(UiBuilder.IconFont);
            string plusIcon = FontAwesomeIcon.Plus.ToIconString();
            ImGui.PopFont();

            if (ImGui.Button($"  {plusIcon}  Create New Preset##CreatePreset", new Vector2(width - 24, 30)))
            {
                var newPreset = config.AddPreset();
                OpenEditor(newPreset, true);
            }
            ImGui.PopStyleVar(2);
            ImGui.PopStyleColor(5);
            ImGui.Dummy(new Vector2(0, 12));
        }

        // ═══════════════════════════════════════════════════════════
        //  EDITOR WINDOW
        // ═══════════════════════════════════════════════════════════

        private void OpenEditor(PfPresetData preset, bool isNew)
        {
            editingPreset = preset;
            isNewPreset = isNew;
            isEditorWindowVisible = true;

            editorPresetName = preset.Name;
            editorComment = preset.Comment;
            editorPassword = preset.PrivatePartyPassword;
            editorPasswordStr = preset.PrivatePartyPassword;
            editorGroupType = preset.GroupType;
            editorDutyCategoryId = preset.DutyCategoryId;
            editorDutyIndex = preset.DutyIndex;
            editorDutyName = preset.DutyName;
            editorDutyCategoryName = preset.DutyCategoryName;
            editorObjectiveId = preset.ObjectiveId;
            editorSlots = preset.Slots.Select(s => new RoleSlot { SlotIndex = s.SlotIndex, Role = s.Role, AcceptedJobFlags = s.AcceptedJobFlags }).ToList();
            editorUnselectClasses = preset.UnselectClasses;
            editorOnePlayerPerJob = preset.OnePlayerPerJob;
            editorRemoveRoleRestrictions = preset.RemoveRoleRestrictions;
            editorAutoAdjustRoles = preset.AutoAdjustRoles;
            editorLimitToWorld = preset.LimitRecruitingToWorld;
            editorPrivateParty = preset.FormPrivateParty;
            editorCompletionStatus = preset.CompletionStatusEnabled;
            editorCompletionStatusType = preset.CompletionStatusType;
            editorAvgItemLvEnabled = preset.AvgItemLvEnabled;
            editorAvgItemLv = preset.AvgItemLv;
            editorAvgItemLvOrAbove = preset.AvgItemLvOrAbove;
            editorUnrestrictedParty = preset.UnrestrictedParty;
            editorMinimumIL = preset.MinimumIL;
            editorSilenceEcho = preset.SilenceEcho;
            editorLootRules = preset.LootRules;
            editorLangJapanese = preset.LangJapanese;
            editorLangEnglish = preset.LangEnglish;
            editorLangGerman = preset.LangGerman;
            editorLangFrench = preset.LangFrench;
            dutySearchQuery = string.Empty;
            showJobSelector = false;
            jobSelectorSlotIndex = -1;
        }

        private void SaveEditor()
        {
            if (editingPreset == null) return;

            editingPreset.Name = editorPresetName;
            editingPreset.Comment = editorComment;
            editingPreset.PrivatePartyPassword = editorPassword;
            editingPreset.GroupType = editorGroupType;
            editingPreset.DutyCategoryId = editorDutyCategoryId;
            editingPreset.DutyIndex = editorDutyIndex;
            editingPreset.DutyName = editorDutyName;
            editingPreset.DutyCategoryName = editorDutyCategoryName;
            editingPreset.ObjectiveId = editorObjectiveId;
            editingPreset.Slots = editorSlots.Select(s => new RoleSlot { SlotIndex = s.SlotIndex, Role = s.Role, AcceptedJobFlags = s.AcceptedJobFlags }).ToList();
            editingPreset.UnselectClasses = editorUnselectClasses;
            editingPreset.OnePlayerPerJob = editorOnePlayerPerJob;
            editingPreset.RemoveRoleRestrictions = editorRemoveRoleRestrictions;
            editingPreset.AutoAdjustRoles = editorAutoAdjustRoles;
            editingPreset.LimitRecruitingToWorld = editorLimitToWorld;
            editingPreset.FormPrivateParty = editorPrivateParty;
            editingPreset.CompletionStatusEnabled = editorCompletionStatus;
            editingPreset.CompletionStatusType = editorCompletionStatusType;
            editingPreset.AvgItemLvEnabled = editorAvgItemLvEnabled;
            editingPreset.AvgItemLv = editorAvgItemLv;
            editingPreset.AvgItemLvOrAbove = editorAvgItemLvOrAbove;
            editingPreset.UnrestrictedParty = editorUnrestrictedParty;
            editingPreset.MinimumIL = editorMinimumIL;
            editingPreset.SilenceEcho = editorSilenceEcho;
            editingPreset.LootRules = editorLootRules;
            editingPreset.LangJapanese = editorLangJapanese;
            editingPreset.LangEnglish = editorLangEnglish;
            editingPreset.LangGerman = editorLangGerman;
            editingPreset.LangFrench = editorLangFrench;

            config.UpdatePreset(editingPreset);
            isEditorWindowVisible = false;
            editingPreset = null;
            pfAutomation.ClearAutoAdjustCache();
        }

        private void CancelEditor()
        {
            if (isNewPreset && editingPreset != null)
                config.DeletePreset(editingPreset.Id);
            isEditorWindowVisible = false;
            editingPreset = null;
            pfAutomation.ClearAutoAdjustCache();
        }

        private void DrawEditorWindow()
        {
            if (!isEditorWindowVisible || editingPreset == null) return;

            ImGui.SetNextWindowSize(new Vector2(580, 650), ImGuiCond.FirstUseEver);
            ImGui.SetNextWindowSizeConstraints(new Vector2(500, 520), new Vector2(750, 850));

            ImGui.PushStyleColor(ImGuiCol.WindowBg, BgOuter);
            ImGui.PushStyleColor(ImGuiCol.Border, BorderDefault);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 1.0f);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 8.0f);
            ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 4.0f);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(16, 12));

            string title = isNewPreset ? "Create New Preset##PfPresetEditor" : $"Edit: {editingPreset.Name}##PfPresetEditor";
            bool open = isEditorWindowVisible;
            if (ImGui.Begin(title, ref open, ImGuiWindowFlags.NoCollapse))
            {
                if (!open) { CancelEditor(); }
                else { DrawEditorContent(); }
            }
            ImGui.End();
            ImGui.PopStyleVar(4);
            ImGui.PopStyleColor(2);
        }


        private void DrawEditorContent()
        {
            float contentWidth = ImGui.GetContentRegionAvail().X;

            // Preset Name
            DrawSectionLabel("PRESET NAME");
            ImGui.PushStyleColor(ImGuiCol.FrameBg, BgCard);
            ImGui.PushStyleColor(ImGuiCol.Border, BorderDefault);
            ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 1.0f);
            ImGui.SetNextItemWidth(contentWidth);
            ImGui.InputText("##PresetName", ref editorPresetName, 128);
            ImGui.PopStyleVar();
            ImGui.PopStyleColor(2);
            ImGui.Dummy(new Vector2(0, 8));

            float footerHeight = 46;
            float scrollHeight = ImGui.GetContentRegionAvail().Y - footerHeight;
            ImGui.BeginChild("EditorScroll", new Vector2(contentWidth, scrollHeight), false, ImGuiWindowFlags.None);

            float halfWidth = (contentWidth - 12) / 2f;

            // ══ LEFT COLUMN ═══════════════════════════════════════
            ImGui.BeginChild("EditorLeft", new Vector2(halfWidth, 0), false, ImGuiWindowFlags.None);

            // Group Type (Forced to Normal/0)
            editorGroupType = 0;

            // Duty (Category-based)
            DrawSectionLabel("DUTY");
            DrawDutyCategorySelector();
            ImGui.Dummy(new Vector2(0, 6));

            // Objective
            DrawSectionLabel("OBJECTIVE");
            ImGui.PushStyleColor(ImGuiCol.FrameBg, BgCard);
            ImGui.PushStyleColor(ImGuiCol.Border, BorderDefault);
            ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 1.0f);
            string[] objectives = { "None", "Duty Completion", "Practice", "Loot" };
            ImGui.SetNextItemWidth(-1);
            ImGui.Combo("##Objective", ref editorObjectiveId, objectives, objectives.Length);
            ImGui.PopStyleVar();
            ImGui.PopStyleColor(2);
            ImGui.Dummy(new Vector2(0, 6));

            // Comment (191 char max, 2 lines that wrap)
            DrawSectionLabel("COMMENT");
            ImGui.PushStyleColor(ImGuiCol.FrameBg, BgCard);
            ImGui.PushStyleColor(ImGuiCol.Border, BorderDefault);
            ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 1.0f);
            ImGui.SetNextItemWidth(-1);
            // 2-line height is approximately ImGui.GetTextLineHeightWithSpacing() * 2, which is about 38. We also try ImGuiInputTextFlags.WordWrap
            float twoLineHeight = ImGui.GetTextLineHeight() * 2 + ImGui.GetStyle().FramePadding.Y * 2;
            ImGui.InputTextMultiline("##Comment", ref editorComment, 192, new Vector2(-1, twoLineHeight), (ImGuiInputTextFlags)0x01000000);
            ImGui.PopStyleVar();
            ImGui.PopStyleColor(2);
            // Clamp to 191 chars
            if (editorComment.Length > 191)
                editorComment = editorComment.Substring(0, 191);
            int charCount = editorComment.Length;
            ImGui.TextColored(charCount >= 191 ? AccentRed : TextMuted, $"{charCount}/191");
            ImGui.SameLine();
            ImGui.TextColored(TextSecondary, " | Wrapped Preview:");
            ImGui.Indent(10);
            string wrappedPreview = WrapText(editorComment, 38);
            ImGui.TextColored(TextMuted, wrappedPreview);
            ImGui.Unindent(10);
            ImGui.Dummy(new Vector2(0, 6));

            // Roles
            DrawSectionLabel("ROLES");
            DrawRoleSlotEditor();

            ImGui.EndChild();
            ImGui.SameLine(0, 12);

            // ══ RIGHT COLUMN ══════════════════════════════════════
            ImGui.BeginChild("EditorRight", new Vector2(halfWidth, 0), false, ImGuiWindowFlags.None);

            // Search Area
            DrawSectionLabel("SEARCH AREA");
            DrawStyledCheckbox("Limit Recruiting to World", ref editorLimitToWorld);
            DrawStyledCheckbox("Form a Private Party", ref editorPrivateParty);
            if (editorPrivateParty)
            {
                ImGui.Indent(20);
                ImGui.TextColored(TextSecondary, "Party Password (4 digits):");
                ImGui.PushStyleColor(ImGuiCol.FrameBg, BgCard);
                ImGui.PushStyleColor(ImGuiCol.Border, BorderDefault);
                ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 1.0f);
                ImGui.SetNextItemWidth(70);
                // Numeric-only password input with max 4 chars
                if (ImGui.InputText("##Password", ref editorPasswordStr, 4, ImGuiInputTextFlags.CharsDecimal))
                {
                    if (editorPasswordStr.Length > 4)
                        editorPasswordStr = editorPasswordStr.Substring(0, 4);
                    editorPassword = editorPasswordStr;
                }
                ImGui.PopStyleVar();
                ImGui.PopStyleColor(2);
                string displayPw = int.TryParse(editorPassword, out int val) ? val.ToString("D4") : "0000";
                ImGui.TextColored(TextMuted, $"Display: {displayPw}");
                ImGui.Unindent(20);
            }
            ImGui.Dummy(new Vector2(0, 8));

            // Conditions
            DrawSectionLabel("CONDITIONS");
            DrawStyledCheckbox("Completion Status", ref editorCompletionStatus);
            if (editorCompletionStatus)
            {
                ImGui.Indent(20);
                ImGui.PushStyleColor(ImGuiCol.FrameBg, BgCard);
                ImGui.PushStyleColor(ImGuiCol.Border, BorderDefault);
                ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 1.0f);
                string[] completionTypes = { "Duty Complete", "Duty Complete (Weekly Reward Unclaimed)", "Duty Incomplete" };
                ImGui.SetNextItemWidth(-1);
                ImGui.Combo("##CompletionType", ref editorCompletionStatusType, completionTypes, completionTypes.Length);
                ImGui.PopStyleVar();
                ImGui.PopStyleColor(2);
                ImGui.Unindent(20);
            }

            DrawStyledCheckbox("Avg. Item Lv.", ref editorAvgItemLvEnabled);
            if (editorAvgItemLvEnabled)
            {
                ImGui.Indent(20);
                ImGui.PushStyleColor(ImGuiCol.FrameBg, BgCard);
                ImGui.PushStyleColor(ImGuiCol.Border, BorderDefault);
                ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 1.0f);
                ImGui.SetNextItemWidth(100);
                ImGui.InputInt("##AvgIL", ref editorAvgItemLv, 1, 10);
                if (editorAvgItemLv < 1) editorAvgItemLv = 1;
                if (editorAvgItemLv > 99999) editorAvgItemLv = 99999;
                ImGui.SameLine();
                DrawStyledCheckbox("or Above", ref editorAvgItemLvOrAbove);
                ImGui.PopStyleVar();
                ImGui.PopStyleColor(2);
                ImGui.Unindent(20);
            }
            ImGui.Dummy(new Vector2(0, 8));

            // Duty Finder Settings
            DrawSectionLabel("DUTY FINDER SETTINGS");
            DrawStyledCheckbox("Unrestricted Party", ref editorUnrestrictedParty);
            DrawStyledCheckbox("Minimum IL", ref editorMinimumIL);
            DrawStyledCheckbox("Silence Echo", ref editorSilenceEcho);
            ImGui.Dummy(new Vector2(0, 8));

            // Loot Rules
            DrawSectionLabel("LOOT RULES");
            ImGui.PushStyleColor(ImGuiCol.FrameBg, BgCard);
            ImGui.PushStyleColor(ImGuiCol.Border, BorderDefault);
            ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 1.0f);
            string[] lootOptions = { "Normal", "Greed Only", "Lootmaster" };
            ImGui.SetNextItemWidth(-1);
            ImGui.Combo("##LootRules", ref editorLootRules, lootOptions, lootOptions.Length);
            ImGui.PopStyleVar();
            ImGui.PopStyleColor(2);
            ImGui.Dummy(new Vector2(0, 8));

            // Language
            DrawSectionLabel("LANGUAGE");
            ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(8, 4));
            DrawLanguageFlag("J", ref editorLangJapanese, ColorFromHex("#cc3333"));
            ImGui.SameLine();
            DrawLanguageFlag("E", ref editorLangEnglish, ColorFromHex("#3366cc"));
            ImGui.SameLine();
            DrawLanguageFlag("D", ref editorLangGerman, ColorFromHex("#cc9933"));
            ImGui.SameLine();
            DrawLanguageFlag("F", ref editorLangFrench, ColorFromHex("#3399cc"));
            ImGui.PopStyleVar();

            ImGui.EndChild();
            ImGui.EndChild(); // EditorScroll

            // Footer buttons
            ImGui.Dummy(new Vector2(0, 4));
            ImGui.GetWindowDrawList().AddLine(new Vector2(ImGui.GetWindowPos().X, ImGui.GetCursorScreenPos().Y), new Vector2(ImGui.GetWindowPos().X + ImGui.GetWindowWidth(), ImGui.GetCursorScreenPos().Y), ImGui.ColorConvertFloat4ToU32(BorderDefault), 1.0f);
            ImGui.Dummy(new Vector2(0, 6));

            float btnWidth = (contentWidth - 12) / 2f;

            ImGui.PushStyleColor(ImGuiCol.Button, ColorFromHex("#1a3a2a"));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, ColorFromHex("#224832"));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, ColorFromHex("#2a5a3c"));
            ImGui.PushStyleColor(ImGuiCol.Text, AccentGreen);
            ImGui.PushStyleColor(ImGuiCol.Border, ColorFromHex("#27c93f40"));
            ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 1.0f);
            ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 6.0f);
            if (ImGui.Button("  Save Preset##SavePreset", new Vector2(btnWidth, 30)))
                SaveEditor();
            ImGui.PopStyleVar(2);
            ImGui.PopStyleColor(5);

            ImGui.SameLine(0, 12);

            ImGui.PushStyleColor(ImGuiCol.Button, BgCard);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, ColorFromHex("#1c2230"));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, ColorFromHex("#243a54"));
            ImGui.PushStyleColor(ImGuiCol.Text, TextSecondary);
            ImGui.PushStyleColor(ImGuiCol.Border, BorderDefault);
            ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 1.0f);
            ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 6.0f);
            if (ImGui.Button("  Cancel##CancelPreset", new Vector2(btnWidth, 30)))
                CancelEditor();
            ImGui.PopStyleVar(2);
            ImGui.PopStyleColor(5);
        }

        // ── Duty Category Selector (matches in-game dropdown) ─────
        private void DrawDutyCategorySelector()
        {
            // Category dropdown
            ImGui.PushStyleColor(ImGuiCol.FrameBg, BgCard);
            ImGui.PushStyleColor(ImGuiCol.Border, BorderDefault);
            ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 1.0f);
            ImGui.SetNextItemWidth(-1);
            
            int comboIndex = editorDutyCategoryId switch
            {
                4 => 1, // Trials
                5 => 2, // Raids
                6 => 3, // High-end Duty
                _ => 0  // None
            };
            string[] visibleCategories = { "None", "Trials", "Raids", "High-end Duty" };
            
            if (ImGui.Combo("##DutyCategory", ref comboIndex, visibleCategories, visibleCategories.Length))
            {
                editorDutyCategoryId = comboIndex switch
                {
                    1 => 4, // Trials
                    2 => 5, // Raids
                    3 => 6, // High-end Duty
                    _ => 0  // None
                };
                editorDutyCategoryName = DutyCategories.Names[editorDutyCategoryId];
                editorDutyIndex = 0;
                // If category is "None", clear duty name
                if (editorDutyCategoryId == 0)
                {
                    editorDutyName = "None";
                }
                else
                {
                    // Try to get duties from Lumina for this category
                    var duties = dutyDataHelper.GetDutiesByType(editorDutyCategoryName);
                    editorDutyName = duties.Count > 0 ? duties[0].Name : editorDutyCategoryName;
                }
            }
            ImGui.PopStyleVar();
            ImGui.PopStyleColor(2);

            // If a category is selected, show the duty sub-dropdown
            if (editorDutyCategoryId > 0)
            {
                ImGui.Dummy(new Vector2(0, 2));
                ImGui.PushStyleColor(ImGuiCol.FrameBg, BgCard);
                ImGui.PushStyleColor(ImGuiCol.Border, BorderDefault);
                ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 1.0f);

                // Get duties for this category from Lumina
                var duties = dutyDataHelper.GetDutiesByType(editorDutyCategoryName);

                if (duties.Count > 0)
                {
                    string[] dutyNames = duties.Select(d => d.Name).ToArray();
                    if (editorDutyIndex >= dutyNames.Length) editorDutyIndex = 0;
                    ImGui.SetNextItemWidth(-1);
                    if (ImGui.Combo("##DutySelection", ref editorDutyIndex, dutyNames, dutyNames.Length))
                    {
                        editorDutyName = dutyNames[editorDutyIndex];
                    }
                }
                else
                {
                    // Manual text input fallback if Lumina doesn't have duties for this category
                    ImGui.SetNextItemWidth(-1);
                    ImGui.InputTextWithHint("##DutyManual", "Type duty name...", ref editorDutyName, 128);
                }

                ImGui.PopStyleVar();
                ImGui.PopStyleColor(2);
            }
        }

        // ── Role Slot Editor ──────────────────────────────────────
        private void DrawRoleSlotEditor()
        {
            int targetSlots = editorGroupType == 1 ? 24 : 8;
            while (editorSlots.Count < targetSlots)
                editorSlots.Add(new RoleSlot { SlotIndex = editorSlots.Count, Role = RoleType.Free });
            while (editorSlots.Count > targetSlots)
                editorSlots.RemoveAt(editorSlots.Count - 1);

            int slotsPerRow = 8;
            float slotSize = 28f;
            float spacing = 4f;

            if (editorAutoAdjustRoles)
            {
                ImGui.BeginDisabled();
            }

            List<(RoleType Role, uint? JobId, string Tooltip)>? autoSlots = null;
            if (editorAutoAdjustRoles)
            {
                autoSlots = pfAutomation.GetAutoAdjustedSlots();
            }

            for (int i = 0; i < editorSlots.Count; i++)
            {
                if (i > 0 && i % slotsPerRow == 0)
                    ImGui.Dummy(new Vector2(0, 2));
                else if (i > 0)
                    ImGui.SameLine(0, spacing);

                var slot = editorSlots[i];
                bool isSlot1 = (i == 0);
                uint? iconId = null;
                Vector4 slotColor = GetRoleColor(slot.Role);
                string tooltip = "";

                if (editorAutoAdjustRoles && autoSlots != null && i < autoSlots.Count)
                {
                    var autoSlot = autoSlots[i];
                    slotColor = GetRoleColor(autoSlot.Role);
                    if (autoSlot.JobId.HasValue)
                    {
                        iconId = 62100 + autoSlot.JobId.Value;
                    }
                    else
                    {
                        iconId = autoSlot.Role switch
                        {
                            RoleType.Tank => 62581,
                            RoleType.Healer => 62582,
                            RoleType.MeleeDPS => 62583,
                            RoleType.PhysRangedDPS => 62583,
                            RoleType.MagicRangedDPS => 62583,
                            RoleType.Nullify => 60523,
                            _ => null
                        };
                    }
                    tooltip = autoSlot.Tooltip;
                }
                else
                {
                    if (isSlot1)
                    {
                        if (pfAutomation.PlayerState.IsLoaded && pfAutomation.PlayerState.ClassJob.RowId > 0)
                        {
                            iconId = 62100 + pfAutomation.PlayerState.ClassJob.RowId;
                        }
                        else
                        {
                            iconId = 60501; // Fallback
                        }
                        slotColor = TextPrimary;
                        string jobName = (pfAutomation.PlayerState.IsLoaded && pfAutomation.PlayerState.ClassJob.RowId > 0) ? pfAutomation.PlayerState.ClassJob.Value.Name.ToString() : "Unknown";
                        tooltip = $"Slot 1: You ({jobName})\nThis slot is locked to your current class/job.";
                    }
                    else
                    {
                        // Check if exactly one job is selected
                        uint? singleJobIconId = null;
                        if (slot.AcceptedJobFlags != 0 && (slot.AcceptedJobFlags & (slot.AcceptedJobFlags - 1)) == 0)
                        {
                            foreach (var job in JobData.AllJobsAndClasses)
                            {
                                if ((1UL << job.BitIndex) == slot.AcceptedJobFlags)
                                {
                                    singleJobIconId = 62100 + (uint)job.Id;
                                    break;
                                }
                            }
                        }

                        if (singleJobIconId.HasValue)
                        {
                            iconId = singleJobIconId.Value;
                        }
                        else
                        {
                            iconId = slot.Role switch
                            {
                                RoleType.Tank => 62581,
                                RoleType.Healer => 62582,
                                RoleType.MeleeDPS => 62583,
                                RoleType.PhysRangedDPS => 62583,
                                RoleType.MagicRangedDPS => 62583,
                                RoleType.Nullify => 60523,
                                _ => null
                            };
                        }
                        string jobInfo = slot.AcceptedJobFlags == 0 ? "All jobs" : "Custom jobs";
                        tooltip = $"Slot {i + 1}: {PfAutomation.GetRoleName(slot.Role)}\nJobs: {jobInfo}\nClick: open job/role selector";
                    }
                }

                if (isSlot1)
                {
                    Vector2 pos = ImGui.GetCursorScreenPos();
                    if (iconId.HasValue)
                    {
                        ImTextureID handle = GetIconHandle(iconId.Value);
                        ImGui.Image(handle, new Vector2(slotSize, slotSize));
                    }
                    else
                    {
                        ImGui.Button("US", new Vector2(slotSize, slotSize));
                    }
                    ImGui.GetWindowDrawList().AddRect(pos, new Vector2(pos.X + slotSize, pos.Y + slotSize), ImGui.ColorConvertFloat4ToU32(AccentBlue), 4.0f, ImDrawFlags.None, 1.5f);

                    if (ImGui.IsItemHovered())
                    {
                        ImGui.SetTooltip(tooltip);
                    }
                }
                else
                {
                    Vector4 slotBg = new Vector4(slotColor.X, slotColor.Y, slotColor.Z, 0.3f);
                    ImGui.PushStyleColor(ImGuiCol.Button, slotBg);
                    ImGui.PushStyleColor(ImGuiCol.ButtonHovered, slotColor);
                    ImGui.PushStyleColor(ImGuiCol.ButtonActive, slotColor);
                    ImGui.PushStyleColor(ImGuiCol.Text, TextPrimary);
                    ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 4.0f);

                    bool clicked = false;
                    if (iconId.HasValue)
                    {
                        ImTextureID handle = GetIconHandle(iconId.Value);
                        ImGui.PushID($"SlotBtn_{i}");
                        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(2, 2));
                        clicked = ImGui.ImageButton(handle, new Vector2(slotSize - 4f, slotSize - 4f));
                        ImGui.PopStyleVar();
                        ImGui.PopID();
                    }
                    else
                    {
                        string slotLabel = GetRoleShortLabel(slot.Role);
                        if (editorAutoAdjustRoles && autoSlots != null && i < autoSlots.Count)
                        {
                            slotLabel = GetRoleShortLabel(autoSlots[i].Role);
                        }
                        clicked = ImGui.Button($"{slotLabel}##Slot_{i}", new Vector2(slotSize, slotSize));
                    }

                    ImGui.PopStyleVar();
                    ImGui.PopStyleColor(4);

                    if (clicked)
                    {
                        jobSelectorSlotIndex = i;
                        showJobSelector = true;
                        tempSelectorRole = slot.Role;
                        tempSelectorJobFlags = slot.AcceptedJobFlags;
                    }

                    if (ImGui.IsItemHovered())
                    {
                        ImGui.SetTooltip(tooltip);
                    }
                }
            }

            if (editorAutoAdjustRoles)
            {
                ImGui.EndDisabled();
            }

            ImGui.Dummy(new Vector2(0, 4));

            DrawStyledCheckbox("Auto-Adjust Roles (Seek Job Distributions)", ref editorAutoAdjustRoles);
            if (editorAutoAdjustRoles)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, TextMuted);
                ImGui.TextWrapped("Roles will be automatically sought by the game client based on the selected high-end duty when applying this preset.");
                ImGui.PopStyleColor();
            }

            ImGui.Dummy(new Vector2(0, 4));
            DrawStyledCheckbox("Unselect Classes", ref editorUnselectClasses);
            DrawStyledCheckbox("One Player per Job", ref editorOnePlayerPerJob);

            bool disableRemoveRestrictions = editorAutoAdjustRoles;
            if (disableRemoveRestrictions) ImGui.BeginDisabled();
            bool oldRemove = editorRemoveRoleRestrictions;
            if (DrawStyledCheckbox("Remove role restrictions for all remaining openings.", ref editorRemoveRoleRestrictions))
            {
                if (editorRemoveRoleRestrictions && !oldRemove)
                {
                    foreach (var slot in editorSlots)
                    {
                        slot.Role = RoleType.Free;
                        slot.AcceptedJobFlags = 0;
                    }
                }
            }
            if (disableRemoveRestrictions) ImGui.EndDisabled();
        }

        private void ApplyCompositionToEditor(RoleType[] composition)
        {
            int targetSlots = editorGroupType == 1 ? 24 : 8;
            editorSlots.Clear();
            for (int i = 0; i < targetSlots; i++)
            {
                editorSlots.Add(new RoleSlot
                {
                    SlotIndex = i,
                    Role = i < composition.Length ? composition[i] : RoleType.Free,
                    AcceptedJobFlags = 0,
                });
            }
        }

        // ═══════════════════════════════════════════════════════════
        //  JOB SELECTOR WINDOW
        // ═══════════════════════════════════════════════════════════

        private bool TryDrawIcon(uint iconId, Vector2 size)
        {
            try
            {
                var iconTexture = textureProvider.GetFromGameIcon(new Dalamud.Interface.Textures.GameIconLookup { IconId = iconId });
                if (iconTexture != null && iconTexture.TryGetWrap(out var wrap, out _))
                {
                    ImGui.Image(wrap.Handle, size);
                    return true;
                }
            }
            catch {}
            return false;
        }

        private ImTextureID GetIconHandle(uint iconId)
        {
            try
            {
                var tex = textureProvider.GetFromGameIcon(new Dalamud.Interface.Textures.GameIconLookup { IconId = iconId });
                if (tex != null && tex.TryGetWrap(out var wrap, out _))
                {
                    return wrap.Handle;
                }
            }
            catch {}
            return default;
        }

        private List<int> GetJobIndicesForCategory(string category)
        {
            var list = new List<int>();
            switch (category)
            {
                case "Tank":
                    list.AddRange(new[] { JobData.PLD.BitIndex, JobData.WAR.BitIndex, JobData.DRK.BitIndex, JobData.GNB.BitIndex, JobData.GLA.BitIndex, JobData.MRD.BitIndex });
                    break;
                case "Healer":
                    list.AddRange(new[] { JobData.WHM.BitIndex, JobData.AST.BitIndex, JobData.SCH.BitIndex, JobData.SGE.BitIndex, JobData.CNJ.BitIndex });
                    break;
                case "Pure Healer":
                    list.AddRange(new[] { JobData.WHM.BitIndex, JobData.AST.BitIndex, JobData.CNJ.BitIndex });
                    break;
                case "Barrier Healer":
                    list.AddRange(new[] { JobData.SCH.BitIndex, JobData.SGE.BitIndex });
                    break;
                case "DPS":
                    list.AddRange(new[] {
                        JobData.MNK.BitIndex, JobData.DRG.BitIndex, JobData.NIN.BitIndex, JobData.SAM.BitIndex, JobData.RPR.BitIndex, JobData.VPR.BitIndex, JobData.BST.BitIndex,
                        JobData.PGL.BitIndex, JobData.LNC.BitIndex, JobData.ROG.BitIndex,
                        JobData.BRD.BitIndex, JobData.MCH.BitIndex, JobData.DNC.BitIndex, JobData.ARC.BitIndex,
                        JobData.BLM.BitIndex, JobData.SMN.BitIndex, JobData.RDM.BitIndex, JobData.PCT.BitIndex, JobData.BLU.BitIndex,
                        JobData.THM.BitIndex, JobData.ACN.BitIndex
                    });
                    break;
                case "Melee DPS":
                    list.AddRange(new[] { JobData.MNK.BitIndex, JobData.DRG.BitIndex, JobData.NIN.BitIndex, JobData.SAM.BitIndex, JobData.RPR.BitIndex, JobData.VPR.BitIndex, JobData.BST.BitIndex, JobData.PGL.BitIndex, JobData.LNC.BitIndex, JobData.ROG.BitIndex });
                    break;
                case "Physical Ranged DPS":
                    list.AddRange(new[] { JobData.BRD.BitIndex, JobData.MCH.BitIndex, JobData.DNC.BitIndex, JobData.ARC.BitIndex });
                    break;
                case "Magical Ranged DPS":
                    list.AddRange(new[] { JobData.BLM.BitIndex, JobData.SMN.BitIndex, JobData.RDM.BitIndex, JobData.PCT.BitIndex, JobData.BLU.BitIndex, JobData.THM.BitIndex, JobData.ACN.BitIndex });
                    break;
            }
            return list;
        }

        private RoleType MapCategoryToRole(JobCategory category) => category switch
        {
            JobCategory.Tank => RoleType.Tank,
            JobCategory.PureHealer or JobCategory.BarrierHealer => RoleType.Healer,
            JobCategory.MeleeDPS => RoleType.MeleeDPS,
            JobCategory.PhysRangedDPS => RoleType.PhysRangedDPS,
            JobCategory.MagicRangedDPS => RoleType.MagicRangedDPS,
            _ => RoleType.Free
        };

        private void SelectRoleCategory(string category, RoleType role)
        {
            // Exclusive role selection: clear ALL flags first, then set only this role's
            tempSelectorJobFlags = 0;
            tempSelectorRole = role;
            var indices = GetJobIndicesForCategory(category);
            foreach (var idx in indices)
            {
                tempSelectorJobFlags |= (1UL << idx);
            }
        }

        private bool IsJobSelectedInSelector(JobInfo job)
        {
            if (tempSelectorJobFlags != 0)
            {
                return (tempSelectorJobFlags & (1UL << job.BitIndex)) != 0;
            }

            if (tempSelectorRole == RoleType.Free)
                return true;

            if (tempSelectorRole == RoleType.Nullify)
                return false;

            return MapCategoryToRole(job.Category) == tempSelectorRole;
        }

        private ulong GetDefaultFlagsForRole(RoleType role)
        {
            if (role == RoleType.Free)
            {
                ulong mask = 0;
                foreach (var j in JobData.AllJobsAndClasses)
                    mask |= (1UL << j.BitIndex);
                return mask;
            }

            if (role == RoleType.Nullify)
                return 0;

            ulong roleMask = 0;
            foreach (var j in JobData.AllJobsAndClasses)
            {
                if (MapCategoryToRole(j.Category) == role)
                    roleMask |= (1UL << j.BitIndex);
            }
            return roleMask;
        }

        private void DrawJobIcon(JobInfo job, Vector4 roleColor)
        {
            bool isSelected = IsJobSelectedInSelector(job);
            Vector2 size = new Vector2(24, 24);
            Vector2 pos = ImGui.GetCursorScreenPos();
            
            ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(0, 0));
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0, 0, 0, 0));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(1, 1, 1, 0.15f));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(1, 1, 1, 0.25f));
            
            ImGui.PushID($"##JobBtn_{job.Id}");
            if (ImGui.ImageButton(GetIconHandle(62100 + (uint)job.Id), size))
            {
                if (tempSelectorJobFlags == 0)
                {
                    tempSelectorJobFlags = GetDefaultFlagsForRole(tempSelectorRole);
                }
                // Class/job selection: toggle individually, allow cross-role picks
                tempSelectorJobFlags ^= (1UL << job.BitIndex);
                // Recalculate role from what's selected
                tempSelectorRole = RecalcRoleFromFlags();
            }
            ImGui.PopID();
            ImGui.PopStyleColor(3);
            ImGui.PopStyleVar();

            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(job.Name);
            }

            if (isSelected)
            {
                ImGui.GetWindowDrawList().AddRect(pos, new Vector2(pos.X + size.X, pos.Y + size.Y), ImGui.ColorConvertFloat4ToU32(AccentYellow), 2.0f, ImDrawFlags.None, 2.0f);
            }
        }

        /// <summary>Determine role from currently selected job flags. If all jobs belong to one role, use that role. Otherwise Free.</summary>
        private RoleType RecalcRoleFromFlags()
        {
            if (tempSelectorJobFlags == 0)
                return RoleType.Free;

            bool hasTank = false, hasHealer = false, hasMelee = false, hasPhys = false, hasMagic = false;
            foreach (var job in JobData.AllJobs)
            {
                if ((tempSelectorJobFlags & (1UL << job.BitIndex)) != 0)
                {
                    switch (job.Category)
                    {
                        case JobCategory.Tank: hasTank = true; break;
                        case JobCategory.PureHealer:
                        case JobCategory.BarrierHealer: hasHealer = true; break;
                        case JobCategory.MeleeDPS: hasMelee = true; break;
                        case JobCategory.PhysRangedDPS: hasPhys = true; break;
                        case JobCategory.MagicRangedDPS: hasMagic = true; break;
                    }
                }
            }
            // Also check base classes
            int[] tankClassBits = { JobData.GLA.BitIndex, JobData.MRD.BitIndex };
            int[] healerClassBits = { JobData.CNJ.BitIndex };
            int[] meleeClassBits = { JobData.PGL.BitIndex, JobData.LNC.BitIndex, JobData.ROG.BitIndex };
            int[] physClassBits = { JobData.ARC.BitIndex };
            int[] magicClassBits = { JobData.THM.BitIndex, JobData.ACN.BitIndex };
            foreach (var b in tankClassBits) if ((tempSelectorJobFlags & (1UL << b)) != 0) hasTank = true;
            foreach (var b in healerClassBits) if ((tempSelectorJobFlags & (1UL << b)) != 0) hasHealer = true;
            foreach (var b in meleeClassBits) if ((tempSelectorJobFlags & (1UL << b)) != 0) hasMelee = true;
            foreach (var b in physClassBits) if ((tempSelectorJobFlags & (1UL << b)) != 0) hasPhys = true;
            foreach (var b in magicClassBits) if ((tempSelectorJobFlags & (1UL << b)) != 0) hasMagic = true;

            int count = (hasTank ? 1 : 0) + (hasHealer ? 1 : 0) + (hasMelee ? 1 : 0) + (hasPhys ? 1 : 0) + (hasMagic ? 1 : 0);
            if (count == 1)
            {
                if (hasTank) return RoleType.Tank;
                if (hasHealer) return RoleType.Healer;
                if (hasMelee) return RoleType.MeleeDPS;
                if (hasPhys) return RoleType.PhysRangedDPS;
                if (hasMagic) return RoleType.MagicRangedDPS;
            }
            return RoleType.Free;
        }

        private void DrawJobSelectorRow(string label, uint roleIconId, JobInfo[] jobs, JobInfo[] classes, Vector4 roleColor, string categoryName, RoleType roleType)
        {
            Vector2 cur = ImGui.GetCursorScreenPos();
            
            if (TryDrawIcon(roleIconId, new Vector2(18, 18)))
            {
                ImGui.SameLine(0, 6);
            }
            
            ImGui.AlignTextToFramePadding();
            bool isCategorySelected = tempSelectorRole == roleType && (tempSelectorJobFlags == 0 || tempSelectorJobFlags == GetDefaultFlagsForRole(roleType));
            if (ImGui.Selectable($"{label}##row_{label}", isCategorySelected, ImGuiSelectableFlags.None, new Vector2(100, 20)))
            {
                SelectRoleCategory(categoryName, roleType);
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip($"Click to exclusively select all {label}s");
            }
            
            ImGui.SameLine(130);
            foreach (var job in jobs)
            {
                DrawJobIcon(job, roleColor);
                ImGui.SameLine(0, 4);
            }
            
            ImGui.SameLine(330);
            foreach (var cls in classes)
            {
                DrawJobIcon(cls, roleColor);
                ImGui.SameLine(0, 4);
            }
            
            ImGui.NewLine();
            ImGui.Dummy(new Vector2(0, 2));
        }

        private void DrawJobSelectorWindow()
        {
            if (!showJobSelector || jobSelectorSlotIndex < 0 || jobSelectorSlotIndex >= editorSlots.Count)
                return;

            var slot = editorSlots[jobSelectorSlotIndex];

            bool isLastActiveSlot = false;
            if (jobSelectorSlotIndex > 0)
            {
                int activeCount = 0;
                for (int idx = 1; idx < editorSlots.Count; idx++)
                {
                    if (editorSlots[idx].Role != RoleType.Nullify)
                    {
                        activeCount++;
                    }
                }
                if (activeCount <= 1 && slot.Role != RoleType.Nullify)
                {
                    isLastActiveSlot = true;
                }
            }

            ImGui.SetNextWindowSize(new Vector2(520, 420), ImGuiCond.FirstUseEver);
            ImGui.PushStyleColor(ImGuiCol.WindowBg, BgOuter);
            ImGui.PushStyleColor(ImGuiCol.Border, AccentBlue);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 2.0f);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 8.0f);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(16, 12));

            bool open = showJobSelector;
            if (ImGui.Begin("Job Selector##JobSelectorWindow", ref open, ImGuiWindowFlags.NoCollapse))
            {
                if (!open) showJobSelector = false;

                ImGui.TextColored(AccentBlue, $"Editing Slot {jobSelectorSlotIndex + 1}");
                ImGui.Dummy(new Vector2(0, 2));

                // Job Grid (full width, no left sidebar)
                ImGui.BeginChild("JobGridRight", new Vector2(0, -40), true);
                
                ImGui.Text("");
                ImGui.SameLine(130);
                ImGui.TextColored(TextSecondary, "JOB");
                ImGui.SameLine(330);
                ImGui.TextColored(TextSecondary, "CLASS");
                ImGui.Separator();
                ImGui.Dummy(new Vector2(0, 4));

                // Tank
                DrawJobSelectorRow("Tank", 62581, 
                    new[] { JobData.PLD, JobData.WAR, JobData.DRK, JobData.GNB },
                    new[] { JobData.GLA, JobData.MRD },
                    RoleTank, "Tank", RoleType.Tank);

                // Pure Healer
                DrawJobSelectorRow("Pure Healer", 62582,
                    new[] { JobData.WHM, JobData.AST },
                    new[] { JobData.CNJ },
                    RoleHealer, "Pure Healer", RoleType.Healer);

                // Barrier Healer
                DrawJobSelectorRow("Barrier Healer", 62582,
                    new[] { JobData.SCH, JobData.SGE },
                    new JobInfo[0],
                    RoleHealer, "Barrier Healer", RoleType.Healer);

                // Melee DPS
                DrawJobSelectorRow("Melee DPS", 62583,
                    new[] { JobData.MNK, JobData.DRG, JobData.NIN, JobData.SAM, JobData.RPR, JobData.VPR, JobData.BST },
                    new[] { JobData.PGL, JobData.LNC, JobData.ROG },
                    RoleDPS, "Melee DPS", RoleType.MeleeDPS);

                // Phys Ranged
                DrawJobSelectorRow("Phys Ranged", 62583,
                    new[] { JobData.BRD, JobData.MCH, JobData.DNC },
                    new[] { JobData.ARC },
                    RoleDPS, "Physical Ranged DPS", RoleType.PhysRangedDPS);

                // Magic Ranged
                DrawJobSelectorRow("Magic Ranged", 62583,
                    new[] { JobData.BLM, JobData.SMN, JobData.RDM, JobData.PCT, JobData.BLU },
                    new[] { JobData.THM, JobData.ACN },
                    RoleDPS, "Magical Ranged DPS", RoleType.MagicRangedDPS);

                // Free & Nullify
                ImGui.Separator();
                ImGui.Dummy(new Vector2(0, 4));
                
                // Free row
                ImGui.AlignTextToFramePadding();
                if (ImGui.Selectable("Free##row_Free", tempSelectorRole == RoleType.Free && tempSelectorJobFlags == 0, ImGuiSelectableFlags.None, new Vector2(100, 20)))
                {
                    tempSelectorRole = RoleType.Free;
                    tempSelectorJobFlags = 0;
                }
                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip("Set slot to Free (any job)");
                }
                ImGui.SameLine(130);
                ImGui.TextColored(TextMuted, "Accepts any role and job for this slot");

                ImGui.Dummy(new Vector2(0, 4));

                // Nullify row
                ImGui.AlignTextToFramePadding();
                if (isLastActiveSlot)
                {
                    ImGui.BeginDisabled(true);
                }
                if (ImGui.Selectable("Nullify##row_Nullify", tempSelectorRole == RoleType.Nullify, ImGuiSelectableFlags.None, new Vector2(100, 20)))
                {
                    tempSelectorRole = RoleType.Nullify;
                    tempSelectorJobFlags = 0;
                }
                if (isLastActiveSlot)
                {
                    ImGui.EndDisabled();
                    if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                    {
                        ImGui.SetTooltip("Cannot nullify the last remaining active slot.");
                    }
                }
                else if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip("Nullify slot (removes/disables it entirely)");
                }
                ImGui.SameLine(130);
                
                Vector2 nullSize = new Vector2(20, 20);
                if (TryDrawIcon(60523, nullSize))
                {
                    ImGui.SameLine(0, 8);
                }
                ImGui.TextColored(TextMuted, "Removes this slot from recruitment (empty slot)");
                
                ImGui.EndChild();

                // Confirm / Cancel Buttons
                ImGui.Dummy(new Vector2(0, 8));
                float btnW = (ImGui.GetContentRegionAvail().X - 8) / 2f;

                ImGui.PushStyleColor(ImGuiCol.Button, ColorFromHex("#1a3a2a"));
                ImGui.PushStyleColor(ImGuiCol.ButtonHovered, ColorFromHex("#224832"));
                ImGui.PushStyleColor(ImGuiCol.ButtonActive, ColorFromHex("#2a5a3c"));
                ImGui.PushStyleColor(ImGuiCol.Text, AccentGreen);
                ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 6.0f);
                if (ImGui.Button("OK##ConfirmJobSelector", new Vector2(btnW, 28)))
                {
                    slot.Role = tempSelectorRole;
                    slot.AcceptedJobFlags = tempSelectorJobFlags;
                    showJobSelector = false;
                }
                ImGui.PopStyleVar();
                ImGui.PopStyleColor(4);

                ImGui.SameLine(0, 8);

                ImGui.PushStyleColor(ImGuiCol.Button, BgCard);
                ImGui.PushStyleColor(ImGuiCol.ButtonHovered, ColorFromHex("#1c2230"));
                ImGui.PushStyleColor(ImGuiCol.ButtonActive, ColorFromHex("#243a54"));
                ImGui.PushStyleColor(ImGuiCol.Text, TextSecondary);
                ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 6.0f);
                if (ImGui.Button("Cancel##CancelJobSelector", new Vector2(btnW, 28)))
                {
                    showJobSelector = false;
                }
                ImGui.PopStyleVar();
                ImGui.PopStyleColor(4);
            }
            ImGui.End();
            ImGui.PopStyleVar(3);
            ImGui.PopStyleColor(2);
        }

        // ═══════════════════════════════════════════════════════════
        //  SETTINGS WINDOW
        // ═══════════════════════════════════════════════════════════

        private void DrawSettingsWindow()
        {
            if (!isSettingsWindowVisible) return;

            ImGui.SetNextWindowSize(new Vector2(380, 200), ImGuiCond.FirstUseEver);
            ImGui.PushStyleColor(ImGuiCol.WindowBg, BgOuter);
            ImGui.PushStyleColor(ImGuiCol.Border, BorderDefault);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 1.0f);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 8.0f);
            ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 4.0f);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(16, 12));

            bool settingsOpen = isSettingsWindowVisible;
            if (ImGui.Begin("PF Presets Settings##PfPresetsSettings", ref settingsOpen, ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoResize))
            {
                isSettingsWindowVisible = settingsOpen;

                DrawSectionLabel("AUTO REFRESHER");

                bool isRrActive = false;
                try
                {
                    isRrActive = pluginInterface.InstalledPlugins
                        .Any(p => (p.Name == "RecruitmentRefresher" || p.InternalName == "RecruitmentRefresher") && p.IsLoaded);
                }
                catch (Exception)
                {
                    // Safe fallback
                }

                if (isRrActive)
                {
                    bool dummyVal = true;
                    ImGui.BeginDisabled(true);
                    DrawStyledCheckbox("Enable Auto Refresher##AutoRefresher", ref dummyVal);
                    ImGui.EndDisabled();

                    ImGui.TextColored(AccentYellow, "You already got recruitment refresher");
                }
                else
                {
                    bool autoRefresh = config.AutoRefresherEnabled;
                    if (DrawStyledCheckbox("Enable Auto Refresher##AutoRefresher", ref autoRefresh))
                    {
                        config.AutoRefresherEnabled = autoRefresh;
                        config.Save();
                    }
                    ImGui.TextColored(TextSecondary, "Automatically refreshes recruitment every 30 minutes.");
                }

                ImGui.Dummy(new Vector2(0, 16));
                DrawSectionLabel("SUPPORT");

                ImGui.PushStyleColor(ImGuiCol.Button, AccentPurple);
                ImGui.PushStyleColor(ImGuiCol.ButtonHovered, ColorFromHex("#b08bff"));
                ImGui.PushStyleColor(ImGuiCol.ButtonActive, ColorFromHex("#824eff"));
                ImGui.PushStyleColor(ImGuiCol.Text, TextPrimary);
                ImGui.PushStyleColor(ImGuiCol.Border, BorderDefault);
                ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 1.0f);
                ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 6.0f);

                if (ImGui.Button("support my project##Kofi", new Vector2(-1, 30)))
                {
                    Dalamud.Utility.Util.OpenLink("https://ko-fi.com/marobotic");
                }
                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip("Support the developer on Ko-fi!");
                }

                ImGui.PopStyleVar(2);
                ImGui.PopStyleColor(5);
            }
            ImGui.End();
            ImGui.PopStyleVar(4);
            ImGui.PopStyleColor(2);
        }

        // ═══════════════════════════════════════════════════════════
        //  CHECKLIST OVERLAY
        // ═══════════════════════════════════════════════════════════

        private void DrawChecklistOverlay()
        {
            if (!pfAutomation.ShowChecklist || pfAutomation.ActivePreset == null) return;
            var preset = pfAutomation.ActivePreset;
            var initial = pfAutomation.InitialGameState;

            ImGui.SetNextWindowSize(new Vector2(460, 540), ImGuiCond.FirstUseEver);
            ImGui.PushStyleColor(ImGuiCol.WindowBg, ColorFromHex("#0d1117e8"));
            ImGui.PushStyleColor(ImGuiCol.Border, AccentBlue);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 2.0f);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 10.0f);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(16, 12));

            bool checklistOpen = true;
            if (ImGui.Begin("PF Preset Automation Status##Checklist", ref checklistOpen, ImGuiWindowFlags.NoCollapse))
            {
                if (!checklistOpen) pfAutomation.DismissChecklist();

                ImGui.TextColored(AccentBlue, "Party Finder Preset Automation");
                ImGui.TextColored(TextSecondary, $"Preset: {preset.Name}");
                ImGui.Dummy(new Vector2(0, 4));

                // Display current automation status
                ImGui.TextColored(AccentPurple, $"Status: {pfAutomation.AutomationStatus}");
                ImGui.Dummy(new Vector2(0, 4));

                ImGui.GetWindowDrawList().AddLine(
                    new Vector2(ImGui.GetWindowPos().X + 16, ImGui.GetCursorScreenPos().Y),
                    new Vector2(ImGui.GetWindowPos().X + ImGui.GetWindowWidth() - 16, ImGui.GetCursorScreenPos().Y),
                    ImGui.ColorConvertFloat4ToU32(BorderDefault), 1.0f);
                ImGui.Dummy(new Vector2(0, 8));

                ImGui.TextColored(TextMuted, "Settings Comparison (Preset vs Current Game PF):");
                ImGui.Dummy(new Vector2(0, 4));

                // Scrollable area for the list of settings
                if (ImGui.BeginChild("ComparisonScroll", new Vector2(-1, 340), true, ImGuiWindowFlags.None))
                {
                    // 1. Duty
                    DrawChecklistComparison("Duty", 
                        $"{preset.DutyCategoryName} > {preset.DutyName}", 
                        initial != null ? $"{initial.DutyCategory} > {initial.DutyName}" : "None");

                    // 2. Objective
                    DrawChecklistComparison("Objective", 
                        PfAutomation.GetObjectiveName(preset.ObjectiveId), 
                        initial != null ? initial.Objective : "None");

                    // 3. Roles
                    DrawChecklistComparison("Roles", 
                        preset.GetRoleSummary(), 
                        initial != null ? initial.Roles : "None");

                    // 4. Comment
                    DrawChecklistComparison("Comment", 
                        preset.Comment, 
                        initial != null ? initial.Comment : "");

                    // 5. Private Passcode
                    string presetPwd = preset.FormPrivateParty ? preset.PrivatePartyPassword : "None";
                    if (preset.FormPrivateParty && int.TryParse(preset.PrivatePartyPassword, out int pwdVal))
                    {
                        presetPwd = pwdVal.ToString("D4");
                    }
                    DrawChecklistComparison("Private Passcode", 
                        presetPwd, 
                        initial != null ? initial.Password : "None");

                    // 6. Limit Recruiting to World
                    DrawChecklistComparison("Limit to World", 
                        preset.LimitRecruitingToWorld ? "Limit to World" : "None", 
                        initial != null && initial.Flags.Contains("Limit to World") ? "Limit to World" : "None");

                    // 7. One Player Per Job
                    DrawChecklistComparison("One Player Per Job", 
                        preset.OnePlayerPerJob ? "One Per Job" : "None", 
                        initial != null && initial.Flags.Contains("One Per Job") ? "One Per Job" : "None");

                    // 8. Completion Status
                    string presetComp = preset.CompletionStatusEnabled 
                        ? PfAutomation.GetCompletionStatusName(preset.CompletionStatusType) 
                        : "None";
                    DrawChecklistComparison("Completion Status", 
                        presetComp, 
                        initial != null ? initial.Completion : "None");

                    // 9. Loot Rules
                    DrawChecklistComparison("Loot Rules", 
                        PfAutomation.GetLootRuleName(preset.LootRules), 
                        initial != null ? initial.Loot : "Normal");

                    // 10. Languages
                    var presetLangList = new List<string>();
                    if (preset.LangJapanese) presetLangList.Add("J");
                    if (preset.LangEnglish) presetLangList.Add("E");
                    if (preset.LangGerman) presetLangList.Add("D");
                    if (preset.LangFrench) presetLangList.Add("F");
                    string presetLangs = presetLangList.Count > 0 ? string.Join(",", presetLangList) : "None";
                    DrawChecklistComparison("Languages", 
                        presetLangs, 
                        initial != null ? initial.Lang : "None");

                    ImGui.EndChild();
                }

                ImGui.Dummy(new Vector2(0, 12));
                
                // Done button
                ImGui.PushStyleColor(ImGuiCol.Button, ColorFromHex("#1a3a2a"));
                ImGui.PushStyleColor(ImGuiCol.ButtonHovered, ColorFromHex("#224832"));
                ImGui.PushStyleColor(ImGuiCol.ButtonActive, ColorFromHex("#2a5a3c"));
                ImGui.PushStyleColor(ImGuiCol.Text, AccentGreen);
                ImGui.PushStyleColor(ImGuiCol.Border, ColorFromHex("#27c93f40"));
                ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 1.0f);
                ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 6.0f);
                
                if (ImGui.Button("Close Status Window##DismissChecklist", new Vector2(-1, 30)))
                    pfAutomation.DismissChecklist();
                
                ImGui.PopStyleVar(2);
                ImGui.PopStyleColor(5);
            }
            ImGui.End();
            ImGui.PopStyleVar(3);
            ImGui.PopStyleColor(2);
        }

        private void DrawChecklistComparison(string label, string presetValue, string gameValue)
        {
            bool match = presetValue.Equals(gameValue, StringComparison.OrdinalIgnoreCase);

            ImGui.PushFont(UiBuilder.IconFont);
            if (match)
            {
                ImGui.TextColored(AccentGreen, FontAwesomeIcon.CheckCircle.ToIconString());
            }
            else
            {
                ImGui.TextColored(AccentYellow, FontAwesomeIcon.ArrowCircleRight.ToIconString());
            }
            ImGui.PopFont();
            ImGui.SameLine(0, 8);

            ImGui.TextColored(TextPrimary, $"{label}:");
            ImGui.Indent(24);
            ImGui.TextColored(TextSecondary, $"Preset: {presetValue}");
            ImGui.TextColored(TextMuted, $"Game PF: {gameValue}");
            ImGui.Unindent(24);
            ImGui.Dummy(new Vector2(0, 4));
        }

        // ═══════════════════════════════════════════════════════════
        //  HELPERS
        // ═══════════════════════════════════════════════════════════

        private void DrawSectionLabel(string label)
        {
            ImGui.TextColored(AccentBlue, label);
            ImGui.Dummy(new Vector2(0, 2));
        }

        private bool DrawStyledCheckbox(string label, ref bool value)
        {
            ImGui.PushStyleColor(ImGuiCol.FrameBg, BgCard);
            ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, ColorFromHex("#1c2230"));
            ImGui.PushStyleColor(ImGuiCol.FrameBgActive, ColorFromHex("#243a54"));
            ImGui.PushStyleColor(ImGuiCol.CheckMark, AccentBlue);
            ImGui.PushStyleColor(ImGuiCol.Border, BorderDefault);
            ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 1.0f);
            ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 3.0f);
            bool changed = ImGui.Checkbox(label, ref value);
            ImGui.PopStyleVar(2);
            ImGui.PopStyleColor(5);
            return changed;
        }

        private void DrawLanguageFlag(string letter, ref bool enabled, Vector4 color)
        {
            Vector4 bgColor = enabled ? new Vector4(color.X, color.Y, color.Z, 0.3f) : BgCard;
            Vector4 textColor = enabled ? TextPrimary : TextMuted;
            Vector4 borderColor = enabled ? color : BorderDefault;

            ImGui.PushStyleColor(ImGuiCol.Button, bgColor);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(color.X, color.Y, color.Z, 0.4f));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(color.X, color.Y, color.Z, 0.5f));
            ImGui.PushStyleColor(ImGuiCol.Text, textColor);
            ImGui.PushStyleColor(ImGuiCol.Border, borderColor);
            ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 1.0f);
            ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 4.0f);
            if (ImGui.Button($"{letter}##Lang_{letter}", new Vector2(30, 26)))
                enabled = !enabled;
            ImGui.PopStyleVar(2);
            ImGui.PopStyleColor(5);
        }

        private static Vector4 GetRoleColor(RoleType role) => role switch
        {
            RoleType.Tank => RoleTank,
            RoleType.Healer => RoleHealer,
            RoleType.MeleeDPS or RoleType.PhysRangedDPS or RoleType.MagicRangedDPS => RoleDPS,
            RoleType.Nullify => ColorFromHex("#2d3748"),
            _ => RoleFree,
        };

        private static string GetRoleShortLabel(RoleType role) => role switch
        {
            RoleType.Tank => "T",
            RoleType.Healer => "H",
            RoleType.MeleeDPS => "M",
            RoleType.PhysRangedDPS => "R",
            RoleType.MagicRangedDPS => "C",
            RoleType.Nullify => "\u2205",
            _ => "F",
        };

        private static Vector4 GetPrimaryRoleColor(PfPresetData preset)
        {
            var roles = preset.Slots.Select(s => s.Role).ToList();
            if (roles.Any(r => r == RoleType.Tank)) return RoleTank;
            if (roles.Any(r => r == RoleType.Healer)) return RoleHealer;
            if (roles.Any(r => r == RoleType.MeleeDPS || r == RoleType.PhysRangedDPS || r == RoleType.MagicRangedDPS)) return RoleDPS;
            return RoleFree;
        }

        private static Vector4 ColorFromHex(string hex)
        {
            hex = hex.TrimStart('#');
            float r, g, b, a = 1.0f;
            if (hex.Length == 8)
            {
                r = Convert.ToInt32(hex.Substring(0, 2), 16) / 255f;
                g = Convert.ToInt32(hex.Substring(2, 2), 16) / 255f;
                b = Convert.ToInt32(hex.Substring(4, 2), 16) / 255f;
                a = Convert.ToInt32(hex.Substring(6, 2), 16) / 255f;
            }
            else
            {
                r = Convert.ToInt32(hex.Substring(0, 2), 16) / 255f;
                g = Convert.ToInt32(hex.Substring(2, 2), 16) / 255f;
                b = Convert.ToInt32(hex.Substring(4, 2), 16) / 255f;
            }
            return new Vector4(r, g, b, a);
        }

        private string WrapText(string text, int lineLength)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            var sb = new StringBuilder();
            int index = 0;
            while (index < text.Length)
            {
                if (index > 0) sb.Append('\n');
                int chunkLen = Math.Min(lineLength, text.Length - index);
                sb.Append(text.Substring(index, chunkLen));
                index += chunkLen;
            }
            return sb.ToString();
        }
    }
}
