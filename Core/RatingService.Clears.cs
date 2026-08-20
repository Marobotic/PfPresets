#if PFP_RATINGS
using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;

namespace PfPresets
{
    /// <summary>
    /// High-end clears for the profile card: what somebody has killed, and how they parsed.
    ///
    /// The same shape as progression, for the same reasons. Nothing is fetched because a card
    /// opened - opening a profile reads what the server already holds and costs a table lookup on
    /// our own box. Only the refresh button asks the server to go and look, and even then the
    /// server refuses if anyone has looked this character up within the hour. Once fetched, the
    /// answer belongs to everybody: the next plugin user to open that profile is served from the
    /// same row, having spent nothing.
    ///
    /// One request to the providers per character per hour, on a belt shared by every user of the
    /// plugin - see clearsQueue.js and lane.js on the server. That is the entire budget, and the
    /// reason this feature can be offered at all.
    /// </summary>
    internal sealed partial class RatingService
    {
        /// <summary>What each card draws from, keyed like everything else on "name@world".</summary>
        private readonly ConcurrentDictionary<string, ClearsResponse> clears = new();

        /// <summary>
        /// Requests in flight, so a card drawn every frame issues one rather than sixty.
        ///
        /// Keyed by character AND by kind. A press and a background poll are different requests
        /// with different consequences, and sharing one key meant a press that happened to land
        /// while the card's own poll was out was dropped on the floor - the button went busy for
        /// twenty seconds having asked for nothing.
        /// </summary>
        private readonly ConcurrentDictionary<string, byte> clearsInFlight = new();

        /// <summary>
        /// A press that failed, and when to try it again.
        ///
        /// Presses fail for reasons that pass on their own - the server still warming its fight
        /// list after a restart, a request that outran its timeout - and the honest response to
        /// those is to try again in a moment, not to tell somebody their button doesn't work.
        /// </summary>
        private readonly ConcurrentDictionary<string, (DateTime When, int Attempts)> clearsRetry = new();

        /// <summary>How long after a failed press to try again, and how many times before saying
        /// so out loud. Two retries covers a server that has just restarted.</summary>
        private static readonly TimeSpan ClearsRetryAfter = TimeSpan.FromSeconds(5);
        private const int ClearsMaxAttempts = 3;

        /// <summary>When each character was last read, so a card sitting open re-reads slowly
        /// while it waits for the belt and not at all once the answer has landed.</summary>
        private readonly ConcurrentDictionary<string, DateTime> clearsReadAt = new();

        private volatile string? clearsNote;
        private DateTime clearsNoteUntil = DateTime.MinValue;

        /// <summary>Something to say instead of sections, or null. Expires on its own so a single
        /// failed call doesn't look like a broken feature until the plugin is reloaded.</summary>
        public string? ClearsNote =>
            DateTime.UtcNow <= clearsNoteUntil ? clearsNote : null;

        /// <summary>
        /// How often a card that is waiting on the belt asks again.
        ///
        /// Only ever while something is queued. Once the answer has landed there is nothing to poll
        /// for - a character's clears change when they raid, not while somebody looks at them.
        /// </summary>
        private static readonly TimeSpan ClearsPollAfter = TimeSpan.FromSeconds(6);

        /// <summary>How long the button rests after a press, regardless of what the server does
        /// with it. The real cooldown is the server's hour; this only stops the button being held
        /// down while the belt is getting to it.</summary>
        private static readonly TimeSpan ClearsPressRest = TimeSpan.FromSeconds(20);

        private readonly ConcurrentDictionary<string, DateTime> clearsPressedAt = new();

        // ── The budget, and the character that never resolves ──────
        //
        // WHAT WENT WRONG, so it is not re-derived from scratch next time. Searching a character
        // who does not exist and pressing refresh sent hundreds of lookups and got the address
        // rate-limited at both ends.
        //
        // No single bug: three things that are each defensible alone.
        //
        //   1. The server answers 200 for any well-formed name. It does not check that a character
        //      exists - it cannot cheaply - it just queues the work.
        //   2. When the queue gives up on a character it stops being queued, but nothing writes a
        //      fetched_at row. So FetchedAt stays null.
        //   3. ClearsRefreshWait returns zero the moment !Fetched, because "never fetched" was
        //      meant to read as "nobody has looked yet, go ahead".
        //
        // Together: a name nothing will ever answer for is permanently in the state the cooldown
        // treats as "fresh press welcome". The button never rests, the press-rest is actively
        // cleared when the answer comes back unqueued, and every press starts the whole cycle
        // again. Nobody was holding the button down; the button simply never said no.
        //
        // Both halves are fixed here rather than one. The per-character rest stops this exact
        // shape, and the global budget stops the next one - whatever it turns out to be - from
        // reaching four hundred requests before anybody notices.

