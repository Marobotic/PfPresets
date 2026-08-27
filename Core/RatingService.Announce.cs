#if PFP_RATINGS
using System;
using System.Collections.Generic;
using System.Threading;

namespace PfPresets
{
    /// <summary>
    /// Which clears are worth putting across somebody's screen, and when.
    ///
    /// The drawing half is PluginUI.ClearAnnounce.cs. This half decides the harder question, which
    /// is not "what does an announcement look like" but "what counts as news". Four rules, and every
    /// one of them exists because breaking it produces the same failure - an announcement nobody
    /// asked for, which is how a feature like this gets turned off in the first ten minutes:
    ///
    ///   never a backlog     The first read of an install seeds the mark and announces nothing. A
    ///                       login that dumps the last four hours of clears down the middle of the
    ///                       screen is not a notification, it is a wall.
    ///   never stale         A clear older than <see cref="AnnounceFreshWindow"/> is marked as seen
    ///                       and dropped. Coming back from a two-hour break should not replay it.
    ///   never your own      You were there. The feed already refuses you a heart on your own post
    ///                       for the same reason.
    ///   never a flood       At most <see cref="AnnounceQueueCap"/> waiting at once, oldest first.
    ///                       A busy evening on a full server can post six clears inside one poll,
    ///                       and six in a row at six seconds each is most of a minute of somebody
    ///                       else's screen.
    ///
    /// WHERE THE POSTS COME FROM. Nothing here opens a connection of its own. The feed's existing
    /// top-of-feed read is the only request involved: <see cref="ObserveForAnnounce"/> is called
    /// from inside it, and <see cref="TickAnnouncePoll"/> only asks that same read to happen on a
    /// timer while nobody has the tab open. One request either way, and none at all while the
    /// announcer is off or nobody is logged in.
    /// </summary>
    internal sealed partial class RatingService
    {
        /// <summary>
        /// How often the feed is read for the announcer's sake, when nothing else is reading it.
        ///
        /// The same two minutes the open tab uses, deliberately: this is the same request, and a
        /// client with the tab open must not end up polling twice as fast as one without it. The
        /// throttle is shared - see the feedReadAt check below - so opening the tab does not add a
        /// poll, it just takes over the one already running.
        ///
        /// A clear announced up to two minutes late is still a clear announced. Halving this to make
        /// it feel live would double a row read on our box for every client in the plugin, which is
        /// a poor trade for ninety seconds.
        /// </summary>
        private static readonly TimeSpan AnnouncePollAfter = TimeSpan.FromMinutes(2);

        /// <summary>
        /// How recent a clear has to be to be worth interrupting somebody for.
        ///
        /// Comfortably wider than the poll interval, so an ordinary read never drops a clear it
        /// should have shown, and far narrower than a session, so a client that has been asleep -
        /// a laptop lid, a long queue, a server that was unreachable for an hour - comes back and
        /// quietly catches up instead of announcing history.
        /// </summary>
        private static readonly TimeSpan AnnounceFreshWindow = TimeSpan.FromMinutes(20);

        /// <summary>The most that can be waiting to be shown. Anything past it is marked seen and
        /// dropped - it will be on the feed, which is where a clear that missed its moment
        /// belongs.</summary>
        private const int AnnounceQueueCap = 3;

        private readonly Queue<AchievementPost> announceQueue = new();
        private readonly object announceLock = new();

        /// <summary>
        /// Ids already announced, so the same clear cannot arrive twice.
        ///
        /// The mark alone is not enough. Two clears can share a millisecond, and a post whose
        /// ClearedAt is exactly the mark would either be announced on every poll forever (compare
        /// with >=) or lost (compare with >). The set settles it, and the mark is what survives a
        /// restart.
        ///
        /// Bounded, because it is fed by the server: the oldest half is dropped when it fills, and
        /// anything that old is outside the freshness window anyway.
        /// </summary>
        private readonly HashSet<string> announced = new(StringComparer.Ordinal);
        private readonly Queue<string> announcedOrder = new();
        private const int AnnouncedMemory = 240;

