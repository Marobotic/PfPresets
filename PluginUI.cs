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

        // ── Job Selector State ────────────────────────────────────
        private int jobSelectorSlotIndex = -1;
        private bool showJobSelector = false;
        private RoleType tempSelectorRole;
        private ulong tempSelectorJobFlags;

        // ── Color Palette (blue-slate, matching the Job Selector) ──
        private static readonly Vector4 BgOuter = ColorFromHex("#212c3e");
        private static readonly Vector4 BgCard = ColorFromHex("#2a3850");
        private static readonly Vector4 BgCardExpanded = ColorFromHex("#1b2536");
        private static readonly Vector4 BgDropdown = ColorFromHex("#28344a");
        private static readonly Vector4 BorderDefault = ColorFromHex("#3a4860");
        private static readonly Vector4 BorderHover = ColorFromHex("#4d5e7e");
        private static readonly Vector4 BorderActiveAccent = ColorFromHex("#6fa8dc55");
        private static readonly Vector4 TextPrimary = ColorFromHex("#dfe6f1");
        private static readonly Vector4 TextSecondary = ColorFromHex("#9aa7bb");
        private static readonly Vector4 TextMuted = ColorFromHex("#6e7c91");
        private static readonly Vector4 TextHover = ColorFromHex("#c4cfe0");
        private static readonly Vector4 AccentBlue = ColorFromHex("#6fa8dc");
        private static readonly Vector4 AccentGreen = ColorFromHex("#3fb56a");
        private static readonly Vector4 AccentRed = ColorFromHex("#e06a5a");
        private static readonly Vector4 AccentYellow = ColorFromHex("#ffbd2e");
        private static readonly Vector4 AccentPurple = ColorFromHex("#9b6dff");

        // ── Role Colors ───────────────────────────────────────────
        private static readonly Vector4 RoleTank = ColorFromHex("#3752d8");
        private static readonly Vector4 RoleHealer = ColorFromHex("#2e8b57");
        private static readonly Vector4 RoleDPS = ColorFromHex("#c43333");
        private static readonly Vector4 RoleFree = ColorFromHex("#808080");

        // ── Role / Slot Icon IDs ──────────────────────────────────
        // Game UI role icons. The game's role-icon set runs contiguously:
        //   62581 Tank, 62582 Healer, 62583 DPS, 62584 Melee, 62585 Phys Ranged,
        //   62586 Magic Ranged, 62587 All-Rounder.
        // Free (any job) and Omit have no game role icon, so they use Dalamud's bundled
        // FontAwesome glyphs (a grey person and a circle-with-slash) which always render.
        private const uint IconRoleTank = 62581;
        private const uint IconRoleHealer = 62582;
        private const uint IconRoleDps = 62583;
        private const uint IconAllRounder = 62587; // used for any multi-role slot (2 or 3 roles)
        private const uint IconJobBase = 62100;    // job icon = IconJobBase + ClassJob RowId
        private const FontAwesomeIcon FreeGlyph = FontAwesomeIcon.User; // grey "any job" person
        private const FontAwesomeIcon OmitGlyph = FontAwesomeIcon.Ban;  // circle-with-slash

        // Duty-category icons (game ContentType icons), indexed by DutyCategoryId
        // (= index into DutyCategories.Names). 0 = no icon.
        private static readonly uint[] DutyCategoryIcons =
        {
            0,      // None
            61807,  // Duty Roulette
            61801,  // Dungeons
            61803,  // Guildhests
            61804,  // Trials
            61802,  // Raids
            61832,  // High-end Duty
            61806,  // PvP
            61820,  // Gold Saucer
            61809,  // FATEs
            61808,  // Treasure Hunt
            61819,  // The Hunt
            61815,  // Gathering Forays
            61824,  // Deep Dungeons
            61837,  // Field Operations
            61846,  // V&C Dungeon Finder
        };

        private static uint GetCategoryIcon(int categoryId)
            => (categoryId >= 0 && categoryId < DutyCategoryIcons.Length) ? DutyCategoryIcons[categoryId] : 0;

        // ── Job Selector theme (blue-slate, matching the target mockup) ──
        private static readonly Vector4 JsBg = ColorFromHex("#212c3e");
        private static readonly Vector4 JsTitle = ColorFromHex("#2b3850");
        private static readonly Vector4 JsBorder = ColorFromHex("#3a4860");
        private static readonly Vector4 JsAccent = ColorFromHex("#6fa8dc");   // light blue (headings)
        private static readonly Vector4 JsText = ColorFromHex("#d6deea");
        private static readonly Vector4 JsMuted = ColorFromHex("#8b97a9");
        private static readonly Vector4 JsConnector = ColorFromHex("#4b5a72");
        private static readonly Vector4 JsTank = ColorFromHex("#6f8fd6");
        private static readonly Vector4 JsHealer = ColorFromHex("#67c184");
        private static readonly Vector4 JsDPS = ColorFromHex("#cf8b76");
        private static readonly Vector4 JsOkBg = ColorFromHex("#2f7d4e");
        private static readonly Vector4 JsOkHover = ColorFromHex("#379059");
        private static readonly Vector4 JsCancelBg = ColorFromHex("#36435a");
        private static readonly Vector4 JsCancelHover = ColorFromHex("#414f68");

        // Vivid role colors used to tint the split-person icon for multi-role slots.
        private static readonly Vector4 SplitTank = ColorFromHex("#4a78d6");
        private static readonly Vector4 SplitHealer = ColorFromHex("#3fb56a");
        private static readonly Vector4 SplitDPS = ColorFromHex("#d6584a");

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
            int themeColors = PushPluginTheme();
            DrawMainWindow();
            DrawEditorWindow();
            DrawSettingsWindow();
            DrawChecklistOverlay();
            DrawJobSelectorWindow();
            ImGui.PopStyleColor(themeColors);
        }

        /// <summary>Pushes the blue-slate theme's shared ImGui colors (title bars, dropdown popups,
        /// scrollbars, checkmarks, frames, …) so every plugin window matches the Job Selector.
        /// Returns the number of colors pushed.</summary>
        private int PushPluginTheme()
        {
            (ImGuiCol Col, Vector4 Value)[] theme =
            {
                (ImGuiCol.TitleBg, JsTitle),
                (ImGuiCol.TitleBgActive, JsTitle),
                (ImGuiCol.TitleBgCollapsed, JsTitle),
                (ImGuiCol.FrameBg, BgCard),
                (ImGuiCol.FrameBgHovered, BorderDefault),
                (ImGuiCol.FrameBgActive, BorderHover),
                (ImGuiCol.PopupBg, BgDropdown),
                (ImGuiCol.Header, ColorFromHex("#33405a")),
                (ImGuiCol.HeaderHovered, BorderHover),
                (ImGuiCol.HeaderActive, BorderActiveAccent),
                (ImGuiCol.CheckMark, AccentBlue),
                (ImGuiCol.SliderGrab, AccentBlue),
                (ImGuiCol.SliderGrabActive, AccentBlue),
                (ImGuiCol.ScrollbarBg, new Vector4(0, 0, 0, 0)),
                (ImGuiCol.ScrollbarGrab, BorderDefault),
                (ImGuiCol.ScrollbarGrabHovered, BorderHover),
                (ImGuiCol.ScrollbarGrabActive, AccentBlue),
                (ImGuiCol.Separator, BorderDefault),
                (ImGuiCol.Text, TextPrimary),
                (ImGuiCol.TextDisabled, TextMuted),
                (ImGuiCol.Button, BgCard),
                (ImGuiCol.ButtonHovered, BorderHover),
                (ImGuiCol.ButtonActive, BorderActiveAccent),
            };
            foreach (var (col, value) in theme)
                ImGui.PushStyleColor(col, value);
            return theme.Length;
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
            if (ImGui.IsItemHovered()) { hoveredHeaderBtn = 1; PaddedTooltip("Settings"); }

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

        /// <summary>Tag color for an objective: Loot=yellow, Duty Completion=blue, Practice=green, else grey.</summary>
        private static Vector4 GetObjectiveColor(int objectiveId) => objectiveId switch
        {
            1 => AccentBlue,    // Duty Completion
            2 => AccentGreen,   // Practice
            3 => AccentYellow,  // Loot
            _ => TextMuted,
        };

        /// <summary>Draws a FontAwesome glyph centered inside a box at a screen position.</summary>
        private void DrawGlyphAt(FontAwesomeIcon icon, Vector2 topLeft, float size, Vector4 color)
        {
            var dl = ImGui.GetWindowDrawList();
            string g = icon.ToIconString();
            using (pluginInterface.UiBuilder.IconFontHandle.Push())
            {
                Vector2 ts = ImGui.CalcTextSize(g);
                dl.AddText(new Vector2(topLeft.X + (size - ts.X) * 0.5f, topLeft.Y + (size - ts.Y) * 0.5f),
                    ImGui.ColorConvertFloat4ToU32(color), g);
            }
        }

        /// <summary>Draws a FontAwesome glyph centered within a rectangle (used to overlay icons on buttons).</summary>
        private void DrawGlyphCentered(FontAwesomeIcon icon, Vector2 rectMin, Vector2 rectMax, Vector4 color)
        {
            var dl = ImGui.GetWindowDrawList();
            string g = icon.ToIconString();
            using (pluginInterface.UiBuilder.IconFontHandle.Push())
            {
                Vector2 ts = ImGui.CalcTextSize(g);
                dl.AddText(new Vector2((rectMin.X + rectMax.X - ts.X) * 0.5f, (rectMin.Y + rectMax.Y - ts.Y) * 0.5f),
                    ImGui.ColorConvertFloat4ToU32(color), g);
            }
        }

        /// <summary>Tooltip with proper padding. The main window uses zero WindowPadding for its
        /// full-bleed layout, which tooltips would otherwise inherit (looking cramped), so we push
        /// real padding around the tooltip.</summary>
        private static void PaddedTooltip(string text)
        {
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(10, 8));
            ImGui.SetTooltip(text);
            ImGui.PopStyleVar();
        }

        /// <summary>Draws a slot's composition icon (role/job/split-person/free/omit) at a screen position.</summary>
        private void DrawSlotMiniIcon(RoleSlot slot, Vector2 topLeft, float size)
        {
            if (slot.Role == RoleType.Omit) { DrawGlyphAt(OmitGlyph, topLeft, size, TextMuted); return; }
            if (IsFreeAnySlot(slot)) { DrawGlyphAt(FreeGlyph, topLeft, size, TextSecondary); return; }
            var split = GetSplitRoleColors(slot);
            if (split != null) { DrawSplitRolePerson(topLeft, size, split); return; }
            uint? gi = GetSlotDisplayIcon(slot);
            if (gi.HasValue && TryGetIconHandle(gi.Value, out var h))
                ImGui.GetWindowDrawList().AddImage(h, topLeft, new Vector2(topLeft.X + size, topLeft.Y + size));
            else
                DrawGlyphAt(FreeGlyph, topLeft, size, TextSecondary);
        }

        /// <summary>Draws an auto-adjusted slot's icon: the specific job if known, else the role icon.</summary>
        private void DrawAutoSlotMiniIcon(RoleType role, uint? jobId, Vector2 topLeft, float size)
        {
            var dl = ImGui.GetWindowDrawList();
            Vector2 br = new Vector2(topLeft.X + size, topLeft.Y + size);
            if (jobId.HasValue && jobId.Value > 0 && TryGetIconHandle(IconJobBase + jobId.Value, out var jh))
            {
                dl.AddImage(jh, topLeft, br);
                return;
            }
            uint ric = role switch
            {
                RoleType.Tank => IconRoleTank,
                RoleType.Healer => IconRoleHealer,
                RoleType.MeleeDPS or RoleType.PhysRangedDPS or RoleType.MagicRangedDPS => IconRoleDps,
                _ => 0u,
            };
            if (ric != 0 && TryGetIconHandle(ric, out var rh))
                dl.AddImage(rh, topLeft, br);
            else
                DrawGlyphAt(FreeGlyph, topLeft, size, TextSecondary);
        }

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
                        tx += DrawTagPill(new Vector2(tx, ty), PfAutomation.GetObjectiveName(preset.ObjectiveId), GetObjectiveColor(preset.ObjectiveId)) + 5f;
                    if (preset.CompletionStatusEnabled)
                        tx += DrawTagPill(new Vector2(tx, ty), PfAutomation.GetCompletionStatusName(preset.CompletionStatusType), TextMuted) + 5f;
                    if (preset.OnePlayerPerJob)
                        DrawTagPill(new Vector2(tx, ty), "One Player per Job", TextMuted);
                    dl.PopClipRect();
                }

                // ── Comment (white, wrapped) ──
                if (hasComment)
                {
                    ImGui.SetCursorScreenPos(new Vector2(cardPos.X + leftX, cardPos.Y + 90f));
                    ImGui.PushTextWrapPos(leftMaxX);
                    ImGui.TextColored(TextPrimary, preset.Comment);
                    ImGui.PopTextWrapPos();
                }

                // ── Right column (always visible) ──
                // Composition icons. For Auto-Adjust presets, show the game's auto-sought comp.
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
                // Loot rule (+ password when forming a private party), on one line.
                dl.AddText(new Vector2(rightX, cardPos.Y + 40f), ImGui.ColorConvertFloat4ToU32(TextSecondary), "Loot:");
                float lootValX = rightX + ImGui.CalcTextSize("Loot:").X + 6f;
                dl.AddText(new Vector2(lootValX, cardPos.Y + 40f), ImGui.ColorConvertFloat4ToU32(TextPrimary), PfAutomation.GetLootRuleName(preset.LootRules));
                if (preset.FormPrivateParty)
                {
                    float py = cardPos.Y + 62f;
                    DrawGlyphAt(FontAwesomeIcon.Lock, new Vector2(rightX, py), 15f, AccentYellow);
                    dl.AddText(new Vector2(rightX + 20f, py + 1f), ImGui.ColorConvertFloat4ToU32(TextPrimary), preset.PasswordDisplay);
                }

                // ── Apply Preset button + kebab menu (always visible, bottom-right) ──
                const float btnH = 30f, kebabW = 30f, btnGap = 6f;
                float applyW = rightW - kebabW - btnGap;
                float btnRowY = cardPos.Y + cardHeight - btnH - 8f;
                bool canRecruit = pfAutomation.CanRecruit(out var reason);

                Vector2 applyPos = new Vector2(rightX, btnRowY);
                Vector4 applyBg = canRecruit ? JsOkBg : ColorFromHex("#3a4456");
                Vector4 applyText = canRecruit ? ColorFromHex("#eafff0") : new Vector4(0.85f, 0.89f, 0.96f, 0.45f);
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

        private void DrawFooter()
        {
            Vector2 curPos = ImGui.GetCursorScreenPos();
            float width = ImGui.GetWindowWidth();
            ImGui.GetWindowDrawList().AddLine(new Vector2(ImGui.GetWindowPos().X, curPos.Y), new Vector2(ImGui.GetWindowPos().X + width, curPos.Y), ImGui.ColorConvertFloat4ToU32(BorderDefault), 1.0f);

            ImGui.SetCursorScreenPos(new Vector2(ImGui.GetWindowPos().X + 12, curPos.Y + 8));
            ImGui.PushStyleColor(ImGuiCol.Button, JsOkBg);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, JsOkHover);
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, JsOkHover);
            ImGui.PushStyleColor(ImGuiCol.Text, ColorFromHex("#eafff0"));
            ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 6.0f);

            ImGui.PushFont(UiBuilder.IconFont);
            string plusIcon = FontAwesomeIcon.Plus.ToIconString();
            ImGui.PopFont();

            // Disabled while a preset is being created/edited (the editor window is open).
            bool editingInProgress = isEditorWindowVisible;
            if (editingInProgress) ImGui.BeginDisabled();
            if (ImGui.Button($"  {plusIcon}  Create New Preset##CreatePreset", new Vector2(width - 24, 30)))
            {
                var newPreset = config.AddPreset();
                OpenEditor(newPreset, true);
            }
            if (editingInProgress) ImGui.EndDisabled();
            ImGui.PopStyleVar();
            ImGui.PopStyleColor(4);
            if (editingInProgress && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                PaddedTooltip("Finish or close the current preset first.");
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

            ImGui.PushStyleColor(ImGuiCol.Button, JsOkBg);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, JsOkHover);
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, JsOkHover);
            ImGui.PushStyleColor(ImGuiCol.Text, ColorFromHex("#eafff0"));
            ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 6.0f);
            if (ImGui.Button("  Save Preset##SavePreset", new Vector2(btnWidth, 30)))
                SaveEditor();
            ImGui.PopStyleVar();
            ImGui.PopStyleColor(4);

            ImGui.SameLine(0, 12);

            ImGui.PushStyleColor(ImGuiCol.Button, JsCancelBg);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, JsCancelHover);
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, JsCancelHover);
            ImGui.PushStyleColor(ImGuiCol.Text, JsText);
            ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 6.0f);
            if (ImGui.Button("  Cancel##CancelPreset", new Vector2(btnWidth, 30)))
                CancelEditor();
            ImGui.PopStyleVar();
            ImGui.PopStyleColor(4);
        }

        // ── Duty Category Selector (matches in-game dropdown) ─────
        private void DrawDutyCategorySelector()
        {
            // Category dropdown
            ImGui.PushStyleColor(ImGuiCol.FrameBg, BgCard);
            ImGui.PushStyleColor(ImGuiCol.Border, BorderDefault);
            ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 1.0f);
            // All in-game duty categories (index in DutyCategories.Names == category id).
            int catId = editorDutyCategoryId;
            if (catId < 0 || catId >= DutyCategories.Names.Length) catId = 0;

            // Icon for the currently-selected category, drawn to the left of the dropdown.
            uint selIcon = GetCategoryIcon(catId);
            if (selIcon != 0 && TryGetIconHandle(selIcon, out var selHandle))
            {
                ImGui.SetCursorPosY(ImGui.GetCursorPosY() + 1f);
                ImGui.Image(selHandle, new Vector2(18, 18));
                ImGui.SameLine(0, 6);
            }

            ImGui.SetNextItemWidth(-1);
            if (ImGui.BeginCombo("##DutyCategory", DutyCategories.Names[catId]))
            {
                for (int i = 0; i < DutyCategories.Names.Length; i++)
                {
                    Vector2 rp = ImGui.GetCursorScreenPos();
                    bool sel = i == catId;
                    if (ImGui.Selectable($"##cat_{i}", sel, ImGuiSelectableFlags.None, new Vector2(0, 20)))
                    {
                        editorDutyCategoryId = i;
                        editorDutyCategoryName = DutyCategories.Names[i];
                        editorDutyIndex = 0;
                        if (i == 0)
                            editorDutyName = "None";
                        else
                        {
                            var d = dutyDataHelper.GetDutiesByType(editorDutyCategoryName);
                            editorDutyName = d.Count > 0 ? d[0].Name : editorDutyCategoryName;
                        }
                    }
                    var cdl = ImGui.GetWindowDrawList();
                    float textX = rp.X + 2f;
                    uint ic = GetCategoryIcon(i);
                    if (ic != 0 && TryGetIconHandle(ic, out var ih))
                    {
                        cdl.AddImage(ih, new Vector2(rp.X + 2f, rp.Y + 1f), new Vector2(rp.X + 20f, rp.Y + 19f));
                        textX = rp.X + 26f;
                    }
                    cdl.AddText(new Vector2(textX, rp.Y + 2f),
                        ImGui.ColorConvertFloat4ToU32(sel ? AccentBlue : TextPrimary), DutyCategories.Names[i]);
                }
                ImGui.EndCombo();
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
                    ImGui.SetNextItemWidth(-1);
                    // Dropdown menu, but with the popup capped to a max height so a long duty
                    // list scrolls inside the dropdown instead of filling the whole screen.
                    ImGui.SetNextWindowSizeConstraints(new Vector2(0, 0), new Vector2(float.MaxValue, 320));
                    if (ImGui.BeginCombo("##DutySelection", editorDutyName))
                    {
                        for (int di = 0; di < duties.Count; di++)
                        {
                            var duty = duties[di];
                            bool isSel = duty.Name.Equals(editorDutyName, StringComparison.OrdinalIgnoreCase);
                            if (ImGui.Selectable($"{duty.Name}##duty_{di}", isSel))
                            {
                                editorDutyName = duty.Name;
                                editorDutyIndex = di;
                            }
                            if (isSel)
                                ImGui.SetItemDefaultFocus();
                        }
                        ImGui.EndCombo();
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
                FontAwesomeIcon? glyph = null;
                Vector4[]? splitColors = null;
                Vector4 slotColor = GetRoleColor(slot.Role);
                string tooltip = "";

                if (editorAutoAdjustRoles && autoSlots != null && i < autoSlots.Count)
                {
                    var autoSlot = autoSlots[i];
                    slotColor = GetRoleColor(autoSlot.Role);
                    if (autoSlot.JobId.HasValue)
                    {
                        iconId = IconJobBase + autoSlot.JobId.Value;
                    }
                    else
                    {
                        switch (autoSlot.Role)
                        {
                            case RoleType.Tank: iconId = IconRoleTank; break;
                            case RoleType.Healer: iconId = IconRoleHealer; break;
                            case RoleType.MeleeDPS:
                            case RoleType.PhysRangedDPS:
                            case RoleType.MagicRangedDPS: iconId = IconRoleDps; break;
                            case RoleType.Omit: glyph = OmitGlyph; break;
                            default: glyph = FreeGlyph; break;
                        }
                    }
                    tooltip = autoSlot.Tooltip;
                }
                else
                {
                    if (isSlot1)
                    {
                        if (pfAutomation.PlayerState.IsLoaded && pfAutomation.PlayerState.ClassJob.RowId > 0)
                        {
                            iconId = IconJobBase + pfAutomation.PlayerState.ClassJob.RowId;
                        }
                        else
                        {
                            glyph = FreeGlyph; // unknown job → grey person
                        }
                        slotColor = TextPrimary;
                        string jobName = (pfAutomation.PlayerState.IsLoaded && pfAutomation.PlayerState.ClassJob.RowId > 0) ? pfAutomation.PlayerState.ClassJob.Value.Name.ToString() : "Unknown";
                        tooltip = $"Slot 1: You ({jobName})\nThis slot is locked to your current class/job.";
                    }
                    else
                    {
                        if (slot.Role == RoleType.Omit) glyph = OmitGlyph;
                        else if (IsFreeAnySlot(slot)) glyph = FreeGlyph;
                        else
                        {
                            splitColors = GetSplitRoleColors(slot);
                            if (splitColors == null) iconId = GetSlotDisplayIcon(slot);
                        }
                        string jobInfo = slot.AcceptedJobFlags == 0 ? "All jobs" : "Custom jobs";
                        tooltip = $"Slot {i + 1}: {PfAutomation.GetRoleName(slot.Role)}\nJobs: {jobInfo}\nClick: open job/role selector";
                    }
                }

                if (isSlot1)
                {
                    Vector2 pos = ImGui.GetCursorScreenPos();
                    if (glyph.HasValue)
                    {
                        DrawGlyphButton(glyph.Value, $"Slot1Icon", new Vector2(slotSize, slotSize), TextSecondary);
                    }
                    else if (iconId.HasValue)
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
                    if (splitColors != null)
                    {
                        Vector2 sp = ImGui.GetCursorScreenPos();
                        clicked = ImGui.InvisibleButton($"Slot_{i}", new Vector2(slotSize, slotSize));
                        DrawSplitRolePerson(sp, slotSize, splitColors);
                    }
                    else if (glyph.HasValue)
                    {
                        clicked = DrawGlyphButton(glyph.Value, $"Slot_{i}", new Vector2(slotSize, slotSize), TextSecondary);
                    }
                    else if (iconId.HasValue && TryGetIconHandle(iconId.Value, out var handle))
                    {
                        ImGui.PushID($"SlotBtn_{i}");
                        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(2, 2));
                        clicked = ImGui.ImageButton(handle, new Vector2(slotSize - 4f, slotSize - 4f));
                        ImGui.PopStyleVar();
                        ImGui.PopID();
                    }
                    else
                    {
                        // Fallback to a short text label when the icon can't be loaded.
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

        /// <summary>Tries to resolve a game icon handle. Returns false if the icon can't be loaded,
        /// letting callers fall back to a text label instead of rendering a blank image.</summary>
        private bool TryGetIconHandle(uint iconId, out ImTextureID handle)
        {
            try
            {
                var tex = textureProvider.GetFromGameIcon(new Dalamud.Interface.Textures.GameIconLookup { IconId = iconId });
                if (tex != null && tex.TryGetWrap(out var wrap, out _))
                {
                    handle = wrap.Handle;
                    return true;
                }
            }
            catch {}
            handle = default;
            return false;
        }

        /// <summary>Renders a FontAwesome glyph as a square button. Always available (bundled font),
        /// so it never renders blank like an unknown game icon would.</summary>
        private bool DrawGlyphButton(FontAwesomeIcon icon, string id, Vector2 size, Vector4 textColor)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, textColor);
            bool clicked;
            using (pluginInterface.UiBuilder.IconFontHandle.Push())
            {
                clicked = ImGui.Button($"{icon.ToIconString()}##{id}", size);
            }
            ImGui.PopStyleColor();
            return clicked;
        }

        /// <summary>Draws a FontAwesome glyph inline at the cursor (no button chrome).</summary>
        private void DrawGlyph(FontAwesomeIcon icon, Vector4 color)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, color);
            using (pluginInterface.UiBuilder.IconFontHandle.Push())
            {
                ImGui.TextUnformatted(icon.ToIconString());
            }
            ImGui.PopStyleColor();
        }

        /// <summary>Returns which of the three top-level role groups (Tank / Healer / DPS) are
        /// represented by the given accepted-job bitfield. All DPS sub-roles count as one "DPS" group.</summary>
        private static (bool Tank, bool Healer, bool Dps) GetRoleGroupsFromFlags(ulong flags)
        {
            bool tank = false, healer = false, dps = false;
            foreach (var job in JobData.AllJobsAndClasses)
            {
                if ((flags & (1UL << job.BitIndex)) == 0) continue;
                switch (job.Category)
                {
                    case JobCategory.Tank: tank = true; break;
                    case JobCategory.PureHealer:
                    case JobCategory.BarrierHealer: healer = true; break;
                    case JobCategory.MeleeDPS:
                    case JobCategory.PhysRangedDPS:
                    case JobCategory.MagicRangedDPS: dps = true; break;
                }
            }
            return (tank, healer, dps);
        }

        /// <summary>Determines the icon for a recruitment slot based on its role and accepted jobs:
        /// a single job → that job's icon; one role group → that role icon; two role groups → the
        /// two-role icon; all three → the All-Rounder icon; Free → the person icon; Omit → the slash icon.</summary>
        /// <summary>Game icon for a slot, or null when the slot is Free/Omit (the caller draws those
        /// with FontAwesome glyphs instead).</summary>
        private uint? GetSlotDisplayIcon(RoleSlot slot)
        {
            if (slot.Role == RoleType.Omit)
                return null; // drawn with the Ban glyph

            // No custom job restriction: use the role's icon (Free is drawn with the person glyph).
            if (slot.AcceptedJobFlags == 0)
            {
                return slot.Role switch
                {
                    RoleType.Tank => IconRoleTank,
                    RoleType.Healer => IconRoleHealer,
                    RoleType.MeleeDPS or RoleType.PhysRangedDPS or RoleType.MagicRangedDPS => IconRoleDps,
                    _ => null, // Free
                };
            }

            // Exactly one job selected → show that job's icon.
            if ((slot.AcceptedJobFlags & (slot.AcceptedJobFlags - 1)) == 0)
            {
                foreach (var job in JobData.AllJobsAndClasses)
                {
                    if ((1UL << job.BitIndex) == slot.AcceptedJobFlags)
                        return IconJobBase + (uint)job.Id;
                }
            }

            // Multiple jobs → one role group shows that role icon; 2+ role groups show All-Rounder.
            var (tank, healer, dps) = GetRoleGroupsFromFlags(slot.AcceptedJobFlags);
            int groups = (tank ? 1 : 0) + (healer ? 1 : 0) + (dps ? 1 : 0);
            if (groups >= 2) return IconAllRounder;
            if (tank) return IconRoleTank;
            if (healer) return IconRoleHealer;
            return IconRoleDps;
        }

        /// <summary>True when a slot accepts any job (Free) and so should show the person glyph.</summary>
        private static bool IsFreeAnySlot(RoleSlot slot)
            => slot.Role == RoleType.Free && slot.AcceptedJobFlags == 0;

        /// <summary>If a slot's jobs span two or three role groups, returns the role colours (in
        /// Tank → Healer → DPS order) used to tint the split-person icon; otherwise null.</summary>
        private Vector4[]? GetSplitRoleColors(RoleSlot slot)
        {
            if (slot.AcceptedJobFlags == 0) return null;
            var (tank, healer, dps) = GetRoleGroupsFromFlags(slot.AcceptedJobFlags);
            int n = (tank ? 1 : 0) + (healer ? 1 : 0) + (dps ? 1 : 0);
            if (n < 2) return null;
            var list = new List<Vector4>(3);
            if (tank) list.Add(SplitTank);
            if (healer) list.Add(SplitHealer);
            if (dps) list.Add(SplitDPS);
            return list.ToArray();
        }

        /// <summary>Draws the combined-role icon: the role colours fill the background as equal
        /// triangles (half/half for two roles, thirds for three) with a neutral person silhouette on
        /// top. The person is never tinted — only the background carries the colours.</summary>
        private void DrawSplitRolePerson(Vector2 topLeft, float size, Vector4[] colors)
        {
            var dl = ImGui.GetWindowDrawList();
            Vector2 tl = topLeft;
            Vector2 tr = new Vector2(topLeft.X + size, topLeft.Y);
            Vector2 br = new Vector2(topLeft.X + size, topLeft.Y + size);
            Vector2 bl = new Vector2(topLeft.X, topLeft.Y + size);
            Vector2 c = new Vector2(topLeft.X + size * 0.5f, topLeft.Y + size * 0.5f);
            float h = size * 0.5f;

            uint Col(Vector4 v) => ImGui.ColorConvertFloat4ToU32(v);

            if (colors.Length >= 3)
            {
                // Three equal wedges from the centre (one pointing up, two down).
                Vector2 Boundary(float deg)
                {
                    float r = deg * (MathF.PI / 180f);
                    float dx = MathF.Cos(r), dy = MathF.Sin(r);
                    float t = h / MathF.Max(MathF.Abs(dx), MathF.Abs(dy));
                    return new Vector2(c.X + dx * t, c.Y + dy * t);
                }
                float[] cornerAng = { -135f, -45f, 45f, 135f };
                Vector2[] cornerPt = { tl, tr, br, bl };
                int n = colors.Length;
                float seg = 360f / n;
                for (int i = 0; i < n; i++)
                {
                    float a0 = -150f + i * seg;
                    float a1 = a0 + seg;
                    var perim = new List<Vector2> { Boundary(a0) };
                    var mids = new List<(float Ang, Vector2 P)>();
                    for (int k = 0; k < 4; k++)
                    {
                        float ca = cornerAng[k];
                        while (ca < a0) ca += 360f;
                        if (ca > a0 && ca < a1) mids.Add((ca, cornerPt[k]));
                    }
                    mids.Sort((x, y) => x.Ang.CompareTo(y.Ang));
                    foreach (var m in mids) perim.Add(m.P);
                    perim.Add(Boundary(a1));
                    for (int j = 0; j < perim.Count - 1; j++)
                        dl.AddTriangleFilled(c, perim[j], perim[j + 1], Col(colors[i]));
                }
            }
            else if (colors.Length == 2)
            {
                // Diagonal half/half (two triangles).
                dl.AddTriangleFilled(tl, tr, br, Col(colors[0]));
                dl.AddTriangleFilled(tl, br, bl, Col(colors[1]));
            }
            else
            {
                dl.AddRectFilled(tl, br, Col(colors[0]), 0f);
            }

            // Border.
            dl.AddRect(tl, br, Col(ColorFromHex("#1b2230")), 4f, ImDrawFlags.None, 1.5f);

            // Neutral person silhouette on top (with a subtle shadow for contrast).
            string glyph = FreeGlyph.ToIconString();
            using (pluginInterface.UiBuilder.IconFontHandle.Push())
            {
                Vector2 ts = ImGui.CalcTextSize(glyph);
                Vector2 tp = new Vector2(topLeft.X + (size - ts.X) * 0.5f, topLeft.Y + (size - ts.Y) * 0.5f);
                dl.AddText(new Vector2(tp.X + 1f, tp.Y + 1f), Col(new Vector4(0, 0, 0, 0.45f)), glyph);
                dl.AddText(tp, Col(ColorFromHex("#eef2f8")), glyph);
            }
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

            if (tempSelectorRole == RoleType.Omit)
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

            if (role == RoleType.Omit)
                return 0;

            ulong roleMask = 0;
            foreach (var j in JobData.AllJobsAndClasses)
            {
                if (MapCategoryToRole(j.Category) == role)
                    roleMask |= (1UL << j.BitIndex);
            }
            return roleMask;
        }

        /// <summary>Draws a clickable job icon. Selected jobs render at full opacity, unselected at 60%
        /// (no coloured highlight ring) — selection is conveyed purely by brightness.</summary>
        private void DrawJobIcon(JobInfo job, float iconSize)
        {
            bool isSelected = IsJobSelectedInSelector(job);
            Vector2 pos = ImGui.GetCursorScreenPos();
            Vector2 br = new Vector2(pos.X + iconSize, pos.Y + iconSize);
            var drawList = ImGui.GetWindowDrawList();

            float alpha = isSelected ? 1.0f : 0.6f;
            uint tint = ImGui.ColorConvertFloat4ToU32(new Vector4(1, 1, 1, alpha));
            if (TryGetIconHandle(IconJobBase + (uint)job.Id, out var h))
                drawList.AddImage(h, pos, br, Vector2.Zero, Vector2.One, tint);

            if (ImGui.InvisibleButton($"##JobBtn_{job.Id}", new Vector2(iconSize, iconSize)))
            {
                if (tempSelectorJobFlags == 0)
                    tempSelectorJobFlags = GetDefaultFlagsForRole(tempSelectorRole);
                tempSelectorJobFlags ^= (1UL << job.BitIndex);
                tempSelectorRole = RecalcRoleFromFlags();
            }
            if (ImGui.IsItemHovered())
            {
                drawList.AddRect(pos, br, ImGui.ColorConvertFloat4ToU32(new Vector4(1, 1, 1, 0.5f)), 4f, ImDrawFlags.None, 1.5f);
                ImGui.SetTooltip(job.Name);
            }
        }

        /// <summary>Draws a role icon inside a subtle rounded, role-coloured frame (matches the mockup).</summary>
        private void DrawFramedRoleIcon(uint iconId, Vector2 topLeft, float size, Vector4 color)
        {
            var dl = ImGui.GetWindowDrawList();
            Vector2 br = new Vector2(topLeft.X + size, topLeft.Y + size);
            dl.AddRectFilled(topLeft, br, ImGui.ColorConvertFloat4ToU32(new Vector4(color.X, color.Y, color.Z, 0.14f)), 5f);
            if (TryGetIconHandle(iconId, out var h))
                dl.AddImage(h, new Vector2(topLeft.X + 2f, topLeft.Y + 2f), new Vector2(br.X - 2f, br.Y - 2f));
            dl.AddRect(topLeft, br, ImGui.ColorConvertFloat4ToU32(new Vector4(color.X, color.Y, color.Z, 0.65f)), 5f, ImDrawFlags.None, 1.5f);
        }

        /// <summary>Draws the tree connector lines linking a role header to its sub-rows.</summary>
        private void DrawTreeConnector(float contentLeftX, float parentMidY, float[] childMidYs)
        {
            if (childMidYs.Length == 0) return;
            var dl = ImGui.GetWindowDrawList();
            uint col = ImGui.ColorConvertFloat4ToU32(JsConnector);
            float vx = contentLeftX + JobSelLabelX + JobSelRoleIcon * 0.5f;
            float childIconLeft = contentLeftX + JobSelLabelX + JobSelSubIndent;
            float top = parentMidY + JobSelRoleIcon * 0.5f + 1f;
            float bottom = childMidYs[childMidYs.Length - 1];
            dl.AddLine(new Vector2(vx, top), new Vector2(vx, bottom), col, 1.5f);
            foreach (var cy in childMidYs)
                dl.AddLine(new Vector2(vx, cy), new Vector2(childIconLeft, cy), col, 1.5f);
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

        // Fixed-size job selector geometry. Columns are pixel offsets from the window's
        // content-left edge; the wide gap between the label column and the JOB column keeps
        // role labels from sitting under the job icons (prevents misclicks). Everything is
        // laid out with explicit positions for consistent margins and vertical centering.
        private const float JobSelWindowW = 700f;
        private const float JobSelWindowH = 600f;
        private const float JobSelLabelX = 22f;
        private const float JobSelJobX = 245f;
        private const float JobSelClassX = 510f;
        private const float JobSelIconSize = 30f;
        private const float JobSelIconGap = 6f;
        private const float JobSelSubIndent = 30f;
        private const float JobSelRoleIcon = 26f;

        /// <summary>Bitfield of all jobs+classes that belong to the named category.</summary>
        private ulong GetCategoryMask(string category)
        {
            ulong mask = 0;
            foreach (var idx in GetJobIndicesForCategory(category))
                mask |= (1UL << idx);
            return mask;
        }

        /// <summary>Draws one job-selector row with explicit positioning so every row shares the same
        /// columns, row height and vertical centering. Header rows (Tank / Healer / DPS) select their
        /// whole role; sub-rows are indented and select their sub-category. The whole label-and-icon
        /// area is hoverable/clickable so the role icon is part of the hover. Returns the row's icon
        /// centre Y (used to draw the tree connectors).</summary>
        private float DrawJobSelectorRow(string label, uint roleIconId, JobInfo[] jobs, JobInfo[] classes,
            Vector4 roleColor, string categoryName, RoleType roleType, bool isHeader, float indent)
        {
            bool hasIcons = jobs.Length > 0 || classes.Length > 0;
            float rowH = hasIcons ? JobSelIconSize + 10f : 30f;
            Vector2 p0 = ImGui.GetCursorScreenPos();
            float contentW = ImGui.GetContentRegionAvail().X;
            float midY = p0.Y + rowH * 0.5f;
            var drawList = ImGui.GetWindowDrawList();

            ulong categoryMask = GetCategoryMask(categoryName);
            bool isSelected = tempSelectorJobFlags != 0
                ? tempSelectorJobFlags == categoryMask
                : (!isHeader && tempSelectorRole == roleType);

            // Clickable/hover area spans the role icon AND the label (icon is part of the hover).
            float clickW = (JobSelJobX - 14f) - indent;
            if (clickW < 90f) clickW = 90f;
            ImGui.SetCursorScreenPos(new Vector2(p0.X + indent, p0.Y));
            if (ImGui.InvisibleButton($"##row_{categoryName}", new Vector2(clickW, rowH)))
                SelectRoleCategory(categoryName, roleType);
            if (ImGui.IsItemHovered())
            {
                drawList.AddRectFilled(new Vector2(p0.X + indent - 6f, p0.Y), new Vector2(p0.X + indent + clickW, p0.Y + rowH),
                    ImGui.ColorConvertFloat4ToU32(new Vector4(1, 1, 1, 0.05f)), 4f);
                ImGui.SetTooltip($"Select all {label} for this slot");
            }

            // Role icon (framed), vertically centered.
            DrawFramedRoleIcon(roleIconId, new Vector2(p0.X + indent, midY - JobSelRoleIcon * 0.5f), JobSelRoleIcon, roleColor);

            // Label text.
            float labelX = indent + JobSelRoleIcon + 8f;
            Vector4 labelColor = isHeader || isSelected ? roleColor : JsMuted;
            Vector2 ts = ImGui.CalcTextSize(label);
            drawList.AddText(new Vector2(p0.X + labelX, midY - ts.Y * 0.5f), ImGui.ColorConvertFloat4ToU32(labelColor), label);

            // Job icons.
            for (int k = 0; k < jobs.Length; k++)
            {
                ImGui.SetCursorScreenPos(new Vector2(p0.X + JobSelJobX + k * (JobSelIconSize + JobSelIconGap), midY - JobSelIconSize * 0.5f));
                DrawJobIcon(jobs[k], JobSelIconSize);
            }
            // Class icons.
            for (int k = 0; k < classes.Length; k++)
            {
                ImGui.SetCursorScreenPos(new Vector2(p0.X + JobSelClassX + k * (JobSelIconSize + JobSelIconGap), midY - JobSelIconSize * 0.5f));
                DrawJobIcon(classes[k], JobSelIconSize);
            }

            // Reserve the row so the next row flows below it.
            ImGui.SetCursorScreenPos(p0);
            ImGui.Dummy(new Vector2(contentW, rowH));
            return midY;
        }

        /// <summary>Draws the "Omit" row left-aligned: the FontAwesome circle-with-slash glyph with the
        /// "Omit" label to its right (matching the Free row). Omitting removes the slot from recruitment.</summary>
        private void DrawOmitButton(bool isLastActiveSlot)
        {
            const float rowH = 32f;
            Vector2 p0 = ImGui.GetCursorScreenPos();
            float contentW = ImGui.GetContentRegionAvail().X;
            float midY = p0.Y + rowH * 0.5f;
            var dl = ImGui.GetWindowDrawList();
            bool selected = tempSelectorRole == RoleType.Omit;
            Vector4 tint = selected ? AccentRed : JsMuted;

            if (isLastActiveSlot) ImGui.BeginDisabled(true);

            float clickW = 160f;
            ImGui.SetCursorScreenPos(new Vector2(p0.X + JobSelLabelX, p0.Y));
            bool clicked = ImGui.InvisibleButton("##row_Omit", new Vector2(clickW, rowH));
            bool hovered = ImGui.IsItemHovered(isLastActiveSlot ? ImGuiHoveredFlags.AllowWhenDisabled : ImGuiHoveredFlags.None);
            if (clicked && !isLastActiveSlot)
            {
                tempSelectorRole = RoleType.Omit;
                tempSelectorJobFlags = 0;
            }
            if (hovered)
            {
                if (!isLastActiveSlot)
                    dl.AddRectFilled(new Vector2(p0.X + JobSelLabelX - 6f, p0.Y), new Vector2(p0.X + JobSelLabelX + clickW, p0.Y + rowH),
                        ImGui.ColorConvertFloat4ToU32(new Vector4(1, 1, 1, 0.05f)), 4f);
                ImGui.SetTooltip(isLastActiveSlot
                    ? "Cannot omit the last remaining active slot."
                    : "Omit — removes this slot from recruitment (empty slot)");
            }

            // Ban glyph at the left, "Omit" text to its right.
            ImGui.SetCursorScreenPos(new Vector2(p0.X + JobSelLabelX, midY - 9f));
            DrawGlyph(OmitGlyph, tint);
            Vector2 ts = ImGui.CalcTextSize("Omit");
            dl.AddText(new Vector2(p0.X + JobSelLabelX + JobSelRoleIcon + 8f, midY - ts.Y * 0.5f),
                ImGui.ColorConvertFloat4ToU32(selected ? AccentRed : JsText), "Omit");

            if (isLastActiveSlot) ImGui.EndDisabled();

            ImGui.SetCursorScreenPos(p0);
            ImGui.Dummy(new Vector2(contentW, rowH));
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
                    if (editorSlots[idx].Role != RoleType.Omit)
                    {
                        activeCount++;
                    }
                }
                if (activeCount <= 1 && slot.Role != RoleType.Omit)
                {
                    isLastActiveSlot = true;
                }
            }

            ImGui.SetNextWindowSize(new Vector2(JobSelWindowW, JobSelWindowH), ImGuiCond.Always);
            ImGui.PushStyleColor(ImGuiCol.WindowBg, JsBg);
            ImGui.PushStyleColor(ImGuiCol.Border, JsBorder);
            ImGui.PushStyleColor(ImGuiCol.TitleBg, JsTitle);
            ImGui.PushStyleColor(ImGuiCol.TitleBgActive, JsTitle);
            ImGui.PushStyleColor(ImGuiCol.Text, JsText);
            ImGui.PushStyleColor(ImGuiCol.Separator, JsBorder);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 1.0f);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 8.0f);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(20, 16));

            var noClasses = new JobInfo[0];
            const float subIndent = JobSelLabelX + JobSelSubIndent;

            bool open = showJobSelector;
            var flags = ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoResize
                      | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;
            if (ImGui.Begin("Job Selector##JobSelectorWindow", ref open, flags))
            {
                if (!open) showJobSelector = false;

                ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(10, 5));

                // ── Header ──
                ImGui.AlignTextToFramePadding();
                ImGui.TextColored(JsAccent, $"Editing Slot {jobSelectorSlotIndex + 1}");
                ImGui.SameLine(0, 8);
                ImGui.TextColored(JsMuted, "|");
                ImGui.SameLine(0, 8);
                ImGui.AlignTextToFramePadding();
                ImGui.TextColored(JsMuted, "Pick a role, individual jobs, or omit the slot");

                ImGui.Dummy(new Vector2(0, 6));
                ImGui.Separator();
                ImGui.Dummy(new Vector2(0, 6));

                // ── Roles ──
                float contentLeftX = ImGui.GetCursorScreenPos().X;

                DrawJobSelectorRow("Tank", IconRoleTank,
                    new[] { JobData.PLD, JobData.WAR, JobData.DRK, JobData.GNB },
                    new[] { JobData.GLA, JobData.MRD },
                    JsTank, "Tank", RoleType.Tank, isHeader: true, indent: JobSelLabelX);

                float healerY = DrawJobSelectorRow("Healer", IconRoleHealer, noClasses, noClasses,
                    JsHealer, "Healer", RoleType.Healer, isHeader: true, indent: JobSelLabelX);
                float pureY = DrawJobSelectorRow("Pure Healer", IconRoleHealer,
                    new[] { JobData.WHM, JobData.AST }, new[] { JobData.CNJ },
                    JsHealer, "Pure Healer", RoleType.Healer, isHeader: false, indent: subIndent);
                float barrierY = DrawJobSelectorRow("Barrier Healer", IconRoleHealer,
                    new[] { JobData.SCH, JobData.SGE }, noClasses,
                    JsHealer, "Barrier Healer", RoleType.Healer, isHeader: false, indent: subIndent);
                DrawTreeConnector(contentLeftX, healerY, new[] { pureY, barrierY });

                float dpsY = DrawJobSelectorRow("DPS", IconRoleDps, noClasses, noClasses,
                    JsDPS, "DPS", RoleType.MeleeDPS, isHeader: true, indent: JobSelLabelX);
                float meleeY = DrawJobSelectorRow("Melee DPS", IconRoleDps,
                    new[] { JobData.MNK, JobData.DRG, JobData.NIN, JobData.SAM, JobData.RPR, JobData.VPR, JobData.BST },
                    new[] { JobData.PGL, JobData.LNC, JobData.ROG },
                    JsDPS, "Melee DPS", RoleType.MeleeDPS, isHeader: false, indent: subIndent);
                float physY = DrawJobSelectorRow("Physical Ranged DPS", IconRoleDps,
                    new[] { JobData.BRD, JobData.MCH, JobData.DNC }, new[] { JobData.ARC },
                    JsDPS, "Physical Ranged DPS", RoleType.PhysRangedDPS, isHeader: false, indent: subIndent);
                float magicY = DrawJobSelectorRow("Magical Ranged DPS", IconRoleDps,
                    new[] { JobData.BLM, JobData.SMN, JobData.RDM, JobData.PCT, JobData.BLU },
                    new[] { JobData.THM, JobData.ACN },
                    JsDPS, "Magical Ranged DPS", RoleType.MagicRangedDPS, isHeader: false, indent: subIndent);
                DrawTreeConnector(contentLeftX, dpsY, new[] { meleeY, physY, magicY });

                // ── Free / Omit ──
                ImGui.Dummy(new Vector2(0, 6));
                ImGui.Separator();
                ImGui.Dummy(new Vector2(0, 4));
                DrawFreeRow();
                DrawOmitButton(isLastActiveSlot);

                // ── Footer (anchored to the bottom) ──
                float footerH = 32f;
                float bottomY = ImGui.GetWindowContentRegionMax().Y - footerH;
                if (ImGui.GetCursorPosY() < bottomY) ImGui.SetCursorPosY(bottomY);

                float btnW = (ImGui.GetContentRegionAvail().X - 10) / 2f;

                ImGui.PushStyleColor(ImGuiCol.Button, JsOkBg);
                ImGui.PushStyleColor(ImGuiCol.ButtonHovered, JsOkHover);
                ImGui.PushStyleColor(ImGuiCol.ButtonActive, JsOkHover);
                ImGui.PushStyleColor(ImGuiCol.Text, ColorFromHex("#eafff0"));
                ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 6.0f);
                if (ImGui.Button("OK##ConfirmJobSelector", new Vector2(btnW, footerH)))
                {
                    slot.Role = tempSelectorRole;
                    slot.AcceptedJobFlags = tempSelectorJobFlags;
                    showJobSelector = false;
                }
                ImGui.PopStyleVar();
                ImGui.PopStyleColor(4);

                ImGui.SameLine(0, 10);

                ImGui.PushStyleColor(ImGuiCol.Button, JsCancelBg);
                ImGui.PushStyleColor(ImGuiCol.ButtonHovered, JsCancelHover);
                ImGui.PushStyleColor(ImGuiCol.ButtonActive, JsCancelHover);
                ImGui.PushStyleColor(ImGuiCol.Text, JsText);
                ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 6.0f);
                if (ImGui.Button("Cancel##CancelJobSelector", new Vector2(btnW, footerH)))
                {
                    showJobSelector = false;
                }
                ImGui.PopStyleVar();
                ImGui.PopStyleColor(4);

                ImGui.PopStyleVar(); // ItemSpacing
            }
            ImGui.End();
            ImGui.PopStyleVar(3);
            ImGui.PopStyleColor(6);
        }

        /// <summary>Draws the "Free" (any job) row in the same column layout as the role rows,
        /// using the FontAwesome person glyph.</summary>
        private void DrawFreeRow()
        {
            const float rowH = 32f;
            Vector2 p0 = ImGui.GetCursorScreenPos();
            float contentW = ImGui.GetContentRegionAvail().X;
            float midY = p0.Y + rowH * 0.5f;
            var drawList = ImGui.GetWindowDrawList();
            bool selected = tempSelectorRole == RoleType.Free && tempSelectorJobFlags == 0;

            // Clickable/hover area spans the person glyph and the "Free" label.
            float clickW = 160f;
            ImGui.SetCursorScreenPos(new Vector2(p0.X + JobSelLabelX, p0.Y));
            if (ImGui.InvisibleButton("##row_Free", new Vector2(clickW, rowH)))
            {
                tempSelectorRole = RoleType.Free;
                tempSelectorJobFlags = 0;
            }
            if (ImGui.IsItemHovered())
            {
                drawList.AddRectFilled(new Vector2(p0.X + JobSelLabelX - 6f, p0.Y), new Vector2(p0.X + JobSelLabelX + clickW, p0.Y + rowH),
                    ImGui.ColorConvertFloat4ToU32(new Vector4(1, 1, 1, 0.05f)), 4f);
                ImGui.SetTooltip("Set slot to Free (accepts any role and job)");
            }

            // Person glyph (gold) with "Free" label to its right, then a muted description.
            ImGui.SetCursorScreenPos(new Vector2(p0.X + JobSelLabelX, midY - 9f));
            DrawGlyph(FreeGlyph, AccentYellow);
            Vector2 ts = ImGui.CalcTextSize("Free");
            drawList.AddText(new Vector2(p0.X + JobSelLabelX + JobSelRoleIcon + 8f, midY - ts.Y * 0.5f),
                ImGui.ColorConvertFloat4ToU32(AccentYellow), "Free");
            drawList.AddText(new Vector2(p0.X + JobSelJobX, midY - ts.Y * 0.5f),
                ImGui.ColorConvertFloat4ToU32(JsMuted), "Accepts any role and job for this slot");

            ImGui.SetCursorScreenPos(p0);
            ImGui.Dummy(new Vector2(contentW, rowH));
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
            RoleType.Omit => ColorFromHex("#2d3748"),
            _ => RoleFree,
        };

        private static string GetRoleShortLabel(RoleType role) => role switch
        {
            RoleType.Tank => "T",
            RoleType.Healer => "H",
            RoleType.MeleeDPS => "M",
            RoleType.PhysRangedDPS => "R",
            RoleType.MagicRangedDPS => "C",
            RoleType.Omit => "\u2205",
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
