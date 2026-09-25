#if PFP_RATINGS
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dalamud.Game.Text;
using Dalamud.Plugin.Services;

namespace PfPresets
{
    /// <summary>
    /// Joining any listing on the data centre you are standing on, straight from the board - the
    /// same Join Party the game's own window has, for recruiters who do not run the plugin as well
    /// as those who do.
    ///
    /// THE BOARD IS NOT THE GAME. What the board holds is somebody's read of the Party Finder a
    /// minute or ten ago; the seats may have filled since. So a press never trusts it: it opens the
    /// listing, which has the game fetch it afresh, puts that fresh copy on the board so the card
    /// shows exactly what the game shows, checks it - the seats and your job, one player per job,
    /// the item level, the completion requirement, the level and unlock - and only then presses
    /// Join Party. A refusal says why in a sentence, on the card and in chat.
    ///
    /// NOT THE SAME AS APPLYING. A plugin user's coordinated listing also takes applications from
    /// other data centres (see <see cref="PfCoordination"/>): that is "Apply", and it waits for the
    /// listing to fill. This is "Join", and it is now or not at all.
    /// </summary>
    internal sealed class PfDirectJoin : IDisposable
    {
        private readonly PfAutomation automation;
        private readonly PfBoard board;
        private readonly WorldHelper worlds;
        private readonly DutyDataHelper duties;
        private readonly IFramework framework;
        private readonly IChatGui chatGui;
        private readonly IPluginLog log;
        private readonly Func<string> currentWorld;
        private readonly Func<CharacterIdentity?> identity;
        private readonly Func<string, string> displayName;

        /// <summary>How long a result stays on the card.</summary>
        private static readonly TimeSpan ResultShownFor = TimeSpan.FromSeconds(20);

        private enum Phase { Checking, Joining }

        /// <summary>The listing being joined, and how far along. One at a time.</summary>
        private (string ListingId, Phase Phase, int Party)? active;

        private readonly Dictionary<string, (string Message, bool Good, DateTime At)> results = new();

        /// <summary>The game's own error lines while a join is in flight - its reason, when it
        /// refuses one we thought was fine.</summary>
        private string? gameSaid;
        private bool listening;

        public PfDirectJoin(PfAutomation automation, PfBoard board, WorldHelper worlds, DutyDataHelper duties,
            IFramework framework, IChatGui chatGui, IPluginLog log, Func<string> currentWorld,
            Func<CharacterIdentity?> identity, Func<string, string> displayName)
        {
            this.displayName = displayName;
            this.automation = automation;
            this.board = board;
            this.worlds = worlds;
            this.duties = duties;
            this.framework = framework;
            this.chatGui = chatGui;
            this.log = log;
            this.currentWorld = currentWorld;
            this.identity = identity;
        }

        public void Dispose() => Listen(false);

        /// <summary>
        /// Whether this listing gets a Join button at all, whether it can be pressed, what it says and
        /// why. Shown only for listings on the data centre you are on - joining anywhere else is a
        /// data centre travel, which is what Apply is for.
        /// </summary>
        public (bool Shown, bool Enabled, string Label, string Tooltip) State(PfBoardListing listing)
        {
            if (!listing.OnBoard || string.IsNullOrWhiteSpace(listing.Id))
                return (false, false, "Join", string.Empty);

            string here = currentWorld();
            if (here.Length == 0 || !string.Equals(worlds.GetDataCentre(here), listing.Dc, StringComparison.OrdinalIgnoreCase))
                return (false, false, "Join", string.Empty);

            var me = identity();
            if (me != null && me.Key == new CharacterIdentity(listing.LeaderName, listing.LeaderWorld).Key)
                return (false, false, "Join", string.Empty);

            if (active is { } a)
            {
                if (a.ListingId == listing.Id)
                    return (true, false, a.Phase == Phase.Checking ? "Checking..." : "Joining...",
                        "Reading this listing from the game, then joining if a seat fits you.");
                return (true, false, "Join", "Already joining another party.");
            }

            if (automation.IsCoordinationBusy)
                return (true, false, "Join", "PF Analysis is busy with another party.");

            if (!automation.CanJoinParty(out string blocked))
                return (true, false, "Join", blocked);

            return (true, true, "Join",
                "Join now, on this data centre.\n\nChecks the listing as it stands this second first - the seats, "
                + "your job, item level and completion - and says why if it can't let you in.");
        }

