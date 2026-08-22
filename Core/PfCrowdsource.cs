#if PFP_RATINGS
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;

namespace PfPresets
{
    /// <summary>
    /// Publishes the party this character is sitting in while it is listed in the party finder,
    /// and reads back the party behind a listing somebody else owns.
    ///
    /// ONE PARTICIPANT DESCRIBES THE WHOLE PARTY. This used to send only the local character, on
    /// the reasoning that the other seven had not agreed to be published. The result was a feature
    /// that could not work: a reader learned one name they could usually already see, and the
    /// panel only filled up if every seat happened to be running this plugin. Reporting the party
    /// is what makes one user enough, which is how PFRadar behaves and what was asked for.
    ///
    /// WHAT LEAVES THIS MACHINE, EXACTLY: the leader's name and world (the key everyone matches
    /// on), the duty id, and for every party member their name, world and job - all of it already
    /// on this client's screen, and none of it beyond what standing in that party would show you.
    ///
    /// WHAT STILL BOUNDS IT: only a listed party is ever sent, never a private one. It goes up
    /// while the listing is up and comes down when it ends, and the server forgets a report within
    /// the hour regardless - this describes a party recruiting in public right now, not where
    /// anybody has been. It is off for anybody running PFRadar, which does this already, and off
    /// for anybody who turns it off.
    /// </summary>
    internal sealed class PfCrowdsource
    {
        /// <summary>How often a standing report is refreshed while nothing about the party has
        /// changed. The row carries its own timestamp and the server expires it, so this is a
        /// heartbeat rather than a write per frame - a party that actually changes is reported at
        /// the next tick, not at the next heartbeat.</summary>
        private static readonly TimeSpan ReportEvery = TimeSpan.FromMinutes(2);

        /// <summary>How long a looked-up roster is trusted before asking again. A party fills over
        /// minutes, not seconds, and the panel is read for a moment.</summary>
        private static readonly TimeSpan RosterFreshFor = TimeSpan.FromSeconds(45);

        /// <summary>How often the reporting half looks at the world at all.</summary>
        private static readonly TimeSpan TickEvery = TimeSpan.FromSeconds(5);

        private readonly PfApiClient api;
        private readonly Configuration config;
        private readonly IPluginLog log;
        private readonly PfAutomation pfAutomation;
        private readonly WorldHelper worlds;
        private readonly Func<CharacterIdentity?> localIdentity;
        private readonly Func<bool> suppressed;

        private DateTime lastTick = DateTime.MinValue;
        private DateTime lastReport = DateTime.MinValue;

        /// <summary>
        /// A frame number for <c>GetSnapshot</c> that cannot collide with the UI's.
        ///
        /// That method caches one snapshot keyed on the number it is handed, and the UI hands it
        /// <c>ImGui.GetFrameCount()</c>. Passing anything from this side that could ever equal an
        /// ImGui frame count - a tick count, say - risks the UI being handed a snapshot built on a
        /// different frame, which is a stale party list drawn as a current one.
        ///
        /// ImGui frame counts are never negative, so counting down from zero can never be mistaken
        /// for one. The cost is that our call rebuilds rather than sharing the UI's snapshot, which
        /// at once every five seconds is nothing.
        /// </summary>
        private int snapshotTicket = -1;

        /// <summary>The key we last reported under, so a listing that ends can be withdrawn from
        /// and a party whose leader changes does not leave a row behind under the old one.</summary>
        private string reportedUnder = string.Empty;

        /// <summary>
        /// The roster we last sent, flattened, so a party that CHANGES is published within five
        /// seconds instead of within two minutes.
        ///
        /// This is the difference between a panel that is worth looking at and one that is not.
        /// Somebody reads a listing to decide whether to join it, and a two-minute-old answer to
        /// "who is in this" describes a party that has since filled or emptied. Comparing what we
        /// are about to say against what we last said costs a string per tick and makes the
        /// heartbeat purely a backstop against a dropped request.
        /// </summary>
        private string reportedRoster = string.Empty;

        private readonly ConcurrentDictionary<string, (DateTime When, List<PfMember> Members)> rosters = new();
        private readonly ConcurrentDictionary<string, byte> rosterInFlight = new();

        public PfCrowdsource(PfApiClient api, Configuration config, IPluginLog log,
            PfAutomation pfAutomation, WorldHelper worlds,
            Func<CharacterIdentity?> localIdentity, Func<bool> suppressed)
        {
            this.api = api;
            this.config = config;
            this.log = log;
            this.pfAutomation = pfAutomation;
            this.worlds = worlds;
            this.localIdentity = localIdentity;
            this.suppressed = suppressed;
        }

        /// <summary>Whether this install is taking part at all.</summary>
        public bool Enabled
            => config.PfCrowdsourceEnabled && config.RatingsEnabled && !suppressed();

        // ── Reporting ─────────────────────────────────────────────

