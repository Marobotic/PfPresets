#if PFP_RATINGS
using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Textures.TextureWraps;

namespace PfPresets
{
    /// <summary>
    /// The achievements feed: a column of clears worth celebrating, newest first.
    ///
    /// Laid out as a feed rather than as a table, and the difference is not decoration. A clear is
    /// something one person did, so the card leads with them - name, world, job, then the fight -
    /// and the two things you can do about it sit inside the same border, along the bottom. An
    /// earlier version had them floating under the row on the tab's own background, and they read
    /// as belonging to nothing.
    ///
    /// The column holds its width whatever the window does. A post stretched across a 790px body is
    /// a table row again, and every social feed ever built settled on a fixed column for the same
    /// reason: a name three inches from its own timestamp is two facts, not one.
    /// </summary>
    public partial class PluginUI
    {
        /// <summary>
        /// Breathing room between the feed and the window's own edge.
        ///
        /// The cards take the full width inside it. There was a fixed 524px column for a while,
        /// borrowed from how the web does this, and in a window that is already a narrow panel it
        /// just left two dead gutters and a card too cramped for its own timestamp.
        /// </summary>
        private const float FeedMargin = 14f;

        private const float FeedCardPad = 14f;
        private const float FeedGap = 10f;

        /// <summary>Where the feed goes two cards across. Above the tablet's body and well
        /// above the phone's, so in practice this is "a tablet gets two, a phone gets one".
        /// </summary>
        private const float FeedTwoColumnWidth = 720f;
        private const float FeedIconSize = 44f;
        private const float FeedJobIconSize = 18f;
        private const float FeedActionHeight = 34f;

        /// <summary>The pager's row, reserved out of the list's height when there is more than one
        /// page.</summary>
        /// <summary>
        /// The strip the page numbers live in: the rule, the gap under it, the 30px cells, and a
        /// gutter of air below them.
        ///
        /// Summed rather than guessed at, because this number is BOTH the height reserved off the
        /// bottom of the scroll region and the offset the pager positions itself from. A constant
        /// that only matched one of those is what put the numbers against the bottom edge.
        /// </summary>
        private const float FeedPagerCell = 30f;
        private const float FeedPagerHeight = 1f + Space.Gap + FeedPagerCell + Space.Gutter;

        /// <summary>Gap between the text column and the chip stack on the right, so a long fight
        /// name stops rather than running under a timestamp.</summary>
        private const float FeedRightGutter = 16f;

        /// <summary>
        /// The fight's own art, by the roster's slug.
        ///
        /// A missing file falls back to the section glyph rather than to a gap, so a fight added
        /// next patch draws a crown in a frame until an image is dropped in - which needs no code
        /// change, only a file named after the slug.
        /// </summary>
        private float feedScrollY;
        private bool feedScrollToTop;

        /// <summary>
        /// The fight's own art.
        ///
        /// Tried by the roster's slug first and by the short label second, because the two only
        /// agree for Ultimates. A savage fight's slug is whatever the catalogue calls the boss -
        /// the current tier's last floor is `lindwurm-ii` - so keying on the slug alone left every
        /// tier clear drawing the section's book glyph instead of its art.
        ///
        /// Slug first so a file CAN be exact when it matters: next tier's last floor is still
        /// labelled M4S but is a different boss, and dropping in a file named after its slug
        /// overrides the label's generic one without touching any code.
        /// </summary>
        private IDalamudTextureWrap? FightArt(string slug, string label)
            => ArtNamed(slug) ?? ArtNamed(label);

        private IDalamudTextureWrap? ArtNamed(string name)
        {
            // Validated before it is used to name a resource, because this arrives from the server
            // and the texture cache never forgets a key it has been asked for. Without this, a
            // server sending ten thousand distinct names would grow two dictionaries in this
            // process forever - each miss is cached as "no such image" and never retried.
            if (string.IsNullOrWhiteSpace(name) || name.Length > 32)
                return null;

            foreach (char c in name)
            {
                if (!char.IsAsciiLetterOrDigit(c) && c != '-')
                    return null;
            }

            return EmbeddedTexture($"PfPresets.Data.Icons.bosses.{name.ToLowerInvariant()}.jpg");
        }

