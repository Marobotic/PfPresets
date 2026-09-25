using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.ManagedFontAtlas;

namespace PfPresets
{
    /// <summary>
    /// The floating overlays' frame - the coordination window and the watched-party windows - in
    /// the same iOS style as the Feedback tab.
    ///
    /// NO IMGUI TITLE BAR. The windows used ImGui's own, and with the sheet's 18px corner rounding
    /// its title text and close cross sat inside the curve and were cut off. The header is drawn
    /// here instead: a title and a grey subtitle on the left, round buttons on the right, all
    /// inside the window's padding. The window still drags by any empty part of it.
    /// </summary>
    public partial class PluginUI
    {
        private const float OverlayPad = 14f;
        private const float OverlayCircle = 24f;
        private const float OverlayInsetPad = 10f;
        private const float OverlayButtonHeight = 32f;

        private static readonly Vector4 OverlayInsetBg = ColorFromHex("#2c2c2e");
        private static readonly Vector4 OverlayButtonBg = ColorFromHex("#3a3a3c");
        private static readonly Vector4 OverlayButtonHover = ColorFromHex("#48484a");

        /// <summary>Begins an overlay window with the shared frame. Always pair with
        /// <see cref="EndOverlayWindow"/>, whatever this returns.</summary>
        private bool BeginOverlayWindow(string id, float width, bool locked)
        {
            ImGui.SetNextWindowSize(new Vector2(width, 0), ImGuiCond.Always);

            ImGui.PushStyleColor(ImGuiCol.WindowBg, FbCard);
            ImGui.PushStyleColor(ImGuiCol.Border, new Vector4(1f, 1f, 1f, 0.08f));
            ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 1f);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, Radius.Sheet);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(OverlayPad, OverlayPad));

            var flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoResize
                | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoFocusOnAppearing
                | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;
            if (locked)
                flags |= ImGuiWindowFlags.NoMove;

            return ImGui.Begin(id, flags);
        }

        private static void EndOverlayWindow()
        {
            ImGui.End();
            ImGui.PopStyleVar(3);
            ImGui.PopStyleColor(2);
        }

        /// <summary>
        /// The header: title over a grey subtitle, and round buttons at the right - a lock when
        /// <paramref name="locked"/> is given, and a close cross. Returns which was pressed.
        /// </summary>
        private (bool Close, bool Lock) DrawOverlayHeader(string id, string title, string? subtitle, bool? locked,
            bool closable = true)
        {
            var dl = ImGui.GetWindowDrawList();
            float width = ImGui.GetContentRegionAvail().X;
            Vector2 min = ImGui.GetCursorScreenPos();

            int buttons = (locked.HasValue ? 1 : 0) + (closable ? 1 : 0);
            float buttonsW = buttons == 0 ? 0f : buttons * OverlayCircle + (buttons - 1) * 6f;
            float textRoom = width - buttonsW - 10f;

            float titleH, subH = 0f;
            using (UiTitleFont.Push())
            {
                titleH = ImGui.GetTextLineHeight();
                dl.AddText(min, ImGui.ColorConvertFloat4ToU32(Ink), Fit(title, textRoom));
            }

            if (!string.IsNullOrEmpty(subtitle))
            {
                using (UiHelpFont.Push())
                {
                    subH = ImGui.GetTextLineHeight() + 2f;
                    dl.AddText(new Vector2(min.X, min.Y + titleH + 2f), ImGui.ColorConvertFloat4ToU32(FbSlate400),
                        Fit(subtitle!, textRoom));
                }
            }

            float height = MathF.Max(OverlayCircle, titleH + subH);
            float x = min.X + width;
            bool close = false;
            if (closable)
            {
                x -= OverlayCircle;
                close = OverlayCircleButton($"##{id}close", new Vector2(x, min.Y), FontAwesomeIcon.Times, "Close");
                x -= 6f;
            }

            bool lockPressed = false;
            if (locked is { } isLocked)
            {
                x -= OverlayCircle;
                lockPressed = OverlayCircleButton($"##{id}lock", new Vector2(x, min.Y),
                    isLocked ? FontAwesomeIcon.Lock : FontAwesomeIcon.LockOpen,
                    isLocked ? "Locked in place - click to unlock" : "Click to lock this window in place",
                    isLocked ? FbBlue : (Vector4?)null);
            }

            ImGui.SetCursorScreenPos(min);
            ImGui.Dummy(new Vector2(width, height));
            ImGui.Dummy(new Vector2(0, 6f));
            return (close, lockPressed);
        }

        /// <summary>A 24px round grey button with a glyph, like the sheet's close button.</summary>
        private bool OverlayCircleButton(string id, Vector2 min, FontAwesomeIcon icon, string tip, Vector4? tint = null)
        {
            var dl = ImGui.GetWindowDrawList();
            ImGui.SetCursorScreenPos(min);
            bool clicked = ImGui.InvisibleButton(id, new Vector2(OverlayCircle));
            bool hot = ImGui.IsItemHovered();
            dl.AddCircleFilled(min + new Vector2(OverlayCircle * 0.5f), OverlayCircle * 0.5f,
                ImGui.ColorConvertFloat4ToU32(hot ? FbNeutral700 : OverlayInsetBg), 24);
            DrawGlyphAtOn(dl, icon, min, OverlayCircle, tint ?? FbSlate400, UiIconSmall);
            if (hot)
            {
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                PaddedTooltip(tip);
            }
            return clicked;
        }

        /// <summary>A rounded inset panel, a step lighter than the window, around whatever the body
        /// draws. Measured after the body is laid out.</summary>
        private static void OverlayInset(Action<float> body)
        {
            var dl = ImGui.GetWindowDrawList();
            float width = ImGui.GetContentRegionAvail().X;
            Vector2 min = ImGui.GetCursorScreenPos();

            dl.ChannelsSplit(2);
            dl.ChannelsSetCurrent(1);
            ImGui.BeginGroup();
            try
            {
                ImGui.Dummy(new Vector2(width, OverlayInsetPad - ImGui.GetStyle().ItemSpacing.Y));
                ImGui.Indent(OverlayInsetPad);
                // Wrapped text stops at the panel's padding, not the window's edge.
                ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + width - OverlayInsetPad * 2f);
                body(width - OverlayInsetPad * 2f);
                ImGui.PopTextWrapPos();
                ImGui.Unindent(OverlayInsetPad);
                ImGui.Dummy(new Vector2(width, OverlayInsetPad - ImGui.GetStyle().ItemSpacing.Y));
            }
            finally
            {
                ImGui.EndGroup();
            }

            float height = ImGui.GetItemRectSize().Y;
            dl.ChannelsSetCurrent(0);
            dl.AddRectFilled(min, min + new Vector2(width, height), ImGui.ColorConvertFloat4ToU32(OverlayInsetBg),
                Radius.Card);
            dl.ChannelsMerge();
        }

        /// <summary>A small capsule: tinted background, the text in the tint.</summary>
        private void OverlayPill(string text, Vector4 tint)
            => ImGui.Dummy(DrawPillAt(ImGui.GetWindowDrawList(), ImGui.GetCursorScreenPos(), text, tint));

        /// <summary>The same capsule at a point, for placing beside other things. Returns its size.</summary>
        private Vector2 DrawPillAt(ImDrawListPtr dl, Vector2 min, string text, Vector4 tint)
        {
            using (UiHelpFont.Push())
            {
                var size = PillSize(text);
                dl.AddRectFilled(min, min + size, ImGui.ColorConvertFloat4ToU32(tint with { W = 0.16f }), size.Y * 0.5f);
                dl.AddText(min + new Vector2(8f, 3f), ImGui.ColorConvertFloat4ToU32(tint), text);
                return size;
            }
        }

        private Vector2 PillSize(string text)
        {
            using (UiHelpFont.Push())
            {
                Vector2 ts = ImGui.CalcTextSize(text);
                return new Vector2(ts.X + 16f, ts.Y + 6f);
            }
        }

        /// <summary>An iOS button: blue and filled for the primary action, grey otherwise.</summary>
        private bool OverlayButton(string label, string id, float width, bool primary, bool enabled = true)
        {
            var dl = ImGui.GetWindowDrawList();
            Vector2 min = ImGui.GetCursorScreenPos();
            var size = new Vector2(width, OverlayButtonHeight);

            bool clicked = ImGui.InvisibleButton(id, size) && enabled;
            bool hot = ImGui.IsItemHovered() && enabled;
            bool held = ImGui.IsItemActive() && enabled;
            if (hot)
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            float alpha = enabled ? 1f : 0.45f;

            Vector2 bMin = min, bMax = min + size;
            if (held)
            {
                bMin += size * 0.01f;
                bMax -= size * 0.01f;
            }

            Vector4 fill = primary ? (hot ? FbBlueHover : FbBlue) : (hot ? OverlayButtonHover : OverlayButtonBg);
            dl.AddRectFilled(bMin, bMax, ImGui.ColorConvertFloat4ToU32(fill with { W = fill.W * alpha }), Radius.Control);

            using (UiBodyFont.Push())
            {
                Vector2 ts = ImGui.CalcTextSize(label);
                Vector4 ink = primary ? FbWhite : Ink;
                dl.AddText((bMin + bMax) * 0.5f - ts * 0.5f, ImGui.ColorConvertFloat4ToU32(ink with { W = ink.W * alpha }), label);
            }

            return clicked;
        }

        /// <summary>Two buttons side by side across the width, primary on the left.</summary>
        private (bool First, bool Second) OverlayButtonPair(string first, string second, string id)
        {
            float w = ImGui.GetContentRegionAvail().X;
            float each = (w - 8f) * 0.5f;
            bool a = OverlayButton(first, $"##{id}a", each, true);
            ImGui.SameLine(0, 8f);
            bool b = OverlayButton(second, $"##{id}b", each, false);
            return (a, b);
        }

        // ── Sheet controls ────────────────────────────────────────

        /// <summary>
        /// A full iOS button with a glyph before its label: the accent with a soft glow when it is
        /// the primary action, the glass grey otherwise; a small press; dimmed when disabled.
        /// </summary>
        private bool DrawIosButton(string label, string id, FontAwesomeIcon icon, Vector2 size, bool primary,
            bool enabled = true)
        {
            var dl = ImGui.GetWindowDrawList();
            Vector2 min = ImGui.GetCursorScreenPos();
            bool clicked = ImGui.InvisibleButton(id, size) && enabled;
            bool hot = ImGui.IsItemHovered() && enabled;
            bool held = ImGui.IsItemActive() && enabled;
            if (hot)
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);

            float alpha = enabled ? 1f : 0.45f;
            Vector2 bMin = min, bMax = min + size;
            if (held)
            {
                bMin += size * 0.01f;
                bMax -= size * 0.01f;
            }

            if (primary)
            {
                for (int i = 1; i <= 3; i++)
                    dl.AddRectFilled(bMin + new Vector2(-i, 3f - i * 0.5f), bMax + new Vector2(i, 2f + i),
                        ImGui.ColorConvertFloat4ToU32(Accent with { W = 0.07f * alpha }), Radius.Card + i);
                dl.AddRectFilled(bMin, bMax, ImGui.ColorConvertFloat4ToU32((hot ? AccentHover : Accent) with { W = alpha }), Radius.Card);
            }
            else
            {
                dl.AddRectFilled(bMin, bMax, ImGui.ColorConvertFloat4ToU32((hot ? FbNeutral700 : FbNeutral800) with { W = alpha }), Radius.Card);
                dl.AddRect(bMin, bMax, ImGui.ColorConvertFloat4ToU32(new Vector4(1, 1, 1, 0.1f * alpha)), Radius.Card, ImDrawFlags.None, 1f);
            }

            Vector4 ink = primary || hot ? new Vector4(1, 1, 1, alpha) : PfSlate300 with { W = alpha };
            DrawIconLabelCentered(icon, label, bMin, bMax - bMin, ink, 1f, UiIconSmall);
            return clicked;
        }

        /// <summary>
        /// A text box on its own rounded field - the translucent grey of the search field, a faint
        /// border, the accent ring while it has focus - with the text wrapped to the box rather than
        /// running off to one side. Returns whether the text changed.
        /// </summary>
        private bool DrawIosTextBox(string id, ref string text, int capacity, Vector2 size, bool readOnly)
        {
            const float pad = 10f;
            var dl = ImGui.GetWindowDrawList();
            Vector2 min = ImGui.GetCursorScreenPos();
            var max = min + size;

            var fill = new Vector4(0.463f, 0.463f, 0.502f, 0.22f);
            dl.AddRectFilled(min, max, ImGui.ColorConvertFloat4ToU32(fill), Radius.Card);
            dl.AddRect(min, max, ImGui.ColorConvertFloat4ToU32(new Vector4(1, 1, 1, 0.05f)), Radius.Card, ImDrawFlags.None, 1f);

            ImGui.SetCursorScreenPos(min + new Vector2(pad));
            PushFbBareInput();
            var flags = FeedbackWordWrap | (readOnly ? ImGuiInputTextFlags.ReadOnly : ImGuiInputTextFlags.None);
            bool changed = ImGui.InputTextMultiline(id, ref text, capacity, size - new Vector2(pad * 2f), flags);
            PopFbBareInput();

            if (ImGui.IsItemActive())
                dl.AddRect(min - new Vector2(1f), max + new Vector2(1f), ImGui.ColorConvertFloat4ToU32(Accent),
                    Radius.Card + 1f, ImDrawFlags.None, 2f);

            ImGui.SetCursorScreenPos(new Vector2(min.X, max.Y));
            ImGui.Dummy(new Vector2(size.X, 0f));
            return changed;
        }

        /// <summary>A tinted note - the colour at 12% behind, 25% round it, the words in it.</summary>
        private void DrawIosNote(string text, Vector4 tint, float width)
        {
            MeasuredPanel($"note{text.GetHashCode()}", width, tint with { W = 0.12f }, tint with { W = 0.25f },
                Radius.Card, 10f, _ =>
            {
                using (UiHelpFont.Push())
                    ImGui.TextColored(tint, text);
            });
        }

        // ── Loading text ──────────────────────────────────────────

        /// <summary>
        /// Text with a soft highlight sweeping through it, left to right and round again - the
        /// "still loading" shimmer. Each character is lit by how near the sweep is to it, on a
        /// smooth falloff, so the light moves rather than steps. <paramref name="italic"/> slants
        /// it: there is no italic face loaded, so the glyphs' own vertices are sheared, which is
        /// what an oblique is.
        /// </summary>
        private static unsafe void DrawShimmerText(ImDrawListPtr dl, Vector2 pos, string text,
            Vector4 baseColour, Vector4 lightColour, bool italic = false)
        {
            const float period = 2.2f;   // one sweep, and a breath before the next
            const float band = 38f;      // how wide the light is, in pixels

            float width = ImGui.CalcTextSize(text).X;
            float lineH = ImGui.GetTextLineHeight();
            float t = (float)(ImGui.GetTime() % period) / period;
            float sweepX = pos.X - band + t * (width + band * 2f);

            int firstVertex = dl.VtxBuffer.Size;

            float x = pos.X;
            for (int i = 0; i < text.Length; i++)
            {
                string ch = text[i].ToString();
                float cw = ImGui.CalcTextSize(ch).X;
                float d = MathF.Abs(x + cw * 0.5f - sweepX) / band;
                float light = d >= 1f ? 0f : 0.5f + 0.5f * MathF.Cos(d * MathF.PI);
                dl.AddText(new Vector2(x, pos.Y), ImGui.ColorConvertFloat4ToU32(Vector4.Lerp(baseColour, lightColour, light)), ch);
                x += cw;
            }

            if (!italic)
                return;

            // Lean everything just drawn to the right, about twelve degrees, pivoting on the baseline.
            const float slant = 0.21f;
            float baseline = pos.Y + lineH * 0.8f;
            var verts = dl.VtxBuffer;
            var data = (ImDrawVert*)verts.Data;
            for (int i = firstVertex; i < verts.Size; i++)
                data[i].Pos.X += (baseline - data[i].Pos.Y) * slant;
        }

        // ── Measured panels ───────────────────────────────────────

        /// <summary>Each panel's height as last drawn, by key.</summary>
        private readonly System.Collections.Generic.Dictionary<string, float> panelHeights = new();

        /// <summary>
        /// A rounded panel around whatever the body draws, sized by what it drew last frame.
        ///
        /// For panels inside panels. <see cref="OverlayInset"/> measures with draw-list channels,
        /// and ImGui refuses to split channels twice at once - so a card with insets in it draws
        /// its backgrounds at the height the same panel came to last frame instead. The first
        /// frame a panel appears it has no background; nobody sees one frame.
        /// </summary>
        private void MeasuredPanel(string key, float width, Vector4 bg, Vector4? border, float radius, float pad,
            Action<float> body)
        {
            var dl = ImGui.GetWindowDrawList();
            Vector2 min = ImGui.GetCursorScreenPos();

            if (panelHeights.TryGetValue(key, out float h) && h > 0f)
            {
                dl.AddRectFilled(min, min + new Vector2(width, h), ImGui.ColorConvertFloat4ToU32(bg), radius);
                if (border is { } b)
                    dl.AddRect(min, min + new Vector2(width, h), ImGui.ColorConvertFloat4ToU32(b), radius, ImDrawFlags.None, 1f);
            }

            float gap = ImGui.GetStyle().ItemSpacing.Y;
            ImGui.BeginGroup();
            try
            {
                // No spacer at zero padding (a zero-height Dummy still costs a line gap), and no
                // Indent(0) - ImGui reads zero as "the default indent", not "none".
                if (pad > 0f)
                {
                    ImGui.Dummy(new Vector2(width, MathF.Max(0f, pad - gap)));
                    ImGui.Indent(pad);
                }
                ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + width - pad * 2f);
                body(width - pad * 2f);
                ImGui.PopTextWrapPos();
                if (pad > 0f)
                {
                    ImGui.Unindent(pad);
                    ImGui.Dummy(new Vector2(width, MathF.Max(0f, pad - gap)));
                }
            }
            finally
            {
                ImGui.EndGroup();
            }

            panelHeights[key] = ImGui.GetItemRectSize().Y;
        }

        // ── Settings rows ─────────────────────────────────────────

        /// <summary>Each switch's position through its slide, by id.</summary>
        private readonly System.Collections.Generic.Dictionary<string, float> switchAnim = new();

        /// <summary>Small caps over a card, inset to the card's text - the Feedback tab's section
        /// headings.</summary>
        private void DrawSectionCaption(string label, string? right = null)
        {
            var dl = ImGui.GetWindowDrawList();
            Vector2 p = ImGui.GetCursorScreenPos();
            float width = ImGui.GetContentRegionAvail().X;
            using (UiHelpFont.Push())
            {
                float h = ImGui.GetTextLineHeight();
                DrawTrackedCaps(dl, p + new Vector2(FbPad, 0f), label, FbSlate400);
                if (right != null)
                {
                    float rw = ImGui.CalcTextSize(right).X;
                    dl.AddText(new Vector2(p.X + width - FbPad - rw, p.Y), ImGui.ColorConvertFloat4ToU32(FbSlate500), right);
                }
                ImGui.Dummy(new Vector2(width, h));
            }
            ImGui.Dummy(new Vector2(0, MathF.Max(0f, 6f - ImGui.GetStyle().ItemSpacing.Y)));
        }

