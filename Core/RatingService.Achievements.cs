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
    /// TWO LISTS, NOT ONE. First clears in one and Ultimate reclears in the other, because they are
    /// two unlike things and mixing them meant the second burying the first. The lists themselves
    /// live in ClearsFeed - see that file for why the split is there rather than a filter applied
    /// here. What stays in this file is what belongs to neither list on its own: the unread badge,
    /// the announcer's own read of the undivided table, and the two reactions.
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
        /// <summary>
        /// First clears: an Ultimate's, a savage floor's, and the tier clears posted under the
        /// older scheme. The list somebody opens the tab for.
        /// </summary>
        public ClearsFeed FirstClears { get; }

        /// <summary>Every savage floor clear of the current tier. Only ever first clears, because
        /// a savage floor is only posted the first time - so this is the savage half of
        /// <see cref="FirstClears"/>, given its own tab so it reads as a place.</summary>
        public ClearsFeed SavageClears { get; }

        /// <summary>Every Ultimate clear, first ones included - and most of the table.</summary>
        public ClearsFeed UltimateClears { get; }

        /// <summary>Both, in the order the tab draws them, so a caller wanting to do something to
        /// each does not have to name them and cannot miss one when a third appears.</summary>
        private IEnumerable<ClearsFeed> Streams
        {
            get
            {
                yield return FirstClears;
                yield return SavageClears;
                yield return UltimateClears;
            }
        }

        /// <summary>Built here rather than in the main constructor so the two streams and the
        /// things that read them stay in one file. Called from RatingService's own ctor.</summary>
        private void InitClearsFeeds(out ClearsFeed first, out ClearsFeed savage, out ClearsFeed ultimate)
        {
            first = new ClearsFeed(api, log, () => config.CommunityEnabled, "first", "First clears");
            savage = new ClearsFeed(api, log, () => config.CommunityEnabled, "savage", "Savage clears");
            ultimate = new ClearsFeed(api, log, () => config.CommunityEnabled, "ultimate", "Ultimate clears");

            // ONLY THE FIRST CLEARS STREAM POKES THE BADGE, because the badge counts first clears.
            // Wiring the other one to it would have a page of Ultimate reclears asking for a number
            // that cannot have changed.
            first.HeldNewPosts = () => unseenAskNow = true;
        }

        /// <summary>
        /// Reads the top of the WHOLE feed, for the announcer alone.
        ///
        /// Unscoped, and separate from either stream's own poll, and both of those are load-bearing:
        ///
        ///   unscoped   The announcer is watching for anything worth a banner. A scoped read would
        ///              be watching half the table, and whichever half was not being watched would
        ///              simply never announce.
        ///   separate   The announcer's mark moves past everything it has looked at. If a stream's
        ///              read fed it, a page of Ultimate reclears would mark a first clear as
        ///              accounted for - and that clear would then never be announced to anybody,
        ///              because the mark says it already was.
        ///
        /// The cost is one extra read every two minutes, and only for a client that has the clears
        /// tab actually open - which is a small minority at any instant. Nothing is kept from it:
        /// the posts are observed and dropped.
        /// </summary>
        private void RefreshForAnnounce()
        {
            if (Interlocked.CompareExchange(ref announceReadInFlight, 1, 0) != 0)
                return;

            _ = Task.Run(async () =>
            {
                try
                {
                    var result = await api.GetFeedAsync(0).ConfigureAwait(false);

                    if (result.IsOk && result.Value != null)
                        ObserveForAnnounce(result.Value.Posts, result.Value.Now);
                }
                catch (Exception ex)
                {
                    log.Debug($"[Ratings] Announce read failed: {ex.Message}");
                }
                finally
                {
                    Interlocked.Exchange(ref announceReadInFlight, 0);
                }
            });
        }

        private int announceReadInFlight;

        /// <summary>
        /// Puts both lists back at the top on the next frame either is drawn.
        ///
        /// For the changes that make them wrong rather than stale: broadcasting going off, an
        /// opt-out landing, their own clear being posted. Cheaper than working out which rows
        /// survive, and there is no case where it matters that it is not.
        /// </summary>
        public void ResetClearsFeeds()
        {
            foreach (var stream in Streams)
                stream.Reset();
        }

        /// <summary>
        /// Says both lists are out of date without asking for anything.
        ///
        /// Both, rather than the one a change belongs to, because working out which is more code
        /// than it saves: a share can be on either list, broadcasting affects both, and the cost of
        /// being wrong is a stale list. Neither fetches until its tab is drawn - see Invalidate.
        /// </summary>
        private void InvalidateClearsFeeds()
        {
            foreach (var stream in Streams)
                stream.Invalidate();
        }

        /// <summary>Posts being hearted or shared right now, so a button held down issues one
        /// request rather than one per frame.</summary>
        private readonly ConcurrentDictionary<string, byte> reacting = new();

        /// <summary>
        /// One character's portrait, as bytes, or null.
        ///
        /// A pass-through so the UI's texture cache does not need its own HttpClient pointed at the
        /// same server. There is nothing for this class to decide about a picture - see
        /// PfApiClient.GetPortraitAsync for why it is the one call here with no retries and no
        /// circuit breaker behind it.
        /// </summary>
        public Task<byte[]?> GetPortraitAsync(string path) => api.GetPortraitAsync(path);

        /// <summary>
        /// Puts a reaction onto every copy of the post that is loaded anywhere.
        ///
        /// The lists overlap now: a first Ultimate clear is on both First clears and Ultimates, and
        /// anybody's own clear is additionally on My clears. All of those are the same row on the
        /// server with one heart count between them - so a heart has to be seen to move in all of
        /// them at once, or switching tabs shows somebody their own heart apparently undone.
        ///
        /// Called after every optimistic change and again after the server's answer, so the
        /// correction lands everywhere the guess did.
        /// </summary>
        private void SyncReaction(AchievementPost post)
        {
            if (string.IsNullOrEmpty(post.Id))
                return;

            foreach (var stream in Streams)
                stream.SyncReaction(post);

            SyncMyClearsReaction(post);
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

        /// <summary>
        /// Ask on the next tick rather than waiting out the window.
        ///
        /// Set when something has been learned first-hand that the badge ought to reflect now - a
        /// clear of the reader's own landing, broadcasting being switched off. It still asks the
        /// server for the number rather than counting anything itself: this client cannot tell
        /// whose clears it is looking at, and a reader's own must never ring a bell.
        ///
        /// Volatile: written by a worker, read by the frame.
        /// </summary>
        private volatile bool unseenAskNow;

        /// <summary>How many FIRST CLEARS have appeared since the mark, as the server last counted
        /// them.
        ///
        /// First clears and nothing else, which is a decision about what a number on the navigation
        /// is for. It interrupts a reading to say something happened; an Ultimate reclear happens
        /// most evenings, several times over, and a badge that rang for each of them is a badge
        /// people learn to ignore - at which point it is worse than no badge, because the tab wears
        /// a permanent smudge nobody believes.</summary>
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
                    var result = await api.GetUnseenAsync(since, "first").ConfigureAwait(false);

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
            // THE FIRST CLEARS STREAM, AND ONLY THAT ONE. The badge counts first clears - see the
            // scope on the unseen request - so the mark it moves has to belong to the list that
            // shows them. Taking it from whichever list happened to be open would let somebody
            // reading Ultimate reclears clear a badge about a first clear they were never shown,
            // which is the exact failure the rule at the top of this section exists to prevent.
            long shown = FirstClears.ShownMark;

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
            SyncReaction(post);

            _ = Task.Run(async () =>
            {
                try
                {
                    var result = await api.HeartAsync(post.Id).ConfigureAwait(false);

                    if (result.IsOk && result.Value != null)
                    {
                        ApplyReaction(post, result.Value, optimisticHearted: true);
                        SyncReaction(post);
                    }
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
            SyncReaction(post);

            _ = Task.Run(async () =>
            {
                try
                {
                    var result = await api.UnheartAsync(post.Id).ConfigureAwait(false);

                    if (result.IsOk && result.Value != null)
                    {
                        ApplyReaction(post, result.Value, optimisticHearted: false);
                        SyncReaction(post);
                    }
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
                        SyncReaction(post);

                        // A share moves the post to the top, so the order on screen is now wrong.
                        // Re-read rather than shuffle the local copy: the server decides the order
                        // and this is the one action that changes it.
                        InvalidateClearsFeeds();
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
            {
                pendingDuties.Enqueue(evidence);
                while (pendingDuties.Count > MaxPendingDuties)
                    pendingDuties.Dequeue();
            }
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
        private static readonly TimeSpan DutyPostMaxDelay = TimeSpan.FromMinutes(15);

        /// <summary>Failures in a row, for the backoff. Reset by any answer that is not a failure.</summary>
        private int dutyPostFailures;

        /// <summary>More than this waiting and the oldest goes: a week offline should not end in a
        /// burst the server's hourly limit refuses most of anyway.</summary>
        private const int MaxPendingDuties = 30;

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

                    if (result.IsOk)
                        dutyPostFailures = 0;

                    if (result.IsOk && result.Value?.Posted == true)
                    {
                        log.Debug($"[Ratings] Achievement posted: {result.Value.Fight} ({result.Value.Kind})");

                        // Their own clear should be at the top the next time they look.
                        InvalidateClearsFeeds();

                        // And on their own list, which has just gained a row. Marked rather than
                        // read: the tab may not be open, and a list nobody is looking at can wait
                        // until somebody is - see EnsureMyClearsLoaded.
                        mineReadAt = DateTime.MinValue;
                    }
                    else if (result.Status is ApiStatus.BadRequest or ApiStatus.Refused)
                    {
                        // DROPPED. The server read it and said no - an old build, a payload it will
                        // not accept - and it will say no again every time. Retrying those is what
                        // had clients asking every twenty seconds for hours.
                        log.Debug($"[Ratings] Achievement refused ({result.Status}), not retrying");
                    }
                    else if (!result.IsOk)
                    {
                        // PUT BACK, NOT DROPPED. A duty that fails to file is a duty no vote out of
                        // it can ever be checked against, which is the failure this whole path
                        // exists to prevent - and the usual reason to fail is the network being
                        // briefly unavailable, which is exactly the case worth retrying.
                        Requeue(evidence, result.Status == ApiStatus.RateLimited ? result.RetryAfter : null);
                    }
                }
                catch (Exception ex)
                {
                    log.Debug($"[Ratings] Achievement post failed: {ex.Message}");
                    Requeue(evidence, null);
                }
                finally
                {
                    dutyPostInFlight = false;
                }
            });
        }

        /// <summary>
        /// Puts a duty back and waits before the next try: as long as the server said when it
        /// said, otherwise twenty seconds doubling with each failure in a row, up to fifteen
        /// minutes. A limit that resets hourly is not helped by asking three times a minute.
        /// </summary>
        private void Requeue(string evidence, TimeSpan? serverSaid)
        {
            lock (pendingDutyGate)
            {
                pendingDuties.Enqueue(evidence);
                while (pendingDuties.Count > MaxPendingDuties)
                    pendingDuties.Dequeue();
            }

            dutyPostFailures = Math.Min(dutyPostFailures + 1, 10);
            var backoff = TimeSpan.FromTicks(DutyPostRetryDelay.Ticks << (dutyPostFailures - 1));
            if (backoff > DutyPostMaxDelay)
                backoff = DutyPostMaxDelay;
            if (serverSaid is { } said && said > backoff)
                backoff = said > TimeSpan.FromHours(2) ? TimeSpan.FromHours(2) : said;

            nextDutyPostUtc = DateTime.UtcNow + backoff;
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
                    InvalidateClearsFeeds();
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