        /// <summary>The last result for this listing, while it is recent enough to show.</summary>
        public (string Message, bool Good)? Result(PfBoardListing listing)
        {
            if (listing.Id == null || !results.TryGetValue(listing.Id, out var r))
                return null;
            return DateTime.UtcNow - r.At < ResultShownFor ? (r.Message, r.Good) : null;
        }

        /// <summary>
        /// Joins the listing: party <paramref name="party"/> of an alliance (0 for A), or -1 for an
        /// ordinary listing - and for an alliance, the first party with a seat that fits you.
        /// </summary>
        public void Join(PfBoardListing listing, string dutyLabel, int party = -1, int? password = null)
            => Run(listing, dutyLabel, party, readOnly: false, password);

        /// <summary>Reads the listing from the game and puts it on the board, without joining - how an
        /// alliance's parties come to show who is in each.</summary>
        public void Check(PfBoardListing listing, string dutyLabel)
            => Run(listing, dutyLabel, -1, readOnly: true);

        /// <summary>Which party of this listing is being joined right now, or null.</summary>
        public int? ActiveParty(PfBoardListing listing)
            => active is { } a && a.ListingId == listing.Id ? a.Party : null;

        private void Run(PfBoardListing listing, string dutyLabel, int party, bool readOnly, int? password = null)
        {
            if (!ulong.TryParse(listing.Id, out ulong id))
                return;
            var state = State(listing);
            if (!state.Shown || (!readOnly && !state.Enabled) || active != null)
                return;

            string listingId = listing.Id!;
            // Through the name setting: this goes on screen, in chat and on the card.
            string leader = displayName(listing.LeaderName);
            active = (listingId, Phase.Checking, party);
            results.Remove(listingId);
            gameSaid = null;
            if (!readOnly)
                Listen(true);

            _ = Task.Run(async () =>
            {
                var (outcome, reason, fresh) = await automation.DirectJoinAsync(
                    id,
                    read: f =>
                    {
                        board.ApplyFresh(listingId, f);
                        board.ShareFresh(listingId, f);
                    },
                    decide: f =>
                    {
                        var d = Decide(f, party, password != null);
                        if (d.Why == null)
                            active = (listingId, Phase.Joining, d.Party);
                        return d;
                    },
                    readOnly, password).ConfigureAwait(false);

                await framework.RunOnFrameworkThread(() =>
                {
                    Listen(false);
                    active = null;

                    if (outcome == PfAutomation.JoinOutcome.ListingMissing)
                        board.ApplyFresh(listingId, null);

                    var (message, good) = Describe(outcome, reason, dutyLabel, leader);
                    results[listingId] = (message, good, DateTime.UtcNow);
                    if (outcome != PfAutomation.JoinOutcome.Checked)
                        chatGui.Print($"[PF Analysis] {message}");
                    log.Information($"[PF Join] {(readOnly ? "Check" : "Direct join")} of {listingId}: {outcome}{(reason != null ? $" ({reason})" : string.Empty)}.");
                });
            });
        }

        /// <summary>
        /// Notices a listing the player opens in the game's own window and puts what it shows on the
        /// board - for an alliance, who is in which party. Called every frame; reads at most twice a
        /// second, and only while the window is open.
        /// </summary>
        public void Tick()
        {
            if (DateTime.UtcNow < nextViewRead || active != null)
                return;
            nextViewRead = DateTime.UtcNow.AddMilliseconds(500);

            var fresh = automation.ViewedListing();
            if (fresh == null)
                return;

            string id = fresh.Id.ToString();
            string signature = $"{fresh.Filled}|{string.Join(",", fresh.Jobs)}|{string.Join(",", fresh.Masks)}";
            if (id == lastViewedId && signature == lastViewedSignature)
                return;
            lastViewedId = id;
            lastViewedSignature = signature;

            board.ApplyFresh(id, fresh);
            board.ShareFresh(id, fresh);
        }

