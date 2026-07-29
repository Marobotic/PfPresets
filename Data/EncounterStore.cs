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
    /// <summary>
    /// The local log of duties and who was in them, persisted beside the plugin config rather
    /// than inside it.
    ///
    /// It lives in its own file for two reasons. Dalamud rewrites the whole config blob on every
    /// <see cref="Configuration.Save"/>, so putting a per-duty log in there would mean serialising
    /// every preset the user owns each time somebody joins a party. And this is the one piece of
    /// data the plugin holds about other people, so it is worth being able to point at a single
    /// file and say "that is all of it, delete it and nothing is lost".
    ///
    /// The log is capped on both axes - <see cref="MaxEncounters"/> and <see cref="MaxAgeDays"/> -
    /// because it only needs to answer "who did I just play with", not keep a permanent history.
    /// </summary>
    public sealed class EncounterStore
    {
        private const string FileName = "encounters.json";

        /// <summary>
        /// How many distinct players the contact list remembers. This is the real retention limit:
        /// once you have met a 25th person, the oldest one is forgotten entirely, along with any
        /// duty that no longer has anyone worth remembering in it.
        ///
        /// Small on purpose. This list exists so you can rate the people you just played with, not
        /// so the plugin can build a history of everyone you have ever met.
        /// </summary>
        public const int MaxContacts = 24;

        /// <summary>Backstop cap on retained duties, in case a single session produces many duties
        /// with few distinct players.</summary>
        public const int MaxEncounters = 60;

        /// <summary>Encounters older than this are dropped on load and on every save.</summary>
        public const int MaxAgeDays = 7;

        /// <summary>
        /// How long after meeting someone you may still vote on them. Past this the encounter stays
        /// in Contacts as a record of who you played with, but the vote is gone: an opinion formed
        /// about a run yesterday is worth something, one dredged up a week later much less.
        /// </summary>
        public static readonly TimeSpan VotingWindow = TimeSpan.FromHours(24);

        private static readonly JsonSerializerSettings Settings = new()
        {
            TypeNameHandling = TypeNameHandling.None,
            MetadataPropertyHandling = MetadataPropertyHandling.Ignore,
            MaxDepth = 16,
            Formatting = Formatting.Indented,
        };

        private readonly string path;
        private readonly IPluginLog log;
        private readonly List<DutyEncounter> encounters = new();
        private readonly object gate = new();

        private bool dirty;

        public EncounterStore(IDalamudPluginInterface pluginInterface, IPluginLog log)
        {
            this.log = log;

            var dir = pluginInterface.GetPluginConfigDirectory();
            Directory.CreateDirectory(dir);
            path = Path.Combine(dir, FileName);

            Load();
        }

        /// <summary>Where the log lives, so the settings window can show the user the exact file.</summary>
        public string FilePath => path;

        // ══════════════════════════════════════════════════════════
        //  READS
        // ══════════════════════════════════════════════════════════

        /// <summary>Most recent first.</summary>
        public List<DutyEncounter> Recent(int max = 20)
        {
            lock (gate)
            {
                return encounters
                    .OrderByDescending(e => e.CompletedUtc)
                    .Take(max)
                    .ToList();
            }
        }

        /// <summary>The newest duty that still has someone worth rating, or null. This is what the
        /// post-duty prompt offers.</summary>
        public DutyEncounter? LatestPromptable()
        {
            lock (gate)
            {
                return encounters
                    .Where(e => !e.Dismissed && e.HasUnratedMembers)
                    .OrderByDescending(e => e.CompletedUtc)
                    .FirstOrDefault();
            }
        }

        public int Count
        {
            get { lock (gate) { return encounters.Count; } }
        }

        /// <summary>
        /// The people you have met, most recent first, one entry each and capped at
        /// <see cref="MaxContacts"/>. Someone met in several duties appears once, against the most
        /// recent one.
        /// </summary>
        public List<Contact> RecentContacts()
        {
            lock (gate)
            {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var contacts = new List<Contact>();

                foreach (var encounter in encounters.OrderByDescending(e => e.CompletedUtc))
                {
                    foreach (var member in encounter.Members)
                    {
                        if (!member.IsValid || !seen.Add(member.Identity.Key))
                            continue;

                        contacts.Add(new Contact
                        {
                            Member = member,
                            EncounterId = encounter.Id,
                            DutyName = encounter.DutyName,
                            DutyRowId = encounter.DutyRowId,
                            MetUtc = encounter.CompletedUtc,
                        });

                        if (contacts.Count >= MaxContacts)
                            return contacts;
                    }
                }

                return contacts;
            }
        }

        /// <summary>
        /// The people you may still vote on: met inside <see cref="VotingWindow"/> and not yet
        /// voted on. This is what the Ratings tab lists and what decides whether the post-duty
        /// prompt appears at all.
        /// </summary>
        public List<Contact> EligibleToVote()
        {
            var cutoff = DateTime.UtcNow - VotingWindow;
            var eligible = new List<Contact>();

            lock (gate)
            {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var encounter in encounters.OrderByDescending(e => e.CompletedUtc))
                {
                    if (encounter.CompletedUtc < cutoff)
                        continue;

                    foreach (var member in encounter.Members)
                    {
                        if (!member.IsValid || member.Rated)
                            continue;
                        if (!seen.Add(member.Identity.Key))
                            continue;

                        eligible.Add(new Contact
                        {
                            Member = member,
                            EncounterId = encounter.Id,
                            DutyName = encounter.DutyName,
                            DutyRowId = encounter.DutyRowId,
                            MetUtc = encounter.CompletedUtc,
                        });
                    }
                }
            }

            return eligible;
        }

        /// <summary>The people from one specific duty who can still be voted on.</summary>
        public List<Contact> EligibleInEncounter(string encounterId)
        {
            lock (gate)
            {
                var encounter = encounters.FirstOrDefault(e => e.Id == encounterId);
                if (encounter == null || encounter.CompletedUtc < DateTime.UtcNow - VotingWindow)
                    return new List<Contact>();

                return encounter.Members
                    .Where(m => m.IsValid && !m.Rated)
                    .Select(m => new Contact
                    {
                        Member = m,
                        EncounterId = encounter.Id,
                        DutyName = encounter.DutyName,
                        DutyRowId = encounter.DutyRowId,
                        MetUtc = encounter.CompletedUtc,
                    })
                    .ToList();
            }
        }

        /// <summary>
        /// The job this character was last seen on, or 0 if they aren't in the duty log. Used to
        /// put an icon on history entries recovered from the cooldown list, which never stored one.
        /// </summary>
        /// <summary>
        /// Every duty this install has seen a character in, most recent first.
        ///
        /// Local only, and it stays that way. This is the viewer's own record of who they played
        /// with - the server is never sent it and cannot be asked for it. A history of where a
        /// player has been is exactly the kind of thing that should not accumulate centrally.
        /// </summary>
        public List<(string DutyName, DateTime WhenUtc)> SeenIn(CharacterIdentity who, int max = 8)
        {
            var seen = new List<(string, DateTime)>();
            if (!who.IsValid)
                return seen;

            lock (gate)
            {
                foreach (var encounter in encounters.OrderByDescending(e => e.CompletedUtc))
                {
                    foreach (var member in encounter.Members)
                    {
                        if (!member.IsValid || !member.Identity.Key.Equals(who.Key, StringComparison.OrdinalIgnoreCase))
                            continue;

                        seen.Add((encounter.DutyName, encounter.CompletedUtc));
                        break;
                    }

                    if (seen.Count >= max)
                        break;
                }
            }

            return seen;
        }

        public uint LastKnownJob(CharacterIdentity who)
        {
            if (!who.IsValid)
                return 0;

            lock (gate)
            {
                foreach (var encounter in encounters.OrderByDescending(e => e.CompletedUtc))
                {
                    foreach (var member in encounter.Members)
                    {
                        if (member.IsValid && member.JobId != 0 && member.Identity.Equals(who))
                            return member.JobId;
                    }
                }
            }
            return 0;
        }

        /// <summary>
        /// Whether this character is someone the local player has actually finished a duty with.
        /// This is the gate on rating: you may only rate people you have played with, so a name
        /// typed into a search box is never rateable on its own.
        /// </summary>
        public bool HasMet(CharacterIdentity who)
        {
            if (!who.IsValid)
                return false;

            var cutoff = DateTime.UtcNow - VotingWindow;

            lock (gate)
            {
                foreach (var encounter in encounters)
                {
                    // Met, but too long ago to still have an opinion worth recording.
                    if (encounter.CompletedUtc < cutoff)
                        continue;

                    foreach (var member in encounter.Members)
                    {
                        if (member.IsValid && member.Identity.Equals(who))
                            return true;
                    }
                }
            }
            return false;
        }

        // ══════════════════════════════════════════════════════════
        //  WRITES
        // ══════════════════════════════════════════════════════════

        public void Add(DutyEncounter encounter)
        {
            if (encounter.Members.Count == 0)
                return; // a solo duty has nobody to rate

            lock (gate)
            {
                encounters.Add(encounter);
                Trim();
                dirty = true;
            }
            Flush();
        }

        /// <summary>
        /// Marks everyone currently votable as dealt with, without rating them.
        ///
        /// A deliberate decline rather than a postponement: the window is 24 hours and a list you
        /// did not want to answer would otherwise sit there for all of it. Nothing is sent - this
        /// only closes the prompt on this machine.
        /// </summary>
        public int SkipAllVotable()
        {
            int skipped = 0;
            var cutoff = DateTime.UtcNow - VotingWindow;

            lock (gate)
            {
                foreach (var encounter in encounters)
                {
                    if (encounter.CompletedUtc < cutoff)
                        continue;

                    foreach (var member in encounter.Members)
                    {
                        if (member.Rated || !member.IsValid)
                            continue;

                        member.Rated = true;
                        skipped++;
                    }
                }

                dirty = true;
            }

            // Outside the lock, matching MarkRated: Flush takes it again.
            Flush();
            return skipped;
        }

        public void MarkRated(string encounterId, CharacterIdentity who)
        {
            lock (gate)
            {
                var encounter = encounters.FirstOrDefault(e => e.Id == encounterId);
                if (encounter == null)
                    return;

                foreach (var m in encounter.Members)
                {
                    if (m.IsValid && m.Identity.Equals(who))
                        m.Rated = true;
                }
                dirty = true;
            }
            Flush();
        }

        public void Dismiss(string encounterId)
        {
            lock (gate)
            {
                var encounter = encounters.FirstOrDefault(e => e.Id == encounterId);
                if (encounter == null)
                    return;
                encounter.Dismissed = true;
                dirty = true;
            }
            Flush();
        }

        /// <summary>Deletes the whole log. Offered in settings so the user can clear the one thing
        /// the plugin knows about other players without hunting for the file.</summary>
        public void Clear()
        {
            lock (gate)
            {
                encounters.Clear();
                dirty = true;
            }
            Flush();
        }

        /// <summary>
        /// Enforces every retention limit: age, the backstop encounter cap, and - the one that
        /// actually matters - the 24-contact limit. Members beyond the newest 24 distinct people
        /// are removed from their encounters, and any encounter left with nobody in it is dropped.
        ///
        /// Caller must hold the lock.
        /// </summary>
        private void Trim()
        {
            var cutoff = DateTime.UtcNow.AddDays(-MaxAgeDays);
            encounters.RemoveAll(e => e.CompletedUtc < cutoff);

            encounters.Sort((a, b) => b.CompletedUtc.CompareTo(a.CompletedUtc));

            if (encounters.Count > MaxEncounters)
                encounters.RemoveRange(MaxEncounters, encounters.Count - MaxEncounters);

            // Walk newest first, keeping the first MaxContacts distinct people and discarding the
            // rest. Forgetting is the point here, so this deletes rather than hides.
            var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var encounter in encounters)
            {
                encounter.Members.RemoveAll(m =>
                {
                    if (!m.IsValid)
                        return true;

                    string key = m.Identity.Key;
                    if (keep.Contains(key))
                        return false;

                    if (keep.Count >= MaxContacts)
                        return true;

                    keep.Add(key);
                    return false;
                });
            }

            encounters.RemoveAll(e => e.Members.Count == 0);
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

                string json = File.ReadAllText(path);
                var loaded = JsonConvert.DeserializeObject<List<DutyEncounter>>(json, Settings);
                if (loaded == null)
                    return;

                lock (gate)
                {
                    encounters.Clear();
                    encounters.AddRange(loaded.Where(e => e != null));
                    Trim();
                }
            }
            catch (Exception ex)
            {
                // A corrupt log is not worth failing plugin load over - start empty and move on.
                log.Debug($"[Ratings] Could not read the duty log, starting fresh: {ex.Message}");
            }
        }

        /// <summary>
        /// Writes the log if anything changed, via a temp file and a move so a crash mid-write
        /// leaves the previous log intact rather than a half-written one.
        /// </summary>
        public void Flush()
        {
            List<DutyEncounter> snapshot;
            lock (gate)
            {
                if (!dirty)
                    return;
                dirty = false;
                snapshot = new List<DutyEncounter>(encounters);
            }

            try
            {
                string json = JsonConvert.SerializeObject(snapshot, Settings);
                string temp = path + ".tmp";
                File.WriteAllText(temp, json);
                File.Move(temp, path, overwrite: true);
            }
            catch (Exception ex)
            {
                log.Debug($"[Ratings] Could not write the duty log: {ex.Message}");
            }
        }
    }
}
#endif
