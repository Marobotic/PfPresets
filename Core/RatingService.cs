#if PFP_RATINGS
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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

        private readonly ConcurrentDictionary<string, CacheEntry> cache = new();

        /// <summary>Keys queued or in flight, so the same name can't be requested twice over.</summary>
        private readonly ConcurrentDictionary<string, CharacterIdentity> pending = new();

        private readonly CancellationTokenSource cancel = new();

        public RatingPolicy Policy { get; private set; } = RatingPolicy.Default;

        /// <summary>The rating server actually in use, surfaced so settings can show it.</summary>
        public string Endpoint => api.Endpoint;

        public RatingService(PfApiClient api, Configuration config, IPluginLog log,
            EncounterStore encounters, RatingHistory history, VoteQueue votes)
        {
            this.api = api;
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
            return null;
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

            if (!characterPending.TryAdd(who.Key, 0))
                return null;

            _ = Task.Run(async () =>
            {
                try
                {
                    var result = await api.GetCharacterAsync(who).ConfigureAwait(false);
                    if (result.IsOk && result.Value != null)
                        characters[who.Key] = result.Value;
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

            return null;
        }

        // ══════════════════════════════════════════════════════════
        //  PROGRESSION
        // ══════════════════════════════════════════════════════════

        private readonly ConcurrentDictionary<string, PlayerProgress> progress = new();
        private string progressDuty = string.Empty;
        private volatile bool progressFetching;
        private volatile string? progressNote;

        /// <summary>True while a fetch is in flight, so the control can say so.</summary>
        public bool ProgressFetching => progressFetching;

        private DateTime progressNoteUntil = DateTime.MaxValue;

        /// <summary>A sentence to show instead of badges, or null when there is nothing to say.
        /// Transient failures expire so the row recovers without a reload.</summary>
        public string? ProgressNote =>
            DateTime.UtcNow <= progressNoteUntil ? progressNote : null;

        /// <summary>Whether anything has been fetched for this duty yet.</summary>
        public bool HasProgressFor(string dutyName)
            => !progress.IsEmpty && string.Equals(progressDuty, dutyName, StringComparison.OrdinalIgnoreCase);

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

        /// <summary>
        /// Loads whatever the server already knows, once per duty and party.
        ///
        /// Costs nothing beyond a database read on the server, so a panel opening always shows the
        /// last known prog rather than an empty row waiting for someone to press a button.
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
            if (signature == progressReadFor)
                return;

            progressReadFor = signature;
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

            var request = new ProgressRequest { DutyName = dutyName, Refresh = refresh };
            foreach (var (who, region) in party)
            {
                if (who.IsValid && !string.IsNullOrEmpty(region))
                    request.Players.Add(new ProgressPlayer { Name = who.Name, World = who.World, Region = region });
            }

            if (request.Players.Count == 0)
            {
                progressFetching = false;
                return;
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

                        // A failure shouldn't cost the whole cooldown either.
                        progressButtonUntil = DateTime.UtcNow.AddSeconds(10);
                        return;
                    }

                    progress.Clear();
                    progressDuty = dutyName;

                    if (result.Value.Encounter == null)
                    {
                        // Names no provider on purpose. Progression comes from Tomestone with
                        // FFLogs behind it, and which one answered is not the user's problem -
                        // naming one made a mapping gap read as that site being down.
                        progressNote = "No progress data for this duty.";
                        return;
                    }

                    foreach (var p in result.Value.Players)
                    {
                        if (!string.IsNullOrEmpty(p.Status))
                            progress[p.Key] = p;
                    }

                    progressNote = null;
                    progressNoteUntil = DateTime.MaxValue;
                }
                catch (Exception ex)
                {
                    log.Debug($"[Ratings] Progress fetch failed: {ex.Message}");
                    progressNote = "Couldn't fetch progress right now.";
                }
                finally
                {
                    progressFetching = false;
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
