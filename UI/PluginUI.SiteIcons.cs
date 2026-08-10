using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures.TextureWraps;

namespace PfPresets
{
    /// <summary>
    /// The three sites a character can be looked up on, as their own marks.
    ///
    /// Embedded in the assembly rather than fetched at runtime. The plugin does not make requests
    /// on its own account, and it would be a poor look for a feature whose whole pitch is "nothing
    /// leaves your machine unless you press something" to quietly call out to three websites every
    /// time a profile opened. They are 32px PNGs and cost about five kilobytes in total.
    ///
    /// Loaded on first draw and never unloaded: three textures for the life of the session, which
    /// is less than one job icon's worth of churn.
    /// </summary>
    public partial class PluginUI
    {
        /// <summary>Which site a link goes to. The enum exists so the icon, the colour and the URL
        /// cannot drift apart into three parallel switch statements.</summary>
        internal enum LinkSite
        {
            Lodestone,
            Tomestone,
            FfLogs,
        }

        /// <summary>Concurrent because the decode lands on a worker thread while the frame thread
        /// is reading: a plain dictionary can rehash under the read.</summary>
        private readonly ConcurrentDictionary<string, IDalamudTextureWrap?> embeddedTextures = new();
        private readonly ConcurrentDictionary<string, byte> embeddedLoading = new();

        private static string ResourceFor(LinkSite site) => site switch
        {
            LinkSite.Lodestone => "PfPresets.Data.Icons.lodestone.png",
            LinkSite.Tomestone => "PfPresets.Data.Icons.tomestone.png",
            _ => "PfPresets.Data.Icons.fflogs.png",
        };

        /// <summary>
        /// The site's own accent, used for the hover border and glow.
        ///
        /// Taken from each site's branding rather than from the plugin's palette, because the point
        /// of the hover is to confirm where the click is about to take you - and the colour people
        /// recognise a site by is the site's, not ours.
        /// </summary>
        private static Vector4 SiteAccent(LinkSite site) => site switch
        {
            LinkSite.Lodestone => new Vector4(0.918f, 0.702f, 0.031f, 1f),
            LinkSite.Tomestone => new Vector4(0.388f, 0.400f, 0.945f, 1f),
            _ => new Vector4(0.886f, 0.408f, 0.659f, 1f),
        };

        private static string SiteName(LinkSite site) => site switch
        {
            LinkSite.Lodestone => "the Lodestone",
            LinkSite.Tomestone => "Tomestone",
            _ => "FFLogs",
        };

        /// <summary>
        /// An embedded PNG as a texture, decoded once and kept for the session.
        ///
        /// Shared by the site favicons and the Ultimate totems - both are small images baked into
        /// the assembly, and both want the same "return null until it lands, never ask twice"
        /// behaviour. Callers draw a fallback while it is null, so a slow decode costs a moment of
        /// plainness rather than a gap.
        /// </summary>
        private IDalamudTextureWrap? EmbeddedTexture(string resource)
        {
            if (embeddedTextures.TryGetValue(resource, out var loaded))
                return loaded;

            if (!embeddedLoading.TryAdd(resource, 0))
                return null;

            try
            {
                using var stream = typeof(PluginUI).Assembly.GetManifestResourceStream(resource);
                if (stream == null)
                {
                    embeddedTextures[resource] = null;
                    return null;
                }

                using var buffer = new MemoryStream();
                stream.CopyTo(buffer);
                byte[] bytes = buffer.ToArray();

                // The placeholder goes in BEFORE the decode is started, and that ordering is the
                // whole of a bug worth remembering.
                //
                // It used to be written after, on the reasonable-looking assumption that an async
                // decode could not possibly finish inside the next two statements. It can: these
                // are small PNGs and a warm texture provider can hand one back on the same tick.
                // When it did, the continuation stored the finished texture and then this line
                // immediately overwrote it with null - permanently, because the slot now exists and
                // the early return above never retries. Whichever image lost the race fell back to
                // its placeholder for the rest of the session, and which one varied per launch.
                embeddedTextures[resource] = null;

                _ = textureProvider.CreateFromImageAsync(bytes).ContinueWith(t =>
                {
                    embeddedTextures[resource] = t.IsCompletedSuccessfully ? t.Result : null;
                });
            }
            catch (Exception)
            {
                // Swallowed on purpose, and remembered as a null so it is not retried every frame.
                // A missing image costs a fallback glyph and nothing else.
                embeddedTextures[resource] = null;
            }

            return null;
        }

