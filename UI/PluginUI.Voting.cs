#if PFP_RATINGS
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;

namespace PfPresets
{
    /// <summary>
    /// The row every list of people is built from - the party list, Recent players, the listing
    /// panel - and the small pieces that go in it: the hover wash, the kebab and its menu, a
    /// character's name and world, and a job icon.
    ///
    /// Row heights are measured from the font and frame metrics rather than hard-coded, because
    /// hard-coded ones clip the moment anyone runs a different UI scale - which is exactly how the
    /// first version of this went wrong.
    /// </summary>
    public partial class PluginUI
    {

        private const float RowPadX = 10f;
        private const float RowPadY = 7f;

        // ══════════════════════════════════════════════════════════
        //  LAYOUT PRIMITIVES
        // ══════════════════════════════════════════════════════════
        //
        // Every clipped control in this UI so far came from `GetWindowWidth() - someNumber`.
        // Inside a child window GetWindowWidth() includes the padding, so that expression is
        // already wrong by 2x the pad, and it takes no account of what is actually being drawn.
        // These two exist so no caller has to guess again.

        // Fit now lives in PluginUI.Theme.cs, which compiles in both builds - the recruitment
        // card's comment wrapping calls it and that code is not behind PFP_RATINGS.

        // ══════════════════════════════════════════════════════════
        //  HOVER ROW
        // ══════════════════════════════════════════════════════════

        /// <summary>Per-row hover brightness, eased each frame. ImGui has no transitions, so the
        /// fade is done by hand - a row that lights instantly reads as a flicker when the cursor
        /// crosses a list.</summary>
        private readonly Dictionary<string, float> rowGlow = new();

        /// <summary>Height every list row uses, so the party list and Recent players match.</summary>
        /// <summary>
        /// How tall a person's row is.
        ///
        /// Measured against the face the NAME is set in, not against whatever font happened to be
        /// pushed when this was called. That is also why it is no longer static: the row was sized
        /// off the ambient line height plus ten, which came out around thirty pixels - a name, a
        /// job icon, a prog point and a menu button crammed into a strip barely taller than the
        /// text in it. A person is the thing this list is made of and should be able to be pointed
        /// at.
        /// </summary>
        private float HoverRowHeight()
        {
            float line;
            using (UiRowNameFont.Push())
                line = ImGui.GetTextLineHeight();

            return MathF.Max(line, 22f) + 18f;
        }

        /// <summary>
        /// The gap between one hover row and the next.
        ///
        /// It was a single pixel, which was right while the rows were invisible until hovered - a
        /// list of names wants to read as a list, not as a stack of tiles. Now that the party rows
        /// carry a fill of their own (see restColor) a one-pixel gap welds them into one block with
        /// hairlines through it, so there is room for the boxes to be separate boxes.
        ///
        /// Named because it is arithmetic in four places, one of which is a height RESERVATION -
        /// see PartySectionHeight. A gap changed in the drawing and not in the measure is a party
        /// list that runs out of the bottom of its card.
        /// </summary>
        internal const float HoverRowGap = 4f;

        /// <summary>
        /// How far a hover row's text sits in from the surface it is drawn on.
        ///
        /// Not a number anybody chose - it falls out of the row's own geometry (6px to the row's
        /// left edge, then 8px of padding inside it) - but anything drawn ALONGSIDE the rows has to
        /// match it or it sits at a different left edge from every name under it. Named so the odd
        /// lines that share a card with a list can line up with the list.
        /// </summary>
        internal const float HoverRowTextInset = 14f;