        /// <summary>When the poll last asked for a feed read on the announcer's behalf.</summary>
        private DateTime announceCheckedAt = DateTime.MinValue;

        // ── The mark, and which thread is allowed to write it down ────
        //
        // The mark is decided on a WORKER thread - it falls out of a feed read - and written to disk
        // on the FRAME thread, and the split is not fussiness. Saving the config serialises the whole
        // of it, the preset list included, and that list belongs to the frame: a background save
        // landing while somebody adds or reorders a preset is a serialiser walking a collection that
        // is being modified underneath it. Rare, and a crash when it happens.
        //
        // So the worker moves the number and raises a flag, and the tick - which runs every frame,
        // so "later" means within a few milliseconds - is what writes the file.

        /// <summary>The newest clear accounted for, or -1 before the config's copy has been read.
        /// Written by the poll's worker, read by the frame.</summary>
        private long announceMark = -1;

        private volatile bool announceMarkDirty;

        /// <summary>Writes a mark the worker moved, on the thread that owns the config file. Called
        /// from the tick before anything else, including the guards - a mark that has been earned
        /// should survive a logout or the setting being switched off a moment later.</summary>
        private void FlushAnnounceMark()
        {
            if (!announceMarkDirty)
                return;

            announceMarkDirty = false;

            long mark = Volatile.Read(ref announceMark);
            if (mark > config.ClearAnnouncementMark)
            {
                config.ClearAnnouncementMark = mark;
                config.Save();
            }
        }

        /// <summary>Whether anything is waiting. Read every frame by the overlay, so it is a
        /// property rather than a copy of the queue.</summary>
        public bool HasAnnouncement
        {
            get { lock (announceLock) return announceQueue.Count > 0; }
        }

        /// <summary>Takes the next clear to announce, or null. Taking it is what removes it - the
        /// overlay owns it from here and the service does not hold a second reference.</summary>
        public AchievementPost? TakeAnnouncement()
        {
            lock (announceLock)
                return announceQueue.Count > 0 ? announceQueue.Dequeue() : null;
        }

        /// <summary>Empties the queue. Called when the setting is switched off, so a clear parked
        /// behind the one on screen does not appear after somebody has just said they do not want
        /// this.</summary>
        public void ClearAnnouncements()
        {
            lock (announceLock)
                announceQueue.Clear();
        }

        /// <summary>
        /// Asks for a feed read if nobody else has done one lately.
        ///
        /// Called from the framework tick rather than from a draw call, which is the whole point:
        /// an announcement that only arrives while the plugin's own window is open is an
        /// announcement for the two people who leave it open.
        ///
        /// Cheap on the frames it does nothing, which is all but one in seven thousand.
        /// </summary>
        public void TickAnnouncePoll()
        {
            // First, and before every guard below - see FlushAnnounceMark.
            FlushAnnounceMark();

            if (!config.CommunityEnabled || !config.ClearAnnouncementsEnabled)
                return;

            // Nobody logged in: no character to skip posts of, nothing on screen to draw over, and
            // no reason to be talking to the server from the title screen.
            if (api.LocalIdentity is not { IsValid: true })
                return;

            var now = DateTime.UtcNow;

            if (now - announceCheckedAt < AnnouncePollAfter)
                return;

            // SHARED WITH THE TAB'S OWN THROTTLE. If somebody has the feed open, its poll is already
            // doing this read and ObserveForAnnounce is already seeing the answer - so this waits
            // rather than asking for the same rows a second time.
            if (now - feedReadAt < AnnouncePollAfter)
                return;

            announceCheckedAt = now;
            RefreshFeed();
        }

