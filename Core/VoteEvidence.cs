#if PFP_RATINGS
using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;

namespace PfPresets
{
    /// <summary>
    /// Evidence: the sealed payload a rating or vote carries to show it came out of a real duty.
    ///
    /// <para>
    /// The payload names the instance, the duty, when it started and ended, who was in the party
    /// and who the vote is about. It is serialised, then sealed with AES-GCM under a key compiled
    /// into the plugin, and travels as one opaque base64 blob.
    /// </para>
    ///
    /// <para>
    /// This is a speed bump, not a defence. The key ships inside every copy of the plugin, so it
    /// is symmetric in the worst sense: anyone holding the assembly can both read a payload and
    /// mint a new one. What actually holds is the server-side checking - a duty of plausible
    /// length, containing both players, recent, and no more of them per hour than a person could
    /// really play. Treat everything below as untrusted input on arrival.
    /// </para>
    /// </summary>
    internal partial class RatingService
    {
        private static readonly byte[] EvidenceKey = Convert.FromHexString("6041965aaee0ba93c46ea3f1e407be24dfab2e53962765961cab50014681eeee");

        partial void BuildClearEvidence(DutyEncounter encounter, ref string evidence)
        {
            CharacterIdentity me = api.LocalIdentity;

            // A NAMELESS DUTY IS STILL A ROOM WITH PEOPLE IN IT.
            //
            // This used to give up when DutyName was blank, and giving up here is total: no sealed
            // payload means PostAchievement returns without calling the server, so no duty is filed,
            // so nothing exists for a vote to be checked against - and every vote cast out of that
            // duty is held forever, with nothing on either side to say why. Meanwhile
            // BuildVoteEvidence has never required the name, so the vote itself sealed perfectly.
            // One missing string, and the vote and the thing that justifies it stopped agreeing.
            //
            // The name comes from a lookup that returns empty on any failure, so this fired for
            // ordinary reasons - content whose row could not be resolved, or a duty picked up after
            // a plugin reload. The server does not need it: it names the fight for the feed and
            // nothing else, and a duty it cannot name is simply not posted. The roster is the part
            // that matters, and that is present either way.
            if (encounter == null || me == null || !me.IsValid)
            {
                return;
            }
            List<object> party = new List<object>
            {
                new
                {
                    n = me.Name,
                    w = me.World,
                    j = (int)encounter.LocalJobId
                }
            };
            foreach (EncounterMember member in encounter.Members)
            {
                if (member.IsValid)
                {
                    party.Add(new
                    {
                        n = member.Identity.Name,
                        w = member.Identity.World,
                        j = (int)member.JobId
                    });
                }
            }
            if (party.Count >= 2)
            {
                var payload = new
                {
                    v = 1,
                    instance = encounter.Id,
                    duty = (int)encounter.DutyRowId,
                    dutyName = encounter.DutyName,
                    started = ToUnixMsEvidence(encounter.StartedUtc),
                    ended = ToUnixMsEvidence(encounter.CompletedUtc),
                    cleared = encounter.Cleared,
                    job = (int)encounter.LocalJobId,
                    region = RegionOf?.Invoke(me.World),
                    party = party,
                    me = new
                    {
                        n = me.Name,
                        w = me.World
                    },
                    ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    nonce = Guid.NewGuid().ToString("N")
                };
                evidence = Seal(JsonConvert.SerializeObject(payload));
            }
        }

        partial void BuildSettingEvidence(string kind, ref string evidence)
        {
            CharacterIdentity me = api.LocalIdentity;
            if (me != null && me.IsValid && !string.IsNullOrEmpty(kind))
            {
                var payload = new
                {
                    v = 1,
                    kind = kind,
                    me = new
                    {
                        n = me.Name,
                        w = me.World
                    },
                    ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    nonce = Guid.NewGuid().ToString("N")
                };
                evidence = Seal(JsonConvert.SerializeObject(payload));
            }
        }

        partial void BuildVoteEvidence(CharacterIdentity target, int score, ref string evidence)
        {
            CharacterIdentity me = api.LocalIdentity;
            DutyEncounter encounter = encounters.LatestWith(target);
            if (encounter == null || me == null || !me.IsValid || !target.IsValid)
            {
                return;
            }
            List<object> party = new List<object>
            {
                new
                {
                    n = me.Name,
                    w = me.World,
                    j = (int)encounter.LocalJobId
                }
            };
            foreach (EncounterMember member in encounter.Members)
            {
                if (member.IsValid)
                {
                    party.Add(new
                    {
                        n = member.Identity.Name,
                        w = member.Identity.World,
                        j = (int)member.JobId
                    });
                }
            }
            var payload = new
            {
                v = 1,
                instance = encounter.Id,
                duty = (int)encounter.DutyRowId,
                dutyName = encounter.DutyName,
                started = ToUnixMsEvidence(encounter.StartedUtc),
                ended = ToUnixMsEvidence(encounter.CompletedUtc),
                cleared = encounter.Cleared,
                job = (int)encounter.LocalJobId,
                party = party,
                me = new
                {
                    n = me.Name,
                    w = me.World
                },
                target = new
                {
                    n = target.Name,
                    w = target.World
                },
                score = score,
                ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                nonce = Guid.NewGuid().ToString("N")
            };
            evidence = Seal(JsonConvert.SerializeObject(payload));
        }

        private static long ToUnixMsEvidence(DateTime utc)
        {
            return new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
        }

        private static string Seal(string json)
        {
            byte[] plain = Encoding.UTF8.GetBytes(json);
            byte[] iv = RandomNumberGenerator.GetBytes(12);
            byte[] cipher = new byte[plain.Length];
            byte[] tag = new byte[16];
            using (AesGcm gcm = new AesGcm(EvidenceKey, 16))
            {
                gcm.Encrypt(iv, plain, cipher, tag);
            }
            byte[] blob = new byte[iv.Length + cipher.Length + tag.Length];
            Buffer.BlockCopy(iv, 0, blob, 0, iv.Length);
            Buffer.BlockCopy(cipher, 0, blob, iv.Length, cipher.Length);
            Buffer.BlockCopy(tag, 0, blob, iv.Length + cipher.Length, tag.Length);
            return Convert.ToBase64String(blob);
        }
    }
}
#endif