        private DateTime nextViewRead = DateTime.MinValue;
        private string? lastViewedId;
        private string? lastViewedSignature;

        private (string Message, bool Good) Describe(PfAutomation.JoinOutcome outcome, string? reason,
            string dutyLabel, string leader)
        {
            string who = $"{leader}'s {dutyLabel}";
            return outcome switch
            {
                PfAutomation.JoinOutcome.Joined => ($"Joined {who} party.", true),
                PfAutomation.JoinOutcome.Checked => ("Updated from the game just now.", true),
                PfAutomation.JoinOutcome.JoinUnavailable when reason != null => ($"Can't join: {reason}", false),
                PfAutomation.JoinOutcome.JoinUnavailable => ("Can't join: the Join button isn't available on this listing.", false),
                PfAutomation.JoinOutcome.ListingMissing => ("This party is no longer listed.", false),
                PfAutomation.JoinOutcome.NotNow => ("Can't join right now - you're recruiting, or in a duty, a queue or combat.", false),
                PfAutomation.JoinOutcome.InParty => ("Leave your current party to join another.", false),
                PfAutomation.JoinOutcome.Busy => ("PF Analysis is busy with another party. Try again in a moment.", false),
                PfAutomation.JoinOutcome.PasswordPrompt => ("The game didn't ask for the password. Try again.", false),
                PfAutomation.JoinOutcome.NotAccepted when gameSaid != null => ($"The game refused: {gameSaid}", false),
                PfAutomation.JoinOutcome.NotAccepted => ("You weren't added to the party. It may have just filled.", false),
                _ => ("Something went wrong joining. Try again.", false),
            };
        }

        /// <summary>
        /// Why this character cannot take a seat in the listing as it stands now, or null if it can.
        /// In the order the game itself would object, most fundamental first.
        /// </summary>
        /// <summary>
        /// Whether to join and where: a reason when this character cannot, otherwise the alliance
        /// party to press (-1 for an ordinary listing). Asked for party -1 on an alliance, it picks
        /// the first party with an open seat that fits.
        /// </summary>
        private (string? Why, int Party) Decide(PfAutomation.FreshListing f, int party, bool havePassword)
        {
            const byte Private = 2;
            if ((f.JoinConditions & Private) != 0 && !havePassword)
                return ("this party is private. Enter its password to join.", -1);

            if (f.Parties <= 1)
                return (Refuse(f, 0, f.Masks.Length), -1);

            if (party >= 0)
                return (Refuse(f, party * 8, 8, $"Alliance {(char)('A' + party)}"), party);

            string? first = null;
            for (int p = 0; p < f.Parties; p++)
            {
                string? why = Refuse(f, p * 8, 8, $"Alliance {(char)('A' + p)}");
                if (why == null)
                    return (null, p);
                first ??= why;
            }
            return (first, -1);
        }

