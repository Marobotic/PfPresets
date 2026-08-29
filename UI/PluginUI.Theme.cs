using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.ManagedFontAtlas;

namespace PfPresets
{
    /// <summary>
    /// The plugin's visual language: a soft, near-black system modelled on a phone, where structure
    /// comes from grouped rounded surfaces rather than from ruled lines.
    ///
    /// Three rules the rest of the UI is built on:
    ///   - Nothing is square. Every corner in the plugin comes off <see cref="Radius"/>, and a
    ///     literal 0f passed as a rounding argument is a bug, not a style.
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
        // ══════════════════════════════════════════════════════════
        //  RADIUS
        // ══════════════════════════════════════════════════════════

        /// <summary>
        /// The corner scale. Every rounded thing in the plugin picks a step from here; nothing
        /// invents its own number.
        ///
        /// The steps are not arbitrary and they are not a gradient: each one belongs to a layer of
        /// the layout, and the layers nest. A chip inside a card inside a sheet has to look smaller
        /// than what contains it or the whole stack reads as one blurry mass, which is the one way a
        /// rounded design goes wrong that a square one cannot.
        /// </summary>
        internal static class Radius
        {
            /// <summary>The phone's screen. Deliberately enormous - it is the single strongest
            /// signal that this window is meant to be held rather than dragged around.</summary>
            public const float Screen = 30f;

            /// <summary>The tablet's screen. A tablet's corners are gentler than a phone's, at both
            /// the real hardware and here.</summary>
            public const float ScreenWide = 18f;

            /// <summary>A modal sheet: the surface that slides up over the body.</summary>
            public const float Sheet = 18f;

            /// <summary>A card - a grouped block of content sitting on the ground.</summary>
            public const float Card = 12f;

            /// <summary>Buttons, inputs, and the containers segmented pills sit in.</summary>
            public const float Control = 10f;

            /// <summary>Icon buttons, menu rows, the app mark, and the pills inside a segmented
            /// control - anything one step in from a Control.</summary>
            public const float Small = 8f;

            /// <summary>A job or role tile.</summary>
            public const float Tile = 7f;

            /// <summary>Chips, badges, and the help marker.</summary>
            public const float Chip = 6f;

            /// <summary>Fully round. ImGui clamps rounding to half the shorter side, so any number
            /// past that is a capsule - the toggle track and its knob, and nothing else.</summary>
            public const float Pill = 999f;
        }

        // ══════════════════════════════════════════════════════════
        //  LAYOUT
        // ══════════════════════════════════════════════════════════

        /// <summary>
        /// The spacing scale. Every gutter, gap and pad in the plugin comes off one of these.
        ///
        /// They existed already, one per file, under seven different names - FeedMargin 14,
        /// RatingsGutter 12, BodyGutter 12, SectionInset 6, ListCardPad 6, CardPad 14, CardPadY 12.
        /// None of them was wrong on its own and no two tabs agreed, so the Clears feed sat two
        /// pixels further in than Recruit and the profile card's rows were half the inset of the
        /// card beside them. That is not a thing anybody can see and name; it is the reason a
        /// layout "feels off" without anybody being able to say why.
        ///
        /// Four steps, and a tab that needs a fifth is a tab doing something the others do not.
        /// </summary>
        internal static class Space
        {
            /// <summary>Inside a chip, between an icon and its label - the smallest real gap.</summary>
            public const float Tight = 8f;

            /// <summary>Between two cards in a list, and between a heading and what it heads.</summary>
            public const float Gap = 10f;

            /// <summary>The margin a tab keeps at its own edges, and between two columns.</summary>
            public const float Gutter = 12f;

            /// <summary>Padding inside a card, between its edge and its contents.</summary>
            public const float Card = 14f;
        }

        /// <summary>Padding inside any card. Named separately because it is read far more often
        /// than it is set, and <c>Space.Card</c> at a call site reads as a gap rather than as the
        /// card's own inset.</summary>
        private const float CardPadding = Space.Card;

        /// <summary>
        /// The height of a control on a tab's toolbar - the search field and the buttons beside it.
        ///
        /// A step taller than a button inside a card, because a toolbar is the top of a surface and
        /// the first thing on it. Every tab with a toolbar uses this one, so the Recruit search and
        /// the profile search are the same control at the same size rather than two searches to
        /// learn.
        /// </summary>
        private const float ToolbarButtonHeight = 40f;

        /// <summary>The toolbar's full height, control plus the air above and below it.</summary>
        private const float ToolbarHeight = ToolbarButtonHeight + Space.Gutter * 2f;

        // ── Colour tokens ─────────────────────────────────────────
        //
        // TRUE BLACK, and three greys above it. The palette used to be a warm near-black - #171614
        // ground, #1b1a17 panel - which is a fine scheme and the wrong one for a design shaped like
        // a phone. A phone's dark mode is neutral and it starts at zero, and the whole point of
        // starting at zero is that everything above it reads as a surface with a height: the ground
        // is nothing, the chrome sits just off it, and the cards sit on top of both.
        //
        // Read them as a stack, because that is what they are:
        //   Ground  #000000  nothing. The window's background, and the gaps between cards.
        //   Panel   #141416  the chrome - sidebar, header, tab bar, footer. Barely off the ground.
        //   Field   #1c1c1e  a surface you can put something on: cards, inputs, closed dropdowns.
        //   Raised  #2c2c2e  the same surface, lifted - hover, and the card for a first clear.
        private static readonly Vector4 Ground = ColorFromHex("#000000");        // window background
        private static readonly Vector4 Panel = ColorFromHex("#141416");         // header, rail, footer
        private static readonly Vector4 Field = ColorFromHex("#1c1c1e");         // cards, inputs
        private static readonly Vector4 Raised = ColorFromHex("#2c2c2e");        // hover fill
        private static readonly Vector4 RuleStrong = ColorFromHex("#38383a");    // 2px section dividers
        private static readonly Vector4 RuleHair = ColorFromHex("#2c2c2e");      // 1px row dividers
        private static readonly Vector4 BorderControl = ColorFromHex("#48484a"); // input / outline borders

        /// <summary>
        /// The hairline around a card.
        ///
        /// White at 12% rather than a grey from the stack above, and the difference matters on a
        /// palette built out of near-blacks: a fixed grey has to be picked against one background
        /// and is then wrong on every other, while a translucent white lifts whatever it is drawn
        /// over by the same amount. The same line reads correctly on a card sitting on the ground,
        /// on the sidebar, and inside a sheet - which is three places the plugin puts cards.
        /// </summary>
        private static readonly Vector4 CardBorder = ColorFromHex("#ffffff1f");

        // The text tones came up with the ground. They were warm greys chosen against a warm
        // near-black; on true black the darkest of them was help text nobody could read.
        private static readonly Vector4 Ink = ColorFromHex("#f5f5f7");           // primary text
        private static readonly Vector4 Dim = ColorFromHex("#a8a8ad");           // secondary text
        private static readonly Vector4 Faint = ColorFromHex("#7c7c82");         // help text, placeholders

