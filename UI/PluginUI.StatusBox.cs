using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;

namespace PfPresets
{
    /// <summary>
    /// The context card above the preset list: what you're doing right now, and why applying a
    /// preset is refused when it is. While a listing is up - yours or your party leader's - it
    /// expands into a readout of that listing.
    ///
    /// Layout is a fixed stack of rows so everything lines up on a single left margin, with
    /// metadata right-aligned to a single right margin. State is signalled by the border colour.
    /// </summary>
    public partial class PluginUI
    {
        private const float CardPadX = 14f;
        private const float CardPadY = 12f;
        private const float CardRowGap = 7f;

        /// <summary>Space above the party section. Larger than a normal row gap because the block
        /// below it is a different kind of thing - a list, not another line of text.</summary>
        private const float CardSectionGap = 12f;

        /// <summary>Space around the collapse chevron. It's a seam between two blocks, so it needs
        /// air on both sides or it reads as belonging to whichever one it's touching.</summary>
        private const float CardChevronGap = 8f;

        /// <summary>
        /// Expand progress, 0 collapsed to 1 expanded, eased each frame.
        ///
        /// The card's height is derived from this rather than from the boolean, so the whole card
        /// grows and shrinks with the list instead of snapping between two sizes. ImGui has no
        /// transitions, so it's a hand-rolled ease against DeltaTime - cheap, one float.
        /// </summary>
        private float partyExpandT;
        private const float CardSeat = 24f;
        private const float CardSeatGap = 4f;
        private const float CardButtonH = 28f;

        /// <summary>
        /// Whether the card carries the party member list itself.
        ///
        /// Any party outside a duty, listing or not. It used to require a listing, which meant an
        /// ordinary party drew the card and then a second, separate PARTY block underneath it -
        /// two boxes describing one party. Inside content the card is gone entirely and the list
        /// stands alone, because there is no listing to describe and none of the card's actions
        /// apply in there.
        /// </summary>
        private bool ShowsEmbeddedParty(RecruitmentSnapshot snap)
        {
#if PFP_RATINGS
            return !snap.DetailsUnavailable
                && !pfAutomation.IsInDuty() && PartyMemberCount() > 0;
#else
            return false;
#endif
        }

        /// <summary>
        /// Height the party section occupies: the icon strip when collapsed, the member rows when
        /// expanded.
        ///
        /// One function, called by both the measure pass and the draw pass. They were computing
        /// this separately, and the moment they disagreed the card drew past its own bottom edge
        /// and over whatever came next.
        /// </summary>
        private float PartySectionHeight(RecruitmentSnapshot snap)
        {
#if PFP_RATINGS
            if (ShowsEmbeddedParty(snap))
            {
                float listH = PartyMemberCount(snap.DutyName, snap.DutyRowId) * (HoverRowHeight() + 1f);
                return CardSeat + (listH - CardSeat) * partyExpandT;
            }
#endif
            return CardSeat;
        }

        /// <summary>Advances the expand animation. Called once per frame, before anything measures
        /// the card, so the measure and draw passes see the same value.</summary>
        private void UpdatePartyExpand(RecruitmentSnapshot snap)
        {
#if PFP_RATINGS
            float target = ShowsEmbeddedParty(snap) && PartyListIsExpanded(snap) ? 1f : 0f;

            // ~180ms end to end. Fast enough not to be waited on, slow enough to read as motion.
            float step = ImGui.GetIO().DeltaTime * 6f;
            partyExpandT += Math.Clamp(target - partyExpandT, -step, step);

            if (Math.Abs(partyExpandT - target) < 0.002f)
                partyExpandT = target;
#endif
        }


        /// <summary>
        /// Whether the member list is showing rather than the seat strip.
        ///
        /// Collapsing exists to keep the card small while a listing is up, where the strip still
        /// says something useful - how many seats are filled, and of what. An ordinary party has
        /// no listing to summarise, so the strip would be the same information with the names
        /// taken out, and the chevron a control with one useful position. Always expanded there.
        /// </summary>
        private bool PartyListIsExpanded(RecruitmentSnapshot snap)
            => !snap.IsRecruiting || config.PartyListExpanded;

