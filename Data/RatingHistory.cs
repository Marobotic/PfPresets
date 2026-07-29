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
    /// <summary>One rating this install has given.</summary>
    public sealed class RatingGiven
    {
        public string Name { get; set; } = string.Empty;
        public string World { get; set; } = string.Empty;
        public uint JobId { get; set; }

        /// <summary>Which way it went. Shown back as the same arrow the button used.</summary>
        public VoteDirection Direction { get; set; }

        public DateTime RatedUtc { get; set; }

        [JsonIgnore]
        public CharacterIdentity Identity => new(Name, World);

        [JsonIgnore]
        public bool IsValid => !string.IsNullOrWhiteSpace(Name) && !string.IsNullOrWhiteSpace(World);
    }

    /// <summary>
    /// The people this install has rated, most recent first.
    ///
    /// This exists because of a real bug: the eligible list drops anyone already rated, and until
    /// now nothing else listed them - so rating someone made them vanish with no trace that it had
    /// worked. This is the trace.
    ///
    /// Its own file rather than part of the encounter log, because the two have different
    /// lifetimes: the encounter log forgets people after a week so it stays small, while this is a
    /// record of something you did and is kept until it falls off the end.
    /// </summary>
    public sealed class RatingHistory
    {
        private const string FileName = "ratings-given.json";

        /// <summary>How many entries are kept. Past this the oldest is dropped.</summary>
        public const int MaxEntries = 100;

        private static readonly JsonSerializerSettings Settings = new()
        {
            TypeNameHandling = TypeNameHandling.None,
            MetadataPropertyHandling = MetadataPropertyHandling.Ignore,
            MaxDepth = 16,
            Formatting = Formatting.Indented,
        };

        private readonly string path;
        private readonly IPluginLog log;
        private readonly List<RatingGiven> entries = new();
        private readonly object gate = new();
        private bool dirty;

        public RatingHistory(IDalamudPluginInterface pluginInterface, IPluginLog log)
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
            get { lock (gate) { return entries.Count; } }
        }

        /// <summary>Most recent first.</summary>
        public List<RatingGiven> Recent()
        {
            lock (gate)
                return entries.OrderByDescending(e => e.RatedUtc).ToList();
        }

        /// <summary>
        /// When this install last rated a character, or null. Read by the cooldown check as a
        /// second, independent record of the same fact.
        /// </summary>
        public DateTime? LastRated(CharacterIdentity who)
        {
            if (!who.IsValid)
                return null;

            lock (gate)
            {
                foreach (var e in entries)
                {
                    if (e.IsValid && e.Identity.Equals(who))
                        return e.RatedUtc;
                }
            }
            return null;
        }

        /// <summary>
        /// Records a rating. A repeat rating of the same character replaces the earlier entry
        /// rather than adding a second one - the list answers "what do I think of them", which has
        /// exactly one current answer.
        /// </summary>
        public void Add(CharacterIdentity who, uint jobId, VoteDirection direction)
        {
            if (!who.IsValid)
                return;

            lock (gate)
            {
                entries.RemoveAll(e => e.IsValid && e.Identity.Equals(who));

                entries.Add(new RatingGiven
                {
                    Name = who.Name,
                    World = who.World,
                    JobId = jobId,
                    Direction = direction,
                    RatedUtc = DateTime.UtcNow,
                });

                Trim();
                dirty = true;
            }

            Flush();
        }

        /// <summary>
        /// Recovers entries from the cooldown list for anyone the history doesn't already know
        /// about. The cooldown list has always recorded who was rated and when; the history file
        /// arrived later, so without this an existing install shows an empty Recent players even
        /// though it has cooldowns going back a day.
        ///
        /// The direction can't be recovered - the cooldown never stored it - so those entries show
        /// a neutral dot rather than an arrow they'd be guessing at.
        /// </summary>
        public void SeedFrom(IReadOnlyDictionary<string, DateTime> cooldowns, Func<CharacterIdentity, uint> jobLookup)
        {
            if (cooldowns.Count == 0)
                return;

            bool added = false;

            lock (gate)
            {
                var known = new HashSet<string>(
                    entries.Where(e => e.IsValid).Select(e => e.Identity.Key),
                    StringComparer.OrdinalIgnoreCase);

                foreach (var (key, when) in cooldowns)
                {
                    if (known.Contains(key))
                        continue;

                    int at = key.LastIndexOf('@');
                    if (at <= 0 || at == key.Length - 1)
                        continue;

                    var recovered = new RatingGiven
                    {
                        Name = Capitalise(key.Substring(0, at)),
                        World = Capitalise(key.Substring(at + 1)),
                        Direction = VoteDirection.Unknown,
                        RatedUtc = when,
                    };

                    // The duty log still remembers what they were playing, even though the
                    // cooldown list never did.
                    recovered.JobId = jobLookup(recovered.Identity);

                    entries.Add(recovered);
                    added = true;
                }

                if (!added)
                    return;

                Trim();
                dirty = true;
            }

            Flush();
        }

        /// <summary>The cooldown key is lowercased, so names come back needing their case restored.
        /// Only a display nicety - nothing matches on it.</summary>
        private static string Capitalise(string s)
        {
            var parts = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length; i++)
            {
                parts[i] = char.ToUpperInvariant(parts[i][0])
                         + (parts[i].Length > 1 ? parts[i].Substring(1) : string.Empty);
            }
            return string.Join(' ', parts);
        }

        /// <summary>Fills in job icons on any entry still missing one, now that the duty log is
        /// available. Cheap, and runs once at startup.</summary>
        public void BackfillJobs(Func<CharacterIdentity, uint> jobLookup)
        {
            bool changed = false;

            lock (gate)
            {
                foreach (var e in entries)
                {
                    if (!e.IsValid || e.JobId != 0)
                        continue;

                    uint job = jobLookup(e.Identity);
                    if (job == 0)
                        continue;

                    e.JobId = job;
                    changed = true;
                }

                if (!changed)
                    return;
                dirty = true;
            }

            Flush();
        }

        public void Clear()
        {
            lock (gate)
            {
                entries.Clear();
                dirty = true;
            }
            Flush();
        }

        /// <summary>Caller must hold the lock.</summary>
        private void Trim()
        {
            if (entries.Count <= MaxEntries)
                return;

            entries.Sort((a, b) => b.RatedUtc.CompareTo(a.RatedUtc));
            entries.RemoveRange(MaxEntries, entries.Count - MaxEntries);
        }

        private void Load()
        {
            try
            {
                if (!File.Exists(path))
                    return;

                var loaded = JsonConvert.DeserializeObject<List<RatingGiven>>(File.ReadAllText(path), Settings);
                if (loaded == null)
                    return;

                lock (gate)
                {
                    entries.Clear();
                    entries.AddRange(loaded.Where(e => e != null && e.IsValid));
                    Trim();
                }
            }
            catch (Exception ex)
            {
                log.Debug($"[Ratings] Could not read the rating history, starting fresh: {ex.Message}");
            }
        }

        /// <summary>Writes via a temp file and a move, so a crash mid-write leaves the previous
        /// file intact rather than a half-written one.</summary>
        public void Flush()
        {
            List<RatingGiven> snapshot;
            lock (gate)
            {
                if (!dirty)
                    return;
                dirty = false;
                snapshot = new List<RatingGiven>(entries);
            }

            try
            {
                string temp = path + ".tmp";
                File.WriteAllText(temp, JsonConvert.SerializeObject(snapshot, Settings));
                File.Move(temp, path, overwrite: true);
            }
            catch (Exception ex)
            {
                log.Debug($"[Ratings] Could not write the rating history: {ex.Message}");
            }
        }
    }
}
#endif