        private IDalamudTextureWrap? SiteIcon(LinkSite site) => EmbeddedTexture(ResourceFor(site));

        /// <summary>
        /// One favicon-sized link button.
        ///
        /// Lifts by two pixels and takes the site's colour on hover - the same gesture as the
        /// mockup, and the only animation on the card. Everything else here is flat and still, so a
        /// small movement is enough to say "this is a thing you can press" without a label.
        /// </summary>
        /// <returns>True on click.</returns>
        private bool DrawSiteLink(LinkSite site, string id, float size, string tooltip)
        {
            var dl = ImGui.GetWindowDrawList();
            Vector2 origin = ImGui.GetCursorScreenPos();

            ImGui.InvisibleButton($"##site{id}{site}", new Vector2(size, size));
            bool hovered = ImGui.IsItemHovered();
            bool clicked = ImGui.IsItemClicked();

            // The lift, eased over about a tenth of a second. Held in a dictionary keyed by the
            // button's own id so two cards on screen animate independently.
            float t = HoverLift($"{id}{site}", hovered);
            var pos = new Vector2(origin.X, origin.Y - 2f * t);
            var max = new Vector2(pos.X + size, pos.Y + size);

            var accent = SiteAccent(site);
            uint border = ImGui.ColorConvertFloat4ToU32(
                hovered ? accent : RuleHair);

            dl.AddRectFilled(pos, max, ImGui.ColorConvertFloat4ToU32(Field));

            // The glow, only on hover: a second rectangle a pixel out at a low alpha. Cheap, and it
            // reads as light rather than as a thicker border.
            if (t > 0f)
            {
                uint glow = ImGui.ColorConvertFloat4ToU32(accent with { W = 0.25f * t });
                dl.AddRect(new Vector2(pos.X - 1, pos.Y - 1), new Vector2(max.X + 1, max.Y + 1), glow, 0f, 0, 1f);
            }

            dl.AddRect(pos, max, border, 0f, 0, 1f);

            var icon = SiteIcon(site);
            const float inset = 3f;

            if (icon != null)
            {
                dl.AddImage(icon.Handle,
                    new Vector2(pos.X + inset, pos.Y + inset),
                    new Vector2(max.X - inset, max.Y - inset));
            }
            else
            {
                // Two letters in the site's colour, until the texture lands or instead of it.
                string mark = site switch
                {
                    LinkSite.Lodestone => "LS",
                    LinkSite.Tomestone => "TS",
                    _ => "FL",
                };

                using (UiCaptionFont.Push())
                {
                    Vector2 ts = ImGui.CalcTextSize(mark);
                    dl.AddText(new Vector2(pos.X + (size - ts.X) * 0.5f, pos.Y + (size - ts.Y) * 0.5f),
                        ImGui.ColorConvertFloat4ToU32(hovered ? accent : TextMuted), mark);
                }
            }

            if (hovered)
                PaddedTooltip(tooltip);

            return clicked;
        }

        /// <summary>Per-widget hover animation, eased, one entry per id.</summary>
        private readonly Dictionary<string, float> hoverLifts = new();

        private float HoverLift(string id, bool hovered)
        {
            hoverLifts.TryGetValue(id, out float t);

            // Roughly a tenth of a second either way, measured in frames rather than seconds
            // because that is what the rest of the plugin's animations do.
            float step = ImGui.GetIO().DeltaTime / 0.10f;
            t = Math.Clamp(hovered ? t + step : t - step, 0f, 1f);

            hoverLifts[id] = t;
            return Ease(t);
        }

        private void DisposeSiteIcons()
        {
            foreach (var icon in embeddedTextures.Values)
                icon?.Dispose();

            embeddedTextures.Clear();
            embeddedLoading.Clear();
        }
    }
}
