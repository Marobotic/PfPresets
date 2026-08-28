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
        private const float CardPadX = CardPadding;
        private const float CardPadY = CardPadding;
        private const float CardRowGap = 7f;

        /// <summary>Space above the party section. Larger than a normal row gap because the block
        /// below it is a different kind of thing - a list, not another line of text.</summary>
        private const float CardSectionGap = 12f;

        // The card's actions are the same height as every other action button in the plugin, and
        // as each other - two buttons side by side at different heights is the thing this number
        // exists to prevent.
        private const float CardButtonH = ButtonHeight;

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
        /// Height the member rows occupy, or 0 when the card isn't carrying them.
        ///
        /// One function, called by both the measure pass and the draw pass. They were computing
        /// this separately, and the moment they disagreed the card drew past its own bottom edge
        /// and over whatever came next.
        /// </summary>
        private float PartySectionHeight(RecruitmentSnapshot snap)
        {
#if PFP_RATINGS
            if (ShowsEmbeddedParty(snap))
                return PartyMemberCount(snap.DutyName, snap.DutyRowId) * (HoverRowHeight() + HoverRowGap);
#endif
            return 0f;
        }

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
        /// <see cref="WrapComment"/> with the line cap as an argument, so the preset rows - which
        /// wrap the same text to their own width - can share the one piece of wrapping logic
        /// rather than measure it a second way and disagree about the height.
        /// </summary>
        private static List<string> WrapCommentToLines(string comment, float width, int maxLines)
        {
            var lines = new List<string>();
            if (string.IsNullOrWhiteSpace(comment) || width <= 0f || maxLines <= 0)
                return lines;

            string rest = comment.Trim();

            while (rest.Length > 0 && lines.Count < maxLines)
            {
                if (ImGui.CalcTextSize(rest).X <= width)
                {
                    lines.Add(rest);
                    break;
                }

                // Last line we are allowed: take what fits, with an ellipsis.
                if (lines.Count == maxLines - 1)
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
                    // Only when the draw pass will actually print it - the header takes the fight's
                    // name whenever it has one, and a line reserved for something that is no longer
                    // drawn is a card an empty row too tall.
                    // One line either way: the fight when the header could not take it, or who
                    // owns the listing when it could. Both, never - see the draw pass.
                    if (!HeaderNamesDuty(snap) || ShowsLeaderLine(snap))
                        h += line;                      // duty, or the leader

                    // However many lines the comment actually wraps to, measured the same way
                    // the draw pass will wrap it - in the comment's own face, which is not the one
                    // the rest of the card is set in. See CommentFont.
                    int commentLines = WrapCommentInFace(snap.Comment, CommentWidth(), CommentMaxLines).Count;
                    if (commentLines > 0)
                        h += CardRowGap + (CommentLineHeight() * commentLines);

#if PFP_RATINGS
                    // Only when the rows are actually drawn. It used to reserve a seat-strip's
                    // worth of height unconditionally, which left a band of empty card under any
                    // listing the member list didn't apply to.
                    if (ShowsEmbeddedParty(snap))
                        h += CardSectionGap + PartySectionHeight(snap);
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

            // THE DUTY'S OWN ICON WHERE THERE IS A DUTY, the status glyph where there is not.
            //
            // The header carried a bullhorn, a tick or a door - a picture of the STATE, which the
            // words beside it and the colour they are set in were already saying twice over. The
            // one thing on this card that a picture can say faster than text is which kind of
            // content this is, and the preset rows in the list beside it have been drawing exactly
            // that icon from the same sheet all along.
            //
            // Sized to the header line rather than to the glyph's 14px: a game icon is art with its
            // own margins and comes out visibly smaller than a FontAwesome glyph at the same box.
            const float mark = 18f;
            uint dutyIcon = GetCategoryIcon(
                dutyDataHelper.GetCategoryIdForDuty(snap.DutyRowId, snap.DutyName));

            if (dutyIcon != 0 && TryGetIconHandle(dutyIcon, out var dutyHandle))
            {
                var markMin = new Vector2(left, y + (line - mark) * 0.5f);
                dl.AddImage(dutyHandle, markMin, new Vector2(markMin.X + mark, markMin.Y + mark));
            }
            else
            {
                DrawGlyphAt(glyph, new Vector2(left, y + (line - 14f) * 0.5f), 14f, accent);
            }

            float cursor = right;

            // Laid out from the right edge inward so a long title can never displace them.
            if (snap.IsRecruiting && !snap.DetailsUnavailable && snap.TimeLeft.HasValue)
            {
                // AN ESTIMATE SAYS SO. The game only reports a listing's real remaining time while
                // its detail window is open, so most of the time this is the plugin counting down
                // from when it last saw the listing go up - which drifts, and drifts most on
                // somebody else's listing where we may never have seen it start. It was printed as
                // a flat "41m" either way, and a number with no qualifier is a claim.
                string time = FormatTimeLeft(snap.TimeLeft.Value);
                if (!snap.TimeLeftIsExact)
                    time = "~" + time;

                Vector2 ts = ImGui.CalcTextSize(time);
                var timeAt = new Vector2(cursor - ts.X, y);
                dl.AddText(timeAt, ImGui.ColorConvertFloat4ToU32(TextMuted), time);

                float iconX = cursor - ts.X - 17f;
                DrawGlyphAt(FontAwesomeIcon.Clock, new Vector2(iconX, y + (line - 12f) * 0.5f),
                    12f, TextMuted);

                if (IsMouseOver(new Vector2(iconX, y), new Vector2(cursor, y + line)))
                {
                    PaddedTooltip(snap.TimeLeftIsExact
                        ? "Time left on the listing, read from the game."
                        : "Estimated from when the listing was last seen.\n\n"
                            + "The game only reports the real figure while a listing's\n"
                            + "detail window is open, so this is a countdown rather than\n"
                            + "a reading. A listing expires an hour after it goes up.");
                }

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

            DrawPartySection(snap, left, right, ref y);

            // Offered whenever there's a party, leader or not. "End Recruitment" belongs to a
            // listing that's up; with no listing the only thing left to do here is leave.
            if (InAParty())
            {
                y = cardBottom - CardPadY - CardButtonH;
                DrawCardActionRow(snap, left, right, ref y);
            }
        }

        /// <summary>
        /// Leave, or disband, depending on what the press would actually do.
        ///
        /// Three cases, and the button says which one it is:
        ///
        ///   In a duty - untouched. The duty's own Leave lives on the party panel and is the right
        ///   action there; the game refuses a party leave from inside instanced content anyway.
        ///
        ///   Leading a cross-world party - "Disband Party". A cross-world party *is* the Party
        ///   Finder listing, so the leader walking out ends it for everyone. The old button called
        ///   /leave and described that as leaving, which is true about the keystroke and false
        ///   about the outcome: seven other people were sent home by a button that said it only
        ///   concerned you. It now calls the game's own disband, which is what was happening.
        ///
        ///   Anything else - "Leave Party", unchanged. In a same-world party the lead passes to
        ///   somebody else and the party carries on, so leaving is exactly what it says.
        /// </summary>
        /// <summary>
        /// The card's actions, on one row: Update progress on the left, leaving on the right.
        ///
        /// Both the same size and both filled, because they are two choices of equal standing at
        /// the bottom of the same card. The progress button used to sit at the end of the member
        /// list instead, a different size and a different weight from the button directly beneath
        /// it, which read as two unrelated controls that happened to be near each other.
        /// </summary>
        private void DrawCardActionRow(RecruitmentSnapshot snap, float left, float right, ref float y)
        {
            float slot = CardActionSlotWidth(snap, left, right, out float leaveLeft);
            DrawProgressActionSlot(snap, left, y, slot);
            DrawLeaveOrDisband(snap, leaveLeft, right, ref y);
        }

        /// <summary>
        /// How wide each button in the card's action row gets, and where the row's second button
        /// starts.
        ///
        /// The row fills the card and splits evenly between whatever is in it. Update progress used
        /// to take a fixed 150px off the left and the rest was pinned to 150 as well, so a card
        /// twice that wide carried two small buttons and a stretch of nothing - and which buttons
        /// there are changes with the state, so the empty stretch changed size as you played.
        /// </summary>
        private float CardActionSlotWidth(RecruitmentSnapshot snap, float left, float right,
            out float secondLeft, bool hasSecondary = false)
        {
            float room = MathF.Max(90f, right - left);
            bool progress = HasCardProgressAction(snap, hasSecondary);

            if (!progress)
            {
                secondLeft = left;
                return room;
            }

            float slot = (room - CardActionGap) * 0.5f;
            secondLeft = left + slot + CardActionGap;
            return slot;
        }

        /// <param name="hasSecondary">Whether the row already carries two actions of its own -
        /// Queue to duty beside Disband. When it does, Update progress is dropped rather than
        /// squeezed in as a third: three buttons across a column this narrow are three labels too
        /// short to read, and the per-player Fetch on each row covers the same ground.</param>
        private bool HasCardProgressAction(RecruitmentSnapshot snap, bool hasSecondary = false)
        {
#if PFP_RATINGS
            return !hasSecondary && HasProgressAction(snap.DutyRowId, PartyPlayers().Count);
#else
            return false;
#endif
        }

        /// <summary>
        /// Draws Update progress at the left of the card's action row, if there is progress to
        /// look up. Returns whether it took the slot, so the caller knows where its own button
        /// starts.
        ///
        /// One helper for both card bodies. Wiring it into only one of them is exactly how it
        /// ended up paired on the idle card and stranded on the recruiting one.
        /// </summary>
        private bool DrawProgressActionSlot(RecruitmentSnapshot snap, float left, float y, float width)
        {
#if PFP_RATINGS
            // Progress lookups only exist in the build that has the rating service behind them.
            ImGui.SetCursorScreenPos(new Vector2(left, y));
            return DrawProgressAction(new Vector2(width, CardButtonH),
                snap.DutyName, snap.DutyRowId, PartyPlayers());
#else
            return false;
#endif
        }

        private void DrawLeaveOrDisband(RecruitmentSnapshot snap, float left, float right, ref float y)
        {
            if (!snap.IsCrossWorldLeader || pfAutomation.IsInDuty())
            {
                DrawCardAction(snap, "Leave Party", left, right, ref y,
                    "Leave this party.", isDestructive: true);
                return;
            }

            DrawCardAction(snap, "Disband Party", left, right, ref y,
                "Break up the party. You lead this cross-world party, so leaving it ends it "
                + "for everyone.", isDestructive: true);
        }

        /// <summary>
        /// The party, as member rows.
        ///
        /// Shared by both card bodies. It used to live inside the listing body only, so an
        /// ordinary party fell through to the standalone panel and the user saw two boxes for one
        /// party.
        ///
        /// There used to be a collapsed form as well - a strip of seat icons behind a chevron -
        /// and the card could be in either. Two layouts for one block meant two heights to keep in
        /// step with the measure pass, and the strip said less than the rows it replaced: no
        /// names, no ratings, no prog. The rows are the party now, always.
        /// </summary>
        private void DrawPartySection(RecruitmentSnapshot snap, float left, float right, ref float y)
        {
            if (!ShowsEmbeddedParty(snap))
                return;

            y += CardSectionGap;

            float sectionH = PartySectionHeight(snap);

#if PFP_RATINGS
            // Clipped to the measured height, so a row that measures short can never draw over
            // the action button below it.
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

            // The card never reads back where ImGui's cursor ended up - it jumps by the measured
            // height instead. That's what stops the rows and the blocks below them fighting over
            // the same space.
            y += sectionH;
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
                //
                // The offer waits for the watcher's one automatic look to be spent. While that is
                // still coming, a button here would be a second way to ask the same question, and
                // pressing it would cost another listing window and another line of the game's
                // "This player is no longer recruiting party members." in chat. So the card says
                // it is looking, and only becomes a button once looking by itself has stopped.
                bool spent = pfAutomation.ListingProbeSpent;

                ClippedText(dl,
                    spent ? "No listing found for this party."
                          : "Checking for a listing...",
                    left, right, y, line, TextMuted);
                y += line + CardRowGap;

                if (spent)
                {
                    DrawCardAction(snap, "Load Details", left, right, ref y,
                        "Check again for the party's listing.\n\n"
                        + "Opens it briefly so its duty, slots and timer can be read.\n"
                        + "Nothing is checked automatically after the first look, so\n"
                        + "this is the way to ask once the leader has posted one.",
                        isDestructive: false);
                }
                return;
            }

            // ── Duty, or whose listing this is ──
            //
            // The header carries the fight whenever there is one to carry, so this line is either
            // the fight (when the header could not take it) or, on somebody else's recruitment,
            // the name of whoever owns it. Never both, and never the same string twice.
            if (!HeaderNamesDuty(snap))
            {
                ClippedText(dl, snap.DutyName, left, right, y, line, AccentBlue);
                y += line;
            }
            else if (ShowsLeaderLine(snap))
            {
                ClippedText(dl, $"{DisplayName(snap.LeaderName)} is recruiting",
                    left, right, y, line, Dim);
                y += line;
            }

            // ── Comment ──
            // Always one line. Wrapping made every block below it move, which is half the reason
            // the card never looked settled.
            var commentLines = WrapCommentInFace(snap.Comment, right - left, CommentMaxLines);
            if (commentLines.Count > 0)
            {
                y += CardRowGap;
                float commentTop = y;

                float commentLine_ = CommentLineHeight();
                using (CommentFont.Push())
                {
                    foreach (string commentLine in commentLines)
                    {
                        ClippedCommentLine(dl, commentLine, left, right, y, commentLine_, TextPrimary);
                        y += commentLine_;
                    }
                }

                // The whole block hovers, not just the last line, and always offers the full text -
                // two lines can still be a truncation.
                if (IsMouseOver(new Vector2(left, commentTop), new Vector2(right, y)))
                    PaddedTooltip(snap.Comment);
            }

            DrawPartySection(snap, left, right, ref y);

            // ── Action ──
            //
            // Only when a listing is actually up. Without one there is nothing to end, and the
            // button was appearing regardless.
            if (!snap.IsRecruiting)
                return;

            // Pinned to the card's own bottom edge rather than flowing after whatever came before,
            // so the gap beneath it is the same whether the party is a strip or a six-row list.
            y = cardBottom - CardPadY - CardButtonH;

            // Update progress leads the row here too. It was only wired into the idle card, so
            // while a listing was actually up - the state this card exists for - the two buttons
            // still sat on different lines at different sizes.
            // Queue to duty is the one state that already needs two buttons of its own, so the
            // row is told about it before it works out how many it is placing.
            bool twoActions = snap.IsLeader && snap.IsPartyFull;
            float slot = CardActionSlotWidth(snap, left, right, out float actionLeft, twoActions);
            DrawProgressActionSlot(snap, left, y, slot);

            // Which action belongs here is a function of state, not of who's leading. The old
            // version offered "End Recruitment" inside a duty, with no party, and at 8 of 8 -
            // none of which have a listing left to end.
            if (snap.IsLeader && snap.IsPartyFull)
            {
                DrawCardAction(snap, "Queue to duty", actionLeft, right, ref y,
                    "Queue for the duty this listing was for.", isDestructive: false,
                    secondary: "Disband", secondaryTooltip: "Break up the party.");
            }
            else if (snap.IsLeader)
            {
                DrawCardAction(snap, "End Recruitment", actionLeft, right, ref y,
                    "Take your Party Finder listing down.", isDestructive: true);
            }
            else
            {
                DrawCardAction(snap, "Leave Party", actionLeft, right, ref y,
                    "Leave this party.", isDestructive: true);
            }
        }

        /// <summary>
        /// The card's footer action. Full width, because a right-aligned button was a fifth
        /// alignment on a card that already had four and never looked deliberate.
        ///
        /// An optional secondary sits beside it, sized to its own label, for the one state that
        /// has two reasonable things to do.
        /// </summary>
        /// <summary>Width of a card action, matching Apply preset in the list below it, and the
        /// gap between two of them.</summary>
        private const float CardActionWidth = 150f;
        private const float CardActionGap = 8f;

        /// <summary>
        /// The mark for a card action, chosen from what the action is called.
        ///
        /// Keyed on the label because the labels are already how this function dispatches its
        /// clicks - see the chain at the bottom of DrawCardAction - and adding a second parameter
        /// that had to be kept in step with the first would be one more thing to get out of step.
        /// </summary>
        private static FontAwesomeIcon ActionIcon(string label) => label switch
        {
            "End Recruitment" => FontAwesomeIcon.StopCircle,
            "Leave Party" => FontAwesomeIcon.SignOutAlt,
            "Disband Party" or "Disband" => FontAwesomeIcon.UserSlash,
            "Queue to duty" => FontAwesomeIcon.Hourglass,
            "Load Details" => FontAwesomeIcon.Search,
            _ => FontAwesomeIcon.Play,
        };

        private void DrawCardAction(RecruitmentSnapshot snap, string label,
            float left, float right, ref float y, string tooltip, bool isDestructive,
            string? secondary = null, string? secondaryTooltip = null)
        {
            // THE ROW OF ACTIONS FILLS THE CARD, SPLIT EVENLY.
            //
            // Both buttons used to be pinned to a fixed 150px so they would match Apply preset in
            // the list beside them - which stopped being a comparison worth making once the card
            // moved into a column of its own. What it left was End Recruitment and Disband huddled
            // against the left edge of a card twice their width, with the rest of the row empty.
            //
            // Equal halves when there are two, the whole width when there is one. They are the
            // card's actions; the card is what they are actions on.
            float gap = secondary != null ? CardActionGap : 0f;
            float room = MathF.Max(90f, right - left);
            float w = secondary != null ? (room - gap) * 0.5f : room;
            float secondaryW = secondary != null ? room - gap - w : 0f;

            ImGui.SetCursorScreenPos(new Vector2(left, y));

            var size = new Vector2(w, CardButtonH);
            bool clicked = DrawActionButton(ActionIcon(label), label, "CardAction", size,
                isDestructive ? ActionStyle.Danger : ActionStyle.Primary);

            if (ImGui.IsItemHovered())
                PaddedTooltip(tooltip);

            if (secondary != null)
            {
                ImGui.SetCursorScreenPos(new Vector2(left + w + gap, y));
                bool second = DrawActionButton(ActionIcon(secondary), secondary, "CardActionSecond",
                    new Vector2(secondaryW, CardButtonH), ActionStyle.Danger);

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
                else if (label == "Disband Party")
                {
                    // Same dialog as every other irreversible action, and the question names the
                    // consequence rather than the mechanic: "break up the party" is what the person
                    // pressing it needs to weigh, not which API call runs.
                    AskConfirm("Disband party", "Break up the party?", "Yes, disband",
                        () => pfAutomation.DisbandParty(),
                        detail: "Everyone is removed at once. This cannot be undone.");
                }
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
            const float radius = Radius.Card;

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

            // The whole edge, not just the top one. A lit top hairline was the old way of saying
            // "raised surface" and it only reads as that from directly above; the same white at the
            // same weight all the way round is what every other card in the plugin wears now.
            dl.AddRect(min, max, ImGui.ColorConvertFloat4ToU32(CardBorder),
                radius, ImDrawFlags.None, 1f);
        }

        /// <summary>
        /// Whether the snapshot carries a duty name worth printing.
        ///
        /// "None" is the game's own answer for a listing with no duty attached, and it arrives as
        /// that literal string rather than as an empty one - so every check for a usable duty name
        /// has to exclude it, and there were three of them written out separately.
        /// </summary>
        private static bool HasRealDutyName(RecruitmentSnapshot snap)
            => !string.IsNullOrWhiteSpace(snap.DutyName)
               && !snap.DutyName.Equals("None", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Whether the header's title already contains the fight's name, so the body knows not to
        /// print it a second time.
        ///
        /// Kept beside HasRealDutyName rather than derived from DescribeActivity's output, because
        /// the measure pass has to ask this BEFORE anything is drawn and DescribeActivity is where
        /// the drawing happens. The one rule they share is the one written here: a leader's own
        /// listing is titled after the fight, somebody else's is titled after them.
        /// </summary>
        private static bool HeaderNamesDuty(RecruitmentSnapshot snap) => HasRealDutyName(snap);

        /// <summary>
        /// Whether the card should say whose listing this is, under the fight's name.
        ///
        /// Only for somebody else's recruitment. On our own the buttons say it - End Recruitment is
        /// not a thing offered to a passenger - and a line reading "you are recruiting" under a card
        /// headed with the fight you are recruiting for is the card talking to itself.
        /// </summary>
        private static bool ShowsLeaderLine(RecruitmentSnapshot snap)
            => snap.IsRecruiting && !snap.IsLeader
               && !string.IsNullOrWhiteSpace(snap.LeaderName);

        private (string Title, Vector4 Color, FontAwesomeIcon Glyph) DescribeActivity(
            RecruitmentSnapshot snap)
        {
            if (snap.IsRecruiting)
            {
                // NAMED, when the name is known.
                //
                // "Your Recruitment" is a label for the card, not information - the card is the
                // only thing on the tab and its buttons already say whose listing it is. What
                // somebody wants off this line is which fight is up, and that was a second line
                // underneath in a different colour while the header spent itself on a heading.
                //
                // The unnamed forms stay for a listing whose duty has not resolved yet, and they
                // still carry leader-or-not, because with no fight to name that is the only thing
                // left worth saying.
                bool named = HasRealDutyName(snap);

                if (snap.IsLeader)
                    return (named ? $"Recruiting for {snap.DutyName}" : "Your Recruitment",
                        AccentGreen, FontAwesomeIcon.Bullhorn);

                // THE FIGHT ON TOP, WHOSE IT IS UNDERNEATH.
                //
                // Both facts belong on this card and the order was the wrong way round: the header
                // is the biggest text on the surface and it was spending that on a stranger's name,
                // with the fight - the thing you are deciding about - relegated to a smaller line
                // below it. Which duty it is decides whether you care at all; whose listing it is
                // matters once you already do.
                //
                // So the header takes the duty here exactly as it does on our own listing, and the
                // leader moves to the line beneath. See DrawLeaderLine.
                return (named ? snap.DutyName : "Party Recruitment",
                    AccentBlue, FontAwesomeIcon.Bullhorn);
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
                string title = HasRealDutyName(snap) ? $"In {snap.DutyName}" : "In a duty";
                return (title, AccentYellow, FontAwesomeIcon.DoorClosed);
            }

            // A PARTY THAT FILLED ITS LISTING IS PREPPING, NOT "READY TO RECRUIT".
            //
            // Outside a duty and outside the queue, a duty name here can only have come from a
            // recruitment this party still stands for - RecruitedDutyStillApplies has already
            // decided that, and it says yes for as long as the party has not broken up. So this is
            // a group that recruited for a fight and has not yet gone and done it.
            //
            // "Ready to recruit" was actively wrong in that state: an 8/8 party cannot recruit,
            // and the line underneath was already saying so. The header announced an option that
            // was not available while the fight's own progress was listed directly beneath it.
            if (snap.Activity is PfActivity.Idle or PfActivity.InPartyNotLeader
                && HasRealDutyName(snap))
            {
                return ($"Prepping for {snap.DutyName}", AccentGreen, FontAwesomeIcon.CheckCircle);
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
