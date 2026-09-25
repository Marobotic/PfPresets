#if PFP_RATINGS
using System;
using System.Collections.Generic;
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
        /// <summary>The portrait slot. Square, because a Lodestone avatar is.</summary>
        private const float FeedIconSize = 44f;

        /// <summary>
        /// How much of a card's right-hand side the fight's own art occupies.
        ///
        /// A fraction rather than a fixed width, because the card is a fraction of the window and a
        /// fixed panel would be a third of a narrow card and a sliver of a wide one. Clamped at both
        /// ends: below the minimum it stops reading as a picture at all, and above the maximum it
        /// starts competing with the name instead of sitting behind it.
        /// </summary>
        private const float FeedArtFraction = 0.42f;
        private const float FeedArtMinWidth = 96f;
        private const float FeedArtMaxWidth = 240f;

        /// <summary>
        /// How strongly the fight's art shows through.
        ///
        /// Low, and it took several passes to believe how low it had to be. The art is a mood - it
        /// says which fight this is at a glance, from across a column - and the card is about a
        /// person. Anything above about a quarter and the boss is what the eye lands on, which is
        /// the arrangement this redesign exists to get away from; the name has to win.
        /// </summary>
        private const float FeedArtAlpha = 0.22f;
        private const float FeedJobIconSize = 18f;
        private const float FeedActionHeight = 34f;

        /// <summary>Gap between the text column and the chip stack on the right, so a long fight
        /// name stops rather than running under a timestamp.</summary>
        private const float FeedRightGutter = 16f;

        /// <summary>
        /// How close to the bottom the list gets before the next page is asked for.
        ///
        /// A whole screen's worth, so the page is already on its way while there is still a
        /// screen of clears to read - which is the difference between a feed that goes on and a
        /// feed that stops at the bottom and then jerks.
        /// </summary>
        private static float FeedLoadMoreMargin => ImGui.GetWindowHeight();

        /// <summary>
        /// The three lists this tab holds.
        ///
        /// It was two - everybody's clears, and your own - and the first of those was carrying two
        /// unlike things at once. A first clear happens once and is the thing somebody will
        /// remember; an Ultimate reclear happens most evenings. Four hundred of the second buried
        /// forty-five of the first, so the clear that mattered was three screens down by morning.
        ///
        /// THE TWO PUBLIC LISTS OVERLAP RATHER THAN PARTITION. A first Ultimate clear is on both,
        /// because it is both things - and a reader who opened "Ultimates" to see Ultimate clears
        /// would be surprised to find the best ones filtered out of it. It is one row on the server
        /// either way, so it carries one heart count between the two appearances; see
        /// RatingService.SyncReaction for the one thing that costs on this side.
        ///
        /// Left to right: Ultimate, First clears, Savage, then your own. The numbers are the tab
        /// positions - the segmented control selects by index - so the order lives here and in the
        /// labels below, and nowhere else. The tab still opens on First clears.
        /// </summary>
        private enum ClearsView
        {
            Ultimate = 0,
            First = 1,
            Savage = 2,
            Mine = 3,
        }

        private ClearsView clearsView = ClearsView.First;

        /// <summary>In <see cref="ClearsView"/>'s own order, and held rather than built - this is
        /// read on every frame the tab is open.</summary>
        private static readonly string[] ClearsViewLabels =
            { "Ultimates", "First clears", "Savage", "My clears" };

        /// <summary>Without "My clears", for somebody who has not had one yet. A separate array
        /// rather than a slice, so neither can be built wrong at a call site.</summary>
        private static readonly string[] ClearsViewLabelsNoMine =
            { "Ultimates", "First clears", "Savage" };

        /// <summary>
        /// Where one of the two lists had got to, and what to do about it on the next frame.
        ///
        /// One per list rather than one shared: they are two different lists at two different
        /// depths, and remembering a single number would put the feed back at wherever "My clears"
        /// happened to be left.
        /// </summary>
        private sealed class PostListScroll
        {
            public float Y;

            /// <summary>Put it back where it was: the tab is being returned to.</summary>
            public bool Restore;

            /// <summary>Take it to the top: the list underneath has been replaced.</summary>
            public bool ToTop;
        }

        private readonly PostListScroll firstScroll = new();
        private readonly PostListScroll savageScroll = new();
        private readonly PostListScroll ultimateScroll = new();
        private readonly PostListScroll mineScroll = new();

        /// <summary>Where the list being drawn keeps its place. One per view, because they are three
        /// different lists at three different depths - remembering a single number would put the
        /// feed back at wherever whichever one was last read happened to be.</summary>
        private PostListScroll ScrollFor(ClearsView view) => view switch
        {
            ClearsView.First => firstScroll,
            ClearsView.Savage => savageScroll,
            ClearsView.Ultimate => ultimateScroll,
            _ => mineScroll,
        };

        /// <summary>Every list's scroll state, so "put them all back where they were" cannot quietly
        /// miss one when a fourth is added.</summary>
        private IEnumerable<PostListScroll> AllScrolls()
        {
            yield return firstScroll;
            yield return savageScroll;
            yield return ultimateScroll;
            yield return mineScroll;
        }

        /// <summary>
        /// The last frame this tab drew, so returning to it can be told apart from staying on it.
        ///
        /// Scroll position is remembered rather than reset, and "remembered" only means anything if
        /// the moment of coming back is identifiable - ImGui hands out no such event, so it is
        /// derived from the frame counter skipping.
        /// </summary>
        private int feedLastFrame = -2;

        /// <summary>
        /// The fight's own art.
        ///
        /// Tried by the roster's slug first and by the short label second. The slug is the fight -
        /// `lindwurm-ii`, `ucob` - and it is what the files are named after, so a fight added next
        /// patch needs an image dropped in and nothing else. A missing file falls back to the
        /// section glyph rather than to a gap.
        ///
        /// The label is only a second chance at a file, and one that fewer and fewer fights can
        /// take: a savage label is the boss's own name now, and "Lindwurm II" is not a resource
        /// name - ArtNamed refuses anything with a space in it. That costs nothing. The slug has
        /// been on every post the feed has ever carried, so there is no fight the label has to
        /// answer for that the slug could not.
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

            // Only the list on screen polls. Three streams that all kept themselves fresh would be
            // three reads every two minutes to draw one of them, and the two nobody is looking at
            // do not need to be right - they are re-read the moment they are asked for.
            //
            // "My clears" is the exception and is loaded whichever view is showing: it is what
            // decides whether the third segment exists at all, and a strip that appears a beat after
            // somebody arrives is a strip that moves under their cursor.
            ratings.EnsureMyClearsLoaded();

            // Coming back to the tab, as opposed to sitting on it. See feedLastFrame.
            int frame = ImGui.GetFrameCount();
            if (frame - feedLastFrame > 1)
            {
                foreach (var s in AllScrolls())
                    s.Restore = true;
            }
            feedLastFrame = frame;

            float avail = ImGui.GetContentRegionAvail().X;
            float width = Math.Max(160f, avail - FeedMargin * 2f);

            ImGui.Indent(FeedMargin);

            // THE TAB'S OWN TOP MARGIN. Every other tab gets this for free from the toolbar at the
            // top of it - the search field is centred in a 64px bar, so there is a gutter of air
            // above the first heading. Clears has no toolbar, so its heading was the first thing in
            // the body and sat hard against the header strip.
            ImGui.Dummy(new Vector2(0, Space.Gutter));

            // THE THIRD SEGMENT DISAPPEARS WHEN THERE IS NOTHING BEHIND IT. A strip advertising a
            // dead end is worse than a shorter strip, and for anybody who has not cleared anything
            // since installing the plugin "My clears" would be one. It arrives with their first
            // clear and not before.
            //
            // The other two are always there. Both are lists of other people's clears and both have
            // something in them from the day the plugin is installed, so neither can be the empty
            // promise this rule exists to avoid.
            bool haveMine = ratings.MyClearsCount > 0;
            if (!haveMine && clearsView == ClearsView.Mine)
                clearsView = ClearsView.First;

            // Same heading as "Your profile" and "Everyone you have met" - one primitive, so the
            // three cannot drift into three sizes.
            DrawListHeading(clearsView switch
            {
                ClearsView.Mine => "My clears",
                ClearsView.Ultimate => "Ultimate clears",
                ClearsView.Savage => "Savage clears",
                _ => "First clears",
            });

            int selected = (int)clearsView;
            var labels = haveMine ? ClearsViewLabels : ClearsViewLabelsNoMine;

            // FITTED, NOT STRETCHED. Three words spread across a 900px body are three words
            // marooned in three enormous boxes - the strip stops reading as one group of related
            // choices and starts reading as three separate buttons that happen to be adjacent.
            // Sized to its own labels, it is a control; `width` is only the ceiling.
            // The Party Finder tab's segmented picker, so the two tabs' pills match.
            if (DrawIosSegmented("clearsview", labels, ref selected, IosSegmentedFitWidth(labels, width)))
            {
                clearsView = (ClearsView)selected;

                // Whichever list is coming back should land where it was left, not at whatever
                // offset another one happens to be sitting at.
                foreach (var s in AllScrolls())
                    s.Restore = true;
            }

            ImGui.Dummy(new Vector2(0, Space.Gap));

            if (clearsView == ClearsView.Mine)
            {
                // NO MARK CLAIMED HERE. The badge counts other people's first clears, and somebody
                // reading their own has been shown none - see the note above MarkFeedSeen about the
                // one thing that number must never do.
                DrawMyClearsList(ratings, width);
                ImGui.Unindent(FeedMargin);
                return;
            }

            bool first = clearsView == ClearsView.First;

            var stream = clearsView switch
            {
                ClearsView.Savage => ratings.SavageClears,
                ClearsView.Ultimate => ratings.UltimateClears,
                _ => ratings.FirstClears,
            };

            var scroll = ScrollFor(clearsView);

            stream.EnsureLoaded();

            // ONLY THE FIRST CLEARS LIST CLAIMS THE MARK, because the badge counts first clears -
            // see the scope on the unseen request. Being HERE is what reads it, not clicking the tab
            // and not scrolling to the bottom: somebody who opens the tab, sees the top three posts
            // and leaves has been told what the badge was for, and asking them to scroll before it
            // clears would make the number a chore rather than a notice.
            if (first)
                ratings.MarkFeedSeen();

            // Already at the top: nothing to lose your place in, so newer posts just appear. The
            // pill is for somebody who has scrolled away, and is drawn inside the list itself so it
            // can float over whatever they are reading - see DrawFloatingNewPostsPill.
            if (stream.HasNewPosts && scroll.Y <= 2f)
                stream.ApplyNewPosts();

            var posts = stream.Posts();

            if (posts.Count == 0)
            {
                DrawFeedEmpty(stream, clearsView, width);
            }
            else
            {
                // The stream asks for the top when it has replaced the list under somebody - their
                // own clear landing, broadcasting switched off. Taken here rather than inside the
                // list so the flag cannot be swallowed by a frame that did not draw.
                if (stream.TakeScrollRequest())
                    scroll.ToTop = true;

                DrawPostList($"##ClearsScroll{clearsView}",
                    posts, width, scroll,
                    stream.HasMore, stream.LoadingMore, stream.LoadMore,
                    clearsView switch
                    {
                        ClearsView.Savage => "That's every savage clear on the feed.",
                        ClearsView.Ultimate => "That's every Ultimate clear on the feed.",
                        _ => "That's every first clear on the feed.",
                    },
                    newPosts: stream.HasNewPosts,
                    onNewPosts: () =>
                    {
                        stream.ApplyNewPosts();

                        // The list underneath has just been replaced with a newer one, so it is read
                        // from the top. Set on this list's own scroll state rather than a shared
                        // flag: the three lists remember their places apart.
                        scroll.ToTop = true;
                    });
            }

            ImGui.Unindent(FeedMargin);
        }

        /// <summary>
        /// Your own clears, as this server recorded them - the same posts the feed carries,
        /// filtered to you.
        ///
        /// NOT THE PROFILE CARD'S CLEARS, which are what Tomestone and FFLogs say you have ever
        /// killed. This is the shorter and more particular list: what the plugin was running for,
        /// checked, and posted. See the header on RatingService.MyClears.cs for why the two are
        /// kept apart rather than merged into one "clears" idea.
        ///
        /// Drawn with the feed's own card. The card already knows what to do with a post of your
        /// own - the heart is not offered, because the server would refuse it - so there is nothing
        /// to special-case here and one renderer to keep right.
        /// </summary>
        private void DrawMyClearsList(RatingService ratings, float width)
        {
            var posts = ratings.MyClears();

            if (posts.Count == 0)
            {
                // Only reachable in the moment between the strip appearing and a read emptying the
                // list - the strip is not drawn at all until there is something behind it.
                using (UiBodyFont.Push())
                    ImGui.TextColored(Faint, "Nothing recorded yet.");
                return;
            }

            DrawPostList("##MyClearsScroll", posts, width, mineScroll,
                ratings.MyClearsHasMore, ratings.MyClearsLoadingMore, ratings.LoadMoreMyClears,
                "That's every clear this server has of yours.");
        }

        /// <summary>
        /// A scrolling column of clear cards that loads more as it is scrolled, and remembers where
        /// it was.
        ///
        /// One implementation for both halves of the tab. They differ only in which list they are
        /// of and what the bottom of it says; everything that was fiddly to get right - the grid's
        /// row heights, when to ask for another page, the three things a scroll position can mean -
        /// is the same problem twice and was worth solving once.
        /// </summary>
        private void DrawPostList(string id, IReadOnlyList<AchievementPost> posts, float width,
            PostListScroll scroll, bool hasMore, bool loadingMore, Action loadMore, string endNote,
            bool newPosts = false, Action? onNewPosts = null)
        {
            ImGui.BeginChild(id, new Vector2(width, -Space.Gutter), false);
            try
            {
                // WHERE THEY WERE, in three cases and this order.
                //
                // A list that has been replaced from the top goes to the top - the whole reason it
                // was replaced is that something above them changed. A list being returned to is
                // put back where it was left, because the pages they scrolled through are still
                // loaded and still say the same thing. Every other frame simply records the
                // position, which is what makes the second case possible at all.
                if (scroll.ToTop)
                {
                    ImGui.SetScrollY(0f);
                    scroll.Y = 0f;
                    scroll.ToTop = false;
                    scroll.Restore = false;
                }
                else if (scroll.Restore)
                {
                    // Clamped by ImGui against the content it can see, so a list that came back
                    // shorter lands at its own bottom rather than past it.
                    ImGui.SetScrollY(scroll.Y);
                    scroll.Restore = false;
                }
                else
                {
                    scroll.Y = ImGui.GetScrollY();
                }

                // Measured HERE, not outside, because this is the number that knows whether a
                // scrollbar is taking a slice out of the right-hand side. Measuring outside is what
                // put every card's timestamp underneath the bar - "Today 12:3" and then nothing.
                // Card height does not depend on width, so the scrollbar's appearance cannot feed
                // back into the layout and make it oscillate.
                float region = ImGui.GetContentRegionAvail().X;

                // TWO ACROSS ON A TABLET, one on a phone.
                //
                // A clear is a short card - a name, a fight, a time, two buttons - and a single
                // column of them across a 900px body was three lines of text with half the row
                // empty beside them. Two columns fit twice as many clears on screen without making
                // any of them wider than they have anything to put in.
                int columns = region >= FeedTwoColumnWidth ? 2 : 1;
                float cardWidth = columns == 1
                    ? region
                    : (region - FeedGap) * 0.5f;

                // Zero spacing for the grid: the row's height is measured from where the cursor
                // lands after a card, and ImGui's own gap between items would be counted into it on
                // top of FeedGap - two gaps where the layout intends one.
                ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, Vector2.Zero);

                for (int i = 0; i < posts.Count; i += columns)
                {
                    // The row is as tall as its tallest card, and the next row starts below that.
                    // Laid out by letting each card advance the cursor itself, the second card of a
                    // pair would start where the first one ended.
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

                DrawListTail(loadingMore, hasMore, posts.Count, endNote, region);

                // ASKED FOR HERE, at the bottom of the list, because this is the only place that
                // knows how much of it is left. Reading the scroll before the cards are laid out
                // would be reading last frame's content height, which on the frame a page lands is
                // exactly one page out of date - and that is the frame this decision is made on.
                //
                // Safe every frame: the load calls keep their own in-flight guard and return
                // immediately once there is nothing more to give.
                if (hasMore && ImGui.GetScrollMaxY() - ImGui.GetScrollY() < FeedLoadMoreMargin)
                    loadMore();

                // LAST, and inside the child. See the note on the pill itself for why both of
                // those are load-bearing rather than tidiness.
                if (newPosts && onNewPosts != null)
                    DrawFloatingNewPostsPill(region, onNewPosts);
            }
            finally
            {
                ImGui.EndChild();
            }

            // AFTER THE CHILD, NOT INSIDE IT. OpenProfile switches the active tab, and doing that
            // while the list it was pressed in is still being drawn leaves ImGui mid-window with a
            // stack that no longer matches what the frame is about to draw. Here it is one frame
            // later in appearance and entirely safe.
            //
            // Covers all four lists - the three feeds and My clears - because every one of them is
            // drawn through here.
            if (feedProfileClick is { } who)
            {
                feedProfileClick = null;
                OpenProfile(who);
            }
        }

        /// <summary>
        /// The foot of a list: whether there is more coming, or that there is not.
        ///
        /// Both lines exist for the same reason. A list that loads as you scroll and then simply
        /// stops gives no way to tell "the end" from "still loading" from "broken", and the reader
        /// is left holding the scroll wheel to find out which. Neither line is a button: there is
        /// nothing to press, because scrolling is the gesture that asks.
        /// </summary>
        private void DrawListTail(bool loadingMore, bool hasMore, int loaded, string endNote,
            float width)
        {
            if (loadingMore)
            {
                DrawListTailNote("Loading more clears...", width);
                return;
            }

            // Only once there has been enough to scroll through. On a list of six the bottom is
            // visible from the top, and announcing it would be telling somebody something they can
            // already see.
            if (!hasMore && loaded > 12)
                DrawListTailNote(endNote, width);
        }

        private void DrawListTailNote(string text, float width)
        {
            ImGui.Dummy(new Vector2(0, Space.Gap));

            using (UiCaptionFont.Push())
            {
                var dl = ImGui.GetWindowDrawList();
                Vector2 at = ImGui.GetCursorScreenPos();
                Vector2 size = ImGui.CalcTextSize(text);

                dl.AddText(new Vector2(at.X + MathF.Max(0f, (width - size.X) * 0.5f), at.Y),
                    ImGui.ColorConvertFloat4ToU32(Faint), text);

                ImGui.Dummy(new Vector2(width, size.Y));
            }

            ImGui.Dummy(new Vector2(0, Space.Gutter));
        }


        /// <summary>
        /// Newer clears, waiting - floating over the top of the list wherever it has been scrolled
        /// to.
        ///
        /// The poll does not replace the list under somebody who is reading it. When it finds
        /// something new it holds it and this appears; press it and the feed takes the newer posts
        /// and goes back to the top, and it is gone until the next time. That is the only refresh
        /// control in the tab and it exists only while it has a reason to.
        ///
        /// DRAWN INSIDE THE SCROLL REGION, LAST, AND PINNED TO THE SCROLL OFFSET. All three matter:
        ///
        ///   inside  - a widget submitted to the parent window cannot be clicked where a child
        ///             window covers it. The child owns the pointer inside its own rectangle, so an
        ///             overlay drawn outside it is decoration that eats presses. That is why the
        ///             earlier version sat in the gap ABOVE the list rather than over it.
        ///   last    - within one window ImGui gives the hover to the most recently submitted item,
        ///             so this takes the click in preference to whatever card is underneath it.
        ///   pinned  - positioned at GetScrollY() rather than at the top of the content, so it
        ///             stays put on screen while the list moves behind it.
        ///
        /// Only ever while scrolled away from the top: at the top there is nothing to lose your
        /// place in, and the tab applies newer posts silently instead - see the caller.
        /// </summary>
        private void DrawFloatingNewPostsPill(float width, Action onPressed)
        {
            var dl = ImGui.GetWindowDrawList();

            const string label = "New clears";
            const float height = 30f;

            float glyph;
            using (pluginInterface.UiBuilder.IconFontHandle.Push())
                glyph = ImGui.CalcTextSize(FontAwesomeIcon.ArrowUp.ToIconString()).X;

            float text;
            using (UiCaptionFont.Push())
                text = ImGui.CalcTextSize(label).X;

            float pillWidth = 16f + glyph + 8f + text + 16f;

            // Where the cursor was, so none of this disturbs the list's own layout.
            Vector2 resume = ImGui.GetCursorPos();

            ImGui.SetCursorPos(new Vector2(
                MathF.Max(0f, (width - pillWidth) * 0.5f),
                ImGui.GetScrollY() + 10f));

            Vector2 min = ImGui.GetCursorScreenPos();
            ImGui.InvisibleButton("##feedNewPosts", new Vector2(pillWidth, height));

            bool hovered = ImGui.IsItemHovered();
            if (ImGui.IsItemClicked())
                onPressed();

            var max = new Vector2(min.X + pillWidth, min.Y + height);
            float corner = height * 0.5f;

            // A capsule with a shadow under it, because it is the one thing on this tab that is
            // floating rather than laid out - and something hovering over a list has to look like
            // it is above the list rather than punched into it.
            dl.AddRectFilled(new Vector2(min.X, min.Y + 2f), new Vector2(max.X, max.Y + 2f),
                ImGui.ColorConvertFloat4ToU32(new Vector4(0f, 0f, 0f, 0.35f)), corner);
            dl.AddRectFilled(min, max,
                ImGui.ColorConvertFloat4ToU32(hovered ? AccentHover : Accent), corner);

            uint ink = ImGui.ColorConvertFloat4ToU32(OnAccent);
            float midY = min.Y + height * 0.5f;

            using (pluginInterface.UiBuilder.IconFontHandle.Push())
            {
                string arrow = FontAwesomeIcon.ArrowUp.ToIconString();
                Vector2 gs = ImGui.CalcTextSize(arrow);
                dl.AddText(new Vector2(min.X + 16f, midY - gs.Y * 0.5f), ink, arrow);
            }

            using (UiCaptionFont.Push())
            {
                Vector2 ts = ImGui.CalcTextSize(label);
                dl.AddText(new Vector2(min.X + 16f + glyph + 8f, midY - ts.Y * 0.5f), ink, label);
            }

            ImGui.SetCursorPos(resume);
        }

        /// <summary>
        /// What a list says when it has nothing in it.
        ///
        /// Three different sentences, because there are three different reasons to be looking at an
        /// empty column and only one of them is "nothing has happened yet". The server's own note
        /// wins when it has one - that is the case where something is actually wrong.
        /// </summary>
        private void DrawFeedEmpty(ClearsFeed stream, ClearsView view, float width)
        {
            string note = stream.Note
                ?? (stream.EverLoaded
                    ? view switch
                    {
                        ClearsView.Savage =>
                            "No savage clears yet. Every clear of the current tier's floors turns up "
                            + "here - first clears and the weekly farm alike.",
                        ClearsView.Ultimate =>
                            "No Ultimate clears yet. They turn up here as people run them.",
                        _ =>
                            "No first clears yet. An Ultimate or a savage floor cleared for the first "
                            + "time turns up here as people get them - yours and everybody else's.",
                    }
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
        /// Somebody whose profile a click on the feed asked for, acted on once the list is closed.
        ///
        /// Recorded rather than acted on where it happens, because opening a profile changes the
        /// active tab and does it from inside the scrolling child the cards are drawn in. The
        /// Ratings tab learned this the hard way with its own search results - see clickedProfile
        /// in PluginUI.Ratings.cs, which is the same pattern for the same reason. Its own field
        /// rather than that one, so two unrelated lists cannot clear each other's click.
        /// </summary>
        private CharacterIdentity? feedProfileClick;

        /// <summary>
        /// A rectangle on a feed card that opens that person's profile in the plugin.
        ///
        /// WHY THE FEED NEEDED THIS. A feed of clears is a feed of people, and the question every
        /// row raises is "who is that" - which the plugin can already answer in full, on the card
        /// the Ratings tab draws. Until now the way to ask it was to read the name off the card,
        /// switch tab, and type it back in, which is the plugin failing to join two things it has
        /// both of.
        ///
        /// THE FACE AND THE NAME, AND NOTHING ELSE. Not the whole card: the fight art, the chip and
        /// the timestamp are not claims about a person, and a card that navigates wherever it is
        /// pressed is a card you cannot read without being taken somewhere. The two things that ARE
        /// the person are the two things that go.
        ///
        /// Draws nothing. Whatever it covers is the affordance, and the caller lights that up from
        /// the hover state this returns.
        /// </summary>
        private bool FeedProfileHotspot(AchievementPost post, string id, Vector2 min, Vector2 max)
        {
            var size = max - min;

            // A card narrow enough to leave no room for a name still draws one, clipped. There is
            // nothing to press at a zero or negative size and ImGui would assert on it.
            if (size.X <= 1f || size.Y <= 1f)
                return false;

            Vector2 resume = ImGui.GetCursorScreenPos();

            ImGui.SetCursorScreenPos(min);
            ImGui.InvisibleButton($"##feedwho{id}{post.Id}", size);

            bool hovered = ImGui.IsItemHovered();

            if (hovered)
            {
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);

                // The abbreviated name, like everywhere else - somebody who has asked not to see
                // full names has not made an exception for tooltips.
                PaddedTooltip($"Open {DisplayName(post.Name)}'s profile");
            }

            if (ImGui.IsItemClicked())
                feedProfileClick = post.Identity;

            // Put back, because the caller is midway through laying a card out and this borrowed
            // the cursor to place a button by hand.
            ImGui.SetCursorScreenPos(resume);

            return hovered;
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

            // The card an announcement was pressed about, for a few seconds after arriving here.
            // A feed is a column of identically shaped rows, and "it is at the top" stops being
            // true the moment anybody else clears - so the thing that was clicked says which one it
            // was rather than leaving the reader to work it out from the timestamps.
            if (IsAnnouncedCard(post))
                dl.AddRect(min, max, ImGui.ColorConvertFloat4ToU32(Accent),
                    Radius.Card, ImDrawFlags.None, 2f);

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

            // ── The fight, behind everything ──
            //
            // Drawn first so every other thing on the card sits on top of it, which is the whole
            // arrangement in one sentence: the fight is the ground, the person is the subject.
            DrawCardArt(post, min, width, bodyHeight);

            // ── Whose clear it is, framed like every other slot in the plugin ──
            //
            // THE PERSON, NOT THE FIGHT. This slot used to hold the boss, and eight reclears of
            // UCOB in an evening drew the same picture of Bahamut eight times - a column of
            // identical rows saying nothing about the eight different people in it. The face is the
            // part that differs between two cards, so the face is what goes where the eye lands.
            var artMin = new Vector2(x, centreY - FeedIconSize * 0.5f);
            var artMax = new Vector2(artMin.X + FeedIconSize, artMin.Y + FeedIconSize);

            // ── The face opens the profile ──
            //
            // Submitted before the tile is painted so the hover state is known in time to light it,
            // and so the card's footer buttons - submitted after - keep their own hit areas. It
            // draws nothing itself: the portrait under it is the affordance.
            bool faceHot = FeedProfileHotspot(post, "face", artMin, artMax);

            dl.AddRectFilled(artMin, artMax, ImGui.ColorConvertFloat4ToU32(Panel), Radius.Tile);

            var portrait = CachedImage(post.Portrait);

            if (portrait != null)
            {
                dl.AddImageRounded(portrait.Handle, artMin, artMax, Vector2.Zero, Vector2.One,
                    0xFFFFFFFF, Radius.Tile, ImDrawFlags.RoundCornersAll);
            }
            else
            {
                // No face: the fight's own art, which is exactly the card this was before portraits
                // existed. A perfectly good card, and a far better fallback than an empty tile -
                // somebody with a hidden Lodestone profile is not a card with a hole in it.
                var fallback = CachedImage(post.Art) ?? FightArt(post.FightSlug, post.FightLabel);

                if (fallback != null)
                {
                    dl.AddImageRounded(fallback.Handle, artMin, artMax, Vector2.Zero, Vector2.One,
                        0xFFFFFFFF, Radius.Tile, ImDrawFlags.RoundCornersAll);
                }
                else
                {
                    using (pluginInterface.UiBuilder.IconFontHandle.Push())
                    {
                        string glyph = (post.IsFirstClear
                            ? FontAwesomeIcon.Crown
                            : FontAwesomeIcon.User).ToIconString();

                        Vector2 gs = ImGui.CalcTextSize(glyph);
                        dl.AddText(new Vector2(artMin.X + (FeedIconSize - gs.X) * 0.5f,
                                               artMin.Y + (FeedIconSize - gs.Y) * 0.5f),
                            ImGui.ColorConvertFloat4ToU32(Dim), glyph);
                    }
                }
            }

            // The frame is the hover state: a ring in the accent colour, over the same rectangle
            // the border already occupies, so nothing moves and nothing is added - the line that is
            // always there simply changes colour and thickens by a pixel.
            dl.AddRect(artMin, artMax,
                ImGui.ColorConvertFloat4ToU32(faceHot ? Accent : CardBorder),
                Radius.Tile, ImDrawFlags.None, faceHot ? 2f : 1f);

            // ── Measure both lines before drawing either ──
            //
            // Three faces, three jobs: the person at 15, the fight at 13, the world and the clock
            // at 11. All of it measured in its own face first, because a string measured in one
            // font and drawn in another is how text ends up running through the thing beside it.
            float nameH, fightH, smallH;
            float nameW, worldW, whenW;

            string when = LocalClearTime(post.ClearedAt);

            // DisplayName, here and everywhere below. A name is measured, truncated, drawn and
            // put in a tooltip on this card, and all four have to be the same string - measuring
            // the full name and drawing an abbreviated one lays the card out for text that is not
            // on it. post.Name stays the real name and is what the profile is opened with.
            string shown = DisplayName(post.Name);

            using (UiRowNameFont.Push())
            {
                nameH = ImGui.GetTextLineHeight();
                nameW = ImGui.CalcTextSize(shown).X;
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

            // Cut to fit and measured BEFORE the hot spot is placed, because the hot spot has to
            // be the width of the text actually drawn - a target sized to the untruncated name
            // reaches out over the fight line beside it.
            string name;
            using (UiRowNameFont.Push())
            {
                name = Truncate(shown, nameRoom);
                nameW = ImGui.CalcTextSize(name).X;
            }

            // The name and the world are one label about one person, so they are one target - a hot
            // spot that stops at the end of the name is a hot spot people miss.
            bool nameHot = FeedProfileHotspot(post, "name",
                new Vector2(nameX, topY),
                new Vector2(nameX + nameW + 8f + worldW, topY + nameH));

            using (UiRowNameFont.Push())
                dl.AddText(new Vector2(nameX, topY),
                    ImGui.ColorConvertFloat4ToU32(nameHot ? Accent : Ink), name);

            // Underlined on hover as well as recoloured. Colour alone is the one cue somebody with
            // a colour deficiency may not get, and this is the card's only navigation.
            if (nameHot)
                dl.AddLine(new Vector2(nameX, topY + nameH - 1f),
                    new Vector2(nameX + nameW, topY + nameH - 1f),
                    ImGui.ColorConvertFloat4ToU32(Accent), 1f);

            using (UiLabelFont.Push())
                dl.AddText(new Vector2(nameX + nameW + 8f, topY + (nameH - smallH) * 0.5f),
                    ImGui.ColorConvertFloat4ToU32(nameHot ? Accent : Faint), post.World);

            // Line two: the fight, by the name people say out loud rather than its initials.
            using (UiBodyFont.Push())
            {
                string title = Truncate(post.Title, textWidth);
                dl.AddText(new Vector2(textX, topY + nameH + lineGap),
                    ImGui.ColorConvertFloat4ToU32(post.IsFirstClear ? Ink : Dim), title);
            }
        }

        /// <summary>
        /// The fight's art, along the card's right edge, faint enough to read over.
        ///
        /// WHY IT IS BACKGROUND NOW. It used to be the 44px tile on the left, and that slot is the
        /// one the eye lands on first - so the loudest thing on a card about a person was a picture
        /// of a boss, repeated identically down a column of eight different people. The picture is
        /// still worth having: it says which fight this is from across the room, before any text is
        /// read. It just is not the subject, so it is drawn like a ground rather than like a
        /// portrait.
        ///
        /// Three things make it recede rather than compete, and all three are needed:
        ///
        ///   FAINT       Tinted to about a fifth. See FeedArtAlpha for how low that had to go.
        ///   FADED IN    A left-to-right gradient of the card's own colour over its left half, so it
        ///               emerges from the card instead of starting at a hard vertical seam - which
        ///               is what it looks like without this, and reads as a rendering fault.
        ///   COVERED     Cropped to fill its panel rather than squashed into it. The art is square
        ///               and the panel is a wide letterbox, so the vertical middle is taken and the
        ///               rest is left out; stretching it instead is instantly visible on anything
        ///               with a face in it.
        ///
        /// Clipped to the card's own top-right corner, and stopping at the divider above the
        /// footer, so the buttons keep their flat ground and their rounded bottom corners.
        /// </summary>
        private void DrawCardArt(AchievementPost post, Vector2 min, float width, float bodyHeight)
        {
            var dl = ImGui.GetWindowDrawList();

            // THE SERVER'S COPY FIRST. It has one for every fight in the roster, including ones this
            // build has never heard of; the embedded set is the fallback for an older server and for
            // the moments before the download lands.
            var art = CachedImage(post.Art) ?? FightArt(post.FightSlug, post.FightLabel);

            float panelWidth = Math.Clamp(width * FeedArtFraction, FeedArtMinWidth, FeedArtMaxWidth);

            // Never wider than the card has room for. On the narrowest card the clamp's minimum
            // could otherwise reach past the portrait and under the name.
            panelWidth = MathF.Min(panelWidth, width - FeedIconSize - FeedCardPad * 3f);
            if (panelWidth < 40f)
                return;

            // HELD ONE PIXEL INSIDE THE CARD'S OWN OUTLINE, on the two edges it touches.
            //
            // The border is drawn before the body, so a panel flush to the card's edge paints over
            // it - and at this alpha the line does not disappear, it goes patchy, which reads as a
            // rendering fault rather than as a design. The same one-pixel inset the footer buttons
            // take against the same border, for the same reason. The bottom edge is not inset: the
            // divider above the footer is drawn afterwards and covers the seam itself.
            float right = min.X + width - 1f;
            var panelMin = new Vector2(right - panelWidth, min.Y + 1f);
            var panelMax = new Vector2(right, min.Y + bodyHeight);

            // COVER, NOT STRETCH. The source is square; the panel is wide and short. Taking a
            // horizontal band out of the middle keeps the aspect ratio, and the middle is where the
            // boss is in every one of these images.
            float band = MathF.Min(1f, (panelMax.Y - panelMin.Y) / panelWidth);
            var uvMin = new Vector2(0f, 0.5f - band * 0.5f);
            var uvMax = new Vector2(1f, 0.5f + band * 0.5f);

            uint tint = ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 1f, FeedArtAlpha));

            if (art != null)
            {
                dl.AddImageRounded(art.Handle, panelMin, panelMax, uvMin, uvMax, tint,
                    Radius.Card - 1f, ImDrawFlags.RoundCornersTopRight);
            }
            else
            {
                // NO ART FOR THIS FIGHT, which is the ordinary case for a savage floor - the
                // shipped set covers the Ultimates and the tier's final boss and nothing else.
                //
                // A glyph rather than nothing. Half a feed of cards with a picture and half with a
                // blank right-hand side does not read as "some art is missing", it reads as the
                // cards being two different designs; one faint mark in the same place keeps the
                // composition whether or not a file exists. Drop a jpg named after the roster slug
                // into Data/Icons/bosses and this branch stops being reached for that fight.
                DrawCardArtGlyph(dl, post, panelMin, panelMax);
            }

            // The fade. Opaque card colour on the left, nothing on the right, over the panel's own
            // left half - so there is no edge where the picture begins.
            //
            // The card under a first clear is Raised rather than Field, so the gradient has to be
            // whichever of the two this card is painted in or the fade ends in a faint vertical
            // band of the wrong grey.
            var ground = post.IsFirstClear ? Raised : Field;
            uint solid = ImGui.ColorConvertFloat4ToU32(ground);
            uint clear = ImGui.ColorConvertFloat4ToU32(ground with { W = 0f });

            float fadeTo = panelMin.X + panelWidth * 0.55f;

            dl.AddRectFilledMultiColor(
                panelMin, new Vector2(fadeTo, panelMax.Y),
                solid, clear, clear, solid);
        }

        /// <summary>
        /// A fight with no art of its own, marked rather than left blank.
        ///
        /// Deliberately faint and deliberately large: it is filling the role the picture would, so
        /// it has to sit at the same depth. A glyph at label size in the corner of the panel would
        /// read as a badge - something with a meaning to work out - rather than as texture.
        /// </summary>
        private void DrawCardArtGlyph(ImDrawListPtr dl, AchievementPost post,
            Vector2 panelMin, Vector2 panelMax)
        {
            float height = panelMax.Y - panelMin.Y;

            using (pluginInterface.UiBuilder.IconFontHandle.Push())
            {
                string mark = (post.IsFirstClear
                    ? FontAwesomeIcon.Crown
                    : FontAwesomeIcon.Dragon).ToIconString();

                Vector2 size = ImGui.CalcTextSize(mark);
                if (size.X <= 0f || size.Y <= 0f)
                    return;

                // Scaled to the panel rather than drawn at the font's own size, so it fills the
                // same area the art would have. ImGui's AddText takes a size, which scales the
                // glyph from the atlas - fine for one large mark at low opacity.
                float scale = (height * 0.72f) / size.Y;
                float fontSize = ImGui.GetFontSize() * scale;

                Vector2 scaled = size * scale;

                dl.AddText(ImGui.GetFont(), fontSize,
                    new Vector2(panelMax.X - scaled.X - FeedCardPad,
                                panelMin.Y + (height - scaled.Y) * 0.5f),
                    ImGui.ColorConvertFloat4ToU32(Ink with { W = FeedArtAlpha * 0.55f }),
                    mark);
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
