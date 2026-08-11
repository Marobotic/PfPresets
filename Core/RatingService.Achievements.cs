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
            if (!config.RatingsEnabled)
                return;

            if (DateTime.UtcNow - feedReadAt < FeedPollAfter)
                return;

            RefreshFeed();
        }

        /// <summary>Asks for the top of the feed now. The tab's own refresh, and what the poll
        /// calls.</summary>
        public void RefreshFeed()
        {
            if (Interlocked.CompareExchange(ref feedInFlight, 1, 0) != 0)
                return;

            feedReadAt = DateTime.UtcNow;

            _ = Task.Run(async () =>
            {
                try
                {
                    var result = await api.GetFeedAsync().ConfigureAwait(false);

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

                    lock (feedLock)
                    {
                        // Nothing on screen yet, or the same top post: apply it. Anything else is
                        // held, because replacing a list somebody is reading loses their place and
                        // moves the thing they were about to press.
                        bool nothingShown = feed.Count == 0;
                        bool sameTop = !nothingShown && posts.Count > 0
                            && string.Equals(posts[0].Id, feed[0].Id, StringComparison.Ordinal);

                        if (nothingShown || sameTop || applyNextRead)
                        {
                            feed.Clear();
                            feed.AddRange(posts);
                            incoming.Clear();
                        }
                        else
                        {
                            incoming.Clear();
                            incoming.AddRange(posts);
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
            if (!config.RatingsEnabled)
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