        /// <summary>
        /// A borderless list row that washes faintly on hover, the way a Windows list behaves.
        /// Both the party list and Recent players go through this, so they can't drift apart in
        /// height, padding or feel.
        ///
        /// <paramref name="body"/> is given the row's right edge in screen coordinates and lays
        /// itself out inward from there.
        /// </summary>
        /// <param name="restColor">A fill the row carries all the time, not only under the cursor.
        /// Rows on the ground do not want one - the card they sit on is already a surface, and a
        /// second one inside it is noise. Rows that ARE the content, like the party list, do: each
        /// person reads as their own object rather than as a line in a block of text.</param>
        private void DrawHoverRow(string id, Action<float> body, Vector4? washColor = null,
            bool forceLit = false, float? width = null, float? originX = null,
            Action? contextMenu = null, float? height = null, Vector4? restColor = null,
            float? separatorInset = null, Vector4? borderColor = null, bool dashedBorder = false,
            float? rounding = null)
        {
            // Callers that sit beside a list of vote rows pass that list's height, so the two
            // sections of the same column don't read as two different densities.
            float rowH = height ?? HoverRowHeight();
            float rowW = width ?? (ImGui.GetContentRegionAvail().X - 12f);

            // X comes from the caller when it has one.
            //
            // Chaining it through the cursor doesn't work: the Dummy that closes each row returns
            // the cursor to the *child's* line-start X, not to where the row began. A first row
            // positioned explicitly inside a card therefore sat several pixels right of every row
            // after it - which is exactly what it looked like.
            Vector2 cursor = ImGui.GetCursorScreenPos();
            Vector2 origin = new Vector2(originX ?? cursor.X, cursor.Y);

            // INSET ON BOTH SIDES. It was inset six pixels on the left and ran the full width from
            // there, so the box finished six pixels past where the surface it sits on stops - and
            // the card clips to its own padding, which sliced the rounded right-hand corners off
            // every row in the party list. The left edge was fine, which is what made it read as
            // "the right side has no border radius" rather than as an overflow.
            const float inset = 6f;
            var min = new Vector2(origin.X + inset, origin.Y);
            var max = new Vector2(origin.X + rowW - inset, origin.Y + rowH);

            bool hovered = IsMouseOver(min, max);

            float radius = rounding ?? Radius.Small;
            if (restColor.HasValue)
                ImGui.GetWindowDrawList().AddRectFilled(min, max,
                    ImGui.ColorConvertFloat4ToU32(restColor.Value), radius);
            if (borderColor.HasValue)
            {
                if (dashedBorder)
                    DrawDashedRoundedRect(ImGui.GetWindowDrawList(), min, max, borderColor.Value, radius);
                else
                    ImGui.GetWindowDrawList().AddRect(min, max, ImGui.ColorConvertFloat4ToU32(borderColor.Value),
                        radius, ImDrawFlags.None, 1f);
            }

            rowGlow.TryGetValue(id, out float glow);
            float target = hovered || forceLit ? 1f : 0f;
            float step = ImGui.GetIO().DeltaTime * 8f;
            glow += Math.Clamp(target - glow, -step, step);
            rowGlow[id] = glow;

            if (glow > 0.01f)
            {
                // Raised well past what it was. 5% white was a legible highlight over the old
                // #211f1d card; over a #1c1c1e card sitting on true black it is about one value
                // step and reads as nothing at all - hovering a name looked like hovering nothing.
                var baseCol = washColor ?? new Vector4(1f, 1f, 1f, 1f);
                var wash = new Vector4(baseCol.X, baseCol.Y, baseCol.Z,
                    (washColor.HasValue ? 0.20f : 0.10f) * glow);
                ImGui.GetWindowDrawList().AddRectFilled(min, max,
                    ImGui.ColorConvertFloat4ToU32(wash), 5f);
            }

            // THE HAIRLINE THAT MAKES A COLUMN OF ROWS A LIST.
            //
            // Drawn from where the row's words begin rather than from its edge, and out to the
            // trailing edge - the same rule the settings rows follow, so the met list, the party
            // list and a settings page are visibly the same object. A caller that ends its group
            // passes no inset and gets none; every list passes the width of whatever leads its
            // rows, which is a job icon in all of them.
            if (separatorInset.HasValue)
                ImGui.GetWindowDrawList().AddRectFilled(
                    new Vector2(min.X + separatorInset.Value, max.Y),
                    new Vector2(max.X, max.Y + 1f),
                    ImGui.ColorConvertFloat4ToU32(RuleHair));

            ImGui.PushID(id);
            try
            {
                // Cleared before the body so a kebab press from the previous row can't carry over
                // and open this one's menu.
                rowMenuRequested = false;

                ImGui.SetCursorScreenPos(new Vector2(min.X + 8f, origin.Y + (rowH - 22f) * 0.5f));
                ImGui.BeginGroup();
                try
                {
                    body(max.X - 8f);
                }
                finally
                {
                    ImGui.EndGroup();
                }

                // Inside the PushID, so the popup's id is scoped to this row without every caller
                // having to invent a unique name for it.
                if (contextMenu != null)
                    DrawRowContextMenu(hovered, contextMenu, rowMenuRequested);
            }
            finally
            {
                ImGui.PopID();
            }

            // Dummy first, cursor second - not the other way round.
            //
            // Dummy advances the cursor by its own size PLUS ItemSpacing.Y. Placing the cursor and
            // then calling Dummy therefore added ~4px per row that nothing had measured, which
            // compounded down the list until the last row sat under whatever came next.
            ImGui.Dummy(new Vector2(0, 0));
            ImGui.SetCursorScreenPos(new Vector2(origin.X, origin.Y + rowH + HoverRowGap));
        }

