#if PFP_RATINGS
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Newtonsoft.Json;

namespace PfPresets
{
    /// <summary>One vote waiting to be sent.</summary>
    internal sealed class QueuedVote
    {
        /// <summary>Minted when the player clicks, and reused on every attempt.
        ///
        /// This is what makes resending safe. The server remembers ids it has processed, so a vote
        /// whose reply was lost is recognised on the retry instead of being counted twice - which
        /// would turn a dropped response into an inflated score.</summary>
        public string VoteId { get; set; } = Guid.NewGuid().ToString();

        public CharacterIdentity Target { get; set; } = new();
        public int Score { get; set; }
        public int Tags { get; set; }
        public int DutyRowId { get; set; }
        public SocialLink SocialLink { get; set; }
        public DateTime MetAtUtc { get; set; }
        public DateTime QueuedUtc { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// The sealed proof of the duty this vote came out of, built when the player clicked.
        ///
        /// KEPT WITH THE VOTE, because it cannot be rebuilt later. It describes a duty the encounter
        /// store forgets after an hour, and a queued vote routinely waits longer than that - so
        /// building it at send time meant queued votes went out with nothing attached and were held.
        /// Sealed at the click, it stays true however long the queue takes to drain.
        ///
        /// Empty is a legitimate value: a build without the sealing component, or a vote whose duty
        /// really was gone. It is still sent. The server decides, not this.
        /// </summary>
        public string Evidence { get; set; } = string.Empty;

        /// <summary>Failed sends, so one permanently bad vote can't block the queue forever.</summary>
        public int Attempts { get; set; }

        /// <summary>
        /// Not worth sending again before this. Set from the server's own Retry-After.
        ///
        /// Without it the flush loop re-sent the head of the queue every twenty seconds forever,
        /// which for a vote refused by a daily limit is about four thousand pointless requests a
        /// day - per stuck vote, per install. On 2026-08-10 that was 9,474 of the API's 9,682
        /// refusals: the plugin was comfortably the largest source of load on its own server.
        /// </summary>
        public DateTime? NotBeforeUtc { get; set; }
    }

    /// <summary>
    /// Votes that haven't reached the server yet.
    ///
    /// The server paces a voter at 24 an hour, which a busy alliance evening can legitimately
    /// exceed. Rather than showing an error for a vote the player meant to cast, the plugin takes
    /// it, records it locally, and sends it when the window frees. From the player's side the
    /// click always works; the pacing is invisible.
    ///
    /// Written to disk on every change, because the whole point is surviving the thing that makes
    /// an in-memory queue useless: a crash, a client restart, or a logout between casting a vote
    /// and it being accepted.
    /// </summary>
    internal sealed class VoteQueue
    {
        /// <summary>Matches the server's own allowance so the client stops at the same line rather
        /// than discovering it by being refused.</summary>
        public const int VotesPerHour = 24;

        /// <summary>Given up on after this many failures. A vote the server keeps rejecting is
        /// malformed or about a character that no longer resolves; retrying it forever would
        /// block everything queued behind it.</summary>
        private const int MaxAttempts = 6;

        private readonly string path;
        private readonly IPluginLog log;
        private readonly object gate = new();

        private List<QueuedVote> pending = new();

        /// <summary>When each recent vote was accepted, for the client's half of the pacing.</summary>
        private List<DateTime> sentAt = new();

        public VoteQueue(IDalamudPluginInterface pluginInterface, IPluginLog log)
        {
            this.log = log;
            path = Path.Combine(pluginInterface.GetPluginConfigDirectory(), "vote-queue.json");
            Load();
        }

        public int Count
        {
            get { lock (gate) { return pending.Count; } }
        }

        /// <summary>Votes accepted in the last hour, which is what the allowance is measured
        /// against.</summary>
        public int SentThisHour()
        {
            var cutoff = DateTime.UtcNow.AddHours(-1);
            lock (gate)
            {
                sentAt.RemoveAll(t => t < cutoff);
                return sentAt.Count;
            }
        }

        public bool CanSendNow() => SentThisHour() < VotesPerHour;

        /// <summary>
        /// Stores the seal a vote was cast with, and writes the queue to disk.
        ///
        /// Persisting matters as much as storing: a vote queued at midnight and sent after a restart
        /// must still carry the duty it came from, and a seal held only in memory would leave it
        /// with nothing to show - the exact failure this whole change exists to end.
        /// </summary>
        public void AttachEvidence(QueuedVote vote, string evidence)
        {
            lock (gate)
            {
                vote.Evidence = evidence ?? string.Empty;
                Save();
            }
        }

        /// <summary>Takes a vote. Always succeeds - that is the point.</summary>
        public QueuedVote Enqueue(CharacterIdentity target, VoteDirection direction, int tags,
            int dutyRowId, SocialLink social, DateTime metAtUtc)
        {
            var vote = new QueuedVote
            {
                Target = target,
                Score = direction == VoteDirection.Up ? 1 : -1,
                Tags = tags,
                DutyRowId = dutyRowId,
                SocialLink = social,
                MetAtUtc = metAtUtc,
            };

            lock (gate)
            {
                pending.Add(vote);
                Save();
            }

            return vote;
        }

