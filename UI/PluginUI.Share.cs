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

        /// <summary>Whether the paste box had focus last frame - it shows its own text while being
        /// typed in, and the wrapped copy otherwise.</summary>
        private bool shareImportActive;

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

            if (!BeginSheet("ShareExport", "Share preset", 340f, ShareSheetWidth))
                return;

            try
            {
                if (BeginSheetBody(0f))
                {
                    try
                    {
                        float width = ImGui.GetContentRegionAvail().X;

                        DrawSectionCaption("SHARE CODE");
                        using (UiHelpFont.Push())
                        {
                            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + width);
                            ImGui.TextColored(FbSlate400,
                                $"Anyone can paste this into PF Analysis to get \"{shareExportPresetName}\".");
                            ImGui.PopTextWrapPos();
                        }
                        ImGui.Dummy(new Vector2(0, 6));

                        // The code, all of it, broken across lines wherever it has to be - it has no
                        // spaces, so ImGui's word wrap would leave it one line running off the box.
                        // Shown rather than edited: Copy below is how it leaves, and a code nobody
                        // can type into is a code nobody can break.
                        DrawWrappedCodeBox(shareExportCode, width);

                        ImGui.Dummy(new Vector2(0, 10));

                        bool justCopied = shareExportCopiedAt > 0
                            && ImGui.GetTime() - shareExportCopiedAt < CopiedFeedbackSeconds;

                        // The button says so itself rather than growing a "Copied!" beside it - the
                        // label changing under the cursor is the clearer confirmation.
                        if (DrawIosButton(justCopied ? "Copied!" : "Copy to clipboard", "##ShareExportCopy",
                                justCopied ? FontAwesomeIcon.Check : FontAwesomeIcon.Copy,
                                new Vector2(width, ButtonHeight), primary: true))
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

            if (!BeginSheet("ShareImport", "Import preset", 360f, ShareSheetWidth))
                return;

            try
            {
                if (BeginSheetBody(0f))
                {
                    try
                    {
                        float width = ImGui.GetContentRegionAvail().X;

                        DrawSectionCaption("PASTE A SHARE CODE");
                        using (UiHelpFont.Push())
                        {
                            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + width);
                            ImGui.TextColored(FbSlate400,
                                "Paste a PF Analysis code below, or pull one straight from your clipboard.");
                            ImGui.PopTextWrapPos();
                        }
                        ImGui.Dummy(new Vector2(0, 6));

                        // WRAPPED WHEREVER IT HAS TO BREAK. A pasted code has no spaces, so the field
                        // cannot wrap it itself: while it is not being typed in, the field's own text
                        // is hidden and the code is drawn over it broken across lines, all on screen.
                        Vector2 boxMin = ImGui.GetCursorScreenPos();
                        const float boxH = 100f;
                        bool typing = shareImportActive;
                        if (!typing && shareImportInput.Length > 0)
                            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0, 0, 0, 0));
                        if (DrawIosTextBox("##ShareImportInput", ref shareImportInput, ShareCodeBufferSize,
                                new Vector2(width, boxH), readOnly: false))
                        {
                            // Typing again clears the previous result so stale feedback never sits
                            // under a code the user has since changed.
                            shareImportError = string.Empty;
                            shareImportSuccess = string.Empty;
                        }
                        if (!typing && shareImportInput.Length > 0)
                            ImGui.PopStyleColor();
                        shareImportActive = ImGui.IsItemActive();

                        var boxDl = ImGui.GetWindowDrawList();
                        if (shareImportInput.Length == 0)
                        {
                            boxDl.AddText(boxMin + new Vector2(10f),
                                ImGui.ColorConvertFloat4ToU32(FbSlate500), "Paste a share code here");
                        }
                        else if (!typing)
                        {
                            using (UiBodyFont.Push())
                            {
                                float lh = ImGui.GetTextLineHeight() + 2f;
                                var lines = HardWrap(shareImportInput.Trim(), width - 20f);
                                int fit = Math.Max(1, (int)((boxH - 20f) / lh));
                                for (int i = 0; i < lines.Count && i < fit; i++)
                                {
                                    string line = i == fit - 1 && lines.Count > fit ? Fit(lines[i] + "…", width - 20f) : lines[i];
                                    boxDl.AddText(boxMin + new Vector2(10f, 10f + i * lh),
                                        ImGui.ColorConvertFloat4ToU32(PfSlate300), line);
                                }
                            }
                        }

                        ImGui.Dummy(new Vector2(0, 10));

                        float half = (width - 8f) * 0.5f;

                        if (DrawIosButton("Import", "##ShareImportGo", FontAwesomeIcon.FileImport,
                                new Vector2(half, ButtonHeight), primary: true,
                                enabled: !string.IsNullOrWhiteSpace(shareImportInput)))
                            TryImportShareCode(shareImportInput);

                        ImGui.SameLine(0, 8);
                        if (DrawIosButton("From clipboard", "##ShareImportClipboard", FontAwesomeIcon.Paste,
                                new Vector2(half, ButtonHeight), primary: false))
                        {
                            string clip = ReadClipboard();
                            shareImportInput = clip;
                            TryImportShareCode(clip);
                        }

                        ImGui.Dummy(new Vector2(0, 8));

                        if (!string.IsNullOrEmpty(shareImportError))
                            DrawIosNote(shareImportError, AccentRed, width);
                        else if (!string.IsNullOrEmpty(shareImportSuccess))
                            DrawIosNote(shareImportSuccess, AccentGreen, width);
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

        /// <summary>The share sheets on the wide layout: a code and a button or two, which a 760px
        /// sheet spread out to a sliver of text in a slab of nothing.</summary>
        private const float ShareSheetWidth = 460f;

        /// <summary>Lines of text that break anywhere - mid-word when a word is longer than the
        /// line, which a share code always is.</summary>
        private static System.Collections.Generic.List<string> HardWrap(string text, float width)
        {
            var lines = new System.Collections.Generic.List<string>();
            int start = 0;
            while (start < text.Length)
            {
                int len = 1;
                while (start + len < text.Length && ImGui.CalcTextSize(text.Substring(start, len + 1)).X <= width)
                    len++;
                lines.Add(text.Substring(start, len));
                start += len;
            }
            return lines;
        }

        /// <summary>A read-only code on the rounded field, wrapped to it. Selecting it is not
        /// needed - the Copy button is right below.</summary>
        private void DrawWrappedCodeBox(string code, float width)
        {
            const float pad = 12f;
            var dl = ImGui.GetWindowDrawList();
            Vector2 min = ImGui.GetCursorScreenPos();

            float lh;
            System.Collections.Generic.List<string> lines;
            using (UiBodyFont.Push())
            {
                lh = ImGui.GetTextLineHeight() + 2f;
                lines = HardWrap(code, width - pad * 2f);
            }
            float h = MathF.Max(64f, pad * 2f + lines.Count * lh);
            var max = min + new Vector2(width, h);

            dl.AddRectFilled(min, max, ImGui.ColorConvertFloat4ToU32(new Vector4(0.463f, 0.463f, 0.502f, 0.22f)), Radius.Card);
            dl.AddRect(min, max, ImGui.ColorConvertFloat4ToU32(new Vector4(1, 1, 1, 0.05f)), Radius.Card, ImDrawFlags.None, 1f);
            using (UiBodyFont.Push())
            {
                for (int i = 0; i < lines.Count; i++)
                    dl.AddText(min + new Vector2(pad, pad + i * lh), ImGui.ColorConvertFloat4ToU32(PfSlate300), lines[i]);
            }

            ImGui.Dummy(new Vector2(width, h));
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