        /// <summary>Comments get two lines at most. Three starts to push the party list off the
        /// bottom of a small window, and a listing comment that long is usually a Discord link.</summary>
        private const int CommentMaxLines = 2;

        /// <summary>
        /// Width available to the comment, derived exactly as the card derives its own.
        ///
        /// Measure and draw both wrap through this, from the same content region in the same
        /// frame. If they ever disagreed about the width they would disagree about the line
        /// count, and the card would reserve one height and draw another.
        /// </summary>
        private static float CommentWidth() => ImGui.GetContentRegionAvail().X - 4f - (CardPadX * 2f);

        /// <summary>
        /// Splits a listing comment into at most <see cref="CommentMaxLines"/> lines.
        ///
        /// Breaks on spaces so words stay whole, and ellipsises the last line rather than letting
        /// it run under the card border - ImGui clips instead of wrapping, so an overlong comment
        /// used to simply lose its tail with no sign there was more.
        /// </summary>
        private static List<string> WrapComment(string comment, float width)
        {
            var lines = new List<string>();
            if (string.IsNullOrWhiteSpace(comment) || width <= 0f)
                return lines;

            string rest = comment.Trim();

            while (rest.Length > 0 && lines.Count < CommentMaxLines)
            {
                if (ImGui.CalcTextSize(rest).X <= width)
                {
                    lines.Add(rest);
                    break;
                }

                // Last line we are allowed: take what fits, with an ellipsis.
                if (lines.Count == CommentMaxLines - 1)
                {
                    lines.Add(Fit(rest, width));
                    break;
                }

                // Longest prefix that fits, then back up to the last space in it.
                int len = rest.Length;
                while (len > 1 && ImGui.CalcTextSize(rest.Substring(0, len)).X > width)
                    len--;

                int brk = rest.LastIndexOf(' ', Math.Min(len, rest.Length - 1));
                if (brk <= 0)
                    brk = len;   // one very long word: hard break rather than loop forever

                lines.Add(rest.Substring(0, brk).TrimEnd());
                rest = rest.Substring(brk).TrimStart();
            }

            return lines;
        }

        /// <summary>Space the card needs this frame, or 0 when there's nothing to report.</summary>
        private float MeasureStatusCard(RecruitmentSnapshot snap)
        {
            if (!snap.HasAnythingToShow)
                return 0f;

            float line = ImGui.GetTextLineHeight();
            float h = CardPadY * 2f;

            h += line;                                  // header row
            h += CardRowGap + 1f + CardRowGap;          // divider

            if (snap.IsRecruiting)
            {
                if (snap.DetailsUnavailable)
                {
                    h += line;                          // "can't read it" note
                    h += CardRowGap + CardButtonH;      // action button
                }
                else
                {
                    h += line;                          // duty

                    // However many lines the comment actually wraps to, measured the same way
                    // the draw pass will wrap it.
                    int commentLines = WrapComment(snap.Comment, CommentWidth()).Count;
                    if (commentLines > 0)
                        h += CardRowGap + (line * commentLines);
                    h += CardSectionGap + PartySectionHeight(snap);   // strip or member list

#if PFP_RATINGS
                    // The chevron, and the air either side of it - faded in with the list so the
                    // card doesn't jump the moment the animation starts.
                    if (ShowsEmbeddedParty(snap))
                        h += (CardChevronGap + 22f + CardChevronGap) * partyExpandT;
#endif

                    h += CardSectionGap + CardButtonH;                // action button
                }
            }
            else
            {
                bool queueOrDuty = snap.Activity == PfActivity.InQueue
                    || snap.Activity == PfActivity.InDuty;

                // Queue/duty: only add the "Your party" line when there's a party to show.
                // Otherwise: always one status/blocked-reason line.
                if (queueOrDuty)
                {
                    if (ShowsEmbeddedParty(snap))
                        h += line;                          // "Your party" label
                }
                else
                {
                    h += line;                              // status line / blocked reason
                }

#if PFP_RATINGS
                // Same party section the listing card reserves, so an ordinary party gets one
                // card instead of a card plus a loose list.
                // No chevron in an ordinary party, so no room reserved for one.
                if (ShowsEmbeddedParty(snap))
                    h += CardSectionGap + PartySectionHeight(snap);
#endif

                if (InAParty())
                    h += CardRowGap + CardButtonH;      // leave party
            }

            return h;
        }

