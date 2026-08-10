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
        /// <summary>How wide a post is, whatever the window is. Narrow windows get the width they
        /// have; nothing is ever wider than this.</summary>
        private const float FeedColumnWidth = 524f;

        /// <summary>Breathing room between the column and the window's own edge.</summary>
        private const float FeedMargin = 12f;

        private const float FeedCardPad = 12f;
        private const float FeedGap = 10f;
        private const float FeedIconSize = 44f;
        private const float FeedJobIconSize = 18f;
        private const float FeedActionHeight = 32f;

        /// <summary>
        /// The fight's own art, by the roster's slug.
        ///
        /// A missing file falls back to the section glyph rather than to a gap, so a fight added
        /// next patch draws a crown in a frame until an image is dropped in - which needs no code
        /// change, only a file named after the slug.
        /// </summary>
        private float feedScrollY;
        private bool feedScrollToTop;

        private IDalamudTextureWrap? FightArt(string slug)
        {
            // Validated before it is used to name a resource, because the slug arrives from the
            // server and the texture cache never forgets a key it has been asked for. Without this,
            // a server sending ten thousand distinct slugs would grow two dictionaries in this
            // process forever - each miss is cached as "no such image" and never retried. The rule
            // is the shape a roster slug actually has, so nothing legitimate is turned away.
            if (string.IsNullOrWhiteSpace(slug) || slug.Length > 32)
                return null;

            foreach (char c in slug)
            {
                if (!char.IsAsciiLetterOrDigit(c) && c != '-')
                    return null;
            }

            return EmbeddedTexture($"PfPresets.Data.Icons.bosses.{slug}.jpg");
        }

        private void DrawAchievementsTab()
        {
            var ratings = Ratings;
            if (!config.RatingsEnabled || ratings == null)
                return;

            ratings.EnsureFeedLoaded();

            float avail = ImGui.GetContentRegionAvail().X;

            // The column never touches the window edge. Without the margin the cards butted
            // straight up against the frame on a narrow window, which read as a rendering fault
            // rather than as a layout.
            float width = Math.Min(FeedColumnWidth, avail - FeedMargin * 2f);
            if (width < 200f)
                width = Math.Max(120f, avail - FeedMargin * 2f);

            float indent = Math.Max(FeedMargin, (avail - width) * 0.5f);

            ImGui.Indent(indent);

            DrawFeedHeader(ratings, width);
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
                ImGui.BeginChild("##FeedScroll", new Vector2(width, -FeedMargin), false);
                try
                {
                    feedScrollY = ImGui.GetScrollY();

                    if (feedScrollToTop)
                    {
                        ImGui.SetScrollY(0f);
                        feedScrollToTop = false;
                    }

                    for (int i = 0; i < posts.Count; i++)
                    {
                        DrawAchievementCard(posts[i], width);

                        if (i < posts.Count - 1)
                            ImGui.Dummy(new Vector2(0, FeedGap));
                    }
                }
                finally
                {
                    ImGui.EndChild();
                }
            }

            ImGui.Unindent(indent);
        }

        /// <summary>
        /// The one line above the feed.
        ///
        /// No refresh button. The feed refreshes itself, and a button that says "Refresh" parked
        /// permanently in the corner is a piece of plumbing showing through the floor - nobody
        /// presses refresh on a feed unless it has already failed them. What replaces it appears
        /// only when there is something to press it for: see DrawNewPostsPill.
        /// </summary>
        private void DrawFeedHeader(RatingService ratings, float width)
        {
            // Room above, so the heading is not welded to the tab strip.
            ImGui.Dummy(new Vector2(0, FeedMargin));

            // The plugin's own section heading - a rule, then tracked caps in the heading face -
            // rather than the one this tab grew for itself. "RECENT CLEARS" was drawn small, purple
            // and untracked while every other heading in the window was large, dim and spaced, and
            // the tab read as though it had been bolted on by somebody else.
            DrawListHeading("Recent clears");
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
                    ? "No clears yet. Yours will show up here."
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
                lines += ImGui.GetTextLineHeight() + 3f;

            float bodyHeight = Math.Max(FeedIconSize, lines) + FeedCardPad * 2f;
            float height = bodyHeight + FeedActionHeight + 1f;

            var min = origin;
            var max = new Vector2(origin.X + width, origin.Y + height);

            // A first clear is the one somebody will remember, and the only difference is the
            // ground it sits on plus the chip. No accent edge: the chip already says it, and two
            // marks for one fact is one mark too many.
            dl.AddRectFilled(min, max,
                ImGui.ColorConvertFloat4ToU32(post.IsFirstClear ? Raised : Panel));
            dl.AddRect(min, max, ImGui.ColorConvertFloat4ToU32(RuleStrong), 0f, 0, 1f);

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

            dl.AddRectFilled(artMin, artMax, ImGui.ColorConvertFloat4ToU32(Field));

            var art = FightArt(post.FightSlug);
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

            dl.AddRect(artMin, artMax, ImGui.ColorConvertFloat4ToU32(BorderControl), 0f, 0, 1f);

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

            float chipHeight = smallH + 7f;
            float stackHeight = chipHeight + 5f + smallH;
            float stackTop = centreY - stackHeight * 0.5f;

            float chipWidth = DrawKindChip(post, rightEdge, stackTop, chipHeight);

            using (UiLabelFont.Push())
                dl.AddText(new Vector2(rightEdge - whenW, stackTop + chipHeight + 5f),
                    ImGui.ColorConvertFloat4ToU32(Faint), when);

            // ── The two text lines ──
            float textX = artMax.X + FeedCardPad;
            float textRight = rightEdge - Math.Max(chipWidth, whenW) - 14f;
            float textWidth = Math.Max(60f, textRight - textX);

            float lineGap = 3f;
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

            string label = post.KindLabel.ToUpperInvariant();

            float textWidth;
            using (UiLabelFont.Push())
                textWidth = ImGui.CalcTextSize(label).X;

            float width = textWidth + 16f;
            var min = new Vector2(rightEdge - width, top);
            var max = new Vector2(rightEdge, top + height);

            if (post.IsFirstClear)
            {
                dl.AddRectFilled(min, max, ImGui.ColorConvertFloat4ToU32(Accent));
            }
            else
            {
                dl.AddRect(min, max, ImGui.ColorConvertFloat4ToU32(BorderControl), 0f, 0, 1f);
            }

            using (UiLabelFont.Push())
            {
                Vector2 ts = ImGui.CalcTextSize(label);
                dl.AddText(new Vector2(min.X + (width - ts.X) * 0.5f,
                                       min.Y + (height - ts.Y) * 0.5f),
                    ImGui.ColorConvertFloat4ToU32(post.IsFirstClear ? OnAccent : Dim), label);
            }

            return width;
        }

        /// <summary>
        /// The footer: heart and share, divided from the body and from each other by hairlines, so
        /// they read as part of the card rather than as two things floating under it.
        /// </summary>
        private void DrawCardActions(AchievementPost post, Vector2 origin, float width)
        {
            var dl = ImGui.GetWindowDrawList();

            ImGui.SetCursorScreenPos(origin);

            float heartWidth = FeedActionButton(
                post, "heart", origin,
                post.Hearted ? FontAwesomeIcon.Heart : FontAwesomeIcon.Heart,
                post.Hearts > 0 ? post.Hearts.ToString() : string.Empty,
                post.Hearted ? Accent : Faint,
                out bool heartClicked);

            if (heartClicked && !post.Hearted)
                Ratings?.Heart(post);

            // The divider between the two.
            float divX = origin.X + heartWidth;
            dl.AddRectFilled(new Vector2(divX, origin.Y),
                new Vector2(divX + 1f, origin.Y + FeedActionHeight),
                ImGui.ColorConvertFloat4ToU32(RuleHair));

            FeedActionButton(
                post, "share", new Vector2(divX + 1f, origin.Y),
                FontAwesomeIcon.ShareAlt,
                post.Reshared ? "Shared" : "Share",
                post.Reshared ? Faint with { W = 0.5f } : Faint,
                out bool shareClicked);

            if (shareClicked && !post.Reshared)
                Ratings?.Share(post);
        }

        /// <summary>One footer button. Returns its width so the next one can be placed after
        /// it.</summary>
        private float FeedActionButton(AchievementPost post, string id, Vector2 origin,
            FontAwesomeIcon icon, string label, Vector4 colour, out bool clicked)
        {
            var dl = ImGui.GetWindowDrawList();

            float glyphWidth;
            using (pluginInterface.UiBuilder.IconFontHandle.Push())
                glyphWidth = ImGui.CalcTextSize(icon.ToIconString()).X;

            float labelWidth = string.IsNullOrEmpty(label) ? 0f : ImGui.CalcTextSize(label).X;
            float width = 12f + glyphWidth + (labelWidth > 0 ? 7f + labelWidth : 0f) + 12f;

            ImGui.SetCursorScreenPos(origin);
            ImGui.InvisibleButton($"##feed{id}{post.Id}", new Vector2(width, FeedActionHeight));

            bool hovered = ImGui.IsItemHovered();
            clicked = ImGui.IsItemClicked();

            if (hovered)
            {
                dl.AddRectFilled(origin, new Vector2(origin.X + width, origin.Y + FeedActionHeight),
                    ImGui.ColorConvertFloat4ToU32(Field));
            }

            var drawColour = ImGui.ColorConvertFloat4ToU32(hovered ? Ink : colour);
            float centreY = origin.Y + FeedActionHeight * 0.5f;

            using (pluginInterface.UiBuilder.IconFontHandle.Push())
            {
                Vector2 gs = ImGui.CalcTextSize(icon.ToIconString());
                dl.AddText(new Vector2(origin.X + 12f, centreY - gs.Y * 0.5f),
                    drawColour, icon.ToIconString());
            }

            if (labelWidth > 0)
            {
                Vector2 ls = ImGui.CalcTextSize(label);
                dl.AddText(new Vector2(origin.X + 12f + glyphWidth + 7f, centreY - ls.Y * 0.5f),
                    drawColour, label);
            }

            return width;
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