        /// <summary>
        /// The next vote worth trying, or null when the queue is empty, the hour is spent, or
        /// everything in it is waiting out a refusal.
        ///
        /// Skips past anything holding a Retry-After rather than stopping at it: one vote the
        /// server will not take until tomorrow must not block the ones behind it, which is what
        /// returning `pending[0]` unconditionally did.
        /// </summary>
        public QueuedVote? Next()
        {
            if (!CanSendNow())
                return null;

            var now = DateTime.UtcNow;

            lock (gate)
            {
                // NOTHING IS DROPPED HERE ANY MORE, and this was the third place the client threw
                // away votes it had no business judging.
                //
                // It deleted anything queued for more than an hour, reasoning that the server would
                // refuse it as stale anyway. That reasoning was sound and the arithmetic was fatal:
                // the send allowance is twenty-four an hour, so a busy night queues votes for
                // exactly the sort of wait that tripped this - and they were deleted here, quietly,
                // in a Debug line, without ever being sent. A vote nobody refused and nobody counted
                // existed in no place at all.
                //
                // They now carry the seal they were cast with (see QueuedVote.Evidence) and the
                // server dates the duty from when the vote was cast, so a wait is no longer what
                // makes a vote stale. Anything genuinely too old is held for a person to look at,
                // which is where that decision belongs.
                foreach (var vote in pending)
                {
                    if (vote.NotBeforeUtc == null || vote.NotBeforeUtc <= now)
                        return vote;
                }

                return null;
            }
        }

        /// <summary>Holds a vote back until the server says it is worth trying again.</summary>
        public void HoldUntil(QueuedVote vote, DateTime whenUtc)
        {
            lock (gate)
            {
                var found = pending.Find(v => v.VoteId == vote.VoteId);
                if (found != null)
                    found.NotBeforeUtc = whenUtc;
            }

            Save();
        }

        /// <summary>Drops a vote the server accepted, and counts it against the hour.</summary>
        public void Accepted(QueuedVote vote)
        {
            lock (gate)
            {
                pending.RemoveAll(v => v.VoteId == vote.VoteId);
                sentAt.Add(DateTime.UtcNow);
                Save();
            }
        }

        /// <summary>
        /// Records a failed attempt.
        ///
        /// <paramref name="permanent"/> for a refusal that resending cannot fix - a malformed
        /// vote, or a target the server won't accept. Those are dropped immediately rather than
        /// retried, because the queue is ordered and a stuck head starves everything behind it.
        /// </summary>
        public void Failed(QueuedVote vote, bool permanent)
        {
            lock (gate)
            {
                var found = pending.FirstOrDefault(v => v.VoteId == vote.VoteId);
                if (found == null)
                    return;

                found.Attempts++;

                if (permanent || found.Attempts >= MaxAttempts)
                {
                    log.Debug($"[Ratings] Dropping vote after {found.Attempts} attempt(s).");
                    pending.Remove(found);
                }
                else
                {
                    // Moved to the back so one bad entry can't hold up the rest.
                    pending.Remove(found);
                    pending.Add(found);
                }

                Save();
            }
        }

        public void Clear()
        {
            lock (gate)
            {
                pending.Clear();
                sentAt.Clear();
                Save();
            }
        }

        // ── persistence ──────────────────────────────────────────

        private sealed class Persisted
        {
            public List<QueuedVote> Pending { get; set; } = new();
            public List<DateTime> SentAt { get; set; } = new();
        }

        private void Load()
        {
            try
            {
                if (!File.Exists(path))
                    return;

                var data = JsonConvert.DeserializeObject<Persisted>(File.ReadAllText(path));
                if (data == null)
                    return;

                var cutoff = DateTime.UtcNow.AddHours(-1);
                lock (gate)
                {
                    pending = data.Pending ?? new List<QueuedVote>();
                    sentAt = (data.SentAt ?? new List<DateTime>()).FindAll(t => t >= cutoff);
                }

                if (pending.Count > 0)
                    log.Information($"[Ratings] {pending.Count} queued vote(s) recovered.");
            }
            catch (Exception ex)
            {
                log.Warning($"[Ratings] Couldn't read the vote queue: {ex.Message}");
            }
        }

        /// <summary>Called with the lock held.</summary>
        private void Save()
        {
            try
            {
                File.WriteAllText(path, JsonConvert.SerializeObject(
                    new Persisted { Pending = pending, SentAt = sentAt }, Formatting.Indented));
            }
            catch (Exception ex)
            {
                log.Warning($"[Ratings] Couldn't write the vote queue: {ex.Message}");
            }
        }
    }
}
#endif