        private void DrawStatusCard(RecruitmentSnapshot snap, float height)
        {
            if (height <= 0f)
                return;

            var dl = ImGui.GetWindowDrawList();
            Vector2 origin = ImGui.GetCursorScreenPos();

            // Measured from the content region, not the window width. GetWindowWidth() includes
            // the scrollbar, so the card ran under it and lost its right edge the moment the
            // preset list grew long enough to need one.
            float avail = ImGui.GetContentRegionAvail().X;

            Vector2 min = new Vector2(origin.X, origin.Y);
            Vector2 max = new Vector2(origin.X + avail - 4f, origin.Y + height);
            DrawRaisedCard(dl, min, max, snap);

            float left = min.X + CardPadX;
            float right = max.X - CardPadX;
            float y = min.Y + CardPadY;
            float line = ImGui.GetTextLineHeight();

            DrawCardHeader(dl, snap, left, right, ref y, line);

            // Divider under the header ties the card together instead of leaving the title
            // floating above loose text.
            y += CardRowGap;
            dl.AddLine(new Vector2(left, y), new Vector2(right, y),
                ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 1f, 0.07f)), 1f);
            y += 1f + CardRowGap;

            if (snap.IsRecruiting)
                DrawListingBody(dl, snap, left, right, ref y, line, max.Y);
            else
                DrawIdleBody(dl, snap, left, right, ref y, line, max.Y);

