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
    ///   never a backlog     The FIRST read of an install seeds the mark and announces nothing.
    ///                       Somebody who has just installed this has no idea what these are, and a
    ///                       wall of strangers' clears is a poor way to find out.
    ///   never stale         Mid-session, a clear older than <see cref="AnnounceFreshWindow"/> is
    ///                       marked as seen and dropped: a laptop lid, a long queue or an hour of
    ///                       an unreachable server should be caught up on quietly, not replayed.
    ///   never your own      You were there. The feed already refuses you a heart on your own post
    ///                       for the same reason.
    ///   never a flood       At most <see cref="AnnounceQueueCap"/> waiting at once, oldest first,
    ///                       and once it is full the newest clear pushes the oldest out. A busy
    ///                       evening on a full server can post six clears inside one poll, and six
    ///                       in a row at six seconds each is most of a minute of somebody else's
    ///                       screen.
    ///   never lost to a     Combat and duties hold the queue rather than skipping it, so the
    ///   fight               clears that land while somebody is inside a fight are still waiting
    ///                       when they come out. See UpdateAnnouncementHold.
    ///
    /// AND ONE EXCEPTION, AT LOGIN. The freshness rule is about a client that has been asleep with
    /// somebody sitting in front of it; logging in is the other thing entirely, and there the clears
    /// that landed while they were away ARE the news - it is the first question anybody asks coming
    /// back to a raiding server. So the first read after a login is a catch-up: the window comes
    /// off, the mark decides what counts as missed, and the cap goes up to
    /// <see cref="AnnounceCatchUpCap"/>. It is still a cap, and it is still not the very first read
    /// of an install, which seeds and says nothing.
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

        /// <summary>
        /// The most that can be waiting to be shown.
        ///
        /// THREE WAS SIZED FOR A QUEUE THAT DRAINED CONTINUOUSLY. Back then nothing waited longer
        /// than the banner in front of it, so three was only ever "how many landed in one poll".
        /// Holding announcements through combat and duties changed what this number means: the
        /// queue now has to cover a whole fight, which is ten to twenty polls rather than one, and
        /// at three the fourth clear of a raid night was marked seen and thrown away.
        ///
        /// Eight is about a minute of banners in the worst case, which is a real cost but a bounded
        /// one, and it is paid at the moment somebody walks out of a duty rather than in the middle
        /// of anything. The cap still exists because it has to - a queue with no ceiling is the
        /// wall of other people's evenings the header promises never to build.
        ///
        /// Anything past it is still marked seen and dropped; it will be on the feed, which is
        /// where a clear that missed its moment belongs.
        /// </summary>
        private const int AnnounceQueueCap = 8;

        /// <summary>
        /// The most a login catch-up will queue, which is a different number from the one above.
        ///
        /// Eight is "how many can land while you are in a fight". This is "how many happened while
        /// you were logged out", and on a busy evening that is a bigger number - capping it at
        /// eight would mean the catch-up quietly dropping the half of the news it was added to
        /// deliver.
        ///
        /// Sixteen is the feed's own page, which is the real ceiling anyway: the announcer reads
        /// one page and cannot see past it, so this is "everything the read can offer" rather than
        /// a limit picked to sit under one. At the ten seconds a banner now runs for, a worst-case
        /// catch-up is a bit under three minutes of them, drained one at a time and held through
        /// anything the player is actually doing.
        /// </summary>
        private const int AnnounceCatchUpCap = 16;

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

        /// <summary>
        /// Whether somebody was logged in on the previous tick, so that logging in can be told from
        /// being logged in. Frame thread only - the tick is the only thing that touches it.
        /// </summary>
        private bool announceWasLoggedIn;

        /// <summary>
        /// Set by the tick when a login is noticed, taken by the next read that observes a page.
        ///
        /// Volatile because the two ends are different threads: the tick raises it on the frame and
        /// <see cref="ObserveForAnnounce"/> takes it on the worker that the feed read completes on.
        /// </summary>
        private volatile bool announceCatchUp;

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

            // NOTICED BEFORE THE GUARDS, AND EXACTLY ONCE. Logging in is an edge rather than a
            // state, and the only way to see it is to compare against the last tick. Tracked even
            // while the announcer is switched off, because otherwise somebody sitting at the title
            // screen who turns it on hours into a session would have that tick read as a login.
            bool loggedIn = api.LocalIdentity is { IsValid: true };
            bool justLoggedIn = loggedIn && !announceWasLoggedIn;
            announceWasLoggedIn = loggedIn;

            if (!config.CommunityEnabled || !config.ClearAnnouncementsEnabled)
                return;

            // Nobody logged in: no character to skip posts of, nothing on screen to draw over, and
            // no reason to be talking to the server from the title screen.
            if (!loggedIn)
                return;

            var now = DateTime.UtcNow;

            if (justLoggedIn)
            {
                // What they missed, now rather than in two minutes' time. Both throttles are stood
                // down for this one read: the announcer's own, and the one it shares with the feed
                // tab - a client that was reading the feed on another character a moment ago would
                // otherwise sit out the whole of its first two minutes back.
                announceCatchUp = true;
                announceCheckedAt = DateTime.MinValue;
                feedReadAt = DateTime.MinValue;
            }

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

            // THE FIRST READ OF AN INSTALL SEEDS AND SAYS NOTHING. See the header - this is the one
            // backlog rule the catch-up below does not lift, because somebody who has just
            // installed this does not yet know what a banner across their screen even is.
            bool seeding = mark <= 0;

            // TAKEN, not read: a catch-up is spent by the first page that observes it, and every
            // read after this one is an ordinary poll again.
            bool catchUp = announceCatchUp;
            announceCatchUp = false;

            string? mine = api.LocalIdentity is { IsValid: true } me ? me.Key : null;

            // At login the freshness window is exactly the wrong rule. Mid-session it means "this
            // client was asleep, do not replay history"; at login the history IS what is being
            // asked for, and the mark already knows where it starts - it is the clear they were
            // last told about, which is to say the moment they logged out.
            var cutoff = catchUp ? DateTime.MinValue : DateTime.UtcNow - AnnounceFreshWindow;
            int cap = catchUp ? AnnounceCatchUpCap : AnnounceQueueCap;

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
                    // THE NEWEST WINS THE LAST SEAT. Overflow used to refuse the arriving clear and
                    // keep whatever had been waiting longest, which is the wrong way round: coming
                    // out of a duty to be told about the three oldest things that happened while
                    // you were in it, and never the ones that just landed, is the least useful
                    // possible eight. Dropping from the front keeps the survivors in order.
                    if (announceQueue.Count >= cap)
                        announceQueue.Dequeue();

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