        /// <summary>How long a character rests after a lookup that produced nothing at all.</summary>
        private static readonly TimeSpan ClearsEmptyRest = TimeSpan.FromMinutes(10);

        /// <summary>Characters whose last press came back with nothing known and nothing queued,
        /// and when they may be asked about again.</summary>
        private readonly ConcurrentDictionary<string, DateTime> clearsEmptyUntil = new();

        /// <summary>
        /// The ceiling on presses, across every character, per window.
        ///
        /// Deliberately a whole-client budget rather than another per-character one. Per-character
        /// limits are what this feature already had, and they are exactly what a person searching
        /// one bad name after another walks straight past - every new name is a fresh allowance.
        /// Ten in five minutes is more than anybody inspecting a party will ever use and far below
        /// anything the server would notice.
        /// </summary>
        private static readonly TimeSpan ClearsBudgetWindow = TimeSpan.FromMinutes(5);

        private const int ClearsBudgetMax = 10;

        private readonly ConcurrentQueue<DateTime> clearsPressLog = new();

        /// <summary>
        /// How long a queued character is polled before the client stops believing the queue.
        ///
        /// The poll exists to notice an answer landing, and an answer that has not landed in three
        /// minutes is not going to. Without this the client keeps reading every six seconds for as
        /// long as the card is open, which is the quiet half of what happened - not a press, just a
        /// window somebody left on screen.
        /// </summary>
        private static readonly TimeSpan ClearsPollGiveUp = TimeSpan.FromMinutes(3);

        private readonly ConcurrentDictionary<string, DateTime> clearsPollingSince = new();

        /// <summary>Drops presses that have aged out of the window, and reports what is left.</summary>
        private int ClearsBudgetUsed()
        {
            DateTime cutoff = DateTime.UtcNow - ClearsBudgetWindow;
            while (clearsPressLog.TryPeek(out var when) && when < cutoff)
                clearsPressLog.TryDequeue(out _);

            return clearsPressLog.Count;
        }

        /// <summary>How long until another press is allowed, or zero when one is allowed now. Drives
        /// the button's own disabled state, so the budget is visible before it is hit rather than
        /// as a refusal afterwards.</summary>
        public TimeSpan ClearsBudgetWait()
        {
            if (ClearsBudgetUsed() < ClearsBudgetMax)
                return TimeSpan.Zero;

            if (!clearsPressLog.TryPeek(out var oldest))
                return TimeSpan.Zero;

            var wait = oldest + ClearsBudgetWindow - DateTime.UtcNow;
            return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
        }

        /// <summary>This character's clears as last known, or null if never read.</summary>
        public ClearsResponse? ClearsFor(CharacterIdentity who)
            => who.IsValid && clears.TryGetValue(who.Key, out var found) ? found : null;

        /// <summary>True while this character is on the server's clears belt, or a read is out.</summary>
        public bool ClearsPending(CharacterIdentity who)
        {
            if (!who.IsValid)
                return false;

            if (ClearsFor(who)?.Queued == true)
                return true;

            // A press waiting to be retried is still a press in progress as far as anyone looking
            // at the button is concerned.
            if (clearsRetry.ContainsKey(who.Key))
                return true;

            return clearsPressedAt.TryGetValue(who.Key, out var at)
                && DateTime.UtcNow - at < ClearsPressRest;
        }

