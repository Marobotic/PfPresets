#if PFP_RATINGS
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.ManagedFontAtlas;

namespace PfPresets
{
    /// <summary>
    /// The Feedback tab: report a bug, ask for a feature, ask a question, or say what you think.
    ///
    /// Drawn to the author's iPadOS mockup (modern_ios_feedback_form_ipados_style.html, dark mode):
    /// an inset-grouped form in a centred column - the category row that opens a sheet, the message card
    /// with its counter in the heading, the reply card with its switch, and a full-width Send -
    /// then a toast when it lands. The colours are the mockup's; the sizes are its proportions at
    /// the plugin's own scale. The window chrome and the light/dark button in the mockup are
    /// the plugin's own header here.
    ///
    /// It goes to the author's Discord through the server - the webhook URL never ships in the
    /// plugin - and the server stores it first, so a Discord outage delays a message rather than
    /// losing it.
    /// </summary>
    public partial class PluginUI
    {
        private static readonly (string Label, string Prompt, FontAwesomeIcon Icon, Vector4 Tint)[] FeedbackKinds =
        {
            ("Report a bug", "What happened, and what did you expect to happen?", FontAwesomeIcon.Bug, ColorFromHex("#ef4444")),
            ("Feature request", "What would you like the plugin to do?", FontAwesomeIcon.Lightbulb, ColorFromHex("#f59e0b")),
            ("General question", "What would you like to know?", FontAwesomeIcon.QuestionCircle, ColorFromHex("#3b82f6")),
            ("Share your thoughts", "How do you feel about the plugin?", FontAwesomeIcon.Heart, ColorFromHex("#ec4899")),
        };

        private const int MaxFeedback = 2000;
        private const int MaxFeedbackContact = 100;

        // ── The mockup's layout at the plugin's own scale ─────────
        //
        // The mockup is a web page at iPad sizes: 16px body text, 64px rows, a 56px button. The
        // plugin's scale is 13px body text, 44px rows and 36px buttons, so every measurement is
        // the mockup's at that scale rather than copied pixel for pixel - the same proportions,
        // not the same numbers.
        private const float FbFormWidth = 480f;
        private const float FbSectionGap = 18f;
        private const float FbHeadingGap = 6f;
        private const float FbCardRadius = Radius.Card;
        private const float FbRowHeight = 40f;
        private const float FbTopicIcon = 26f;
        private const float FbTextareaLines = 5f;
        private const float FbButtonHeight = 38f;
        private const float FbSheetWidth = 340f;
        private const float FbSheetPad = 14f;
        private const float FbOptionHeight = 40f;
        private const float FbOptionIcon = 28f;
        private const float FbSwitchW = 36f, FbSwitchH = 20f, FbKnob = 16f;
        private const float FbGlyph = 12f;

        private static readonly Vector4 FbGreen = ColorFromHex("#34c759");
        private static readonly Vector4 FbRed = ColorFromHex("#ef4444");
        private static readonly Vector4 FbToast = new(0.173f, 0.173f, 0.18f, 0.9f);   // #2C2C2E/90

        private const string FbSheetId = "##FeedbackTopicSheet";

        private int feedbackKind;
        private string feedbackText = string.Empty;
        private string feedbackContact = string.Empty;
        private bool feedbackIncludeName = true;
        private bool feedbackSending;
        private volatile bool feedbackJustSent;
        private volatile string feedbackStatus = string.Empty;

        private float fbSwitchT = 1f;
        private DateTime fbSheetOpenedAt;
        private DateTime? fbToastAt;

