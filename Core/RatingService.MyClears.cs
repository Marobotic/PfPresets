#if PFP_RATINGS
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PfPresets
{
    /// <summary>
    /// Your own clears, as this server has them.
    ///
    /// THE SAME ROWS AS THE FEED, not the same rows as the profile card. Those are two different
    /// facts with the same name and keeping them apart is the whole point of this file:
    ///
    ///   the profile card  What Tomestone and FFLogs say a character has killed. Everything, ever,
    ///                     whether this plugin existed at the time or not - and it costs a provider
    ///                     lookup on a shared budget to find out.
    ///   this              What THIS server watched them clear and recorded: the posts on the feed
    ///                     with their name on. Nothing outside it, nothing before they installed
    ///                     the plugin, and no provider involved at any point.
    ///
    /// The second is the smaller list and the honest one for a tab sitting beside the feed. A clear
    /// is here because the plugin was running when it happened and the evidence checked out, which
    /// is exactly the claim the feed makes about everybody else's.
    ///
    /// Reads are free - our own table, keyed by the session's character - so this polls on the same
    /// terms as the feed and never on anybody's budget.
    /// </summary>
    internal sealed partial class RatingService
    {
        /// <summary>How often an open tab re-reads the list. Slower than the feed's own poll: this
        /// changes when the reader clears something, which is not something they need telling
        /// about - they were there.</summary>
        private static readonly TimeSpan MyClearsPollAfter = TimeSpan.FromMinutes(5);

        private readonly List<AchievementPost> mine = new();
        private readonly object mineLock = new();

        private DateTime mineReadAt = DateTime.MinValue;
        private int mineInFlight;
        private int mineMoreInFlight;

        /// <summary>Paging, exactly as the feed does it - see the note there. The cursor matters
        /// slightly less here (your own clears arrive one evening at a time) and costs nothing.
        /// </summary>
        private long mineCursor;
        private int mineNextPage;
        private int minePagesKnown = 1;

        /// <summary>
        /// Which character the list belongs to.
        ///
        /// Kept because the answer is per-character and the character changes underneath this
        /// without warning - an alt, a logout, a world visit. Without it, switching characters
        /// leaves the previous one's clears on screen under the new one's name, which is the one
        /// mistake a page called "My clears" must not make.
        /// </summary>
        private string mineKey = string.Empty;

        /// <summary>True once a read has landed for the current character, so the tab can tell
        /// "nothing here" from "not asked yet" - the first draws no tab at all, the second would
        /// draw an empty one.</summary>
        public bool MyClearsEverLoaded { get; private set; }

        /// <summary>How many the server says there are in total, not how many are loaded.</summary>
        public int MyClearsTotal { get; private set; }

        public IReadOnlyList<AchievementPost> MyClears()
        {
            lock (mineLock)
                return mine.ToArray();
        }

        public int MyClearsCount
        {
            get { lock (mineLock) return mine.Count; }
        }

        public bool MyClearsHasMore
        {
            get { lock (mineLock) return mine.Count > 0 && mineNextPage < minePagesKnown; }
        }

        public bool MyClearsLoadingMore => mineMoreInFlight != 0;

        /// <summary>
        /// Reads the list if it is time to, and starts it over when the character has changed.
        ///
        /// Safe to call every frame; only ever called while the tab is open, because a list nobody
        /// is looking at does not need to be current.
        /// </summary>
        public void EnsureMyClearsLoaded()
        {
            if (!config.CommunityEnabled)
                return;

            string key = api.LocalIdentity is { IsValid: true } me ? me.Key : string.Empty;

            if (!string.Equals(key, mineKey, StringComparison.Ordinal))
            {
                lock (mineLock)
                {
                    mine.Clear();
                    mineCursor = 0;
                    mineNextPage = 0;
                    minePagesKnown = 1;
                }

                mineKey = key;
                MyClearsEverLoaded = false;
                MyClearsTotal = 0;
                mineReadAt = DateTime.MinValue;
            }

            // Nobody logged in. Nothing to ask about, and the empty list above is already the
            // right answer.
            if (key.Length == 0)
                return;

            if (DateTime.UtcNow - mineReadAt < MyClearsPollAfter)
                return;

            RefreshMyClears();
        }

        /// <summary>
        /// Reads the first page now, replacing whatever is held.
        ///
        /// Called by the poll above, and directly when this client posts a clear of its own - that
        /// is the one moment the list is known to be out of date, and the one moment somebody is
        /// most likely to go and look at it.
        /// </summary>
        public void RefreshMyClears()
        {
            if (!config.CommunityEnabled)
                return;

            if (Interlocked.CompareExchange(ref mineInFlight, 1, 0) != 0)
                return;

            mineReadAt = DateTime.UtcNow;
            string readFor = mineKey;

            _ = Task.Run(async () =>
            {
                try
                {
                    var result = await api.GetMyClearsAsync(0).ConfigureAwait(false);

                    if (!result.IsOk || result.Value == null)
                        return;

                    // The character changed while this was out. Answering about the previous one
                    // now would put their clears under somebody else's name.
                    if (!string.Equals(readFor, mineKey, StringComparison.Ordinal))
                        return;

                    lock (mineLock)
                    {
                        mine.Clear();
                        mine.AddRange(result.Value.Posts);

                        minePagesKnown = Math.Max(1, result.Value.Pages);
                        mineCursor = result.Value.Now;
                        mineNextPage = 1;
                    }

                    MyClearsTotal = Math.Max(result.Value.Total, result.Value.Posts.Count);
                    MyClearsEverLoaded = true;
                }
                catch (Exception ex)
                {
                    log.Debug($"[Ratings] My clears read failed: {ex.Message}");
                }
                finally
                {
                    Interlocked.Exchange(ref mineInFlight, 0);
                }
            });
        }

        /// <summary>Adds the next page to the bottom. Same contract as the feed's: safe every
        /// frame, silent on failure, and asked for again by the next scroll.</summary>
        public void LoadMoreMyClears()
        {
            if (!config.CommunityEnabled)
                return;

            int page;
            long cursor;

            lock (mineLock)
            {
                if (mine.Count == 0 || mineNextPage >= minePagesKnown)
                    return;

                page = mineNextPage;
                cursor = mineCursor;
            }

            if (Interlocked.CompareExchange(ref mineMoreInFlight, 1, 0) != 0)
                return;

            string readFor = mineKey;

            _ = Task.Run(async () =>
            {
                try
                {
                    var result = await api.GetMyClearsAsync(page, cursor).ConfigureAwait(false);

                    if (!result.IsOk || result.Value == null)
                        return;

                    if (!string.Equals(readFor, mineKey, StringComparison.Ordinal))
                        return;

                    lock (mineLock)
                    {
                        // A refresh landed while this was out, so this page is of a list that has
                        // been replaced.
                        if (mineNextPage != page || mineCursor != cursor)
                            return;

                        var posts = result.Value.Posts;

                        if (posts.Count == 0)
                        {
                            minePagesKnown = page;
                            return;
                        }

                        var known = new HashSet<string>(StringComparer.Ordinal);
                        foreach (var p in mine)
                            known.Add(p.Id);

                        foreach (var p in posts)
                        {
                            if (known.Add(p.Id))
                                mine.Add(p);
                        }

                        minePagesKnown = Math.Max(minePagesKnown, Math.Max(1, result.Value.Pages));
                        mineNextPage = page + 1;
                    }
                }
                catch (Exception ex)
                {
                    log.Debug($"[Ratings] My clears page {page} failed: {ex.Message}");
                }
                finally
                {
                    Interlocked.Exchange(ref mineMoreInFlight, 0);
                }
            });
        }
    }
}
#endif