#if PFP_RATINGS
        /// <summary>One row of a list inside an inset: a job icon, the name, a quiet note at the
        /// right, and a hairline under it unless it is the last.</summary>
        private void OverlayListRow(uint job, string name, string note, Vector4 noteTint, float width, bool last)
        {
            const float rowH = 28f, icon = 18f;
            var dl = ImGui.GetWindowDrawList();
            Vector2 min = ImGui.GetCursorScreenPos();
            float cy = min.Y + rowH * 0.5f;

            ImGui.SetCursorScreenPos(new Vector2(min.X, cy - icon * 0.5f));
            if (job > 0)
                DrawJobIconInline(job, icon);
            else
                ImGui.Dummy(new Vector2(icon));

            float textX = min.X + icon + 8f;
            float noteW = 0f;
            using (UiHelpFont.Push())
            {
                if (note.Length > 0)
                {
                    Vector2 ns = ImGui.CalcTextSize(note);
                    noteW = ns.X + 8f;
                    dl.AddText(new Vector2(min.X + width - ns.X, cy - ns.Y * 0.5f),
                        ImGui.ColorConvertFloat4ToU32(noteTint), note);
                }
            }

            using (UiBodyFont.Push())
            {
                float lh = ImGui.GetTextLineHeight();
                dl.AddText(new Vector2(textX, cy - lh * 0.5f), ImGui.ColorConvertFloat4ToU32(Ink),
                    Fit(name, width - (textX - min.X) - noteW));
            }

            if (!last)
                dl.AddRectFilled(new Vector2(textX, min.Y + rowH - 0.5f), new Vector2(min.X + width, min.Y + rowH + 0.5f),
                    ImGui.ColorConvertFloat4ToU32(FbSeparator));

            ImGui.SetCursorScreenPos(min);
            ImGui.Dummy(new Vector2(width, rowH));
        }
