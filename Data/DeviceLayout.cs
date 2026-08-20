using System.Numerics;

namespace PfPresets
{
    /// <summary>
    /// Which of the two fixed windows the plugin draws itself as.
    ///
    /// PORTRAIT AND LANDSCAPE, not iPhone and iPad. The names were the devices for a while, which
    /// is where the shapes came from, but this is a plugin for a game on a desktop - naming a
    /// window after somebody else's hardware tells a player nothing about what it will do, and one
    /// of the two names is a phone they may well not own. Tall or wide is the whole choice.
    ///
    /// There are two, and there is no third. The window used to be resizable between 400x560 and
    /// 1600x1200, which meant every surface in the plugin had to survive four hundred widths and
    /// none of them were designed - the two-column bodies collapsed at some unmarked point, the rail
    /// folded into a strip at another, and rows truncated wherever a person happened to leave the
    /// edge. Every one of those in-between states was a layout nobody drew and nobody checked.
    ///
    /// Fixing the size to two known shapes is what buys the rest of the design: a body can be laid
    /// out for exactly 460 or exactly 1180 and be right, sheets can be sized as a fraction of a
    /// number that does not move, and there is no minimise, no resize handle and no persisted
    /// width to restore. The player picks one in Settings, the same way they pick a phone.
    /// </summary>
    public enum DeviceLayout
    {
        /// <summary>The tall one: 460 x 900. One column, a bottom tab bar, sheets that slide up
        /// from the bottom edge. Fits beside the game.</summary>
        Portrait = 0,

        /// <summary>The wide one: 1180 x 820. A sidebar, two-column bodies, sheets centred on the
        /// screen. Wants a big monitor.</summary>
        Landscape = 1,
    }

    /// <summary>The fixed geometry of each layout. One place, so the window, the sheets and the
    /// bodies can never disagree about how big the screen is.</summary>
    public static class DeviceMetrics
    {
        /// <summary>The tall window, in ImGui units.</summary>
        public static readonly Vector2 PortraitSize = new(460f, 900f);

        /// <summary>The wide window, in ImGui units.</summary>
        public static readonly Vector2 LandscapeSize = new(1180f, 820f);

        public static Vector2 SizeOf(DeviceLayout layout)
            => layout == DeviceLayout.Landscape ? LandscapeSize : PortraitSize;

        /// <summary>Corner radius of the screen itself. The tall window's is dramatic, the wide
        /// one's is not - the same relationship a phone and a tablet have.</summary>
        public static float ScreenRadius(DeviceLayout layout)
            => layout == DeviceLayout.Landscape ? PluginUI.Radius.ScreenWide : PluginUI.Radius.Screen;

        /// <summary>Human-readable size, for the Settings switch.</summary>
        public static string SizeLabel(DeviceLayout layout)
        {
            Vector2 s = SizeOf(layout);
            return $"{s.X:F0} × {s.Y:F0}";
        }
    }
}
