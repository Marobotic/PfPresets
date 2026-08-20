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

                // Counted here rather than on Copy: producing the code is the export, and the
                // window shows it plainly enough that plenty of people never press the button.
                config.CountPresetExported();
                config.Save();
            }
            catch (Exception)
            {
                // Encoding a preset shouldn't be able to fail, but a share window showing nothing
                // would be worse than not opening one.
                shareExportCode = string.Empty;
                isShareExportVisible = false;
            }

            if (isShareExportVisible)
                OpenSheet(SheetKind.ShareExport);
        }

        private void OpenShareImport()
        {
            shareImportInput = string.Empty;
            shareImportError = string.Empty;
            shareImportSuccess = string.Empty;
            isShareImportVisible = true;
            OpenSheet(SheetKind.ShareImport);
        }

        // ══════════════════════════════════════════════════════════
        //  EXPORT SHEET
        // ══════════════════════════════════════════════════════════

        private void DrawShareExportSheet()
        {
            if (!isShareExportVisible)
            {
                CloseSheet();
                return;
            }

            if (!BeginSheet("ShareExport", "Share preset", 340f))
                return;

            try
            {
                if (BeginSheetBody(0f))
                {
                    try
                    {
                        DrawSectionLabel("SHARE CODE");
                        ImGui.PushTextWrapPos(0);
                        ImGui.TextColored(TextSecondary,
                            $"Anyone can paste this into PF Analysis to get \"{shareExportPresetName}\".");
                        ImGui.PopTextWrapPos();
                        ImGui.Dummy(new Vector2(0, 8));

                        // Read-only so the code can be selected and copied by hand but never edited
                        // into something that no longer decodes.
                        PushFramedInput();
                        ImGui.InputTextMultiline(
                            "##ShareExportCode",
                            ref shareExportCode,
                            ShareCodeBufferSize,
                            new Vector2(-1, 96),
                            ImGuiInputTextFlags.ReadOnly);
                        PopFramedInput();

                        ImGui.Dummy(new Vector2(0, 10));

                        bool justCopied = shareExportCopiedAt > 0
                            && ImGui.GetTime() - shareExportCopiedAt < CopiedFeedbackSeconds;

                        // The button says so itself rather than growing a "Copied!" beside it. On a
                        // 460px sheet there is no room for a third thing on that row, and the label
                        // changing under the cursor is the clearer confirmation anyway.
                        if (DrawPrimaryButton(
                                justCopied ? "Copied!##ShareExportCopy" : "Copy to clipboard##ShareExportCopy",
                                new Vector2(-1, ButtonHeight)))
                        {
                            ImGui.SetClipboardText(shareExportCode);
                            shareExportCopiedAt = ImGui.GetTime();
                        }
                    }
                    finally
                    {
                        EndSheetBody();
                    }
                }
                else
                {
                    EndSheetBody();
                }
            }
            finally
            {
                EndSheet();
            }
        }

        // ══════════════════════════════════════════════════════════
        //  IMPORT SHEET
        // ══════════════════════════════════════════════════════════

        private void DrawShareImportSheet()
        {
            if (!isShareImportVisible)
            {
                CloseSheet();
                return;
            }

            if (!BeginSheet("ShareImport", "Import preset", 360f))
                return;

            try
            {
                if (BeginSheetBody(0f))
                {
                    try
                    {
                        DrawSectionLabel("PASTE A SHARE CODE");
                        ImGui.PushTextWrapPos(0);
                        ImGui.TextColored(TextSecondary,
                            "Paste a PF Analysis code below, or pull one straight from your clipboard.");
                        ImGui.PopTextWrapPos();
                        ImGui.Dummy(new Vector2(0, 8));

                        PushFramedInput();
                        if (ImGui.InputTextMultiline(
                                "##ShareImportInput",
                                ref shareImportInput,
                                ShareCodeBufferSize,
                                new Vector2(-1, 86)))
                        {
                            // Typing again clears the previous result so stale feedback never sits
                            // under a code the user has since changed.
                            shareImportError = string.Empty;
                            shareImportSuccess = string.Empty;
                        }
                        PopFramedInput();

                        ImGui.Dummy(new Vector2(0, 10));

                        float half = (ImGui.GetContentRegionAvail().X - 8f) * 0.5f;

                        if (DrawPrimaryButton("Import##ShareImportGo", new Vector2(half, ButtonHeight)))
                            TryImportShareCode(shareImportInput);

                        ImGui.SameLine(0, 8);
                        if (DrawNeutralButton("From clipboard##ShareImportClipboard",
                                new Vector2(half, ButtonHeight)))
                        {
                            string clip = ReadClipboard();
                            shareImportInput = clip;
                            TryImportShareCode(clip);
                        }

                        ImGui.Dummy(new Vector2(0, 8));

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
                    finally
                    {
                        EndSheetBody();
                    }
                }
                else
                {
                    EndSheetBody();
                }
            }
            finally
            {
                EndSheet();
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
