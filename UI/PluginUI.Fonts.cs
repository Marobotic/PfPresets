using System;
using System.Collections.Generic;
using Dalamud.Interface.GameFonts;
using Dalamud.Interface.ManagedFontAtlas;

namespace PfPresets
{
    /// <summary>
    /// Font handles, and the one rule about them: never scale a font, build it at the size wanted.
    ///
    /// ImGui's AddText overload that takes a size stretches the rasterised glyphs it already has,
    /// which at anything above the atlas size is visibly soft - it was what made the welcome
    /// screen's headline look blurred. A handle built at the target pixel size is rasterised at
    /// that size and stays crisp.
    ///
    /// Lives outside the ratings half deliberately: the profile card was the first thing to need a
    /// custom face, but it is not the only one, and shared plumbing in a conditionally-compiled
    /// file is only shared by half the builds.
    /// </summary>
    public partial class PluginUI
    {
        /// <summary>
        /// Smoothstep. Linear motion looks mechanical; this eases both ends.
        ///
        /// Shared rather than living with the rating rows that first needed it - the welcome screen
        /// animates too, and in a build without ratings that file isn't compiled.
        /// </summary>
        private static float Ease(float t) => t * t * (3f - 2f * t);

        /// <summary>The two weights the design uses. Nothing in the plugin is drawn in a weight
        /// that is not one of these.</summary>
        internal enum FontWeight
        {
            /// <summary>Body copy, help text, a person's name in a list row.</summary>
            Regular,

            /// <summary>Headings, labels, tab names, the plugin's own name, and any number set
            /// large enough to be read as a figure rather than as text.</summary>
            SemiBold,
        }

        private static byte[]? regularBytes;
        private static byte[]? semiBoldBytes;

        /// <summary>
        /// The typeface, as bytes.
        ///
        /// SEMIBOLD IS A REAL WEIGHT NOW. It used to be faked, by drawing the same run twice a
        /// sub-pixel apart - a trick that works at one size and smears at every other, and that had
        /// to be remembered at every call site that wanted a heading to look like one. Two files at
        /// 121KB each is a much better trade than a rendering hack in the middle of the layout code.
        /// </summary>
        private static byte[] Typeface(FontWeight weight)
        {
            ref byte[]? slot = ref weight == FontWeight.SemiBold
                ? ref semiBoldBytes
                : ref regularBytes;

            if (slot != null)
                return slot;

            string name = weight == FontWeight.SemiBold
                ? "PfPresets.Data.Fonts.Roboto-SemiBold.ttf"
                : "PfPresets.Data.Fonts.Roboto-Regular.ttf";

            using var stream = typeof(PluginUI).Assembly.GetManifestResourceStream(name)
                ?? throw new InvalidOperationException($"{name} missing from the assembly.");

            using var buffer = new System.IO.MemoryStream();
            stream.CopyTo(buffer);
            return slot = buffer.ToArray();
        }

        /// <summary>
        /// Everything the plugin has to draw that Roboto has no glyph for, borrowed from the
        /// game's own face.
        ///
        /// THIS IS WHY PRESET COMMENTS WERE FULL OF QUESTION MARKS. A comment like
        /// "†Θmεηs† 【】Mαlßoro" is ordinary in a Party Finder listing - people build names out of
        /// Greek letters, daggers, fullwidth capitals and CJK brackets because the game lets them.
        /// Roboto is a Latin face and has none of that, so every one of those characters came out
        /// as a box. It looked like the text was being mangled, and the giveaway that it was not
        /// was the editor: the editor draws in ImGui's default font, which is the game's, and there
        /// the comment read perfectly.
        ///
        /// Latin is deliberately absent. Roboto covers it, and merging over the top would replace
        /// glyphs of the plugin's own typeface with the game's for no reason.
        ///
        /// An ImGui glyph range: begin/end pairs, terminated by a zero.
        /// </summary>
        private static readonly ushort[] GameFallbackGlyphs =
        {
            0x0370, 0x03FF,  // Greek and Coptic - the single most common source of this
            0x0400, 0x04FF,  // Cyrillic
            0x2000, 0x206F,  // General punctuation, the dagger among it
            0x20A0, 0x20BF,  // Currency
            0x2100, 0x214F,  // Letterlike symbols
            0x2190, 0x21FF,  // Arrows
            0x2200, 0x22FF,  // Mathematical operators
            0x2460, 0x24FF,  // Enclosed alphanumerics
            0x25A0, 0x25FF,  // Geometric shapes
            0x2600, 0x26FF,  // Miscellaneous symbols, hearts included
            0x2700, 0x27BF,  // Dingbats
            0x3000, 0x303F,  // CJK symbols and punctuation - the bracket pair
            0xFF00, 0xFFEF,  // Halfwidth and fullwidth forms

            // The auto-translate brackets, which are game glyphs rather than characters: they live
            // in the private-use area and no ordinary font has them, Roboto included. A comment
            // carrying an auto-translate phrase draws its brackets instead of two empty boxes.
            CommentText.AutoTranslateOpen, CommentText.AutoTranslateClose,

            0,
        };

