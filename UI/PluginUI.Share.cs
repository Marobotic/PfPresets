using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;

namespace PfPresets
{
    /// <summary>
    /// The two share windows: Export shows a preset's one-line code for copying, Import turns a
    /// pasted code back into a preset. Both are deliberately dead ends - Export can't edit the
    /// preset, Import can't apply anything until it has a code that actually validates.
    /// </summary>
    public partial class PluginUI
    {
        // ── Export state ──────────────────────────────────────────
        private bool isShareExportVisible = false;
        private string shareExportCode = string.Empty;
        private string shareExportPresetName = string.Empty;
        private double shareExportCopiedAt = 0;

        // ── Import state ──────────────────────────────────────────
        private bool isShareImportVisible = false;
        private string shareImportInput = string.Empty;
        private string shareImportError = string.Empty;
        private string shareImportSuccess = string.Empty;

        /// <summary>Buffer size for the paste box. A preset code is a few hundred characters; this
        /// leaves room for one that arrives wrapped in whitespace.</summary>
        private const int ShareCodeBufferSize = 8192;

        /// <summary>How long the "Copied!" confirmation stays up, in seconds.</summary>
        private const double CopiedFeedbackSeconds = 2.0;

        // ══════════════════════════════════════════════════════════
        //  ENTRY POINTS
        // ══════════════════════════════════════════════════════════

        private void OpenShareExport(PfPresetData preset)
        {
            try
            {
                shareExportCode = PresetShare.Export(preset);
                shareExportPresetName = preset.Name;
                shareExportCopiedAt = 0;
                isShareExportVisible = true;
            }
            catch (Exception)
            {
                // Encoding a preset shouldn't be able to fail, but a share window showing nothing
                // would be worse than not opening one.
                shareExportCode = string.Empty;
                isShareExportVisible = false;
            }
        }

        private void OpenShareImport()
        {
            shareImportInput = string.Empty;
            shareImportError = string.Empty;
            shareImportSuccess = string.Empty;
            isShareImportVisible = true;
        }

        // ══════════════════════════════════════════════════════════
        //  EXPORT WINDOW
        // ══════════════════════════════════════════════════════════