        /// <summary>Text drawn on top of an accent fill. Always the ground colour, never white: the
        /// accent is picked by the player and some of the offered ones are light.</summary>
        private static readonly Vector4 OnAccent = ColorFromHex("#000000");

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
        /// The seven accents as round swatches, the chosen one ringed in Ink.
        ///
        /// THE ONLY ACCENT PICKER IN THE PLUGIN, and it lives here rather than in the settings file
        /// so that every build has it. The onboarding asks the same question on its third step, and
        /// the first version of that step drew its own copy of this - which drifted immediately: it
        /// put a one-pixel outline around every unselected swatch, a stroke centred on the fill's
        /// own rounded edge with half of it inside the colour and half outside on black. Following
        /// a rounded corner that reads as a chewed edge, and it was the one control in the run
        /// people said looked pixelated while this one, four steps away, did not.
        ///
        /// Two implementations of one control is how that happens. There is one now, and both
        /// surfaces call it.
        ///
        /// A ring rather than a tick: the swatch is the colour, and a mark drawn in the colour's own
        /// contrast is the one thing guaranteed to be legible on all seven.
        ///
        /// A ROUNDED SQUARE, not a circle and not a square. It is the shape the system this
        /// follows gives anything that is purely its own colour, and the mockup sets it at 28px
        /// with an 8px corner - which is the app-icon proportion, about two-sevenths of the side.
        /// </summary>
        private const float AccentSwatchRadius = 8f;

        private void DrawAccentSwatches()
        {
            // 28px, 8px corner, 10px apart - the mockup's numbers.
            const float swatch = 28f;
            const float gap = 10f;

            var dl = ImGui.GetWindowDrawList();
            string current = string.IsNullOrWhiteSpace(config.AccentColorHex)
                ? DefaultAccentHex
                : config.AccentColorHex.Trim();

            for (int i = 0; i < AccentChoices.Length; i++)
            {
                var (hex, name) = AccentChoices[i];
                if (i > 0)
                    ImGui.SameLine(0, gap);

                Vector2 p = ImGui.GetCursorScreenPos();
                ImGui.InvisibleButton($"##accent{hex}", new Vector2(swatch, swatch));

                bool chosen = string.Equals(hex, current, StringComparison.OrdinalIgnoreCase);
                bool hot = ImGui.IsItemHovered();

                if (ImGui.IsItemClicked())
                {
                    config.AccentColorHex = hex;
                    config.Save();
                }

                var max = new Vector2(p.X + swatch, p.Y + swatch);
                dl.AddRectFilled(p, max, ImGui.ColorConvertFloat4ToU32(ColorFromHex(hex)),
                    AccentSwatchRadius);

                // TWO RINGS, WITH THE GROUND BETWEEN THEM. The mockup selects a chip with a 2px
                // halo in the background colour and a 2px ring in the ink outside that, so the mark
                // never touches the colour it is marking - which matters most on the pale ones,
                // where a ring drawn against the fill is the one thing you cannot see.
                if (chosen)
                {
                    dl.AddRect(new Vector2(p.X - 2f, p.Y - 2f), new Vector2(max.X + 2f, max.Y + 2f),
                        ImGui.ColorConvertFloat4ToU32(Field), AccentSwatchRadius + 2f,
                        ImDrawFlags.None, 2f);
                    dl.AddRect(new Vector2(p.X - 4f, p.Y - 4f), new Vector2(max.X + 4f, max.Y + 4f),
                        ImGui.ColorConvertFloat4ToU32(Ink), AccentSwatchRadius + 4f,
                        ImDrawFlags.None, 2f);
                }
                else if (hot)
                {
                    dl.AddRect(new Vector2(p.X - 3f, p.Y - 3f), new Vector2(max.X + 3f, max.Y + 3f),
                        ImGui.ColorConvertFloat4ToU32(BorderControl), AccentSwatchRadius + 3f,
                        ImDrawFlags.None, 1f);
                }

                if (hot)
                    PaddedTooltip(name);
            }
        }

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
        /// <summary>
        /// Ink that can be read on a given fill - the near-black or the near-white, whichever the
        /// fill is further from.
        ///
        /// The parse brackets run from a very light green through mid orange and pink to a dark
        /// blue, so no single text colour works across them: white on #1eff00 is unreadable and
        /// black on #0070ff is worse. Relative luminance decides it, weighted the way the eye
        /// weights the channels rather than as a flat average - green carries most of the
        /// brightness, and a flat average calls the green bracket dark.
        /// </summary>
        private static Vector4 ReadableOn(Vector4 fill)
        {
            float luma = 0.2126f * fill.X + 0.7152f * fill.Y + 0.0722f * fill.Z;
            return luma > 0.55f ? Ground : Ink;
        }

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

        // Every size below is the mockup's, in the mockup's pixels.
        //
        // A character's own name on their card, and on the recruit tab's solo card - one token, so
        // the two cannot drift apart. The job mark beside it is set from the mockup separately: it
        // is 26px there, slightly LARGER than the 24px name, which is not something a line height
        // would ever produce on its own.
        private const float PersonPx = 24f;
        private const float CaptionPx = 12f;   // the small caps caption over it
        /// <summary>
        /// A PAGE TITLE. One number for every one of them.
        ///
        /// The name of whatever you are looking at - Recruit, My Profile, Clears, Vote, Settings -
        /// wherever the plugin writes it. A section heading inside the page is a step down from
        /// this, and a row inside a section a step down again; change it here and every title moves
        /// together.
        ///
        /// Sixteen, measured off the reference: the title's caps come out ten pixels of ink there,
        /// and this face puts a cap at about 0.71 of the size.
        /// </summary>
        private const float TitlePx = 16f;
        private const float HeadingPx = 16f;   // list section headings
        private const float NamePx = 16f;
        // A person's name in a list row, and the one face in the scale set larger than its
        // neighbour on purpose: it is what the eye runs down a party list looking for, and at the
        // same size as everything else the list read as a block of text rather than as people.
        private const float RowNamePx = 16f;
        private const float BodyPx = 13f;
        private const float LabelPx = 11f;
        private const float HelpPx = 11.5f;

        // Prefixed because the welcome screen and the profile card own faces of their own at other
        // sizes; these are the system scale, not one window's headline.
        private IFontHandle? uiPersonFont, uiCaptionFont, uiTitleFont, uiHeadingFont, uiNameFont, uiRowNameFont, uiBodyFont,
            uiLabelFont, uiHelpFont;

