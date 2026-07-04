using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace PfPresets
{
    /// <summary>
    /// The settings window (opened from the plugin installer's cog button).
    /// </summary>
    public partial class PluginUI
    {
        private void DrawSettingsWindow()
        {
            if (!isSettingsWindowVisible) return;

            ImGui.SetNextWindowSize(new Vector2(380, 200), ImGuiCond.FirstUseEver);
            ImGui.PushStyleColor(ImGuiCol.WindowBg, BgOuter);
            ImGui.PushStyleColor(ImGuiCol.Border, BorderDefault);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 1.0f);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 8.0f);
            ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 4.0f);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(16, 12));

            bool settingsOpen = isSettingsWindowVisible;
            if (ImGui.Begin("PF Presets Settings##PfPresetsSettings", ref settingsOpen, ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoResize))
            {
                isSettingsWindowVisible = settingsOpen;

                DrawSectionLabel("AUTO REFRESHER");

                if (IsRecruitmentRefresherActive())
                {
                    bool dummyVal = true;
                    ImGui.BeginDisabled(true);
                    DrawStyledCheckbox("Enable Auto Refresher##AutoRefresher", ref dummyVal);
                    ImGui.EndDisabled();

                    ImGui.TextColored(AccentYellow, "The RecruitmentRefresher plugin is handling refreshes.");
                }
                else
                {
                    bool autoRefresh = config.AutoRefresherEnabled;
                    if (DrawStyledCheckbox("Enable Auto Refresher##AutoRefresher", ref autoRefresh))
                    {
                        config.AutoRefresherEnabled = autoRefresh;
                        config.Save();
                    }
                    ImGui.TextColored(TextSecondary, "Automatically re-posts your Party Finder listing\nevery 15 or 30 minutes (set in the main window).");
                }

                ImGui.Dummy(new Vector2(0, 16));
                DrawSectionLabel("SUPPORT");

                ImGui.PushStyleColor(ImGuiCol.Button, AccentPurple);
                ImGui.PushStyleColor(ImGuiCol.ButtonHovered, ColorFromHex("#b08bff"));
                ImGui.PushStyleColor(ImGuiCol.ButtonActive, ColorFromHex("#824eff"));
                ImGui.PushStyleColor(ImGuiCol.Text, TextPrimary);
                ImGui.PushStyleColor(ImGuiCol.Border, BorderDefault);
                ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 1.0f);
                ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 6.0f);

                if (ImGui.Button("Support my project##Kofi", new Vector2(-1, 30)))
                {
                    Dalamud.Utility.Util.OpenLink("https://ko-fi.com/marobotic");
                }
                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip("Support the developer on Ko-fi!");
                }

                ImGui.PopStyleVar(2);
                ImGui.PopStyleColor(5);
            }
            ImGui.End();
            ImGui.PopStyleVar(4);
            ImGui.PopStyleColor(2);
        }
    }
}