        private void DrawShareExportWindow()
        {
            if (!isShareExportVisible) return;

            ImGui.SetNextWindowSize(new Vector2(430, 250), ImGuiCond.FirstUseEver);
            ImGui.PushStyleColor(ImGuiCol.WindowBg, BgOuter);
            ImGui.PushStyleColor(ImGuiCol.Border, BorderDefault);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 1.0f);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 8.0f);
            ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 4.0f);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(16, 12));

            bool open = isShareExportVisible;
            try
            {
                if (ImGui.Begin("Share Preset##PfPresetsShareExport", ref open, ImGuiWindowFlags.NoCollapse))
                {
                    isShareExportVisible = open;

                    DrawSectionLabel("SHARE CODE");
                    ImGui.TextColored(TextSecondary, $"Anyone can paste this into PF Presets to get \"{shareExportPresetName}\".");
                    ImGui.Dummy(new Vector2(0, 6));

                    // Read-only so the code can be selected and copied by hand but never edited into
                    // something that no longer decodes.
                    PushFramedInput();
                    ImGui.InputTextMultiline(
                        "##ShareExportCode",
                        ref shareExportCode,
                        ShareCodeBufferSize,
                        new Vector2(-1, 90),
                        ImGuiInputTextFlags.ReadOnly);
                    PopFramedInput();

                    ImGui.Dummy(new Vector2(0, 8));

                    if (DrawPrimaryButton("Copy to Clipboard##ShareExportCopy", new Vector2(180, 30)))
                    {
                        ImGui.SetClipboardText(shareExportCode);
                        shareExportCopiedAt = ImGui.GetTime();
                    }

                    ImGui.SameLine(0, 8);
                    if (DrawSecondaryButton("Close##ShareExportClose", new Vector2(100, 30)))
                        isShareExportVisible = false;

                    // Transient confirmation next to the button.
                    if (shareExportCopiedAt > 0 && ImGui.GetTime() - shareExportCopiedAt < CopiedFeedbackSeconds)
                    {
                        ImGui.SameLine(0, 10);
                        ImGui.AlignTextToFramePadding();
                        ImGui.TextColored(AccentGreen, "Copied!");
                    }
                }
            }
            finally
            {
                ImGui.End();
                ImGui.PopStyleVar(4);
                ImGui.PopStyleColor(2);
            }
        }

        // ══════════════════════════════════════════════════════════
        //  IMPORT WINDOW
        // ══════════════════════════════════════════════════════════

        private void DrawShareImportWindow()
        {
            if (!isShareImportVisible) return;

            ImGui.SetNextWindowSize(new Vector2(430, 280), ImGuiCond.FirstUseEver);
            ImGui.PushStyleColor(ImGuiCol.WindowBg, BgOuter);
            ImGui.PushStyleColor(ImGuiCol.Border, BorderDefault);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 1.0f);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 8.0f);
            ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 4.0f);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(16, 12));

            bool open = isShareImportVisible;
            try
            {
                if (ImGui.Begin("Import Preset##PfPresetsShareImport", ref open, ImGuiWindowFlags.NoCollapse))
                {
                    isShareImportVisible = open;

                    DrawSectionLabel("PASTE A SHARE CODE");
                    ImGui.TextColored(TextSecondary, "Paste a PF Presets code below, or pull one straight\nfrom your clipboard.");
                    ImGui.Dummy(new Vector2(0, 6));

                    PushFramedInput();
                    if (ImGui.InputTextMultiline(
                            "##ShareImportInput",
                            ref shareImportInput,
                            ShareCodeBufferSize,
                            new Vector2(-1, 80)))
                    {
                        // Typing again clears the previous result so stale feedback never sits under
                        // a code the user has since changed.
                        shareImportError = string.Empty;
                        shareImportSuccess = string.Empty;
                    }
                    PopFramedInput();

                    ImGui.Dummy(new Vector2(0, 8));

                    if (DrawPrimaryButton("Import##ShareImportGo", new Vector2(120, 30)))
                        TryImportShareCode(shareImportInput);

                    ImGui.SameLine(0, 8);
                    if (DrawSecondaryButton("Import from Clipboard##ShareImportClipboard", new Vector2(190, 30)))
                    {
                        string clip = ReadClipboard();
                        shareImportInput = clip;
                        TryImportShareCode(clip);
                    }

                    ImGui.Dummy(new Vector2(0, 6));

                    if (!string.IsNullOrEmpty(shareImportError))
                    {
                        ImGui.PushTextWrapPos(0);
                        ImGui.TextColored(AccentRed, shareImportError);
                        ImGui.PopTextWrapPos();
                    }
                    else if (!string.IsNullOrEmpty(shareImportSuccess))
                    {
                        ImGui.PushTextWrapPos(0);
                        ImGui.TextColored(AccentGreen, shareImportSuccess);
                        ImGui.PopTextWrapPos();
                    }
                }
            }
            finally
            {
                ImGui.End();
                ImGui.PopStyleVar(4);
                ImGui.PopStyleColor(2);
            }
        }

        /// <summary>Validates a code and, if it holds up, adds the preset. Everything the user needs
        /// to know lands in <see cref="shareImportError"/> or <see cref="shareImportSuccess"/>.</summary>
        private void TryImportShareCode(string code)
        {
            shareImportError = string.Empty;
            shareImportSuccess = string.Empty;

            if (!PresetShare.TryImport(code, out var preset, out string error))
            {
                shareImportError = error;
                return;
            }

            var added = config.AddImportedPreset(preset);
            shareImportSuccess = $"Imported \"{added.Name}\".";
            shareImportInput = string.Empty;
        }

        /// <summary>Reads the clipboard, tolerating a host that has none set.</summary>
        private static string ReadClipboard()
        {
            try
            {
                return ImGui.GetClipboardText().ToString();
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }
    }
}
