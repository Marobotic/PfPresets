using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace PfPresets
{
    /// <summary>
    /// The one confirmation dialog, and the chrome every dialog in the plugin is built from.
    ///
    /// There were four of these: delete a preset, kick a player, disband a party, clear local
    /// data. All the same shape - a question, a consequence, a red button and a way out - and all
    /// written separately, which is why they had drifted into looking and behaving differently.
    /// Two of them confirmed in place by rewriting a button under the cursor, which is the worst
    /// version of the pattern.
    ///
    /// Anything destructive asks through <see cref="AskConfirm"/>. Adding another one should mean
    /// one call, not another window.
    /// </summary>
    public partial class PluginUI
    {
        private sealed class ConfirmRequest
        {
            public string Title = "Are you sure?";
            public string Question = string.Empty;

            /// <summary>The consequence, in a quieter colour. Optional - omit it rather than pad
            /// the dialog with a restatement of the question.</summary>
            public string? Detail;

            public string ConfirmLabel = "Yes";
            public string CancelLabel = "Never mind";

            public Action? OnConfirm;
        }

        private ConfirmRequest? pendingConfirm;

        /// <summary>
        /// Asks before doing something irreversible. The action runs only if the user says yes;
        /// dismissing the window in any other way counts as no.
        /// </summary>
        private void AskConfirm(string title, string question, string confirmLabel, Action onConfirm,
            string? detail = null, string cancelLabel = "Never mind")
        {
            pendingConfirm = new ConfirmRequest
            {
                Title = title,
                Question = question,
                Detail = detail,
                ConfirmLabel = confirmLabel,
                CancelLabel = cancelLabel,
                OnConfirm = onConfirm,
            };
        }

        /// <summary>True while a confirmation is on screen, so callers can avoid stacking a second
        /// question on top of the first.</summary>
        private bool IsConfirming => pendingConfirm != null;

        private void DrawConfirmDialog()
        {
            if (pendingConfirm == null)
                return;

            var request = pendingConfirm;
            bool open = true;

            if (BeginDialog(request.Title, "PfPresetsConfirm", 300f, ref open))
            {
                try
                {
                    ImGui.PushTextWrapPos(0);
                    ImGui.TextColored(TextPrimary, request.Question);

                    if (!string.IsNullOrEmpty(request.Detail))
                    {
                        ImGui.Dummy(new Vector2(0, 4));
                        ImGui.TextColored(TextMuted, request.Detail);
                    }
                    ImGui.PopTextWrapPos();

                    ImGui.Dummy(new Vector2(0, 12));

                    if (DrawDangerButton(request.ConfirmLabel, new Vector2(150, 28)))
                    {
                        // Cleared before invoking, so an action that opens another dialog isn't
                        // immediately closed again by this one.
                        pendingConfirm = null;
                        request.OnConfirm?.Invoke();
                    }

                    ImGui.SameLine(0, 8);
                    if (DrawSecondaryButton($"{request.CancelLabel}##ConfirmCancel", new Vector2(-1, 28)))
                        pendingConfirm = null;
                }
                finally
                {
                    EndDialog();
                }
            }
            else
            {
                EndDialog();
            }

            if (!open)
                pendingConfirm = null;
        }

        // ══════════════════════════════════════════════════════════
        //  SHARED CHROME
        // ══════════════════════════════════════════════════════════

        /// <summary>
        /// Opens a centred dialog with the plugin's window styling. Every dialog was pushing the
        /// same five style values by hand; getting that stack wrong corrupts ImGui for every other
        /// plugin, so it exists once.
        ///
        /// Always pair with <see cref="EndDialog"/>, including when this returns false.
        /// </summary>
        private static bool BeginDialog(string title, string id, float width, ref bool open)
        {
            var vp = ImGui.GetMainViewport();
            ImGui.SetNextWindowPos(
                new Vector2(vp.WorkPos.X + vp.WorkSize.X * 0.5f, vp.WorkPos.Y + vp.WorkSize.Y * 0.42f),
                ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
            ImGui.SetNextWindowSize(new Vector2(width, 0), ImGuiCond.Always);

            ImGui.PushStyleColor(ImGuiCol.WindowBg, BgOuter);
            ImGui.PushStyleColor(ImGuiCol.Border, BorderDefault);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 1.0f);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 8.0f);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(16, 14));

            return ImGui.Begin($"{title}##{id}", ref open,
                ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoResize
                | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.AlwaysAutoResize);
        }

        /// <summary>Closes a dialog opened by <see cref="BeginDialog"/> and unwinds its styles.</summary>
        private static void EndDialog()
        {
            ImGui.End();
            ImGui.PopStyleVar(3);
            ImGui.PopStyleColor(2);
        }

        /// <summary>The red button used for anything that destroys something. Was copied into four
        /// places with three slightly different sets of colours.</summary>
        private static bool DrawDangerButton(string label, Vector2 size)
        {
            ImGui.PushStyleColor(ImGuiCol.Button, AccentRed);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, ColorFromHex("#e8806f"));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, ColorFromHex("#c75446"));
            ImGui.PushStyleColor(ImGuiCol.Text, ColorFromHex("#fff1ee"));
            ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 6.0f);

            bool clicked = ImGui.Button(label, size);

            ImGui.PopStyleVar();
            ImGui.PopStyleColor(4);
            return clicked;
        }
    }
}