        /// <summary>A rounded rectangle outlined in dashes all the way round - the dashboard
        /// mockup's empty seat. The outline is walked as one path, corners included, and dashed by
        /// distance along it, so a short row's sides and its rounded corners are dashed like the
        /// long edges rather than drawn solid.</summary>
        private static void DrawDashedRoundedRect(ImDrawListPtr dl, Vector2 min, Vector2 max, Vector4 colour, float radius)
        {
            uint col = ImGui.ColorConvertFloat4ToU32(colour);
            const float dash = 4f, space = 3f;

            min += new Vector2(0.5f);
            max -= new Vector2(0.5f);
            radius = MathF.Min(radius, MathF.Min(max.X - min.X, max.Y - min.Y) * 0.5f);

            // The outline as a closed polyline: four arcs joined by the straight edges between them.
            var pts = new System.Collections.Generic.List<Vector2>(80);
            void Arc(Vector2 c, float from)
            {
                const int steps = 10;
                for (int i = 0; i <= steps; i++)
                {
                    float t = from + MathF.PI * 0.5f * i / steps;
                    pts.Add(c + new Vector2(MathF.Cos(t), MathF.Sin(t)) * radius);
                }
            }
            Arc(new Vector2(min.X + radius, min.Y + radius), MathF.PI);
            Arc(new Vector2(max.X - radius, min.Y + radius), MathF.PI * 1.5f);
            Arc(new Vector2(max.X - radius, max.Y - radius), 0f);
            Arc(new Vector2(min.X + radius, max.Y - radius), MathF.PI * 0.5f);
            pts.Add(pts[0]);

            // Walk it, drawing while "on" and skipping while "off", carrying the phase across
            // segments so the pattern runs unbroken round the corners.
            bool on = true;
            float left = dash;
            for (int i = 0; i + 1 < pts.Count; i++)
            {
                Vector2 a = pts[i], b = pts[i + 1];
                float len = Vector2.Distance(a, b);
                float pos = 0f;
                while (pos < len)
                {
                    float step = MathF.Min(left, len - pos);
                    Vector2 from = Vector2.Lerp(a, b, pos / len);
                    Vector2 to = Vector2.Lerp(a, b, (pos + step) / len);
                    if (on)
                        dl.AddLine(from, to, col, 1f);
                    pos += step;
                    left -= step;
                    if (left <= 0.001f)
                    {
                        on = !on;
                        left = on ? dash : space;
                    }
                }
            }
        }