        /// <summary>
        /// How long until the server would actually re-read this character, or zero if now.
        ///
        /// The server refuses work inside its hour: a press on a character read ten minutes ago is
        /// accepted, matched against the stored row and quietly not queued. That is correct, and
        /// also indistinguishable from a broken button unless the button knows about the window
        /// too - which is what this is for.
        /// </summary>
        public TimeSpan ClearsRefreshWait(CharacterIdentity who)
        {
            // A character nothing answered for. Checked FIRST, because this is precisely the case
            // the test below waves through: no fetch row means no cooldown, which is the right
            // reading for "nobody has looked yet" and the wrong one for "we looked and there is
            // nothing there". They are indistinguishable in the response, so the client has to
            // remember which of the two it just did.
            if (who.IsValid && clearsEmptyUntil.TryGetValue(who.Key, out var until))
            {
                var rest = until - DateTime.UtcNow;
                if (rest > TimeSpan.Zero)
                    return rest;

                clearsEmptyUntil.TryRemove(who.Key, out _);
            }

            var found = ClearsFor(who);
            if (found == null || !found.Fetched || found.RefreshAfterSec <= 0)
                return TimeSpan.Zero;

            var left = TimeSpan.FromSeconds(found.RefreshAfterSec) - found.Age;
            return left > TimeSpan.Zero ? left : TimeSpan.Zero;
        }

        /// <summary>
        /// Reads what the server already knows, and keeps reading while it owes an answer.
        ///
        /// Safe to call every frame: it reads once per character, and again only while that
        /// character is sitting on the belt. Costs nothing beyond a database read on our own
        /// server, so a card always opens showing the last known clears rather than a blank panel
        /// waiting for someone to press something.
        /// </summary>
        public void EnsureClearsLoaded(CharacterIdentity who, string? region)
        {
            if (!who.IsValid)
                return;

            // A press that failed for a passing reason, coming round again. Done here rather than
            // on a timer because this is already called every frame a card is open, and a retry
            // that only fires while somebody is still looking at the card is the right scope.
            if (clearsRetry.TryGetValue(who.Key, out var retry) && DateTime.UtcNow >= retry.When)
            {
                clearsRetry.TryRemove(who.Key, out _);
                Fetch(who, region, refresh: true, attempt: retry.Attempts);
                return;
            }

            bool known = clears.TryGetValue(who.Key, out var found);

            // Nothing to poll for once the answer has landed. The one thing that keeps a card
            // asking is the server saying the character is still on its belt.
            bool waiting = known && found!.Queued;

            if (known && !waiting)
                return;

            // The belt is allowed to be slow; it is not allowed to be believed forever. A card left
            // open on a character the queue will never answer for polled every six seconds for as
            // long as the window stayed up - no press involved, no ceiling, and nothing on screen
            // to suggest anything was happening.
            if (waiting)
            {
                DateTime since = clearsPollingSince.GetOrAdd(who.Key, DateTime.UtcNow);

                if (DateTime.UtcNow - since > ClearsPollGiveUp)
                {
                    // Believe the belt no longer. Marked as rested rather than merely stopped, so
                    // the button does not sit there inviting the press that starts it all again.
                    found!.Queued = false;
                    clearsEmptyUntil[who.Key] = DateTime.UtcNow + ClearsEmptyRest;
                    clearsPollingSince.TryRemove(who.Key, out _);
                    clearsPressedAt.TryRemove(who.Key, out _);
                    return;
                }
            }

            if (clearsReadAt.TryGetValue(who.Key, out var last)
                && DateTime.UtcNow - last < ClearsPollAfter)
                return;

            Fetch(who, region, refresh: false);
        }

        /// <summary>
        /// Asks the server to look this character up.
        ///
        /// The one gesture in this feature that can eventually cause a character name to reach
        /// FFLogs and Tomestone, which is why it is a button and never a timer.
        /// </summary>
        public void RequestClears(CharacterIdentity who, string? region)
        {
            if (!who.IsValid || ClearsPending(who) || ClearsRefreshWait(who) > TimeSpan.Zero)
                return;

            // The budget is spent on the press, not on the answer. A lookup that fails still cost
            // the server the work of trying, and a budget that only counted successes would be no
            // budget at all in exactly the situation it exists for.
            if (ClearsBudgetWait() > TimeSpan.Zero)
            {
                clearsNote = "That's a lot of lookups - give it a few minutes.";
                clearsNoteUntil = DateTime.UtcNow.AddSeconds(12);
                return;
            }

            clearsPressLog.Enqueue(DateTime.UtcNow);
            clearsPressedAt[who.Key] = DateTime.UtcNow;
            Fetch(who, region, refresh: true, attempt: 0);
        }