        private void DrawFeedbackTab()
        {
            // Applied here, on the UI thread: the send finishes on a task.
            if (feedbackJustSent)
            {
                feedbackJustSent = false;
                feedbackText = string.Empty;
                feedbackContact = string.Empty;
                fbToastAt = DateTime.UtcNow;
            }

            ImGui.SetCursorPosX(Space.Gutter);
            ImGui.BeginChild("FeedbackBody",
                new Vector2(ImGui.GetWindowWidth() - Space.Gutter * 2f, 0), false);
            try
            {
                Vector2 bodyMin = ImGui.GetWindowPos();
                Vector2 bodySize = ImGui.GetWindowSize();

                float avail = ImGui.GetContentRegionAvail().X;
                float width = MathF.Min(avail, FbFormWidth);
                float left = ImGui.GetCursorScreenPos().X + (avail - width) * 0.5f;

                ImGui.Dummy(new Vector2(0, FbSectionGap));
                DrawFbForm(left, width);
                ImGui.Dummy(new Vector2(0, FbSectionGap));

                DrawFbSheet(bodyMin, bodySize);
                DrawFbToast(bodyMin, bodySize);
            }
            finally
            {
                ImGui.EndChild();
            }
        }

        private void DrawFbForm(float left, float width)
        {
            var dl = ImGui.GetWindowDrawList();

            // Description banner: centred, slate-400, px-4.
            using (UiBodyFont.Push())
                FbCentredText(dl, left + FbPad, width - FbPad * 2f,
                    "Found a bug, have an idea, or need a hand? It goes straight to the plugin author.", FbSlate400);

            // 1 · What's it about
            ImGui.Dummy(new Vector2(0, FbSectionGap - 6f)); // mb-2 under the banner, then the form
            FbHeading(dl, left, width, "WHAT'S IT ABOUT", null);
            DrawFbTopicRow(dl, left, width);

            // 2 · Your message
            ImGui.Dummy(new Vector2(0, FbSectionGap));
            FbHeading(dl, left, width, "YOUR MESSAGE", $"{feedbackText.Length} / {MaxFeedback}");
            DrawFbMessage(dl, left, width);

            // 3 · How to reply
            ImGui.Dummy(new Vector2(0, FbSectionGap));
            FbHeading(dl, left, width, "HOW TO REPLY", null);
            DrawFbReply(dl, left, width);

            ImGui.Dummy(new Vector2(0, FbHeadingGap + 2f)); // space-y-2, pt-1
            using (UiHelpFont.Push())
                FbWrappedText(dl, left + FbPad, width - FbPad * 2f, feedbackIncludeName
                    ? "Your character name lets the author look you up and answer you in game."
                    : "Sent without your name. Leave a Discord name above if you'd like an answer.",
                    FbSlate400);

            // Send: pt-4 inside the section gap.
            ImGui.Dummy(new Vector2(0, FbSectionGap + 6f));
            DrawFbSendButton(dl, left, width);

            if (!string.IsNullOrEmpty(feedbackStatus))
            {
                ImGui.Dummy(new Vector2(0, 8f));
                using (UiHelpFont.Push())
                    FbCentredText(dl, left + FbPad, width - FbPad * 2f, feedbackStatus, AccentYellow);
            }
        }

        // ── Section pieces ────────────────────────────────────────

        /// <summary>text-xs uppercase tracking-wider slate-400, px-4; an optional counter on the right.</summary>
        private void FbHeading(ImDrawListPtr dl, float left, float width, string label, string? counter)
        {
            Vector2 p = new(left + FbPad, ImGui.GetCursorScreenPos().Y);
            float h;
            using (UiHelpFont.Push())
            {
                h = ImGui.GetTextLineHeight();
                DrawTrackedCaps(dl, p, label, FbSlate400);
            }

            if (counter != null)
            {
                using (UiHelpFont.Push())
                {
                    Vector2 cs = ImGui.CalcTextSize(counter);
                    var colour = feedbackText.Length >= MaxFeedback - 100 ? FbRed : FbSlate500;
                    dl.AddText(new Vector2(left + width - FbPad - cs.X, p.Y + (h - cs.Y) * 0.5f),
                        ImGui.ColorConvertFloat4ToU32(colour), counter);
                }
            }

            ImGui.Dummy(new Vector2(width, h));
            ImGui.Dummy(new Vector2(0, FbHeadingGap - ImGui.GetStyle().ItemSpacing.Y));
        }

        private static void FbCardBg(ImDrawListPtr dl, Vector2 min, Vector2 max)
        {
            dl.AddRectFilled(min, max, ImGui.ColorConvertFloat4ToU32(FbCard), FbCardRadius);
            dl.AddRect(min, max, ImGui.ColorConvertFloat4ToU32(FbBorder), FbCardRadius, ImDrawFlags.None, 1f);
        }

