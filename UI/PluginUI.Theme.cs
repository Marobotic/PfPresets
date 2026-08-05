using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.ManagedFontAtlas;

namespace PfPresets
{
    /// <summary>
    /// The plugin's visual language: a flat, ruled, near-black system where structure comes from
    /// alignment and dividers rather than from decoration.
    ///
    /// Three rules the rest of the UI is built on:
    ///   - Nothing is rounded. Every rounding style var is zero, everywhere.
    ///   - One accent, chosen by the player, and it is the only accent in the chrome. Role and vote
    ///     colours are data, not chrome, and never follow it.
    ///   - No colour-only meaning: anything carrying state also carries a word or a shape.
    ///
    /// The old blue-slate names are kept as aliases onto the new tokens. Every window in the plugin
    /// referred to them by name, and re-pointing the names repaints all of them at once - a rename
    /// across a dozen files would have been a much larger diff with much more to get wrong.
    /// </summary>
    public partial class PluginUI
    {
        // ── Colour tokens ─────────────────────────────────────────
        private static readonly Vector4 Ground = ColorFromHex("#171614");        // window background
        private static readonly Vector4 Panel = ColorFromHex("#1b1a17");         // header, rail, footer
        private static readonly Vector4 Field = ColorFromHex("#211f1d");         // inputs, closed dropdowns
        private static readonly Vector4 Raised = ColorFromHex("#262421");        // hover fill
        private static readonly Vector4 RuleStrong = ColorFromHex("#34312e");    // 2px section dividers
        private static readonly Vector4 RuleHair = ColorFromHex("#2b2926");      // 1px row dividers
        private static readonly Vector4 BorderControl = ColorFromHex("#4a4642"); // input / outline borders
        private static readonly Vector4 Ink = ColorFromHex("#f4f1ee");           // primary text
        private static readonly Vector4 Dim = ColorFromHex("#a09b95");           // secondary text
        private static readonly Vector4 Faint = ColorFromHex("#6f6a65");         // help text, placeholders

        /// <summary>Text drawn on top of an accent fill. Always the ground colour, never white: the
        /// accent is picked by the player and some of the offered ones are light.</summary>
        private static readonly Vector4 OnAccent = ColorFromHex("#171614");

        /// <summary>Ko-fi, and destructive actions. Nothing else may use it, so the donate button
        /// never competes with Apply preset.</summary>
        private static readonly Vector4 KoFi = ColorFromHex("#e8503c");

        // Vote and score colours. Data, not chrome.
        private static readonly Vector4 Positive = ColorFromHex("#4ea36b");
        private static readonly Vector4 Negative = ColorFromHex("#d6584a");

        // ── Legacy aliases ────────────────────────────────────────
        // Same names the rest of the UI already uses, pointing at the new system.
        private static readonly Vector4 BgOuter = Ground;
        private static readonly Vector4 BgCard = Field;
        private static readonly Vector4 BgCardExpanded = Panel;
        private static readonly Vector4 BgDropdown = Field;
        private static readonly Vector4 BorderDefault = BorderControl;
        private static readonly Vector4 BorderHover = Raised;
        private static readonly Vector4 TextPrimary = Ink;
        private static readonly Vector4 TextSecondary = Dim;
        private static readonly Vector4 TextMuted = Faint;
        private static readonly Vector4 AccentGreen = Positive;
        private static readonly Vector4 AccentRed = Negative;
        private static readonly Vector4 AccentYellow = ColorFromHex("#e0a53c");
        private static readonly Vector4 AccentPurple = ColorFromHex("#9b6dff");

        private static readonly Vector4 StatusBorderRecruiting = RuleStrong;
        private static readonly Vector4 StatusBorderParty = RuleHair;

        private static readonly Vector4 JsBg = Ground;
        private static readonly Vector4 JsTitle = Panel;
        private static readonly Vector4 JsBorder = BorderControl;
        private static readonly Vector4 JsText = Ink;
        private static readonly Vector4 JsMuted = Dim;
        private static readonly Vector4 JsConnector = RuleStrong;
        private static readonly Vector4 JsCancelBg = new(0f, 0f, 0f, 0f);
        private static readonly Vector4 JsCancelHover = Raised;

        // ── Role colours ──────────────────────────────────────────
        // Data colours: they say what a slot is, so they stay put whatever the accent is set to.
        private static readonly Vector4 RoleTank = ColorFromHex("#3752d8");
        private static readonly Vector4 RoleHealer = ColorFromHex("#2e8b57");
        private static readonly Vector4 RoleDPS = ColorFromHex("#c43333");
        private static readonly Vector4 RoleFree = ColorFromHex("#6f6a65");

        private static readonly Vector4 JsTank = ColorFromHex("#6f8fd6");
        private static readonly Vector4 JsHealer = ColorFromHex("#67c184");
        private static readonly Vector4 JsDPS = ColorFromHex("#cf8b76");

        private static readonly Vector4 SplitTank = ColorFromHex("#4a78d6");
        private static readonly Vector4 SplitHealer = ColorFromHex("#3fb56a");
        private static readonly Vector4 SplitDPS = ColorFromHex("#d6584a");

