#if PFP_RATINGS
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;

namespace PfPresets
{
    /// <summary>What happened to a rating the user tried to submit, in terms the UI can show.</summary>
    internal enum SubmitOutcome
    {
        Submitted,
        OnCooldown,
        OptedOut,
        RateLimited,
        Offline,
        Rejected,

        /// <summary>The target isn't someone this player has finished a duty with.</summary>
        NotMet,
    }

    /// <summary>Why a report did or didn't go out, in terms the dialog can explain.</summary>
    internal enum ReportOutcome
    {
        Sent,

        /// <summary>Stopped by this install's own limits before it reached the wire.</summary>
        LocalLimit,

        /// <summary>The server refused it for now (HTTP 429).</summary>
        RateLimited,

        Offline,
        Failed,
    }

    internal readonly struct ReportSendResult
    {
        public ReportOutcome Outcome { get; init; }
        public TimeSpan? RetryAfter { get; init; }

        public bool Ok => Outcome == ReportOutcome.Sent;
    }

    internal readonly struct SubmitResult
    {
        public SubmitOutcome Outcome { get; init; }
        public double WeightApplied { get; init; }
        public DateTime? NextEligibleAt { get; init; }

        public string Message => Outcome switch
        {
            SubmitOutcome.Submitted => WeightApplied >= 0.999
                ? "Rated."
                : $"Rated, at {WeightApplied:0.##}x weight.",
            SubmitOutcome.OnCooldown => NextEligibleAt is { } t
                ? $"Already rated. You can rate them again {Humanise(t)}."
                : "Already rated in the last 24 hours.",
            SubmitOutcome.OptedOut => "They've opted out of ratings.",
            SubmitOutcome.NotMet => "You can only rate people you've played with.",
            SubmitOutcome.RateLimited => "Too many ratings just now. Try again shortly.",
            SubmitOutcome.Offline => "Couldn't reach the rating server.",
            _ => "That rating couldn't be sent.",
        };

        private static string Humanise(DateTime whenUtc)
        {
            var left = whenUtc - DateTime.UtcNow;
            if (left <= TimeSpan.Zero) return "now";
            if (left.TotalHours >= 1) return $"in {left.TotalHours:0} hour(s)";
            return $"in {Math.Max(1, left.TotalMinutes):0} minute(s)";
        }
    }

    /// <summary>
    /// Everything the UI needs to know about player ratings, with the network kept off the draw
    /// thread entirely.
    ///
    /// Lookups never block: <see cref="Get"/> answers instantly from cache and quietly queues a
    /// refresh when the entry is missing or stale, so a panel drawing 40 listings at 60fps issues
    /// no synchronous work at all. Queued lookups are drained by a background pump that folds them
    /// into batch requests, so a full party plus a screen of contacts costs one request rather
    /// than thirty.
    /// </summary>
    internal sealed class RatingService : IDisposable
    {
        /// <summary>How long a fetched rating is considered fresh. Ratings move slowly - a player's
        /// average doesn't meaningfully change inside ten minutes - so this is generous on purpose.</summary>
        private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);

        /// <summary>Unknown players are re-checked sooner than known ones, so someone who has just
        /// received their first ratings shows up without a relog.</summary>
        private static readonly TimeSpan NegativeCacheTtl = TimeSpan.FromMinutes(2);

        /// <summary>How often the pump drains the queue. Short enough to feel immediate when you
        /// type a name, long enough that a scrolling overlay coalesces into one request.</summary>
        private static readonly TimeSpan PumpInterval = TimeSpan.FromMilliseconds(250);

        private sealed class CacheEntry
        {
            public PlayerRating? Value;
            public DateTime FetchedUtc;

            public bool IsFresh(TimeSpan ttl) => DateTime.UtcNow - FetchedUtc < ttl;
            public bool IsFresh() => IsFresh(Value == null ? NegativeCacheTtl : CacheTtl);
        }

        private readonly PfApiClient api;
        private readonly Configuration config;
        private readonly IPluginLog log;

        /// <summary>The record of who this player has actually played with. Rating is gated on it,
        /// so it is a dependency of the service rather than something the UI checks by hand -
        /// a gate that lives in the UI is a gate every new screen can forget.</summary>
        private readonly EncounterStore encounters;

        /// <summary>What this install has rated. Consulted alongside the cooldown list so the lock
        /// survives either file being lost.</summary>
        private readonly RatingHistory history;

        /// <summary>The permanent player list, which doubles as the on-disk cache for jobs and for
        /// the last rating seen. Survives a reload, unlike the in-memory caches here.</summary>
        private readonly PlayerHistory players;

        private readonly ConcurrentDictionary<string, CacheEntry> cache = new();

        /// <summary>Keys queued or in flight, so the same name can't be requested twice over.</summary>
        private readonly ConcurrentDictionary<string, CharacterIdentity> pending = new();

        private readonly CancellationTokenSource cancel = new();

        public RatingPolicy Policy { get; private set; } = RatingPolicy.Default;

        /// <summary>The rating server actually in use, surfaced so settings can show it.</summary>
        public string Endpoint => api.Endpoint;

        public RatingService(PfApiClient api, Configuration config, IPluginLog log,
            EncounterStore encounters, RatingHistory history, VoteQueue votes,
            PlayerHistory players)
        {
            this.api = api;
            this.players = players;
            this.votes = votes;
            this.config = config;
            this.log = log;
            this.encounters = encounters;
            this.history = history;

            // Recover Recent players for installs that rated before the history file existed,
            // and put job icons on anything still missing one.
            history.SeedFrom(config.LocalCooldowns, encounters.LastKnownJob);
            history.BackfillJobs(encounters.LastKnownJob);
            PruneLocalCooldowns();
            _ = Task.Run(PumpAsync);
            _ = Task.Run(FlushVotesAsync);
            _ = Task.Run(LoadPolicyAsync);
            _ = Task.Run(RefreshStatsAsync);
        }

        // ══════════════════════════════════════════════════════════
        //  READS
        // ══════════════════════════════════════════════════════════

        /// <summary>
        /// The cached rating for a player, or null if we don't have one yet. Safe to call every
        /// frame: it never waits on the network, and a miss or a stale entry just schedules a
        /// refresh. A stale value is returned rather than null while that refresh runs, so the UI
        /// doesn't flicker back to "loading" on every expiry.
        /// </summary>
        public PlayerRating? Get(CharacterIdentity who)
        {
            if (!who.IsValid)
                return null;

            if (cache.TryGetValue(who.Key, out var entry))
            {
                if (!entry.IsFresh())
                    Enqueue(who);
                return entry.Value;
            }

            Enqueue(who);

            // Nothing in memory, but the last rating seen for this player is on disk. Show it while
            // the refresh runs rather than a spinner: it is what was true when we last looked, and
            // it is replaced the moment the answer lands. A profile that opens with numbers already
            // on it and then corrects them reads as fast; one that opens blank reads as broken,
            // even when it takes the same time.
            //
            // Deliberately not written into `cache`, which would make it look fetched and stop the
            // refresh from being scheduled on the next draw.
            return players.RatingFor(who);
        }

