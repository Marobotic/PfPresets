#if PFP_RATINGS
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;

namespace PfPresets
{
    /// <summary>
    /// One scrolling list of other people's clears, and everything it takes to keep one honest.
    ///
    /// WHY THERE ARE TWO OF THESE NOW. The feed used to be one list carrying two unlike things. A
    /// first clear happens once and is the thing somebody will remember; an Ultimate reclear happens
    /// most evenings, several times. Mixed together the second buries the first - four hundred
    /// reclears against forty-five firsts - so by morning the clear that mattered was three screens
    /// down among the farm parties. They are two lists now, asked for by scope, and this class is
    /// the one implementation both of them are.
    ///
    /// Everything that was fiddly to get right about the old single feed is in here rather than in
    /// two copies: the cursor that keeps paging stable while the table grows underneath it, the
    /// three things a scroll position can mean, and the rule that a read which lands while somebody
    /// is mid-list waits behind a pill rather than replacing what they are reading.
    ///
    /// EACH KEEPS ITS OWN MARK. The two lists are read at different times and to different depths,
    /// and "I have seen the feed up to here" is a claim about a particular list - so it cannot be
    /// shared between them. The badge on the navigation counts first clears and is cleared by being
    /// shown that list; see RatingService.Achievements.cs.
    ///
    /// NOTHING HERE OBSERVES FOR THE ANNOUNCER, and that is deliberate rather than an omission. The
    /// announcer's mark moves past everything it has looked at, so letting a scoped read feed it
    /// would let a page of Ultimate reclears mark a first clear as accounted for without anybody
    /// ever having seen it. The announcer keeps its own unscoped read - see RefreshForAnnounce.
    /// </summary>
    internal sealed class ClearsFeed
    {
        /// <summary>How often an open tab re-reads its list. Slow on purpose - a clear is not news
        /// that goes stale in seconds, and every client doing this is a row read on our box.</summary>
        private static readonly TimeSpan PollAfter = TimeSpan.FromMinutes(2);

        private readonly PfApiClient api;
        private readonly IPluginLog log;

        /// <summary>Whether the community half is switched on at all. A function rather than a
        /// captured bool: the setting changes under this, and a list that kept polling after
        /// somebody opted out would be the one thing in the plugin still talking to the server
        /// after being asked to stop.</summary>
        private readonly Func<bool> enabled;

        /// <summary>"first" | "ultimate". Sent on every read, and the whole of what makes two of
        /// these two different lists.</summary>
        private readonly string scope;

        /// <summary>For log lines, so two streams failing are distinguishable in a log where they
        /// would otherwise both say "feed read failed".</summary>
        private readonly string name;

        private readonly List<AchievementPost> posts = new();

        /// <summary>
        /// Newer posts the poll has fetched but not shown.
        ///
        /// A list that rewrites itself under somebody mid-read is a list that loses their place, so
        /// the poll parks what it finds here and the tab offers it. Empty whenever what is on screen
        /// is current, which is nearly always.
        /// </summary>
        private readonly List<AchievementPost> incoming = new();

        private readonly object gate = new();

        private DateTime readAt = DateTime.MinValue;
        private int inFlight;
        private int moreInFlight;

        // ── How the list grows ────────────────────────────────────
        //
        // ONE LIST THAT ONLY GETS LONGER, not a page at a time. It is a river of other people's
        // clears, read top-down until you lose interest; page four is not a place, it is however far
        // down you happened to get, and losing that was one misclick away when it was numbered.
        //
        //   cursor      The instant the first page was read, in the SERVER's clock. Every page after
        //               it is asked for against that instant, so the read is of the list as it stood
        //               then. Without it, a clear posted mid-scroll shifts every row down by one and
        //               the next page hands back a post already on screen while quietly skipping
        //               another.
        //   nextPage    Which page has not been asked for yet.
        //   pagesKnown  How many the server said there are, under that cursor.
        //
        // Newer posts are not lost by the cursor - the top-of-list poll still finds them and offers
        // them as the pill. Pressing it starts a new list from the top, which is the one moment it
        // is right to throw the accumulated pages away.