        // ══════════════════════════════════════════════════════════
        //  ACCENT
        // ══════════════════════════════════════════════════════════

        /// <summary>
        /// The offered accents. Two colours are deliberately absent: red belongs to Ko-fi and to
        /// destructive actions, and amber is what the Apply button turns when the game is about to
        /// warn about party composition. An accent that also means "careful" is not an accent.
        /// </summary>
        private static readonly (string Hex, string Name)[] AccentChoices =
        {
            ("#9b6dff", "Purple"),
            ("#6b7bff", "Indigo"),
            ("#4a9be0", "Blue"),
            ("#2fb3a6", "Teal"),
            ("#4ea36b", "Green"),
            ("#d264c0", "Magenta"),
        };

        private const string DefaultAccentHex = "#9b6dff";

        // Held statically because the widget helpers below are static - they are pure drawing and
        // have no business holding a config reference. Refreshed once a frame from the config by
        // RefreshAccent, so a change in Settings shows up on the next frame everywhere at once.
        private static Vector4 accentColor = ColorFromHex(DefaultAccentHex);
        private static Vector4 accentHoverColor = Lighten(ColorFromHex(DefaultAccentHex), 0.12f);
        private static Vector4 accentPressedColor = Darken(ColorFromHex(DefaultAccentHex), 0.15f);
        private static string accentSourceHex = DefaultAccentHex;

        private static Vector4 Accent => accentColor;
        private static Vector4 AccentHover => accentHoverColor;
        private static Vector4 AccentPressed => accentPressedColor;

        /// <summary>Accent at a given opacity, for fills that sit behind text.</summary>
        private static Vector4 AccentAlpha(float alpha) => Accent with { W = alpha };

        // The old name for "the interactive colour". Every window used it; it now means the
        // player's accent rather than a fixed blue.
        private static Vector4 AccentBlue => Accent;
        private static Vector4 JsAccent => Accent;
        private static Vector4 SliderFill => Accent;
        private static Vector4 SliderKnob => Accent;
        private static Vector4 SliderKnobHot => AccentHover;
        private static Vector4 JsOkBg => Accent;
        private static Vector4 JsOkHover => AccentHover;
        private static Vector4 JsOkText => OnAccent;
        private static Vector4 BorderActiveAccent => AccentAlpha(0.33f);

        /// <summary>
        /// Re-reads the accent from the config, if it has changed since the last frame.
        ///
        /// Guarded on the string rather than recomputed every frame: the derived hover and pressed
        /// shades cost two colour-space conversions each, and this runs on every window's first
        /// draw call.
        /// </summary>
        private void RefreshAccent()
        {
            string hex = string.IsNullOrWhiteSpace(config.AccentColorHex)
                ? DefaultAccentHex
                : config.AccentColorHex.Trim();

            if (hex == accentSourceHex)
                return;

            Vector4 parsed;
            try
            {
                parsed = ColorFromHex(hex);
            }
            catch
            {
                // A hand-edited config with a bad value must not take the UI down; fall back and
                // remember the bad string so this doesn't re-throw every frame.
                parsed = ColorFromHex(DefaultAccentHex);
            }

            accentSourceHex = hex;
            accentColor = parsed;
            accentHoverColor = Lighten(parsed, 0.12f);
            accentPressedColor = Darken(parsed, 0.15f);
        }

        /// <summary>Blend towards white in linear space, so the result keeps its hue instead of
        /// washing out the way a straight sRGB lerp does.</summary>
        private static Vector4 Lighten(Vector4 c, float amount) => MixLinear(c, 1f, amount);

        /// <summary>Blend towards black in linear space.</summary>
        private static Vector4 Darken(Vector4 c, float amount) => MixLinear(c, 0f, amount);

        private static Vector4 MixLinear(Vector4 c, float target, float amount)
        {
            static float ToLinear(float v) => MathF.Pow(v, 2.2f);
            static float ToSrgb(float v) => MathF.Pow(v, 1f / 2.2f);

            float t = Math.Clamp(amount, 0f, 1f);
            float lt = ToLinear(Math.Clamp(target, 0f, 1f));
            return new Vector4(
                ToSrgb(ToLinear(c.X) + (lt - ToLinear(c.X)) * t),
                ToSrgb(ToLinear(c.Y) + (lt - ToLinear(c.Y)) * t),
                ToSrgb(ToLinear(c.Z) + (lt - ToLinear(c.Z)) * t),
                c.W);
        }

        // ══════════════════════════════════════════════════════════
        //  TYPE
        // ══════════════════════════════════════════════════════════
        //
        // One scale, built at real pixel sizes rather than scaled at draw time (see PluginUI.Fonts).
        // Nothing in the UI is allowed below HelpPx: help text is the whole reason a setting is
        // decidable, and 10px help is help nobody reads.

        private const float PersonPx = 24f;    // a character's own name on their card
        private const float CaptionPx = 13f;   // the small caps caption over it
        private const float TitlePx = 19f;
        private const float HeadingPx = 16f;   // list section headings
        private const float NamePx = 15f;
        private const float RowNamePx = 15f;   // a person's name in a list row
        private const float BodyPx = 13f;
        private const float LabelPx = 11f;
        private const float HelpPx = 11.5f;