        private void Fetch(CharacterIdentity who, string? region, bool refresh, int attempt = 0)
        {
            // One request of each kind per character at a time. Without this a card drawn at 60fps
            // would issue sixty reads before the first came back - and with a shared key, a press
            // arriving during one of those reads was silently thrown away.
            string flight = refresh ? who.Key + "#press" : who.Key + "#read";
            if (!clearsInFlight.TryAdd(flight, 0))
                return;

            clearsReadAt[who.Key] = DateTime.UtcNow;

            var request = new ClearsRequest
            {
                Name = who.Name,
                World = who.World,
                // Empty rather than absent when the world can't be placed: the server treats a
                // missing region as "FFLogs is not reachable for this character" and still answers
                // from Tomestone, which is a thinner card rather than no card.
                Region = region ?? string.Empty,
                Refresh = refresh,
            };

            _ = Task.Run(async () =>
            {
                try
                {
                    var result = await api.GetClearsAsync(request).ConfigureAwait(false);

                    if (!result.IsOk || result.Value == null)
                    {
                        // Only a press is worth complaining about. A background read failing is
                        // invisible to the user and self-correcting on the next one; putting a
                        // sentence on the card for it would make an offline moment look like the
                        // player had no clears.
                        if (refresh)
                            PressFailed(who, result.Status, attempt, result.Message);

                        return;
                    }

                    var value = result.Value;
                    value.AppliedAt = DateTime.UtcNow;
                    clears[who.Key] = value;

                    if (refresh)
                    {
                        clearsNote = null;
                        clearsRetry.TryRemove(who.Key, out _);

                        if (!value.Queued)
                        {
                            // TWO VERY DIFFERENT ANSWERS, AND THEY USED TO BE THE SAME ONE.
                            //
                            // Not queued AND fetched is the server declining work because somebody
                            // read this character twenty minutes ago. The press is genuinely
                            // finished, and holding the button for the full rest would be
                            // pretending to work - so it is released, as before.
                            //
                            // Not queued and NOT fetched is the other thing entirely: nothing is
                            // known, nothing is coming, and the queue has already given up or never
                            // took it. Releasing the button there is what made a nonexistent
                            // character infinitely re-pressable - each press cleared its own rest
                            // on the way out.
                            if (value.Fetched)
                                clearsPressedAt.TryRemove(who.Key, out _);
                            else
                                clearsEmptyUntil[who.Key] = DateTime.UtcNow + ClearsEmptyRest;
                        }
                    }

                    // The queue answered one way or the other, so the poll's clock starts over.
                    if (!value.Queued)
                        clearsPollingSince.TryRemove(who.Key, out _);
                }
                catch (Exception ex)
                {
                    log.Debug($"[Ratings] Clears fetch failed: {ex.Message}");
                    if (refresh)
                        PressFailed(who, ApiStatus.Offline, attempt);
                }
                finally
                {
                    clearsInFlight.TryRemove(flight, out _);
                }
            });
        }

        /// <summary>
        /// A press that didn't come back cleanly: try again shortly, and only say so if it keeps
        /// happening.
        ///
        /// The first version announced every failure immediately, which was actively misleading.
        /// A press can time out on this side while the server accepts it, builds its fight list,
        /// queues the character and fetches them perfectly - so the card said "couldn't fetch" and
        /// then filled itself in seconds later. Retrying quietly matches what is actually going on,
        /// and the background poll picks up the answer either way.
        /// </summary>
        private void PressFailed(CharacterIdentity who, ApiStatus status, int attempt,
            string serverMessage = "")
        {
            // Nothing to retry: the server told us plainly that we may not. Saying so is the point.
            bool permanent = status is ApiStatus.BadRequest or ApiStatus.RateLimited;

            if (!permanent && attempt + 1 < ClearsMaxAttempts)
            {
                clearsRetry[who.Key] = (DateTime.UtcNow + ClearsRetryAfter, attempt + 1);
                return;
            }

            // The server's wording when there is any. The three below are the fallback for the one
            // case it cannot cover - a server that never answered.
            clearsNote = !string.IsNullOrWhiteSpace(serverMessage)
                ? serverMessage
                : status switch
                {
                    ApiStatus.Offline => "Couldn't reach the server.",
                    ApiStatus.RateLimited => "Too many lookups for now - try again later.",
                    _ => "Couldn't fetch clears right now.",
                };
            clearsNoteUntil = DateTime.UtcNow.AddSeconds(12);

            clearsRetry.TryRemove(who.Key, out _);
            clearsPressedAt.TryRemove(who.Key, out _);
        }
    }
}
#endif
