using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace PfPresets
{
    /// <summary>
    /// The Party Finder's own slot tiles, taken from the game rather than redrawn.
    ///
    /// The mixed-role, any-job and omitted slots have no entry in the icon set; the Party Finder
    /// draws them from ui/uld/LFG.tex, a coloured background with a figure laid over it. These are
    /// the same two layers from the same sheet, at the rectangles the game's own LFGCondition and
    /// LFGSearch layouts use (in the sheet's 132x316 units, so they fit the high-resolution copy
    /// too). If the sheet cannot be loaded, callers fall back to the old glyphs.
    /// </summary>
    public partial class PluginUI
    {
        internal enum PfSlotTile
        {
            Any,
            Omit,
            TankHealer,
            TankDps,
            HealerDps,
            AllRoles,
        }

        private const string LfgSheetPath = "ui/uld/LFG_hr1.tex";
        private static readonly Vector2 LfgSheetUnits = new(132f, 316f);

        // Backgrounds, 36x36.
        private static readonly Vector2 LfgBgAny = new(36f, 44f);
        private static readonly Vector2 LfgBgDark = new(72f, 44f);
        private static readonly Vector2 LfgBgTankHealer = new(56f, 136f);
        private static readonly Vector2 LfgBgTankDps = new(0f, 172f);
        private static readonly Vector2 LfgBgHealerDps = new(36f, 172f);
        private static readonly Vector2 LfgBgAllRoles = new(72f, 172f);
        private const float LfgBgSide = 36f;

        // Figures, 28x28, laid centred over a background.
        private static readonly Vector2 LfgPerson = new(84f, 108f);
        private static readonly Vector2 LfgOmit = new(0f, 136f);
        private const float LfgFigureSide = 28f;

        /// <summary>The Party Finder's tile for a slot kind, drawn at <paramref name="size"/>
        /// pixels. False when the game's sheet is not available yet.</summary>
        private bool DrawPfTile(ImDrawListPtr dl, PfSlotTile tile, Vector2 topLeft, float size)
        {
            if (!TryGetLfgSheet(out var sheet))
                return false;

            // The tile art sits inside its square with a margin of its own, where a job icon fills
            // its square edge to edge - so at the same size the tile read as the smaller of the two.
            // Drawn a touch larger, about its own centre, so the two look the same size.
            float grow = size * 0.06f;
            topLeft -= new Vector2(grow);
            size += grow * 2f;

            Vector2 bg = tile switch
            {
                PfSlotTile.Any => LfgBgAny,
                PfSlotTile.Omit => LfgBgDark,
                PfSlotTile.TankHealer => LfgBgTankHealer,
                PfSlotTile.TankDps => LfgBgTankDps,
                PfSlotTile.HealerDps => LfgBgHealerDps,
                _ => LfgBgAllRoles,
            };
            Vector2 figure = tile == PfSlotTile.Omit ? LfgOmit : LfgPerson;

            dl.AddImage(sheet, topLeft, topLeft + new Vector2(size, size),
                bg / LfgSheetUnits, (bg + new Vector2(LfgBgSide)) / LfgSheetUnits);

            float f = size * LfgFigureSide / LfgBgSide;
            Vector2 fMin = topLeft + new Vector2((size - f) * 0.5f);
            dl.AddImage(sheet, fMin, fMin + new Vector2(f, f),
                figure / LfgSheetUnits, (figure + new Vector2(LfgFigureSide)) / LfgSheetUnits);
            return true;
        }

        private bool DrawPfTile(PfSlotTile tile, Vector2 topLeft, float size)
            => DrawPfTile(ImGui.GetWindowDrawList(), tile, topLeft, size);

        /// <summary>The mixed-role tile for a set of accepted jobs spanning two or three roles.</summary>
        private static PfSlotTile SplitTileFor(bool tank, bool healer, bool dps)
            => tank && healer && dps ? PfSlotTile.AllRoles
             : tank && healer ? PfSlotTile.TankHealer
             : tank && dps ? PfSlotTile.TankDps
             : PfSlotTile.HealerDps;

        private bool TryGetLfgSheet(out ImTextureID handle)
        {
            try
            {
                var tex = textureProvider.GetFromGame(LfgSheetPath);
                if (tex != null && tex.TryGetWrap(out var wrap, out _))
                {
                    handle = wrap.Handle;
                    return true;
                }
            }
            catch
            {
                // Falls back to the glyphs.
            }

            handle = default;
            return false;
        }
    }
}