        private long cursor;
        private int nextPage;
        private int pagesKnown = 1;

        /// <summary>Pages and cursor belonging to a read parked behind the pill, promoted with it in
        /// <see cref="ApplyNewPosts"/> - the held list is a different list from the one on screen,
        /// and its pagination has to travel with it or the first scroll after pressing the pill
        /// would append the old list's page two to the new list's page one.</summary>
        private int pendingPages = 1;
        private long pendingCursor;

        /// <summary>The server's clock as of the last read actually PUT ON SCREEN, in unix ms. Zero
        /// until one is. Guarded by the gate: written by the poll's worker, read by the frame.</summary>
        private long mark;

        /// <summary>
        /// The mark belonging to posts the poll is holding behind the pill.
        ///
        /// A read that lands while somebody is mid-list does not replace what they are looking at -
        /// it waits, and its mark has to wait with it. Otherwise the act of FETCHING a post would be
        /// what marks it read, and a clear that was never drawn would leave no badge behind.
        /// </summary>
        private long pendingMark;

        private bool scrollWanted;

        /// <summary>Set when the change came from this client - their own clear, their own share,
        /// pressing an announcement. Offering somebody a pill to see a thing they just did
        /// themselves would be absurd, so the next read lands straight on screen.</summary>
        private volatile bool applyNextRead;

        private volatile string? note;
        private DateTime noteUntil = DateTime.MinValue;

        /// <summary>
        /// Raised when a read is parked behind the pill: this list has just learned first-hand that
        /// there is something nobody has been shown.
        ///
        /// The badge uses it to ask for its number now rather than sitting out the rest of a
        /// three-minute window it is already halfway through. It is a nudge and not the count - this
        /// client cannot tell whose clears these are, and the reader's own must never ring a bell,
        /// so the number still comes from the server.
        ///
        /// Called on a worker thread, outside the gate.
        /// </summary>
        public Action? HeldNewPosts { get; set; }

        public ClearsFeed(PfApiClient api, IPluginLog log, Func<bool> enabled,
            string scope, string name)
        {
            this.api = api;
            this.log = log;
            this.enabled = enabled;
            this.scope = scope;
            this.name = name;
        }

        /// <summary>True until the first read lands, so the tab can say "loading" once rather than
        /// showing an empty list and calling it empty.</summary>
        public bool EverLoaded { get; private set; }

        /// <summary>Something to say instead of posts, or null. Expires on its own.</summary>
        public string? Note => DateTime.UtcNow <= noteUntil ? note : null;

        /// <summary>Whether there is more below what has been handed over. False also while nothing
        /// has loaded at all, so the tab does not offer to extend an empty list.</summary>
        public bool HasMore
        {
            get { lock (gate) return posts.Count > 0 && nextPage < pagesKnown; }
        }

        /// <summary>True while the next page is out, so the foot of the list can say so rather than
        /// ending in a way that looks like the end.</summary>
        public bool LoadingMore => moreInFlight != 0;

        /// <summary>Whether the poll is holding something newer than what is on screen.</summary>
        public bool HasNewPosts
        {
            get { lock (gate) return incoming.Count > 0; }
        }

        /// <summary>The server's clock as of the newest read this list has actually shown somebody,
        /// or zero. What the unread mark is taken from - see MarkFeedSeen.</summary>
        public long ShownMark
        {
            get { lock (gate) return mark; }
        }

        /// <summary>A snapshot for the frame. Copied under the gate because the poll writes from a
        /// worker thread while the UI reads.</summary>
        public IReadOnlyList<AchievementPost> Posts()
        {
            lock (gate) return posts.ToArray();
        }

        /// <summary>Set when a read has replaced the whole list, so it is drawn from the top rather
        /// than at whatever offset the previous one was left at.</summary>
        public bool TakeScrollRequest()
        {
            lock (gate)
            {
                if (!scrollWanted) return false;
                scrollWanted = false;
                return true;
            }
        }