        /// <summary>
        /// Right-click menu for a hover row.
        ///
        /// Deliberately the same chrome as the preset kebab menu in the main window - 6px window
        /// padding, two leading spaces on every label, separators between groups - because a menu
        /// that opens from a list row should not look like a different plugin's menu.
        ///
        /// <paramref name="hovered"/> comes from the row's own rect test rather than
        /// IsItemHovered: the row is a hand-drawn rect, not an ImGui item.
        /// </summary>
        private static void DrawRowContextMenu(bool hovered, Action items, bool opened = false)
        {
            if (opened || (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Right)))
                ImGui.OpenPopup("rowctx");

            PushIosMenuStyle();
            if (ImGui.BeginPopup("rowctx"))
            {
                items();
                ImGui.EndPopup();
            }
            PopIosMenuStyle();
        }

        /// <summary>
        /// Set by <see cref="DrawRowKebab"/> and read by the row that owns it.
        ///
        /// A field rather than a return value because the button is drawn deep inside the row's
        /// body callback, and the popup it opens is handled by DrawHoverRow afterwards. Both run
        /// inside the same PushID on the same frame, one after the other, so there is nothing for
        /// this to race with.
        /// </summary>
        private bool rowMenuRequested;

        /// <summary>
        /// The three-dash button at the end of a player row, opening the same menu as a right-click.
        ///
        /// A menu rather than the row of buttons this replaced. Report and Kick were the only two
        /// actions that fitted, which meant every new one had to either displace a column or not
        /// exist; and on a row already carrying a job icon, a name, a world, a rating chip and a
        /// prog point, two more competing click targets is where a list stops being readable.
        /// Right-click still works and always did - the button is there because nothing on screen
        /// said so.
        /// </summary>
        private bool DrawRowKebab(float rightEdge, float rowHeight, string tooltip)
        {
            const float w = 22f;
            float h = rowHeight;

            Vector2 pos = new Vector2(rightEdge - w, ImGui.GetCursorScreenPos().Y);
            ImGui.SetCursorScreenPos(pos);

            // Transparent until hovered: at rest this is punctuation, not a control competing with
            // the name beside it.
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0, 0, 0, 0));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, BorderHover);
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, BorderDefault);
            ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, Radius.Control);

            bool clicked = ImGui.Button("##rowkebab", new Vector2(w, h));

            ImGui.PopStyleVar();
            ImGui.PopStyleColor(3);

            bool hot = ImGui.IsItemHovered();
            DrawGlyphCentered(FontAwesomeIcon.EllipsisV, pos, new Vector2(pos.X + w, pos.Y + h),
                hot ? TextPrimary : TextMuted);

            if (hot && !string.IsNullOrEmpty(tooltip))
                PaddedTooltip(tooltip);

            if (clicked)
                rowMenuRequested = true;

            return clicked;
        }

        /// <summary>Name plus world, clipped to whatever room is left before the given right edge.
        /// Returns nothing - it draws and hovers for the full text if it had to shorten.</summary>
        private void DrawRowIdentity(uint jobId, string name, string world, float leftEdge, float rightEdge)
        {
            // The icon is sized off the name's own line height, not the window's, so the two stay
            // proportionate when the name face changes size.
            float iconSize;
            using (UiRowNameFont.Push())
                iconSize = ImGui.GetTextLineHeight() + 10f;

            DrawJobIconInline(jobId, iconSize);
            ImGui.SameLine(0, 9);

            string shownName = DisplayName(name);
            string label = string.IsNullOrEmpty(world) ? shownName : $"{shownName}  @{world}";
            float room = rightEdge - (leftEdge + iconSize + 9f) - 8f;

            using (UiRowNameFont.Push())
            {
                ImGui.AlignTextToFramePadding();
                string shown = Fit(label, room);
                ImGui.TextColored(Ink, shown);
                if (shown != label && ImGui.IsItemHovered())
                    PaddedTooltip(label);
            }
        }

        // ══════════════════════════════════════════════════════════
        //  JOB ICON
        // ══════════════════════════════════════════════════════════

        /// <summary>
        /// A job's game icon, drawn inline at the cursor, falling back to the abbreviation only if
        /// the icon can't be loaded. The game's own icons read far faster than three letters.
        ///
        /// Named -Inline to keep it distinct from the job selector's DrawJobIcon, which takes a
        /// JobInfo and is an interactive toggle rather than a passive icon.
        /// </summary>
        /// <param name="remembered">The job is the last one we saw them on rather than one the
        /// game is reporting now. Says so on the hover, because an icon that looks live and isn't
        /// is the one way this can mislead.</param>
        private void DrawJobIconInline(uint jobId, float size, bool offline = false,
            bool remembered = false)
        {
            var job = JobData.FindById(jobId);

            if (jobId > 0 && TryGetIconHandle(IconJobBase + jobId, out var handle))
            {
                Vector2 pos = ImGui.GetCursorScreenPos();

                // Offline members keep their icon but lose most of its colour, so the row still
                // reads as "this person, absent" rather than as an empty slot.
                var tint = offline ? new Vector4(1f, 1f, 1f, 0.32f) : Vector4.One;
                ImGui.Image(handle, new Vector2(size, size), Vector2.Zero, Vector2.One, tint);

                if (offline)
                    DrawOfflineMark(pos, size);

                if (job != null && ImGui.IsItemHovered())
                {
                    PaddedTooltip(job.Name
                        + (remembered ? "\nLast job we saw them on" : string.Empty)
                        + (offline ? "\nOffline or disconnected" : string.Empty));
                }
                return;
            }

            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(offline || job == null ? TextMuted : GetRoleColor(RoleOf(job)),
                job?.Abbreviation ?? "?");
        }

        /// <summary>A small unplugged badge in the icon's corner. Colour alone wouldn't survive a
        /// glance, and greying a job icon can read as "unknown job" rather than "not here".</summary>
        private void DrawOfflineMark(Vector2 iconTopLeft, float iconSize)
        {
            var dl = ImGui.GetWindowDrawList();
            float r = iconSize * 0.34f;
            var centre = new Vector2(iconTopLeft.X + iconSize - r * 0.7f,
                                     iconTopLeft.Y + iconSize - r * 0.7f);

            dl.AddCircleFilled(centre, r, ImGui.ColorConvertFloat4ToU32(BgOuter));
            dl.AddCircleFilled(centre, r - 1.5f, ImGui.ColorConvertFloat4ToU32(AccentRed));

            // A slash, rather than a glyph - it stays legible at 7px where an icon wouldn't.
            float s = (r - 1.5f) * 0.55f;
            dl.AddLine(new Vector2(centre.X - s, centre.Y + s), new Vector2(centre.X + s, centre.Y - s),
                ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 1f, 0.95f)), 1.6f);
        }

        private static RoleType RoleOf(JobInfo job) => job.Category switch
        {
            JobCategory.Tank => RoleType.Tank,
            JobCategory.PureHealer or JobCategory.BarrierHealer => RoleType.Healer,
            _ => RoleType.MeleeDPS,
        };

        // ══════════════════════════════════════════════════════════
        //  RATING ROW
        // ══════════════════════════════════════════════════════════

        // ══════════════════════════════════════════════════════════
        //  SCORE DISPLAY
        // ══════════════════════════════════════════════════════════

        private static string Ago(DateTime utc)
        {
            var span = DateTime.UtcNow - utc;
            if (span.TotalMinutes < 1) return "now";
            if (span.TotalMinutes < 60) return $"{span.TotalMinutes:0}m";
            if (span.TotalHours < 24) return $"{span.TotalHours:0}h";
            return $"{span.TotalDays:0}d";
        }
    }
}
#endif