        /// <summary>
        /// The brackets on their own, for the faces that never draw anything a player typed.
        ///
        /// The full range above is a few hundred real glyphs, and it is baked once per size. The
        /// headings, chips and the 54px score are strings this plugin wrote, so they pay for two
        /// glyphs instead of six hundred.
        /// </summary>
        private static readonly ushort[] AutoTranslateGlyphs =
        {
            CommentText.AutoTranslateOpen, CommentText.AutoTranslateClose, 0,
        };

        /// <summary>Builds the handle on first use and caches it in the caller's field.</summary>
        /// <param name="userText">Whether this face ever draws something a player typed - a name,
        /// a comment, a world. If it does it needs the full fallback range; if it only ever draws
        /// the plugin's own words, it does not.</param>
        /// <param name="gameGlyphs">Whether to merge the game's own face in behind Roboto for the
        /// characters Roboto does not carry.
        ///
        /// OFF FOR ANYTHING THAT IS ONLY EVER DIGITS. The game's Axis face exists at a fixed set of
        /// sizes, and asking for one it does not have is how a handle fails to build - and a handle
        /// that fails to build does not fall back to Roboto at the size asked for, it falls back
        /// to Dalamud's default, about 12px. That is why a score set at 54 came out smaller than
        /// the two words beside it. A number needs no fallback face: there is no digit Roboto is
        /// missing.</param>
        /// <summary>The largest Axis face Dalamud carries. See the note inside <see cref="Font"/>.
        /// </summary>
        private const float MaxGameGlyphPx = 36f;

        private IFontHandle Font(ref IFontHandle? slot, float px,
            FontWeight weight = FontWeight.Regular, bool userText = true, bool gameGlyphs = true)
        {
            slot ??= pluginInterface.UiBuilder.FontAtlas.NewDelegateFontHandle(tk =>
                tk.OnPreBuild(pre =>
                {
                    var face = pre.AddFontFromMemory(
                        Typeface(weight), new SafeFontConfig { SizePx = px }, "Roboto");

                    // THE GAME FACE IS ASKED FOR AT A SIZE IT ACTUALLY HAS.
                    //
                    // Dalamud ships Axis at five sizes and no others: 9.6, 12, 14, 18 and 36. Ask
                    // for one that is not on that list - 54 for the score, 64 for a name somebody
                    // is trying out - and the request does not degrade, it fails, and a font handle
                    // that fails to build does not fall back to Roboto at the size requested. It
                    // falls back to Dalamud's default, around 12px.
                    //
                    // Which is why raising a size and rebuilding appeared to change nothing: past
                    // 36 every one of these went to the same small default, so the number in the
                    // source and the text on screen stopped being connected at all.
                    //
                    // Roboto still gets the exact size - it is what draws every Latin character
                    // here. Only the fallback face, which exists for the characters Roboto has
                    // none of, is capped. A Japanese name in a 64px heading comes out at 36 rather
                    // than taking the whole heading down with it.
                    if (gameGlyphs)
                        pre.AddGameGlyphs(new GameFontStyle(GameFontFamily.Axis, MathF.Min(px, MaxGameGlyphPx)),
                            userText ? GameFallbackGlyphs : AutoTranslateGlyphs, face);
                }));
            return slot;
        }