        /// <summary>
        /// Why this character cannot take one of the seats from <paramref name="from"/> to
        /// <paramref name="from"/> + <paramref name="count"/>, or null if it can. In the order the
        /// game itself would object, most fundamental first. <paramref name="partyName"/> names an
        /// alliance party in the seat messages.
        /// </summary>
        private string? Refuse(PfAutomation.FreshListing f, int from, int count, string? partyName = null)
        {
            int end = Math.Min(f.Masks.Length, from + count);
            var (jobId, level) = automation.GetLocalJobAndLevel();
            var job = JobData.FindById(jobId);
            if (job == null)
                return "switch to a combat job first.";

            int open = 0;
            for (int i = from; i < end; i++)
                if (f.Jobs[i] == 0 && f.Masks[i] != 0)
                    open++;
            if (open == 0 || (f.Parties <= 1 && f.Total > 0 && f.Filled >= f.Total))
                return partyName != null ? $"{partyName} is full now." : "the party is full now.";

            const byte WorldOnly = 8, OnePerJob = 32;

            string listingWorld = worlds.GetWorldName(f.CurrentWorld);
            if ((f.JoinConditions & WorldOnly) != 0 && listingWorld.Length > 0
                && !string.Equals(listingWorld, currentWorld(), StringComparison.OrdinalIgnoreCase))
                return $"this party only takes players on {listingWorld}.";

            var duty = duties.GetDutyEntry(f.DutyId);
            if (duty != null)
            {
                if (duty.ClassJobLevelRequired > 0 && level > 0 && level < duty.ClassJobLevelRequired)
                    return $"{duty.Name} needs level {duty.ClassJobLevelRequired}; your {job.Abbreviation} is {level}.";
                if (!duties.IsDutyUnlocked(duty))
                    return $"you haven't unlocked {duty.Name}.";
            }

            if (f.MinItemLevel > 0)
            {
                int mine = automation.AverageItemLevel();
                if (mine > 0 && mine < f.MinItemLevel)
                    return $"the party wants item level {f.MinItemLevel}; yours is {mine}.";
            }

            const byte Complete = 2, Incomplete = 4;
            if ((f.Completion & (Complete | Incomplete)) != 0 && automation.DutyCompleted(f.DutyId) is { } done)
            {
                string name = duty?.Name ?? "this duty";
                if ((f.Completion & Complete) != 0 && !done)
                    return $"the party is for players who have completed {name}.";
                if ((f.Completion & Incomplete) != 0 && done)
                    return $"the party is for players who haven't completed {name} yet.";
            }

            bool seatFits = false;
            var openRoles = new SortedSet<string>();
            for (int i = from; i < end; i++)
            {
                if (f.Jobs[i] != 0 || f.Masks[i] == 0)
                    continue;
                if (PfAutomation.MaskTakesJob(f.Masks[i], jobId))
                    seatFits = true;
                foreach (string role in RolesIn(f.Masks[i]))
                    openRoles.Add(role);
            }
            if (!seatFits)
            {
                string takes = openRoles.Count > 0 ? $" Open seats take {string.Join(", ", openRoles)}." : string.Empty;
                return $"no open seat{(partyName != null ? $" in {partyName}" : string.Empty)} takes {job.Abbreviation}.{takes}";
            }

            // One per job counts the whole listing - an alliance's rule spans all its parties.
            if ((f.JoinConditions & OnePerJob) != 0 && f.Jobs.Contains(jobId))
                return $"it's one player per job, and there's already a {job.Abbreviation}.";

            if (!f.JoinEnabled)
                return "the game isn't offering Join Party on this listing for you.";

            return null;
        }

        private static IEnumerable<string> RolesIn(ulong mask)
        {
            var roles = new HashSet<string>();
            for (int bit = 0; bit < 64; bit++)
            {
                if ((mask & (1UL << bit)) == 0)
                    continue;
                var job = JobData.FindById(JobMasks.GetJobIdFromGameBit(bit));
                if (job == null)
                    continue;
                roles.Add(job.Category switch
                {
                    JobCategory.Tank => "tanks",
                    JobCategory.PureHealer or JobCategory.BarrierHealer => "healers",
                    _ => "DPS",
                });
            }
            return roles;
        }

        private void Listen(bool on)
        {
            if (on == listening)
                return;
            listening = on;
            if (on)
                chatGui.ChatMessage += OnChat;
            else
                chatGui.ChatMessage -= OnChat;
        }

        private void OnChat(Dalamud.Game.Chat.IHandleableChatMessage message)
        {
            if (message.LogKind == XivChatType.ErrorMessage)
                gameSaid = message.Message.TextValue.Trim();
        }
    }
}
#endif