            ImGui.SetCursorScreenPos(new Vector2(origin.X, max.Y + 8f));
        }

        // ══════════════════════════════════════════════════════════
        //  ROWS
        // ══════════════════════════════════════════════════════════

        /// <summary>
        /// Title on the left, and the two numbers you actually glance at on the right: how full the
        /// party is, and how long the listing has left.
        ///
        /// They used to sit on their own row below the comment, which put them directly in the path
        /// of the member list - the list drew over them. The header has room and they belong with
        /// the thing they describe. The old "Leader" chip is gone: the title already reads "Your
        /// Recruitment", so the chip told you nothing you couldn't see.
        /// </summary>
        private void DrawCardHeader(ImDrawListPtr dl, RecruitmentSnapshot snap,
            float left, float right, ref float y, float line)
        {
            var (title, accent, glyph) = DescribeActivity(snap);

            DrawGlyphAt(glyph, new Vector2(left, y + (line - 14f) * 0.5f), 14f, accent);

            float cursor = right;

            // Laid out from the right edge inward so a long title can never displace them.
            if (snap.IsRecruiting && !snap.DetailsUnavailable && snap.TimeLeft.HasValue)
            {
                string time = FormatTimeLeft(snap.TimeLeft.Value);
                Vector2 ts = ImGui.CalcTextSize(time);
                dl.AddText(new Vector2(cursor - ts.X, y),
                    ImGui.ColorConvertFloat4ToU32(TextMuted), time);

                float iconX = cursor - ts.X - 17f;
                DrawGlyphAt(FontAwesomeIcon.Clock, new Vector2(iconX, y + (line - 12f) * 0.5f),
                    12f, TextMuted);
                cursor = iconX - 16f;
            }

            if (snap.IsRecruiting && !snap.DetailsUnavailable && snap.SlotsTotal > 0)
            {
                // A full party is the outcome you were recruiting for, so it gets said rather than
                // counted - "8 of 8" makes you do the arithmetic to notice.
                bool full = snap.IsPartyFull;
                string filled = full ? "Party filled" : $"{snap.SlotsFilled} of {snap.SlotsTotal}";

                Vector2 fs = ImGui.CalcTextSize(filled);
                dl.AddText(new Vector2(cursor - fs.X, y),
                    ImGui.ColorConvertFloat4ToU32(full ? AccentGreen : TextSecondary), filled);
                cursor = cursor - fs.X - 14f;
            }

            ClippedText(dl, title, left + 21f, cursor, y, line, accent);

            y += line;
        }

        /// <summary>Compact form of the listing's remaining time - "59m", "2h 04m".</summary>
        private static string FormatTimeLeft(TimeSpan left)
        {
            if (left <= TimeSpan.Zero)
                return "expired";
            if (left.TotalHours >= 1)
                return $"{(int)left.TotalHours}h {left.Minutes:00}m";
            return $"{Math.Max(1, (int)left.TotalMinutes)}m";
        }

        private void DrawIdleBody(ImDrawListPtr dl, RecruitmentSnapshot snap,
            float left, float right, ref float y, float line, float cardBottom)
        {
            // In queue or in a duty: the header already names the duty/queue, so we don't
            // repeat it. Show "Your party" as a label before the member list instead of the
            // generic "you can post" line or the blocked reason (which was the source of the
            // duplicate "You are in the duty finder queue" text).
            bool queueOrDuty = snap.Activity == PfActivity.InQueue
                || snap.Activity == PfActivity.InDuty;

            if (queueOrDuty)
            {
                // "Your party" label — only if there's actually a party to show beneath it.
                if (ShowsEmbeddedParty(snap))
                {
                    ClippedText(dl, "Your party", left, right, y, line, TextSecondary);
                    y += line;
                }
            }
            else
            {
                string text = snap.BlockedReason.Length > 0
                    ? snap.BlockedReason
                    : "You can post a Party Finder listing.";

                ClippedText(dl, text, left, right, y, line, TextSecondary);
                y += line;
            }

            DrawPartySection(dl, snap, left, right, ref y);

            // Offered whenever there's a party, leader or not. "End Recruitment" belongs to a
            // listing that's up; with no listing the only thing left to do here is leave.
            if (InAParty())
            {
                y = cardBottom - CardPadY - CardButtonH;
                DrawCardAction(snap, "Leave Party", left, right, ref y,
                    "Leave this party.", isDestructive: true);
            }
        }

        /// <summary>
        /// The party: an icon strip when collapsed, the member rows when expanded.
        ///
        /// Shared by both card bodies. It used to live inside the listing body only, so an
        /// ordinary party fell through to the standalone panel and the user saw two boxes for one
        /// party. The strip and the rows are alternatives, never both - showing the same eight
        /// people twice was the duplication that collapsing exists to avoid.
        /// </summary>
        private void DrawPartySection(ImDrawListPtr dl, RecruitmentSnapshot snap,
            float left, float right, ref float y)
        {
            if (!ShowsEmbeddedParty(snap))
                return;

            y += CardSectionGap;

            bool expanded = ShowsEmbeddedParty(snap) && PartyListIsExpanded(snap);
            float sectionH = PartySectionHeight(snap);

            if (partyExpandT > 0.02f)
            {
#if PFP_RATINGS
                // Clipped to the animated height, so rows are revealed rather than popping in.
                ImGui.PushClipRect(new Vector2(left, y), new Vector2(right, y + sectionH), true);
                try
                {
                    ImGui.SetCursorScreenPos(new Vector2(left, y));
                    DrawPartyMembers(allowKick: true, width: right - left, originX: left,
                        dutyName: snap.DutyName, dutyRowId: snap.DutyRowId);
                }
                finally
                {
                    ImGui.PopClipRect();
                }
#endif
            }
            else
            {
                // Collapsed shows every seat, filled and open - it's the whole party at a glance,
                // which is the point of collapsing.
                float sx = left;
                float seatRoom = right - CardSeat - 8f;   // leave the chevron its corner
                foreach (var seat in snap.Filled)
                {
                    if (sx + CardSeat > seatRoom) break;
                    DrawFilledSeat(seat, new Vector2(sx, y));
                    sx += CardSeat + CardSeatGap;
                }
                foreach (var seat in snap.Open)
                {
                    if (sx + CardSeat > seatRoom) break;
                    DrawOpenSeat(seat, new Vector2(sx, y));
                    sx += CardSeat + CardSeatGap;
                }
            }

            // The card never reads back where ImGui's cursor ended up - it jumps by the measured
            // height instead. That's what stops the rows and the blocks below them fighting over
            // the same space.
            y += sectionH;

            DrawPartyToggle(snap, left, right, ref y, expanded);
        }

        /// <summary>Whether there's anyone else in the party right now.</summary>
        private bool InAParty()
        {
            try { return pfAutomation.GetOtherPartyMemberDetails().Count > 0; }
            catch (Exception) { return false; }
        }

        private void DrawListingBody(ImDrawListPtr dl, RecruitmentSnapshot snap,
            float left, float right, ref float y, float line, float cardBottom)
        {
            if (snap.DetailsUnavailable)
            {
                // Someone else leads and we have no trustworthy read of their listing. Say so and
                // offer to fetch it, rather than showing our own settings as if they were theirs.
                ClippedText(dl, "Details aren't loaded for this listing yet.",
                    left, right, y, line, TextMuted);
                y += line + CardRowGap;

                DrawCardAction(snap, "Load Details", left, right, ref y,
                    "Open the party's listing briefly so its duty, slots and\ntimer can be read.",
                    isDestructive: false);
                return;
            }

            // ── Duty ──
            ClippedText(dl, snap.DutyName, left, right, y, line, AccentBlue);
            y += line;

            // ── Comment ──
            // Always one line. Wrapping made every block below it move, which is half the reason
            // the card never looked settled.
            var commentLines = WrapComment(snap.Comment, right - left);
            if (commentLines.Count > 0)
            {
                y += CardRowGap;
                float commentTop = y;

                foreach (string commentLine in commentLines)
                {
                    ClippedText(dl, commentLine, left, right, y, line, TextPrimary);
                    y += line;
                }

                // The whole block hovers, not just the last line, and always offers the full text -
                // two lines can still be a truncation.
                if (IsMouseOver(new Vector2(left, commentTop), new Vector2(right, y)))
                    PaddedTooltip(snap.Comment);
            }

            DrawPartySection(dl, snap, left, right, ref y);

            // ── Action ──
            //
            // Only when a listing is actually up. Without one there is nothing to end, and the
            // button was appearing regardless.
            if (!snap.IsRecruiting)
                return;

            // Pinned to the card's own bottom edge rather than flowing after whatever came before,
            // so the gap beneath it is the same whether the party is a strip or a six-row list.
            y = cardBottom - CardPadY - CardButtonH;
            // Which action belongs here is a function of state, not of who's leading. The old
            // version offered "End Recruitment" inside a duty, with no party, and at 8 of 8 -
            // none of which have a listing left to end.
            if (snap.IsLeader && snap.IsPartyFull)
            {
                DrawCardAction(snap, "Queue to duty", left, right, ref y,
                    "Queue for the duty this listing was for.", isDestructive: false,
                    secondary: "Disband", secondaryTooltip: "Break up the party.");
            }
            else if (snap.IsLeader)
            {
                DrawCardAction(snap, "End Recruitment", left, right, ref y,
                    "Take your Party Finder listing down.", isDestructive: true);
            }
            else
            {
                DrawCardAction(snap, "Leave Party", left, right, ref y,
                    "Leave this party.", isDestructive: true);
            }
        }

        /// <summary>
        /// The chevron that swaps the icon strip for the member list.
        ///
        /// Collapsed it sits at the right end of the seat row - it reads as "there's more behind
        /// this". Expanded it moves to the centre, below the list, where it's the seam between the
        /// list and the footer rather than a control floating beside a name.
        /// </summary>
        private void DrawPartyToggle(RecruitmentSnapshot snap, float left, float right,
            ref float y, bool expanded)
        {
#if PFP_RATINGS
            // Hidden without a listing: there is nothing to collapse back to that would be worth
            // reading, so the control has one useful position and isn't a control.
            if (!ShowsEmbeddedParty(snap) || !snap.IsRecruiting)
                return;

            const float w = 26f, h = 22f;
            float x, ty;

            if (expanded)
            {
                y += CardChevronGap * partyExpandT;
                x = left + (right - left - w) * 0.5f;
                ty = y;
                y += (h + CardChevronGap) * partyExpandT;
            }
            else
            {
                // Same row as the seats, hard right. y has already moved past the strip.
                x = right - w;
                ty = y - CardSeat + (CardSeat - h) * 0.5f;
            }

            ImGui.SetCursorScreenPos(new Vector2(x, ty));
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0, 0, 0, 0));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(1, 1, 1, 0.06f));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(1, 1, 1, 0.10f));
            ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 5f);

            if (ImGui.Button("##PartyToggle", new Vector2(w, h)))
            {
                config.PartyListExpanded = !expanded;
                config.Save();
            }

            bool hovered = ImGui.IsItemHovered();
            ImGui.PopStyleVar();
            ImGui.PopStyleColor(3);

            DrawGlyphCentered(expanded ? FontAwesomeIcon.ChevronUp : FontAwesomeIcon.ChevronDown,
                new Vector2(x, ty), new Vector2(x + w, ty + h),
                hovered ? TextPrimary : TextMuted);

            if (hovered)
                PaddedTooltip(expanded ? "Show as icons" : "Show the party");
