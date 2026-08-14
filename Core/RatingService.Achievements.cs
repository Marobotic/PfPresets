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

        /// <summary>Which page is on screen, and how many there are.</summary>
        public int FeedPage { get; private set; }
        public int FeedPages { get; private set; } = 1;

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

                // These are on screen now, so their mark is finally ours to claim. Pressing the
                // pill is the only thing that turns a held read into a shown one.
                if (pendingMark > feedMark)
                    feedMark = pendingMark;

                pendingMark = 0;
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

        /// <summary>Asks for the top of the feed now. The tab's own refresh, and what the poll
        /// calls.</summary>
        /// <summary>Moves to a page and reads it. Out of range is ignored rather than clamped -
        /// the buttons already know the bounds and a silent correction would hide a bug.</summary>
        public void ShowFeedPage(int page)
        {
            if (page < 0 || page >= FeedPages || page == FeedPage)
                return;

            FeedPage = page;
            feedScrollWanted = true;
            RefreshFeed();
        }

        /// <summary>Set when a page change wants the list back at the top.</summary>
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
                // The page this read is OF, kept rather than re-read afterwards: FeedPage can move
                // under a request in flight, and the mark below is only ever page one's to claim.
                int askedFor = FeedPage;

                try
                {
                    var result = await api.GetFeedAsync(askedFor).ConfigureAwait(false);

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
                    FeedPages = Math.Max(1, result.Value.Pages);

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

                    // A page that has emptied out from under you - somebody opted out, a post was
                    // removed - lands you on the last one that still exists rather than on nothing.
                    if (posts.Count == 0 && FeedPage > 0 && FeedPage >= FeedPages)
                    {
                        FeedPage = FeedPages - 1;
                        feedReadAt = DateTime.MinValue;
                    }

                    lock (feedLock)
                    {
                        // Nothing on screen yet, or the same top post: apply it. Anything else is
                        // held, because replacing a list somebody is reading loses their place and
                        // moves the thing they were about to press.
                        // Only page one can be "current"; the pages behind it do not move under
                        // somebody reading them.
                        bool nothingShown = feed.Count == 0 || FeedPage > 0;
                        bool sameTop = !nothingShown && posts.Count > 0
                            && string.Equals(posts[0].Id, feed[0].Id, StringComparison.Ordinal);

                        if (nothingShown || sameTop || applyNextRead)
                        {
                            feed.Clear();
                            feed.AddRange(posts);
                            incoming.Clear();
                            pendingMark = 0;

                            // ONLY PAGE ONE CAN CLAIM THE MARK. The mark is a time, not a place,
                            // and a page further down the feed proves nothing about what has
                            // arrived at the top of it - so reading page three would otherwise
                            // silently mark every new clear read without drawing one of them.
                            //
                            // Only ever forward: two reads can land out of order, a page change
                            // racing the poll, and taking the earlier answer's mark would un-see
                            // posts that had already been shown.
                            if (askedFor == 0 && mark > feedMark)
                                feedMark = mark;
                        }
                        else
                        {
                            incoming.Clear();
                            incoming.AddRange(posts);

                            // Held, and so is its mark - see pendingMark. What is on screen is
                            // still the older feed, and that is all anybody has been shown.
                            if (mark > pendingMark)
                                pendingMark = mark;

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
        /// Applied here the moment it is pressed and never taken back, because the server answers
        /// the same whatever it decides to do underneath - stored, held until the account is known,
        /// or dropped. Guessing at which from the client would produce a heart that flickered off a
        /// second after somebody gave it, which is worse than the small chance of being optimistic.
        /// The next read tells the truth either way.
        /// </summary>
        public void Heart(AchievementPost post)
        {
            if (post.Hearted || string.IsNullOrEmpty(post.Id))
                return;

            if (!reacting.TryAdd(post.Id + "#heart", 0))
                return;

            post.Hearted = true;
            post.Hearts += 1;

            _ = Task.Run(async () =>
            {
                try
                {
                    await api.HeartAsync(post.Id).ConfigureAwait(false);
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
                    }
                }
                catch (Exception ex)
                {
                    log.Debug($"[Ratings] Achievement post failed: {ex.Message}");
                }
            });
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
