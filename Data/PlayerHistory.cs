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
    /// <summary>One character this install has met, and what is known about the meeting.</summary>
    public sealed class PlayerSeen
    {
        public string Name { get; set; } = string.Empty;
        public string World { get; set; } = string.Empty;

        /// <summary>The job they were last seen on. Display only - people switch.</summary>
        public uint JobId { get; set; }

        /// <summary>When <see cref="JobId"/> was last established, by either route.</summary>
        public DateTime JobUpdatedUtc { get; set; }

        /// <summary>
        /// Whether the job was read off the game rather than fetched from Tomestone.
        ///
        /// The game is always right and always current: it is what they are on now, in front of
        /// you. Tomestone reports whatever they last played publicly, which may be months old and
        /// is never better than a live sighting. So a game reading overwrites a fetched one
        /// unconditionally, and a fetch never overwrites a game reading that is still fresh.
        /// </summary>
        public bool JobFromGame { get; set; }

        public DateTime FirstSeenUtc { get; set; }
        public DateTime LastSeenUtc { get; set; }

        /// <summary>Duties shared with them. Counted per duty, not per sighting, so sitting in one
        /// party for three hours is one.</summary>
        public int TimesMet { get; set; }

        /// <summary>The duty they were last met in, for the "where do I know them from" question
        /// that is the whole reason anyone opens this list.</summary>
        public string LastDutyName { get; set; } = string.Empty;

        /// <summary>
        /// The last rating seen for this player, kept so a profile can show something the instant
        /// it opens instead of a spinner. Always refreshed behind whatever it displays - this is
        /// what was true last time, never presented as what is true now.
        /// </summary>
        public PlayerRating? Rating { get; set; }

        public DateTime RatingUpdatedUtc { get; set; }

        /// <summary>
        /// Whether this row records an actual meeting, or exists only to cache a job.
        ///
        /// Seeing somebody's job in a Party Finder listing is not meeting them, and a row created
        /// that way must not turn up in Recent players claiming otherwise. Those rows are reachable
        /// by lookup and nothing else.
        /// </summary>
        [JsonIgnore]
        public bool Met => TimesMet > 0;

        [JsonIgnore]
        public CharacterIdentity Identity => new(Name, World);

        [JsonIgnore]
        public string Key => $"{Name.Trim().ToLowerInvariant()}@{World.Trim().ToLowerInvariant()}";

        [JsonIgnore]
        public bool IsValid => !string.IsNullOrWhiteSpace(Name) && !string.IsNullOrWhiteSpace(World);
    }

    /// <summary>
    /// Everyone this install has ever met, kept indefinitely.
    ///
    /// Deliberately a different thing from <see cref="EncounterStore"/>, which forgets people after
    /// a week and holds only two dozen of them. That store answers "who did I just play with", and
    /// it is small on purpose because it drives the rating prompt. This one answers "have I run
    /// into this person before", which is only worth asking if the answer reaches back further than
    /// a week - so it keeps one row per character, forever, and no per-duty detail.
    ///
    /// One row per character rather than one per meeting is what keeps that affordable. A player
    /// met two hundred times costs the same as one met once, and the file grows with people you
    /// have met rather than with hours played.
    ///
    /// Same reasoning as the other stores for living in its own file: Dalamud rewrites the whole
    /// config blob on every save, and this is data about other people, so it should be one file you
    /// can point at and delete.
    /// </summary>
    public sealed class PlayerHistory
    {
        private const string FileName = "players.json";

        /// <summary>
        /// A ceiling, not a retention policy.
        ///
        /// The list is meant to be permanent and nothing here expects to reach this. It exists so a
        /// bug that recorded a name per frame writes a large file rather than an unbounded one, and
        /// it is high enough that a decade of raiding will not touch it. When it does bite, the
        /// least recently seen go first.
        /// </summary>
        public const int HardCap = 20_000;

        /// <summary>How many the main list shows. The rest stay searchable.</summary>
        public const int RecentShown = 100;

        /// <summary>
        /// How long a job read from Tomestone is trusted before it is worth asking again.
        ///
        /// A week, because a job is not a fact that changes usefully faster than that for the
        /// purpose it serves here - putting a recognisable icon next to a name. Re-fetching a
        /// stranger's job every time you glance at them spends somebody else's rate limit to
        /// redraw an icon that was already right.
        /// </summary>
        public static readonly TimeSpan JobTtl = TimeSpan.FromDays(7);

        private static readonly JsonSerializerSettings Settings = new()
        {
            TypeNameHandling = TypeNameHandling.None,
            MetadataPropertyHandling = MetadataPropertyHandling.Ignore,
            MaxDepth = 16,
            Formatting = Formatting.Indented,
        };

        private readonly string path;
        private readonly IPluginLog log;

        /// <summary>Keyed by <see cref="PlayerSeen.Key"/>, because every read is a lookup by
        /// character and a list would make the write path O(n) on every party member.</summary>
        private readonly Dictionary<string, PlayerSeen> players =
            new(StringComparer.OrdinalIgnoreCase);

        private readonly object gate = new();
        private bool dirty;

        public PlayerHistory(IDalamudPluginInterface pluginInterface, IPluginLog log)
        {
            this.log = log;

            var dir = pluginInterface.GetPluginConfigDirectory();
            Directory.CreateDirectory(dir);
            path = Path.Combine(dir, FileName);

            Load();
        }

        public string FilePath => path;

        public int Count
        {
            get { lock (gate) { return players.Count; } }
        }

        /// <summary>How many were actually met, which is what the lists count. Job-cache rows are
        /// not people you have played with and must not inflate "showing 100 of N".</summary>
        public int MetCount
        {
            get { lock (gate) { return players.Values.Count(p => p.IsValid && p.Met); } }
        }

        /// <summary>
        /// Writes only if it has been a while, for the callers that fire every frame.
        ///
        /// Observing jobs happens once per party member per draw. Those are almost all no-ops, but
        /// the ones that aren't would otherwise each rewrite the whole file - so the write is
        /// allowed to lag, and the real guarantee comes from the flush on unload.
        /// </summary>
        private DateTime lastWriteUtc = DateTime.MinValue;
        private static readonly TimeSpan WriteInterval = TimeSpan.FromSeconds(20);

        public void FlushIfDue()
        {
            lock (gate)
            {
                if (!dirty || DateTime.UtcNow - lastWriteUtc < WriteInterval)
                    return;
            }

            Flush();
        }

        // ══════════════════════════════════════════════════════════
        //  READS
        // ══════════════════════════════════════════════════════════

        /// <summary>The most recently met, newest first. What the main list shows.</summary>
        public List<PlayerSeen> Recent(int max = RecentShown)
        {
            lock (gate)
            {
                return players.Values
                    .Where(p => p.IsValid && p.Met)
                    .OrderByDescending(p => p.LastSeenUtc)
                    .Take(Math.Max(0, max))
                    .ToList();
            }
        }

        /// <summary>
        /// Everyone whose name contains <paramref name="term"/>, newest first.
        ///
        /// Substring rather than prefix, and on the name rather than the world, matching the search
        /// box's existing behaviour: the case this is for is remembering a surname and not a first
        /// name. Searching the whole history rather than the visible hundred is the point of the
        /// store - someone you met once eight months ago is exactly who you cannot remember.
        /// </summary>
        public List<PlayerSeen> Search(string term, int max = 50)
        {
            string needle = (term ?? string.Empty).Trim();
            if (needle.Length == 0)
                return new List<PlayerSeen>();

            lock (gate)
            {
                return players.Values
                    .Where(p => p.IsValid && p.Met
                                && p.Name.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)
                    .OrderByDescending(p => p.LastSeenUtc)
                    .Take(Math.Max(0, max))
                    .ToList();
            }
        }

        /// <summary>Everyone, newest first. For the search box's own suggestion pass.</summary>
        public List<PlayerSeen> All()
        {
            lock (gate)
            {
                return players.Values
                    .Where(p => p.IsValid && p.Met)
                    .OrderByDescending(p => p.LastSeenUtc)
                    .ToList();
            }
        }

        /// <summary>What is known about one character, or null.</summary>
        public PlayerSeen? Find(CharacterIdentity who)
        {
            if (!who.IsValid)
                return null;

            lock (gate)
                return players.TryGetValue(who.Key, out var found) ? found : null;
        }

        /// <summary>The cached job for a character, or 0 if none is known.</summary>
        public uint JobFor(CharacterIdentity who)
        {
            if (!who.IsValid)
                return 0;

            lock (gate)
                return players.TryGetValue(who.Key, out var found) ? found.JobId : 0u;
        }

        /// <summary>
        /// Whether this character's job is worth asking Tomestone about: either nothing is known,
        /// or what is known is older than <see cref="JobTtl"/>.
        ///
        /// A job seen in game never expires into a lookup while it is inside the window, which is
        /// the whole point - the party list is a better source than the network and it costs
        /// nothing.
        /// </summary>
        public bool NeedsJobLookup(CharacterIdentity who)
        {
            if (!who.IsValid)
                return false;

            lock (gate)
            {
                if (!players.TryGetValue(who.Key, out var found) || found.JobId == 0)
                    return true;

                return DateTime.UtcNow - found.JobUpdatedUtc > JobTtl;
            }
        }

        /// <summary>
        /// Records a job.
        ///
        /// <paramref name="fromGame"/> marks a live sighting - the party list, a recruitment card,
        /// anywhere the client itself can see them. Those always win: a fetched job can never
        /// overwrite one read off the game inside the TTL, because the network copy is at best a
        /// staler version of the same fact.
        ///
        /// Creates a row when there isn't one, without marking it as a meeting - see
        /// <see cref="PlayerSeen.Met"/>.
        /// </summary>
        public void RememberJob(CharacterIdentity who, uint jobId, bool fromGame)
        {
            if (!who.IsValid || jobId == 0)
                return;

            lock (gate)
            {
                if (players.TryGetValue(who.Key, out var existing))
                {
                    if (!fromGame && existing.JobFromGame
                        && DateTime.UtcNow - existing.JobUpdatedUtc <= JobTtl)
                        return;

                    // Nothing changed, so nothing needs writing. Worth checking: the party panel
                    // calls this every frame for every member.
                    if (existing.JobId == jobId && existing.JobFromGame == fromGame)
                        return;

                    existing.JobId = jobId;
                    existing.JobFromGame = fromGame;
                    existing.JobUpdatedUtc = DateTime.UtcNow;
                    dirty = true;
                    return;
                }

                players[who.Key] = new PlayerSeen
                {
                    Name = who.Name,
                    World = who.World,
                    JobId = jobId,
                    JobFromGame = fromGame,
                    JobUpdatedUtc = DateTime.UtcNow,
                    TimesMet = 0,
                };

                Trim();
                dirty = true;
            }
        }

        /// <summary>The last rating seen for this character, however old. Null when there is none.</summary>
        public PlayerRating? RatingFor(CharacterIdentity who)
        {
            if (!who.IsValid)
                return null;

            lock (gate)
                return players.TryGetValue(who.Key, out var found) ? found.Rating : null;
        }

        /// <summary>
        /// Stores the rating just fetched, so the next view of this player has something to show
        /// before the network answers. Creates a row if the character is only known from a search.
        /// </summary>
        public void RememberRating(CharacterIdentity who, PlayerRating? rating)
        {
            if (!who.IsValid || rating == null)
                return;

            lock (gate)
            {
                if (!players.TryGetValue(who.Key, out var existing))
                {
                    existing = new PlayerSeen
                    {
                        Name = who.Name,
                        World = who.World,
                        TimesMet = 0,
                    };
                    players[who.Key] = existing;
                    Trim();
                }

                existing.Rating = rating;
                existing.RatingUpdatedUtc = DateTime.UtcNow;
                dirty = true;
            }
        }

        // ══════════════════════════════════════════════════════════
        //  WRITES
        // ══════════════════════════════════════════════════════════

        /// <summary>
        /// Folds a finished duty into the history: one row per member, created or updated.
        ///
        /// Called for a duty the encounter log already decided was worth keeping, so the "did this
        /// last long enough to count" judgement lives in one place rather than being made twice
        /// with two different answers.
        /// </summary>
        public void RecordEncounter(DutyEncounter encounter)
        {
            if (encounter?.Members == null || encounter.Members.Count == 0)
                return;

            DateTime when = encounter.CompletedUtc == default
                ? DateTime.UtcNow
                : encounter.CompletedUtc;

            lock (gate)
            {
                foreach (var member in encounter.Members)
                {
                    if (!member.IsValid)
                        continue;

                    string key = member.Identity.Key;

                    if (players.TryGetValue(key, out var existing))
                    {
                        existing.TimesMet++;
                        existing.LastDutyName = encounter.DutyName ?? existing.LastDutyName;

                        // Straight from the game, so it outranks anything fetched.
                        if (member.JobId != 0)
                        {
                            existing.JobId = member.JobId;
                            existing.JobFromGame = true;
                            existing.JobUpdatedUtc = when;
                        }

                        // Guarded because duties are committed when they end, and a long duty that
                        // started before a short one finishes after it. Taking the later of the two
                        // keeps the list in the order the player actually experienced.
                        if (when > existing.LastSeenUtc)
                            existing.LastSeenUtc = when;

                        continue;
                    }

                    players[key] = new PlayerSeen
                    {
                        Name = member.Name,
                        World = member.World,
                        JobId = member.JobId,
                        JobFromGame = member.JobId != 0,
                        JobUpdatedUtc = member.JobId != 0 ? when : default,
                        FirstSeenUtc = when,
                        LastSeenUtc = when,
                        TimesMet = 1,
                        LastDutyName = encounter.DutyName ?? string.Empty,
                    };
                }

                Trim();
                dirty = true;
            }

            Flush();
        }

        /// <summary>
        /// Fills an empty history from the stores that predate it.
        ///
        /// Without this, upgrading empties the Recent players list: it used to be drawn from the
        /// ratings you had given, and it is now drawn from this file, which starts with nothing in
        /// it. The names are all still on disk in the older stores, so the list would come back
        /// blank while claiming to be permanent - which reads as having lost them. The same
        /// problem, and the same fix, as <see cref="RatingHistory.SeedFrom"/>.
        ///
        /// Only ever runs into an empty history, so it cannot overwrite a real meeting with a
        /// reconstructed one. What it recovers is approximate and deliberately conservative:
        /// TimesMet starts at 1 because neither old store counted meetings, and a made-up number
        /// would be worse than an honest floor.
        /// </summary>
        public void SeedFrom(IEnumerable<Contact>? contacts, IEnumerable<RatingGiven>? rated)
        {
            lock (gate)
            {
                if (players.Count > 0)
                    return;

                // Contacts first: they carry the duty and the time you actually met, which is
                // better than the time you rated someone.
                foreach (var contact in contacts ?? Enumerable.Empty<Contact>())
                {
                    var member = contact?.Member;
                    if (member == null || !member.IsValid)
                        continue;

                    players[member.Identity.Key] = new PlayerSeen
                    {
                        Name = member.Name,
                        World = member.World,
                        JobId = member.JobId,
                        FirstSeenUtc = contact!.MetUtc,
                        LastSeenUtc = contact.MetUtc,
                        TimesMet = 1,
                        LastDutyName = contact.DutyName ?? string.Empty,
                    };
                }

                foreach (var entry in rated ?? Enumerable.Empty<RatingGiven>())
                {
                    if (entry == null || !entry.IsValid)
                        continue;

                    string key = entry.Identity.Key;
                    if (players.ContainsKey(key))
                        continue;

                    // No duty name to recover - the rating history never stored one - and an
                    // invented one would be a lie about where you know them from.
                    players[key] = new PlayerSeen
                    {
                        Name = entry.Name,
                        World = entry.World,
                        JobId = entry.JobId,
                        FirstSeenUtc = entry.RatedUtc,
                        LastSeenUtc = entry.RatedUtc,
                        TimesMet = 1,
                    };
                }

                if (players.Count == 0)
                    return;

                dirty = true;
                log.Debug($"[Ratings] Seeded the player history with {players.Count} known character(s).");
            }

            Flush();
        }

        public void Clear()
        {
            lock (gate)
            {
                players.Clear();
                dirty = true;
            }

            Flush();
        }

        /// <summary>Only ever reached if something has gone wrong; least recently seen go first.</summary>
        private void Trim()
        {
            if (players.Count <= HardCap)
                return;

            var doomed = players.Values
                .OrderBy(p => p.Met)          // job-only rows first: nothing was met to forget
                .ThenBy(p => p.LastSeenUtc)
                .Take(players.Count - HardCap)
                .Select(p => p.Key)
                .ToList();

            foreach (var key in doomed)
                players.Remove(key);

            log.Debug($"[Ratings] Player history hit its cap; dropped {doomed.Count} of the oldest.");
        }

        // ══════════════════════════════════════════════════════════
        //  PERSISTENCE
        // ══════════════════════════════════════════════════════════

        private void Load()
        {
            try
            {
                if (!File.Exists(path))
                    return;

                var loaded = JsonConvert.DeserializeObject<List<PlayerSeen>>(
                    File.ReadAllText(path), Settings);

                if (loaded == null)
                    return;

                lock (gate)
                {
                    foreach (var entry in loaded)
                    {
                        if (entry != null && entry.IsValid)
                            players[entry.Key] = entry;
                    }
                }
            }
            catch (Exception ex)
            {
                // A history that fails to load is a cosmetic loss, and it must not stop the plugin
                // starting. Starting empty is recoverable; refusing to start is not.
                log.Debug($"[Ratings] Couldn't read the player history: {ex.Message}");
            }
        }

        public void Flush()
        {
            List<PlayerSeen> snapshot;

            lock (gate)
            {
                if (!dirty)
                    return;

                dirty = false;
                lastWriteUtc = DateTime.UtcNow;
                snapshot = players.Values.ToList();
            }

            try
            {
                // Written through a temp file and moved into place, so an interrupted write leaves
                // the previous history intact rather than a half-file that won't parse.
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, JsonConvert.SerializeObject(snapshot, Settings));
                File.Move(tmp, path, overwrite: true);
            }
            catch (Exception ex)
            {
                log.Debug($"[Ratings] Couldn't save the player history: {ex.Message}");
            }
        }
    }
}
#endif
