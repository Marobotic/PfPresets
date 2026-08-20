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
    /// The shared rating row, used by the Ratings tab and the post-duty window.
    ///
    /// One click rates. There is no score to pick and no confirm step: after a duty you either
    /// would play with someone again or you wouldn't, and making that a two-stage form only added
    /// a button that could be missed.
    ///
    /// Row heights are measured from the font and frame metrics rather than hard-coded, because
    /// hard-coded ones clip the moment anyone runs a different UI scale - which is exactly how the
    /// first version of this went wrong.
    /// </summary>
    public partial class PluginUI
    {
        private sealed class RateState
        {
            public bool Sending;
            public bool Done;
            public VoteDirection Given;

            /// <summary>When the rating landed, which drives the confirm-then-collapse animation.
            /// Default means "not yet", so a row restored from disk doesn't animate on open.</summary>
            public DateTime DoneAtUtc = DateTime.MinValue;
        }

        /// <summary>How long the row holds, tinted, so the click reads as registered.</summary>
        private static readonly TimeSpan RowConfirmTime = TimeSpan.FromMilliseconds(200);

        /// <summary>How long it takes to slide off to the right.</summary>
        private static readonly TimeSpan RowSlideTime = TimeSpan.FromMilliseconds(140);

        /// <summary>How long the empty space then takes to close up.</summary>
        private static readonly TimeSpan RowCollapseTime = TimeSpan.FromMilliseconds(120);

        /// <summary>
        /// Where a rated row is in its exit, as two independent phases.
        ///
        /// Three beats, in order: it turns green or red and holds; it slides off to the right and
        /// fades; and only then does the gap it left close up. Separating the slide from the
        /// collapse is what makes it read as a sequence rather than a smear - the earlier version
        /// shrank and faded at once, which squashed the contents and, because the prompt window
        /// auto-resizes, made the window jitter for the whole duration.
        /// </summary>
        private static (float Slide, float Collapse) RowExitPhases(RateState state)
        {
            if (!state.Done || state.DoneAtUtc == DateTime.MinValue)
                return (0f, 0f);

            var elapsed = DateTime.UtcNow - state.DoneAtUtc;
            if (elapsed <= RowConfirmTime)
                return (0f, 0f);

            var afterConfirm = elapsed - RowConfirmTime;
            if (afterConfirm <= RowSlideTime)
                return ((float)(afterConfirm.TotalMilliseconds / RowSlideTime.TotalMilliseconds), 0f);

            var afterSlide = afterConfirm - RowSlideTime;
            float collapse = (float)(afterSlide.TotalMilliseconds / RowCollapseTime.TotalMilliseconds);
            return (1f, Math.Clamp(collapse, 0f, 1f));
        }

        /// <summary>1 once the row is gone entirely, for callers deciding when a list is done.</summary>
        private static float RowExitProgress(RateState state) => RowExitPhases(state).Collapse;


        private readonly Dictionary<string, RateState> rateStates = new();

        private RateState StateFor(CharacterIdentity who)
        {
            if (!rateStates.TryGetValue(who.Key, out var state))
            {
                state = new RateState();
                rateStates[who.Key] = state;
            }
            return state;
        }

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

        /// <summary>Moves the cursor so a block of the given width ends exactly at the content
        /// edge. Uses GetContentRegionMax, which is padding-aware, unlike GetWindowWidth.</summary>
        private static void RightAlign(float blockWidth)
        {
            float x = ImGui.GetContentRegionMax().X - blockWidth;
            ImGui.SameLine();
            ImGui.SetCursorPosX(Math.Max(ImGui.GetCursorPosX(), x));
        }

        // Fit now lives in PluginUI.Theme.cs, which compiles in both builds - the recruitment
        // card's comment wrapping calls it and that code is not behind PFP_RATINGS.

        /// <summary>Height of one row, derived from the current font and frame sizes so its
        /// contents always fit at any UI scale.</summary>
        private static float RateRowHeight(bool withSubline)
        {
            float line = ImGui.GetTextLineHeight();
            float button = ImGui.GetFrameHeight() + 4f;
            float spacing = ImGui.GetStyle().ItemSpacing.Y;

            float h = RowPadY * 2f + Math.Max(button, line);
            if (withSubline)
                h += spacing + line;
            return h;
        }

        // ══════════════════════════════════════════════════════════
        //  THE ARROW
        // ══════════════════════════════════════════════════════════

        /// <summary>
        /// Reddit's upvote arrow: an arrowhead over a stem, seven points, filled.
        ///
        /// Used only on the rating buttons. Anywhere a rating is being *reported* rather than
        /// cast uses <see cref="DrawTriangleInline"/> instead - the stem is what makes this read
        /// as something you press, and at indicator size it just looks blunt.
        ///
        /// Drawn on the draw list rather than as a font glyph or an image. It is geometry, so it
        /// stays crisp at any UI scale with no asset to ship, and it can be mirrored for the
        /// downvote by flipping Y about the centre.
        ///
        /// Reddit's own path rounds the tip and the barbs. ImGui has no rounded-corner polygon, so
        /// those corners are sharp here; at the ~15px this renders at, the difference isn't visible.
        /// Filled rather than outlined for the same reason - a 1px outline at this size reads as a
        /// smudge rather than an arrow.
        /// </summary>
        private static void DrawRedditArrow(ImDrawListPtr dl, Vector2 topLeft, float size, bool up, uint color)
        {
            // Proportions taken from the 20x20 source: tip at the top centre, barbs at 46% height,
            // stem a third of the width.
            static Vector2 P(Vector2 o, float s, float x, float y, bool flip)
                => new(o.X + x * s, o.Y + (flip ? 1f - y : y) * s);

            Span<Vector2> pts = stackalloc Vector2[7];
            pts[0] = P(topLeft, size, 0.50f, 0.03f, !up); // tip
            pts[1] = P(topLeft, size, 0.06f, 0.46f, !up); // left barb
            pts[2] = P(topLeft, size, 0.35f, 0.46f, !up);
            pts[3] = P(topLeft, size, 0.35f, 0.94f, !up); // stem bottom left
            pts[4] = P(topLeft, size, 0.65f, 0.94f, !up); // stem bottom right
            pts[5] = P(topLeft, size, 0.65f, 0.46f, !up);
            pts[6] = P(topLeft, size, 0.94f, 0.46f, !up); // right barb

            // The arrow is concave and this ImGui build has only AddConvexPolyFilled, so it goes
            // down as two convex pieces: the head and the stem.
            //
            // They are made to OVERLAP along the barb line by a fraction of a pixel. Abutting them
            // exactly looks right on paper but renders as a pale seam: each piece gets its own
            // antialiased edge, and two adjacent alpha ramps don't sum back to solid. That seam is
            // what reads as a grainy line across the arrow. The overlap puts one ramp on top of
            // the other instead.
            // Antialiased fill, forced on for this shape.
            //
            // AddConvexPolyFilled only feathers its edges when the draw list carries
            // AntiAliasedFill; without it the diagonals come out stair-stepped, which is what read
            // as "grainy" at this size. Dalamud doesn't guarantee the flag, so it's set here and
            // put back afterwards - the draw list is shared with every other plugin, so leaving it
            // changed would change how their shapes render too.
            var previousFlags = dl.Flags;
            dl.Flags |= ImDrawListFlags.AntiAliasedFill;

            float bleed = Math.Max(0.5f, size * 0.04f);
            float seam = pts[2].Y;

            Span<Vector2> head = stackalloc Vector2[3];
            head[0] = pts[0];
            head[1] = new Vector2(pts[1].X, seam + (up ? bleed : -bleed));
            head[2] = new Vector2(pts[6].X, seam + (up ? bleed : -bleed));

            Span<Vector2> stem = stackalloc Vector2[4];
            stem[0] = new Vector2(pts[2].X, seam - (up ? bleed : -bleed));
            stem[1] = new Vector2(pts[5].X, seam - (up ? bleed : -bleed));
            stem[2] = pts[4];
            stem[3] = pts[3];

            unsafe
            {
                fixed (Vector2* h = head) dl.AddConvexPolyFilled(h, 3, color);
                fixed (Vector2* s = stem) dl.AddConvexPolyFilled(s, 4, color);
            }

            dl.Flags = previousFlags;
        }

        /// <summary>
        /// A plain filled triangle, used wherever a rating is being *reported* rather than cast.
        ///
        /// The Reddit arrow is deliberately not used here. It carries a stem, which is what makes
        /// it read as a button you press; at indicator size that stem just makes it look heavy and
        /// blunt. A bare triangle says the same thing and stays clean small.
        /// </summary>
        private static void DrawTriangleInline(bool up, Vector4 color, float scale = 0.55f)
        {
            float w = ImGui.GetTextLineHeight() * scale;
            float h = w * 0.86f;
            Vector2 pos = ImGui.GetCursorScreenPos();
            float y = pos.Y + (ImGui.GetTextLineHeight() - h) * 0.5f;
            uint col = ImGui.ColorConvertFloat4ToU32(color);

            var dl = ImGui.GetWindowDrawList();
            var previousFlags = dl.Flags;
            dl.Flags |= ImDrawListFlags.AntiAliasedFill;

            if (up)
            {
                dl.AddTriangleFilled(new Vector2(pos.X + w * 0.5f, y),
                                     new Vector2(pos.X, y + h),
                                     new Vector2(pos.X + w, y + h), col);
            }
            else
            {
                dl.AddTriangleFilled(new Vector2(pos.X, y),
                                     new Vector2(pos.X + w, y),
                                     new Vector2(pos.X + w * 0.5f, y + h), col);
            }

            dl.Flags = previousFlags;
            ImGui.Dummy(new Vector2(w, ImGui.GetTextLineHeight()));
        }

        private static float TriangleWidth(float scale = 0.55f) => ImGui.GetTextLineHeight() * scale;

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
            float? separatorInset = null)
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

            if (restColor.HasValue)
                ImGui.GetWindowDrawList().AddRectFilled(min, max,
                    ImGui.ColorConvertFloat4ToU32(restColor.Value), Radius.Small);

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

            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(6, 6));
            if (ImGui.BeginPopup("rowctx"))
            {
                items();
                ImGui.EndPopup();
            }
            ImGui.PopStyleVar();
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

        /// <summary>One person you can rate. Returns true if a rating was cast this frame.</summary>
        private bool DrawRateRow(Contact contact, bool showDutyLine = true)
        {
            var identity = contact.Identity;
            var state = StateFor(identity);
            bool rated = false;

            var (slideRaw, collapseRaw) = RowExitPhases(state);
            float slide = Ease(slideRaw);
            float collapse = Ease(collapseRaw);

            if (collapseRaw >= 0.995f)
                return false;   // gone entirely - takes no space at all

            // `exit` still drives the tint, which fades out as the row leaves.
            float exit = Math.Max(slide, collapse);

            // Fades at full height rather than collapsing.
            //
            // The row used to shrink as it faded, which did two bad things: it squashed the
            // contents against a child that was getting shorter every frame, and - because the
            // prompt window is AlwaysAutoResize - it made the window itself resize on every one
            // of those frames, which is the jitter between votes. Holding the height means one
            // layout change at the end instead of fifteen, and nothing gets squeezed on the way.
            float fullHeight = RateRowHeight(showDutyLine);

            // Full height until the slide has finished, then eased down - but never to zero.
            //
            // BeginChild reads a size of 0 on an axis as "fill the remaining space", so the frame
            // where the collapse reached exactly 0 made the row expand to the full window instead
            // of disappearing. That is the snap back to full size at the end of the animation: the
            // row ballooned for a frame or two before being dropped. Clamped to a pixel, so the
            // child is always a real height and the only way it leaves is by being skipped.
            float height = Math.Max(1f, fullHeight * (1f - collapse));

            ImGui.PushID($"rate{identity.Key}");
            try
            {
                // Tint toward the rating that was given while it confirms, so the row itself
                // answers rather than only the little arrow.
                var bg = BgCard;
                if (state.Done)
                {
                    var hint = state.Given == VoteDirection.Up ? AccentGreen : AccentRed;
                    float mix = 0.16f * (1f - exit);
                    bg = new Vector4(
                        BgCard.X + (hint.X - BgCard.X) * mix,
                        BgCard.Y + (hint.Y - BgCard.Y) * mix,
                        BgCard.Z + (hint.Z - BgCard.Z) * mix,
                        BgCard.W);
                }

                ImGui.PushStyleColor(ImGuiCol.ChildBg, bg);
                ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, Radius.Card);
                ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(RowPadX, RowPadY));
                ImGui.PushStyleVar(ImGuiStyleVar.Alpha, ImGui.GetStyle().Alpha * (1f - slide));

                try
                {
                    if (ImGui.BeginChild("row", new Vector2(0, height), true,
                            ImGuiWindowFlags.NoScrollbar))
                    {
                        try
                        {
                            // The slide happens inside the row, not by moving the row.
                            //
                            // Offsetting the cursor before BeginChild pushed the row past the
                            // window's content edge, and because the prompt is AlwaysAutoResize
                            // the window grew to contain it and snapped back a frame later - the
                            // spike after every vote. Shifted in here, the child clips it and the
                            // layout never changes, so the only thing that moves the window is the
                            // collapse afterwards.
                            if (slide > 0f)
                                ImGui.SetCursorPosX(ImGui.GetCursorPosX()
                                    + (slide * ImGui.GetContentRegionAvail().X));

                            rated = DrawRateRowBody(contact, identity, state, showDutyLine);
                        }
                        finally
                        {
                            ImGui.EndChild();
                        }
                    }
                    else
                    {
                        ImGui.EndChild();
                    }
                }
                finally
                {
                    // In a finally because the body returns early for rows already rated, and
                    // ImGui's style stacks are shared with every other plugin.
                    ImGui.PopStyleVar(3);
                    ImGui.PopStyleColor();
                }

                ImGui.Dummy(new Vector2(0, 4f * (1f - exit)));
            }
            finally
            {
                ImGui.PopID();
            }

            return rated;
        }

        private bool DrawRateRowBody(Contact contact, CharacterIdentity identity, RateState state, bool showDutyLine)
        {
            var member = contact.Member;

            // EVERYTHING ON THE ROW IS CENTRED ON THE BUTTONS' LINE.
            //
            // The icon, the name and the up/down pair were each vertically placed by a different
            // rule - the icon inline at text height, the name by AlignTextToFramePadding, the
            // buttons at frame height plus four - and the three landed a couple of pixels apart.
            // Nothing about that is visible as a bug; it just reads as a row that was never quite
            // straightened. One mid-line, and all three are hung off it.
            float rowH = ImGui.GetFrameHeight() + 4f;
            float top = ImGui.GetCursorPosY();

            float iconSize = ImGui.GetTextLineHeight() + 4f;
            ImGui.SetCursorPosY(top + (rowH - iconSize) * 0.5f);
            DrawJobIconInline(member.JobId, iconSize);
            ImGui.SameLine(0, 7);

            // Whatever the buttons don't need. Names are clipped to it rather than allowed to
            // push the controls off the edge.
            float reserved = rowH * 1.45f * 2f + 5f + 12f;
            float nameRoom = ImGui.GetContentRegionMax().X - iconSize - 7f - reserved;

            string label = $"{DisplayName(member.Name)}  @{member.World}";

            using (UiRowNameFont.Push())
            {
                float lineH = ImGui.GetTextLineHeight();
                ImGui.SetCursorPosY(top + (rowH - lineH) * 0.5f);
                ImGui.TextColored(TextPrimary, Fit(label, nameRoom));
                if (ImGui.IsItemHovered() && label != Fit(label, nameRoom))
                    PaddedTooltip(label);
            }

            ImGui.SameLine(0, 0);
            ImGui.SetCursorPosY(top);

            if (member.Social != SocialLink.None)
            {
                ImGui.SameLine(0, 6);
                ImGui.TextColored(AccentYellow, member.Social == SocialLink.Friend ? "friend" : "FC");
                if (ImGui.IsItemHovered())
                    PaddedTooltip("Ratings between people who know each other count for less.");
            }

            bool rated = DrawRateButtons(contact, identity, state);

            if (showDutyLine)
            {
                string duty = string.IsNullOrWhiteSpace(contact.DutyName) ? "a duty" : contact.DutyName;
                ImGui.TextColored(TextMuted, $"{duty}  ·  {Ago(contact.MetUtc)}");
            }

            return rated;
        }

        /// <summary>
        /// The up/down pair, hard right against the row edge. Two separate buttons rather than one
        /// split control: on a 24-player alliance list you click these twenty-odd times, and two
        /// distinct targets are harder to misclick than two halves sharing an outline.
        /// </summary>
        private bool DrawRateButtons(Contact contact, CharacterIdentity identity, RateState state)
        {
            float h = ImGui.GetFrameHeight() + 4f;
            float w = h * 1.45f;
            float pairW = w * 2f + 5f;

            // Rated, or in flight: draw nothing on the right at all.
            //
            // The row's own tint already says the rating registered, and it's about to collapse
            // anyway - an arrow appearing for a third of a second, or the word "sending", is one
            // more thing moving in a row that's already moving.
            if (state.Done || contact.Member.Rated || state.Sending)
                return false;

            RightAlign(pairW);

            bool rated = false;

            if (DrawArrowButton("up", new Vector2(w, h), true, AccentGreen))
            {
                CastRating(contact, identity, state, VoteDirection.Up);
                rated = true;
            }
            if (ImGui.IsItemHovered())
                PaddedTooltip("I'd play with them again.");

            ImGui.SameLine(0, 5);

            if (DrawArrowButton("down", new Vector2(w, h), false, AccentRed))
            {
                CastRating(contact, identity, state, VoteDirection.Down);
                rated = true;
            }
            if (ImGui.IsItemHovered())
                PaddedTooltip("I'd rather not.");

            return rated;
        }

        /// <summary>A button with the arrow painted over it. ImGui can't put a polygon inside a
        /// button label, so the button is drawn empty and the arrow goes on top.</summary>
        private bool DrawArrowButton(string id, Vector2 size, bool up, Vector4 accent)
        {
            var tint = new Vector4(accent.X, accent.Y, accent.Z, 0.18f);
            var hover = new Vector4(accent.X, accent.Y, accent.Z, 0.38f);
            var border = new Vector4(accent.X, accent.Y, accent.Z, 0.55f);

            ImGui.PushStyleColor(ImGuiCol.Button, tint);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, hover);
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, accent);
            ImGui.PushStyleColor(ImGuiCol.Border, border);
            ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, Radius.Control);
            ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 1.0f);

            Vector2 pos = ImGui.GetCursorScreenPos();
            bool clicked = ImGui.Button($"##{id}", size);

            ImGui.PopStyleVar(2);
            ImGui.PopStyleColor(4);

            float arrow = size.Y * 0.62f;
            DrawRedditArrow(ImGui.GetWindowDrawList(),
                new Vector2(pos.X + (size.X - arrow) * 0.5f, pos.Y + (size.Y - arrow) * 0.5f),
                arrow, up, ImGui.ColorConvertFloat4ToU32(accent));

            return clicked;
        }

        /// <summary>
        /// Sends the rating straight away. The click is the submission - the row goes to "..." and
        /// settles into the arrow you gave when the server answers. Nothing blocks the draw thread.
        /// </summary>
        private void CastRating(Contact contact, CharacterIdentity identity, RateState state, VoteDirection direction)
        {
            if (Ratings == null || state.Sending || state.Done)
                return;

            state.Sending = true;

            uint dutyRowId = contact.DutyRowId;
            uint jobId = contact.Member.JobId;
            var social = contact.Member.Social;
            var metAt = contact.MetUtc;
            string encounterId = contact.EncounterId;

            _ = Task.Run(async () =>
            {
                try
                {
                    var result = await Ratings.SubmitAsync(
                        identity, direction, 0, (int)dutyRowId, social, metAt).ConfigureAwait(false);

                    if (result.Outcome == SubmitOutcome.Submitted || result.Outcome == SubmitOutcome.OnCooldown)
                    {
                        state.Given = direction;
                        state.DoneAtUtc = DateTime.UtcNow;
                        state.Done = true;
                        Encounters?.MarkRated(encounterId, identity);
                        History?.Add(identity, jobId, direction);
                    }
                    else
                    {
                        // Left un-done so the buttons come back and it can be retried.
                        ratingStatusMessage = result.Message;
                        ratingStatusExpiresUtc = DateTime.UtcNow.AddSeconds(8);
                    }
                }
                catch (Exception)
                {
                    ratingStatusMessage = "That rating couldn't be sent.";
                    ratingStatusExpiresUtc = DateTime.UtcNow.AddSeconds(8);
                }
                finally
                {
                    state.Sending = false;
                }
            });
        }

        // ══════════════════════════════════════════════════════════
        //  SCORE DISPLAY
        // ══════════════════════════════════════════════════════════

        /// <summary>
        /// A player's score as a compact chip. Shows a dash rather than a number below the display
        /// threshold - a "0%" there would read as a real score.
        /// </summary>
        /// <summary>
        /// A player's score, drawn into a fixed-width column so every row in a list ends at the
        /// same place. Variable-width content was what made the party list read as scattered.
        /// </summary>
        private const float RatingChipWidth = 52f;

        private void DrawRatingChip(CharacterIdentity identity)
        {
            // Nothing at all when the system is off. The chip was still being drawn on party rows
            // inside the recruitment card, so disabling ratings hid the tab but left scores on
            // screen - which is the one place someone turning it off would look first.
            if (!config.CommunityEnabled || !config.PartyRatingsEnabled)
            {
                ImGui.Dummy(new Vector2(RatingChipWidth, 0));
                return;
            }

            var rating = Ratings?.Get(identity);
            ImGui.AlignTextToFramePadding();

            // THE ROW STAYS, THE CHIP GOES. A hidden player who is genuinely in your party is still
            // in your party - dropping the row would leave a full group showing seven of eight and
            // read as the plugin losing track of somebody. What they lose is the score, the profile
            // and the vote, which is everything the community half is.
            //
            // Opted out says so; banned says nothing, and reads as a party member nobody has rated.
            if (IsOptedOut(rating))
            {
                DrawOptedOutColumn(identity, RatingChipWidth);
                return;
            }

            if (IsHidden(rating))
            {
                ImGui.Dummy(new Vector2(RatingChipWidth, 0));
                return;
            }

            if (rating is { Gated: false, OptedOut: false } && rating.Count > 0)
            {
                // The same caret pair the profile uses. This was a percentage with a hand-drawn
                // triangle, which disagreed with the profile on both the glyph and the number -
                // and the rating is a net tally, so a percentage was wrong here regardless.
                bool up = rating.Score >= 0;
                var colour = NetScoreColor(rating.Score);
                int shown = Math.Abs(rating.Score);

                float used = ArrowCountWidth(shown);
                if (used < RatingChipWidth)
                {
                    ImGui.Dummy(new Vector2(RatingChipWidth - used, 0));
                    ImGui.SameLine(0, 0);
                }

                DrawArrowCount(up ? FontAwesomeIcon.CaretUp : FontAwesomeIcon.CaretDown,
                    shown, colour);

                if (ImGui.IsItemHovered())
                {
                    PaddedTooltip($"{identity}\n\n"
                        + $"Rating {rating.Score}\n"
                        + $"{rating.Upvotes} up, {rating.Downvotes} down, {rating.Count} votes");
                }
                return;
            }

            // A dash for "no score yet" was drawn on nearly every row, because almost nobody has
            // reached the minimum vote count. A column of dashes looks like data and isn't; the
            // column still reserves its width so the rows stay aligned, but draws nothing in it.
            //
            // The loading state does still show, because "···" is temporary and means something.
            bool loading = rating == null && Ratings?.IsLoading(identity) == true;
            if (!loading)
            {
                ImGui.Dummy(new Vector2(RatingChipWidth, 0));
                return;
            }

            const string text = "···";
            float tw = ImGui.CalcTextSize(text).X;
            if (tw < RatingChipWidth)
            {
                ImGui.Dummy(new Vector2(RatingChipWidth - tw, 0));
                ImGui.SameLine(0, 0);
            }
            ImGui.TextColored(TextMuted, text);
        }

        /// <summary>Red through amber to green across the 0-100% range.</summary>
        private static Vector4 ScoreColor(double percent) => percent switch
        {
            >= 85 => ColorFromHex("#3fb56a"),
            >= 70 => ColorFromHex("#7ec96f"),
            >= 50 => ColorFromHex("#ffbd2e"),
            >= 30 => ColorFromHex("#e8956a"),
            _ => ColorFromHex("#e06a5a"),
        };

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
