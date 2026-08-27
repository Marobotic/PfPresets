#if PFP_RATINGS
using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace PfPresets
{
    /// <summary>
    /// The achievements feed: other people's clears, and the two things you can do about them.
    ///
    /// Everything here reaches our own server and stops there. No provider is involved, nothing is
    /// looked up on anybody's behalf, and the feed is the same rows for everybody - so this is one
    /// of the few parts of the plugin that can poll on a timer without costing anyone anything.
    ///
    /// Posting is the other half, and it is deliberately quiet. Every duty that finishes is offered
    /// to the server, which answers "not worth a post" for almost all of them. The list of what
    /// counts lives there, not here: when a patch adds an Ultimate the feed picks it up without a
    /// plugin update, and a client from six months ago still posts the right things.
    /// </summary>
    internal sealed partial class RatingService
    {
        /// <summary>How often an open tab re-reads the feed. Slow on purpose - a clear is not news
        /// that goes stale in seconds, and every client doing this is a row read on our box.</summary>
        private static readonly TimeSpan FeedPollAfter = TimeSpan.FromMinutes(2);

        private readonly List<AchievementPost> feed = new();

        /// <summary>
        /// Newer posts the poll has fetched but not shown.
        ///
        /// A feed that rewrites itself under somebody mid-read is a feed that loses their place,
        /// so the poll parks what it finds here and the tab offers it. Empty whenever what is on
        /// screen is current, which is nearly always.
        /// </summary>
        private readonly List<AchievementPost> incoming = new();

        private readonly object feedLock = new();

        private DateTime feedReadAt = DateTime.MinValue;
        private int feedInFlight;

        // ── How the feed grows ────────────────────────────────────
        //
        // ONE LIST THAT ONLY GETS LONGER, not a page at a time. The feed used to be numbered, and
        // numbering it was the wrong shape for what it is: a river of other people's clears, read
        // top-down until you lose interest. Nobody wants "page four" of that - page four is not a
        // place, it is however far down you happened to get - and the numbers meant losing your
        // place was one misclick away.
        //
        // The pieces below are what makes appending safe:
        //
        //   feedCursor    The instant the first page was read, in the SERVER's clock. Every page
        //                 after the first is asked for against it, so the read is of the feed as it
        //                 stood then. Without it, a clear posted mid-scroll shifts every row down
        //                 by one and the next page hands back a post already on screen while
        //                 quietly skipping another.
        //   feedNextPage  Which page has not been asked for yet.
        //   feedPagesKnown How many the server said there are, under that cursor.
        //
        // Newer posts are not lost by the cursor - the top-of-feed poll still finds them, and they
        // are offered as the pill exactly as before. Pressing it starts a new list from the top,
        // which is the one moment it is right to throw the accumulated pages away.

        /// <summary>Unix ms in the server's clock, or zero before the first page has landed (and
        /// against a server too old to send one, where paging falls back to plain offsets).</summary>
        private long feedCursor;

        private int feedNextPage;
        private int feedPagesKnown = 1;
        private int feedMoreInFlight;

        /// <summary>Pages and cursor belonging to a read parked behind the pill, promoted with it
        /// in <see cref="ApplyNewPosts"/> - the held list is a different feed from the one on
        /// screen, and its pagination has to travel with it or the first scroll after pressing the
        /// pill would append the old feed's page two to the new feed's page one.</summary>
        private int pendingPages = 1;
        private long pendingCursor;

        /// <summary>Whether there is more feed below what has been handed over. False also while
        /// nothing has loaded at all, so the tab does not offer to extend an empty list.</summary>
        public bool FeedHasMore
        {
            get { lock (feedLock) return feed.Count > 0 && feedNextPage < feedPagesKnown; }
        }

        /// <summary>True while the next page is out, so the foot of the list can say so rather
        /// than ending in a way that looks like the end.</summary>
        public bool FeedLoadingMore => feedMoreInFlight != 0;

        /// <summary>Set when the change came from this client - their own clear, their own share,
        /// their own opt-out. Offering somebody a pill to see a thing they just did themselves
        /// would be absurd, so the next read lands straight on screen.</summary>
        private volatile bool applyNextRead;

        private volatile string? feedNote;
        private DateTime feedNoteUntil = DateTime.MinValue;

        /// <summary>Posts being hearted or shared right now, so a button held down issues one
        /// request rather than one per frame.</summary>
        private readonly ConcurrentDictionary<string, byte> reacting = new();

        /// <summary>True until the first read lands, so the tab can say "loading" once rather than
        /// showing an empty feed and calling it empty.</summary>
        public bool FeedEverLoaded { get; private set; }

        /// <summary>Something to say instead of posts, or null. Expires on its own.</summary>
        public string? FeedNote =>
            DateTime.UtcNow <= feedNoteUntil ? feedNote : null;

        /// <summary>Whether the poll is holding something newer than what is on screen.</summary>
        public bool HasNewPosts
        {
            get { lock (feedLock) return incoming.Count > 0; }
        }

        // ── The unread mark ───────────────────────────────────────
        //
        // What the badge on the tab counts, and the one thing it must never do is lie in the
        // direction of "there is something here" when there is not. Somebody who presses a tab
        // because it said three and finds nothing new stops believing the number, and a number
        // nobody believes is worse than no number - it is a permanent smudge on the navigation.
        //
        // So the rule is: the mark only moves when the feed has actually been PUT IN FRONT OF
        // SOMEBODY. Not when the tab is clicked, not when a poll lands in the background, not on a
        // timer. A read that fails leaves the mark where it was and the badge keeps its word.

        /// <summary>How often the badge asks, while the window is open and the tab is not. Slower
        /// than the feed's own poll: this one runs for every client with the window up, and a clear
        /// that shows up three minutes late is a clear that shows up.</summary>
        private static readonly TimeSpan UnseenPollAfter = TimeSpan.FromMinutes(3);

        private DateTime unseenCheckedAt = DateTime.MinValue;
        private int unseenInFlight;

        /// <summary>Set by the poll when it parks posts nobody has been shown, so the next tick
        /// asks straight away instead of waiting out a window it is already in the middle of.
        /// Volatile: written by the poll's worker, read by the frame.</summary>
        private volatile bool unseenAskNow;

        /// <summary>How many posts have appeared since the mark, as the server last counted
        /// them.</summary>
        public int UnseenCount { get; private set; }

        /// <summary>The count hit the server's ceiling, so the badge says "99+".</summary>
        public bool UnseenCapped { get; private set; }

        /// <summary>
        /// The feed has never been shown to this install.
        ///
        /// Its own state rather than "unseen == everything", because those two want completely
        /// different things drawn: this one is an invitation to go and look at a tab somebody may
        /// not have noticed exists, and a count of every clear ever posted is not that.
        /// </summary>
        public bool FeedNeverSeen => config.AchievementsSeenMark <= 0;

        /// <summary>
        /// Reads the count if it is time to. Safe to call every frame.
        ///
        /// Not called while the feed itself is on screen - the tab in front of them IS the answer,
        /// and paying for a second one would be paying to be told something they can see.
        /// </summary>
        public void EnsureUnseenChecked()
        {
            if (!config.CommunityEnabled)
                return;

            // Nothing to count from. The tab wears a dot in this state and that costs no request:
            // "you have never opened this" is knowable without asking anybody.
            if (FeedNeverSeen)
                return;

            if (!unseenAskNow && DateTime.UtcNow - unseenCheckedAt < UnseenPollAfter)
                return;

            if (Interlocked.CompareExchange(ref unseenInFlight, 1, 0) != 0)
                return;

            unseenAskNow = false;
            unseenCheckedAt = DateTime.UtcNow;
            long since = config.AchievementsSeenMark;

            _ = Task.Run(async () =>
            {
                try
                {
                    var result = await api.GetUnseenAsync(since).ConfigureAwait(false);

                    // A failed count leaves the badge exactly as it was. There is no such thing as
                    // an error state for this: the alternative to a number is no number, and
                    // flickering between them every three minutes on a poor connection would be
                    // the most annoying thing in the plugin.
                    if (!result.IsOk || result.Value == null)
                        return;

                    // The mark may have moved on while this was in flight - they opened the tab.
                    // Anything counted against the old mark is stale by definition, and applying it
                    // would put a badge back on the tab they are sitting on.
                    if (config.AchievementsSeenMark != since)
                        return;

                    UnseenCount = Math.Max(0, result.Value.Count);
                    UnseenCapped = result.Value.More;
                }
                catch (Exception ex)
                {
                    log.Debug($"[Ratings] Unread count failed: {ex.Message}");
                }
                finally
                {
                    Interlocked.Exchange(ref unseenInFlight, 0);
                }
            });
        }

        /// <summary>
        /// Marks the feed read up to whatever the server last handed over.
        ///
        /// Called every frame the tab is on screen, and does nothing on almost all of them. The
        /// mark it takes is the server's, carried on the feed response - so "read up to here" means
        /// the same thing to both ends whatever this machine's clock says.
        /// </summary>
        public void MarkFeedSeen()
        {
            long shown;
            lock (feedLock)
                shown = feedMark;

            // Nothing has been SHOWN yet: the first read is still out, every read has failed, or
            // the only thing that has arrived is parked behind the pill. Either way there is
            // nothing to claim as seen - and the count stands, because it is still true. Zeroing it
            // here was the bug that ate a clear: somebody sitting on the tab while the poll parked
            // a post they never saw had it marked read on their behalf.
            if (shown <= 0 || shown <= config.AchievementsSeenMark)
                return;

            config.AchievementsSeenMark = shown;
            config.Save();

            UnseenCount = 0;
            UnseenCapped = false;

            // The next tick asks fresh rather than sitting on a three-minute-old answer about a
            // mark that no longer exists.
            unseenCheckedAt = DateTime.MinValue;
        }

        /// <summary>
        /// The server's clock as of the last feed that was actually PUT ON SCREEN, in unix ms.
        /// Zero until one is. Guarded by feedLock: written by the poll's worker, read by the frame.
        /// </summary>
        private long feedMark;

        /// <summary>
        /// The mark belonging to posts the poll is holding behind the pill.
        ///
        /// A read that lands while somebody is mid-feed does not replace what they are looking at -
        /// it waits. Its mark has to wait with it, or the act of fetching a post would be what
        /// marks it read, and the clear that was never drawn would leave no badge behind. Promoted
        /// in ApplyNewPosts, which is the moment those posts are genuinely shown.
        /// </summary>
        private long pendingMark;

        /// <summary>Shows what the poll has been holding. The tab scrolls itself back to the top
        /// afterwards - the whole point is that something above you has changed.</summary>
        public void ApplyNewPosts()
        {
            lock (feedLock)
            {
                if (incoming.Count == 0)
                    return;

                feed.Clear();
                feed.AddRange(incoming);
                incoming.Clear();

                // A NEW LIST, so its pagination starts over. Whatever had been scrolled into view
                // belonged to the older feed; keeping those pages and appending the new feed's
                // second page under them would interleave two different reads of the same table.
                feedPagesKnown = pendingPages;
                feedCursor = pendingCursor;
                feedNextPage = 1;

                // These are on screen now, so their mark is finally ours to claim. Pressing the
                // pill is the only thing that turns a held read into a shown one.
                if (pendingMark > feedMark)
                    feedMark = pendingMark;

                pendingMark = 0;
                pendingCursor = 0;
            }
        }

        /// <summary>A snapshot for the frame. Copied under the lock because the poll writes from a
        /// worker thread while the UI reads.</summary>
        public IReadOnlyList<AchievementPost> Feed()
        {
            lock (feedLock)
                return feed.ToArray();
        }

        /// <summary>
        /// Reads the feed if it is time to, which is safe to call every frame.
        ///
        /// Only ever called while the tab is open. A feed nobody is looking at is a feed that does
        /// not need to be fresh.
        /// </summary>
        public void EnsureFeedLoaded()
        {
            // Nothing is asked for while opted out. The tab is gone in that state so this should
            // not be reachable, but the poll is the thing that would keep talking to the server
            // after somebody asked us to stop - so it checks for itself rather than trusting that
            // every caller has already been removed.
            if (!config.CommunityEnabled)
                return;

            if (DateTime.UtcNow - feedReadAt < FeedPollAfter)
                return;

            RefreshFeed();
        }

        /// <summary>
        /// Adds the next page to the bottom of the list.
        ///
        /// Called by the tab as the list nears its own end, so it is safe to call on any frame and
        /// does nothing on nearly all of them. A failed read is silent and leaves everything as it
        /// was: the next scroll asks again, which is a better answer than an error message at the
        /// foot of somebody's feed.
        /// </summary>
        public void LoadMoreFeed()
        {
            if (!config.CommunityEnabled)
                return;

            int page;
            long cursor;

            lock (feedLock)
            {
                if (feed.Count == 0 || feedNextPage >= feedPagesKnown)
                    return;

                page = feedNextPage;
                cursor = feedCursor;
            }

            if (Interlocked.CompareExchange(ref feedMoreInFlight, 1, 0) != 0)
                return;

            _ = Task.Run(async () =>
            {
                try
                {
                    var result = await api.GetFeedAsync(page, cursor).ConfigureAwait(false);

                    if (!result.IsOk || result.Value == null)
                        return;

                    lock (feedLock)
                    {
                        // The list may have been replaced while this was in flight - the pill
                        // pressed, their own clear landing, broadcasting switched off. That read
                        // owns the feed now and this page is of one that no longer exists.
                        if (feedNextPage != page || feedCursor != cursor)
                            return;

                        var posts = result.Value.Posts;

                        // Nothing came back where the count said there would be. Believe what
                        // arrived rather than the arithmetic, and stop asking - otherwise the tab
                        // reaches the bottom, asks, gets nothing, and asks again every frame.
                        if (posts.Count == 0)
                        {
                            feedPagesKnown = page;
                            return;
                        }

                        // Deduped by id. The cursor makes a duplicate unlikely rather than
                        // impossible: a reshare re-ranks a post to the top, which lifts it out of
                        // the page it used to sit in and shuffles everything below it up one.
                        var known = new HashSet<string>(StringComparer.Ordinal);
                        foreach (var p in feed)
                            known.Add(p.Id);

                        foreach (var p in posts)
                        {
                            if (known.Add(p.Id))
                                feed.Add(p);
                        }

                        feedPagesKnown = Math.Max(feedPagesKnown, Math.Max(1, result.Value.Pages));
                        feedNextPage = page + 1;
                    }

                    // NO MARK IS CLAIMED HERE, for the same reason page one used to be the only
                    // page that could claim one: the mark is a time, and reading further DOWN the
                    // feed proves nothing about what has arrived at the top of it.
                }
                catch (Exception ex)
                {
                    log.Debug($"[Ratings] Feed page {page} failed: {ex.Message}");
                }
                finally
                {
                    Interlocked.Exchange(ref feedMoreInFlight, 0);
                }
            });
        }

        /// <summary>Set when a read has replaced the whole list, so it is drawn from the top rather
        /// than at whatever offset the previous one had been left at.</summary>
        private bool feedScrollWanted;

        public bool TakeFeedScrollRequest()
        {
            if (!feedScrollWanted) return false;
            feedScrollWanted = false;
            return true;
        }

        public void RefreshFeed()
        {
            if (Interlocked.CompareExchange(ref feedInFlight, 1, 0) != 0)
                return;

            feedReadAt = DateTime.UtcNow;

            _ = Task.Run(async () =>
            {
                try
                {
                    // ALWAYS THE TOP OF THE FEED, and never against the cursor. This is the read
                    // that answers "has anything new appeared?", and asking it inside the snapshot
                    // the rest of the pages are pinned to would be asking it to answer no.
                    var result = await api.GetFeedAsync(0).ConfigureAwait(false);

                    if (!result.IsOk || result.Value == null)
                    {
                        // Only worth saying once the tab has nothing else to show. A failed poll
                        // behind a feed that is already on screen is invisible and self-correcting.
                        if (!FeedEverLoaded)
                        {
                            feedNote = !string.IsNullOrWhiteSpace(result.Message)
                                ? result.Message
                                : "Couldn't reach the server.";
                            feedNoteUntil = DateTime.UtcNow.AddSeconds(20);
                        }

                        return;
                    }

                    var posts = result.Value.Posts;
                    int pages = Math.Max(1, result.Value.Pages);

                    // BEFORE THE MERGE BELOW, on the posts as they arrived. The announcer wants to
                    // know what the server just said, which is not the same as what survives being
                    // reconciled with whatever is on screen: a read that gets parked behind the pill
                    // has still found the clear, and a client with no window open has no screen for
                    // it to be reconciled against. See RatingService.Announce.cs.
                    ObserveForAnnounce(posts);

                    // The mark this read is entitled to claim - IF its posts end up in front of
                    // somebody. Whether they do is decided below, and the two cases are not the
                    // same: a read that is held behind the pill has shown nobody anything.
                    //
                    // Falls back to this machine's clock against a server that does not send one.
                    // It is the wrong clock, and it is still much better than the alternative:
                    // without a mark the tab wears the never-opened dot forever, and a mark nobody
                    // can clear is a permanent smudge on the navigation. The count endpoint does
                    // not exist on such a server either, so the only thing this fallback decides is
                    // that the dot goes away when they look - which is the whole of what it means.
                    long mark = result.Value.Now > 0
                        ? result.Value.Now
                        : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                    // The cursor every page after the first is read against. Only ever taken from
                    // the server's own clock: the fallback above is fine for a mark, which is only
                    // ever compared against itself, and wrong for this, which the server compares
                    // against its own timestamps. Zero leaves paging on plain offsets, which is
                    // what this did before the cursor existed and is still correct - just no longer
                    // proof against a post arriving mid-scroll.
                    long cursor = result.Value.Now;

                    lock (feedLock)
                    {
                        // Nothing on screen yet, or the same top post. Anything else is held,
                        // because replacing a list somebody is reading loses their place and moves
                        // the thing they were about to press.
                        bool nothingShown = feed.Count == 0;
                        bool sameTop = !nothingShown && posts.Count > 0
                            && string.Equals(posts[0].Id, feed[0].Id, StringComparison.Ordinal);

                        if (nothingShown || applyNextRead)
                        {
                            // A NEW LIST FROM THE TOP. Everything scrolled into view belonged to
                            // the feed being replaced, so the pages start over with it.
                            feed.Clear();
                            feed.AddRange(posts);
                            incoming.Clear();
                            pendingMark = 0;
                            pendingCursor = 0;

                            feedPagesKnown = pages;
                            feedCursor = cursor;
                            feedNextPage = 1;

                            // Only when something was actually displaced. The first read of all has
                            // nothing to scroll back to and would only be fighting a restored
                            // position for no reason.
                            if (!nothingShown)
                                feedScrollWanted = true;

                            // Only ever forward: two reads can land out of order, and taking the
                            // earlier answer's mark would un-see posts already shown.
                            if (mark > feedMark)
                                feedMark = mark;
                        }
                        else if (sameTop)
                        {
                            // NOTHING NEW AT THE TOP, so the pages below it are still the right
                            // pages and are left exactly where they are. What this read is good for
                            // is the counts: hearts move on posts that are already on screen, and
                            // throwing away everything scrolled into view to collect them would be
                            // the pagination bug this rewrite exists to remove, wearing a poll's
                            // clothes.
                            for (int i = 0; i < posts.Count && i < feed.Count; i++)
                                feed[i] = posts[i];

                            // Trusted downward as well as up: a post coming off the feed - somebody
                            // opting out, a removal - genuinely shortens it.
                            feedPagesKnown = Math.Max(pages, feedNextPage);

                            if (mark > feedMark)
                                feedMark = mark;
                        }
                        else
                        {
                            incoming.Clear();
                            incoming.AddRange(posts);

                            // Held, and so are its mark and its pagination - see pendingMark. What
                            // is on screen is still the older feed, and that is all anybody has
                            // been shown.
                            if (mark > pendingMark)
                                pendingMark = mark;

                            pendingPages = pages;
                            pendingCursor = cursor;

                            // We have just learned first-hand that there is something they have not
                            // been shown, so the badge does not sit out the rest of a three-minute
                            // window before finding out. It still asks the server for the number
                            // rather than counting these itself - the client cannot tell whose
                            // clears these are, and their own must not ring a bell.
                            unseenAskNow = true;
                        }

                        applyNextRead = false;
                    }

                    FeedEverLoaded = true;
                    feedNote = null;
                }
                catch (Exception ex)
                {
                    log.Debug($"[Ratings] Feed read failed: {ex.Message}");
                }
                finally
                {
                    Interlocked.Exchange(ref feedInFlight, 0);
                }
            });
        }

        /// <summary>
        /// Hearts a post.
        ///
        /// Applied here the moment it is pressed, because a button that waits on a round trip
        /// before it moves feels broken on a bad connection. What is new is that the server's reply
        /// is now believed: a heart is one per post per connection, so this press can legitimately
        /// be refused - an alt reaching for a heart the main already cast - and the optimistic
        /// state has to be put back when that happens. <see cref="ApplyReaction"/> is where that
        /// lands, for both directions.
        /// </summary>
        public void Heart(AchievementPost post)
        {
            // HeartLocked is somebody else's heart on this connection. The button does not offer
            // it, so this is the second lock rather than the first - worth keeping anyway, because
            // the feed can be re-read between the draw and the click.
            if (post.Hearted || post.HeartLocked || string.IsNullOrEmpty(post.Id))
                return;

            if (!reacting.TryAdd(post.Id + "#heart", 0))
                return;

            post.Hearted = true;
            post.Hearts += 1;

            _ = Task.Run(async () =>
            {
                try
                {
                    var result = await api.HeartAsync(post.Id).ConfigureAwait(false);

                    if (result.IsOk && result.Value != null)
                        ApplyReaction(post, result.Value, optimisticHearted: true);
                }
                catch (Exception ex)
                {
                    log.Debug($"[Ratings] Heart failed: {ex.Message}");
                }
                finally
                {
                    reacting.TryRemove(post.Id + "#heart", out _);
                }
            });
        }

        /// <summary>
        /// Takes back a heart this character cast.
        ///
        /// Only ever the owner's own. A heart from another character on the same connection is
        /// <see cref="AchievementPost.HeartLocked"/>, and the whole point of that flag is that it
        /// cannot be undone from here - otherwise the one-per-connection rule would be trivially
        /// defeated by hearting on the main and unhearting on an alt.
        /// </summary>
        public void Unheart(AchievementPost post)
        {
            if (!post.Hearted || post.HeartLocked || string.IsNullOrEmpty(post.Id))
                return;

            if (!reacting.TryAdd(post.Id + "#heart", 0))
                return;

            post.Hearted = false;
            post.Hearts = Math.Max(0, post.Hearts - 1);

            _ = Task.Run(async () =>
            {
                try
                {
                    var result = await api.UnheartAsync(post.Id).ConfigureAwait(false);

                    if (result.IsOk && result.Value != null)
                        ApplyReaction(post, result.Value, optimisticHearted: false);
                }
                catch (Exception ex)
                {
                    log.Debug($"[Ratings] Unheart failed: {ex.Message}");
                }
                finally
                {
                    reacting.TryRemove(post.Id + "#heart", out _);
                }
            });
        }

        /// <summary>
        /// Reconciles a post with what the heart route actually did.
        ///
        /// The count is only taken when the server sends one - see the note on
        /// <see cref="AchievementReactResponse.Hearts"/>. When it does not, the optimistic ±1 is
        /// left in place and corrected on the next read, which is what the feed did for its whole
        /// first year and is still fine.
        ///
        /// <paramref name="optimisticHearted"/> is what this client already drew, so the count can
        /// be walked back by exactly the one it added when the server disagrees, rather than being
        /// guessed at from a state that has since changed.
        /// </summary>
        private static void ApplyReaction(AchievementPost post, AchievementReactResponse reply,
            bool optimisticHearted)
        {
            post.HeartLocked = reply.HeartLocked;

            if (reply.Hearts is int authoritative)
            {
                post.Hearts = Math.Max(0, authoritative);
                post.Hearted = reply.Hearted;
                return;
            }

            if (reply.Hearted == optimisticHearted)
                return;

            // Refused, and no count to fall back on: undo the ±1 this client applied.
            post.Hearts = Math.Max(0, post.Hearts + (optimisticHearted ? -1 : 1));
            post.Hearted = reply.Hearted;
        }

        /// <summary>
        /// Shares a post: once per post, ever, by anybody.
        ///
        /// Unlike a heart this one has a real answer, because losing the race is a thing that
        /// genuinely happened and the button has to stop offering itself. Marked shared either way
        /// - whether this press did it or somebody else's did, the post has had its share.
        /// </summary>
        public void Share(AchievementPost post)
        {
            if (post.Reshared || string.IsNullOrEmpty(post.Id))
                return;

            if (!reacting.TryAdd(post.Id + "#share", 0))
                return;

            _ = Task.Run(async () =>
            {
                try
                {
                    var result = await api.ShareAsync(post.Id).ConfigureAwait(false);

                    if (result.IsOk)
                    {
                        post.Reshared = true;

                        // A share moves the post to the top, so the order on screen is now wrong.
                        // Re-read rather than shuffle the local copy: the server decides the order
                        // and this is the one action that changes it.
                        applyNextRead = true;
                        feedReadAt = DateTime.MinValue;
                    }
                }
                catch (Exception ex)
                {
                    log.Debug($"[Ratings] Share failed: {ex.Message}");
                }
                finally
                {
                    reacting.TryRemove(post.Id + "#share", out _);
                }
            });
        }

        /// <summary>True while a reaction on this post is out, so the button can rest.</summary>
        public bool Reacting(AchievementPost post)
            => reacting.ContainsKey(post.Id + "#heart") || reacting.ContainsKey(post.Id + "#share");

        // ── The community poll ────────────────────────────────────

        /// <summary>The poll the server is running, or null when there is not one.</summary>
        public async Task<PollResponse?> GetPollAsync()
        {
            var res = await api.GetPollAsync().ConfigureAwait(false);
            return res.IsOk && res.Value is { Question.Length: > 0 } ? res.Value : null;
        }

        /// <summary>
        /// Casts a vote. Returns an empty string on success, or the server's reason.
        ///
        /// The reason is passed through rather than translated here: what a person should be told
        /// about a refusal is a UI decision, and this layer has no business deciding it.
        /// </summary>
        public async Task<string> VotePollAsync(
            string slug, string option, string token, bool identified)
        {
            var res = await api.VotePollAsync(slug, option, token, identified).ConfigureAwait(false);

            if (res.IsOk && res.Value?.Ok == true)
                return string.Empty;

            return res.Value?.Error ?? "unreachable";
        }

        // ── Posting ───────────────────────────────────────────────

        /// <summary>
        /// Offers a finished duty to the feed.
        ///
        /// Called for every completed duty and expected to come back "not posted" for nearly all of
        /// them. Silent in both directions: there is no message for success, because the feed is
        /// the message, and none for failure, because nobody wants an error printed over the fight
        /// they just cleared.
        /// </summary>
        public void PostAchievement(DutyEncounter encounter)
        {
            // NOTHING LEAVES THE MACHINE WHILE OPTED OUT, and this is the call that would break
            // that promise: it sends a duty, its length, and the name and world of everybody who
            // was in it. Checked first and on its own, ahead of the two settings below, because
            // those two are preferences about what to do with the data and this one is about
            // whether we are allowed to have it.
            if (!config.CommunityEnabled)
                return;

            // EITHER system wants this call. Ratings wants the duty on record so a vote can be
            // checked against it; the feed wants the clear. They are separate settings and turning
            // one off must not silently disable the other.
            if (!config.RatingsEnabled && !config.BroadcastAchievements)
                return;

            if (encounter == null)
                return;

            // WIPES ARE REPORTED TOO, and the broadcast setting does not stop it.
            //
            // This call does two jobs. It offers a clear to the feed - which the setting governs,
            // and which the server checks again on its own side - and it files the duty as proof
            // that these people were in a room together, which is what a vote is checked against.
            //
            // A prog night that ends in a wipe is the night people most want to rate each other
            // after, and it produced no record at all while this returned early. Somebody who turns
            // off broadcasting is asking not to be celebrated, not asking to lose the thing that
            // stops strangers voting on them - so neither the clear flag nor that setting is
            // checked here. Both are the server's business, and it checks them.

            string evidence = string.Empty;
            BuildClearEvidence(encounter, ref evidence);

            // No sealed payload means no honest claim to make - a build without the evidence
            // component, or a duty this install cannot vouch for. The server would refuse it, so
            // this does not spend the request finding that out.
            if (string.IsNullOrEmpty(evidence))
                return;

            // QUEUED, NOT SENT. See TickPendingDuties - nothing leaves the machine while the
            // player is in combat, and the sealed payload is built here because this is the moment
            // the encounter is whole.
            lock (pendingDutyGate)
                pendingDuties.Enqueue(evidence);
        }

        // ── Filing a duty, out of combat ──────────────────────────

        /// <summary>
        /// Whether the player is fighting something right now. Supplied by the plugin, which owns
        /// the game's condition flags; the rating service has no business holding them for anything
        /// else. Null - in a build or a test that never sets it - reads as "not in combat", which
        /// is the behaviour this had before the gate existed.
        /// </summary>
        public Func<bool>? InCombat { get; set; }

        private readonly object pendingDutyGate = new();
        private readonly Queue<string> pendingDuties = new();

        /// <summary>True while a send is in flight, so the tick does not start a second one.</summary>
        private bool dutyPostInFlight;

        /// <summary>When the next attempt may happen. Moved forward on a failure so a server that
        /// is down is not hammered once a frame.</summary>
        private DateTime nextDutyPostUtc = DateTime.MinValue;

        private static readonly TimeSpan DutyPostRetryDelay = TimeSpan.FromSeconds(20);

        /// <summary>
        /// Sends one filed duty, if there is one waiting and the player is not fighting.
        ///
        /// COMBAT HAS PRIORITY OVER EVERYTHING HERE. A duty is filed the moment it ends, and a duty
        /// can end with the player still in combat - walking out mid-pull, a wipe that is still
        /// resolving, an alliance raid where one party is fighting while another finishes. Firing a
        /// request there spends network and a thread pool slot on something nobody is waiting for,
        /// during the one part of the game where frame time is the whole experience.
        ///
        /// So the payload waits. It is already sealed, it does not expire in any hurry, and the
        /// player will be out of combat within a minute or two of any of those cases. If combat
        /// starts again while a request is in flight, that request finishes - it is a few hundred
        /// bytes and cancelling it would only mean sending it twice - but nothing new is started
        /// until the fight is over.
        ///
        /// Called every frame from the plugin's framework update. Cheap by design: a lock, a count
        /// and two comparisons on the overwhelmingly common path where there is nothing to send.
        /// </summary>
        public void TickPendingDuties()
        {
            if (dutyPostInFlight || DateTime.UtcNow < nextDutyPostUtc)
                return;

            // Asked before the lock is taken, because it is the cheap question and it is false for
            // almost every frame the plugin is ever running.
            lock (pendingDutyGate)
            {
                if (pendingDuties.Count == 0)
                    return;
            }

            if (InCombat?.Invoke() == true)
                return;

            string evidence;
            lock (pendingDutyGate)
            {
                if (pendingDuties.Count == 0)
                    return;
                evidence = pendingDuties.Dequeue();
            }

            dutyPostInFlight = true;

            _ = Task.Run(async () =>
            {
                try
                {
                    var result = await api.PostAchievementAsync(
                        new AchievementPostRequest { Evidence = evidence }).ConfigureAwait(false);

                    if (result.IsOk && result.Value?.Posted == true)
                    {
                        log.Debug($"[Ratings] Achievement posted: {result.Value.Fight} ({result.Value.Kind})");

                        // Their own clear should be at the top the next time they look.
                        applyNextRead = true;
                        feedReadAt = DateTime.MinValue;

                        // And on their own list, which has just gained a row. Marked rather than
                        // read: the tab may not be open, and a list nobody is looking at can wait
                        // until somebody is - see EnsureMyClearsLoaded.
                        mineReadAt = DateTime.MinValue;
                    }
                    else if (!result.IsOk)
                    {
                        // PUT BACK, NOT DROPPED. A duty that fails to file is a duty no vote out of
                        // it can ever be checked against, which is the failure this whole path
                        // exists to prevent - and the usual reason to fail is the network being
                        // briefly unavailable, which is exactly the case worth retrying.
                        Requeue(evidence);
                    }
                }
                catch (Exception ex)
                {
                    log.Debug($"[Ratings] Achievement post failed: {ex.Message}");
                    Requeue(evidence);
                }
                finally
                {
                    dutyPostInFlight = false;
                }
            });
        }

        private void Requeue(string evidence)
        {
            lock (pendingDutyGate)
                pendingDuties.Enqueue(evidence);

            nextDutyPostUtc = DateTime.UtcNow + DutyPostRetryDelay;
        }

        /// <summary>
        /// Builds the sealed payload for a clear, or leaves it empty.
        ///
        /// The same arrangement as votes: implemented in a file that is not in this repository, and
        /// erased along with every call to it in a build that does not have that file. See
        /// PluginUI.AdminHooks.cs for why partial methods are the mechanism.
        /// </summary>
        partial void BuildClearEvidence(DutyEncounter encounter, ref string evidence);

        // ── The opt-out ───────────────────────────────────────────

        /// <summary>
        /// Changes this character's standing in the rating system.
        ///
        /// The two directions are not symmetric, and deliberately. Opting OUT is a request: hiding
        /// somebody's score is a thing done to the rest of the service, and the queue is where that
        /// gets read. Opting back IN takes effect at once - it only affects the person asking, they
        /// are holding the character in-game while they ask, and making somebody wait on a
        /// moderator to rejoin a system they left would be a poor way to treat a change of mind.
        /// </summary>
        public void RequestOptOut(bool optOut, Action<string> done)
        {
            var me = api.LocalIdentity;
            if (me == null || !me.IsValid)
            {
                done("Log in to a character first.");
                return;
            }

            string evidence = string.Empty;
            BuildSettingEvidence(optOut ? "optout" : "optin", ref evidence);

            if (string.IsNullOrEmpty(evidence))
            {
                done("This build can't change that setting.");
                return;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    var result = await api.RequestOptOutAsync(me, optOut, evidence)
                        .ConfigureAwait(false);

                    if (result.IsOk)
                        Invalidate(me);

                    done(result.IsOk
                        ? string.Empty
                        : (!string.IsNullOrWhiteSpace(result.Message)
                            ? result.Message
                            : "Couldn't reach the server. Try again in a moment."));
                }
                catch (Exception ex)
                {
                    log.Debug($"[Ratings] Opt-out request failed: {ex.Message}");
                    done("Couldn't reach the server. Try again in a moment.");
                }
            });
        }

        /// <summary>
        /// The toggle's real state, which is the server's, not the config file's.
        ///
        /// THE TOGGLE IS AN ENROLMENT STATUS. Three server answers, two positions:
        ///
        ///   opted out            off
        ///   request pending      off
        ///   neither (default)    on
        ///
        /// Pending reads as off because that is what the person asked for. Their request has been
        /// filed and not yet read; showing them a switch that says they are still in - and a tab
        /// full of ratings - until a moderator gets to it would be the plugin disagreeing with
        /// something they already decided.
        ///
        /// Authoritative in BOTH directions now. A fresh install defaults to on, so somebody who
        /// opted out last month must not find themselves quietly back in; and equally, once they
        /// are opted back in the switch has to follow, or it would sit off forever with nothing to
        /// explain why.
        /// </summary>
        public void SyncOptOutSetting()
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    var result = await api.GetOptedOutAsync().ConfigureAwait(false);
                    if (!result.IsOk || result.Value?.Known != true)
                        return;

                    bool shouldBeOn = !result.Value.OptedOut && !result.Value.Pending;

                    if (config.RatingsEnabled != shouldBeOn)
                    {
                        log.Debug($"[Ratings] Server says opted out = {result.Value.OptedOut}, "
                            + $"pending = {result.Value.Pending}; setting the toggle to {shouldBeOn}.");

                        config.RatingsEnabled = shouldBeOn;
                    }
                }
                catch (Exception ex)
                {
                    log.Debug($"[Ratings] Couldn't read the opt-out setting: {ex.Message}");
                }
            });
        }

        /// <summary>How often the settings tab re-reads the enrolment status while it is open. A
        /// moderator's decision lands within a minute of somebody looking, without the tab asking
        /// on every frame it is drawn.</summary>
        private static readonly TimeSpan OptOutSyncAfter = TimeSpan.FromMinutes(1);

        private DateTime optOutSyncedAt = DateTime.MinValue;

        /// <summary>Re-reads the enrolment status if it is time to. Safe to call every frame.</summary>
        public void EnsureOptOutSynced()
        {
            if (DateTime.UtcNow - optOutSyncedAt < OptOutSyncAfter)
                return;

            optOutSyncedAt = DateTime.UtcNow;
            SyncOptOutSetting();
        }

        /// <summary>
        /// Tells the server whether this character's clears may be broadcast.
        ///
        /// The setting is stored locally as well, so the checkbox is right the moment it is
        /// pressed, and it stops posting from this client either way. The server copy is what hides
        /// clears that are already up - which is the part that matters, since turning it off with
        /// yesterday's posts still on the feed would not be honest.
        /// </summary>
        public void PushBroadcastSetting(bool broadcast)
        {
            var me = api.LocalIdentity;
            if (me == null || !me.IsValid)
                return;

            _ = Task.Run(async () =>
            {
                try
                {
                    await api.SetBroadcastAsync(me, broadcast).ConfigureAwait(false);

                    // What is on the feed has changed, whichever way it went.
                    applyNextRead = true;
                    feedReadAt = DateTime.MinValue;
                }
                catch (Exception ex)
                {
                    log.Debug($"[Ratings] Broadcast setting failed: {ex.Message}");
                }
            });
        }
    }
}
#endif
