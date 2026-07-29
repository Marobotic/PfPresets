using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace PfPresets
{
    /// <summary>
    /// The settings window (opened from the plugin installer's cog button).
    /// </summary>
    public partial class PluginUI
    {
        /// <summary>
        /// Kept because Dalamud's cog button calls it. Settings live in a tab now, so this opens
        /// the main window on that tab rather than spawning a second window.
        /// </summary>
        private void DrawSettingsWindow()
        {
            if (!isSettingsWindowVisible)
                return;

            isSettingsWindowVisible = false;
            isMainWindowVisible = true;
#if PFP_RATINGS
            activeTab = MainTab.Settings;
#endif
        }
    }
}