        // Prefixed because the welcome screen and the profile card own faces of their own at other
        // sizes; these are the system scale, not one window's headline.
        private IFontHandle? uiPersonFont, uiCaptionFont, uiTitleFont, uiHeadingFont, uiNameFont, uiRowNameFont, uiBodyFont,
            uiLabelFont, uiHelpFont;

        private IFontHandle UiPersonFont => Font(ref uiPersonFont, PersonPx);
        private IFontHandle UiCaptionFont => Font(ref uiCaptionFont, CaptionPx);
        private IFontHandle UiTitleFont => Font(ref uiTitleFont, TitlePx);
        private IFontHandle UiHeadingFont => Font(ref uiHeadingFont, HeadingPx);
        private IFontHandle UiNameFont => Font(ref uiNameFont, NamePx);
        private IFontHandle UiRowNameFont => Font(ref uiRowNameFont, RowNamePx);
        private IFontHandle UiBodyFont => Font(ref uiBodyFont, BodyPx);
        private IFontHandle UiLabelFont => Font(ref uiLabelFont, LabelPx);
        private IFontHandle UiHelpFont => Font(ref uiHelpFont, HelpPx);

        private void DisposeScaleFonts()
        {
            uiPersonFont?.Dispose();
            uiCaptionFont?.Dispose();
            uiTitleFont?.Dispose();
            uiHeadingFont?.Dispose();
            uiNameFont?.Dispose();
            uiRowNameFont?.Dispose();
            uiBodyFont?.Dispose();
            uiLabelFont?.Dispose();
            uiHelpFont?.Dispose();
            uiPersonFont = uiCaptionFont = uiTitleFont = uiHeadingFont = null;
            uiNameFont = uiRowNameFont = null;
            uiBodyFont = uiLabelFont = uiHelpFont = null;
        }

        /// <summary>Text in one of the scale's faces, without the caller having to balance a push.</summary>
        private void TextIn(IFontHandle font, Vector4 color, string text)
        {
            using (font.Push())
                ImGui.TextColored(color, text);
        }

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

        /// <summary>
        /// The plugin's mark, wherever it identifies itself - the main window's header, the apply
        /// checklist, and the button on the game's Party Finder.
        ///
        /// One constant rather than three literals: the mark is how someone recognises that the
        /// button they pressed and the window that opened are the same thing, and that only holds
        /// while all three agree.
        /// </summary>
        private const FontAwesomeIcon LogoIcon = FontAwesomeIcon.ChartLine;

        // ══════════════════════════════════════════════════════════
        //  SHARED STYLED WIDGETS
        // ══════════════════════════════════════════════════════════

        /// <summary>How many colours and style vars <see cref="PushPluginTheme"/> pushed, so the
        /// caller can unwind exactly what it caused.</summary>
        private readonly struct ThemeScope
        {
            public ThemeScope(int colors, int vars) { Colors = colors; Vars = vars; }
            public int Colors { get; }
            public int Vars { get; }
        }

        /// <summary>
        /// Pushes the theme's shared ImGui colours and style vars so every plugin window matches.
        ///
        /// The style vars are half the design: ImGui's defaults round every frame, and a system
        /// built on rules and alignment falls apart the moment a control has soft corners. Button
        /// labels are pushed flush left here too, once, rather than in every button.
        /// </summary>
        private static ThemeScope PushPluginTheme()
        {
            (ImGuiCol Col, Vector4 Value)[] colors =
            {
                (ImGuiCol.WindowBg, Ground),
                (ImGuiCol.ChildBg, new Vector4(0, 0, 0, 0)),
                (ImGuiCol.TitleBg, Panel),
                (ImGuiCol.TitleBgActive, Panel),
                (ImGuiCol.TitleBgCollapsed, Panel),
                (ImGuiCol.FrameBg, Field),
                (ImGuiCol.FrameBgHovered, Raised),
                (ImGuiCol.FrameBgActive, Raised),
                (ImGuiCol.PopupBg, Panel),
                (ImGuiCol.Header, Raised),
                (ImGuiCol.HeaderHovered, Raised),
                (ImGuiCol.HeaderActive, AccentAlpha(0.33f)),
                (ImGuiCol.CheckMark, Accent),
                (ImGuiCol.SliderGrab, Accent),
                (ImGuiCol.SliderGrabActive, AccentPressed),
                (ImGuiCol.ScrollbarBg, new Vector4(0, 0, 0, 0)),
                (ImGuiCol.ScrollbarGrab, RuleStrong),
                (ImGuiCol.ScrollbarGrabHovered, BorderControl),
                (ImGuiCol.ScrollbarGrabActive, Accent),
                (ImGuiCol.Separator, RuleHair),
                (ImGuiCol.SeparatorHovered, RuleStrong),
                (ImGuiCol.Border, BorderControl),
                (ImGuiCol.BorderShadow, new Vector4(0, 0, 0, 0)),
                (ImGuiCol.Text, Ink),
                (ImGuiCol.TextDisabled, Faint),
                (ImGuiCol.Button, Field),
                (ImGuiCol.ButtonHovered, Raised),
                (ImGuiCol.ButtonActive, Raised),
                (ImGuiCol.NavHighlight, Accent),
            };
            foreach (var (col, value) in colors)
                ImGui.PushStyleColor(col, value);

            (ImGuiStyleVar Var, float Value)[] scalars =
            {
                (ImGuiStyleVar.FrameRounding, 0f),
                (ImGuiStyleVar.WindowRounding, 0f),
                (ImGuiStyleVar.ChildRounding, 0f),
                (ImGuiStyleVar.PopupRounding, 0f),
                (ImGuiStyleVar.GrabRounding, 0f),
                (ImGuiStyleVar.TabRounding, 0f),
                (ImGuiStyleVar.ScrollbarRounding, 0f),
            };
            foreach (var (v, value) in scalars)
                ImGui.PushStyleVar(v, value);

            // Flush left, vertically centred: the design puts every label on the same left edge,
            // including the ones inside wide buttons.
            ImGui.PushStyleVar(ImGuiStyleVar.ButtonTextAlign, new Vector2(0f, 0.5f));

            return new ThemeScope(colors.Length, scalars.Length + 1);
        }