        /// <summary>The category row: tinted icon tile, the name, and a chevron. Opens the sheet.</summary>
        private void DrawFbTopicRow(ImDrawListPtr dl, float left, float width)
        {
            var kind = FeedbackKinds[feedbackKind];
            float rowH = ListRowHeight;
            Vector2 min = new(left, ImGui.GetCursorScreenPos().Y);
            Vector2 max = min + new Vector2(width, rowH);

            FbCardBg(dl, min, max);

            ImGui.SetCursorScreenPos(min);
            if (ImGui.InvisibleButton("##FeedbackTopicRow", new Vector2(width, rowH)))
            {
                fbSheetOpenedAt = DateTime.UtcNow;
                ImGui.OpenPopup(FbSheetId);
            }

            if (ImGui.IsItemHovered())
            {
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                // hover:bg-white/[0.03], active:bg-white/[0.06]
                float a = ImGui.IsItemActive() ? 0.06f : 0.03f;
                dl.AddRectFilled(min, max, ImGui.ColorConvertFloat4ToU32(new Vector4(1, 1, 1, a)), FbCardRadius);
            }

            float cy = min.Y + rowH * 0.5f;
            var tile = new Vector2(min.X + FbPad, cy - FbTopicIcon * 0.5f);
            dl.AddRectFilled(tile, tile + new Vector2(FbTopicIcon), // rounded-lg, tint/20
                ImGui.ColorConvertFloat4ToU32(kind.Tint with { W = 0.2f }), Radius.Tile);
            DrawGlyphAtOn(dl, kind.Icon, tile, FbTopicIcon, kind.Tint, UiIconSmall);

            using (UiBodyFont.Push())
            {
                float lh = ImGui.GetTextLineHeight();
                dl.AddText(new Vector2(tile.X + FbTopicIcon + 10f, cy - lh * 0.5f),
                    ImGui.ColorConvertFloat4ToU32(FbWhite), kind.Label);
            }

            DrawGlyphAtOn(dl, FontAwesomeIcon.ChevronRight,
                new Vector2(max.X - FbPad - FbGlyph, cy - FbGlyph * 0.5f), FbGlyph, FbSlate500, UiIconSmall);

            ImGui.SetCursorScreenPos(new Vector2(left, max.Y));
            ImGui.Dummy(new Vector2(width, 0));
        }

        /// <summary>The message card: p-4, a borderless five-row box, the prompt as its placeholder,
        /// and the blue focus ring while typing.</summary>
        private void DrawFbMessage(ImDrawListPtr dl, float left, float width)
        {
            float boxH;
            using (UiBodyFont.Push())
                boxH = ImGui.GetTextLineHeight() * FbTextareaLines;
            Vector2 min = new(left, ImGui.GetCursorScreenPos().Y);
            Vector2 max = min + new Vector2(width, FbPad * 2f + boxH);
            FbCardBg(dl, min, max);

            var inputMin = min + new Vector2(FbPad, FbPad);
            ImGui.SetCursorScreenPos(inputMin);

            bool active;
            using (UiBodyFont.Push())
            {
                PushFbBareInput();
                ImGui.InputTextMultiline("##FeedbackText", ref feedbackText, MaxFeedback,
                    new Vector2(width - FbPad * 2f, boxH), FeedbackWordWrap);
                PopFbBareInput();
                active = ImGui.IsItemActive();

                if (feedbackText.Length == 0)
                    dl.AddText(inputMin, ImGui.ColorConvertFloat4ToU32(FbSlate500), FeedbackKinds[feedbackKind].Prompt);
            }

            // focus-within:ring-2 ring-ios-blue
            if (active)
                dl.AddRect(min - new Vector2(1f), max + new Vector2(1f),
                    ImGui.ColorConvertFloat4ToU32(FbBlue), FbCardRadius + 1f, ImDrawFlags.None, 2f);

            ImGui.SetCursorScreenPos(new Vector2(left, max.Y));
            ImGui.Dummy(new Vector2(width, 0));
        }