        /// <summary>Shows what the poll has been holding. The tab scrolls itself back to the top
        /// afterwards - the whole point is that something above them has changed.</summary>
        public void ApplyNewPosts()
        {
            lock (gate)
            {
                if (incoming.Count == 0)
                    return;

                posts.Clear();
                posts.AddRange(incoming);
                incoming.Clear();

                // A NEW LIST, so its pagination starts over. Whatever had been scrolled into view
                // belonged to the older list; keeping those pages and appending the new list's
                // second page under them would interleave two different reads of the same table.
                pagesKnown = pendingPages;
                cursor = pendingCursor;
                nextPage = 1;

                // These are on screen now, so their mark is finally ours to claim. Pressing the
                // pill is the only thing that turns a held read into a shown one.
                if (pendingMark > mark)
                    mark = pendingMark;

                pendingMark = 0;
                pendingCursor = 0;
            }
        }

        /// <summary>
        /// Reads the list if it is time to, which is safe to call every frame.
        ///
        /// Only ever called while this list is the one on screen. A list nobody is looking at is a
        /// list that does not need to be fresh - which is why the two streams cost one poll between
        /// them rather than two, whichever tab is open.
        /// </summary>
        public void EnsureLoaded()
        {
            // Nothing is asked for while opted out. The tab is gone in that state so this should not
            // be reachable, but the poll is the thing that would keep talking to the server after
            // somebody asked us to stop - so it checks for itself rather than trusting that every
            // caller has already been removed.
            if (!enabled())
                return;

            if (DateTime.UtcNow - readAt < PollAfter)
                return;

            Refresh();
        }

        /// <summary>Asks for the top of the list now, past the poll's own throttle, and puts the
        /// answer straight on screen rather than behind a pill. What pressing an announcement
        /// does.</summary>
        public void Reveal()
        {
            ApplyNewPosts();
            applyNextRead = true;
            readAt = DateTime.MinValue;
            Refresh();
        }

        /// <summary>
        /// Adds the next page to the bottom.
        ///
        /// Called by the tab as the list nears its own end, so it is safe to call on any frame and
        /// does nothing on nearly all of them. A failed read is silent and leaves everything as it
        /// was: the next scroll asks again, which is a better answer than an error message at the
        /// foot of somebody's feed.
        /// </summary>
        public void LoadMore()
        {
            if (!enabled())
                return;

            int page;
            long at;

            lock (gate)
            {
                if (posts.Count == 0 || nextPage >= pagesKnown)
                    return;

                page = nextPage;
                at = cursor;
            }

            if (Interlocked.CompareExchange(ref moreInFlight, 1, 0) != 0)
                return;

            _ = Task.Run(async () =>
            {
                try
                {
                    var result = await api.GetFeedAsync(page, at, scope).ConfigureAwait(false);

                    if (!result.IsOk || result.Value == null)
                        return;

                    lock (gate)
                    {
                        // The list may have been replaced while this was in flight - the pill
                        // pressed, their own clear landing, broadcasting switched off. That read
                        // owns the list now and this page is of one that no longer exists.
                        if (nextPage != page || cursor != at)
                            return;

                        var arrived = result.Value.Posts;

                        // Nothing came back where the count said there would be. Believe what
                        // arrived rather than the arithmetic, and stop asking - otherwise the tab
                        // reaches the bottom, asks, gets nothing, and asks again every frame.
                        if (arrived.Count == 0)
                        {
                            pagesKnown = page;
                            return;
                        }

                        // Deduped by id. The cursor makes a duplicate unlikely rather than
                        // impossible: a reshare re-ranks a post to the top, which lifts it out of
                        // the page it used to sit in and shuffles everything below it up one.
                        var known = new HashSet<string>(StringComparer.Ordinal);
                        foreach (var p in posts)
                            known.Add(p.Id);

                        foreach (var p in arrived)
                        {
                            if (known.Add(p.Id))
                                posts.Add(p);
                        }

                        pagesKnown = Math.Max(pagesKnown, Math.Max(1, result.Value.Pages));
                        nextPage = page + 1;
                    }

                    // NO MARK IS CLAIMED HERE. The mark is a time, and reading further DOWN a list
                    // proves nothing about what has arrived at the top of it.
                }
                catch (Exception ex)
                {
                    log.Debug($"[Ratings] {name} page {page} failed: {ex.Message}");
                }
                finally
                {
                    Interlocked.Exchange(ref moreInFlight, 0);
                }
            });
        }