        private void DrawAchievementsTab()
        {
            // Gated with the rest of the community half - see the note in TabList. The tab is not
            // in the list at all while opted out, so this is the belt to that braces.
            var ratings = Ratings;
            if (ratings == null || !config.CommunityEnabled)
                return;

            ratings.EnsureFeedLoaded();

            // Being here is what reads the feed - not clicking the tab, and not scrolling to the
            // bottom of it. Somebody who opens the tab, sees the top three posts and leaves has
            // been told what the badge was for, and asking them to scroll before it clears would
            // make the number a chore rather than a notice. See MarkFeedSeen.
            ratings.MarkFeedSeen();

            float avail = ImGui.GetContentRegionAvail().X;
            float width = Math.Max(160f, avail - FeedMargin * 2f);

            ImGui.Indent(FeedMargin);

            // THE TAB'S OWN TOP MARGIN. Every other tab gets this for free from the toolbar at the
            // top of it - the search field is centred in a 64px bar, so there is a gutter of air
            // above the first heading. Clears has no toolbar, so its heading was the first thing in
            // the body and sat hard against the header strip: seven pixels below a divider, and
            // twenty above the card it belongs to.
            ImGui.Dummy(new Vector2(0, Space.Gutter));

            DrawFeedHeader();
            DrawNewPostsPill(ratings, width);

            // Already at the top: nothing to lose your place in, so newer posts just appear. The
            // pill is for somebody who has scrolled away.
            if (ratings.HasNewPosts && feedScrollY <= 2f)
                ratings.ApplyNewPosts();

            var posts = ratings.Feed();

            if (posts.Count == 0)
            {
                DrawFeedEmpty(ratings, width);
            }
            else
            {
                bool paged = ratings.FeedPages > 1;
                // The pager already carries its own bottom gutter, so the list stops exactly
                // where the pager starts - it used to subtract a second margin on top and then
                // lose it again to item spacing.
                float listBottom = paged ? -FeedPagerHeight : -Space.Gutter;

                ImGui.BeginChild("##FeedScroll", new Vector2(width, listBottom), false);
                try
                {
                    feedScrollY = ImGui.GetScrollY();

                    if (feedScrollToTop || ratings.TakeFeedScrollRequest())
                    {
                        ImGui.SetScrollY(0f);
                        feedScrollToTop = false;
                    }

                    // Measured HERE, not outside, because this is the number that knows whether a
                    // scrollbar is taking a slice out of the right-hand side. Measuring outside is
                    // what put every card's timestamp underneath the bar - "Today 12:3" and then
                    // nothing. Card height does not depend on width, so the scrollbar's appearance
                    // cannot feed back into the layout and make it oscillate.
                    float region = ImGui.GetContentRegionAvail().X;

                    // TWO ACROSS ON A TABLET, one on a phone.
                    //
                    // A clear is a short card - a name, a fight, a time, two buttons - and a single
                    // column of them across a 900px body was three lines of text with half the row
                    // empty beside them. Two columns fit twice as many clears on screen without
                    // making any of them wider than they have anything to put in.
                    int columns = region >= FeedTwoColumnWidth ? 2 : 1;
                    float cardWidth = columns == 1
                        ? region
                        : (region - FeedGap) * 0.5f;

                    // Zero spacing for the grid: the row's height is measured from where the
                    // cursor lands after a card, and ImGui's own gap between items would be counted
                    // into it on top of FeedGap - two gaps where the layout intends one.
                    ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, Vector2.Zero);

                    for (int i = 0; i < posts.Count; i += columns)
                    {
                        // The row is as tall as its tallest card, and the next row starts below
                        // that. Laid out by letting each card advance the cursor itself, the second
                        // card of a pair would start where the first one ended.
                        float rowTop = ImGui.GetCursorPosY();
                        float rowHeight = 0f;

                        for (int c = 0; c < columns && i + c < posts.Count; c++)
                        {
                            ImGui.SetCursorPos(new Vector2(c * (cardWidth + FeedGap), rowTop));
                            DrawAchievementCard(posts[i + c], cardWidth);
                            rowHeight = MathF.Max(rowHeight, ImGui.GetCursorPosY() - rowTop);
                        }

                        ImGui.SetCursorPos(new Vector2(0, rowTop + rowHeight + FeedGap));
                    }

                    ImGui.PopStyleVar();
                }
                finally
                {
                    ImGui.EndChild();
                }

                if (paged)
                    DrawFeedPager(ratings, width);
            }