        /// <summary>The reply card: "Reply to" with a right-aligned field, a divider, and the name
        /// switch.</summary>
        private void DrawFbReply(ImDrawListPtr dl, float left, float width)
        {
            const float rowH = FbRowHeight;

            Vector2 min = new(left, ImGui.GetCursorScreenPos().Y);
            Vector2 max = min + new Vector2(width, rowH * 2f + 1f);
            FbCardBg(dl, min, max);

            // Row 1: Reply to.
            float cy = min.Y + rowH * 0.5f;
            using (UiBodyFont.Push())
            {
                float lh = ImGui.GetTextLineHeight();
                const string label = "Reply to";
                dl.AddText(new Vector2(min.X + FbPad, cy - lh * 0.5f), ImGui.ColorConvertFloat4ToU32(FbWhite), label);
                float labelRight = min.X + FbPad + ImGui.CalcTextSize(label).X + FbPad; // pr-4

                // text-right: the field is exactly as wide as what it shows, anchored on the right.
                const string hint = "Discord name (optional)";
                float right = max.X - FbPad;
                float shown = ImGui.CalcTextSize(feedbackContact.Length > 0 ? feedbackContact : hint).X + 4f;
                float fieldW = Math.Clamp(shown, 24f, MathF.Max(24f, right - labelRight));

                ImGui.SetCursorScreenPos(new Vector2(right - fieldW, cy - lh * 0.5f));
                PushFbBareInput();
                ImGui.SetNextItemWidth(fieldW);
                ImGui.InputTextWithHint("##FeedbackContact", hint, ref feedbackContact, MaxFeedbackContact);
                PopFbBareInput();
            }

            // divide-y
            float divY = min.Y + rowH;
            dl.AddRectFilled(new Vector2(min.X, divY), new Vector2(max.X, divY + 1f),
                ImGui.ColorConvertFloat4ToU32(FbSeparator));

            // Row 2: the switch, the whole row a target.
            Vector2 r2 = new(min.X, divY + 1f);
            ImGui.SetCursorScreenPos(r2);
            if (ImGui.InvisibleButton("##FeedbackNameRow", new Vector2(width, rowH)))
                feedbackIncludeName = !feedbackIncludeName;
            if (ImGui.IsItemHovered())
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);

            float cy2 = r2.Y + rowH * 0.5f;
            using (UiBodyFont.Push())
            {
                float lh = ImGui.GetTextLineHeight();
                dl.AddText(new Vector2(min.X + FbPad, cy2 - lh * 0.5f), ImGui.ColorConvertFloat4ToU32(FbWhite),
                    Fit("Include my character name", width - FbPad * 3f - FbSwitchW));
            }

            DrawFbSwitch(dl, new Vector2(max.X - FbPad - FbSwitchW, cy2 - FbSwitchH * 0.5f), feedbackIncludeName, ref fbSwitchT);

            ImGui.SetCursorScreenPos(new Vector2(left, max.Y));
            ImGui.Dummy(new Vector2(width, 0));
        }

        /// <summary>The iOS switch: a pill track, a white knob sliding across with a little overshoot.</summary>
        private static void DrawFbSwitch(ImDrawListPtr dl, Vector2 min, bool on, ref float fbSwitchT)
        {
            float target = on ? 1f : 0f;
            float step = ImGui.GetIO().DeltaTime / 0.25f;
            fbSwitchT = fbSwitchT < target ? MathF.Min(target, fbSwitchT + step) : MathF.Max(target, fbSwitchT - step);

            // cubic-bezier(0.34, 1.56, 0.64, 1) - an ease-out-back, mirrored when turning off.
            static float Back(float x) => 1f + 2.70158f * MathF.Pow(x - 1f, 3f) + 1.70158f * MathF.Pow(x - 1f, 2f);
            float eased = on ? Back(fbSwitchT) : 1f - Back(1f - fbSwitchT);

            Vector4 track = Vector4.Lerp(FbNeutral700, FbBlue, Math.Clamp(fbSwitchT, 0f, 1f));
            dl.AddRectFilled(min, min + new Vector2(FbSwitchW, FbSwitchH),
                ImGui.ColorConvertFloat4ToU32(track), FbSwitchH * 0.5f);

            const float inset = (FbSwitchH - FbKnob) * 0.5f;
            float travel = FbSwitchW - FbKnob - inset * 2f;
            var knobCentre = min + new Vector2(inset + FbKnob * 0.5f + travel * eased, FbSwitchH * 0.5f);
            dl.AddCircleFilled(knobCentre + new Vector2(0, 1.5f), FbKnob * 0.5f + 0.5f,
                ImGui.ColorConvertFloat4ToU32(new Vector4(0, 0, 0, 0.25f)), 24); // shadow-md
            dl.AddCircleFilled(knobCentre, FbKnob * 0.5f, ImGui.ColorConvertFloat4ToU32(FbWhite), 24);
        }