        /// <summary>
        /// Reads the top of the list and decides what to do with the answer.
        ///
        /// Three outcomes, and which one happens is the whole of this method's judgement: land it on
        /// screen, fold the counts into what is already there, or hold it behind the pill.
        /// </summary>
        public void Refresh()
        {
            if (Interlocked.CompareExchange(ref inFlight, 1, 0) != 0)
                return;

            readAt = DateTime.UtcNow;

            _ = Task.Run(async () =>
            {
                try
                {
                    // ALWAYS THE TOP, and never against the cursor. This is the read that answers
                    // "has anything new appeared?", and asking it inside the snapshot the rest of
                    // the pages are pinned to would be asking it to answer no.
                    var result = await api.GetFeedAsync(0, 0, scope).ConfigureAwait(false);

                    if (!result.IsOk || result.Value == null)
                    {
                        // Only worth saying once the tab has nothing else to show. A failed poll
                        // behind a list that is already on screen is invisible and self-correcting.
                        if (!EverLoaded)
                        {
                            note = !string.IsNullOrWhiteSpace(result.Message)
                                ? result.Message
                                : "Couldn't reach the server.";
                            noteUntil = DateTime.UtcNow.AddSeconds(20);
                        }

                        return;
                    }

                    var fresh = result.Value.Posts;
                    int pages = Math.Max(1, result.Value.Pages);

                    // The mark this read is entitled to claim - IF its posts end up in front of
                    // somebody. Whether they do is decided below, and the two cases are not the
                    // same: a read held behind the pill has shown nobody anything.
                    //
                    // Falls back to this machine's clock against a server that does not send one.
                    // It is the wrong clock, and it is still much better than the alternative:
                    // without a mark the tab wears the never-opened dot forever, and a mark nobody
                    // can clear is a permanent smudge on the navigation.
                    long freshMark = result.Value.Now > 0
                        ? result.Value.Now
                        : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                    // The cursor every page after the first is read against. Only ever the server's
                    // own clock: the fallback above is fine for a mark, which is only compared
                    // against itself, and wrong for this, which the server compares against its own
                    // timestamps. Zero leaves paging on plain offsets, which is what this did before
                    // the cursor existed and is still correct - just no longer proof against a post
                    // arriving mid-scroll.
                    long freshCursor = result.Value.Now;

                    // Whether this read ended up behind the pill. Decided inside the lock, acted on
                    // outside it.
                    bool held = false;

                    lock (gate)
                    {
                        // Nothing on screen yet, or the same top post. Anything else is held, because
                        // replacing a list somebody is reading loses their place and moves the thing
                        // they were about to press.
                        bool nothingShown = posts.Count == 0;
                        bool sameTop = !nothingShown && fresh.Count > 0
                            && string.Equals(fresh[0].Id, posts[0].Id, StringComparison.Ordinal);

                        if (nothingShown || applyNextRead)
                        {
                            // A NEW LIST FROM THE TOP. Everything scrolled into view belonged to the
                            // list being replaced, so the pages start over with it.
                            posts.Clear();
                            posts.AddRange(fresh);
                            incoming.Clear();
                            pendingMark = 0;
                            pendingCursor = 0;

                            pagesKnown = pages;
                            cursor = freshCursor;
                            nextPage = 1;

                            // Only when something was actually displaced. The first read of all has
                            // nothing to scroll back to and would only be fighting a restored
                            // position for no reason.
                            if (!nothingShown)
                                scrollWanted = true;

                            // Only ever forward: two reads can land out of order, and taking the
                            // earlier answer's mark would un-see posts already shown.
                            if (freshMark > mark)
                                mark = freshMark;
                        }
                        else if (sameTop)
                        {
                            // NOTHING NEW AT THE TOP, so the pages below it are still the right pages
                            // and are left exactly where they are. What this read is good for is the
                            // counts: hearts move on posts already on screen, and throwing away
                            // everything scrolled into view to collect them would be the pagination
                            // bug this design exists to remove, wearing a poll's clothes.
                            for (int i = 0; i < fresh.Count && i < posts.Count; i++)
                                posts[i] = fresh[i];

                            // Trusted downward as well as up: a post coming off the list - somebody
                            // opting out, a removal - genuinely shortens it.
                            pagesKnown = Math.Max(pages, nextPage);

                            if (freshMark > mark)
                                mark = freshMark;
                        }
                        else
                        {
                            incoming.Clear();
                            incoming.AddRange(fresh);

                            // Held, and so are its mark and its pagination. What is on screen is
                            // still the older list, and that is all anybody has been shown.
                            if (freshMark > pendingMark)
                                pendingMark = freshMark;

                            pendingPages = pages;
                            pendingCursor = freshCursor;
                            held = true;
                        }

                        applyNextRead = false;
                    }

                    // Outside the gate: whatever this runs is not this list's business and must not
                    // be able to deadlock against it.
                    if (held)
                        HeldNewPosts?.Invoke();

                    EverLoaded = true;
                    note = null;
                }
                catch (Exception ex)
                {
                    log.Debug($"[Ratings] {name} read failed: {ex.Message}");
                }
                finally
                {
                    Interlocked.Exchange(ref inFlight, 0);
                }
            });
        }

