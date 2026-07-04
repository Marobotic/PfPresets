using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;

namespace PfPresets
{
    /// <summary>
    /// The "Applying Preset" status overlay: a centered card with a friendly stage
    /// description, an animated progress bar, and a Done/Close button.
    /// </summary>
    public partial class PluginUI
    {
        private bool checklistWasOpen = false;
        private float animatedProgress = 0f;

        private void DrawChecklistOverlay()
        {
            if (!pfAutomation.ShowChecklist || pfAutomation.ActivePreset == null)
            {
                checklistWasOpen = false;
                return;
            }
            var preset = pfAutomation.ActivePreset;

            // Reset the animated bar each time a fresh apply begins.
            if (!checklistWasOpen) { checklistWasOpen = true; animatedProgress = 0f; }

            bool done = pfAutomation.IsAutomationDone;
            bool failed = pfAutomation.IsAutomationFailed;

            // Ease the bar toward the real progress so it fills smoothly instead of jumping.
            float target = done && !failed ? 1f : pfAutomation.AutomationProgress;
            animatedProgress += (target - animatedProgress) * MathF.Min(1f, ImGui.GetIO().DeltaTime * 9f);
            if (MathF.Abs(target - animatedProgress) < 0.002f) animatedProgress = target;

            var vp = ImGui.GetMainViewport();
            ImGui.SetNextWindowPos(
                new Vector2(vp.WorkPos.X + vp.WorkSize.X * 0.5f, vp.WorkPos.Y + vp.WorkSize.Y * 0.5f),
                ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
            ImGui.SetNextWindowSize(new Vector2(400, 216), ImGuiCond.Always);

            ImGui.PushStyleColor(ImGuiCol.WindowBg, BgOuter);
            ImGui.PushStyleColor(ImGuiCol.Border, BorderDefault);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 1.0f);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 10.0f);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(20, 18));

