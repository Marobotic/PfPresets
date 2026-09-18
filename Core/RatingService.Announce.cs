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
    /// top-of-feed read is the only request involved: <see cref="RefreshForAnnounce"/> asks for one
    /// page of the undivided feed on a timer and hands it to <see cref="ObserveForAnnounce"/>, which
    /// keeps nothing. None at all while the announcer is off or nobody is logged in.
    /// </summary>
    internal sealed partial class RatingService
    {
        /// <summary>
        /// How often the whole feed is read for the announcer's sake.
        ///
        /// The same two minutes an open tab uses. It is no longer the same REQUEST as the tab's -
        /// the tab reads one half of the feed at a time and the announcer has to watch both, so it
        /// keeps its own unscoped read; see the note in TickAnnouncePoll for why neither half can
        /// stand in for it.
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
        /// Ids already announced, against the clear time each was announced at, so the same clear
        /// cannot arrive twice.
        ///
        /// THIS IS THE DUPLICATE GUARD AND THE MARK IS NOT. They answer different questions: the
        /// mark says how far down the feed this client has accounted for, and cannot say whether
        /// one particular post was shown - two clears can share a millisecond, and the mark is
        /// deliberately moved past posts that were never announced at all. The set settles
        /// duplicates within a session; the mark settles backlog across restarts.
        ///
        /// PRUNED BY AGE, NOT BY COUNT. It used to drop its oldest half on reaching a fixed size,
        /// which is a guess about how busy the feed is - and on an evening busy enough to reach it,
        /// the ids being dropped were exactly the recent ones still inside the freshness window and
        /// so still announceable. Forgetting an id only once it is too old to be announced again
        /// makes the guard airtight and lets its size follow the traffic.
        /// </summary>
        private readonly Dictionary<string, long> announced = new(StringComparer.Ordinal);

        /// <summary>A backstop on the above, and only that. Everything in it is pruned against
        /// timestamps the server sent; a server sending them from a clock years fast would
        /// otherwise grow it without limit.</summary>
        private const int AnnouncedCap = 4096;

        // ── When the poll fires, and why it is not "two minutes since the last one" ───
        //
        // ONE GRID, SO EVERY CLIENT READS AT ROUGHLY THE SAME INSTANT. A timer started when the
        // plugin loaded puts each client on its own phase, so two people sitting in the same room
        // could be a full interval apart on the same clear - one sees the banner as it lands and
        // the other nearly two minutes later. A clear is a thing a group talks about while it is
        // happening, and that gap is the difference between an announcement and an echo.
        //
        // The grid is absolute time divided by the interval, which is the same grid on every
        // machine without anybody agreeing on anything: no handshake, just two clients doing the
        // same arithmetic on the same clock.
        //
        // AND A PER-CLIENT OFFSET, BECAUSE A PERFECT GRID IS A STAMPEDE. Every client reading on
        // the same millisecond turns a steady trickle into one spike every two minutes, which is
        // the load shape that falls over exactly when the feed is busiest. The offset is small
        // against the interval and large against a request: the reads still land together as far
        // as anybody watching a screen is concerned, and arrive at the server spread over it.
        //
        // Chosen once per session rather than derived from anything stable - it only has to differ
        // between clients, not persist within one.
        private static readonly TimeSpan AnnouncePollSpread = TimeSpan.FromSeconds(20);

        private readonly long announceJitterMs =
            Random.Shared.NextInt64((long)AnnouncePollSpread.TotalMilliseconds);

        /// <summary>Which grid slot the announcer last read in, or -1 to read on the next tick.
        /// Frame thread only - the tick is the only thing that touches it.</summary>
        private long announceSlot = -1;

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
            if (mark > config.ClearAnnouncementRankMark)
            {
                config.ClearAnnouncementRankMark = mark;
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

            if (justLoggedIn)
            {
                // What they missed, now rather than at the next grid instant.
                announceCatchUp = true;
                announceSlot = -1;
            }

            // The grid slot this moment falls in. A read happens when the slot changes, which is
            // once per interval and - bar each client's own small offset - at the same instant on
            // every client in the plugin. See the note on announceJitterMs.
            long slot = (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - announceJitterMs)
                / (long)AnnouncePollAfter.TotalMilliseconds;

            if (slot == announceSlot)
                return;

            announceSlot = slot;

            // ITS OWN READ, OF THE WHOLE TABLE. This used to share the feed tab's poll, which was
            // right while the tab read one undivided list: the same rows, so a client with it open
            // did not pay twice.
            //
            // The tab reads one half at a time now, and neither half can stand in for this. Not
            // because it would announce less - because it would announce WRONGLY: the mark below
            // moves past everything it is shown, so a page of Ultimate reclears standing in for
            // this read would mark a first clear as accounted for that nobody has ever seen, and
            // nothing would ever announce it. See RefreshForAnnounce.
            //
            // The cost is one extra read every two minutes, and only while somebody has the clears
            // tab open.
            RefreshForAnnounce();
        }

        /// <summary>
        /// Looks at a page of the feed and decides what, if anything, to announce.
        ///
        /// Called from inside the feed read with whatever came back, before any of it is decided
        /// about - the announcer wants the posts themselves, not the version of the list that
        /// survives being merged into what is on screen. Everything it takes is a copy.
        ///
        /// TWO QUESTIONS, TWO CLOCKS, AND THEY ARE NOT THE SAME CLOCK. This is the whole of what
        /// was wrong with this method before:
        ///
        ///   have I accounted for this post?   Answered against the post's RANK - the order the
        ///                                     feed is actually served in. It has to be, because a
        ///                                     mark is only a high-water mark if nothing can arrive
        ///                                     after it with a lower value, and rank is assigned
        ///                                     when a post is written.
        ///   is this clear still news?         Answered against WHEN IT WAS CLEARED, because that
        ///                                     is what "still news" means.
        ///
        /// Both used to be answered against the clear time, which made the first one wrong: a clear
        /// published after one that happened later than it - a party of eight reporting seconds
        /// apart, a retry, a busy queue - landed under a mark that had already moved past it and
        /// was dropped for good. Whether that happened depended on which side of a client's own
        /// two-minute poll the two posts fell, which is why one person in a party got the banner
        /// and the person beside them never did.
        /// </summary>
        /// <param name="serverNowMs">The server's clock as of this read, from the feed response.
        /// Every comparison below is between two of the server's own numbers, so a player whose PC
        /// is an hour out neither misses announcements nor gets shown history.</param>
        private void ObserveForAnnounce(IReadOnlyList<AchievementPost> posts, long serverNowMs)
        {
            if (!config.ClearAnnouncementsEnabled || posts.Count == 0)
                return;

            // Falls back to this machine's clock only against a server too old to send one, which
            // is the behaviour this had throughout and is no worse than it was.
            long nowMs = serverNowMs > 0
                ? serverNowMs
                : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            // The worker's own copy, seeded from the config the first time through. Read from the
            // field rather than the config on every pass, because two reads can land close together
            // and the second must see what the first decided - the config's copy is a frame behind
            // until the tick writes it.
            long mark = Volatile.Read(ref announceMark);
            if (mark < 0)
                mark = config.ClearAnnouncementRankMark;

            // THE FIRST READ OF AN INSTALL SEEDS AND SAYS NOTHING. See the header - this is the one
            // backlog rule the catch-up below does not lift, because somebody who has just
            // installed this does not yet know what a banner across their screen even is.
            //
            // It also covers the first read after the mark moved into the feed's own clock: the two
            // marks are not comparable, so the old one is not carried over and this pass is a seed
            // like any other.
            bool seeding = mark <= 0;

            // TAKEN, not read: a catch-up is spent by the first page that observes it, and every
            // read after this one is an ordinary poll again.
            bool catchUp = announceCatchUp;
            announceCatchUp = false;

            string? mine = api.LocalIdentity is { IsValid: true } me ? me.Key : null;

            long windowMs = (long)AnnounceFreshWindow.TotalMilliseconds;

            // At login the freshness window is exactly the wrong rule. Mid-session it means "this
            // client was asleep, do not replay history"; at login the history IS what is being
            // asked for, and the mark already knows where it starts - it is the post they were last
            // told about, which is to say the moment they logged out.
            long cutoff = catchUp ? long.MinValue : nowMs - windowMs;
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

                long rank = FeedRank(post);
                long cleared = UnixMs(post.ClearedAt);

                if (rank > newest)
                    newest = rank;

                if (seeding)
                    continue;

                // Already accounted for. In the feed's own order, so nothing published after this
                // read can land beneath it.
                if (rank <= mark)
                    continue;

                // Too old to interrupt anybody for. A share re-ranks an old post to the top of the
                // feed, so this is also what keeps a shared clear from a previous evening out of
                // the queue - the rank is new, the clear is not.
                if (cleared < cutoff)
                    continue;

                // Your own clear. You were there; being told about it is being told what you just
                // did, and the feed refuses you a heart on it for the same reason.
                if (mine != null && string.Equals(post.Identity.Key, mine, StringComparison.Ordinal))
                    continue;

                // THE SAVAGE FARM IS RECORDED AND NEVER ANNOUNCED. The server already keeps savage
                // reclears out of the feed this reads, so this should never fire - it is here so
                // the rule does not rest on one side alone. A banner is for a first savage clear or
                // any Ultimate; a Tuesday M1S is on the Savage tab and that is the whole of it.
                // Counted as seen above regardless, so it cannot come back round as "new".
                if (!post.IsAnnounceable)
                    continue;

                lock (announceLock)
                {
                    // Keyed on the CLEAR time rather than the rank, because this is pruned by how
                    // long a post stays announceable - which is what the freshness window measures.
                    if (!announced.TryAdd(post.Id, cleared))
                        continue;
                }

                worth.Add(post);
            }

            // Twice the window, so an id is only forgotten well after the post it belongs to has
            // stopped being announceable and there is no edge for one to be re-announced across.
            PruneAnnounced(nowMs - windowMs * 2);

            // THE MARK MOVES EVEN WHEN NOTHING IS ANNOUNCED, and that is deliberate: a post that
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
        /// Where a post sits in the feed's own order, in the server's clock.
        ///
        /// THE FEED IS SORTED BY THIS AND NOT BY WHEN THE CLEAR HAPPENED. The announcer's mark has
        /// to be kept in the same space as that sort or it is not a high-water mark at all - see
        /// the note on this method's one caller.
        ///
        /// Falls back to the clear's own time against a server too old to send a rank, which is
        /// exactly what this did before and is the best a client can do when it cannot see the
        /// order the feed was built in.
        /// </summary>
        private static long FeedRank(AchievementPost post)
            => post.RankAt is { } rank ? UnixMs(rank) : UnixMs(post.ClearedAt);

        /// <summary>A UTC timestamp from the server as unix ms. The Kind is forced rather than
        /// assumed: a DateTime deserialised out of JSON arrives Unspecified, and converting one of
        /// those applies this machine's timezone to a number that is already UTC.</summary>
        private static long UnixMs(DateTime value)
            => new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc))
                .ToUnixTimeMilliseconds();

        /// <summary>
        /// Forgets the ids of posts too old to be announced again, so the guard's size follows how
        /// busy the feed is rather than a number picked in advance.
        /// </summary>
        private void PruneAnnounced(long before)
        {
            lock (announceLock)
            {
                if (announced.Count == 0)
                    return;

                List<string>? drop = null;

                foreach (var entry in announced)
                {
                    if (entry.Value < before)
                        (drop ??= new List<string>()).Add(entry.Key);
                }

                if (drop != null)
                {
                    foreach (string id in drop)
                        announced.Remove(id);
                }

                // Nothing here is trustworthy enough to be the only bound - see AnnouncedCap.
                if (announced.Count > AnnouncedCap)
                    announced.Clear();
            }
        }

        /// <summary>
        /// Puts one of the two lists back at the top, showing anything its poll has been holding.
        ///
        /// What pressing an announcement does. The post being announced is by definition the newest
        /// one, so the top of a fresh list is where it is - but what is on screen may be minutes
        /// old, or parked behind the pill, or not read at all yet. Reveal settles all three: apply
        /// whatever is held, then ask for a read that lands straight on screen rather than behind
        /// another pill, past the poll's throttle. This is a click, not a timer, and the one thing
        /// it must not do is take two minutes to show the post it was pressed about.
        /// </summary>
        /// <param name="firstClear">Which list the announced post is on. Taken from the post rather
        /// than guessed, because the tab is about to switch to it and revealing the other one would
        /// leave somebody looking at a list their clear is not in.</param>
        public void RevealFeedTop(bool firstClear)
            => (firstClear ? FirstClears : UltimateClears).Reveal();
    }
}
#endif