        /// <summary>
        /// Called every frame; does something about once every five seconds.
        ///
        /// The whole reporting half lives here rather than reacting to an event, because "am I in a
        /// listing" has no event - the listing goes up, fills, and comes down through several
        /// different mechanisms, and polling a cheap flag is more honest than trying to hook them
        /// all and missing one.
        /// </summary>
        public void Tick()
        {
            if (DateTime.UtcNow - lastTick < TickEvery)
                return;

            lastTick = DateTime.UtcNow;

            // Minted here rather than above, so it advances once per tick instead of once per
            // frame - the counter only has to be unique against ImGui's, not busy.
            int frameCount = snapshotTicket--;

            try
            {
                if (!Enabled)
                {
                    Withdraw();
                    return;
                }

                if (!TryGetListingKey(frameCount, out string leaderName, out string leaderWorld))
                {
                    // Not in a listing any more - take the row down rather than letting it age out,
                    // so somebody looking at that party stops being told we are in it.
                    Withdraw();
                    return;
                }

                string key = $"{leaderName}@{leaderWorld}";

                var me = localIdentity();
                if (me is not { IsValid: true })
                    return;

                var (jobId, _) = pfAutomation.GetLocalJobAndLevel();
                var members = CollectParty(me, jobId);

                // Nothing worth publishing. Not a withdrawal - the listing is still up and we are
                // still in it, so this is a party read that came back empty for a beat rather than
                // a party that has gone.
                if (members.Count == 0)
                    return;

                string roster = RosterSignature(members);

                // A changed listing or a changed party is a report NOW rather than at the next
                // heartbeat; an unchanged one waits for the heartbeat.
                bool changed = key != reportedUnder || roster != reportedRoster;

                if (!changed && DateTime.UtcNow - lastReport < ReportEvery)
                    return;

                lastReport = DateTime.UtcNow;
                reportedUnder = key;
                reportedRoster = roster;

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await api.ReportPfListingAsync(new PfReportRequest
                        {
                            LeaderName = leaderName,
                            LeaderWorld = leaderWorld,
                            Name = me.Name,
                            World = me.World,
                            Job = (int)jobId,
                            DutyId = (int)CurrentDutyId(frameCount),
                            Members = members,
                        }).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        log.Debug($"[PF] Report failed: {ex.Message}");
                    }
                });
            }
            catch (Exception ex)
            {
                log.Debug($"[PF] Tick failed: {ex.Message}");
            }
        }

        /// <summary>Takes our row down. Cheap to call when there is nothing to withdraw - it only
        /// does anything if we believe we have a row up.</summary>
        public void Withdraw()
        {
            if (reportedUnder.Length == 0)
                return;

            reportedUnder = string.Empty;
            reportedRoster = string.Empty;
            lastReport = DateTime.MinValue;

            _ = Task.Run(async () =>
            {
                try
                {
                    await api.WithdrawPfListingAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    log.Debug($"[PF] Withdraw failed: {ex.Message}");
                }
            });
        }

        /// <summary>
        /// The party as it stands, this character included.
        ///
        /// The local player comes from <paramref name="me"/> rather than from the party read,
        /// because both party proxies deliberately leave you out of their own list - and because
        /// in a party of one there is no list to be left out of. Somebody sitting alone on a fresh
        /// listing is exactly the case the panel most needs to answer.
        ///
        /// Duty Support NPCs are dropped: they hold a seat in the proxy with no content id and no
        /// name, and publishing them would put a blank row on somebody's screen.
        /// </summary>
        private List<PfMember> CollectParty(CharacterIdentity me, uint localJobId)
        {
            var members = new List<PfMember>(8)
            {
                new() { Name = me.Name, World = me.World, Job = (int)localJobId },
            };

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                $"{me.Name}@{me.World}",
            };

            foreach (var member in pfAutomation.GetOtherPartyMemberDetails())
            {
                if (member.IsSupportNpc)
                    continue;

                string world = worlds.GetWorldName(member.HomeWorldId);

                // A member whose world has not resolved is skipped rather than sent with a blank
                // one: "name@" matches nothing on the reading side and would draw as a half-name.
                if (string.IsNullOrWhiteSpace(member.Name) || string.IsNullOrWhiteSpace(world))
                    continue;

                if (!seen.Add($"{member.Name}@{world}"))
                    continue;

                members.Add(new PfMember
                {
                    Name = member.Name,
                    World = world,
                    Job = (int)member.JobId,
                });
            }

            return members;
        }

        /// <summary>
        /// A roster flattened to one string, for telling "the party changed" from "the party is
        /// the same" without keeping a copy of the last one around.
        ///
        /// The job is in it on purpose. Somebody swapping from a healer to a tank while sitting in
        /// the listing changes what the party still needs, which is the thing a reader is deciding
        /// on - so it counts as a change worth sending.
        /// </summary>
        private static string RosterSignature(List<PfMember> members)
        {
            var parts = new List<string>(members.Count);
            foreach (var m in members)
                parts.Add($"{m.Name}@{m.World}#{m.Job}");

            // Sorted, so the party proxy handing back the same people in a different order is not
            // mistaken for a party that changed.
            parts.Sort(StringComparer.OrdinalIgnoreCase);
            return string.Join("|", parts);
        }

        private uint CurrentDutyId(int frameCount)
        {
            try
            {
                return pfAutomation.GetSnapshot(frameCount).DutyRowId;
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>
        /// The listing this character is currently in, named the way everybody else will name it.
        ///
        /// THE KEY HAS TO BE THE SAME STRING ON BOTH SIDES. Somebody looking at a listing reads the
        /// leader's name and world out of the game's own listing data; somebody sitting in that
        /// listing has to arrive at exactly the same pair or the two never meet. Hence the leader,
        /// rather than anything about the party that only one side can see.
        /// </summary>
        private bool TryGetListingKey(int frameCount, out string leaderName, out string leaderWorld)
        {
            leaderName = string.Empty;
            leaderWorld = string.Empty;

            var snapshot = pfAutomation.GetSnapshot(frameCount);
            if (!snapshot.IsRecruiting)
                return false;

            // Leading it ourselves is the unambiguous case: the listing is ours, so the key is us.
            if (snapshot.IsLeader || pfAutomation.IsPartyLeader())
            {
                var me = localIdentity();
                if (me is not { IsValid: true })
                    return false;

                leaderName = me.Name;
                leaderWorld = me.World;
                return true;
            }

            // A member of somebody else's listing. THE LEADER COMES FROM THE PARTY, not from the
            // snapshot.
            //
            // This used to read snapshot.LeaderName and then match it against the party list by
            // name. That name comes from the game's LastViewedListing - the listing this client
            // last opened in a detail window - so it is there right after joining through the
            // browser and gone once anything else has been looked at. The result was a report that
            // fired for a minute or two after joining and then silently stopped for the rest of the
            // listing, which reads from the outside as the feature not working at all: everybody in
            // the party is running the plugin and the panel still knows nobody.
            //
            // The party knows who leads it at all times and needs no window to have been opened, so
            // it is asked directly. Same character either way, so the key is the same string a
            // viewer builds from the listing.
            if (pfAutomation.TryGetPartyLeader(out string partyLeader, out uint leaderWorldId))
            {
                string partyLeaderWorld = worlds.GetWorldName(leaderWorldId);
                if (!string.IsNullOrWhiteSpace(partyLeaderWorld))
                {
                    leaderName = partyLeader;
                    leaderWorld = partyLeaderWorld;
                    return true;
                }
            }

            // The captured listing, as a fallback for the case the party read cannot cover: a
            // cross-world party whose proxy has not filled in yet.
            if (string.IsNullOrWhiteSpace(snapshot.LeaderName))
                return false;

            foreach (var member in pfAutomation.GetOtherPartyMemberDetails())
            {
                if (!string.Equals(member.Name, snapshot.LeaderName, StringComparison.OrdinalIgnoreCase))
                    continue;

                string world = worlds.GetWorldName(member.HomeWorldId);
                if (string.IsNullOrWhiteSpace(world))
                    return false;

                leaderName = member.Name;
                leaderWorld = world;
                return true;
            }

            return false;
        }

        // ── Reading ───────────────────────────────────────────────

        /// <summary>
        /// Who has published themselves into this listing, or null while nobody has been asked yet.
        ///
        /// Safe to call every frame: it answers from the cache and asks the server at most once per
        /// listing per <see cref="RosterFreshFor"/>.
        /// </summary>
        public IReadOnlyList<PfMember>? RosterFor(string leaderName, string leaderWorld)
        {
            if (!Enabled || string.IsNullOrWhiteSpace(leaderName) || string.IsNullOrWhiteSpace(leaderWorld))
                return null;

            string key = $"{leaderName}@{leaderWorld}".ToLowerInvariant();

            if (rosters.TryGetValue(key, out var found))
            {
                if (DateTime.UtcNow - found.When < RosterFreshFor)
                    return found.Members;
            }
            else
            {
                found = default;
            }

            Fetch(key, leaderName, leaderWorld);

            // The stale copy while the new one is on its way. A panel that blanked every time it
            // refreshed would flicker once a minute for no reason.
            return found.Members;
        }

        private void Fetch(string key, string leaderName, string leaderWorld)
        {
            if (!rosterInFlight.TryAdd(key, 0))
                return;

            _ = Task.Run(async () =>
            {
                try
                {
                    var result = await api.LookupPfListingAsync(leaderName, leaderWorld)
                        .ConfigureAwait(false);

                    if (result.IsOk && result.Value != null)
                        rosters[key] = (DateTime.UtcNow, result.Value.Members ?? new List<PfMember>());
                }
                catch (Exception ex)
                {
                    log.Debug($"[PF] Lookup failed: {ex.Message}");
                }
                finally
                {
                    rosterInFlight.TryRemove(key, out _);
                }
            });
        }
    }
}
#endif
