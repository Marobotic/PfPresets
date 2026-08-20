using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
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

            /// <summary>
            /// Run when the answer is no, however it was given - the cancel button, the close
            /// cross, or Escape.
            ///
            /// Needed because not every question is asked BEFORE the change. A slider has already
            /// moved by the time the handle is let go, so "no" there means putting it back, and a
            /// dialog that only reports yes would leave the control showing a setting the person
            /// declined.
            /// </summary>
            public Action? OnCancel;

            /// <summary>
            /// Whether the confirming button is the red one.
            ///
            /// Destructive by default, because that is what this dialog was built for. Turning a
            /// feature ON is not destructive, and painting it red would be the interface flinching
            /// at something it just offered - the colour is supposed to mean "this removes
            /// something", and it stops meaning anything if every question wears it.
            /// </summary>
            public bool Danger = true;
        }

        private ConfirmRequest? pendingConfirm;

        /// <summary>
        /// Asks before doing something irreversible. The action runs only if the user says yes;
        /// dismissing the window in any other way counts as no.
        /// </summary>
        private void AskConfirm(string title, string question, string confirmLabel, Action onConfirm,
            string? detail = null, string cancelLabel = "Never mind", bool danger = true,
            Action? onCancel = null)
        {
            pendingConfirm = new ConfirmRequest
            {
                Title = title,
                Question = question,
                Detail = detail,
                ConfirmLabel = confirmLabel,
                CancelLabel = cancelLabel,
                OnConfirm = onConfirm,
                OnCancel = onCancel,
                Danger = danger,
            };

            OpenSheet(SheetKind.Confirm);
        }

        /// <summary>The answer when a confirmation is dismissed rather than answered - by the close
        /// button, by tapping away from it, or by Escape. Always "no", and always with the undo,
        /// or a slider dismissed with Escape would keep a value nobody agreed to.</summary>
        private void DismissConfirmDialog()
        {
            var dismissed = pendingConfirm;
            pendingConfirm = null;
            CloseSheet();
            dismissed?.OnCancel?.Invoke();
        }

        /// <summary>True while a confirmation is on screen, so callers can avoid stacking a second
        /// question on top of the first.</summary>
        private bool IsConfirming => pendingConfirm != null;

        /// <summary>
        /// The confirmation, as an ALERT: a title, the question, the consequence under it, and the
        /// two answers. Centred, 280px wide, and no taller than what is in it.
        ///
        /// Everything is measured before the window opens, because the height has to be known to
        /// place it - an alert that is centred has to know how tall it is before it can know where
        /// its top edge goes. Wrapping is what makes the height vary, so the measuring is done at
        /// the width the text will actually be given, never at the window's.
        /// </summary>
        private void DrawConfirmSheet()
        {
            var request = pendingConfirm;
            if (request == null)
            {
                CloseSheet();
                return;
            }

            float alertW = MathF.Min(AlertWidth, screenSize.X - 40f);
            float textW = alertW - AlertPad * 2f;

            float titleH;
            using (UiHeadingFont.Push())
                titleH = ImGui.GetTextLineHeight();

            // Measured with the SAME wrapper that draws it. CalcTextSize's wrapped height and a
            // hand-rolled word wrap agree until they do not - one trailing space, one word that
            // fits by a fraction of a pixel - and the frame that disagrees is a line drawn past the
            // bottom edge of a window sized for one fewer.
            float lineH = ImGui.GetTextLineHeightWithSpacing();
            float questionH = WrapToWidth(request.Question, textW).Count * lineH;
            float detailH = string.IsNullOrEmpty(request.Detail)
                ? 0f
                : 8f + WrapToWidth(request.Detail!, textW).Count * lineH;

            float want = AlertPad + titleH + 10f + questionH + detailH + 18f + AlertFooterHeight;

            if (!BeginAlert("Confirm", want))
                return;

            try
            {
                float width = ImGui.GetWindowWidth();
                var dl = ImGui.GetWindowDrawList();
                Vector2 origin = ImGui.GetWindowPos();

                // CENTRED, WHICH AN ALERT IS AND A SHEET IS NOT. A sheet is a surface you are
                // working on and its text starts at the left margin like everything else. An alert
                // is a single statement with nothing else on screen competing for the middle.
                using (UiHeadingFont.Push())
                {
                    Vector2 ts = ImGui.CalcTextSize(request.Title);
                    dl.AddText(new Vector2(origin.X + (width - ts.X) * 0.5f, origin.Y + AlertPad),
                        ImGui.ColorConvertFloat4ToU32(Ink), request.Title);
                }

                // No PushTextWrapPos: the text is already broken into lines that fit, and each one
                // is drawn from its own centred x. ImGui's wrap point is measured from the window,
                // not from the cursor, so a centred line starting further right would be wrapped a
                // second time against a limit it was never sized for.
                ImGui.SetCursorPos(new Vector2(AlertPad, AlertPad + titleH + 10f));
                DrawCentredWrapped(request.Question, textW, TextPrimary);

                if (!string.IsNullOrEmpty(request.Detail))
                {
                    ImGui.Dummy(new Vector2(0, 2));
                    ImGui.SetCursorPosX(AlertPad);
                    DrawCentredWrapped(request.Detail!, textW, TextMuted);
                }

                // The footer, measured back from the bottom edge for the same reason the sheets'
                // is: flowed, it collects an ItemSpacing after every element and lands short.
                float h = ImGui.GetWindowHeight();
                ImGui.SetCursorPos(new Vector2(0, h - AlertFooterHeight));
                DrawRuleHair();

                float bw = (width - AlertPad * 2f - AlertButtonGap) * 0.5f;
                var size = new Vector2(bw, AlertButtonHeight);
                ImGui.SetCursorPos(new Vector2(AlertPad, h - AlertButtonHeight - 14f));

                // The destructive answer is drawn by hand rather than through the shared footer,
                // which only knows about the accent primary. Red is not a variant of the accent -
                // it is the one colour in the plugin that means "this removes something".
                bool confirmed = request.Danger
                    ? DrawDangerButton($"{request.ConfirmLabel}##ConfirmYes", size)
                    : DrawPrimaryButton($"{request.ConfirmLabel}##ConfirmYes", size);

                ImGui.SameLine(0, AlertButtonGap);
                bool cancelled = DrawSecondaryButton($"{request.CancelLabel}##ConfirmNo", size);

                if (confirmed)
                {
                    // Cleared before invoking, so an action that opens another sheet isn't
                    // immediately closed again by this one.
                    pendingConfirm = null;
                    CloseSheet();
                    request.OnConfirm?.Invoke();
                }
                else if (cancelled)
                {
                    DismissConfirmDialog();
                }
            }
            finally
            {
                EndSheet();
            }
        }

        /// <summary>
        /// A run of wrapped text with every line centred on the column.
        ///
        /// ImGui wraps and left-aligns; there is no centred wrap. So the wrapping is done here -
        /// CalcTextSize with a wrap width reports the height, but not where it broke - by walking
        /// words and measuring, which is the only way to know what each line is in order to centre
        /// it.
        /// </summary>
        private static void DrawCentredWrapped(string text, float width, Vector4 colour)
        {
            float startX = ImGui.GetCursorPosX();

            foreach (string line in WrapToWidth(text, width))
            {
                float lineW = ImGui.CalcTextSize(line).X;
                ImGui.SetCursorPosX(startX + MathF.Max(0f, (width - lineW) * 0.5f));
                ImGui.TextColored(colour, line);
            }
        }

        /// <summary>Breaks text into the lines it would wrap to at a given width, on spaces.</summary>
        private static List<string> WrapToWidth(string text, float width)
        {
            var lines = new List<string>();
            if (string.IsNullOrEmpty(text))
                return lines;

            var current = new StringBuilder();

            foreach (string word in text.Split(' '))
            {
                string candidate = current.Length == 0 ? word : $"{current} {word}";

                if (current.Length > 0 && ImGui.CalcTextSize(candidate).X > width)
                {
                    lines.Add(current.ToString());
                    current.Clear();
                    current.Append(word);
                    continue;
                }

                current.Clear();
                current.Append(candidate);
            }

            if (current.Length > 0)
                lines.Add(current.ToString());

            return lines;
        }

        // ══════════════════════════════════════════════════════════
        //  FREE-STANDING DIALOG
        // ══════════════════════════════════════════════════════════

        /// <summary>
        /// A centred dialog that is NOT a sheet, for the one case that cannot be one: something the
        /// plugin needs to say when the main window is closed.
        ///
        /// A sheet lives inside the screen, so there is nowhere to put one when there is no screen.
        /// The vote nudge is the only thing left here - everything a player opened themselves is a
        /// sheet, because they were already looking at the window when they asked for it.
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

            ImGui.PushStyleColor(ImGuiCol.WindowBg, Panel);
            ImGui.PushStyleColor(ImGuiCol.Border, BorderDefault);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 1.0f);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, Radius.Sheet);
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

        /// <summary>The red button used for anything that destroys something. One implementation,
        /// shared with the card's Leave and Disband, so the same act never looks like two different
        /// buttons in two places.</summary>
        private static bool DrawDangerButton(string label, Vector2 size)
            => DrawDangerFilledButton(label, size);
    }
}
