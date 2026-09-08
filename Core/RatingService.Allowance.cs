#if PFP_RATINGS
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace PfPresets
{
    /// <summary>
    /// Who this character may vote on after a duty, decided by the server before anybody is offered.
    ///
    /// WHAT THIS REPLACES. The post-duty window used to open on everyone who was in the room and
    /// find out afterwards whether any of it counted. The rule it was judged against - that the
    /// OTHER person's client also filed this duty - could not be true yet at the moment the window
    /// appeared, because both clients leave the instance within a few seconds of each other. A vote
    /// cast into that gap was held server-side, and a held vote is indistinguishable from a counted
    /// one out here. The window was inviting an action that quietly did nothing.
    ///
    /// So the duty is filed, and the window waits for the answer. Only the people whose own clients
    /// filed the same duty naming this character back are offered a row - the one record this
    /// client cannot write for them, which is what makes it worth anything.
    ///
    /// WHAT HAPPENS TO EVERYBODY ELSE: nothing, for an hour, and then the whole thing is discarded.
    /// A member with no plugin has nobody to send a window to; a member whose client has not filed
    /// yet may still turn up. Neither is offered until they are allowed, and at the hour the vote
    /// window shuts anyway - the server stops accepting for that duty, so a row that survived past
    /// it would be a button that fails on press. Discarding is the honest end of the timer.
    ///
    /// NOTHING HERE IS ALLOWED TO TAKE THE WINDOW AWAY. A server that does not know these routes,
    /// one that is unreachable, one that answers with nonsense - every failure lands on
    /// <see cref="AllowanceState.Unavailable"/>, and Unavailable means the prompt behaves exactly
    /// as it did before any of this existed. That is deliberate and it is the backwards
    /// compatibility: this is a better path, not a gate in front of the old one.
    /// </summary>
    internal partial class RatingService
    {
        internal enum AllowanceState
        {
            /// <summary>Filed, and somebody in the party is still to be heard from.</summary>
            Waiting,

            /// <summary>Settled. <see cref="MayVoteOn"/> is now the whole truth about this duty.</summary>
            Ready,

            /// <summary>The server refused the duty as a shape it will not take votes out of -
            /// undersized, today. Nobody is offered and nothing is waited for.</summary>
            Skipped,

            /// <summary>Nothing was learned. The prompt falls back to its old behaviour.</summary>
            Unavailable,
        }

        internal sealed class DutyAllowanceRecord
        {
            public int DutyRowId;
            public long EndedMs;
            public CharacterIdentity Me = new();
            public List<CharacterIdentity> Party = new();

            public AllowanceState State = AllowanceState.Waiting;
            public string Reason = string.Empty;

            /// <summary>
            /// Whether the server considers the party finished answering.
            ///
            /// KEPT APART FROM <see cref="State"/>, because they stopped meaning the same thing.
            /// Settled is about whether there is anyone left to hear from; State is about whether
            /// there is anyone to vote on. A party with one plugin user and six without is never
            /// settled - the six are never going to file - but it has somebody to vote on within
            /// seconds. Reading settledness as readiness kept the window shut on exactly that
            /// party, which is the ordinary shape of a Party Finder group.
            /// </summary>
            public bool Settled;

            /// <summary>Identity keys the server has cleared. Everything else is refused.</summary>
            public readonly HashSet<string> Allowed = new(StringComparer.OrdinalIgnoreCase);

            /// <summary>What the server last said about each of them, for the line the window
            /// shows while it waits. Keys are identity keys.</summary>
            public readonly Dictionary<string, string> States =
                new(StringComparer.OrdinalIgnoreCase);

            public DateTime ExpiresUtc = DateTime.MinValue;
            public DateTime NextPollUtc = DateTime.MinValue;
            public int Polls;
        }

        /// <summary>
        /// How long to leave between polls, by how many have already gone out.
        ///
        /// WIDENING, BECAUSE WHAT IT IS WAITING FOR IS A PERSON. The first minute is where almost
        /// every answer lands: the rest of the party is walking out of the instance as this runs.
        /// After that the likely explanations stop being "any second now" and start being a client
        /// that is not going to file at all, so asking every twenty seconds for an hour would be
        /// fifty requests to learn what the first three already said.
        ///
        /// The last entry repeats until the hour is up.
        /// </summary>
        private static readonly TimeSpan[] AllowancePollBackoff =
        {
            TimeSpan.FromSeconds(15),
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(45),
            TimeSpan.FromSeconds(60),
            TimeSpan.FromSeconds(90),
            TimeSpan.FromMinutes(2),
            TimeSpan.FromMinutes(5),
            TimeSpan.FromMinutes(12),
            TimeSpan.FromMinutes(25),
        };

        /// <summary>The fallback when the server does not say - which is only ever a server too old
        /// to answer at all, since every real reply carries its own expiry.</summary>
        private static readonly TimeSpan AllowanceFallbackWindow = TimeSpan.FromHours(1);

        private readonly object allowanceGate = new();

        /// <summary>Keyed on the encounter id, which is what the prompt has in its hand.</summary>
        private readonly Dictionary<string, DutyAllowanceRecord> allowances = new();

        /// <summary>True while a report or a poll is in flight, so the tick starts one at a time.
        /// There is never any hurry here and a queue of overlapping polls helps nobody.</summary>
        private bool allowanceInFlight;

        // ══════════════════════════════════════════════════════════
        //  FILING
        // ══════════════════════════════════════════════════════════

        /// <summary>
        /// Files a finished duty and takes the first answer, which is usually "nobody yet".
        ///
        /// Subscribed to the tracker's completion event in its own right rather than hanging off the
        /// achievement post, because the two ask different questions. An achievement is about the
        /// feed and only some duties qualify; this is about the vote window, and every duty with
        /// somebody else in it does.
        /// </summary>
        public void FileDutyForAllowance(DutyEncounter encounter)
        {
            if (encounter == null || !config.CommunityEnabled)
                return;

            var me = api.LocalIdentity;
            if (me == null || !me.IsValid)
                return;

            var party = new List<CharacterIdentity>();
            foreach (var member in encounter.Members)
            {
                if (member.IsValid && !string.Equals(member.Identity.Key, me.Key, StringComparison.OrdinalIgnoreCase))
                    party.Add(member.Identity);
            }

            if (party.Count == 0)
                return;

            string evidence = string.Empty;
            BuildClearEvidence(encounter, ref evidence);

            // No sealed payload is not a reason to hide the window - it is a reason to fall back to
            // the behaviour that never needed one. A build without the evidence component reaches
            // exactly this line.
            if (string.IsNullOrEmpty(evidence))
            {
                Remember(encounter, me, party, AllowanceState.Unavailable, string.Empty);
                return;
            }

            var record = Remember(encounter, me, party, AllowanceState.Waiting, string.Empty);
            record.NextPollUtc = DateTime.UtcNow + AllowancePollBackoff[0];

            _ = Task.Run(async () =>
            {
                try
                {
                    var result = await api.ReportDutyAsync(
                        new DutyReportRequest { Evidence = evidence }).ConfigureAwait(false);

                    ApplyAnswer(encounter.Id, result.Status, result.Value);
                }
                catch (Exception ex)
                {
                    log.Debug($"[Ratings] Duty report failed: {ex.Message}");
                }
            });
        }

        private DutyAllowanceRecord Remember(
            DutyEncounter encounter, CharacterIdentity me, List<CharacterIdentity> party,
            AllowanceState state, string reason)
        {
            var record = new DutyAllowanceRecord
            {
                DutyRowId = (int)encounter.DutyRowId,
                EndedMs = new DateTimeOffset(
                    DateTime.SpecifyKind(encounter.CompletedUtc, DateTimeKind.Utc)).ToUnixTimeMilliseconds(),
                Me = me,
                Party = party,
                State = state,
                Reason = reason,
                ExpiresUtc = encounter.CompletedUtc + AllowanceFallbackWindow,
            };

            lock (allowanceGate)
            {
                allowances[encounter.Id] = record;
                PruneAllowances();
            }

            return record;
        }

        // ══════════════════════════════════════════════════════════
        //  POLLING
        // ══════════════════════════════════════════════════════════

        /// <summary>
        /// Asks again about one duty that is still waiting, if anything is due.
        ///
        /// Called every frame from the plugin's framework update, and written to be nearly free on
        /// the frames where there is nothing to do - which is almost all of them. Combat is not
        /// checked here, unlike the duty post: this is a few hundred bytes on a widening backoff,
        /// and the thing it is holding up is a window the player is waiting to see.
        /// </summary>
        public void TickAllowances()
        {
            if (allowanceInFlight)
                return;

            var now = DateTime.UtcNow;
            string? id = null;
            DutyAllowanceRecord? due = null;

            lock (allowanceGate)
            {
                foreach (var pair in allowances)
                {
                    var record = pair.Value;

                    // POLLED PAST READY, because Ready no longer means finished. The window opens
                    // on the first person the server clears; the rest of the party may still be
                    // walking out of the instance behind them, and they are added to a window that
                    // is already up. What ends the polling is the server saying everybody has
                    // answered, or the hour running out.
                    if (record.Settled) continue;
                    if (record.State is AllowanceState.Skipped or AllowanceState.Unavailable) continue;
                    if (now >= record.ExpiresUtc) continue;
                    if (now < record.NextPollUtc) continue;

                    id = pair.Key;
                    due = record;
                    break;
                }

                if (due == null)
                {
                    PruneAllowances();
                    return;
                }

                // Moved before the request goes out, not after it comes back. A poll that fails
                // silently would otherwise be retried on the very next frame, for the rest of the
                // hour.
                due.Polls += 1;
                due.NextPollUtc = now + AllowancePollBackoff[
                    Math.Min(due.Polls, AllowancePollBackoff.Length - 1)];
            }

            allowanceInFlight = true;

            var request = new DutyAllowanceRequest
            {
                Duty = due.DutyRowId,
                Ended = due.EndedMs,
                Me = new DutyAllowanceWho { Name = due.Me.Name, World = due.Me.World },
                Party = new List<DutyAllowanceWho>(due.Party.Count + 1)
                {
                    new() { Name = due.Me.Name, World = due.Me.World },
                },
            };

            foreach (var who in due.Party)
                request.Party.Add(new DutyAllowanceWho { Name = who.Name, World = who.World });

            string encounterId = id!;

            _ = Task.Run(async () =>
            {
                try
                {
                    var result = await api.GetDutyAllowanceAsync(request).ConfigureAwait(false);
                    ApplyAnswer(encounterId, result.Status, result.Value);
                }
                catch (Exception ex)
                {
                    log.Debug($"[Ratings] Allowance poll failed: {ex.Message}");
                }
                finally
                {
                    allowanceInFlight = false;
                }
            });
        }

        /// <summary>
        /// Takes one answer, from the report or from a poll - they have the same shape on purpose.
        ///
        /// WHICH FAILURES ARE FINAL, AND WHICH ARE NOT. A refusal the server is entitled to repeat -
        /// a route it does not have, a payload it will not take - is final, and lands on Unavailable
        /// so the prompt stops waiting and behaves as it always did. Everything transient - offline,
        /// no session yet, rate limited, a 500 - leaves the record waiting, because the next poll
        /// may well get an answer and giving up on a dropped packet would cost the whole feature
        /// on a bad connection.
        /// </summary>
        private void ApplyAnswer(string encounterId, ApiStatus status, DutyAllowanceResponse? answer)
        {
            lock (allowanceGate)
            {
                if (!allowances.TryGetValue(encounterId, out var record))
                    return;

                if (status != ApiStatus.Ok || answer == null)
                {
                    // BadRequest is where a 404 arrives, which is what a server too old to know
                    // these routes answers with. Refused is a 403 about this request and will not
                    // become a yes on a retry either.
                    if (status is ApiStatus.BadRequest or ApiStatus.Refused)
                    {
                        record.State = AllowanceState.Unavailable;
                        log.Debug($"[Ratings] Allowance unavailable ({status}); prompt falls back.");
                    }

                    return;
                }

                if (answer.ExpiresMs > 0)
                {
                    record.ExpiresUtc = DateTimeOffset
                        .FromUnixTimeMilliseconds(answer.ExpiresMs).UtcDateTime;
                }

                record.Allowed.Clear();
                record.States.Clear();

                foreach (var member in answer.Members)
                {
                    if (string.IsNullOrWhiteSpace(member.Name) || string.IsNullOrWhiteSpace(member.World))
                        continue;

                    string key = new CharacterIdentity(member.Name, member.World).Key;
                    record.States[key] = member.State ?? string.Empty;

                    if (string.Equals(member.State, "allowed", StringComparison.OrdinalIgnoreCase))
                        record.Allowed.Add(key);
                }

                record.Reason = answer.Reason ?? string.Empty;

                // Named for what it is rather than `status`, which is already the transport's
                // ApiStatus parameter on this method - two different questions, one word.
                string verdict = (answer.Status ?? string.Empty).ToLowerInvariant();

                // "unfiled" is not a verdict. It means this client's own report has not landed yet
                // - the poll beat the report, or the report was lost - so it is the same "ask
                // again" that pending is.
                record.Settled = verdict is "ready" or "skipped";

                // ONE ALLOWED PERSON IS ENOUGH TO OPEN THE WINDOW. Waiting for the whole party to
                // settle means waiting for people who will never answer, and a party where most of
                // them do not run the plugin is the normal case rather than the exception. The
                // poll carries on underneath - see TickAllowances - so anybody who files later is
                // added to a window that is already up rather than gating it.
                record.State = verdict == "skipped"
                    ? AllowanceState.Skipped
                    : record.Allowed.Count > 0
                        ? AllowanceState.Ready
                        : AllowanceState.Waiting;

                log.Debug(
                    $"[Ratings] Allowance {record.State} for duty {record.DutyRowId}: "
                    + $"{record.Allowed.Count}/{record.Party.Count} votable.");
            }
        }

        // ══════════════════════════════════════════════════════════
        //  WHAT THE PROMPT ASKS
        // ══════════════════════════════════════════════════════════

        /// <summary>Where this duty's allowance has got to, or Unavailable when there is no record
        /// of it at all - a duty that finished before this build, or one filed while opted out.</summary>
        public AllowanceState AllowanceStateFor(string encounterId)
        {
            lock (allowanceGate)
            {
                if (!allowances.TryGetValue(encounterId, out var record))
                    return AllowanceState.Unavailable;

                // EXPIRY IS READ, NOT SWEPT. The record is left in place for the prompt to see one
                // last time and close itself on; PruneAllowances clears it out afterwards. A record
                // that vanished the instant it expired would leave the window with no idea why its
                // rows had gone.
                //
                // AN OPEN WINDOW EXPIRES TOO, and this used to check only the waiting case. Past
                // the hour the server stops accepting votes for that duty, so a window still
                // standing there is a row of buttons that fail on press - which is the exact
                // failure this whole feature exists to remove. Unavailable is left alone: it never
                // consulted an allowance, so it has no window to run out.
                if (DateTime.UtcNow >= record.ExpiresUtc && record.State != AllowanceState.Unavailable)
                    return AllowanceState.Skipped;

                return record.State;
            }
        }

        /// <summary>Why an allowance was skipped, in the server's own word. Empty when it was not,
        /// or when it simply ran out of time.</summary>
        public string AllowanceReasonFor(string encounterId)
        {
            lock (allowanceGate)
                return allowances.TryGetValue(encounterId, out var record) ? record.Reason : string.Empty;
        }

        /// <summary>
        /// Whether this character may be voted on out of this duty.
        ///
        /// Answers true when there is no allowance to consult, and that is not an oversight: no
        /// record means nothing was learned, and the prompt falls back to offering everybody
        /// exactly as it did before. The server still holds what it cannot corroborate, which is
        /// the behaviour every client older than this one already has.
        /// </summary>
        public bool MayVoteOn(string encounterId, CharacterIdentity who)
        {
            if (who == null || !who.IsValid)
                return false;

            lock (allowanceGate)
            {
                if (!allowances.TryGetValue(encounterId, out var record))
                    return true;

                if (record.State == AllowanceState.Unavailable)
                    return true;

                if (record.State == AllowanceState.Skipped)
                    return false;

                return record.Allowed.Contains(who.Key);
            }
        }

        /// <summary>Drops records past their hour. Called under the lock.</summary>
        private void PruneAllowances()
        {
            if (allowances.Count == 0)
                return;

            var cutoff = DateTime.UtcNow - TimeSpan.FromMinutes(5);
            List<string>? dead = null;

            foreach (var pair in allowances)
            {
                if (pair.Value.ExpiresUtc < cutoff)
                    (dead ??= new List<string>()).Add(pair.Key);
            }

            if (dead == null)
                return;

            foreach (var key in dead)
                allowances.Remove(key);
        }
    }
}
#endif