#endif

        private static readonly Vector4 FbSlate400 = ColorFromHex("#94a3b8");

        private static readonly Vector4 FbSlate500 = ColorFromHex("#64748b");

        private static readonly Vector4 FbNeutral700 = ColorFromHex("#404040");

        private static readonly Vector4 FbNeutral800 = ColorFromHex("#262626");

        /// <summary>A FontAwesome glyph centred in a square, onto a given draw list.</summary>
        private void DrawGlyphAtOn(ImDrawListPtr dl, FontAwesomeIcon icon, Vector2 topLeft, float size,
            Vector4 colour, IFontHandle? font = null)
        {
            using ((font ?? pluginInterface.UiBuilder.IconFontHandle).Push())
            {
                string g = icon.ToIconString();
                Vector2 ts = ImGui.CalcTextSize(g);
                dl.AddText(topLeft + new Vector2((size - ts.X) * 0.5f, (size - ts.Y) * 0.5f),
                    ImGui.ColorConvertFloat4ToU32(colour), g);
            }
        }

        /// <summary>
        /// The iOS context menu's look for an ImGui popup: the elevated dark card, rounded, a faint
        /// border, roomy rows with a soft rounded highlight, hairline separators. Pushed round a
        /// BeginPopup, so every menu built from Selectables gets it without being rewritten.
        /// </summary>
        private static void PushIosMenuStyle()
        {
            ImGui.PushStyleColor(ImGuiCol.PopupBg, ColorFromHex("#2c2c2e") with { W = 0.98f });
            ImGui.PushStyleColor(ImGuiCol.Border, new Vector4(1, 1, 1, 0.1f));
            ImGui.PushStyleColor(ImGuiCol.Header, new Vector4(1, 1, 1, 0.08f));
            ImGui.PushStyleColor(ImGuiCol.HeaderHovered, new Vector4(1, 1, 1, 0.08f));
            ImGui.PushStyleColor(ImGuiCol.HeaderActive, new Vector4(1, 1, 1, 0.12f));
            ImGui.PushStyleColor(ImGuiCol.Separator, new Vector4(1, 1, 1, 0.08f));
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1, 1, 1, 1));
            ImGui.PushStyleVar(ImGuiStyleVar.PopupRounding, Radius.Card);
            ImGui.PushStyleVar(ImGuiStyleVar.PopupBorderSize, 1f);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(6, 6));
            ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(6, 2));
            ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, Radius.Small);
            ImGui.PushStyleVar(ImGuiStyleVar.SelectableTextAlign, new Vector2(0f, 0.5f));
        }

        private static void PopIosMenuStyle()
        {
            ImGui.PopStyleVar(6);
            ImGui.PopStyleColor(7);
        }

        /// <summary>
        /// One row of an iOS menu: a glyph and the label, 30px, a rounded highlight under the
        /// cursor; red for an action that destroys something. True when chosen - the popup closes.
        /// </summary>
        private bool IosMenuItem(string label, FontAwesomeIcon icon, bool destructive = false)
        {
            const float rowH = 30f, glyph = 12f;
            var dl = ImGui.GetWindowDrawList();
            Vector2 min = ImGui.GetCursorScreenPos();
            float w = MathF.Max(170f, ImGui.GetContentRegionAvail().X);

            bool chosen = ImGui.InvisibleButton($"##menu{label}", new Vector2(w, rowH));
            bool hot = ImGui.IsItemHovered();
            if (hot)
            {
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                dl.AddRectFilled(min, min + new Vector2(w, rowH),
                    ImGui.ColorConvertFloat4ToU32(destructive ? KoFi with { W = 0.15f } : new Vector4(1, 1, 1, 0.08f)), Radius.Small);
            }

            Vector4 ink = destructive ? KoFi : new Vector4(1, 1, 1, 1);
            DrawGlyphAtOn(dl, icon, new Vector2(min.X + 8f, min.Y + (rowH - glyph) * 0.5f), glyph,
                destructive ? KoFi : FbSlate400, UiIconSmall);
            using (UiBodyFont.Push())
            {
                float lh = ImGui.GetTextLineHeight();
                dl.AddText(new Vector2(min.X + 8f + glyph + 10f, min.Y + (rowH - lh) * 0.5f),
                    ImGui.ColorConvertFloat4ToU32(ink), label);
            }

            if (chosen)
                ImGui.CloseCurrentPopup();
            return chosen;
        }

        /// <summary>One choice in an iOS menu: its name, and a tick in the accent before the chosen
        /// one. True when picked - the popup closes.</summary>
        private bool IosMenuCheckItem(string label, bool selected)
        {
            const float rowH = 30f, check = 12f;
            var dl = ImGui.GetWindowDrawList();
            Vector2 min = ImGui.GetCursorScreenPos();
            float w = MathF.Max(190f, ImGui.GetContentRegionAvail().X);

            bool chosen = ImGui.InvisibleButton($"##menucheck{label}", new Vector2(w, rowH));
            bool hot = ImGui.IsItemHovered();
            if (hot)
            {
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                dl.AddRectFilled(min, min + new Vector2(w, rowH), ImGui.ColorConvertFloat4ToU32(new Vector4(1, 1, 1, 0.08f)), Radius.Small);
            }
            if (selected)
                DrawGlyphAtOn(dl, FontAwesomeIcon.Check, new Vector2(min.X + 8f, min.Y + (rowH - check) * 0.5f), check, Accent, UiIconSmall);
            using (UiBodyFont.Push())
            {
                float lh = ImGui.GetTextLineHeight();
                dl.AddText(new Vector2(min.X + 8f + check + 10f, min.Y + (rowH - lh) * 0.5f),
                    ImGui.ColorConvertFloat4ToU32(new Vector4(1, 1, 1, 1)), label);
            }
            if (chosen)
                ImGui.CloseCurrentPopup();
            return chosen;
        }

        /// <summary>The hairline between groups in an iOS menu.</summary>
        private static void IosMenuSeparator()
        {
            Vector2 p = ImGui.GetCursorScreenPos();
            float w = MathF.Max(170f, ImGui.GetContentRegionAvail().X);
            ImGui.GetWindowDrawList().AddRectFilled(p + new Vector2(0, 2f), p + new Vector2(w, 3f),
                ImGui.ColorConvertFloat4ToU32(new Vector4(1, 1, 1, 0.08f)));
            ImGui.Dummy(new Vector2(w, 5f));
        }

        private static readonly Vector4 PfSlate300 = ColorFromHex("#cbd5e1");

        /// <summary>
        /// The mockup's segmented picker: a dark rounded track, a blue rounded indicator that slides
        /// under the chosen segment, the chosen label white and semibold, the rest grey.
        /// </summary>
        private bool DrawIosSegmented(string id, string[] options, ref int value, float width)
        {
            const float pad = 3f, height = 30f;
            var dl = ImGui.GetWindowDrawList();
            Vector2 min = ImGui.GetCursorScreenPos();
            var max = min + new Vector2(width, height);
            int n = Math.Max(1, options.Length);
            float segW = (width - pad * 2f) / n;

            dl.AddRectFilled(min, max, ImGui.ColorConvertFloat4ToU32(FbCard), Radius.Card);
            dl.AddRect(min, max, ImGui.ColorConvertFloat4ToU32(FbBorder), Radius.Card, ImDrawFlags.None, 1f);

            if (value >= 0 && value < n)
            {
                float target = value;
                float at = segmentAnim.TryGetValue(id, out float v) ? v : target;
                at += (target - at) * MathF.Min(1f, ImGui.GetIO().DeltaTime * 14f);
                if (MathF.Abs(target - at) < 0.001f)
                    at = target;
                segmentAnim[id] = at;

                var iMin = new Vector2(min.X + pad + segW * at, min.Y + pad);
                var iMax = iMin + new Vector2(segW, height - pad * 2f);
                dl.AddRectFilled(iMin + new Vector2(0, 1.5f), iMax + new Vector2(0, 1.5f),
                    ImGui.ColorConvertFloat4ToU32(new Vector4(0, 0, 0, 0.35f)), Radius.Control - 1f); // shadow-segmented
                dl.AddRectFilled(iMin, iMax, ImGui.ColorConvertFloat4ToU32(FbBlue), Radius.Control - 1f);
            }

            bool changed = false;
            for (int i = 0; i < n; i++)
            {
                var sMin = new Vector2(min.X + pad + segW * i, min.Y + pad);
                ImGui.SetCursorScreenPos(sMin);
                if (ImGui.InvisibleButton($"##{id}seg{i}", new Vector2(segW, height - pad * 2f)) && i != value)
                {
                    value = i;
                    changed = true;
                }
                bool hot = ImGui.IsItemHovered();
                if (hot && i != value)
                    ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);

                bool chosen = i == value;
                using ((chosen ? UiSegmentFont : UiBodyFont).Push())
                {
                    string label = Fit(options[i], segW - 8f);
                    Vector2 ts = ImGui.CalcTextSize(label);
                    dl.AddText(new Vector2(sMin.X + (segW - ts.X) * 0.5f, min.Y + (height - ts.Y) * 0.5f),
                        ImGui.ColorConvertFloat4ToU32(chosen ? FbWhite : hot ? Ink : FbSlate400), label);
                }
            }

            ImGui.SetCursorScreenPos(min);
            ImGui.Dummy(new Vector2(width, height));
            return changed;
        }

        private const float FbPad = 12f;

        /// <summary>Wrap long lines in the message box, rather than scrolling sideways.</summary>
        private const ImGuiInputTextFlags FeedbackWordWrap = (ImGuiInputTextFlags)0x01000000;

        /// <summary>An input drawn straight onto its card: no frame, no padding.</summary>
        private static void PushFbBareInput()
        {
            var clear = new Vector4(0, 0, 0, 0);
            ImGui.PushStyleColor(ImGuiCol.FrameBg, clear);
            ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, clear);
            ImGui.PushStyleColor(ImGuiCol.FrameBgActive, clear);
            ImGui.PushStyleColor(ImGuiCol.TextDisabled, FbSlate500);
            ImGui.PushStyleColor(ImGuiCol.Text, FbWhite);
            ImGui.PushStyleColor(ImGuiCol.ScrollbarBg, clear);
            ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 0f);
            ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, Vector2.Zero);
        }

        private static void PopFbBareInput()
        {
            ImGui.PopStyleVar(2);
            ImGui.PopStyleColor(6);
        }

        private static readonly Vector4 FbCard = ColorFromHex("#1c1c1e");

        private static readonly Vector4 FbWhite = new(1f, 1f, 1f, 1f);

        // The mockups' blue is the player's accent, so these screens follow the theme. The accent
        // choices are iOS system tones, Blue among them at the mockups' own #0A84FF.
        private static Vector4 FbBlue => Accent;

        private static Vector4 FbBlueHover => AccentHover;

        private static readonly Vector4 FbSeparator = new(0.329f, 0.329f, 0.345f, 0.35f);

        private static readonly Vector4 FbBorder = new(1f, 1f, 1f, 0.05f);

        /// <summary>Where each segmented picker's blue indicator is, sliding toward the choice.</summary>
        private readonly Dictionary<string, float> segmentAnim = new();
    }
}
