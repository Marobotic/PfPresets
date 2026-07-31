using System;
using System.Threading.Tasks;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace PfPresets
{
    /// <summary>What we can honestly say about whether the party leader has a listing up.</summary>
    internal enum LeaderRecruitState
    {
        /// <summary>The leader isn't loaded on this client - another zone, another world - so
        /// their online status can't be read at all. Not the same as "no".</summary>
        Unknown,
        Recruiting,
        NotRecruiting,
    }

    /// <summary>
    /// Keeping the party leader's Party Finder listing in view when the listing isn't ours.
    ///
    /// Only the player who owns a listing can read it directly: everyone else's client holds
    /// nothing but their own last-configured recruitment settings, and the one place another
    /// party's listing lands is <c>LastViewedListing</c> - which is only populated by actually
    /// opening the listing's detail window. So somebody who joins a Party Finder sees "you're in a
    /// cross-world party" and nothing about the fight they just signed up for, while the leader
    /// sees the full card. That asymmetry is what this closes.
    ///
    /// The rule is to spend nothing until there's a reason. The watcher costs one comparison per
    /// frame while the card isn't on screen, one cheap party read per second while it is, and only
    /// opens the game's own listing window when something suggests there is a listing to find:
    /// somebody joining the party, the leader coming into view with the recruiting status, or a
    /// short, capped burst of attempts after joining a party we don't lead. Everything past that
    /// burst waits for a new signal, so an ordinary party with no listing settles into costing
    /// nothing at all.
    /// </summary>
    public partial class PfAutomation
    {
        // ── what we've captured ──

        /// <summary>Leader whose listing is sitting in the agent's LastViewedListing because we
        /// (or the player) opened it, or 0 when there is nothing captured.</summary>
        private ulong capturedLeaderId;

        /// <summary>When that capture happened. LastViewedListing's own TimeLeft is frozen at the
        /// moment it was read - it never counts down and it never clears - so this timestamp is
        /// the only thing that can tell a live listing from the ghost of an ended one.</summary>
        private DateTime capturedLeaderAt;

        /// <summary>True while a capture is in flight, so the watcher and the button can't stack
        /// two of them onto the same window.</summary>
        private volatile bool isCapturingListing;

        // ── watch state ──

        private long lastCardVisibleTick = long.MinValue / 2;
        private long lastWatchTick;
        private ulong watchedLeader;
        private int partySizeSeen = -1;
        private int probeAttempts;
        private long nextProbeTick;
        private LeaderRecruitState lastLeaderState = LeaderRecruitState.Unknown;

        /// <summary>How long after the card was last drawn the watcher keeps running. Long enough
        /// to survive a dropped frame, short enough that closing the window stops the work.</summary>
        private const long CardVisibleGraceMs = 1500;

        /// <summary>The watcher's own tick. Everything it does is cheap, but none of it is worth
        /// doing at frame rate.</summary>
        private const int WatchIntervalMs = 1000;

        /// <summary>Grace after joining a party before the first look, so a party that is still
        /// assembling isn't probed mid-join.</summary>
        private const int FirstProbeDelayMs = 3000;

        /// <summary>Delay after a positive signal (someone joined, the leader appeared with the
        /// recruiting status). Short - this is the case we want to feel immediate.</summary>
        private const int SignalProbeDelayMs = 1200;

        /// <summary>Floor between two probes, whatever else is asking for one.</summary>
        private const int MinProbeSpacingMs = 10_000;

        /// <summary>Attempts before the watcher gives up and waits for a new signal. Four, spread
        /// over about three minutes: enough for a leader who posts their listing a moment after
        /// forming the party, and not a poll.</summary>
        private const int MaxProbeAttempts = 4;

        private static readonly int[] ProbeBackoffMs = { 15_000, 45_000, 120_000, 300_000 };

        /// <summary>How long a capture is used before it's worth re-reading. The listing itself
        /// lives an hour, but its filled slots and comment change inside that, so a card left open
        /// refreshes rather than showing the party as it was when we first looked.</summary>
        private static readonly TimeSpan ListingRefreshAfter = TimeSpan.FromMinutes(5);

        /// <summary>Called by the status card every frame it draws. The card is the only consumer
        /// of the leader's listing, so it is also the switch that runs the watcher.</summary>
        public void MarkListingCardVisible() => lastCardVisibleTick = Environment.TickCount64;

        // ══════════════════════════════════════════════════════════
        //  THE WATCH
        // ══════════════════════════════════════════════════════════

        /// <summary>Called every framework update. Returns almost immediately unless the status
        /// card is on screen and we're in a party somebody else leads.</summary>
        public void UpdateListingWatch()
        {
            if (disposed || openPartyFinder == null)
                return;

            long now = Environment.TickCount64;

            // Nothing is reading the listing, so nothing needs fetching. This is the check that
            // keeps the feature free for anyone with the window closed.
            if (now - lastCardVisibleTick > CardVisibleGraceMs)
                return;

            if (now - lastWatchTick < WatchIntervalMs)
                return;
            lastWatchTick = now;

            try
            {
                WatchTick(now);
            }
            catch (Exception ex)
            {
                // Reading party memory for a convenience is never worth taking the plugin down.
                pluginLog.Debug($"[ListingWatch] Tick failed: {ex.Message}");
            }
        }

        private void WatchTick(long now)
        {
            // In a duty the card is gone and there is no listing to describe; logged out there is
            // no party to read.
            if (!clientState.IsLoggedIn || IsInDuty())
            {
                ResetListingWatch(0);
                return;
            }

            ulong leader = GetPartyLeaderContentId();

            // Solo, or the listing is ours - our own is readable directly and needs none of this.
            if (leader == 0 || leader == playerState.ContentId)
            {
                ResetListingWatch(0);
                return;
            }

            if (leader != watchedLeader)
                ResetListingWatch(leader);

            // The player may have opened the listing themselves in the game's own Party Finder.
            // That is a capture like any other, and a better one than opening it again would be.
            if (capturedLeaderId != leader && LeaderListingLoaded(leader))
                NoteCapturedListing(leader);

            var state = ReadLeaderRecruitState(leader);

            if (state == LeaderRecruitState.NotRecruiting)
            {
                // The leader is standing right there without the recruiting status: there is no
                // listing, and anything we captured earlier describes one that has ended.
                ForgetCapturedListing();
                lastLeaderState = state;
                return;
            }

            // Somebody joining is the one free proof that a listing is up: nobody joins a party
            // that isn't advertising. It costs a party read a second and catches the case where
            // the leader posts long after the party formed.
            int size = CountOtherPartyMembers();
            bool someoneJoined = partySizeSeen >= 0 && size > partySizeSeen;
            partySizeSeen = size;

            bool becameRecruiting = state == LeaderRecruitState.Recruiting
                                    && lastLeaderState != LeaderRecruitState.Recruiting;
            lastLeaderState = state;

            if (someoneJoined || becameRecruiting)
            {
                // New evidence re-arms a watcher that had given up, and brings the next look
                // forward - without ever pushing it past the spacing floor below.
                probeAttempts = 0;
                nextProbeTick = Math.Min(nextProbeTick, now + SignalProbeDelayMs);
            }

            bool have = CapturedListingIsUsable(leader);

            // A full party's listing is gone - the game takes it down on the last join - so there
            // is nothing left to fetch or to keep up to date. What we captured while it was
            // filling is the last word on it, and stands.
            if (PartyLooksFull(size))
                return;

            // What we have is current enough to draw; nothing to do until it ages out.
            if (have && DateTime.UtcNow - capturedLeaderAt < ListingRefreshAfter)
                return;

            if (now < nextProbeTick)
                return;

            // Give up rather than poll. A party with no listing would otherwise be probed forever,
            // and every probe is a window the game opens in front of the player.
            if (!have && probeAttempts >= MaxProbeAttempts)
                return;

            // The player is in the Party Finder themselves. Opening a listing under them would
            // take over the window they are reading, so it waits until they are done.
            if (condition[ConditionFlag.UsingPartyFinder])
                return;

            probeAttempts++;
            nextProbeTick = now + Math.Max(MinProbeSpacingMs,
                ProbeBackoffMs[Math.Min(probeAttempts - 1, ProbeBackoffMs.Length - 1)]);

            // Silent: the watcher speaks only through the card. The Load Details button is the
            // path that reports what went wrong, because somebody asked it a question.
            _ = CaptureLeaderListingAsync(leader, announce: false);
        }

        /// <summary>Starts the schedule over for a new leader (or for none at all).</summary>
        private void ResetListingWatch(ulong leader)
        {
            watchedLeader = leader;
            partySizeSeen = -1;
            probeAttempts = 0;
            lastLeaderState = LeaderRecruitState.Unknown;
            nextProbeTick = Environment.TickCount64 + FirstProbeDelayMs;

            // A capture belongs to the party it was taken in.
            if (leader == 0 || capturedLeaderId != leader)
                ForgetCapturedListing();
        }

        private int CountOtherPartyMembers()
        {
            try { return GetOtherPartyMemberDetails().Count; }
            catch (Exception) { return partySizeSeen; }
        }

        /// <summary>Whether every seat is taken. Alliance parties are counted against their own
        /// size, or a 24-person raid still filling would read as full the moment it passed eight.</summary>
        private unsafe bool PartyLooksFull(int others)
        {
            var proxy = InfoProxyCrossRealm.Instance();
            if (proxy != null && proxy->IsInCrossRealmParty && proxy->IsInAllianceRaid)
                return others >= 23;

            return others >= 7;
        }

        // ══════════════════════════════════════════════════════════
        //  READING THE LEADER
        // ══════════════════════════════════════════════════════════

        /// <summary>
        /// Whether the party leader is advertising, as far as this client can tell.
        ///
        /// Three answers, not two. "Recruiting Party Members" (online status 26) is the same signal
        /// the game's own party list draws its icon from, so it is exact - but only for a leader
        /// who is loaded on this client. A leader in another zone or on another world isn't in the
        /// object table at all, and reporting that as "not recruiting" is what left everyone but
        /// the leader looking at a card with no listing on it.
        /// </summary>
        private unsafe LeaderRecruitState ReadLeaderRecruitState(ulong leaderId)
        {
            if (leaderId == 0)
                return LeaderRecruitState.NotRecruiting;

            // Our own state is authoritative and always readable.
            if (leaderId == playerState.ContentId)
                return IsRecruiting() ? LeaderRecruitState.Recruiting : LeaderRecruitState.NotRecruiting;

            foreach (var obj in objectTable)
            {
                if (obj is not Dalamud.Game.ClientState.Objects.SubKinds.IPlayerCharacter player)
                    continue;

                // Matched on identity first, then read the status. The other way round - find
                // anyone recruiting, then check whether it's the leader - can only ever answer
                // "yes", which is why "the leader is standing here and is plainly not recruiting"
                // was indistinguishable from "the leader isn't loaded".
                var character = (FFXIVClientStructs.FFXIV.Client.Game.Character.Character*)player.Address;
                if (character == null || character->ContentId != leaderId)
                    continue;

                return player.OnlineStatus.RowId == OnlineStatusRecruiting
                    ? LeaderRecruitState.Recruiting
                    : LeaderRecruitState.NotRecruiting;
            }

            return LeaderRecruitState.Unknown;
        }

        /// <summary>Whether the agent is currently holding this leader's listing. Says nothing
        /// about whether it is still true - see <see cref="CapturedListingIsUsable"/>.</summary>
        private unsafe bool LeaderListingLoaded(ulong leaderId)
        {
            if (leaderId == 0)
                return false;

            var agent = AgentLookingForGroup.Instance();
            if (agent == null)
                return false;

            var viewed = agent->LastViewedListing;
            return viewed.ListingId != 0 && viewed.LeaderContentId == leaderId;
        }

        /// <summary>
        /// Whether the captured listing can still be drawn as fact.
        ///
        /// Three ways it stops being usable: the player browsed another listing and overwrote it,
        /// the party's leader changed, or its own countdown ran out. That last one is the important
        /// one - the stored TimeLeft is frozen, so without ageing it by hand a listing that ended
        /// forty minutes ago still reads as having twenty minutes left.
        /// </summary>
        internal bool CapturedListingIsUsable(ulong leaderId)
        {
            if (leaderId == 0 || capturedLeaderId != leaderId)
                return false;
            if (!LeaderListingLoaded(leaderId))
                return false;

            var left = CapturedListingTimeLeft();

            // With no countdown to age, the listing's own maximum life is the backstop - it can't
            // be shown forever just because the client didn't say how long it had.
            return left.HasValue
                ? left.Value > TimeSpan.Zero
                : DateTime.UtcNow - capturedLeaderAt < ListingLifetime;
        }

        /// <summary>What's left of the captured listing's hour, counted down from the moment it was
        /// read, or null when it carried no countdown worth trusting.</summary>
        internal unsafe TimeSpan? CapturedListingTimeLeft()
        {
            var agent = AgentLookingForGroup.Instance();
            if (agent == null)
                return null;

            uint frozen = agent->LastViewedListing.TimeLeft;
            if (frozen == 0 || frozen > ListingLifetime.TotalSeconds)
                return null;

            return TimeSpan.FromSeconds(frozen) - (DateTime.UtcNow - capturedLeaderAt);
        }

        private void NoteCapturedListing(ulong leaderId)
        {
            capturedLeaderId = leaderId;
            capturedLeaderAt = DateTime.UtcNow;

            // The attempt counter is about consecutive failures, so a success clears it: if this
            // listing later expires and the party is still going, it gets a full set of tries
            // rather than whatever was left over from last time.
            probeAttempts = 0;
        }

        private void ForgetCapturedListing() => capturedLeaderId = 0;

        // ══════════════════════════════════════════════════════════
        //  CAPTURE
        // ══════════════════════════════════════════════════════════

        /// <summary>
        /// Opens the party leader's listing so the game populates LastViewedListing, then closes it
        /// again and leaves the data behind.
        ///
        /// The window is only closed once it has actually appeared: a leader with no listing up
        /// never opens one, so the failure case costs a couple of seconds of polling and shows the
        /// player nothing at all.
        /// </summary>
        private async Task<bool> CaptureLeaderListingAsync(ulong leader, bool announce)
        {
            if (disposed || isEndingRecruitment || isCapturingListing || leader == 0)
                return false;

            isCapturingListing = true;
            try
            {
                bool opened = await framework.RunOnFrameworkThread(() =>
                {
                    unsafe
                    {
                        var agent = AgentLookingForGroup.Instance();
                        if (agent == null || openPartyFinder == null)
                            return false;

                        // A detail window that is already up belongs to the player, not to us.
                        // Opening ours over it would replace what they were reading, and closing
                        // it afterwards would shut a window they opened themselves.
                        if (ListingDetailIsOpen())
                            return false;

                        openPartyFinder(agent, leader);
                        return true;
                    }
                });

                if (!opened)
                {
                    if (announce)
                        chatGui.Print("[PF Presets] Could not open the party's listing right now.");
                    return false;
                }

                // ~2s for the window to appear. It's a server round trip, so it is not instant,
                // and it never arrives at all when there is no listing to show.
                bool shown = false;
                for (int i = 0; i < 40 && !disposed; i++)
                {
                    await Task.Delay(50);
                    shown = await framework.RunOnFrameworkThread(() => ListingDetailIsOpen());
                    if (shown)
                        break;
                }

                if (!shown)
                {
                    // Nothing opened, which is what a listing that no longer exists looks like.
                    // If this was a re-read of one we were still drawing, that is the only notice
                    // we will ever get that it ended - the stored copy never expires by itself.
                    if (capturedLeaderId == leader)
                    {
                        ForgetCapturedListing();
                        InvalidateSnapshot();
                    }

                    if (announce)
                        chatGui.Print("[PF Presets] The party doesn't have a listing up right now.");
                    pluginLog.Debug("[ListingWatch] No listing detail window appeared.");
                    return false;
                }

                // Let it populate before taking it away again.
                await Task.Delay(400);

                bool captured = await framework.RunOnFrameworkThread(() =>
                {
                    bool ok = LeaderListingLoaded(leader);
                    if (ok)
                        NoteCapturedListing(leader);
                    CloseListingDetail();
                    return ok;
                });

                if (captured)
                {
                    // The snapshot is rebuilt per frame, but it is cached for the frame it was
                    // built in - drop it so the card picks this up on the very next one.
                    InvalidateSnapshot();
                    pluginLog.Debug("[ListingWatch] Captured the party leader's listing.");
                }
                else if (announce)
                {
                    chatGui.Print("[PF Presets] Couldn't read the party's listing.");
                }

                return captured;
            }
            catch (Exception ex)
            {
                pluginLog.Error(ex, "[ListingWatch] Failed to read the party's listing.");
                return false;
            }
            finally
            {
                isCapturingListing = false;
            }
        }

        private unsafe bool ListingDetailIsOpen()
        {
            var addon = (AtkUnitBase*)(nint)gameGui.GetAddonByName("LookingForGroupDetail");
            return addon != null && addon->IsVisible;
        }

        /// <summary>Presses the detail window's Back button, the same way a player would.</summary>
        private unsafe void CloseListingDetail()
        {
            var addon = (AtkUnitBase*)(nint)gameGui.GetAddonByName("LookingForGroupDetail");
            if (addon == null || !addon->IsVisible)
                return;

            var back = addon->GetComponentButtonById(111); // "Back"
            if (back != null && back->IsEnabled)
                AtkHelpers.ClickAddonButton(addon, back);
        }
    }
}