            // Custom header (no ImGui title bar) to match the main window's design.
            if (ImGui.Begin("##Checklist",
                ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoResize |
                ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoTitleBar))
            {
                Vector2 origin = ImGui.GetCursorScreenPos();
                float innerW = ImGui.GetContentRegionAvail().X;
                float contentH = ImGui.GetContentRegionAvail().Y;
                var dl = ImGui.GetWindowDrawList();
                float lineH = ImGui.GetTextLineHeight();

                // ── Header: logo box + title + preset name ──
                Vector2 logoMin = origin;
                Vector2 logoMax = new Vector2(origin.X + 34, origin.Y + 34);
                dl.AddRectFilled(logoMin, logoMax, ImGui.ColorConvertFloat4ToU32(ColorFromHex("#1e2a40")), 8f);
                string logoGlyph = FontAwesomeIcon.ClipboardList.ToIconString();
                using (pluginInterface.UiBuilder.IconFontHandle.Push())
                {
                    Vector2 gs = ImGui.CalcTextSize(logoGlyph);
                    dl.AddText(new Vector2(logoMin.X + (34 - gs.X) * 0.5f, logoMin.Y + (34 - gs.Y) * 0.5f),
                        ImGui.ColorConvertFloat4ToU32(AccentBlue), logoGlyph);
                }
                dl.AddText(new Vector2(origin.X + 46, origin.Y + 3),
                    ImGui.ColorConvertFloat4ToU32(TextPrimary), "Applying Preset");
                string sub = string.IsNullOrEmpty(preset.Name) ? "Unnamed preset" : preset.Name;
                dl.AddText(new Vector2(origin.X + 46, origin.Y + 20),
                    ImGui.ColorConvertFloat4ToU32(TextMuted), sub);

                // Close button (top-right), matching the main window's header buttons.
                const float closeSz = 24f;
                ImGui.SetCursorScreenPos(new Vector2(origin.X + innerW - closeSz, origin.Y + 4));
                ImGui.PushStyleColor(ImGuiCol.Button, BgCard);
                ImGui.PushStyleColor(ImGuiCol.ButtonHovered, ColorFromHex("#1c2230"));
                ImGui.PushStyleColor(ImGuiCol.ButtonActive, ColorFromHex("#243a54"));
                ImGui.PushStyleColor(ImGuiCol.Text, TextSecondary);
                ImGui.PushStyleColor(ImGuiCol.Border, BorderDefault);
                ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 1f);
                ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 6f);
                ImGui.PushFont(UiBuilder.IconFont);
                if (ImGui.Button($"{FontAwesomeIcon.Times.ToIconString()}##CloseChecklist", new Vector2(closeSz, closeSz)))
                    pfAutomation.DismissChecklist();
                ImGui.PopFont();
                ImGui.PopStyleVar(2);
                ImGui.PopStyleColor(5);

                // ── Divider ──
                float divY = origin.Y + 48;
                dl.AddLine(new Vector2(origin.X, divY), new Vector2(origin.X + innerW, divY),
                    ImGui.ColorConvertFloat4ToU32(BorderDefault), 1f);

                // ── Status row: spinner/check + friendly stage text + percentage ──
                float rowY = origin.Y + 68;
                string stage = pfAutomation.AutomationStage;
                Vector4 stageColor = failed ? AccentRed : (done ? AccentGreen : TextPrimary);
                if (failed)
                    DrawGlyphAt(FontAwesomeIcon.TimesCircle, new Vector2(origin.X, rowY), 16f, AccentRed);
                else if (done)
                    DrawGlyphAt(FontAwesomeIcon.CheckCircle, new Vector2(origin.X, rowY), 16f, AccentGreen);
                else
                    DrawSpinner(new Vector2(origin.X + 8f, rowY + lineH * 0.5f), 8f, 2.5f, AccentBlue);

                dl.AddText(new Vector2(origin.X + 26, rowY), ImGui.ColorConvertFloat4ToU32(stageColor), stage);

                if (!done)
                {
                    string pct = $"{(int)MathF.Round(animatedProgress * 100f)}%";
                    Vector2 pts = ImGui.CalcTextSize(pct);
                    dl.AddText(new Vector2(origin.X + innerW - pts.X, rowY),
                        ImGui.ColorConvertFloat4ToU32(TextMuted), pct);
                }

                // ── Progress bar ──
                Vector4 barFill = failed ? AccentRed : (done ? AccentGreen : AccentBlue);
                DrawProgressBar(new Vector2(origin.X, origin.Y + 98), innerW, 12f, animatedProgress, barFill);

                // ── Bottom: Done/Close button when finished, else a "please wait" hint ──
                const float btnH = 34f;
                float btnY = origin.Y + contentH - btnH;
                if (done)
                {
                    ImGui.SetCursorScreenPos(new Vector2(origin.X, btnY));
                    bool dismiss = failed
                        ? DrawSecondaryButton("Close##DismissChecklist", new Vector2(innerW, btnH))
                        : DrawPrimaryButton("Done :)##DismissChecklist", new Vector2(innerW, btnH));
                    if (dismiss)
                        pfAutomation.DismissChecklist();
                }
                else
                {
                    const string hint = "Setting things up, please wait...";
                    Vector2 hs = ImGui.CalcTextSize(hint);
                    dl.AddText(new Vector2(origin.X + (innerW - hs.X) * 0.5f, btnY + (btnH - lineH) * 0.5f),
                        ImGui.ColorConvertFloat4ToU32(TextMuted), hint);
                }
            }
            ImGui.End();
            ImGui.PopStyleVar(3);
            ImGui.PopStyleColor(2);
        }

        /// <summary>Draws a rounded (pill) progress bar matching the plugin theme.</summary>
        private void DrawProgressBar(Vector2 pos, float width, float height, float progress, Vector4 fill)
        {
            progress = Math.Clamp(progress, 0f, 1f);
            var dl = ImGui.GetWindowDrawList();
            float r = height * 0.5f;
            Vector2 max = new Vector2(pos.X + width, pos.Y + height);

            // Track
            dl.AddRectFilled(pos, max, ImGui.ColorConvertFloat4ToU32(ColorFromHex("#141b27")), r);
            dl.AddRect(pos, max, ImGui.ColorConvertFloat4ToU32(BorderDefault), r, ImDrawFlags.None, 1f);

            // Fill (kept at least a full pill width so even early progress reads as rounded)
            if (progress > 0f)
            {
                float fw = MathF.Min(MathF.Max(width * progress, height), width);
                Vector2 fmax = new Vector2(pos.X + fw, max.Y);
                dl.AddRectFilled(pos, fmax, ImGui.ColorConvertFloat4ToU32(fill), r);
                // Subtle top sheen for a bit of depth.
                Vector4 sheen = new Vector4(1f, 1f, 1f, 0.10f);
                dl.AddRectFilled(pos, new Vector2(pos.X + fw, pos.Y + height * 0.5f),
                    ImGui.ColorConvertFloat4ToU32(sheen), r, ImDrawFlags.RoundCornersTop);
            }
        }

        /// <summary>Draws a small rotating arc spinner (animated via ImGui time).</summary>
        private void DrawSpinner(Vector2 center, float radius, float thickness, Vector4 color)
        {
            var dl = ImGui.GetWindowDrawList();
            uint col = ImGui.ColorConvertFloat4ToU32(color);
            float t = (float)ImGui.GetTime();
            const int segs = 24;
            float start = t * 5f;
            float arc = MathF.PI * 1.5f;
            Vector2? prev = null;
            for (int i = 0; i <= segs; i++)
            {
                float a = start + (i / (float)segs) * arc;
                var p = new Vector2(center.X + MathF.Cos(a) * radius, center.Y + MathF.Sin(a) * radius);
                if (prev.HasValue) dl.AddLine(prev.Value, p, col, thickness);
                prev = p;
            }
        }
    }
}