        /// <summary>
        /// What is already known about this player, without asking for anything.
        ///
        /// <see cref="Get"/> reads and requests in one gesture, which is right for a party of eight
        /// and wrong for a list of everyone this install has ever met: calling it per row makes the
        /// length of that list the size of the request, and scrolling it a way to poll the server.
        ///
        /// This is the read half on its own. The caller decides separately, and for far fewer
        /// players, which of them are worth a lookup - see the recent players list, which asks only
        /// about the rows actually on screen.
        ///
        /// Falls back to disk for the same reason Get does: what was true when we last looked beats
        /// an empty column, and it is replaced the moment a real answer lands.
        /// </summary>
        public PlayerRating? Peek(CharacterIdentity who)
        {
            if (!who.IsValid)
                return null;

            return cache.TryGetValue(who.Key, out var entry) ? entry.Value : players.RatingFor(who);
        }

        /// <summary>True when a lookup for this player is queued or running and we have nothing
        /// cached yet - the UI's cue to draw a spinner rather than "no ratings".</summary>
        public bool IsLoading(CharacterIdentity who)
            => who.IsValid && !cache.ContainsKey(who.Key) && pending.ContainsKey(who.Key);

        /// <summary>Queues a batch of players (the party panel and Contacts tab use this). Cheap
        /// to call repeatedly - anything already cached and fresh is skipped.</summary>
        public void Prefetch(IEnumerable<CharacterIdentity> who)
        {
            foreach (var w in who)
            {
                if (!w.IsValid)
                    continue;
                if (cache.TryGetValue(w.Key, out var entry) && entry.IsFresh())
                    continue;
                Enqueue(w);
            }
        }

        /// <summary>Forces the next <see cref="Get"/> to refetch, e.g. after submitting a rating.</summary>
        public void Invalidate(CharacterIdentity who)
        {
            if (who.IsValid)
                cache.TryRemove(who.Key, out _);
        }

        /// <summary>Shortest gap between two forced refreshes of the same player. Opening a profile
        /// asks for one, and a profile is a thing people leave and come straight back to - so the
        /// gesture has to be free to repeat without it becoming a way to poll the server.</summary>
        private static readonly TimeSpan RefreshCooldown = TimeSpan.FromSeconds(5);

        private readonly ConcurrentDictionary<string, DateTime> lastRefresh = new();

        /// <summary>
        /// Re-reads one player's rating now, ignoring how fresh the cached copy looks.
        ///
        /// The cache holds a rating for ten minutes, which is right for a list of forty strangers
        /// and wrong for the one person whose card is open: two people in the same party rating
        /// somebody would each see their own vote land and then sit on a stale total until the
        /// entry expired. Opening their profile is the ask, and this answers it - at most once
        /// every five seconds per player, so holding the gesture down changes nothing.
        ///
        /// The cached value is deliberately left in place rather than invalidated: the numbers stay
        /// on screen while the refetch runs instead of blinking back to "loading".
        /// </summary>
        public bool Refresh(CharacterIdentity who)
        {
            if (!who.IsValid || !config.RatingsEnabled)
                return false;

            var now = DateTime.UtcNow;
            if (lastRefresh.TryGetValue(who.Key, out var last) && now - last < RefreshCooldown)
                return false;

            lastRefresh[who.Key] = now;
            PruneRefreshStamps(now);
            Enqueue(who);
            return true;
        }

        /// <summary>Drops refresh stamps that no longer gate anything, so a long session's worth of
        /// looked-up players doesn't accumulate.</summary>
        private void PruneRefreshStamps(DateTime now)
        {
            if (lastRefresh.Count < 64)
                return;

            foreach (var pair in lastRefresh)
            {
                if (now - pair.Value >= RefreshCooldown)
                    lastRefresh.TryRemove(pair.Key, out _);
            }
        }

        private void Enqueue(CharacterIdentity who) => pending.TryAdd(who.Key, who);

        // ══════════════════════════════════════════════════════════
        //  BACKGROUND PUMP
        // ══════════════════════════════════════════════════════════

        private async Task PumpAsync()
        {
            try
            {
                while (!cancel.IsCancellationRequested)
                {
                    await Task.Delay(PumpInterval, cancel.Token).ConfigureAwait(false);

                    if (!config.RatingsEnabled || pending.IsEmpty)
                        continue;

                    var batch = TakeBatch(Math.Max(1, Policy.BatchMax));
                    if (batch.Count == 0)
                        continue;

                    await FetchBatchAsync(batch).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // Plugin unloading - expected.
            }
            catch (Exception ex)
            {
                log.Debug($"[Ratings] Lookup pump ended: {ex.Message}");
            }
        }

        private List<CharacterIdentity> TakeBatch(int max)
        {
            var batch = new List<CharacterIdentity>(Math.Min(max, pending.Count));
            foreach (var kvp in pending)
            {
                if (batch.Count >= max)
                    break;
                batch.Add(kvp.Value);
            }
            return batch;
        }

        private async Task FetchBatchAsync(List<CharacterIdentity> batch)
        {
            try
            {
                var result = await api.LookupBatchAsync(batch).ConfigureAwait(false);

                if (result.IsOk)
                {
                    foreach (var rating in result.Value!.Players)
                        Store(new CharacterIdentity(rating.Name, rating.World), rating);

                    // Anything the server didn't mention has no ratings at all; cache that as a
                    // negative so the overlay stops asking about the same unrated players.
                    foreach (var who in batch)
                    {
                        if (!cache.ContainsKey(who.Key))
                            Store(who, null);
                    }
                    return;
                }

                // A rate limit or an outage must not leave these keys stuck in `pending` forever,
                // but nor should it be cached as "no ratings" - drop them and let the next draw
                // re-queue once the backoff has passed.
                if (result.Status == ApiStatus.Offline || result.Status == ApiStatus.RateLimited)
                    return;

                foreach (var who in batch)
                    Store(who, null);
            }
            finally
            {
                foreach (var who in batch)
                    pending.TryRemove(who.Key, out _);
            }
        }

        private void Store(CharacterIdentity who, PlayerRating? value)
        {
            if (value != null)
            {
                // Never trust a flag bit we don't recognise off the wire.
                value.Name = string.IsNullOrWhiteSpace(value.Name) ? who.Name : value.Name;
                value.World = string.IsNullOrWhiteSpace(value.World) ? who.World : value.World;
            }

            cache[who.Key] = new CacheEntry { Value = value, FetchedUtc = DateTime.UtcNow };

            // Kept on disk so the next view of this player has something to show before the network
            // answers. Only real ratings: "no ratings yet" is the default state and writing a row
            // for every unrated stranger the overlay asks about would fill the file with nothing.
            if (value != null)
                players.RememberRating(who, value);
        }

        private async Task LoadPolicyAsync()
        {
            try
            {
                var result = await api.GetPolicyAsync().ConfigureAwait(false);
                if (result.IsOk)
                    Policy = result.Value!;
            }
            catch (Exception)
            {
                // The defaults are correct as of this build; a fetch failure just means the client
                // explains the rules using them instead of the server's current numbers.
            }
        }

        // ══════════════════════════════════════════════════════════
        //  COOLDOWN (client side)
        // ══════════════════════════════════════════════════════════

        /// <summary>
        /// When the local player may next rate this target, or null if they may now. This is the
        /// first of the two cooldown checks and exists purely so the button greys out instantly
        /// with no round-trip - the server's check in the submit transaction is the one that
        /// actually enforces the rule, and it is authoritative if the two ever disagree.
        /// </summary>
        public DateTime? LocalCooldownUntil(CharacterIdentity who)
        {
            if (!who.IsValid)
                return null;

            DateTime? last = null;

            if (config.LocalCooldowns.TryGetValue(who.Key, out var fromConfig))
                last = Normalise(fromConfig);

            // Checked as well as the cooldown list, not instead of it. Two independent files
            // record the same fact, so losing or hand-editing one doesn't reopen the window.
            var fromHistory = history.LastRated(who);
            if (fromHistory.HasValue && (last == null || fromHistory.Value > last.Value))
                last = Normalise(fromHistory.Value);

            if (last == null)
                return null;

            var until = last.Value.AddHours(Math.Max(1, Policy.CooldownHours));
            return until > DateTime.UtcNow ? until : null;
        }

        /// <summary>Forces a timestamp to UTC. A value round-tripped through JSON can come back
        /// Unspecified or Local, and comparing that against UtcNow is wrong by the offset.</summary>
        private static DateTime Normalise(DateTime value) => value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        };