        /// <summary>Full width, rounded-2xl, ios-blue with its glow; "Send" and a paper plane.</summary>
        private void DrawFbSendButton(ImDrawListPtr dl, float left, float width)
        {
            bool disabled = feedbackSending || string.IsNullOrWhiteSpace(feedbackText);
            Vector2 min = new(left, ImGui.GetCursorScreenPos().Y);
            Vector2 size = new(width, FbButtonHeight);

            ImGui.SetCursorScreenPos(min);
            bool clicked = ImGui.InvisibleButton("##FeedbackSend", size) && !disabled;
            bool hot = ImGui.IsItemHovered() && !disabled;
            bool held = ImGui.IsItemActive() && !disabled;
            if (hot)
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);

            float alpha = disabled ? 0.5f : 1f;

            // active:scale-[0.98]
            Vector2 bMin = min, bMax = min + size;
            if (held)
            {
                Vector2 inset = size * 0.01f;
                bMin += inset;
                bMax -= inset;
            }

            // shadow-lg shadow-blue-500/25
            for (int i = 1; i <= 4; i++)
            {
                float grow = i * 1.5f;
                dl.AddRectFilled(bMin + new Vector2(-grow, 3f - grow * 0.5f), bMax + new Vector2(grow, 3f + grow),
                    ImGui.ColorConvertFloat4ToU32(new Vector4(0.23f, 0.51f, 0.96f, 0.05f * alpha)), Radius.Control + grow);
            }

            Vector4 fill = hot ? FbBlueHover : FbBlue;
            dl.AddRectFilled(bMin, bMax, ImGui.ColorConvertFloat4ToU32(fill with { W = alpha }), Radius.Control);

            string label = feedbackSending ? "Sending..." : "Send";
            Vector2 ts;
            using (UiBodyFont.Push())
                ts = ImGui.CalcTextSize(label);

            const float icon = FbGlyph, gap = 7f; // w-4, space-x-2
            Vector2 centre = (bMin + bMax) * 0.5f;
            float x = centre.X - (ts.X + gap + icon) * 0.5f;
            using (UiBodyFont.Push())
                dl.AddText(new Vector2(x, centre.Y - ts.Y * 0.5f),
                    ImGui.ColorConvertFloat4ToU32(FbWhite with { W = alpha }), label);
            DrawGlyphAtOn(dl, FontAwesomeIcon.PaperPlane, new Vector2(x + ts.X + gap, centre.Y - icon * 0.5f),
                icon, FbWhite with { W = alpha }, UiIconSmall);

