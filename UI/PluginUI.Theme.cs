using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;

namespace PfPresets
{
    /// <summary>
    /// The plugin's visual language: the blue-slate palette, the game icon ids used across
    /// windows, and the small reusable styled widgets every window is built from.
    /// </summary>
    public partial class PluginUI
    {
        // ── Color Palette (blue-slate) ────────────────────────────
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

        // ── Job Selector theme ────────────────────────────────────
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
        private static readonly Vector4 JsOkText = ColorFromHex("#eafff0");
        private static readonly Vector4 JsCancelBg = ColorFromHex("#36435a");
        private static readonly Vector4 JsCancelHover = ColorFromHex("#414f68");

        // Vivid role colors used to tint the split-person icon for multi-role slots.
        private static readonly Vector4 SplitTank = ColorFromHex("#4a78d6");
        private static readonly Vector4 SplitHealer = ColorFromHex("#3fb56a");
        private static readonly Vector4 SplitDPS = ColorFromHex("#d6584a");

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

        // ══════════════════════════════════════════════════════════
        //  SHARED STYLED WIDGETS
        // ══════════════════════════════════════════════════════════

        /// <summary>Pushes the blue-slate theme's shared ImGui colors (title bars, dropdown popups,
        /// scrollbars, checkmarks, frames, …) so every plugin window matches.
        /// Returns the number of colors pushed.</summary>
        private static int PushPluginTheme()
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

        /// <summary>Pushes the standard bordered-input style (card background, default border).
        /// Balance with <see cref="PopFramedInput"/>.</summary>
        private static void PushFramedInput()
        {
            ImGui.PushStyleColor(ImGuiCol.FrameBg, BgCard);
            ImGui.PushStyleColor(ImGuiCol.Border, BorderDefault);
            ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 1.0f);
        }

        private static void PopFramedInput()
        {
            ImGui.PopStyleVar();
            ImGui.PopStyleColor(2);
        }

        /// <summary>The green confirm button used across windows (Save / OK / Create / Done).</summary>
        private static bool DrawPrimaryButton(string label, Vector2 size)
        {
            ImGui.PushStyleColor(ImGuiCol.Button, JsOkBg);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, JsOkHover);
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, JsOkHover);
            ImGui.PushStyleColor(ImGuiCol.Text, JsOkText);
            ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 6.0f);
            bool clicked = ImGui.Button(label, size);
            ImGui.PopStyleVar();
            ImGui.PopStyleColor(4);
            return clicked;
        }

        /// <summary>The neutral cancel/secondary button used across windows.</summary>
        private static bool DrawSecondaryButton(string label, Vector2 size)
        {
            ImGui.PushStyleColor(ImGuiCol.Button, JsCancelBg);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, JsCancelHover);
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, JsCancelHover);
            ImGui.PushStyleColor(ImGuiCol.Text, JsText);
            ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 6.0f);
            bool clicked = ImGui.Button(label, size);
            ImGui.PopStyleVar();
            ImGui.PopStyleColor(4);
            return clicked;
        }

        /// <summary>Section heading inside the editor/settings windows.</summary>
        private static void DrawSectionLabel(string label)
        {
            ImGui.TextColored(AccentBlue, label);
            ImGui.Dummy(new Vector2(0, 2));
        }

        private static bool DrawStyledCheckbox(string label, ref bool value)
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

        /// <summary>Tooltip with proper padding. The main window uses zero WindowPadding for its
        /// full-bleed layout, which tooltips would otherwise inherit (looking cramped), so we push
        /// real padding around the tooltip.</summary>
        private static void PaddedTooltip(string text)
        {
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(10, 8));
            ImGui.SetTooltip(text);
            ImGui.PopStyleVar();
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
            RoleType.Omit => "∅",
            _ => "F",
        };

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
    }
}