            ImGui.Unindent(FeedMargin);
        }

        /// <summary>
        /// Page numbers, under the list.
        ///
        /// Drawn in the same language as everything else here - flat, ruled, zero rounding, the
        /// current page filled with the accent - rather than as a row of ImGui buttons, which would
        /// be the one place in this tab with chrome nobody else has.
        ///
        /// Only appears when there is a second page. A pager showing "1" is a control that has
        /// never had anything to do.
        /// </summary>
        private void DrawFeedPager(RatingService ratings, float width)
        {
            var dl = ImGui.GetWindowDrawList();

            // PLACED FROM THE BOTTOM, not flowed to it. Flowed, ImGui adds its item spacing after
            // the scroll child and after each spacer in here - a dozen pixels the arithmetic never
            // saw - so the numbers drifted down until they were touching the bottom edge of the
            // window with nothing under them.
            ImGui.SetCursorPosY(ImGui.GetWindowHeight() - FeedPagerHeight);

            Vector2 origin = ImGui.GetCursorScreenPos();

            dl.AddRectFilled(origin, new Vector2(origin.X + width, origin.Y + 1f),
                ImGui.ColorConvertFloat4ToU32(RuleHair));

            int pages = ratings.FeedPages;
            int current = ratings.FeedPage;

            // A window of pages around the current one, so a feed with forty pages does not draw
            // forty buttons.
            int from = Math.Max(0, Math.Min(current - 2, pages - 5));
            int to = Math.Min(pages - 1, Math.Max(current + 2, 4));

            // NUMBERS ONLY. The row used to be bracketed by < and >, which is two more controls
            // for a job the numbers already do - every page they could reach is sitting right
            // there, named, one press away. They also went dead at the ends, so a two-page feed
            // showed four cells and two of them were furniture.
            const float cell = FeedPagerCell;
            const float gap = 4f;
            int shown = to - from + 1;
            float total = (cell + gap) * shown - gap;

            float x = origin.X + Math.Max(0f, (width - total) * 0.5f);
            float y = origin.Y + 1f + Space.Gap;

            for (int p = from; p <= to; p++)
            {
                int target = p;
                x += DrawPagerCell(dl, x, y, cell, (p + 1).ToString(), true, p == current,
                    () => ratings.ShowFeedPage(target)) + gap;
            }

            // The strip's own height, claimed so nothing after it overlaps - there is nothing
            // after it today, and a region that does not account for what it drew is the kind of
            // thing that only shows up when something is added below.
            ImGui.SetCursorScreenPos(origin);
            ImGui.Dummy(new Vector2(width, FeedPagerHeight));
        }

        /// <summary>One square in the pager. Returns its width so the row can be laid out by
        /// walking it.</summary>
        private float DrawPagerCell(ImDrawListPtr dl, float x, float y, float size, string label,
            bool enabled, bool active, Action onClick)
        {
            var min = new Vector2(x, y);
            var max = new Vector2(x + size, y + size);

            ImGui.SetCursorScreenPos(min);
            ImGui.InvisibleButton($"##page{label}{x:F0}", new Vector2(size, size));

            bool hovered = enabled && ImGui.IsItemHovered();
            if (enabled && ImGui.IsItemClicked())
                onClick();

            if (active)
                dl.AddRectFilled(min, max, ImGui.ColorConvertFloat4ToU32(Accent), Radius.Small);
            else if (hovered)
                dl.AddRectFilled(min, max, ImGui.ColorConvertFloat4ToU32(Raised), Radius.Small);

            dl.AddRect(min, max,
                ImGui.ColorConvertFloat4ToU32(active ? Accent : RuleStrong),
                Radius.Small, ImDrawFlags.None, 1f);

            var colour = active ? OnAccent : enabled ? (hovered ? Ink : Dim) : Faint;

            Vector2 text = ImGui.CalcTextSize(label);
            dl.AddText(new Vector2(min.X + (size - text.X) * 0.5f, min.Y + (size - text.Y) * 0.5f),
                ImGui.ColorConvertFloat4ToU32(colour), label);

            return size;
        }