        /// <summary>
        /// The people who can actually be rated right now: met in a duty inside the rating window,
        /// and not already rated in the last 24 hours.
        ///
        /// The cooldown check is the important half. The encounter log's per-member "rated" flag
        /// is a UI convenience and is scoped to one encounter; the cooldown is the real rule and
        /// lives in the config, which survives a reload. Filtering on the flag alone is what let a
        /// reload re-offer people who had already been rated that day.
        /// </summary>
        public List<Contact> EligibleToRate()
        {
            var candidates = encounters.EligibleToVote();
            var result = new List<Contact>(candidates.Count);

            foreach (var c in candidates)
            {
                if (LocalCooldownUntil(c.Identity) == null)
                    result.Add(c);
            }

            return result;
        }

        /// <summary>Whether the local player has finished a duty with this character. Rating is
        /// only ever offered for people this returns true for.</summary>
        public bool HasMet(CharacterIdentity who) => encounters.HasMet(who);

        public bool CanRate(CharacterIdentity who)
        {
            if (!HasMet(who))
                return false;

            if (LocalCooldownUntil(who) != null)
                return false;

            var cached = Get(who);
            return cached?.OptedOut != true;
        }

        /// <summary>
        /// Records that a rating just happened, stamped from our own clock.
        ///
        /// Deliberately not derived from the server's nextEligibleAt by subtracting the cooldown:
        /// that timestamp arrives through JSON with whatever DateTimeKind the parser felt like,
        /// and an hour's drift there silently reopens the window early.
        /// </summary>
        private void RecordLocalCooldown(CharacterIdentity who)
        {
            config.LocalCooldowns[who.Key] = DateTime.UtcNow;
            config.Save();
        }

        /// <summary>Drops cooldown entries that have expired, so the config doesn't accumulate an
        /// entry for every player ever rated.</summary>
        private void PruneLocalCooldowns()
        {
            var cutoff = DateTime.UtcNow.AddHours(-Math.Max(1, Policy.CooldownHours));
            var stale = new List<string>();
            foreach (var kvp in config.LocalCooldowns)
            {
                if (Normalise(kvp.Value) < cutoff)
                    stale.Add(kvp.Key);
            }

            if (stale.Count == 0)
                return;

            foreach (var key in stale)
                config.LocalCooldowns.Remove(key);
            config.Save();
        }

        // ══════════════════════════════════════════════════════════
        //  WRITES
        // ══════════════════════════════════════════════════════════

        /// <summary>
        /// Submits a rating. Returns a result the UI can show verbatim; nothing here throws.
        /// The weight sent alongside is advisory - the server recomputes it from its own ledger,
        /// because a client-supplied weight is a client-controlled weight.
        /// </summary>
        public async Task<SubmitResult> SubmitAsync(
            CharacterIdentity target,
            VoteDirection vote,
            int tags,
            int dutyRowId,
            SocialLink socialLink,
            DateTime metAtUtc)
        {
            // Enforced here rather than only in the UI: every rating path goes through this
            // method, so this is the one place the rule cannot be routed around.
            if (!HasMet(target))
                return new SubmitResult { Outcome = SubmitOutcome.NotMet };

            if (LocalCooldownUntil(target) is { } until)
                return new SubmitResult { Outcome = SubmitOutcome.OnCooldown, NextEligibleAt = until };

            // Taken into the queue first, always. The click is never refused for pacing: the
            // vote is recorded locally and sent when the hour allows, so the player sees their
            // rating land and never sees a quota.
            var queued = votes.Enqueue(target, vote, tags & RatingTags.KnownMask,
                dutyRowId, socialLink, metAtUtc);

            if (!votes.CanSendNow())
            {
                RecordLocalCooldown(target);
                Invalidate(target);
                return new SubmitResult { Outcome = SubmitOutcome.Submitted, WeightApplied = 1d };
            }

            var request = new SubmitRatingRequest
            {
                VoteId = queued.VoteId,
                Target = target,
                Score = vote == VoteDirection.Up ? 1 : -1,
                Tags = tags & RatingTags.KnownMask,
                DutyRowId = dutyRowId,
                SocialLink = socialLink,
                MetAt = metAtUtc,
            };

            var result = await api.SubmitAsync(request).ConfigureAwait(false);

            switch (result.Status)
            {
                case ApiStatus.Ok:
                    votes.Accepted(queued);
                    RecordLocalCooldown(target);
                    Invalidate(target);
                    Enqueue(target);
                    // A 200 whose body didn't parse still means the server took the vote, so this
                    // stays an accept - only the weight and the next-eligible time are unknown,
                    // and 1x is the assumption the queued-locally paths above already make.
                    return new SubmitResult
                    {
                        Outcome = SubmitOutcome.Submitted,
                        WeightApplied = result.Value?.WeightApplied ?? 1d,
                        NextEligibleAt = result.Value?.NextEligibleAt,
                    };

                // Held, not failed. The vote is already in the queue and the flush loop will
                // deliver it, so telling the player it was refused would be false - and an error
                // for something the plugin has quietly taken care of is exactly what the queue
                // exists to avoid. The local cooldown is recorded now so the button stops
                // offering itself in the meantime.
                case ApiStatus.RateLimited:
                case ApiStatus.Offline:
                case ApiStatus.NoSession:
                case ApiStatus.ServerError:
                    RecordLocalCooldown(target);
                    Invalidate(target);
                    return new SubmitResult { Outcome = SubmitOutcome.Submitted, WeightApplied = 1d };

                case ApiStatus.Cooldown:
                    // The server knows about a rating this client had forgotten - another install,
                    // or a lost config. Record it locally so the UI stops offering the button.
                    votes.Failed(queued, permanent: true);
                    RecordLocalCooldown(target);
                    var serverUntil = result.RetryAfter.HasValue
                        ? DateTime.UtcNow.Add(result.RetryAfter.Value)
                        : (DateTime?)null;
                    return new SubmitResult { Outcome = SubmitOutcome.OnCooldown, NextEligibleAt = serverUntil };

                case ApiStatus.Refused:
                    votes.Failed(queued, permanent: true);
                    Invalidate(target);
                    return new SubmitResult { Outcome = SubmitOutcome.OptedOut };

                default:
                    votes.Failed(queued, permanent: true);
                    return new SubmitResult { Outcome = SubmitOutcome.Rejected };
            }
        }