        /// <summary>
        /// Looks at a page of the feed and decides what, if anything, to announce.
        ///
        /// Called from inside the feed read with whatever came back, before any of it is decided
        /// about - the announcer wants the posts themselves, not the version of the list that
        /// survives being merged into what is on screen. Everything it takes is a copy.
        /// </summary>
        private void ObserveForAnnounce(IReadOnlyList<AchievementPost> posts)
        {
            if (!config.ClearAnnouncementsEnabled || posts.Count == 0)
                return;

            // The worker's own copy, seeded from the config the first time through. Read from the
            // field rather than the config on every pass, because two reads can land close together
            // and the second must see what the first decided - the config's copy is a frame behind
            // until the tick writes it.
            long mark = Volatile.Read(ref announceMark);
            if (mark < 0)
                mark = config.ClearAnnouncementMark;

            // THE FIRST READ SEEDS AND SAYS NOTHING. See the header - this is the rule that keeps a
            // login from being a wall of other people's evenings.
            bool seeding = mark <= 0;

            string? mine = api.LocalIdentity is { IsValid: true } me ? me.Key : null;
            var cutoff = DateTime.UtcNow - AnnounceFreshWindow;

            long newest = mark;
            var worth = new List<AchievementPost>();

            // Oldest first, so a poll that finds three clears announces them in the order they
            // happened. The feed hands them over newest first.
            for (int i = posts.Count - 1; i >= 0; i--)
            {
                var post = posts[i];
                if (string.IsNullOrEmpty(post.Id))
                    continue;

                long at = new DateTimeOffset(DateTime.SpecifyKind(post.ClearedAt, DateTimeKind.Utc))
                    .ToUnixTimeMilliseconds();

                if (at > newest)
                    newest = at;

                if (seeding)
                    continue;

                if (at <= mark)
                    continue;

                if (post.ClearedAt < cutoff)
                    continue;

                // Your own clear. You were there; being told about it is being told what you just
                // did, and the feed refuses you a heart on it for the same reason.
                if (mine != null && string.Equals(post.Identity.Key, mine, StringComparison.Ordinal))
                    continue;

                lock (announceLock)
                {
                    if (!announced.Add(post.Id))
                        continue;

                    announcedOrder.Enqueue(post.Id);

                    // Bounded, because the ids come from the server. Half at a time rather than one
                    // per add, so this is amortised and not a dictionary rebuild per post.
                    if (announcedOrder.Count > AnnouncedMemory)
                    {
                        for (int drop = 0; drop < AnnouncedMemory / 2 && announcedOrder.Count > 0; drop++)
                            announced.Remove(announcedOrder.Dequeue());
                    }
                }

                worth.Add(post);
            }

            // THE MARK MOVES EVEN WHEN NOTHING IS ANNOUNCED, and that is deliberate: a clear that
            // was too old, or was yours, or overflowed the cap has still been accounted for, and
            // leaving the mark behind it would make the next poll consider it all over again.
            //
            // Moved here and written to disk by the tick - see FlushAnnounceMark.
            if (newest > mark)
            {
                Volatile.Write(ref announceMark, newest);
                announceMarkDirty = true;
            }
            else if (Volatile.Read(ref announceMark) < 0)
            {
                // Nothing moved, but the config's copy has now been read into the field, and
                // leaving it at -1 would make the next pass read the config again.
                Volatile.Write(ref announceMark, mark);
            }

            if (worth.Count == 0)
                return;

            lock (announceLock)
            {
                foreach (var post in worth)
                {
                    if (announceQueue.Count >= AnnounceQueueCap)
                        break;

                    announceQueue.Enqueue(post);
                }
            }
        }

        /// <summary>
        /// Puts the feed back at the top, showing anything the poll has been holding.
        ///
        /// What pressing an announcement does. The post being announced is by definition the newest
        /// one, so the top of a fresh feed is where it is - but the list on screen may be minutes
        /// old, or parked behind the pill, or not read at all yet. This settles all three: apply
        /// whatever is held, then ask for a read that lands straight on screen rather than behind
        /// another pill.
        /// </summary>
        public void RevealFeedTop()
        {
            ApplyNewPosts();

            // Their own press is what asked for this, so the answer is not something to offer them
            // a pill about - see applyNextRead.
            applyNextRead = true;

            // Past the poll's throttle on purpose. This is a click, not a timer, and the one thing
            // it must not do is take two minutes to show the post it was pressed about.
            feedReadAt = DateTime.MinValue;
            RefreshFeed();
        }
    }
}
#endif
