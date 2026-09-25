using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace PfPresets
{
    /// <summary>
    /// The navigation's icons: Lucide line icons, the set the iPadOS mockups are drawn with -
    /// thin, rounded strokes rather than FontAwesome's solid shapes. Embedded as white 48px PNGs
    /// (ISC licence, Data/Icons/lucide/LICENSE.txt) and tinted as they are drawn, so one image
    /// serves white, grey and anything between.
    /// </summary>
    public partial class PluginUI
    {
        /// <summary>Draws a Lucide icon in a square, tinted. False until its image has loaded -
        /// the caller draws its fallback for that frame.</summary>
        private bool DrawLucide(ImDrawListPtr dl, string name, Vector2 min, float size, Vector4 colour)
        {
            var tex = EmbeddedTexture($"PfPresets.Data.Icons.lucide.{name}.png");
            if (tex == null)
                return false;
            dl.AddImage(tex.Handle, min, min + new Vector2(size), Vector2.Zero, Vector2.One,
                ImGui.ColorConvertFloat4ToU32(colour));
            return true;
        }

        /// <summary>
        /// The settings mockup's info button, to the pixel: w-5 h-5 rounded-full bg-white/10
        /// (bg-white/20 on hover), holding Lucide's "info" at w-3 h-3 in slate-400 (white on hover).
        /// Drawn at <paramref name="min"/>; <paramref name="lit"/> keeps it in its hover state, for
        /// one whose explanation is open.
        /// </summary>
        private void DrawInfoCircle(ImDrawListPtr dl, Vector2 min, bool hot, bool lit = false)
        {
            const float size = 20f;
            bool on = hot || lit;
            var c = min + new Vector2(size * 0.5f);
            dl.AddCircleFilled(c, size * 0.5f,
                ImGui.ColorConvertFloat4ToU32(new Vector4(1, 1, 1, on ? 0.2f : 0.1f)), 32);

            // Lucide's "info" at w-3 h-3, drawn as its strokes rather than an image - a 48px icon
            // shrunk to twelve came out a blurred ring. Its geometry, on a 24-unit grid scaled to
            // 12px: a circle of radius 10, a stem from y=12 to 16 and a dot at y=8, stroked at 2
            // units (1px here) with round ends.
            uint ink = ImGui.ColorConvertFloat4ToU32(on ? new Vector4(1, 1, 1, 1) : ColorFromHex("#94a3b8"));
            const float scale = 12f / 24f;
            float stroke = MathF.Max(1.1f, 2f * scale);
            dl.AddCircle(c, 10f * scale, ink, 24, stroke);
            dl.AddLine(c + new Vector2(0, 0f), c + new Vector2(0, 4f * scale), ink, stroke);
            dl.AddCircleFilled(c + new Vector2(0, -4f * scale), stroke * 0.6f, ink, 8);
        }

#if PFP_RATINGS
        /// <summary>Each tab's Lucide icon.</summary>
        private static string? LucideFor(MainTab tab) => tab switch
        {
            MainTab.Presets => "users",
            MainTab.Ratings => "user",
            MainTab.Achievements => "trophy",
            MainTab.PartyFinder => "compass",
            MainTab.Vote => "check-square",
            MainTab.Feedback => "message-square",
            MainTab.Settings => "settings",
            MainTab.ExtraOne => "terminal",
            MainTab.ExtraTwo => "list-checks",
            _ => null,
        };
#endif
    }
}