        // WEIGHT IS PART OF THE SCALE, not something a caller chooses at the draw site. A face is
        // either something being read (regular) or something being scanned - a heading, a label, a
        // name, a figure (semibold) - and which of those a given size is has been decided here once
        // rather than argued about in twenty files.
        private IFontHandle UiPersonFont => Font(ref uiPersonFont, PersonPx, FontWeight.SemiBold);
        private IFontHandle UiCaptionFont => Font(ref uiCaptionFont, CaptionPx, FontWeight.SemiBold);
        private IFontHandle UiTitleFont => Font(ref uiTitleFont, TitlePx, FontWeight.SemiBold);
        private IFontHandle UiHeadingFont => Font(ref uiHeadingFont, HeadingPx, FontWeight.SemiBold);
        private IFontHandle UiNameFont => Font(ref uiNameFont, NamePx, FontWeight.SemiBold);
        private IFontHandle UiRowNameFont => Font(ref uiRowNameFont, RowNamePx);

        /// <summary>The clear pills on a profile: a fight's name is content, so it is set at the
        /// same size as a name in any other list rather than at caption size.</summary>
        private IFontHandle UiPillFont => Font(ref uiPillFont, RowNamePx);
        private IFontHandle? uiPillFont;



        /// <summary>A segment's label, a step under body size - the mockup sets these at 12 against
        /// a 13px page, so a segmented control reads as a control rather than as a line of text.
        /// </summary>
        private IFontHandle UiSegmentFont => Font(ref uiSegmentFont, SegmentPx, FontWeight.SemiBold);
        private IFontHandle? uiSegmentFont;
        private const float SegmentPx = 12f;
        private IFontHandle UiBodyFont => Font(ref uiBodyFont, BodyPx);

        // The only two in the scale that never draw a player's words - chips, badges, version
        // strings and section labels are all ours - so they skip the game-glyph fallback.
        private IFontHandle UiLabelFont
            => Font(ref uiLabelFont, LabelPx, FontWeight.SemiBold, userText: false);
        private IFontHandle UiHelpFont => Font(ref uiHelpFont, HelpPx);

        // ── Icons ─────────────────────────────────────────────────

        /// <summary>Icon sizes, set against the text they sit beside rather than inherited from
        /// Dalamud's shared handle. Small goes with body copy and inside inputs; Row is for the
        /// action buttons on a list row, which are 26px squares.</summary>
        private const float IconSmallPx = 12f;
        private const float IconRowPx = 13f;

        private IFontHandle? iconSmallFont, iconRowFont;

        private IFontHandle UiIconSmall => IconFont(ref iconSmallFont, IconSmallPx);
        private IFontHandle UiIconRow => IconFont(ref iconRowFont, IconRowPx);

        private void DisposeScaleFonts()
        {
            iconSmallFont?.Dispose();
            iconRowFont?.Dispose();
            iconSmallFont = iconRowFont = null;

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
        /// The style vars are half the design. ImGui's own defaults round a little, at one value for
        /// everything; this pushes the real scale, so a combo box and a card and a scrollbar are not
        /// all curved by the same three pixels. Button labels are pushed flush left here too, once,
        /// rather than in every button.
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

                // THE RESIZE GRIP IS ERASED, not merely unused.
                //
                // Every window the plugin opens carries NoResize, which is supposed to be the end
                // of it - and there is still a grip in the bottom-right corner of the recruit tab.
                // Wherever it is coming from (a child, a Dalamud wrapper, a build difference), a
                // grip on a window with a fixed size is a control that lies about what it does, and
                // three transparent colours settle it whatever the cause. Nothing else in the
                // plugin uses these.
                (ImGuiCol.ResizeGrip, new Vector4(0, 0, 0, 0)),
                (ImGuiCol.ResizeGripHovered, new Vector4(0, 0, 0, 0)),
                (ImGuiCol.ResizeGripActive, new Vector4(0, 0, 0, 0)),
            };
            foreach (var (col, value) in colors)
                ImGui.PushStyleColor(col, value);

