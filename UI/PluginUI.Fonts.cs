using System;
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

        private static byte[]? robotoBytes;

        private static byte[] Roboto()
        {
            if (robotoBytes != null)
                return robotoBytes;

            using var stream = typeof(PluginUI).Assembly
                .GetManifestResourceStream("PfPresets.Data.Fonts.Roboto.ttf")
                ?? throw new InvalidOperationException("Roboto.ttf missing from the assembly.");

            using var buffer = new System.IO.MemoryStream();
            stream.CopyTo(buffer);
            return robotoBytes = buffer.ToArray();
        }

        /// <summary>Builds the handle on first use and caches it in the caller's field.</summary>
        private IFontHandle Font(ref IFontHandle? slot, float px)
        {
            slot ??= pluginInterface.UiBuilder.FontAtlas.NewDelegateFontHandle(tk =>
                tk.OnPreBuild(pre => pre.AddFontFromMemory(
                    Roboto(), new SafeFontConfig { SizePx = px }, "Roboto")));
            return slot;
        }
    }
}