        /// <summary>
        /// Copies a reaction onto this list's own copy of a post, if it has one.
        ///
        /// A FIRST ULTIMATE CLEAR IS ON BOTH LISTS. It is one row on the server - one heart count,
        /// one share, and hearting it in one place has genuinely hearted it in the other - but each
        /// list deserialises its own object from its own response, so the two copies are separate
        /// C# instances. Without this the number would move on the card that was pressed and sit
        /// there stale on the other until that list happened to be re-read, which is somebody
        /// switching tabs to find their own heart apparently missing.
        ///
        /// Only the reaction fields, and only from a post that is not this list's own copy. It is
        /// deliberately not a general "replace the post": everything else about a row is whatever
        /// the read that produced it said, and this has no business overwriting it.
        /// </summary>
        public void SyncReaction(AchievementPost source)
        {
            lock (gate)
            {
                Apply(posts, source);
                Apply(incoming, source);
            }

            static void Apply(List<AchievementPost> list, AchievementPost source)
            {
                foreach (var post in list)
                {
                    if (ReferenceEquals(post, source)
                        || !string.Equals(post.Id, source.Id, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    post.Hearts = source.Hearts;
                    post.Hearted = source.Hearted;
                    post.HeartLocked = source.HeartLocked;
                    post.Reshared = source.Reshared;
                }
            }
        }

        /// <summary>
        /// Marks what is on screen as out of date, without asking for anything.
        ///
        /// For the reader's own actions that change the list's ORDER or CONTENTS rather than
        /// invalidating it: sharing a post, which moves it to the top; their own clear landing;
        /// broadcasting going on or off. The next read lands straight on screen rather than behind
        /// a pill - offering somebody a pill to see a thing they just did themselves is absurd.
        ///
        /// DOES NOT FETCH. The read happens on the next frame the tab is drawn, which for a list
        /// nobody is looking at is never - and two streams both fetching on every share would be
        /// two requests to reorder a list that is not on screen.
        /// </summary>
        public void Invalidate()
        {
            applyNextRead = true;
            readAt = DateTime.MinValue;
        }

        /// <summary>
        /// Throws away everything and starts over on the next frame the tab is drawn.
        ///
        /// For the changes that make the whole list wrong rather than merely out of date: a
        /// character switch, opting out, broadcasting going off. Cheaper and less error-prone than
        /// trying to work out which rows survive.
        /// </summary>
        public void Reset()
        {
            lock (gate)
            {
                posts.Clear();
                incoming.Clear();
                cursor = 0;
                nextPage = 0;
                pagesKnown = 1;
                pendingPages = 1;
                pendingCursor = 0;
                pendingMark = 0;
                scrollWanted = false;
            }

            readAt = DateTime.MinValue;
            EverLoaded = false;
        }
    }
}
#endif