            (ImGuiStyleVar Var, float Value)[] scalars =
            {
                (ImGuiStyleVar.FrameRounding, Radius.Control),
                (ImGuiStyleVar.WindowRounding, Radius.Sheet),
                (ImGuiStyleVar.ChildRounding, Radius.Card),
                (ImGuiStyleVar.PopupRounding, Radius.Control),
                (ImGuiStyleVar.GrabRounding, Radius.Pill),
                (ImGuiStyleVar.TabRounding, Radius.Small),
                (ImGuiStyleVar.ScrollbarRounding, Radius.Pill),
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
        /// <summary>
        /// The look every dropdown and text field in the plugin wears.
        ///
        /// A FIELD IS TALLER THAN ITS TEXT. ImGui's default frame padding is a few pixels, which
        /// puts a combo's words hard against its own border and leaves a control the height of one
        /// line of type - so the fields came out shorter than the buttons beside them and the row
        /// they shared looked ragged. Twelve across and eight down gives a 36px field, which is the
        /// height everything else on these sheets is.
        ///
        /// The popup a combo opens is styled with it: same corner, same border, and padding of its
        /// own so the first item is not touching the edge it drops out of.
        /// </summary>
        private static void PushFramedInput()
        {
            ImGui.PushStyleColor(ImGuiCol.FrameBg, Field);
            ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, Raised);
            ImGui.PushStyleColor(ImGuiCol.FrameBgActive, Raised);
            ImGui.PushStyleColor(ImGuiCol.Border, BorderControl);
            ImGui.PushStyleColor(ImGuiCol.PopupBg, Panel);
            ImGui.PushStyleColor(ImGuiCol.Header, Raised);
            ImGui.PushStyleColor(ImGuiCol.HeaderHovered, Raised);
            ImGui.PushStyleColor(ImGuiCol.HeaderActive, BorderControl);

            ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 1.0f);
            ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, Radius.Control);
            ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(12f, 8f));
            ImGui.PushStyleVar(ImGuiStyleVar.PopupRounding, Radius.Control);
            ImGui.PushStyleVar(ImGuiStyleVar.PopupBorderSize, 1f);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(6f, 6f));
            ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(6f, 4f));
        }

        private static void PopFramedInput()
        {
            ImGui.PopStyleVar(7);
            ImGui.PopStyleColor(8);
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
            ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, Radius.Control);
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
            ImGui.PushStyleColor(ImGuiCol.Button, ColorFromHex("#2c2c2e"));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, ColorFromHex("#3a3a3c"));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, ColorFromHex("#48484a"));
            ImGui.PushStyleColor(ImGuiCol.Text, Ink);
            ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, Radius.Control);
            ImGui.PushStyleVar(ImGuiStyleVar.FramePadding,
                new Vector2(ButtonPadX, ImGui.GetStyle().FramePadding.Y));
            bool clicked = ImGui.Button(label, size);
            ImGui.PopStyleVar(2);
            ImGui.PopStyleColor(4);
            return clicked;
        }

        /// <summary>
        /// A square icon button: a rounded fill with the glyph set at icon size inside it.
        ///
        /// The plugin had three of these written out by hand - the sheets' close, the checklist's,
        /// and the rating prompt's - and only the first two matched, because the third reached for
        /// a plain ImGui.Button and got the theme's 10px frame rounding on a 22x20 rect (a lozenge)
        /// with a glyph from Dalamud's shared icon font (too big for it). One implementation, so a
        /// cross is a cross wherever it is offered.
        /// </summary>
        private bool DrawIconSquareButton(FontAwesomeIcon icon, string id, float size)
        {
            Vector2 p = ImGui.GetCursorScreenPos();

            ImGui.InvisibleButton($"##{id}", new Vector2(size, size));
            bool hot = ImGui.IsItemHovered();
            bool clicked = ImGui.IsItemClicked();

            var dl = ImGui.GetWindowDrawList();
            dl.AddRectFilled(p, new Vector2(p.X + size, p.Y + size),
                ImGui.ColorConvertFloat4ToU32(hot ? Raised : Field), Radius.Small);

            string glyph = icon.ToIconString();
            using (UiIconRow.Push())
            {
                Vector2 gs = ImGui.CalcTextSize(glyph);
                dl.AddText(new Vector2(p.X + (size - gs.X) * 0.5f, p.Y + (size - gs.Y) * 0.5f),
                    ImGui.ColorConvertFloat4ToU32(hot ? Ink : Dim), glyph);
            }

            return clicked;
        }

        // ── Navigation rows ───────────────────────────────────────

        /// <summary>The height of a row in a list of destinations.</summary>
        private const float NavRowHeight = 38f;

        /// <summary>The gap under one. Measured off the settings list, which is the one the
        /// sidebar is read against.</summary>
        private const float NavRowGap = 10f;

        /// <summary>
        /// One row in a list of destinations: an icon, a name, and a fill when it is the one you
        /// are on. The sidebar's tabs and the settings list's pages are both this.
        ///
        /// ONE FUNCTION, BECAUSE TWO COPIES DID NOT STAY IDENTICAL AND WERE NEVER GOING TO.
        ///
        /// They were written twice with the same numbers in both - 38 tall, a 2px Dummy after - and
        /// still came out visibly different, because a Dummy's real height is its own plus ImGui's
        /// item spacing, and the sidebar zeroes item spacing for its hand-written layout while the
        /// settings list does not. Same constant, 2px in one column and 10px in the other. Three
        /// rounds of "matching" them by adjusting the numbers could not fix that, because the
        /// numbers were already the same.
        ///
        /// So the gap is advanced ABSOLUTELY rather than by a spacer, which takes item spacing out
        /// of the answer entirely, and there is one copy of it rather than two.
        /// </summary>
        /// <param name="rowMin">The row's top-left in screen space, so a caller can hang a badge or
        /// a chip off it without recomputing the geometry.</param>
        private bool DrawNavRow(string id, FontAwesomeIcon icon, string label, bool active,
            float width, out Vector2 rowMin)
        {
            Vector2 p = ImGui.GetCursorScreenPos();
            rowMin = p;

            var dl = ImGui.GetWindowDrawList();

            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0, 0, 0, 0));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0, 0, 0, 0));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0, 0, 0, 0));
            bool clicked = ImGui.Button($"##nav{id}", new Vector2(width, NavRowHeight));
            ImGui.PopStyleColor(3);

            bool hovered = ImGui.IsItemHovered();

            if (active || hovered)
                dl.AddRectFilled(p, new Vector2(p.X + width, p.Y + NavRowHeight),
                    ImGui.ColorConvertFloat4ToU32(active ? Raised : Field), Radius.Small);

            // Inactive rows sit at Dim, not Faint. Faint is the help-text tone and on a panel it
            // reads as disabled - navigation that looks switched off is navigation nobody finds.
            Vector4 colour = active || hovered ? Ink : Dim;

            using (UiIconRow.Push())
            {
                string glyph = icon.ToIconString();
                Vector2 gs = ImGui.CalcTextSize(glyph);
                dl.AddText(new Vector2(p.X + 12f, p.Y + (NavRowHeight - gs.Y) * 0.5f),
                    ImGui.ColorConvertFloat4ToU32(active ? Accent : colour), glyph);
            }

            using (UiBodyFont.Push())
            {
                Vector2 ts = ImGui.CalcTextSize(label);
                dl.AddText(new Vector2(p.X + 36f, p.Y + (NavRowHeight - ts.Y) * 0.5f),
                    ImGui.ColorConvertFloat4ToU32(colour), Fit(label, width - 44f));
            }

            // Placed, not spaced - see the note above.
            ImGui.SetCursorScreenPos(new Vector2(p.X, p.Y + NavRowHeight + NavRowGap));

            return clicked;
        }

        /// <summary>Which of the plugin's button styles an action wears.</summary>
        private enum ActionStyle
        {
            /// <summary>The one thing this surface is for. Accent fill.</summary>
            Primary,

            /// <summary>Something that ends or removes. Red fill.</summary>
            Danger,

            /// <summary>Everything else. Grey fill.</summary>
            Secondary,
        }

        /// <summary>
        /// A card's action button: an icon and a word, centred together, in one of three fills.
        ///
        /// ONE HELPER BECAUSE THERE WERE FOUR WAYS TO DRAW THIS. End Recruitment was a bare label
        /// flush left; Leave duty was a bare label; Update progress had an icon from Dalamud's
        /// shared font, noticeably heavier than the words beside it, hung off a hand-written
        /// centring call. Three buttons that do the same kind of thing, laid out three different
        /// ways, on two rows a few pixels apart.
        ///
        /// Every one of them is icon-then-label, centred as a pair, with the icon at the size of
        /// the text rather than at whatever Dalamud's handle happens to be. That is what makes a
        /// row of them read as a row rather than as a collection.
        ///
        /// Drawn over the button rather than passed as its label because ImGui cannot mix the icon
        /// font and the text font in one string.
        /// </summary>
        private bool DrawActionButton(FontAwesomeIcon icon, string label, string id, Vector2 size,
            ActionStyle style, bool enabled = true)
        {
            Vector2 at = ImGui.GetCursorScreenPos();

            if (!enabled)
                ImGui.BeginDisabled();

            bool clicked = style switch
            {
                ActionStyle.Primary => DrawPrimaryButton($"##{id}", size),
                ActionStyle.Danger => DrawDangerFilledButton($"##{id}", size),
                _ => DrawSecondaryButton($"##{id}", size),
            };

            if (!enabled)
                ImGui.EndDisabled();

            Vector4 ink = style == ActionStyle.Primary ? OnAccent : Ink;
            DrawIconLabelCentered(icon, label, at, size, ink, enabled ? 1f : 0.5f, UiIconSmall);

            return clicked && enabled;
        }

        /// <summary>
        /// The small icon buttons that sit on a card - edit, share, the kebab on a preset row.
        ///
        /// A TONE OF THEIR OWN, and this is the whole reason the helper exists. They used to be
        /// ordinary secondary buttons at #2c2c2e, which is exactly the colour a card turns when the
        /// mouse is over it - so the moment you reached for one of them the three buttons dissolved
        /// into the row and you were aiming at nothing. Sitting a step above the card's hover state
        /// keeps them visible in both, which is the one thing a control on a hoverable surface has
        /// to do.
        /// </summary>
        private static bool DrawRowActionButton(string label, Vector2 size)
        {
            ImGui.PushStyleColor(ImGuiCol.Button, ColorFromHex("#38383a"));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, ColorFromHex("#48484a"));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, ColorFromHex("#545456"));
            ImGui.PushStyleColor(ImGuiCol.Text, Ink);
            ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, Radius.Small);
            bool clicked = ImGui.Button(label, size);
            ImGui.PopStyleVar();
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
            ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, Radius.Control);
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
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, ColorFromHex("#d8d8dc"));
            ImGui.PushStyleColor(ImGuiCol.Text, Ground);
            ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, Radius.Control);
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
            ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, Radius.Control);
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
            ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, Radius.Control);
            ImGui.PushStyleVar(ImGuiStyleVar.FramePadding,
                new Vector2(ButtonPadX, ImGui.GetStyle().FramePadding.Y));
            bool clicked = ImGui.Button(label, size);
            ImGui.PopStyleVar();
            ImGui.PopStyleVar();
            ImGui.PopStyleColor(4);
            return clicked;
        }

        /// <summary>
        /// A segmented control: two to four mutually exclusive choices in one track, the chosen one
        /// filled with the accent.
        ///
        /// This is what replaced the dropdowns on the settings surfaces. A combo box for three
        /// options hides two of them behind a click and tells you nothing about what else is on
        /// offer until you open it; segmented, all of them are readable at once and choosing one is
        /// a single press. Above four it stops being a fair trade - the segments get too narrow to
        /// hold their own labels - and the caller keeps its dropdown.
        /// </summary>
        /// <returns>True on the frame a different segment was chosen.</returns>
        private bool DrawSegmentedControl(string id, string[] options, ref int value, float width)
        {
            const float trackPad = 3f;
            const float segH = 30f;
            float trackH = segH + trackPad * 2f;

            Vector2 origin = ImGui.GetCursorScreenPos();
            var dl = ImGui.GetWindowDrawList();

            dl.AddRectFilled(origin, new Vector2(origin.X + width, origin.Y + trackH),
                ImGui.ColorConvertFloat4ToU32(Field), Radius.Control);
            dl.AddRect(origin, new Vector2(origin.X + width, origin.Y + trackH),
                ImGui.ColorConvertFloat4ToU32(BorderControl), Radius.Control, ImDrawFlags.None, 1f);

            float segW = (width - trackPad * 2f) / options.Length;
            bool changed = false;

            for (int i = 0; i < options.Length; i++)
            {
                var min = new Vector2(origin.X + trackPad + segW * i, origin.Y + trackPad);
                var max = new Vector2(min.X + segW, min.Y + segH);

                ImGui.SetCursorScreenPos(min);
                ImGui.InvisibleButton($"##seg{id}{i}", new Vector2(segW, segH));
                bool hot = ImGui.IsItemHovered();

                if (ImGui.IsItemClicked() && value != i)
                {
                    value = i;
                    changed = true;
                }

                bool active = value == i;

                if (active)
                    dl.AddRectFilled(min, max, ImGui.ColorConvertFloat4ToU32(Accent), Radius.Small);
                else if (hot)
                    dl.AddRectFilled(min, max, ImGui.ColorConvertFloat4ToU32(Raised), Radius.Small);

                using (UiSegmentFont.Push())
                {
                    // Ellipsised, not clipped. A segment is sized by division rather than by its
                    // label, so the longest word decides nothing and any of them can overrun.
                    string shown = Fit(options[i], segW - 12f);
                    Vector2 ts = ImGui.CalcTextSize(shown);
                    dl.AddText(new Vector2(min.X + (segW - ts.X) * 0.5f, min.Y + (segH - ts.Y) * 0.5f),
                        ImGui.ColorConvertFloat4ToU32(active ? OnAccent : hot ? Ink : Dim), shown);
                }
            }

            ImGui.SetCursorScreenPos(origin);
            ImGui.Dummy(new Vector2(width, trackH));

            return changed;
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
            => DrawSearchFieldCore(id, hint, ref value, width, height, ImGuiInputTextFlags.None,
                out _);

        /// <summary>
        /// The search field with a clear button on its right end, shown only while there is
        /// something to clear.
        /// </summary>
        /// <param name="cleared">True on the frame the cross was pressed. The value is already
        /// empty by then.</param>
        private bool DrawSearchFieldClearable(string id, string hint, ref string value, float width,
            out bool cleared, float height = ButtonHeight)
            => DrawSearchFieldCore(id, hint, ref value, width, height, ImGuiInputTextFlags.None,
                out cleared, allowClear: true);

        private bool DrawSearchFieldCore(string id, string hint, ref string value, float width,
            float height, ImGuiInputTextFlags flags, out bool cleared, bool allowClear = false)
        {
            cleared = false;

            Vector2 origin = ImGui.GetCursorScreenPos();
            var dl = ImGui.GetWindowDrawList();

            float glyphW;
            using (UiIconSmall.Push())
                glyphW = ImGui.CalcTextSize(FontAwesomeIcon.Search.ToIconString()).X;

            float inset = 12f + glyphW + 8f;
            bool showClear = allowClear && value.Length > 0;

            // The cross gets exactly the padding the field already keeps on that side, so the
            // point where ImGui clips the text is the point where the button starts - a wider
            // target would sit over the tail of a long query.
            float trailing = showClear ? inset : 0f;

            // PUSHED AFTER THE FRAME STYLE, NOT BEFORE IT.
            //
            // PushFramedInput sets a frame padding of its own so that plain fields and dropdowns
            // come out the right height. ImGui takes the last value pushed, so doing it in the
            // other order threw this field's inset away and started the text at 12px - directly
            // under the magnifier, which is what put the icon on top of the word.
            PushFramedInput();
            ImGui.PushStyleVar(ImGuiStyleVar.FramePadding,
                new Vector2(inset, (height - ImGui.GetTextLineHeight()) * 0.5f));

            ImGui.SetNextItemWidth(width);
            bool changed = ImGui.InputTextWithHint($"##{id}", hint, ref value,
                128, flags);

            ImGui.PopStyleVar();
            PopFramedInput();

            // The magnifier at icon-small, not at Dalamud's shared size. It was bigger than the
            // placeholder beside it, which made the field look like a button with a badge on it.
            using (UiIconSmall.Push())
            {
                string glyph = FontAwesomeIcon.Search.ToIconString();
                Vector2 gs = ImGui.CalcTextSize(glyph);

                // Centred on the glyph's ink rather than on its line box. A FontAwesome glyph does
                // not fill its em, so halving the line height leaves it sitting low - which is the
                // other half of why this looked off.
                DrawTextCentredOnInk(glyph,
                    new Vector2(origin.X + 12f + gs.X * 0.5f, origin.Y + height * 0.5f), Faint);
            }

            if (!showClear)
                return changed;

            // Submitted AFTER the input, which is what makes it clickable at all: ImGui tests the
            // most recently submitted item in a window first, so a button laid over a text field
            // has to come second or the field swallows the press.
            var clearMin = new Vector2(origin.X + width - trailing, origin.Y);
            ImGui.SetCursorScreenPos(clearMin);
            ImGui.InvisibleButton($"##{id}clear", new Vector2(trailing, height));
            bool hot = ImGui.IsItemHovered();

            if (ImGui.IsItemClicked())
            {
                value = string.Empty;
                cleared = true;
                changed = true;
            }

            using (UiIconSmall.Push())
            {
                string glyph = FontAwesomeIcon.Times.ToIconString();
                Vector2 gs = ImGui.CalcTextSize(glyph);
                dl.AddText(new Vector2(clearMin.X + (trailing - gs.X) * 0.5f,
                        clearMin.Y + (height - gs.Y) * 0.5f),
                    ImGui.ColorConvertFloat4ToU32(hot ? Ink : Faint), glyph);
            }

            if (hot)
                PaddedTooltip("Clear the search");

            return changed;
        }

        /// <summary>
        /// A search field that reports Enter rather than every keystroke, for searches that cost
        /// something to run.
        /// </summary>
        private bool DrawSearchFieldSubmit(string id, string hint, ref string value, float width,
            float height = ButtonHeight)
            => DrawSearchFieldCore(id, hint, ref value, width, height,
                ImGuiInputTextFlags.EnterReturnsTrue, out _);

        /// <summary>
        /// Uppercase text with letter-spacing, drawn a character at a time.
        ///
        /// ImGui has no tracking, so it is done by hand: each glyph is advanced by an extra fraction
        /// of the font size. Weight used to be done by hand here too - the run was drawn twice,
        /// 0.6px apart, because the bundled face had no bold. It does now (see FontWeight), and a
        /// double-strike over a real semibold is a smear, so it is gone. Push a semibold handle
        /// around the call if the heading wants weight.
        /// </summary>
        /// <returns>The width the run occupied, so callers can lay out beside it.</returns>
        private static float DrawTrackedCaps(ImDrawListPtr dl, Vector2 pos, string text,
            Vector4 colour, float tracking = 0.1f)
        {
            string shown = text.ToUpperInvariant();
            uint col = ImGui.ColorConvertFloat4ToU32(colour);
            float extra = ImGui.GetFontSize() * tracking;

            float x = pos.X;
            foreach (char c in shown)
            {
                string glyph = c.ToString();
                dl.AddText(new Vector2(x, pos.Y), col, glyph);
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
            // NO RULE, like every other heading in the plugin. This one is a FIELD label - it names
            // the input under it inside a form, which is why it keeps the accent and the small caps
            // while a tab's heading is the quieter tracked caps of DrawListHeading. Two roles, two
            // looks, and neither of them draws a line any more.
            using (UiLabelFont.Push())
                ImGui.TextColored(Accent, label.ToUpperInvariant());
            ImGui.Dummy(new Vector2(0, 6));
        }

        // ── Settings sections ─────────────────────────────────────

        private ImDrawListPtr settingsCardDl;
        private Vector2 settingsCardMin;
        private float settingsCardWidth;

        /// <summary>
        /// Opens a settings section: the heading, then a card for the rows under it.
        ///
        /// The Settings tab was the one surface still built the old way - accent labels over a 2px
        /// rule, rows on the bare ground - so it read as a different plugin from the three tabs
        /// beside it. Grouped rounded cards under grey caps headings is what every other tab does
        /// here, and what the system this design is modelled on does with settings specifically.
        ///
        /// Imperative rather than a lambda because the sections are long, branch several times, and
        /// indent and unindent inside their own conditionals; wrapping each one in a closure would
        /// have meant re-indenting several hundred lines to change where a background is painted.
        /// Always pair with <see cref="EndSettingsSection"/>.
        /// </summary>
        private void BeginSettingsSection(string label)
        {
            // THE ACCENT, at label size in tracked caps - the mockup sets every settings heading
            // that way, and it is what divides one group of rows from the next.
            DrawSectionLabel(label);

            settingsCardDl = ImGui.GetWindowDrawList();
            settingsCardDl.ChannelsSplit(2);
            settingsCardDl.ChannelsSetCurrent(1);

            settingsCardMin = ImGui.GetCursorScreenPos();

            // A real margin off the right, not a hairline of one. The card ran to the very edge of
            // the page, which put its rounded corner and its border against the panel's own and,
            // on a page long enough to scroll, underneath the scrollbar.
            settingsCardWidth = ImGui.GetContentRegionAvail().X - SettingsPageMargin;

            // MEASURED FROM THE CURSOR, NOT FROM A GROUP.
            //
            // This used to wrap the section in BeginGroup/EndGroup and take the height from
            // GetItemRectSize. That works while everything inside flows; the rows here place
            // themselves in screen space, and a group whose contents set the cursor rather than
            // advancing it cannot be relied on to report the height they used - which is how a
            // card ends up drawn a few pixels tall, or not visibly at all. The rows all advance the
            // cursor by an exact amount, so the distance it travels IS the height.
            settingsCardTop = settingsCardMin.Y;

            ImGui.Dummy(new Vector2(settingsCardWidth, SettingsCardPadY));
            ImGui.Indent(CardPadding);
        }

        /// <summary>Room above the first row and below the last, inside the card.</summary>
        private const float SettingsCardPadY = 8f;

        /// <summary>The page's own margin: how far a card stops short of the panel edge.</summary>
        private const float SettingsPageMargin = 18f;

        private float settingsCardTop;

        private void EndSettingsSection()
        {
            ImGui.Unindent(CardPadding);
            ImGui.Dummy(new Vector2(settingsCardWidth, SettingsCardPadY));

            float cardHeight = ImGui.GetCursorScreenPos().Y - settingsCardTop;

            settingsCardDl.ChannelsSetCurrent(0);
            var cardMax = new Vector2(settingsCardMin.X + settingsCardWidth,
                                      settingsCardMin.Y + cardHeight);
            settingsCardDl.AddRectFilled(settingsCardMin, cardMax,
                ImGui.ColorConvertFloat4ToU32(Field), Radius.Card);
            settingsCardDl.AddRect(settingsCardMin, cardMax,
                ImGui.ColorConvertFloat4ToU32(CardBorder), Radius.Card, ImDrawFlags.None, 1f);
            settingsCardDl.ChannelsMerge();

            ImGui.Dummy(new Vector2(0, Space.Gutter + 6f));
        }

        /// <summary>
        /// The help mark at the end of a settings row: an 18px square with a hairline, a 5px
        /// corner and a question mark in it, from the mockup.
        ///
        /// Placed from its left edge, immediately after the words it belongs to. Out at the end of
        /// the row it read as a third column of its own, sitting a long way from the sentence it
        /// was marking.
        /// </summary>
        private void DrawRowHelpMark(string id, string explanation, Vector2 centreLeft)
        {
            const float side = 18f;
            var min = new Vector2(centreLeft.X, centreLeft.Y - side * 0.5f);
            var max = new Vector2(min.X + side, min.Y + side);

            ImGui.SetCursorScreenPos(min);
            ImGui.InvisibleButton($"##help{id}", new Vector2(side, side));
            bool hot = ImGui.IsItemHovered();

            var dl = ImGui.GetWindowDrawList();
            dl.AddRect(min, max, ImGui.ColorConvertFloat4ToU32(hot ? Dim : BorderControl),
                5f, ImDrawFlags.None, 1f);

            using (UiLabelFont.Push())
            {
                Vector2 ts = ImGui.CalcTextSize("?");
                dl.AddText(new Vector2(min.X + (side - ts.X) * 0.5f, min.Y + (side - ts.Y) * 0.5f),
                    ImGui.ColorConvertFloat4ToU32(hot ? Ink : Dim), "?");
            }

            if (hot)
                WrappedTooltip(explanation);
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
        /// Rounded at the chip step, like every other small box in the plugin.
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

            dl.AddRectFilled(origin, max, ImGui.ColorConvertFloat4ToU32(hot ? Raised : Field),
                Radius.Chip);
            dl.AddRect(origin, max, ImGui.ColorConvertFloat4ToU32(hot ? Accent : BorderControl),
                Radius.Chip, ImDrawFlags.None, 1.2f);

            DrawGlyphCentred("?", (origin + max) * 0.5f, hot ? Ink : Dim);

            if (hot)
                WrappedTooltip(explanation);
        }

        /// <summary>Side of the help square. Rounded to a whole pixel so its 1.2px border lands on
        /// the same pixel on all four sides instead of blurring on two of them.</summary>
        private static float HelpMarkSide() => MathF.Max(13f, MathF.Round(ImGui.GetTextLineHeight()));

        /// <summary>
        /// Draws a short run of text centred on a point by its INK, not by its em box.
        ///
        /// The string version of <see cref="DrawGlyphCentred"/>, and it exists for the same reason:
        /// CalcTextSize measures the em box, which reserves room under the baseline for descenders
        /// no matter whether the word has any. Centring that box drops a word like "Fetch" - all
        /// caps-height and no tail - visibly below the middle of a small chip.
        ///
        /// The vertical extent is taken from the glyphs actually present, so "Fetch" is measured
        /// from the top of the F to the baseline and "Query" from the top of the Q to the bottom of
        /// the y. Horizontally the em box is fine and is what is used: side bearings are what keeps
        /// letters from touching their neighbours, and stripping them would only shift the run.
        /// </summary>
        private static unsafe void DrawTextCentredOnInk(string text, Vector2 centre, Vector4 colour)
        {
            if (string.IsNullOrEmpty(text))
                return;

            var dl = ImGui.GetWindowDrawList();
            uint col = ImGui.ColorConvertFloat4ToU32(colour);
            Vector2 box = ImGui.CalcTextSize(text);

            float top = float.MaxValue, bottom = float.MinValue;

            try
            {
                var font = ImGui.GetFont();
                float scale = font.FontSize > 0f ? ImGui.GetFontSize() / font.FontSize : 1f;

                foreach (char c in text)
                {
                    if (c == ' ')
                        continue;

                    var ink = font.FindGlyph(c);
                    if (ink == null)
                        continue;

                    top = MathF.Min(top, ink->Y0 * scale);
                    bottom = MathF.Max(bottom, ink->Y1 * scale);
                }
            }
            catch (Exception)
            {
                // Any binding-level surprise falls through to the em box below rather than taking
                // the frame with it.
            }

            // Nothing measurable - a string of spaces, or a font that would not answer. The em box
            // is the honest fallback and is what every other caller in the plugin uses.
            if (top > bottom)
            {
                dl.AddText(centre - box * 0.5f, col, text);
                return;
            }

            dl.AddText(new Vector2(centre.X - box.X * 0.5f, centre.Y - (top + bottom) * 0.5f),
                col, text);
        }

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

        // ── iOS list rows ─────────────────────────────────────────

        /// <summary>
        /// The height of one row in a grouped list.
        ///
        /// Forty-four points, which is the tap target the system this design follows has used since
        /// there were touch screens to design for. It is not a look - it is the smallest thing a
        /// finger hits reliably - and every list in the plugin is built from it so that a settings
        /// row, a person in the met list and a clear in the feed are all the same object at the
        /// same size.
        /// </summary>
        private const float ListRowHeight = 44f;

        /// <summary>
        /// One row of a grouped list: a full-width hit target with a hover wash, laid out by the
        /// caller from the rect it hands back.
        ///
        /// The cursor is left exactly where it started, so everything inside the row is placed in
        /// screen space against <paramref name="min"/>. A row that flowed its contents would put
        /// the control wherever the label happened to end, and a list whose switches do not line up
        /// is the single clearest sign that a layout was not designed as a list.
        /// </summary>
        private bool BeginListRow(string id, float width, out Vector2 min, float? height = null)
        {
            float rowH = height ?? ListRowHeight;
            min = ImGui.GetCursorScreenPos();

            ImGui.InvisibleButton($"##row{id}", new Vector2(width, rowH));
            bool clicked = ImGui.IsItemClicked();

            if (ImGui.IsItemHovered())
                ImGui.GetWindowDrawList().AddRectFilled(min,
                    new Vector2(min.X + width, min.Y + rowH),
                    ImGui.ColorConvertFloat4ToU32(Raised), Radius.Small);

            ImGui.SetCursorScreenPos(min);
            return clicked;
        }

        /// <summary>Moves the cursor past a row drawn with <see cref="BeginListRow"/>.</summary>
        private static void EndListRow(Vector2 min)
            => ImGui.SetCursorScreenPos(new Vector2(min.X, min.Y + ListRowHeight));

        /// <summary>
        /// The hairline between two rows.
        ///
        /// IT STARTS AT THE TEXT, NOT AT THE EDGE. A separator that spans the whole card cuts the
        /// group into slices; one that begins where the label begins reads as the rows being a list
        /// rather than the card being divided. It runs out to the card's trailing edge, which is
        /// the other half of the same idea - the line leads the eye along the row and off it.
        /// </summary>
        private static void DrawRowSeparator(ImDrawListPtr dl, Vector2 rowMin, float rowHeight,
            float textInset, float rightEdge)
        {
            float y = rowMin.Y + rowHeight;
            dl.AddRectFilled(new Vector2(rowMin.X + textInset, y), new Vector2(rightEdge, y + 1f),
                ImGui.ColorConvertFloat4ToU32(RuleHair));
        }

        /// <summary>Room a leading selection mark takes, so a row's label knows where to start.
        /// </summary>
        private const float RowCheckColumn = 26f;

        /// <summary>
        /// The mark on the chosen row of a single-choice list: a tick in the accent, LEADING the
        /// label.
        ///
        /// It sat at the trailing edge, which is where the system this follows puts it - and at the
        /// width these cards run to, the mark ended up a hand's breadth from the words it belongs
        /// to and read as a column of its own. Ahead of the label it is next to what it marks.
        ///
        /// A tick, not a filled circle: a radio asks you to read a control, a tick states a fact
        /// about the row it is on, and it needs no second unchecked shape on every other option.
        /// </summary>
        private void DrawRowCheck(Vector2 rowMin, bool chosen)
        {
            if (!chosen)
                return;

            using (pluginInterface.UiBuilder.IconFontHandle.Push())
            {
                string glyph = FontAwesomeIcon.Check.ToIconString();
                Vector2 gs = ImGui.CalcTextSize(glyph);
                ImGui.GetWindowDrawList().AddText(
                    new Vector2(rowMin.X + (RowCheckColumn - gs.X) * 0.5f,
                                rowMin.Y + (ListRowHeight - gs.Y) * 0.5f),
                    ImGui.ColorConvertFloat4ToU32(Accent), glyph);
            }
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
            // NO RULE ABOVE IT. A heading over a list now sits on the ground with a card under it,
            // and a full-width line above that reads as the top edge of something the section has
            // been pushed inside - which is exactly what it looked like on the profile tab. The
            // gap above the heading is the separation.
            //
            // AND THE GAP ABOVE IS THE BIGGER OF THE TWO. A heading belongs to what is under it,
            // so it has to sit nearer to that than to whatever it is being separated from - the
            // profile tab had it the other way round, with the two headings tucked up against the
            // search field and floating well clear of the cards they name.
            ImGui.Dummy(new Vector2(0, Space.Gutter));

            var dl = ImGui.GetWindowDrawList();
            Vector2 p = ImGui.GetCursorScreenPos();

            // Body size, a step below the tab's own name in the header above it - see
            // DrawHeaderStrip. A section heading is a label for the block under it, not a title
            // competing with the page's.
            using (UiBodyFont.Push())
            {
                float lineH = ImGui.GetTextLineHeight();

                // Reserved at the width of the words, not of the row: a full-width spacer leaves
                // nothing to sit beside, which is the other half of why the marker ended up below.
                float used = DrawTrackedCaps(dl, p, label, Dim);
                ImGui.Dummy(new Vector2(used, lineH));
            }

            if (helpId != null && help != null)
                SameLineHelpDot(helpId, help);

            // Half the gap above it, so the heading reads as attached to the card below rather
            // than floating between the two.
            ImGui.Dummy(new Vector2(0, Space.Tight - 2f));
        }

        /// <summary>
        /// Draws a block of rows onto a card, the way the profile card beside it is drawn.
        ///
        /// THE TWO COLUMNS HAVE TO MATCH. One of them was a card with a heading inside it and the
        /// other was rows on the bare ground with a heading above them, which on a black background
        /// is a card next to nothing - the list did not look like part of the same tab, and its
        /// rows ran off the right edge with no edge to stop them.
        ///
        /// Height is measured rather than predicted: the card is drawn on a background channel
        /// after the body has been laid out and its extent is known, so nothing has to be told in
        /// advance how many rows it is about to get.
        /// </summary>
        private void DrawListCard(Action body)
        {
            var dl = ImGui.GetWindowDrawList();
            dl.ChannelsSplit(2);
            dl.ChannelsSetCurrent(1);

            Vector2 cardMin = ImGui.GetCursorScreenPos();
            float cardWidth = ImGui.GetContentRegionAvail().X;

            ImGui.BeginGroup();
            try
            {
                ImGui.Dummy(new Vector2(cardWidth, ListCardPad));
                body();
                ImGui.Dummy(new Vector2(cardWidth, ListCardPad));
            }
            finally
            {
                ImGui.EndGroup();
            }

            float cardHeight = ImGui.GetItemRectSize().Y;

            dl.ChannelsSetCurrent(0);
            var cardMax = new Vector2(cardMin.X + cardWidth, cardMin.Y + cardHeight);
            dl.AddRectFilled(cardMin, cardMax, ImGui.ColorConvertFloat4ToU32(Field), Radius.Card);
            dl.AddRect(cardMin, cardMax, ImGui.ColorConvertFloat4ToU32(CardBorder),
                Radius.Card, ImDrawFlags.None, 1f);

            dl.ChannelsMerge();
        }

        /// <summary>Padding above and below a list card's rows. The card's own inset, like every
        /// other card - it was 6, which made the rows sit half as far in as the profile card's
        /// contents did in the column beside them.</summary>
        private const float ListCardPad = CardPadding;

        /// <summary>
        /// The switch that replaces every checkbox: a 36x20 capsule track with a round 16px knob,
        /// accent when on.
        ///
        /// Hand-drawn rather than a restyled ImGui.Checkbox because a checkbox cannot be made into a
        /// track-and-knob, and because the knob's travel is the one piece of motion in the design -
        /// it is what tells someone the click landed.
        ///
        /// The name says Square and the control has not been square since the phone redesign. It is
        /// called from a dozen files by that name and renaming it would touch all of them to change
        /// nothing; the shape lives in the numbers below, not in the identifier.
        /// </summary>
        private static bool DrawSquareToggle(string id, ref bool value)
        {
            const float trackW = 36f, trackH = 20f, knob = 16f, inset = 2f;

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

            Vector4 track = value ? Accent : ColorFromHex("#39393d");
            if (hot)
                track = value ? AccentHover : Raised;

            dl.AddRectFilled(new Vector2(origin.X, top), new Vector2(origin.X + trackW, top + trackH),
                ImGui.ColorConvertFloat4ToU32(track), Radius.Pill);

            if (!value)
                dl.AddRect(new Vector2(origin.X, top), new Vector2(origin.X + trackW, top + trackH),
                    ImGui.ColorConvertFloat4ToU32(BorderControl), Radius.Pill, ImDrawFlags.None, 1f);

            // A true circle, not a rounded square. AddCircleFilled rather than a rect with a large
            // radius: ImGui's rounded rect approximates each corner with a fixed number of segments,
            // and at 16px across that reads as an octagon.
            float knobX = value ? origin.X + trackW - knob - inset : origin.X + inset;
            float knobY = top + (trackH - knob) * 0.5f;
            dl.AddCircleFilled(new Vector2(knobX + knob * 0.5f, knobY + knob * 0.5f), knob * 0.5f,
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