        private static void PopPluginTheme(ThemeScope scope)
        {
            ImGui.PopStyleVar(scope.Vars);
            ImGui.PopStyleColor(scope.Colors);
        }

        /// <summary>Pushes the standard bordered-input style. Balance with <see cref="PopFramedInput"/>.</summary>
        private static void PushFramedInput()
        {
            ImGui.PushStyleColor(ImGuiCol.FrameBg, Field);
            ImGui.PushStyleColor(ImGuiCol.Border, BorderControl);
            ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 1.0f);
            ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 0f);
        }

        private static void PopFramedInput()
        {
            ImGui.PopStyleVar(2);
            ImGui.PopStyleColor(2);
        }

        /// <summary>
        /// The height of an action button, and the room its label gets on either side.
        ///
        /// One number, so two buttons that sit next to each other cannot end up different heights.
        /// The padding matters as much: labels are flush left in this design, and a button sized to
        /// its own text put the last letter hard against the right edge.
        /// </summary>
        private const float ButtonHeight = 36f;
        private const float ButtonPadX = 14f;

        /// <summary>Width a label needs at <see cref="ButtonPadX"/> on both sides.</summary>
        private static float ButtonWidthFor(string label)
        {
            int marker = label.IndexOf("##", StringComparison.Ordinal);
            string shown = marker >= 0 ? label.Substring(0, marker) : label;
            return ImGui.CalcTextSize(shown).X + ButtonPadX * 2f;
        }

        /// <summary>The one filled button on a surface: accent fill, ground-coloured text, square.</summary>
        private static bool DrawPrimaryButton(string label, Vector2 size)
        {
            ImGui.PushStyleColor(ImGuiCol.Button, Accent);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, AccentHover);
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, AccentPressed);
            ImGui.PushStyleColor(ImGuiCol.Text, OnAccent);
            ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 0f);
            ImGui.PushStyleVar(ImGuiStyleVar.FramePadding,
                new Vector2(ButtonPadX, ImGui.GetStyle().FramePadding.Y));
            bool clicked = ImGui.Button(label, size);
            ImGui.PopStyleVar();
            ImGui.PopStyleVar();
            ImGui.PopStyleColor(4);
            return clicked;
        }

        /// <summary>
        /// The everyday button: a filled surface, not an outline.
        ///
        /// It was a transparent rectangle with a grey border, which made every control that is not
        /// the one accent action look switched off - a page of ghosts around a single real button.
        /// Filled and lifted on hover, it reads as pressable at rest, and the accent stays reserved
        /// for the one action that matters most on each surface.
        /// </summary>
        private static bool DrawSecondaryButton(string label, Vector2 size)
        {
            ImGui.PushStyleColor(ImGuiCol.Button, ColorFromHex("#2f2c28"));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, ColorFromHex("#3a3733"));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, ColorFromHex("#443f3a"));
            ImGui.PushStyleColor(ImGuiCol.Text, Ink);
            ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 0f);
            ImGui.PushStyleVar(ImGuiStyleVar.FramePadding,
                new Vector2(ButtonPadX, ImGui.GetStyle().FramePadding.Y));
            bool clicked = ImGui.Button(label, size);
            ImGui.PopStyleVar(2);
            ImGui.PopStyleColor(4);
            return clicked;
        }

        /// <summary>
        /// Anything that destroys data. The same filled red as everywhere else it appears, so
        /// "this removes something" looks identical wherever it is offered.
        /// </summary>
        private static bool DrawDestructiveButton(string label, Vector2 size)
        {
            // The hover state has to be known before the button is submitted, because it decides
            // the text colour that goes with the fill - red text on the red hover fill is invisible.
            // ImGui.IsItemHovered() at this point would answer for whatever was drawn last, so the
            // rect is tested directly.
            return DrawDangerFilledButton(label, size);
        }

        /// <summary>
        /// Outlined in the accent, for an action that is the point of the surface it sits on but
        /// not the one filled button on it.
        ///
        /// Between primary and secondary: the accent border and text say "this is the thing to
        /// press here" without a second solid block of colour competing with Apply preset.
        /// </summary>
        private static bool DrawAccentOutlineButton(string label, Vector2 size)
        {
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0f, 0f, 0f, 0f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, AccentAlpha(0.18f));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, AccentAlpha(0.28f));
            ImGui.PushStyleColor(ImGuiCol.Text, Accent);
            ImGui.PushStyleColor(ImGuiCol.Border, Accent);
            ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 0f);
            ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 1f);
            ImGui.PushStyleVar(ImGuiStyleVar.FramePadding,
                new Vector2(ButtonPadX, ImGui.GetStyle().FramePadding.Y));
            bool clicked = ImGui.Button(label, size);
            ImGui.PopStyleVar();
            ImGui.PopStyleVar(2);
            ImGui.PopStyleColor(5);
            return clicked;
        }

        /// <summary>
        /// The light button: paper-white fill, ground-coloured text.
        ///
        /// For a neutral action that still deserves weight - opening a character elsewhere, going
        /// back. The accent belongs to the one thing a surface is for, and using it on every
        /// button that matters turns the whole window into one colour; white carries the same
        /// weight without spending the accent on it.
        /// </summary>
        private static bool DrawNeutralButton(string label, Vector2 size)
        {
            ImGui.PushStyleColor(ImGuiCol.Button, Ink);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, ColorFromHex("#ffffff"));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, ColorFromHex("#d9d5d0"));
            ImGui.PushStyleColor(ImGuiCol.Text, Ground);
            ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 0f);
            ImGui.PushStyleVar(ImGuiStyleVar.FramePadding,
                new Vector2(ButtonPadX, ImGui.GetStyle().FramePadding.Y));
            bool clicked = ImGui.Button(label, size);
            ImGui.PopStyleVar(2);
            ImGui.PopStyleColor(4);
            return clicked;
        }

        /// <summary>
        /// Filled red, for an action that ends something and sits beside a filled accent button.
        ///
        /// The outlined destructive style is right when the control stands alone in a settings
        /// column; it is wrong next to a solid button, where the pair reads as one real control and
        /// one ghost of a control rather than as two choices.
        /// </summary>
        private static bool DrawDangerFilledButton(string label, Vector2 size)
        {
            ImGui.PushStyleColor(ImGuiCol.Button, KoFi);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Lighten(KoFi, 0.12f));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, Darken(KoFi, 0.15f));
            ImGui.PushStyleColor(ImGuiCol.Text, Ink);
            ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 0f);
            ImGui.PushStyleVar(ImGuiStyleVar.FramePadding,
                new Vector2(ButtonPadX, ImGui.GetStyle().FramePadding.Y));
            bool clicked = ImGui.Button(label, size);
            ImGui.PopStyleVar(2);
            ImGui.PopStyleColor(4);
            return clicked;
        }

        /// <summary>
        /// Ko-fi: filled red, not outlined.
        ///
        /// It is the one button in the plugin that is a brand rather than an action, and the
        /// outlined destructive style made it read as "delete" - the exact confusion the colour
        /// rule was supposed to prevent. Destructive controls keep the outline; this one is solid,
        /// so the two are told apart by weight as well as by wording.
        /// </summary>
        private static bool DrawKofiButton(string label, Vector2 size)
        {
            ImGui.PushStyleColor(ImGuiCol.Button, KoFi);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Lighten(KoFi, 0.12f));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, Darken(KoFi, 0.15f));
            ImGui.PushStyleColor(ImGuiCol.Text, Ink);
            ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 0f);
            ImGui.PushStyleVar(ImGuiStyleVar.FramePadding,
                new Vector2(ButtonPadX, ImGui.GetStyle().FramePadding.Y));
            bool clicked = ImGui.Button(label, size);
            ImGui.PopStyleVar();
            ImGui.PopStyleVar();
            ImGui.PopStyleColor(4);
            return clicked;
        }

        /// <summary>
        /// The search field: one implementation, used by every surface that has a search.
        ///
        /// It existed twice - once in the Recruit toolbar and once above the player list - which is
        /// how the two ended up different sizes with the magnifier inside one and outside the
        /// other. Anything that is a component of the design lives here now, and the surfaces call
        /// it rather than re-describing it.
        ///
        /// The glyph sits inside the field and the text is inset past its measured width, so the
        /// two can never overlap however the font is sized.
        /// </summary>
        private bool DrawSearchField(string id, string hint, ref string value, float width,
            float height = ButtonHeight)
            => DrawSearchFieldCore(id, hint, ref value, width, height, ImGuiInputTextFlags.None);

        private bool DrawSearchFieldCore(string id, string hint, ref string value, float width,
            float height, ImGuiInputTextFlags flags)
        {
            Vector2 origin = ImGui.GetCursorScreenPos();
            var dl = ImGui.GetWindowDrawList();

            float glyphW;
            using (pluginInterface.UiBuilder.IconFontHandle.Push())
                glyphW = ImGui.CalcTextSize(FontAwesomeIcon.Search.ToIconString()).X;

            float inset = 10f + glyphW + 8f;

            ImGui.PushStyleVar(ImGuiStyleVar.FramePadding,
                new Vector2(inset, (height - ImGui.GetTextLineHeight()) * 0.5f));
            PushFramedInput();
            ImGui.SetNextItemWidth(width);
            bool changed = ImGui.InputTextWithHint($"##{id}", hint, ref value, 128, flags);
            PopFramedInput();
            ImGui.PopStyleVar();

            using (pluginInterface.UiBuilder.IconFontHandle.Push())
            {
                string glyph = FontAwesomeIcon.Search.ToIconString();
                Vector2 gs = ImGui.CalcTextSize(glyph);
                dl.AddText(new Vector2(origin.X + 10f, origin.Y + (height - gs.Y) * 0.5f),
                    ImGui.ColorConvertFloat4ToU32(Faint), glyph);
            }

            return changed;
        }

        /// <summary>
        /// A search field that reports Enter rather than every keystroke, for searches that cost
        /// something to run.
        /// </summary>
        private bool DrawSearchFieldSubmit(string id, string hint, ref string value, float width,
            float height = ButtonHeight)
            => DrawSearchFieldCore(id, hint, ref value, width, height,
                ImGuiInputTextFlags.EnterReturnsTrue);

        /// <summary>
        /// Uppercase text with letter-spacing, drawn a character at a time.
        ///
        /// ImGui has no tracking and the bundled face has no bold, so both are done by hand: each
        /// glyph is advanced by an extra fraction of the font size, and weight is faked by drawing
        /// the run twice a hair apart. Shipping a second font file for one heading is a worse trade
        /// than a sub-pixel double-strike.
        /// </summary>
        /// <returns>The width the run occupied, so callers can lay out beside it.</returns>
        private static float DrawTrackedCaps(ImDrawListPtr dl, Vector2 pos, string text,
            Vector4 colour, float tracking = 0.1f, bool bold = true)
        {
            string shown = text.ToUpperInvariant();
            uint col = ImGui.ColorConvertFloat4ToU32(colour);
            float extra = ImGui.GetFontSize() * tracking;

            float x = pos.X;
            foreach (char c in shown)
            {
                string glyph = c.ToString();
                dl.AddText(new Vector2(x, pos.Y), col, glyph);
                if (bold)
                    dl.AddText(new Vector2(x + 0.6f, pos.Y), col, glyph);

                x += ImGui.CalcTextSize(glyph).X + extra;
            }

            return x - pos.X;
        }

        // ── Rules ─────────────────────────────────────────────────
        //
        // Sections are separated by a 2px rule and rows by a 1px one, never by whitespace alone:
        // the whole structure of the design is these lines.

        private static void DrawRule(float thickness, Vector4 color, float padAbove = 0f, float padBelow = 0f)
        {
            if (padAbove > 0f) ImGui.Dummy(new Vector2(0, padAbove));

            var dl = ImGui.GetWindowDrawList();
            Vector2 p = ImGui.GetCursorScreenPos();
            float width = ImGui.GetContentRegionAvail().X;
            dl.AddRectFilled(p, new Vector2(p.X + width, p.Y + thickness),
                ImGui.ColorConvertFloat4ToU32(color));

            ImGui.Dummy(new Vector2(width, thickness));
            if (padBelow > 0f) ImGui.Dummy(new Vector2(0, padBelow));
        }

        private static void DrawRuleStrong(float padAbove = 0f, float padBelow = 0f)
            => DrawRule(2f, RuleStrong, padAbove, padBelow);

        private static void DrawRuleHair(float padAbove = 0f, float padBelow = 0f)
            => DrawRule(1f, RuleHair, padAbove, padBelow);

        /// <summary>
        /// A section heading: a 2px rule, then the name in accent uppercase.
        ///
        /// The rule comes first because the label belongs to what is under it, and a line above a
        /// heading is what makes a column of settings read as sections rather than as one list.
        /// </summary>
        private void DrawSectionLabel(string label)
        {
            DrawRuleStrong(padBelow: 8f);
            using (UiLabelFont.Push())
                ImGui.TextColored(Accent, label.ToUpperInvariant());
            ImGui.Dummy(new Vector2(0, 6));
        }

        /// <summary>Gap between whatever a help marker explains and the marker itself.</summary>
        private const float HelpMarkGap = 8f;

        /// <summary>
        /// A small "?" square that carries an explanation on hover.
        ///
        /// The explanations were printed under every control for a while and the page became a wall
        /// of prose with the controls buried in it. Behind the marker, the surface reads as a list
        /// of settings again and the sentence is one hover away for anyone who wants it.
        ///
        /// Square, like every other box in the plugin - it was the last round thing in a design
        /// with no rounding anywhere else, including the radius argument of AddRect.
        ///
        /// Drawn rather than composed from ImGui's own widgets: a button sized down this far still
        /// reserves a frame's worth of padding around itself, which would push the marker off the
        /// line of the label it belongs to.
        /// </summary>
        private static void DrawHelpMark(string id, string explanation)
        {
            float side = HelpMarkSide();

            Vector2 origin = ImGui.GetCursorScreenPos();
            ImGui.InvisibleButton($"##help{id}", new Vector2(side, side));
            bool hot = ImGui.IsItemHovered();

            var max = new Vector2(origin.X + side, origin.Y + side);
            var dl = ImGui.GetWindowDrawList();

            dl.AddRectFilled(origin, max, ImGui.ColorConvertFloat4ToU32(hot ? Raised : Field), 0f);
            dl.AddRect(origin, max, ImGui.ColorConvertFloat4ToU32(hot ? Accent : BorderControl),
                0f, ImDrawFlags.None, 1.2f);

            DrawGlyphCentred("?", (origin + max) * 0.5f, hot ? Ink : Dim);

            if (hot)
                WrappedTooltip(explanation);
        }

        /// <summary>Side of the help square. Rounded to a whole pixel so its 1.2px border lands on
        /// the same pixel on all four sides instead of blurring on two of them.</summary>
        private static float HelpMarkSide() => MathF.Max(13f, MathF.Round(ImGui.GetTextLineHeight()));

        /// <summary>
        /// Draws one glyph centred on a point by its *ink*, not by its em box.
        ///
        /// CalcTextSize measures the em box, which reserves room under the baseline for descenders
        /// and above the caps for accents - none of which "?" uses. Centring that box puts the
        /// glyph visibly high and slightly left inside a small square; the font's own glyph bounds
        /// put it where the eye expects it. Falls back to the em box for a glyph the font has no
        /// entry for, which for "?" means never.
        /// </summary>
        private static unsafe void DrawGlyphCentred(string glyph, Vector2 centre, Vector4 colour)
        {
            var dl = ImGui.GetWindowDrawList();
            uint col = ImGui.ColorConvertFloat4ToU32(colour);

            try
            {
                var font = ImGui.GetFont();
                var ink = font.FindGlyph(glyph[0]);

                if (ink != null)
                {
                    // Glyph bounds are in the font's own pixel size; text is drawn at the size the
                    // current scope asked for, which is not always the same number.
                    float scale = font.FontSize > 0f ? ImGui.GetFontSize() / font.FontSize : 1f;

                    // AddText positions the em box's top-left, so the offset from there to the
                    // ink's centre is what has to be taken back off the centre point.
                    dl.AddText(new Vector2(
                            centre.X - (ink->X0 + ink->X1) * 0.5f * scale,
                            centre.Y - (ink->Y0 + ink->Y1) * 0.5f * scale),
                        col, glyph);
                    return;
                }
            }
            catch (Exception)
            {
                // Any binding-level surprise falls through to the em box below rather than taking
                // the frame with it.
            }

            Vector2 box = ImGui.CalcTextSize(glyph);
            dl.AddText(centre - box * 0.5f, col, glyph);
        }

        /// <summary>
        /// Puts a <see cref="DrawHelpMark"/> after whatever was just drawn, centred against it.
        ///
        /// Centred on the previous item's drawn rectangle in screen space, rather than by nudging
        /// the cursor down by a guess at the difference in heights. The guess was made against the
        /// current font's line height, which is not the font the item was drawn in wherever a
        /// caption or a label sits on the row - so the marker slid below its own line on exactly
        /// the rows that mix two type sizes, the profile card among them.
        /// </summary>
        private static void SameLineHelpDot(string id, string explanation)
        {
            float itemCentreY = (ImGui.GetItemRectMin().Y + ImGui.GetItemRectMax().Y) * 0.5f;

            ImGui.SameLine(0, HelpMarkGap);

            float top = itemCentreY - HelpMarkSide() * 0.5f;
            ImGui.SetCursorPosY(ImGui.GetCursorPosY() + (top - ImGui.GetCursorScreenPos().Y));

            DrawHelpMark(id, explanation);
        }

        /// <summary>
        /// A heading over a list, as opposed to over a column of settings.
        ///
        /// Bigger than a section label and in sentence case, because it names a group of rows the
        /// eye is about to scan rather than labelling a field. The rule above it is the same, so
        /// the two kinds of heading still line up as one system.
        /// </summary>
        /// <param name="helpId">Id and explanation for a "?" beside the heading, when it has one.
        /// Taken here rather than left to the caller: the heading ends with a spacer of its own, so
        /// a caller reaching for SameLineHelpDot afterwards would hang the marker off *that* and
        /// land it under the heading rather than beside it.</param>
        private void DrawListHeading(string label, string? helpId = null, string? help = null)
        {
            DrawRuleStrong(padBelow: 8f);

            var dl = ImGui.GetWindowDrawList();
            Vector2 p = ImGui.GetCursorScreenPos();

            using (UiHeadingFont.Push())
            {
                float lineH = ImGui.GetTextLineHeight();

                // Reserved at the width of the words, not of the row: a full-width spacer leaves
                // nothing to sit beside, which is the other half of why the marker ended up below.
                float used = DrawTrackedCaps(dl, p, label, Dim);
                ImGui.Dummy(new Vector2(used, lineH));
            }

            if (helpId != null && help != null)
                SameLineHelpDot(helpId, help);

            ImGui.Dummy(new Vector2(0, 6));
        }

        /// <summary>
        /// The square toggle that replaces every checkbox: a 34x18 track with a 14x14 knob, accent
        /// when on, and no rounding anywhere.
        ///
        /// Hand-drawn rather than a restyled ImGui.Checkbox because a checkbox cannot be made into a
        /// track-and-knob, and because the knob's travel is the one piece of motion in the design -
        /// it is what tells someone the click landed.
        /// </summary>
        private static bool DrawSquareToggle(string id, ref bool value)
        {
            const float trackW = 34f, trackH = 18f, knob = 14f, inset = 2f;

            float lineHeight = ImGui.GetTextLineHeight();
            Vector2 origin = ImGui.GetCursorScreenPos();
            float height = MathF.Max(trackH, lineHeight);

            ImGui.InvisibleButton($"##toggle{id}", new Vector2(trackW, height));
            bool clicked = ImGui.IsItemClicked();
            bool hot = ImGui.IsItemHovered();
            if (clicked)
                value = !value;

            float top = origin.Y + (height - trackH) * 0.5f;
            var dl = ImGui.GetWindowDrawList();

            Vector4 track = value ? Accent : ColorFromHex("#3a3733");
            if (hot)
                track = value ? AccentHover : Raised;

            dl.AddRectFilled(new Vector2(origin.X, top), new Vector2(origin.X + trackW, top + trackH),
                ImGui.ColorConvertFloat4ToU32(track));

            if (!value)
                dl.AddRect(new Vector2(origin.X, top), new Vector2(origin.X + trackW, top + trackH),
                    ImGui.ColorConvertFloat4ToU32(BorderControl), 0f, 0, 1f);

            float knobX = value ? origin.X + trackW - knob - inset : origin.X + inset;
            float knobY = top + (trackH - knob) * 0.5f;
            dl.AddRectFilled(new Vector2(knobX, knobY), new Vector2(knobX + knob, knobY + knob),
                ImGui.ColorConvertFloat4ToU32(value ? OnAccent : Dim));

            return clicked;
        }

        /// <summary>
        /// A labelled toggle, on one line.
        ///
        /// Keeps the name every caller already passes - including the "##id" suffix ImGui needs for
        /// uniqueness, which is stripped before the label is drawn. Every checkbox in the plugin
        /// goes through here, so replacing the control replaced all of them.
        /// </summary>
        private static bool DrawStyledCheckbox(string label, ref bool value)
        {
            int marker = label.IndexOf("##", StringComparison.Ordinal);
            string shown = marker >= 0 ? label.Substring(0, marker) : label;
            string id = marker >= 0 ? label.Substring(marker + 2) : label;

            bool changed = DrawSquareToggle(id, ref value);

            if (!string.IsNullOrEmpty(shown))
            {
                ImGui.SameLine(0, 10);
                float lineHeight = ImGui.GetTextLineHeight();
                float itemHeight = ImGui.GetItemRectSize().Y;
                if (itemHeight > lineHeight)
                    ImGui.SetCursorPosY(ImGui.GetCursorPosY() + (itemHeight - lineHeight) * 0.5f);

                // Clicking the words has to work as well as clicking the track - a 34px target is
                // small, and the label is the part people aim at.
                ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0, 0, 0, 0));
                ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0, 0, 0, 0));
                ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0, 0, 0, 0));
                ImGui.PushStyleColor(ImGuiCol.Text, Ink);
                ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, Vector2.Zero);
                if (ImGui.Button($"{shown}##lbl{id}"))
                {
                    value = !value;
                    changed = true;
                }
                ImGui.PopStyleVar();
                ImGui.PopStyleColor(4);
            }

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

        /// <summary>A tooltip that wraps, for text longer than a few words.
        ///
        /// <see cref="PaddedTooltip"/> is for short labels and breaks its own lines where the
        /// caller put them; an explanation paragraph handed to it would run off in one line.</summary>
        private static void WrappedTooltip(string text)
        {
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(10, 8));
            ImGui.BeginTooltip();
            // Measured in ems so the line length stays readable at any font size.
            ImGui.PushTextWrapPos(ImGui.GetFontSize() * 22f);
            ImGui.TextUnformatted(text);
            ImGui.PopTextWrapPos();
            ImGui.EndTooltip();
            ImGui.PopStyleVar();
        }

        /// <summary>
        /// Shortens text with an ellipsis so it fits, instead of letting it run past the border -
        /// ImGui clips rather than wrapping, so an overlong name silently loses its tail and
        /// whatever follows it.
        /// </summary>
        private static string Fit(string text, float maxWidth)
        {
            if (maxWidth <= 0 || ImGui.CalcTextSize(text).X <= maxWidth)
                return text;

            const string ellipsis = "…";
            float budget = maxWidth - ImGui.CalcTextSize(ellipsis).X;
            if (budget <= 0)
                return ellipsis;

            for (int len = text.Length - 1; len > 0; len--)
            {
                string cut = text.Substring(0, len);
                if (ImGui.CalcTextSize(cut).X <= budget)
                    return cut + ellipsis;
            }
            return ellipsis;
        }

        private static Vector4 GetRoleColor(RoleType role) => role switch
        {
            RoleType.Tank => RoleTank,
            RoleType.Healer => RoleHealer,
            RoleType.MeleeDPS or RoleType.PhysRangedDPS or RoleType.MagicRangedDPS => RoleDPS,
            RoleType.Omit => RuleStrong,
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