        /// <summary>
        /// FontAwesome at a size the plugin picks, rather than at whatever Dalamud's shared icon
        /// font happens to be.
        ///
        /// The shared handle is sized for Dalamud's own UI and is noticeably heavier than 13px body
        /// text - a magnifier inside a search field, or the three dots on a preset row, came out
        /// bigger than the words beside them. An icon that outweighs its label is an icon nobody
        /// reads past.
        /// </summary>
        private IFontHandle IconFont(ref IFontHandle? slot, float px)
        {
            slot ??= pluginInterface.UiBuilder.FontAtlas.NewDelegateFontHandle(tk =>
                tk.OnPreBuild(pre => pre.AddFontAwesomeIconFont(new SafeFontConfig { SizePx = px })));
            return slot;
        }

        /// <summary>
        /// Asks for every face the plugin owns, once, before anything is drawn in one.
        ///
        /// THE FLASH THIS FIXES. Every handle above is built on first use, and a handle that has
        /// not finished building does not draw at the size asked for - Push() on it silently falls
        /// back to Dalamud's own default, around 12px. Built lazily, that meant the FIRST FRAME of
        /// anything was set in the wrong face at the wrong size and corrected itself a moment
        /// later: opening the window, switching to a tab whose faces nothing had touched yet,
        /// scrolling a profile card into view, opening the announcement preview. Every one of those
        /// showed a frame or two of Dalamud's default first.
        ///
        /// Touching each property here builds them all in one atlas pass, at load, while there is
        /// nothing on screen to flicker. It costs one build of about twenty faces instead of twenty
        /// builds spread across the session - which is also cheaper overall, because each separate
        /// NewDelegateFontHandle forces its own atlas rebuild.
        ///
        /// SAFE TO CALL EARLY AND SAFE TO CALL TWICE. Every accessor is `??=` on its own slot, so a
        /// second call is a no-op, and none of them touch ImGui state - they only register a build
        /// callback with the atlas, which Dalamud runs when it is ready.
        ///
        /// A face added anywhere in the plugin belongs on this list. One that is left off still
        /// works; it just brings its flash back with it.
        /// </summary>
        internal void PreloadFonts()
        {
            try
            {
                // The system scale, used by every window.
                _ = UiPersonFont; _ = UiCaptionFont; _ = UiTitleFont; _ = UiHeadingFont;
                _ = UiNameFont; _ = UiRowNameFont; _ = UiPillFont; _ = UiSegmentFont;
                _ = UiBodyFont; _ = UiLabelFont; _ = UiHelpFont;
                _ = UiIconSmall; _ = UiIconRow;

                // The onboarding's own faces. It is the first thing a new install sees, and the
                // one surface with nothing before it to have warmed the atlas.
                _ = OnbDisplay; _ = OnbTitle; _ = OnbLead; _ = OnbCard;
                _ = OnbBody; _ = OnbBodyBold; _ = OnbSmall; _ = OnbSmallBold;
                _ = OnbTiny; _ = OnbScore; _ = OnbFeedName;
                _ = OnbIconMid; _ = OnbIconLarge;

#if PFP_RATINGS
                _ = ScoreFont; _ = LabelFont;

                // The announcement, and the preview of it in the onboarding. Both game faces are
                // asked for up front rather than when the typeface is switched, so switching is
                // instant instead of showing the old face for a frame - see OnbPreviewFaces.
                _ = AnnPluginFont; _ = AnnIconFont;

                // EnsureAnnounceFonts first, because AnnTextFont reads annBuiltFace to decide which
                // family to ask the game for - touched before it, the announcement would build the
                // wrong face now and the right one on the first clear of the session, which is the
                // worst possible moment to be rebuilding an atlas.
                EnsureAnnounceFonts();
                _ = AnnTextFont;

                _ = OnbPreviewPluginFace;
                PreloadOnboardingGameFaces();
#endif
            }
            catch (Exception)
            {
                // A face that cannot be built here will be asked for again at its call site and
                // fall back the way it always did. Nothing about this is worth failing a load over.
            }
        }
    }
}