            if (clicked)
                SendFeedback();
        }

        // ── The category sheet ────────────────────────────────────

        /// <summary>
        /// "Select Category": a sheet centred over the tab on a dimmed backdrop, a close button,
        /// and the options with their tinted tiles and a blue check. Fades in.
        ///
        /// A plain popup, not a modal: ImGui closes it on a click outside by itself, which is the
        /// mockup's tap-the-scrim. The backdrop is drawn by the sheet, full-screen, before its own
        /// card - so the popup window itself is transparent and the card is drawn here too, over
        /// the dimming rather than under it. An option is chosen on the press, not the release, so
        /// nothing can close the sheet between the two and lose the choice.
        /// </summary>
        private void DrawFbSheet(Vector2 bodyMin, Vector2 bodySize)
        {
            if (!ImGui.IsPopupOpen(FbSheetId))
                return;

            float t = Math.Clamp((float)(DateTime.UtcNow - fbSheetOpenedAt).TotalSeconds / 0.25f, 0f, 1f);
            float ease = 1f - MathF.Pow(1f - t, 3f);

            float sheetW = MathF.Min(FbSheetWidth, bodySize.X - 24f);
            ImGui.SetNextWindowSize(new Vector2(sheetW, 0f));
            ImGui.SetNextWindowPos(new Vector2(bodyMin.X + bodySize.X * 0.5f,
                bodyMin.Y + bodySize.Y * 0.5f + (1f - ease) * 10f), ImGuiCond.Always, new Vector2(0.5f, 0.5f));

            var clear = new Vector4(0, 0, 0, 0);
            ImGui.PushStyleColor(ImGuiCol.PopupBg, clear);
            ImGui.PushStyleColor(ImGuiCol.Border, clear);
            ImGui.PushStyleVar(ImGuiStyleVar.PopupBorderSize, 0f);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(FbSheetPad, FbSheetPad));
            ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, Vector2.Zero);

            bool open = ImGui.BeginPopup(FbSheetId,
                ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoSavedSettings);

            ImGui.PopStyleVar(3);
            ImGui.PopStyleColor(2);

            if (!open)
                return;

            var dl = ImGui.GetWindowDrawList();
            Vector2 winMin = ImGui.GetWindowPos();
            Vector2 winMax = winMin + ImGui.GetWindowSize();
            uint A(Vector4 c) => ImGui.ColorConvertFloat4ToU32(c with { W = c.W * ease });

            // bg-black/60 over the whole screen, then the sheet's own card over it. Both outside the
            // window's clip: ImGui clips a window's drawing about half its padding in from each side,
            // which cut the card's rounded corners and border off at the edges.
            dl.PushClipRectFullScreen();
            var vp = ImGui.GetMainViewport();
            dl.AddRectFilled(vp.Pos, vp.Pos + vp.Size, A(new Vector4(0, 0, 0, 0.6f)));
            dl.AddRectFilled(winMin, winMax, A(FbCard), Radius.Sheet);
            dl.AddRect(winMin, winMax, A(new Vector4(1, 1, 1, 0.2f)), Radius.Sheet, ImDrawFlags.None, 1f);
            dl.PopClipRect();

            float w = ImGui.GetContentRegionAvail().X;
            Vector2 top = ImGui.GetCursorScreenPos();

            // Header: title and close button, pb-3, divider.
            const float close = 24f;
            using (UiTitleFont.Push())
            {
                float lh = ImGui.GetTextLineHeight();
                dl.AddText(new Vector2(top.X, top.Y + (close - lh) * 0.5f), A(FbWhite), "Select Category");
            }

            var closeMin = new Vector2(top.X + w - close, top.Y);
            ImGui.SetCursorScreenPos(closeMin);
            ImGui.InvisibleButton("##FbSheetClose", new Vector2(close));
            bool closeHot = ImGui.IsItemHovered();
            if (ImGui.IsItemClicked())
                ImGui.CloseCurrentPopup();
            dl.AddCircleFilled(closeMin + new Vector2(close * 0.5f), close * 0.5f,
                A(closeHot ? FbNeutral700 : FbNeutral800), 24);
            DrawGlyphAtOn(dl, FontAwesomeIcon.Times, closeMin, close, FbSlate500 with { W = ease }, UiIconSmall);

            float divY = top.Y + close + 10f;
            dl.AddRectFilled(new Vector2(top.X, divY), new Vector2(top.X + w, divY + 1f), A(FbSeparator));

            // The options.
            float y = divY + 1f + 10f;
            for (int i = 0; i < FeedbackKinds.Length; i++)
            {
                var kind = FeedbackKinds[i];
                var oMin = new Vector2(top.X, y);
                ImGui.SetCursorScreenPos(oMin);
                ImGui.InvisibleButton($"##FbOption{i}", new Vector2(w, FbOptionHeight));
                bool hot = ImGui.IsItemHovered();
                if (ImGui.IsItemClicked())
                {
                    feedbackKind = i;
                    ImGui.CloseCurrentPopup();
                }

                if (hot)
                {
                    ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                    dl.AddRectFilled(oMin, oMin + new Vector2(w, FbOptionHeight), A(FbNeutral800), Radius.Control);
                }

                float cy = oMin.Y + FbOptionHeight * 0.5f;
                var tile = new Vector2(oMin.X + 6f, cy - FbOptionIcon * 0.5f);
                dl.AddRectFilled(tile, tile + new Vector2(FbOptionIcon), A(kind.Tint with { W = 0.15f }), Radius.Small);
                DrawGlyphAtOn(dl, kind.Icon, tile, FbOptionIcon, kind.Tint with { W = ease }, UiIconRow);

                using (UiBodyFont.Push())
                {
                    float lh = ImGui.GetTextLineHeight();
                    dl.AddText(new Vector2(tile.X + FbOptionIcon + 10f, cy - lh * 0.5f), A(FbWhite), kind.Label);
                }

                if (i == feedbackKind)
                    DrawGlyphAtOn(dl, FontAwesomeIcon.Check, new Vector2(oMin.X + w - 8f - FbGlyph, cy - FbGlyph * 0.5f),
                        FbGlyph, FbBlue with { W = ease }, UiIconSmall);

                y += FbOptionHeight + 4f;
            }

            ImGui.SetCursorScreenPos(new Vector2(top.X, y - 4f));
            ImGui.Dummy(new Vector2(w, 0f));
            ImGui.EndPopup();
        }

        // ── The toast ─────────────────────────────────────────────

        /// <summary>"Message sent successfully!" - a pill at the top of the tab for three seconds,
        /// springing in and fading out.</summary>
        private void DrawFbToast(Vector2 bodyMin, Vector2 bodySize)
        {
            if (fbToastAt is not { } at)
                return;

            float age = (float)(DateTime.UtcNow - at).TotalSeconds;
            if (age > 3.3f)
            {
                fbToastAt = null;
                return;
            }

            // In: 0.35s, cubic-bezier(0.175, 0.885, 0.32, 1.275). Out: 0.3s fade after 3s.
            float tin = Math.Clamp(age / 0.35f, 0f, 1f);
            float spring = 1f + 2.70158f * MathF.Pow(tin - 1f, 3f) + 1.70158f * MathF.Pow(tin - 1f, 2f);
            float alpha = tin * (age > 3f ? 1f - (age - 3f) / 0.3f : 1f);
            float offsetY = (1f - spring) * -20f;
            float scale = 0.95f + 0.05f * spring;

            const string text = "Message sent successfully!";
            Vector2 ts;
            using (UiBodyFont.Push())
                ts = ImGui.CalcTextSize(text);

            const float dot = 18f, gap = 9f, padX = 14f, padY = 8f, padRight = 6f;
            Vector2 size = new Vector2(padX + dot + gap + ts.X + padRight + padX, padY * 2f + dot) * scale;
            Vector2 min = new(bodyMin.X + (bodySize.X - size.X) * 0.5f, bodyMin.Y + 16f + offsetY);
            Vector2 max = min + size;

            var dl = ImGui.GetForegroundDrawList();
            uint A(Vector4 c) => ImGui.ColorConvertFloat4ToU32(c with { W = c.W * alpha });

            for (int i = 1; i <= 4; i++) // shadow-2xl
                dl.AddRectFilled(min - new Vector2(i * 2f, i * 1f), max + new Vector2(i * 2f, i * 3f),
                    A(new Vector4(0, 0, 0, 0.08f)), size.Y * 0.5f + i * 2f);
            dl.AddRectFilled(min, max, A(FbToast), size.Y * 0.5f);
            dl.AddRect(min, max, A(new Vector4(1, 1, 1, 0.2f)), size.Y * 0.5f, ImDrawFlags.None, 1f);

            var dotCentre = new Vector2(min.X + (padX + dot * 0.5f) * scale, (min.Y + max.Y) * 0.5f);
            dl.AddCircleFilled(dotCentre, dot * 0.5f * scale, A(FbGreen), 32);
            DrawGlyphAtOn(dl, FontAwesomeIcon.Check, dotCentre - new Vector2(FbGlyph * 0.5f), FbGlyph, FbWhite with { W = alpha }, UiIconSmall);

            using (UiBodyFont.Push())
                dl.AddText(new Vector2(min.X + (padX + dot + gap) * scale, (min.Y + max.Y) * 0.5f - ts.Y * 0.5f),
                    A(FbWhite), text);
        }

        // ── Small helpers ─────────────────────────────────────────

        /// <summary>Word-wrapped lines of <paramref name="text"/> in the current font.</summary>
        private static List<string> FbWrap(string text, float width)
        {
            var lines = new List<string>();
            string line = string.Empty;
            foreach (string word in text.Split(' '))
            {
                string candidate = line.Length == 0 ? word : line + " " + word;
                if (line.Length > 0 && ImGui.CalcTextSize(candidate).X > width)
                {
                    lines.Add(line);
                    line = word;
                }
                else
                {
                    line = candidate;
                }
            }
            if (line.Length > 0)
                lines.Add(line);
            return lines;
        }

        /// <summary>Wrapped and centred, each line on its own, in the current font.</summary>
        private static void FbCentredText(ImDrawListPtr dl, float left, float width, string text, Vector4 colour)
        {
            float y = ImGui.GetCursorScreenPos().Y;
            float lh = ImGui.GetTextLineHeight() * 1.3f; // leading-relaxed
            foreach (string line in FbWrap(text, width))
            {
                float w = ImGui.CalcTextSize(line).X;
                dl.AddText(new Vector2(left + (width - w) * 0.5f, y), ImGui.ColorConvertFloat4ToU32(colour), line);
                y += lh;
            }
            ImGui.SetCursorScreenPos(new Vector2(ImGui.GetCursorScreenPos().X, y));
            ImGui.Dummy(Vector2.Zero);
        }

        /// <summary>Wrapped and left-aligned, in the current font.</summary>
        private static void FbWrappedText(ImDrawListPtr dl, float left, float width, string text, Vector4 colour)
        {
            float y = ImGui.GetCursorScreenPos().Y;
            float lh = ImGui.GetTextLineHeight() * 1.3f;
            foreach (string line in FbWrap(text, width))
            {
                dl.AddText(new Vector2(left, y), ImGui.ColorConvertFloat4ToU32(colour), line);
                y += lh;
            }
            ImGui.SetCursorScreenPos(new Vector2(ImGui.GetCursorScreenPos().X, y));
            ImGui.Dummy(Vector2.Zero);
        }

        private void SendFeedback()
        {
            if (Ratings == null || feedbackSending)
                return;

            feedbackSending = true;
            feedbackStatus = string.Empty;

            // Read here, on the UI thread, before the task runs.
            int kind = feedbackKind;
            string text = feedbackText.Trim();
            string contact = feedbackContact.Trim();
            var from = feedbackIncludeName ? LocalIdentity?.Invoke() : null;

            _ = Task.Run(async () =>
            {
                try
                {
                    var result = await Ratings.SubmitFeedbackAsync(kind, text, contact, from).ConfigureAwait(false);
                    if (result.Ok)
                    {
                        feedbackJustSent = true;
                        return;
                    }

                    feedbackStatus = result.Outcome switch
                    {
                        ReportOutcome.RateLimited => result.RetryAfter is { } wait
                            ? $"That's 5 messages today, which is the limit. You can send another in {Until(DateTime.UtcNow + wait)}."
                            : "That's 5 messages today, which is the limit. Try again tomorrow.",
                        ReportOutcome.Offline => "Couldn't reach the server. Your message is still here; try again in a moment.",
                        _ => "Couldn't send that. Your message is still here; try again in a moment.",
                    };
                }
                catch (Exception)
                {
                    feedbackStatus = "Couldn't send that. Your message is still here; try again in a moment.";
                }
                finally
                {
                    feedbackSending = false;
                }
            });
        }
    }
}
#endif