        /// <summary>
        /// The one line above the feed.
        ///
        /// No refresh button. The feed refreshes itself, and a button that says "Refresh" parked
        /// permanently in the corner is a piece of plumbing showing through the floor - nobody
        /// presses refresh on a feed unless it has already failed them. What replaces it appears
        /// only when there is something to press it for: see DrawNewPostsPill.
        /// </summary>
        /// <summary>
        /// The one line above the feed: the plugin's own section heading, at the feed's own width.
        ///
        /// Drawn here rather than through DrawListHeading only because that one rules to the
        /// content region's edge, and the feed keeps a margin the content region does not know
        /// about - so the line ran a margin's width past the cards under it. Same face, same
        /// tracking, same rule; just told where to stop.
        /// </summary>
        private void DrawFeedHeader()
        {
            ImGui.Dummy(new Vector2(0, FeedMargin));

            var dl = ImGui.GetWindowDrawList();
            Vector2 p = ImGui.GetCursorScreenPos();
            float width = ImGui.GetContentRegionAvail().X;

            dl.AddRectFilled(p, new Vector2(p.X + width, p.Y + 2f),
                ImGui.ColorConvertFloat4ToU32(RuleStrong));

            ImGui.Dummy(new Vector2(0, 10));

            using (UiHeadingFont.Push())
            {
                Vector2 at = ImGui.GetCursorScreenPos();
                float used = DrawTrackedCaps(dl, at, "Recent clears", Dim);
                ImGui.Dummy(new Vector2(used, ImGui.GetTextLineHeight()));
            }

            ImGui.Dummy(new Vector2(0, 10));
        }

        /// <summary>
        /// Newer posts, waiting.
        ///
        /// The poll does not replace the list under somebody who is reading it. When it finds
        /// something new it holds it and this appears - press it and the feed takes the newer
        /// posts and goes back to the top, and the pill is gone until the next time. That is the
        /// only refresh control in the tab and it exists only while it has a reason to.
        /// </summary>
        private void DrawNewPostsPill(RatingService ratings, float width)
        {
            if (!ratings.HasNewPosts)
                return;

            var dl = ImGui.GetWindowDrawList();

            const string label = "New clears";
            const float height = 26f;

            float glyph;
            using (pluginInterface.UiBuilder.IconFontHandle.Push())
                glyph = ImGui.CalcTextSize(FontAwesomeIcon.ArrowUp.ToIconString()).X;

            float text;
            using (UiCaptionFont.Push())
                text = ImGui.CalcTextSize(label).X;

            float pillWidth = 14f + glyph + 7f + text + 14f;

            // In the gap between the rule and the list rather than floating over it. Overlapping
            // a scrolling child means fighting it for the pointer, and the twenty pixels the list
            // moves down are worth rather less than a button that reliably takes a click.
            Vector2 origin = ImGui.GetCursorScreenPos();
            float x = origin.X + (width - pillWidth) * 0.5f;

            ImGui.SetCursorScreenPos(new Vector2(x, origin.Y));
            ImGui.InvisibleButton("##feedNewPosts", new Vector2(pillWidth, height));

            bool hovered = ImGui.IsItemHovered();
            if (ImGui.IsItemClicked())
            {
                ratings.ApplyNewPosts();
                feedScrollToTop = true;
            }

            var min = new Vector2(x, origin.Y);
            var max = new Vector2(x + pillWidth, origin.Y + height);

            dl.AddRectFilled(min, max,
                ImGui.ColorConvertFloat4ToU32(hovered ? AccentHover : Accent));

            uint ink = ImGui.ColorConvertFloat4ToU32(OnAccent);
            float midY = origin.Y + height * 0.5f;

            using (pluginInterface.UiBuilder.IconFontHandle.Push())
            {
                string arrow = FontAwesomeIcon.ArrowUp.ToIconString();
                Vector2 gs = ImGui.CalcTextSize(arrow);
                dl.AddText(new Vector2(x + 14f, midY - gs.Y * 0.5f), ink, arrow);
            }

            using (UiCaptionFont.Push())
            {
                Vector2 ts = ImGui.CalcTextSize(label);
                dl.AddText(new Vector2(x + 14f + glyph + 7f, midY - ts.Y * 0.5f), ink, label);
            }

            ImGui.SetCursorScreenPos(new Vector2(origin.X, origin.Y + height + 10f));
        }

