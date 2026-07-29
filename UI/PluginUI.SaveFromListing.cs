using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;

namespace PfPresets
{
    /// <summary>
    /// The "Save as Preset" button that rides along with the game's Party Finder listing window.
    ///
    /// Drawn as a bare button - the host ImGui window is fully transparent and borderless, so
    /// nothing appears over the game except the button itself. It follows the listing window and
    /// disappears with it. Failures are reported in chat, since there's no panel to put a message on.
    /// </summary>
    public partial class PluginUI
    {
        private const float SaveListingButtonW = 150f;
        private const float SaveListingButtonH = 30f;

        // The duplicate check walks every preset, so it runs on a timer rather than per frame.
        private string? alreadySavedName;
        private double alreadySavedCheckedAt;
        private const double AlreadySavedRecheckSeconds = 0.5;

        private void DrawSaveFromListingOverlay()
        {
            if (!pfAutomation.TryGetListingWindowRect(out var addonPos, out var addonSize))
            {
                // Window closed - drop state so it doesn't carry over to the next listing.
                alreadySavedName = null;
                alreadySavedCheckedAt = 0;
                return;
            }

            if (!pfAutomation.HasViewedListing())
                return;

            if (ImGui.GetTime() - alreadySavedCheckedAt > AlreadySavedRecheckSeconds)
            {
                alreadySavedName = pfAutomation.FindPresetMatchingViewedListing(config);
                alreadySavedCheckedAt = ImGui.GetTime();
            }

            var buttonSize = new Vector2(SaveListingButtonW, SaveListingButtonH);

            // Sit just below the game's window, left-aligned with it.
            ImGui.SetNextWindowPos(new Vector2(addonPos.X, addonPos.Y + addonSize.Y + 4f), ImGuiCond.Always);
            ImGui.SetNextWindowSize(buttonSize, ImGuiCond.Always);

            // Fully transparent host window: no background, no border, no padding, so only the
            // button is visible over the game.
            ImGui.PushStyleColor(ImGuiCol.WindowBg, new Vector4(0f, 0f, 0f, 0f));
            ImGui.PushStyleColor(ImGuiCol.Border, new Vector4(0f, 0f, 0f, 0f));
            ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 0f);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);

            const ImGuiWindowFlags flags =
                ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove |
                ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse |
                ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoFocusOnAppearing |
                ImGuiWindowFlags.NoNavFocus | ImGuiWindowFlags.NoBackground |
                ImGuiWindowFlags.NoSavedSettings;

            try
            {
                if (ImGui.Begin("##PfPresetsSaveListing", flags))
                {
                    Vector2 btnPos = ImGui.GetCursorScreenPos();

                    if (alreadySavedName != null)
                    {
                        // Already saved: the button's own state is the confirmation, so there's
                        // nothing else to draw.
                        ImGui.BeginDisabled();
                        DrawSecondaryButton("##SaveListingPreset", buttonSize);
                        ImGui.EndDisabled();
                        DrawIconLabelCentered(FontAwesomeIcon.CheckCircle, "Preset saved",
                            btnPos, buttonSize, TextSecondary, 0.85f);

                        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                            PaddedTooltip($"Already saved as \"{alreadySavedName}\".");
                    }
                    else
                    {
                        if (DrawPrimaryButton("##SaveListingPreset", buttonSize))
                            TrySaveViewedListing();

                        DrawIconLabelCentered(FontAwesomeIcon.PlusCircle, "Save as Preset",
                            btnPos, buttonSize, JsOkText);

                        if (ImGui.IsItemHovered())
                            PaddedTooltip("Create a preset from the listing you're viewing.");
                    }
                }
            }
            finally
            {
                ImGui.End();
                ImGui.PopStyleVar(3);
                ImGui.PopStyleColor(2);
            }
        }

        /// <summary>Saves the open listing. Nothing is allowed to throw here - this draws over the
        /// game's own window, so an escaped exception would take the frame with it. Failures are
        /// reported to chat by the automation layer.</summary>
        private void TrySaveViewedListing()
        {
            try
            {
                var preset = pfAutomation.SaveViewedListingAsPreset(config, out _);
                if (preset == null)
                    return;

                // Flip the button to its saved state immediately rather than waiting for the next
                // scheduled re-check.
                alreadySavedName = preset.Name;
                alreadySavedCheckedAt = ImGui.GetTime();
            }
            catch (Exception)
            {
                // Already logged and reported downstream; swallow so the render thread survives.
            }
        }
    }
}