#endif
        }

        /// <summary>
        /// The card's footer action. Full width, because a right-aligned button was a fifth
        /// alignment on a card that already had four and never looked deliberate.
        ///
        /// An optional secondary sits beside it, sized to its own label, for the one state that
        /// has two reasonable things to do.
        /// </summary>
        private void DrawCardAction(RecruitmentSnapshot snap, string label,
            float left, float right, ref float y, string tooltip, bool isDestructive,
            string? secondary = null, string? secondaryTooltip = null)
        {
            float secondaryW = secondary != null
                ? MathF.Max(90f, ImGui.CalcTextSize(secondary).X + 24f)
                : 0f;
            float gap = secondary != null ? 6f : 0f;
            float w = right - left - secondaryW - gap;

            ImGui.SetCursorScreenPos(new Vector2(left, y));

            if (isDestructive)
            {
                ImGui.PushStyleColor(ImGuiCol.Button, AccentRed);
                ImGui.PushStyleColor(ImGuiCol.ButtonHovered, ColorFromHex("#e8806f"));
                ImGui.PushStyleColor(ImGuiCol.ButtonActive, ColorFromHex("#c75446"));
                ImGui.PushStyleColor(ImGuiCol.Text, TextPrimary);
            }
            else
            {
                ImGui.PushStyleColor(ImGuiCol.Button, JsCancelBg);
                ImGui.PushStyleColor(ImGuiCol.ButtonHovered, JsCancelHover);
                ImGui.PushStyleColor(ImGuiCol.ButtonActive, JsCancelHover);
                ImGui.PushStyleColor(ImGuiCol.Text, JsText);
            }

            ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 6f);
            bool clicked = ImGui.Button($"{label}##CardAction", new Vector2(w, CardButtonH));
            ImGui.PopStyleVar();
            ImGui.PopStyleColor(4);

            if (ImGui.IsItemHovered())
                PaddedTooltip(tooltip);

            if (secondary != null)
            {
                ImGui.SetCursorScreenPos(new Vector2(left + w + gap, y));
                ImGui.PushStyleColor(ImGuiCol.Button, ColorFromHex("#7d3a33"));
                ImGui.PushStyleColor(ImGuiCol.ButtonHovered, AccentRed);
                ImGui.PushStyleColor(ImGuiCol.ButtonActive, ColorFromHex("#c75446"));
                ImGui.PushStyleColor(ImGuiCol.Text, ColorFromHex("#ffe6e1"));
                ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 6f);
                bool second = ImGui.Button($"{secondary}##CardActionSecond",
                    new Vector2(secondaryW, CardButtonH));
                ImGui.PopStyleVar();
                ImGui.PopStyleColor(4);

                if (ImGui.IsItemHovered() && secondaryTooltip != null)
                    PaddedTooltip(secondaryTooltip);

                if (second && secondary == "Disband")
                {
                    AskConfirm("Disband party", "Break up the party?", "Yes, disband",
                        () => pfAutomation.DisbandParty(),
                        detail: "Everyone is removed at once. This cannot be undone.");
                }
            }

            if (clicked)
            {
                if (label == "End Recruitment") pfAutomation.EndRecruitment();
                else if (label == "Leave Party") pfAutomation.LeaveParty();
                else if (label == "Load Details") pfAutomation.LoadPartyListingDetails();
                else if (label == "Queue to duty")
                {
                    // Not wired yet - the duty-selection path needs working out, and guessing at
                    // Duty Finder automation would be worse than an honest placeholder.
                    pfAutomation.QueueToListedDuty();
                }
            }

            y += CardButtonH;
        }

        // ══════════════════════════════════════════════════════════
        //  CHROME
        // ══════════════════════════════════════════════════════════

        private static void ClippedText(ImDrawListPtr dl, string text,
            float left, float right, float y, float line, Vector4 color)
        {
            dl.PushClipRect(new Vector2(left, y), new Vector2(right, y + line), true);
            dl.AddText(new Vector2(left, y), ImGui.ColorConvertFloat4ToU32(color), text);
            dl.PopClipRect();
        }

        /// <summary>
        /// The card's border colour, which is how state is signalled: a full party reads green,
        /// active recruiting a deep blue, queueing a brighter blue, and anything blocking you amber.
        /// </summary>
        private static Vector4 StateBorderColor(RecruitmentSnapshot snap)
        {
            if (snap.IsRecruiting)
                return snap.IsPartyFull ? AccentGreen : StatusBorderRecruiting;

            return snap.Activity switch
            {
                PfActivity.InQueue => AccentBlue,
                PfActivity.InDuty => AccentYellow,
                PfActivity.InPartyNotLeader => StatusBorderParty,
                PfActivity.NotLoggedIn => TextMuted,
                _ => snap.BlockedReason.Length > 0 ? AccentYellow : BorderHover,
            };
        }

        /// <summary>
        /// The card surface.
        ///
        /// A raised sheet rather than an outlined box: elevation carries the grouping, and state
        /// is a rail down the left edge instead of a ring around everything. The full 2px coloured
        /// border was the loudest thing on screen and boxed in content that already reads as a
        /// group from its background alone.
        /// </summary>
        private static void DrawRaisedCard(ImDrawListPtr dl, Vector2 min, Vector2 max,
            RecruitmentSnapshot snap)
        {
            const float radius = 8f;

            // Soft drop shadow, approximated with stacked translucent rects - no blurred texture
            // needed and it costs a handful of quads.
            for (int i = 4; i >= 1; i--)
            {
                float spread = i * 1.4f;
                float alpha = 0.05f - (i * 0.009f);
                if (alpha <= 0f) continue;
                dl.AddRectFilled(
                    new Vector2(min.X - spread * 0.5f, min.Y + 1f),
                    new Vector2(max.X + spread * 0.5f, max.Y + spread),
                    ImGui.ColorConvertFloat4ToU32(new Vector4(0f, 0f, 0f, alpha)),
                    radius + spread);
            }

            dl.AddRectFilled(min, max, ImGui.ColorConvertFloat4ToU32(BgCard), radius);

            // A hairline along the top edge reads as a lit surface rather than a drawn outline.
            // Nothing else: no ring, no rail. State is already carried by the header's icon and
            // its role chip, and a coloured edge on top of that was one signal too many.
            dl.AddLine(new Vector2(min.X + radius, min.Y + 0.5f), new Vector2(max.X - radius, min.Y + 0.5f),
                ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 1f, 0.05f)), 1f);
        }

        private void DrawFilledSeat(FilledSeat seat, Vector2 pos)
        {
            var dl = ImGui.GetWindowDrawList();
            Vector2 max = new Vector2(pos.X + CardSeat, pos.Y + CardSeat);

            if (seat.JobId > 0 && TryGetIconHandle(IconJobBase + seat.JobId, out var handle))
                dl.AddImage(handle, pos, max);
            else
                DrawGlyphAt(FreeGlyph, pos, CardSeat, TextSecondary);

            if (seat.IsYou)
                dl.AddRect(pos, max, ImGui.ColorConvertFloat4ToU32(AccentBlue), 4f, ImDrawFlags.None, 1.5f);

            if (IsMouseOver(pos, max))
            {
                string job = JobData.FindById(seat.JobId)?.Name ?? "Unknown";
                PaddedTooltip($"{DisplayName(seat.Name)} ({job})");
            }
        }

        /// <summary>An open seat: dimmed role icon on a recessed backing, so filled and empty read
        /// differently by form and not only by colour.</summary>
        private void DrawOpenSeat(OpenSeat seat, Vector2 pos)
        {
            var dl = ImGui.GetWindowDrawList();
            Vector2 max = new Vector2(pos.X + CardSeat, pos.Y + CardSeat);

            dl.AddRectFilled(pos, max, ImGui.ColorConvertFloat4ToU32(new Vector4(0f, 0f, 0f, 0.28f)), 4f);
            dl.AddRect(pos, max, ImGui.ColorConvertFloat4ToU32(BorderDefault), 4f, ImDrawFlags.None, 1f);

            uint icon = seat.JobId.HasValue ? IconJobBase + seat.JobId.Value : RoleIconFor(seat.Role);
            if (icon != 0 && TryGetIconHandle(icon, out var handle))
            {
                const float inset = 3f;
                dl.AddImage(handle,
                    new Vector2(pos.X + inset, pos.Y + inset),
                    new Vector2(max.X - inset, max.Y - inset),
                    Vector2.Zero, Vector2.One,
                    ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 1f, 0.4f)));
            }
            else
            {
                DrawGlyphAt(FreeGlyph, pos, CardSeat, TextMuted);
            }

            if (IsMouseOver(pos, max))
                PaddedTooltip($"Open: {seat.Label}");
        }

        private static uint RoleIconFor(RoleType role) => role switch
        {
            RoleType.Tank => IconRoleTank,
            RoleType.Healer => IconRoleHealer,
            RoleType.MeleeDPS or RoleType.PhysRangedDPS or RoleType.MagicRangedDPS => IconRoleDps,
            _ => 0u,
        };

        private static (string Title, Vector4 Color, FontAwesomeIcon Glyph) DescribeActivity(
            RecruitmentSnapshot snap)
        {
            if (snap.IsRecruiting)
            {
                return snap.IsLeader
                    ? ("Your Recruitment", AccentGreen, FontAwesomeIcon.Bullhorn)
                    : ("Party Recruitment", AccentBlue, FontAwesomeIcon.Bullhorn);
            }

            if (snap.Activity == PfActivity.InQueue)
            {
                // When we know the specific fight or roulette, say what we're queued for.
                string title = !string.IsNullOrWhiteSpace(snap.DutyName)
                    ? $"In queue for {snap.DutyName}"
                    : "In the Duty Finder queue";
                return (title, AccentBlue, FontAwesomeIcon.Hourglass);
            }

            if (snap.Activity == PfActivity.InDuty)
            {
                string title = !string.IsNullOrWhiteSpace(snap.DutyName)
                    && !snap.DutyName.Equals("None", StringComparison.OrdinalIgnoreCase)
                    ? $"In {snap.DutyName}"
                    : "In a duty";
                return (title, AccentYellow, FontAwesomeIcon.DoorClosed);
            }

            return snap.Activity switch
            {
                PfActivity.InPartyNotLeader => ("In a party", TextSecondary, FontAwesomeIcon.Users),
                PfActivity.NotLoggedIn => ("Not logged in", TextMuted, FontAwesomeIcon.TimesCircle),
                _ => ("Ready to recruit", TextSecondary, FontAwesomeIcon.CheckCircle),
            };
        }

        /// <summary>Hover test against a screen rect, used instead of an invisible ImGui item so the
        /// card's hover regions don't fight the buttons drawn on top of it.</summary>
        private static bool IsMouseOver(Vector2 min, Vector2 max)
        {
            if (!ImGui.IsWindowHovered(ImGuiHoveredFlags.AllowWhenBlockedByActiveItem))
                return false;
            Vector2 m = ImGui.GetMousePos();
            return m.X >= min.X && m.X <= max.X && m.Y >= min.Y && m.Y <= max.Y;
        }
    }
}