        private void DrawFeedEmpty(RatingService ratings, float width)
        {
            string note = ratings.FeedNote
                ?? (ratings.FeedEverLoaded
                    ? "No clears yet. Ultimate and savage tier clears turn up here as people get "
                      + "them - yours and everybody else's."
                    : "Loading...");

            // Unformatted: this is the server's own wording, and ImGui's Text* overloads treat
            // their argument as a format string. A stray percent sign in a message somebody edits
            // months from now should be a stray percent sign, not a crash.
            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + width);
            ImGui.PushStyleColor(ImGuiCol.Text, Faint);
            ImGui.TextUnformatted(note);
            ImGui.PopStyleColor();
            ImGui.PopTextWrapPos();
        }

        /// <summary>
        /// One clear.
        ///
        /// Drawn by hand rather than with ImGui's own widgets because the card is a bordered block
        /// with a divided footer, and getting that out of the layout engine costs more code than
        /// measuring it does. The height is worked out first so the border can be drawn before the
        /// contents that sit on it.
        /// </summary>
        private void DrawAchievementCard(AchievementPost post, float width)
        {
            var dl = ImGui.GetWindowDrawList();
            Vector2 origin = ImGui.GetCursorScreenPos();

            float lines;
            using (UiRowNameFont.Push())
                lines = ImGui.GetTextLineHeight();
            using (UiBodyFont.Push())
                lines += ImGui.GetTextLineHeight() + 4f;

            float bodyHeight = Math.Max(FeedIconSize, lines) + FeedCardPad * 2f;
            float height = bodyHeight + FeedActionHeight + 1f;

            var min = origin;
            var max = new Vector2(origin.X + width, origin.Y + height);

            // A first clear is the one somebody will remember, and the only difference is the
            // ground it sits on plus the chip. No accent edge: the chip already says it, and two
            // marks for one fact is one mark too many.
            dl.AddRectFilled(min, max,
                ImGui.ColorConvertFloat4ToU32(post.IsFirstClear ? Raised : Field), Radius.Card);
            dl.AddRect(min, max, ImGui.ColorConvertFloat4ToU32(CardBorder),
                Radius.Card, ImDrawFlags.None, 1f);

            DrawCardBody(post, min, width, bodyHeight);

            float actionsY = min.Y + bodyHeight;
            dl.AddRectFilled(new Vector2(min.X, actionsY), new Vector2(max.X, actionsY + 1f),
                ImGui.ColorConvertFloat4ToU32(RuleHair));

            DrawCardActions(post, new Vector2(min.X, actionsY + 1f), width);

            // Put the cursor back where the card began before claiming its space. The footer
            // buttons are placed with SetCursorScreenPos, so without this the Dummy below would
            // measure from the action row and every card after the first would sit a footer's
            // height too low - which is the whole feed drifting apart as you scroll.
            ImGui.SetCursorScreenPos(origin);
            ImGui.Dummy(new Vector2(width, height));
        }

        private void DrawCardBody(AchievementPost post, Vector2 min, float width, float bodyHeight)
        {
            var dl = ImGui.GetWindowDrawList();

            float x = min.X + FeedCardPad;
            float centreY = min.Y + bodyHeight * 0.5f;

            // ── The fight's art, framed like every other slot in the plugin ──
            var artMin = new Vector2(x, centreY - FeedIconSize * 0.5f);
            var artMax = new Vector2(artMin.X + FeedIconSize, artMin.Y + FeedIconSize);

            dl.AddRectFilled(artMin, artMax, ImGui.ColorConvertFloat4ToU32(Panel), Radius.Tile);

            var art = FightArt(post.FightSlug, post.FightLabel);
            if (art != null)
            {
                dl.AddImage(art.Handle, artMin, artMax);
            }
            else
            {
                using (pluginInterface.UiBuilder.IconFontHandle.Push())
                {
                    string glyph = (post.Kind == "savage_tier"
                        ? FontAwesomeIcon.Book
                        : FontAwesomeIcon.Crown).ToIconString();

                    Vector2 gs = ImGui.CalcTextSize(glyph);
                    dl.AddText(new Vector2(artMin.X + (FeedIconSize - gs.X) * 0.5f,
                                           artMin.Y + (FeedIconSize - gs.Y) * 0.5f),
                        ImGui.ColorConvertFloat4ToU32(Dim), glyph);
                }
            }

            dl.AddRect(artMin, artMax, ImGui.ColorConvertFloat4ToU32(CardBorder),
                Radius.Tile, ImDrawFlags.None, 1f);

            // ── Measure both lines before drawing either ──
            //
            // Three faces, three jobs: the person at 15, the fight at 13, the world and the clock
            // at 11. All of it measured in its own face first, because a string measured in one
            // font and drawn in another is how text ends up running through the thing beside it.
            float nameH, fightH, smallH;
            float nameW, worldW, whenW;

            string when = LocalClearTime(post.ClearedAt);

            using (UiRowNameFont.Push())
            {
                nameH = ImGui.GetTextLineHeight();
                nameW = ImGui.CalcTextSize(post.Name).X;
            }

            using (UiBodyFont.Push())
                fightH = ImGui.GetTextLineHeight();

            using (UiLabelFont.Push())
            {
                smallH = ImGui.GetTextLineHeight();
                worldW = ImGui.CalcTextSize(post.World).X;
                whenW = ImGui.CalcTextSize(when).X;
            }

            // ── The right edge: the kind, and when ──
            float rightEdge = min.X + width - FeedCardPad;

            float chipHeight = smallH + 8f;
            float stackHeight = chipHeight + 6f + smallH;
            float stackTop = centreY - stackHeight * 0.5f;

            float chipWidth = DrawKindChip(post, rightEdge, stackTop, chipHeight);

            using (UiLabelFont.Push())
                dl.AddText(new Vector2(rightEdge - whenW, stackTop + chipHeight + 6f),
                    ImGui.ColorConvertFloat4ToU32(Faint), when);

            // ── The two text lines ──
            float textX = artMax.X + FeedCardPad;
            float textRight = rightEdge - Math.Max(chipWidth, whenW) - FeedRightGutter;
            float textWidth = Math.Max(60f, textRight - textX);

            float lineGap = 4f;
            float topY = centreY - (nameH + lineGap + fightH) * 0.5f;

            // Line one: who, on what. The job belongs beside the person - it is a fact about them
            // that evening, not about the fight, and every party list in the game reads that way.
            float nameX = textX;

            if (post.Job > 0 && TryGetIconHandle(IconJobBase + post.Job, out var jobHandle))
            {
                float jobY = topY + (nameH - FeedJobIconSize) * 0.5f;
                dl.AddImage(jobHandle, new Vector2(textX, jobY),
                    new Vector2(textX + FeedJobIconSize, jobY + FeedJobIconSize));

                nameX += FeedJobIconSize + 7f;
            }

            float nameRoom = textWidth - (nameX - textX) - worldW - 10f;

            using (UiRowNameFont.Push())
            {
                string name = Truncate(post.Name, nameRoom);
                dl.AddText(new Vector2(nameX, topY), ImGui.ColorConvertFloat4ToU32(Ink), name);
                nameW = ImGui.CalcTextSize(name).X;
            }

            using (UiLabelFont.Push())
                dl.AddText(new Vector2(nameX + nameW + 8f, topY + (nameH - smallH) * 0.5f),
                    ImGui.ColorConvertFloat4ToU32(Faint), post.World);

            // Line two: the fight, by the name people say out loud rather than its initials.
            using (UiBodyFont.Push())
            {
                string title = Truncate(post.Title, textWidth);
                dl.AddText(new Vector2(textX, topY + nameH + lineGap),
                    ImGui.ColorConvertFloat4ToU32(post.IsFirstClear ? Ink : Dim), title);
            }
        }

        /// <summary>The kind chip, right-aligned. Returns its width so the text column knows where
        /// it must stop.</summary>
        private float DrawKindChip(AchievementPost post, float rightEdge, float top, float height)
        {
            if (string.IsNullOrEmpty(post.KindLabel))
                return 0f;

            var dl = ImGui.GetWindowDrawList();

            float width = ChipWidth(post.KindLabel);

            return DrawChip(new Vector2(rightEdge - width, top + (height - ChipHeight) * 0.5f),
                post.KindLabel, post.IsFirstClear ? Accent : Dim, filled: post.IsFirstClear);
        }

        /// <summary>
        /// The footer: heart and share, divided from the body and from each other by hairlines, so
        /// they read as part of the card rather than as two things floating under it.
        /// </summary>
        private void DrawCardActions(AchievementPost post, Vector2 origin, float width)
        {
            var dl = ImGui.GetWindowDrawList();

            ImGui.SetCursorScreenPos(origin);

            // You cannot heart your own clear, so the button does not offer to let you.
            //
            // The server has always refused it - sixteen different people is what verifies an
            // identity, and without that rule the sixteen could be your own posts. But it refuses
            // the way it refuses everything, by answering as though it worked, which meant the
            // heart filled in, sat there, and quietly emptied on the next read. Every heart the
            // feed received on its first day was somebody doing exactly this to their own clear.
            bool mine = IsSelf(post.Identity);

            // THREE STATES, NOT TWO.
            //
            //   yours      - filled, and pressing it takes it back
            //   locked     - filled and dimmed: this connection's heart, cast by another character
            //   available  - empty, and pressing it gives one
            //
            // Locked is drawn as hearted because it is hearted; what it is not is *yours to undo*.
            // Dimming is the whole of the visual difference, and the tooltip carries the rest -
            // an alt that shows an empty heart it cannot fill is the version of this that sends
            // people to the Discord asking why the button is broken.
            bool locked = post.HeartLocked && !post.Hearted;
            bool canPress = !mine && !locked;

            string heartTip =
                mine ? "You can't heart your own clear."
                : locked ? "Already hearted this from another character."
                : post.Hearted ? "Click to remove your heart."
                : string.Empty;

            // Half each, with the divider on the seam. They were sized to their own labels, so
            // "128" and "Share" made a wide button and a narrow one and the divider sat wherever
            // the numbers put it - which moved from card to card as the hearts came in.
            float half = MathF.Floor((width - 1f) * 0.5f);

            FeedActionButton(
                post, "heart", origin, half,
                FontAwesomeIcon.Heart,
                post.Hearts.ToString(),
                post.Hearted || locked ? Accent : Faint,
                out bool heartClicked,
                enabled: canPress,
                tooltip: heartTip,
                corners: ImDrawFlags.RoundCornersBottomLeft);

            if (heartClicked && canPress)
            {
                if (post.Hearted)
                    Ratings?.Unheart(post);
                else
                    Ratings?.Heart(post);
            }

            // The divider between the two.
            float divX = origin.X + half;
            dl.AddRectFilled(new Vector2(divX, origin.Y),
                new Vector2(divX + 1f, origin.Y + FeedActionHeight),
                ImGui.ColorConvertFloat4ToU32(RuleHair));

            FeedActionButton(
                post, "share", new Vector2(divX + 1f, origin.Y), width - half - 1f,
                FontAwesomeIcon.ShareAlt,
                post.Reshared ? "Shared" : "Share",
                post.Reshared ? Faint with { W = 0.5f } : Faint,
                out bool shareClicked,
                corners: ImDrawFlags.RoundCornersBottomRight);

            if (shareClicked && !post.Reshared)
                Ratings?.Share(post);
        }

        /// <summary>One footer button. Returns its width so the next one can be placed after
        /// it.</summary>
        /// <summary>
        /// One of a card's two footer actions, filling the width it is given with its icon and
        /// label centred in it.
        ///
        /// Given a width rather than measuring one, because the two halves of a card's footer have
        /// to match and neither of them can know what the other needs.
        /// </summary>
        /// <param name="corners">Which of this button's corners belong to the card's own outline.
        /// These two sit along the bottom edge, so the outer one of each has to be rounded to the
        /// card's radius or hovering it squares off the card - a highlight is a fill like any
        /// other and takes the shape of whatever it is filling.</param>
        private void FeedActionButton(AchievementPost post, string id, Vector2 origin, float width,
            FontAwesomeIcon icon, string label, Vector4 colour, out bool clicked,
            bool enabled = true, string tooltip = "", ImDrawFlags corners = ImDrawFlags.RoundCornersNone)
        {
            var dl = ImGui.GetWindowDrawList();

            float glyphWidth;
            using (pluginInterface.UiBuilder.IconFontHandle.Push())
                glyphWidth = ImGui.CalcTextSize(icon.ToIconString()).X;

            float labelWidth = string.IsNullOrEmpty(label) ? 0f : ImGui.CalcTextSize(label).X;
            float contentWidth = glyphWidth + (labelWidth > 0 ? 8f + labelWidth : 0f);
            float contentX = origin.X + MathF.Max(FeedCardPad, (width - contentWidth) * 0.5f);

            ImGui.SetCursorScreenPos(origin);
            ImGui.InvisibleButton($"##feed{id}{post.Id}", new Vector2(width, FeedActionHeight));

            // Hover is tracked even when the button is off, because a disabled button is exactly
            // the one that owes an explanation - the highlight stays keyed to `enabled` so it
            // still does not look pressable.
            bool over = ImGui.IsItemHovered();
            bool hovered = enabled && over;
            clicked = enabled && ImGui.IsItemClicked();

            if (over && tooltip.Length > 0)
                PaddedTooltip(tooltip);

            if (!enabled)
                colour = colour with { W = colour.W * 0.45f };

            if (hovered)
            {
                // RAISED, AND HELD OFF THE CARD'S OWN EDGE.
                //
                // The highlight was Field - the colour the card is already painted in - so hovering
                // changed nothing except that the fill covered the card's 1px border along the
                // edges it touches. Nothing lit up; a line went missing, which is a strange thing
                // for a cursor to do and read as damage rather than as feedback.
                //
                // A step lighter than the card says "this is under the pointer", and the fill stops
                // one pixel inside whichever edges are the card's outline so the border it sits
                // against survives. The inner edge, on the divider, is not inset - two buttons that
                // meet at a seam should meet.
                float left = origin.X + (corners.HasFlag(ImDrawFlags.RoundCornersBottomLeft) ? 1f : 0f);
                float right = origin.X + width
                    - (corners.HasFlag(ImDrawFlags.RoundCornersBottomRight) ? 1f : 0f);
                float bottom = origin.Y + FeedActionHeight
                    - (corners == ImDrawFlags.RoundCornersNone ? 0f : 1f);

                dl.AddRectFilled(new Vector2(left, origin.Y), new Vector2(right, bottom),
                    ImGui.ColorConvertFloat4ToU32(Raised),
                    corners == ImDrawFlags.RoundCornersNone ? 0f : Radius.Card - 1f, corners);
            }

            var drawColour = ImGui.ColorConvertFloat4ToU32(hovered ? Ink : colour);
            float centreY = origin.Y + FeedActionHeight * 0.5f;

            using (pluginInterface.UiBuilder.IconFontHandle.Push())
            {
                Vector2 gs = ImGui.CalcTextSize(icon.ToIconString());
                dl.AddText(new Vector2(contentX, centreY - gs.Y * 0.5f),
                    drawColour, icon.ToIconString());
            }

            if (labelWidth > 0)
            {
                Vector2 ls = ImGui.CalcTextSize(label);
                dl.AddText(new Vector2(contentX + glyphWidth + 8f, centreY - ls.Y * 0.5f),
                    drawColour, label);
            }
        }

        /// <summary>
        /// The clear's time, in the reader's own timezone.
        ///
        /// Relative for the last two days because "yesterday" is how people talk about a raid
        /// night, absolute after that because "eleven days ago" is not.
        /// </summary>
        private static string LocalClearTime(DateTime utc)
        {
            DateTime local = DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime();
            DateTime today = DateTime.Now.Date;

            if (local.Date == today)
                return $"Today {local:HH:mm}";

            if (local.Date == today.AddDays(-1))
                return $"Yest. {local:HH:mm}";

            return local.ToString("d MMM HH:mm");
        }

        /// <summary>Cuts a string to fit, with an ellipsis, or returns it whole.</summary>
        private static string Truncate(string text, float maxWidth)
        {
            if (maxWidth <= 0f || string.IsNullOrEmpty(text))
                return text;

            if (ImGui.CalcTextSize(text).X <= maxWidth)
                return text;

            for (int len = text.Length - 1; len > 1; len--)
            {
                string candidate = text[..len] + "...";
                if (ImGui.CalcTextSize(candidate).X <= maxWidth)
                    return candidate;
            }

            return "...";
        }
    }
}
#endif