        /// <summary>Community-wide totals for the analytics view, or null if unavailable. Cached
        /// for the session - these are headline numbers, not something to re-fetch per frame.</summary>
        public RatingStats? Stats { get; private set; }

        public async Task RefreshStatsAsync()
        {
            try
            {
                var result = await api.GetStatsAsync().ConfigureAwait(false);
                if (result.IsOk)
                    Stats = result.Value;
            }
            catch (Exception)
            {
                // Headline numbers are decoration; failing to get them changes nothing.
            }
        }

        /// <summary>
        /// Sends whatever the hourly allowance permits, forever.
        ///
        /// Deliberately unhurried. Queued votes are not urgent - nobody is waiting on one - and a
        /// tight loop would spend the allowance the instant it refreshes, which is the behaviour
        /// the pacing exists to prevent. A permanent refusal drops the vote rather than retrying
        /// it, because the queue is ordered and a stuck head starves everything behind it.
        /// </summary>
        private async Task FlushVotesAsync()
        {
            while (!cancel.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(20), cancel.Token).ConfigureAwait(false);

                    var next = votes.Next();
                    if (next == null)
                        continue;

                    var result = await api.SubmitAsync(new SubmitRatingRequest
                    {
                        VoteId = next.VoteId,
                        Target = next.Target,
                        Score = next.Score,
                        Tags = next.Tags,
                        DutyRowId = next.DutyRowId,
                        SocialLink = next.SocialLink,
                        MetAt = next.MetAtUtc,
                    }).ConfigureAwait(false);

                    switch (result.Status)
                    {
                        case ApiStatus.Ok:
                            votes.Accepted(next);
                            Invalidate(next.Target);
                            break;

                        // Still over the line, or unreachable. Keep it and try later.
                        case ApiStatus.RateLimited:
                        case ApiStatus.Offline:
                        case ApiStatus.NoSession:
                        case ApiStatus.ServerError:
                            break;

                        // Refused for a reason resending cannot change.
                        default:
                            votes.Failed(next, permanent: true);
                            break;
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    log.Debug($"[Ratings] Vote flush failed: {ex.Message}");
                }
            }
        }

        // ══════════════════════════════════════════════════════════
        //  CHARACTER DETAILS
        // ══════════════════════════════════════════════════════════

        /// <summary>Votes that haven't reached the server yet. Owned here because every rating
        /// path goes through SubmitAsync, so this is the one place that can guarantee a vote is
        /// recorded before it is sent.</summary>
        private readonly VoteQueue votes;

        private readonly ConcurrentDictionary<string, CharacterInfo> characters = new();
        private readonly ConcurrentDictionary<string, byte> characterPending = new();

        /// <summary>
        /// Job and level for a character, or null until it arrives.
        ///
        /// Safe to call every frame: a miss queues one fetch and returns null, and the server
        /// answers from its own cache after the first viewer anywhere has looked.
        /// </summary>
        public CharacterInfo? CharacterFor(CharacterIdentity who)
        {
            if (!who.IsValid)
                return null;

            if (characters.TryGetValue(who.Key, out var found))
                return found;

            // The local list first, and often last. Anyone met in a duty or seen in a party had
            // their job read straight off the game, which is better than the network copy and free;
            // Tomestone is only worth asking when nothing is known or what is known has gone stale.
            uint knownJob = players.JobFor(who);
            bool needsLookup = players.NeedsJobLookup(who);

            if (knownJob != 0 && !needsLookup)
            {
                // Cached in memory too, so the next frame doesn't re-take the store's lock. The
                // job name and level are left empty: they are only ever shown beside the icon on
                // the profile card, and the icon is what the id gives us.
                var local = new CharacterInfo
                {
                    Name = who.Name,
                    World = who.World,
                    JobId = knownJob,
                };
                characters[who.Key] = local;
                return local;
            }

            if (!characterPending.TryAdd(who.Key, 0))
            {
                // A lookup is already running. Show the stale job rather than nothing while it
                // does - an icon that is a week old beats an empty space.
                return knownJob == 0
                    ? null
                    : new CharacterInfo { Name = who.Name, World = who.World, JobId = knownJob };
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    var result = await api.GetCharacterAsync(who).ConfigureAwait(false);
                    if (result.IsOk && result.Value != null)
                    {
                        characters[who.Key] = result.Value;

                        // Written back so the week's grace starts now and survives a reload.
                        // Flagged as not-from-game, so a later sighting in a party overrides it.
                        players.RememberJob(who, result.Value.JobId, fromGame: false);
                        players.FlushIfDue();
                    }
                }
                catch (Exception ex)
                {
                    log.Debug($"[Ratings] Character lookup failed: {ex.Message}");
                }
                finally
                {
                    characterPending.TryRemove(who.Key, out _);
                }
            });

            return knownJob == 0
                ? null
                : new CharacterInfo { Name = who.Name, World = who.World, JobId = knownJob };
        }

        /// <summary>
        /// Records a job read off the game itself - the party list, a recruitment card.
        ///
        /// Cheap enough for a draw loop: it is a dictionary lookup that does nothing unless the job
        /// actually changed, and the file write is deferred.
        /// </summary>
        public void ObserveJob(CharacterIdentity who, uint jobId)
        {
            if (!who.IsValid || jobId == 0)
                return;

            players.RememberJob(who, jobId, fromGame: true);

            // Drop the in-memory copy so the next read picks the sighting up rather than serving
            // whatever the network said earlier.
            if (characters.TryGetValue(who.Key, out var cached) && cached.JobId != jobId)
                characters.TryRemove(who.Key, out _);
        }

        // ══════════════════════════════════════════════════════════
        //  PROGRESSION
        // ══════════════════════════════════════════════════════════

        /// <summary>
        /// What every row draws from, replaced wholesale rather than edited in place.
        ///
        /// Not readonly, and that is the point - see <see cref="Apply"/>. The UI reads this on the
        /// main thread every frame while responses land on background ones, so an update has to be
        /// a single reference swap rather than a clear followed by a refill.
        /// </summary>
        private volatile ConcurrentDictionary<string, PlayerProgress> progress = new();
        private string progressDuty = string.Empty;
        /// <summary>
        /// A request is in flight. Guards re-entrancy only - it is deliberately not surfaced.
        ///
        /// It used to drive a "fetching..." line in the panel, from when the request did the
        /// fetching itself and took seconds. It now hands the party to the server's queue and
        /// returns almost at once, and the same call is what the panel makes automatically when
        /// it opens - so showing it announced work the user hadn't asked for, for one frame.
        /// </summary>
        private volatile bool progressFetching;

        private volatile string? progressNote;

        private DateTime progressNoteUntil = DateTime.MaxValue;

        /// <summary>A sentence to show instead of badges, or null when there is nothing to say.
        /// Transient failures expire so the row recovers without a reload.</summary>
        public string? ProgressNote =>
            DateTime.UtcNow <= progressNoteUntil ? progressNote : null;

        /// <summary>Whether anything has been fetched for this duty yet.</summary>
        public bool HasProgressFor(string dutyName)
            => !progress.IsEmpty && string.Equals(progressDuty, dutyName, StringComparison.OrdinalIgnoreCase);

        /// <summary>The duty the server has confirmed it can answer about, or empty.</summary>
        private string progressEncounter = string.Empty;

        /// <summary>
        /// Whether the server recognises this duty as an encounter it has data for.
        ///
        /// Not the same question as <see cref="HasProgressFor"/>, which is only true once somebody
        /// has a prog point to show. A party in a fight none of them has ever logged has no
        /// progress and a perfectly good encounter, and that is exactly the case the per-row fetch
        /// button exists for - so it asks this instead.
        /// </summary>
        public bool HasEncounterFor(string dutyName)
            => !string.IsNullOrEmpty(progressEncounter)
               && string.Equals(progressEncounter, dutyName, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Characters asked about individually and not yet answered, keyed like
        /// <see cref="progress"/>.
        ///
        /// Not a rate limit - the server holds its own per-character cooldown and is the authority
        /// on that. This only stops one person being put on the queue twice by an impatient second
        /// press, and lapses on its own in case the answer never comes.
        /// </summary>
        private readonly ConcurrentDictionary<string, DateTime> playerAsked = new();

        private static readonly TimeSpan PlayerAskWait = TimeSpan.FromSeconds(60);

        /// <summary>How long the party button rests after a single character is asked about. Half
        /// of what a party press costs, because it put an eighth of the work on the belt.</summary>
        private static readonly TimeSpan SinglePressRest = TimeSpan.FromSeconds(30);

        /// <summary>True while this character has been asked about and hasn't answered yet.</summary>
        public bool PlayerProgressPending(CharacterIdentity who)
            => who.IsValid && playerAsked.TryGetValue(who.Key, out var until) && DateTime.UtcNow < until;

        /// <summary>Characters put on the belt this session, so an answer that never came can be
        /// told apart from a character nobody ever asked about.</summary>
        private readonly ConcurrentDictionary<string, byte> playerRequested = new();

        /// <summary>Characters the belt took, tried, and came back from with nothing.</summary>
        private readonly ConcurrentDictionary<string, byte> playerFailed = new();

        /// <summary>
        /// True when this character was queued, left the queue, and still has no stored row.
        ///
        /// The server abandons a job after its attempts and says nothing about it - from the
        /// outside that is identical to never having asked, which would put the row back to an
        /// inviting "Fetch" as though the last eight minutes hadn't happened. Somebody who presses
        /// a button, waits, and is handed the same button back is owed the difference.
        /// </summary>
        public bool PlayerProgressFailed(CharacterIdentity who)
            => who.IsValid && playerFailed.ContainsKey(who.Key);

        /// <summary>The server's per-character cooldown in seconds, or 0 before it has said.</summary>
        private volatile int progressRefreshAfterSec;

        /// <summary>
        /// How long before the server would actually re-read this character, or zero if now.
        ///
        /// The belt refuses work inside this window: a press on a character read four minutes ago
        /// is accepted by the route, matched against the stored row, and quietly not queued. That
        /// is correct, and it is also indistinguishable from a broken button unless the row that
        /// offers the press knows about the window too - which is what this is for.
        /// </summary>
        public TimeSpan PlayerRefreshWait(CharacterIdentity who)
        {
            if (progressRefreshAfterSec <= 0)
                return TimeSpan.Zero;

            var p = ProgressFor(who);

            // No stored row on the server, so nothing to be inside the cooldown of.
            if (p == null || string.IsNullOrEmpty(p.Status) || p.Status is "unfetched" or "queued")
                return TimeSpan.Zero;

            // AgeSec is how old the row was when the server answered. The answer then ages on this
            // side of the wire too, and the panel can sit open a good deal longer than the window.
            double age = p.AgeSec + (DateTime.UtcNow - p.AppliedAt).TotalSeconds;
            double left = progressRefreshAfterSec - age;

            return left > 0 ? TimeSpan.FromSeconds(left) : TimeSpan.Zero;
        }

        /// <summary>This character's standing on the duty last fetched, or null if not fetched.</summary>
        public PlayerProgress? ProgressFor(CharacterIdentity who)
            => who.IsValid && progress.TryGetValue(who.Key, out var found) ? found : null;

        /// <summary>
        /// Fetches progression for a party.
        ///
        /// Never called on a timer or on party change. Every other request this plugin makes stays
        /// between the user and their own server; this one ends up at FFLogs, so it happens when
        /// somebody asks for it and not before.
        /// </summary>
        /// <summary>When the refresh button becomes pressable again.</summary>
        private DateTime progressButtonUntil = DateTime.MinValue;

        /// <summary>Duty + party the last read covered, so opening a panel reads once rather than
        /// every frame.</summary>
        private string progressReadFor = string.Empty;

        public bool ProgressButtonReady => DateTime.UtcNow >= progressButtonUntil;

        public TimeSpan ProgressButtonWait =>
            progressButtonUntil > DateTime.UtcNow ? progressButtonUntil - DateTime.UtcNow : TimeSpan.Zero;

        /// <summary>When the last read went out, so the wait below is measured from something.</summary>
        private DateTime progressReadAt = DateTime.MinValue;

        /// <summary>
        /// How often the panel reads again while it is still waiting on the belt.
        ///
        /// Slow on purpose. The fast polling belongs to the queue watch, which runs for the first
        /// minute after a press and asks every few seconds; this is the long tail behind it, and a
        /// character deep in its retries is minutes rather than seconds away.
        /// </summary>
        private static readonly TimeSpan ReadAgainAfter = TimeSpan.FromSeconds(15);

        /// <summary>Whether anything on screen is still waiting on the server's queue.</summary>
        private bool AnythingQueued()
        {
            foreach (var p in progress.Values)
            {
                if (p.Queued)
                    return true;
            }

            // Asked about but not yet answered even once. Entries here lapse by time rather than
            // being removed, so an unexpired one is the thing to look for.
            var now = DateTime.UtcNow;
            foreach (var until in playerAsked.Values)
            {
                if (now < until)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Loads whatever the server already knows: once per duty and party, and again while an
        /// answer is still owed.
        ///
        /// Costs nothing beyond a database read on the server, so a panel opening always shows the
        /// last known prog rather than an empty row waiting for someone to press a button.
        ///
        /// The second half of that is not an optimisation, it is the whole of a bug. Reading only
        /// when the party changed meant that once the queue watch gave up after its minute, nothing
        /// ever read again - and the last thing applied still said "queued". The belt does not work
        /// to the client's timetable: a character that needs its retries is minutes away, and the
        /// row went on claiming to be queued for the rest of the session while the answer sat in
        /// the server's table. Whether anyone is still waiting is the server's call, not ours -
        /// `queued` comes off the belt itself, and every job leaves it eventually, fetched or
        /// abandoned. So this keeps asking until it says no.
        /// </summary>
        public void EnsureProgressLoaded(string dutyName, IReadOnlyList<(CharacterIdentity Who, string Region)> party)
        {
            if (string.IsNullOrWhiteSpace(dutyName) || party.Count == 0)
                return;

            var sig = new System.Text.StringBuilder(dutyName);
            foreach (var (who, _) in party)
            {
                sig.Append('|');
                sig.Append(who.Key);
            }
            string signature = sig.ToString();

            bool moved = signature != progressReadFor;
            bool due = !moved
                && AnythingQueued()
                && DateTime.UtcNow - progressReadAt >= ReadAgainAfter;

            if (!moved && !due)
                return;

            progressReadFor = signature;
            progressReadAt = DateTime.UtcNow;
            RequestPartyProgress(dutyName, party, refresh: false);
        }

        public void RequestPartyProgress(string dutyName,
            IReadOnlyList<(CharacterIdentity Who, string Region)> party, bool refresh = true)
        {
            if (progressFetching || party.Count == 0 || string.IsNullOrWhiteSpace(dutyName))
                return;

            if (refresh && !ProgressButtonReady)
                return;

            progressFetching = true;
            progressNote = null;

            if (refresh)
            {
                // The button rests for a minute after a press. The server's own per-character
                // cooldown decides who actually gets refreshed; this just stops the button being
                // held down.
                progressButtonUntil = DateTime.UtcNow.AddMinutes(1);
            }

            // Whoever we know least about goes to the front of the request.
            //
            // The server works its queue in the order it is handed, one character at a time on
            // behalf of everybody using the plugin - so on a busy evening the tail of a party of
            // eight may wait a while for its turn. Spending that turn re-reading someone whose
            // prog point is already on screen, ahead of the row that is still blank, is the wrong
            // way round: the blank row is the reason the button was pressed.
            var ordered = refresh
                ? party.OrderBy(p => ProgressPriority(p.Who)).ToList()
                : (IReadOnlyList<(CharacterIdentity Who, string Region)>)party;

            var players = ToRequestPlayers(ordered);
            if (players.Count == 0)
            {
                progressFetching = false;
                return;
            }

            Send(dutyName, players, refresh, merge: false, () => progressFetching = false);
        }

        /// <summary>
        /// Refreshes one character rather than the whole party.
        ///
        /// The same request with one name in it. The queue is global and moves one character at a
        /// time, so asking about the single person you actually care about is the difference
        /// between an answer now and an answer behind seven other people's lookups - and it leaves
        /// the other seven turns for somebody who needs them.
        /// </summary>
        public void RequestPlayerProgress(string dutyName, CharacterIdentity who, string region)
        {
            if (!who.IsValid || string.IsNullOrEmpty(region) || string.IsNullOrWhiteSpace(dutyName))
                return;

            if (PlayerProgressPending(who))
                return;

            // Held until the answer lands, and released early by Apply when it does. The row's own
            // button reads this, so a second press can't queue the same person twice.
            playerAsked[who.Key] = DateTime.UtcNow.Add(PlayerAskWait);

            // The party button rests too, briefly.
            //
            // A single ask is cheap next to a party of eight, but it is the same belt and the same
            // turn: pressing Fetch on one row and Update on the party a second later puts nine
            // characters on it from one person, ahead of everybody else waiting. Extended rather
            // than assigned, so this can only ever lengthen a wait a party press already started.
            var rest = DateTime.UtcNow.Add(SinglePressRest);
            if (rest > progressButtonUntil)
                progressButtonUntil = rest;

            var players = ToRequestPlayers(new[] { (who, region) });
            if (players.Count == 0)
            {
                playerAsked.TryRemove(who.Key, out _);
                return;
            }

            Send(dutyName, players, refresh: true, merge: true, null);
        }

        private static List<ProgressPlayer> ToRequestPlayers(
            IEnumerable<(CharacterIdentity Who, string Region)> party)
        {
            var players = new List<ProgressPlayer>();
            foreach (var (who, region) in party)
            {
                if (who.IsValid && !string.IsNullOrEmpty(region))
                    players.Add(new ProgressPlayer { Name = who.Name, World = who.World, Region = region });
            }
            return players;
        }

        /// <summary>
        /// How badly this character needs the belt's attention. Lower goes first.
        ///
        /// The statuses are the server's, and "unfetched" is one of them - it is what a character
        /// with no stored row comes back as, so it means the same as having no entry here at all.
        /// Reading it as data was the whole bug this ordering exists to avoid.
        /// </summary>
        private int ProgressPriority(CharacterIdentity who)
        {
            if (!who.IsValid || !progress.TryGetValue(who.Key, out var p) || string.IsNullOrEmpty(p.Status))
                return 0;   // never looked up - the blank row

            if (p.Status is "unfetched" or "queued")
                return 0;   // the server's way of saying the same thing

            if (p.Status is "unknown" or "notfound")
                return 1;   // looked up and nothing came back; worth another try

            return 2;       // already showing something
        }

        /// <summary>
        /// One request out, one answer applied, and a queue watch if the server parked any of it.
        ///
        /// <paramref name="merge"/> separates the two callers: a party answer is the whole truth
        /// for the duty and replaces what was on screen, a single-character answer is one row of
        /// it and must not blank the other seven.
        /// </summary>
        private void Send(string dutyName, List<ProgressPlayer> players, bool refresh, bool merge,
            Action? done)
        {
            var request = new ProgressRequest { DutyName = dutyName, Refresh = refresh };
            request.Players.AddRange(players);

            // Only a refresh puts anything on the belt, so only a refresh starts anyone owing an
            // answer. A plain read must not mark a character as asked-about - it would turn every
            // never-fetched row into a failed one the moment the panel opened.
            if (refresh)
            {
                // Counted here for the same reason: a read is a poll of what the server already
                // holds and happens on its own, so counting it would measure the panel being open
                // rather than anybody looking anything up.
                config.CountProgressFetch();
                config.Save();

                foreach (var p in players)
                {
                    string key = new CharacterIdentity(p.Name, p.World).Key;
                    playerRequested[key] = 0;
                    playerFailed.TryRemove(key, out _);
                }
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    var result = await api.GetProgressAsync(request).ConfigureAwait(false);

                    if (!result.IsOk || result.Value == null)
                    {
                        progressNote = result.Status == ApiStatus.Offline
                            ? "Couldn't reach the server."
                            : "Couldn't fetch progress right now.";

                        // Cleared after a while so the row recovers on its own. It used to sit
                        // there permanently, which made a single failed call look like the feature
                        // was broken until the plugin was reloaded.
                        progressNoteUntil = DateTime.UtcNow.AddSeconds(12);

                        // A failure shouldn't cost the whole cooldown either. A single-character
                        // ask never touches the party button at all: it didn't spend the party's
                        // turn, so it has no business disabling the party's button.
                        if (merge)
                            ReleaseAsked(request);
                        else
                            progressButtonUntil = DateTime.UtcNow.AddSeconds(10);
                        return;
                    }

                    // The server fetches nothing inside the request any more - a refresh puts the
                    // stale names on its global queue and answers with what it already had.
                    // Anyone still pending is waited on below.
                    if (Apply(result.Value, dutyName, merge))
                        WaitForQueue(dutyName, request, result.Value.Queue, merge);
                    else if (merge)
                        ReleaseAsked(request);
                }
                catch (Exception ex)
                {
                    log.Debug($"[Ratings] Progress fetch failed: {ex.Message}");
                    progressNote = "Couldn't fetch progress right now.";
                    if (merge)
                        ReleaseAsked(request);
                }
                finally
                {
                    done?.Invoke();
                }
            });
        }

        /// <summary>Lets go of the per-character hold, so the row's button comes back rather than
        /// sitting out the full minute over a request that failed in a second.</summary>
        private void ReleaseAsked(ProgressRequest request)
        {
            foreach (var p in request.Players)
                playerAsked.TryRemove(new CharacterIdentity(p.Name, p.World).Key, out _);
        }

        /// <summary>
        /// Takes one server answer and makes it what the panel shows.
        ///
        /// Replaces rather than merges: the server holds the whole truth for this duty, and a
        /// merge would keep showing a character who has since dropped out of the party.
        /// </summary>
        /// <returns>False when the response was an answer with nothing in it to display.</returns>
        private bool Apply(ProgressResponse response, string dutyName, bool merge)
        {
            bool sameDuty = string.Equals(progressDuty, dutyName, StringComparison.OrdinalIgnoreCase);

            // Built alongside the live one and swapped in at the end, never cleared in place.
            //
            // This used to Clear() and refill, which left a window where the dictionary was empty
            // or half-populated - and the UI reads it every frame from another thread. A frame
            // landing in that window drew a party with no progress at all, so a row showing
            // "(Cleared)" flicked back to "Fetch" and then back again. Rare enough to look random
            // until the panel started re-reading every fifteen seconds, which made it frequent.
            //
            // A single-character answer is folded into what is already there: it speaks for one
            // row, and replacing on it would blank the other seven to update one of them. Changing
            // duty still starts empty, merge or not - the old fight's numbers mean nothing here.
            var next = merge && sameDuty
                ? new ConcurrentDictionary<string, PlayerProgress>(progress)
                : new ConcurrentDictionary<string, PlayerProgress>();

            progressDuty = dutyName;

            if (response.Encounter == null)
            {
                progress = next;
                // Names no provider on purpose. Progression comes from Tomestone with FFLogs
                // behind it, and which one answered is not the user's problem - naming one made a
                // mapping gap read as that site being down.
                progressEncounter = string.Empty;
                progressNote = "No progress data for this duty.";
                return false;
            }

            progressEncounter = dutyName;

            if (response.RefreshAfterSec > 0)
                progressRefreshAfterSec = response.RefreshAfterSec;

            // How long the belt is, service-wide. The single most useful thing to tell somebody
            // watching a "Queued" label: the wait is a queue rather than a lookup, and its length
            // is a fact about how many other people are raiding right now, not about them.
            if (response.Queue != null)
            {
                progressQueueSize = response.Queue.Size;
                if (response.Queue.PollAfterSec > 0)
                    progressQueuePollSec = response.Queue.PollAfterSec;
            }

            foreach (var p in response.Players)
            {
                if (string.IsNullOrEmpty(p.Status))
                    continue;

                // Stamped on arrival so AgeSec keeps counting afterwards - see PlayerRefreshWait.
                p.AppliedAt = DateTime.UtcNow;
                next[p.Key] = p;

                // Still on the belt: nothing is settled, and the hold stays.
                if (p.Queued)
                    continue;

                // Answered, one way or another, so the row's button comes back.
                playerAsked.TryRemove(p.Key, out _);

                // Off the queue with still nothing stored, having been put there by us. The belt
                // spent its attempts and gave up - which is a fact worth keeping, because the
                // alternative is offering the same press again as if it were fresh.
                if (p.Status == "unfetched")
                {
                    if (playerRequested.TryRemove(p.Key, out _))
                        playerFailed[p.Key] = 0;
                    continue;
                }

                playerRequested.TryRemove(p.Key, out _);
                playerFailed.TryRemove(p.Key, out _);
            }

            // The swap. Every row goes from the old answer to the new one between two frames,
            // with no frame in between that sees neither.
            progress = next;

            progressNote = null;
            progressNoteUntil = DateTime.MaxValue;
            return true;
        }

        // ══════════════════════════════════════════════════════════
        //  WAITING ON THE QUEUE
        // ══════════════════════════════════════════════════════════

        /// <summary>
        /// How long the button stays on "Queued" with nothing coming back.
        ///
        /// A minute is longer than the queue should ever take for one party, and short enough that
        /// a server that has quietly stopped moving doesn't leave the button stuck for the evening.
        /// Giving up here costs nothing on the server - the work stays queued and lands in the
        /// cache regardless, so the next time the panel opens it is simply there.
        /// </summary>
        private static readonly TimeSpan QueueWait = TimeSpan.FromMinutes(1);

        private volatile bool watchingQueue;
        private volatile int queuedCount;
        private DateTime queueWaitUntil = DateTime.MinValue;

        /// <summary>True while we are waiting on the server's queue for this party.</summary>
        public bool ProgressQueued => queuedCount > 0 && DateTime.UtcNow < queueWaitUntil;

        /// <summary>How many party members are still on the queue.</summary>
        public int ProgressQueuedCount => queuedCount;

        private volatile int progressQueueSize;
        private volatile int progressQueuePollSec = 3;

        /// <summary>Characters on the belt across the whole service, ours included.</summary>
        public int ProgressQueueSize => progressQueueSize;

        /// <summary>
        /// Roughly how long the whole belt takes to clear, as a way of explaining a wait.
        ///
        /// An estimate and presented as one. The belt moves at a fixed rate, so length times rate
        /// is the honest arithmetic - but somebody else's press lands behind ours, a character
        /// deep in its retries holds a turn without finishing, and neither shows up here.
        /// </summary>
        public TimeSpan ProgressQueueEta
            => TimeSpan.FromSeconds(progressQueueSize * Math.Max(1, progressQueuePollSec));

        /// <summary>
        /// Polls the server until the queue has caught up with this party, or a minute passes.
        ///
        /// Reads only - `refresh` stays false, so this never adds work. It is asking the same
        /// question the panel asks when it opens, just repeatedly, and the server answers it from
        /// its own table without touching a provider.
        /// </summary>
        /// <summary>What the running loop reads each time round, and how to apply it. Held in a
        /// field rather than captured, so a wider request can take the loop over - see below.</summary>
        private volatile ProgressRequest? pollRequest;
        private volatile bool pollMerge;

        private void WaitForQueue(string dutyName, ProgressRequest request, ProgressQueueInfo? queue,
            bool merge)
        {
            int pending = queue?.Pending ?? 0;
            if (pending <= 0)
                return;

            queuedCount = pending;
            queueWaitUntil = DateTime.UtcNow.Add(QueueWait);

            var read = new ProgressRequest { DutyName = dutyName, Refresh = false };
            read.Players.AddRange(request.Players);

            // A second press while one wait is running extends it rather than starting a rival
            // loop: two of these polling the same party would double the reads and race over
            // which one gets to say the wait is over.
            //
            // The running loop does hand over to the wider of the two reads, though. Fetching one
            // character and then pressing the party button would otherwise leave the loop asking
            // after that one name while the other seven answers sat unread on the server, and
            // nothing else would go looking for them until the party itself changed.
            if (watchingQueue)
            {
                var current = pollRequest;
                if (current == null || read.Players.Count > current.Players.Count)
                {
                    pollRequest = read;
                    pollMerge = merge;
                }
                return;
            }

            watchingQueue = true;
            pollRequest = read;
            pollMerge = merge;

            int pollSec = Math.Clamp(queue?.PollAfterSec ?? 3, 2, 10);

            _ = Task.Run(async () =>
            {
                try
                {
                    while (DateTime.UtcNow < queueWaitUntil && !cancel.IsCancellationRequested)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(pollSec), cancel.Token)
                            .ConfigureAwait(false);

                        var poll = pollRequest;
                        if (poll == null)
                            break;

                        var result = await api.GetProgressAsync(poll).ConfigureAwait(false);

                        // A failed poll is not a failed refresh. The queue is still working and
                        // the next poll may well succeed, so this says nothing to the user.
                        if (!result.IsOk || result.Value == null)
                            continue;

                        if (!Apply(result.Value, dutyName, pollMerge))
                            break;

                        int left = result.Value.Queue?.Pending ?? 0;
                        queuedCount = left;
                        if (left <= 0)
                            break;
                    }
                }
                catch (OperationCanceledException)
                {
                    // Unloading.
                }
                catch (Exception ex)
                {
                    log.Debug($"[Ratings] Queue wait ended: {ex.Message}");
                }
                finally
                {
                    queuedCount = 0;
                    watchingQueue = false;
                    pollRequest = null;
                }
            });
        }

        // ══════════════════════════════════════════════════════════
        //  REPORT LIMITS (client side)
        // ══════════════════════════════════════════════════════════

        /// <summary>
        /// Reports allowed per rolling hour from this install, and now the only limit on
        /// reporting. Matches the server exactly rather than sitting under it: with one rule
        /// instead of three, a client that agrees with the server can explain the wait precisely,
        /// and there is no second hidden ceiling to be surprised by.
        /// </summary>
        public const int ReportsPerHour = 10;

        /// <summary>
        /// Always null: there is no per-target cooldown any more.
        ///
        /// A separate block on reporting the same person was the wrong shape. It assumed a second
        /// report about someone is the first one restated, which is false for a genuinely new
        /// incident, and it locked people out for hours over it. The hourly allowance is the whole
        /// rule now. Kept as a method so the dialog has one place to ask, if a reason to refuse
        /// ever comes back.
        /// </summary>
        public DateTime? ReportCooldownUntil(CharacterIdentity who) => null;

        /// <summary>When the hourly allowance frees up again, or null if a report can be sent now.
        /// The oldest report inside the window is the one whose expiry matters.</summary>
        public DateTime? ReportQuotaFreeAt()
        {
            var cutoff = DateTime.UtcNow.AddHours(-1);

            DateTime? oldestInWindow = null;
            int used = 0;

            foreach (var stamp in config.ReportCooldowns.Values)
            {
                var at = Normalise(stamp);
                if (at < cutoff)
                    continue;

                used++;
                if (oldestInWindow == null || at < oldestInWindow.Value)
                    oldestInWindow = at;
            }

            if (used < ReportsPerHour || oldestInWindow == null)
                return null;

            return oldestInWindow.Value.AddHours(1);
        }

        /// <summary>Drops report timestamps that no longer gate anything, so the list stays small
        /// the way the rating cooldowns do. Only the last hour still counts toward anything.</summary>
        private void PruneReportCooldowns()
        {
            var cutoff = DateTime.UtcNow.AddHours(-1);
            var stale = new List<string>();

            foreach (var pair in config.ReportCooldowns)
            {
                if (Normalise(pair.Value) < cutoff)
                    stale.Add(pair.Key);
            }

            foreach (var key in stale)
                config.ReportCooldowns.Remove(key);
        }

        /// <summary>
        /// Sends a report about a player.
        ///
        /// Two client-side limits are checked first - one report per player per day, and three per
        /// hour overall. They are courtesy checks that keep an accidental double-click or a bad
        /// evening off the wire; the server enforces its own and is authoritative.
        /// </summary>
        public async Task<ReportSendResult> SubmitReportAsync(
            CharacterIdentity target, ReportReason reason, string note, int dutyRowId,
            CharacterIdentity? reporter = null)
        {
            if (ReportQuotaFreeAt() != null)
                return new ReportSendResult { Outcome = ReportOutcome.LocalLimit };

            var result = await api.SubmitReportAsync(new SubmitReportRequest
            {
                Target = target,

                // Null, not an empty identity. An empty one would serialise to {"name":"","world":""}
                // and be rejected by the server's validCharacter check anyway - but "we sent nothing"
                // is a stronger promise to the reporter than "we sent something that got discarded".
                Reporter = reporter is { IsValid: true } ? reporter : null,
                Reason = reason,
                Note = note.Length > 500 ? note.Substring(0, 500) : note,
                DutyRowId = dutyRowId,
            }).ConfigureAwait(false);

            // Recorded only on success. Marking it on a failed send would burn the daily slot for
            // someone whose report never arrived, and they would have no way to retry.
            if (result.IsOk && target.IsValid)
            {
                config.ReportCooldowns[target.Key] = DateTime.UtcNow;
                PruneReportCooldowns();
                config.Save();
            }

            // The status is carried back rather than flattened to a bool. It used to return
            // true/false, so a rate limit that lasts an hour was shown as "try again in a
            // moment" - which sent people straight back to the button to fail again.
            return new ReportSendResult
            {
                Outcome = result.Status switch
                {
                    ApiStatus.Ok => ReportOutcome.Sent,
                    ApiStatus.RateLimited => ReportOutcome.RateLimited,
                    ApiStatus.Offline or ApiStatus.NoSession => ReportOutcome.Offline,
                    _ => ReportOutcome.Failed,
                },
                RetryAfter = result.RetryAfter,
            };
        }

        /// <summary>Asks the server to erase this voter's cooldown ledger. Their submitted ratings
        /// are untouched because they carry no link back to them - there is nothing in them that
        /// identifies the voter to erase.</summary>
        public async Task ForgetServerHistoryAsync()
        {
            await api.DeleteMyLedgerAsync().ConfigureAwait(false);
        }

        public void Dispose()
        {
            try
            {
                cancel.Cancel();
                cancel.Dispose();
            }
            catch (Exception)
            {
                // Nothing useful to do while unloading.
            }
        }
    }
}
#endif
