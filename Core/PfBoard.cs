#if PFP_RATINGS
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dalamud.Game.Gui.PartyFinder.Types;
using Dalamud.Plugin.Services;

namespace PfPresets
{
    /// <summary>
    /// The Party Finder tab's data: what goes up from this client, and the board that comes back.
    ///
    /// TWO HALVES, like <see cref="PfCrowdsource"/>, and for the same reason gated apart.
    ///
    /// UPLOADING is the crowdsourcing. The game hands the client every listing on a page of the
    /// Party Finder when the window opens or the page turns, through Dalamud's ReceiveListing. We
    /// copy what we were handed and send it on, batched. It is the game's public board and nothing
    /// else: leader, duty, comment, slots - what anybody on the data centre sees by opening the same
    /// window. Nothing about the listing is hidden or changed; <c>args.Visible</c> is never touched.
    ///
    /// READING is the tab. The server joins what every plugin user uploaded with the parties plugin
    /// users report themselves into, and answers per data centre. The tab asks for it while it is
    /// on screen, and not otherwise.
    /// </summary>
    internal sealed class PfBoard : IDisposable
    {
        /// <summary>How often pending listings are sent. A page of the finder arrives as a burst of
        /// events in one frame; ten seconds folds a session of page-turning into a few requests.</summary>
        private static readonly TimeSpan FlushEvery = TimeSpan.FromSeconds(10);

        /// <summary>An unchanged listing is not re-sent sooner than this. Seeing it again still
        /// matters - it is how the server knows the listing is still up - but not every page turn.</summary>
        private static readonly TimeSpan ResendUnchangedAfter = TimeSpan.FromMinutes(3);

        /// <summary>How often the tab re-reads the board while it is on screen.</summary>
        private static readonly TimeSpan BoardFreshFor = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan InactiveBoardFreshFor = TimeSpan.FromMinutes(15);

        /// <summary>After a failed read, how long before trying again on its own.</summary>
        private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromSeconds(20);

        private const int BatchMax = 100;
        private const int DescriptionMax = 400;

        private readonly IPartyFinderGui partyFinderGui;
        private readonly IDataManager dataManager;
        private readonly PfApiClient api;
        private readonly Configuration config;
        private readonly IPluginLog log;
        private readonly WorldHelper worlds;
        private readonly Func<string> currentWorld;
        private readonly Func<CharacterIdentity?> identity;

        private readonly object gate = new();
        private readonly Dictionary<ulong, PfBoardUpload> pending = new();
        private readonly Dictionary<ulong, (string Signature, DateTime SentAt)> sent = new();
        private DateTime lastFlush = DateTime.MinValue;

        private int receivedCount;

        /// <summary>Listings handed over by the game so far this session - how a Party Finder read
        /// knows a page has arrived. Counted whether or not anything is shared.</summary>
        public int ReceivedCount => System.Threading.Volatile.Read(ref receivedCount);
        private bool flushing;

        private Dictionary<int, uint>? jobByFlagBit;

        public PfBoard(IPartyFinderGui partyFinderGui, IDataManager dataManager, PfApiClient api,
            Configuration config, IPluginLog log, WorldHelper worlds, Func<string> currentWorld,
            Func<CharacterIdentity?> identity)
        {
            this.partyFinderGui = partyFinderGui;
            this.dataManager = dataManager;
            this.api = api;
            this.config = config;
            this.log = log;
            this.worlds = worlds;
            this.currentWorld = currentWorld;
            this.identity = identity;

            this.partyFinderGui.ReceiveListing += OnReceiveListing;
        }

        public void Dispose()
        {
            this.partyFinderGui.ReceiveListing -= OnReceiveListing;
        }

        /// <summary>Whether this install shares the listings it sees. The same switch as sharing
        /// your own party: both are "help fill in the Party Finder for everybody".</summary>
        public bool Uploading => config.PfCrowdsourceEnabled && config.RatingsEnabled;

        // ── Uploading ─────────────────────────────────────────────

        /// <summary>Every listing id handed over while a read is running, changed or not - what the
        /// sweep after it needs, which the upload's unchanged-listing filter would hide.</summary>
        private HashSet<ulong>? readSeen;

        /// <summary>Every listing a read in progress was handed, as the board would store it - what
        /// the board becomes when the read turns out complete.</summary>
        private Dictionary<ulong, PfBoardUpload>? readListings;

        /// <summary>
        /// The last complete read of a data centre: exactly the listings the game showed, and when.
        ///
        /// THE READ WINS. While it is recent, the board for that data centre is this read and nothing
        /// else - a listing it did not see is gone, whatever the server still holds (the server's own
        /// sweep leaves anything somebody saw in the last couple of minutes alone), and one it saw is
        /// shown as it saw it. After a few minutes the server's board governs again.
        /// </summary>
        private (string Dc, Dictionary<ulong, PfBoardUpload> Listings, DateTime At)? exactRead;

        private static readonly TimeSpan ExactReadHolds = TimeSpan.FromMinutes(3);

        /// <summary>How many different listings the read in progress has been handed.</summary>
        public int ReadSeenCount
        {
            get
            {
                lock (gate)
                    return readSeen?.Count ?? 0;
            }
        }

        /// <summary>A Party Finder read is starting: note every listing it is handed.</summary>
        public void BeginRead()
        {
            lock (gate)
            {
                readSeen = new HashSet<ulong>();
                readListings = new Dictionary<ulong, PfBoardUpload>();
            }
        }

        /// <summary>A read that cannot stand for the data centre - filtered, or on another tab - is
        /// dropped without a sweep.</summary>
        public void CancelRead()
        {
            lock (gate)
            {
                readSeen = null;
                readListings = null;
            }
        }

        /// <summary>
        /// A read has finished: tell the server what the data centre really has, so listings that
        /// are gone come off the board. <paramref name="total"/> is the game's own count;
        /// <paramref name="complete"/> says every page was read.
        /// </summary>
        public void EndRead(int total, bool complete)
        {
            HashSet<ulong>? seen;
            Dictionary<ulong, PfBoardUpload>? listings;
            lock (gate)
            {
                seen = readSeen;
                listings = readListings;
                readSeen = null;
                readListings = null;
            }

            // Every page read: this is the data centre, exactly. Held, and put on the board now.
            string readDc = worlds.GetDataCentre(currentWorld());
            if (complete && listings != null && readDc.Length > 0)
            {
                exactRead = (readDc, listings, DateTime.UtcNow);
                if (Board is { } shown)
                    ApplyExactRead(shown);
            }

            if (seen == null || seen.Count == 0 || !Uploading)
                return;

            string world = currentWorld();
            if (string.IsNullOrEmpty(world))
                return;

            var request = new PfBoardSweepRequest
            {
                World = world,
                Seen = seen.Select(id => id.ToString()).ToList(),
                Total = total,
                Complete = complete,
            };

            _ = Task.Run(async () =>
            {
                try
                {
                    var result = await api.SweepPfBoardAsync(request).ConfigureAwait(false);
                    if (result.IsOk && result.Value!.Removed > 0)
                        log.Information($"[PF Board] Swept {result.Value.Removed} listing(s) no longer in the Party Finder "
                            + $"({(complete ? "full read" : $"{seen.Count} of {total} read")}).");
                }
                catch (Exception ex)
                {
                    log.Debug($"[PF Board] Sweep failed: {ex.Message}");
                }
            });
        }

        private void OnReceiveListing(IPartyFinderListing listing, IPartyFinderListingEventArgs args)
        {
            System.Threading.Interlocked.Increment(ref receivedCount);
            lock (gate)
                readSeen?.Add(listing.Id);

            try
            {
                var upload = ToUpload(listing);
                if (upload == null)
                    return;

                // Kept by leader whether or not uploads are on: it is how a member whose game has
                // lost the leader's listing still knows the duty and the seats. See LeaderListing.
                lock (gate)
                {
                    if (readListings != null)
                        readListings[listing.Id] = upload;
                    seenByLeader[$"{upload.LeaderName}@{upload.LeaderWorld}".ToLowerInvariant()] = (upload, DateTime.UtcNow);
                    if (seenByLeader.Count > 2000)
                        seenByLeader.Clear();
                }

                if (!Uploading)
                    return;

                lock (gate)
                {
                    pending[listing.Id] = upload;
                }
            }
            catch (Exception ex)
            {
                log.Debug($"[PF Board] Could not read a listing: {ex.Message}");
            }
        }

        /// <summary>Listings this client has read itself, by "leader@world", with when.</summary>
        private readonly Dictionary<string, (PfBoardUpload Listing, DateTime At)> seenByLeader = new();

        /// <summary>
        /// A leader's listing, for a member whose game no longer holds it: this client's own read
        /// if it has one, otherwise the board's. Null when neither has it or it has run out.
        /// </summary>
        public LeaderListing? LeaderListing(string leaderName, uint leaderWorldId)
        {
            string world = worlds.GetWorldName(leaderWorldId);
            if (leaderName.Length == 0 || world.Length == 0)
                return null;
            string key = $"{leaderName}@{world}".ToLowerInvariant();
            var now = DateTime.UtcNow;

            (PfBoardUpload Listing, DateTime At) seen;
            bool haveSeen;
            lock (gate)
                haveSeen = seenByLeader.TryGetValue(key, out seen);

            if (haveSeen)
            {
                var left = TimeSpan.FromSeconds(seen.Listing.SecondsRemaining) - (now - seen.At);
                if (left > TimeSpan.Zero && seen.Listing.DutyType == 2)
                    return new LeaderListing((uint)seen.Listing.DutyId, seen.Listing.SlotsTotal,
                        GameMasks(seen.Listing.Slots), seen.Listing.Description, left);
            }

            var board = Board?.Listings.FirstOrDefault(l => l.OnBoard
                && string.Equals($"{l.LeaderName}@{l.LeaderWorld}", key, StringComparison.OrdinalIgnoreCase));
            if (board is { DutyType: 2, DutyId: > 0 })
            {
                var left = TimeSpan.FromSeconds(board.SecondsRemaining) - (now - FetchedAt);
                if (left > TimeSpan.Zero)
                    return new LeaderListing((uint)board.DutyId, board.SlotsTotal, GameMasks(board.Slots),
                        board.Description, left);
            }

            return null;
        }

        /// <summary>
        /// Makes a board for the data centre just read exactly what the read saw: listings it did
        /// not see removed (plugin-reported parties with no listing among them), listings it saw
        /// updated to how it saw them, listings the board did not have added. Nothing happens once
        /// the read is a few minutes old, or for any other data centre.
        /// </summary>
        private void ApplyExactRead(PfBoardResponse b)
        {
            if (exactRead is not { } read || DateTime.UtcNow - read.At > ExactReadHolds
                || !string.Equals(b.Dc, read.Dc, StringComparison.OrdinalIgnoreCase))
                return;

            var byId = b.Listings.Where(l => l.Id != null).GroupBy(l => l.Id!).ToDictionary(g => g.Key, g => g.First());
            var exact = new List<PfBoardListing>(read.Listings.Count);
            foreach (var (id, u) in read.Listings)
            {
                if (byId.TryGetValue(id.ToString(), out var l))
                {
                    // An alliance's full seats outlive a read of the list, which only carries one
                    // any-job entry a party. The count still moves; the card says the seating is
                    // as last checked when the two disagree. See the server's upsert.
                    bool keepSeats = l.Parties > 1 && u.Slots.Count < l.Slots.Count && l.Slots.Count >= 16;
                    if (!keepSeats)
                        l.Slots = u.Slots;
                    l.Description = u.Description;
                    l.SlotsFilled = u.SlotsFilled;
                    l.SlotsTotal = u.SlotsTotal;
                    l.SecondsRemaining = u.SecondsRemaining;
                    l.DutyId = u.DutyId;
                    l.DutyType = u.DutyType;
                    l.Category = u.Category;
                    l.MinIlvl = u.MinIlvl;
                    l.Objective = u.Objective;
                    l.Conditions = u.Conditions;
                    l.SearchArea = u.SearchArea | (l.SearchArea & 2);   // the lock, see ShareFresh
                    l.Parties = u.Parties;
                    exact.Add(l);
                }
                else
                {
                    exact.Add(new PfBoardListing
                    {
                        Id = u.Id,
                        Dc = read.Dc,
                        OnBoard = true,
                        LeaderName = u.LeaderName,
                        LeaderWorld = u.LeaderWorld,
                        CreatedWorld = u.CreatedWorld,
                        DutyId = u.DutyId,
                        DutyType = u.DutyType,
                        Category = u.Category,
                        Description = u.Description,
                        MinIlvl = u.MinIlvl,
                        Beginners = u.Beginners,
                        Objective = u.Objective,
                        Conditions = u.Conditions,
                        LootRules = u.LootRules,
                        SearchArea = u.SearchArea,
                        DutySettings = u.DutySettings,
                        Parties = u.Parties,
                        Slots = u.Slots,
                        SlotsFilled = u.SlotsFilled,
                        SlotsTotal = u.SlotsTotal,
                        SecondsRemaining = u.SecondsRemaining,
                    });
                }
            }

            // Swapped whole, never edited in place: the UI may be drawing the old list this frame.
            b.Listings = exact;
        }

        /// <summary>
        /// How long ago this data centre was last read in full by anybody - another player's read
        /// as the server last reported it, or this client's own - or null when not known. What
        /// keeps a hundred readers from each re-reading a data centre somebody read a moment ago.
        /// </summary>
        public TimeSpan? SinceFullRead(string dc)
        {
            TimeSpan? best = null;
            if (Board is { LastFullReadAgo: { } ago } b && string.Equals(b.Dc, dc, StringComparison.OrdinalIgnoreCase))
                best = TimeSpan.FromSeconds(ago) + (DateTime.UtcNow - FetchedAt);
            if (ExactReadAt(dc) is { } mine && (best == null || DateTime.UtcNow - mine < best))
                best = DateTime.UtcNow - mine;
            return best;
        }

        /// <summary>The data centre this character is standing on, or empty.</summary>
        public string OwnDataCentre() => worlds.GetDataCentre(currentWorld());

        /// <summary>
        /// The alliances the read in progress was handed that the board does not know in full: no
        /// full seats yet, or seats whose count the list has since moved away from. What a read opens
        /// in the listing window afterwards - see PfAutomation.ReadDetailsHiddenAsync.
        /// </summary>
        public IReadOnlyList<ulong> AlliancesNeedingDetail()
        {
            List<PfBoardUpload> alliances;
            lock (gate)
                alliances = readListings?.Values.Where(u => u.Parties > 1).ToList() ?? new List<PfBoardUpload>();

            var wanted = new List<ulong>();
            foreach (var u in alliances)
            {
                var known = Board?.Listings.FirstOrDefault(l => l.Id == u.Id);
                bool full = known != null && known.Slots.Count >= 16;
                bool current = full && known!.Slots.Count(x => x.Job > 0) == u.SlotsFilled;
                if (!current && ulong.TryParse(u.Id, out ulong id))
                    wanted.Add(id);
            }
            return wanted;
        }

        /// <summary>Listings a detail-only pass tried lately, so one that cannot be read is not
        /// opened again every few seconds.</summary>
        private readonly Dictionary<ulong, DateTime> detailTried = new();

        /// <summary>Listings this client has read in full, with how many were in them then.</summary>
        private readonly Dictionary<ulong, int> detailDone = new();

        /// <summary>
        /// The alliances on this data centre's board with no full seats yet, or seats whose count
        /// the list has moved away from - what a pass between reads opens in full. Only from a board
        /// of the data centre the character is on, since only those listings can be opened. Each is
        /// offered again at most every few minutes.
        /// </summary>
        public IReadOnlyList<ulong> AlliancesMissingDetail()
        {
            string here = OwnDataCentre();
            if (here.Length == 0)
                return Array.Empty<ulong>();

            var now = DateTime.UtcNow;
            var b = Board;
            bool boardIsHere = b != null && string.Equals(b.Dc, here, StringComparison.OrdinalIgnoreCase);

            // Two places this data centre's alliances are known from: the board, when that is the
            // board on screen, and the listings this client read itself, which are always from
            // where it stands - so the pass works whichever data centre the tab is showing.
            var candidates = new Dictionary<ulong, (int Seats, int Seated, int Filled)>();
            if (boardIsHere)
            {
                foreach (var l in b!.Listings)
                    if (l.OnBoard && l.Parties > 1 && ulong.TryParse(l.Id, out ulong id))
                        candidates[id] = (l.Slots.Count, l.Slots.Count(x => x.Job > 0), l.SlotsFilled);
            }
            lock (gate)
            {
                foreach (var (u, at) in seenByLeader.Values)
                {
                    if (u.Parties <= 1 || !ulong.TryParse(u.Id, out ulong id) || candidates.ContainsKey(id))
                        continue;
                    if (TimeSpan.FromSeconds(u.SecondsRemaining) - (now - at) <= TimeSpan.Zero)
                        continue;
                    if (!string.Equals(worlds.GetDataCentre(u.CreatedWorld), here, StringComparison.OrdinalIgnoreCase))
                        continue;
                    candidates[id] = (u.Slots.Count, u.Slots.Count(x => x.Job > 0), u.SlotsFilled);
                }
            }

            var wanted = new List<ulong>();
            foreach (var (id, c) in candidates)
            {
                if (c.Seats >= 16 && c.Seated == c.Filled)
                    continue;
                // Read in full by this client already, and nobody has joined or left since.
                if (detailDone.TryGetValue(id, out int filledThen) && filledThen == c.Filled)
                    continue;
                if (detailTried.TryGetValue(id, out var at) && now - at < TimeSpan.FromMinutes(3))
                    continue;
                wanted.Add(id);
            }
            foreach (ulong id in wanted)
                detailTried[id] = now;
            if (detailTried.Count > 500)
                detailTried.Clear();
            return wanted;
        }

        /// <summary>A listing read in full during a read of the list: onto the board, and shared.</summary>
        public void TakeDetail(PfAutomation.FreshListing fresh)
        {
            detailDone[fresh.Id] = fresh.Filled;
            if (detailDone.Count > 500)
                detailDone.Clear();
            string id = fresh.Id.ToString();
            ApplyFresh(id, fresh);
            ShareFresh(id, fresh);
        }

        /// <summary>When the last complete read of this data centre was, or null.</summary>
        public DateTime? ExactReadAt(string? dc)
            => exactRead is { } r && string.Equals(r.Dc, dc, StringComparison.OrdinalIgnoreCase) ? r.At : null;

        /// <summary>
        /// Brings one listing on the board up to date from a fresh read of it in the game, so what the
        /// card shows is what the game shows - the seats and who is in them, the count, the comment,
        /// the time left. An alliance comes back with all its parties' seats, eight to a party, where
        /// the board's own copy has one entry per party. Null removes it: the listing is gone.
        /// </summary>
        public void ApplyFresh(string listingId, PfAutomation.FreshListing? fresh)
        {
            var listings = Board?.Listings;
            if (listings == null)
                return;

            int at = listings.FindIndex(l => l.Id == listingId);
            if (at < 0)
                return;

            if (fresh == null)
            {
                listings.RemoveAt(at);
                return;
            }

            var l = listings[at];
            var slots = new List<PfBoardSlot>(fresh.Masks.Length);
            for (int i = 0; i < fresh.Masks.Length; i++)
            {
                slots.Add(new PfBoardSlot
                {
                    Accepting = JobFlagsFromGameMask(fresh.Masks[i]).ToString(),
                    Job = (int)fresh.Jobs[i],
                });
            }

            l.Slots = slots;
            l.SlotsFilled = fresh.Filled;
            l.SlotsTotal = fresh.Total;
            l.Parties = fresh.Parties;
            l.Description = fresh.Comment;
            l.SearchArea |= fresh.JoinConditions & 2;
            l.SecondsRemaining = (int)Math.Max(1, fresh.TimeLeft.TotalSeconds);
            l.CheckedAt = DateTime.UtcNow;
        }

        /// <summary>
        /// Sends a fresh read of a listing to the server with the next upload, so everybody's board
        /// shows it - above all an alliance's seats, which only a full read of the listing carries.
        /// Only while sharing is on, like every other upload. Built from this client's own read of
        /// the listing when it has one, otherwise from the board's copy, with the fresh seats on top.
        /// </summary>
        public void ShareFresh(string listingId, PfAutomation.FreshListing fresh)
        {
            if (!Uploading)
                return;

            var onBoard = Board?.Listings.FirstOrDefault(l => l.Id == listingId);
            PfBoardUpload? seen = null;
            lock (gate)
                seen = seenByLeader.Values.Select(v => v.Listing).FirstOrDefault(u => u.Id == listingId);

            // A listing nobody has uploaded yet - a member sharing the one they joined through - is
            // described by the game's own copy: the recruiter, their home world, the world it is on.
            string createdWorld = seen?.CreatedWorld ?? onBoard?.CreatedWorld ?? worlds.GetWorldName(fresh.CurrentWorld);
            string leaderName = seen?.LeaderName ?? onBoard?.LeaderName ?? fresh.LeaderName;
            string leaderWorld = seen?.LeaderWorld ?? onBoard?.LeaderWorld
                ?? (fresh.HomeWorld != 0 ? worlds.GetWorldName(fresh.HomeWorld) : string.Empty);
            if (leaderName.Length == 0 || leaderWorld.Length == 0 || createdWorld.Length == 0)
                return;

            var slots = new List<PfBoardSlot>(fresh.Masks.Length);
            for (int i = 0; i < fresh.Masks.Length; i++)
                slots.Add(new PfBoardSlot { Accepting = JobFlagsFromGameMask(fresh.Masks[i]).ToString(), Job = (int)fresh.Jobs[i] });

            var upload = new PfBoardUpload
            {
                Id = listingId,
                LeaderName = leaderName,
                LeaderWorld = leaderWorld,
                CreatedWorld = createdWorld,
                DutyId = (int)fresh.DutyId,
                // A roulette's "duty" is the roulette; everything else is a duty by its own id.
                DutyType = seen?.DutyType ?? onBoard?.DutyType ?? (fresh.Category == 2 ? 1 : 2),
                Category = seen?.Category ?? onBoard?.Category ?? (int)fresh.Category,
                Description = fresh.Comment.Length > DescriptionMax ? fresh.Comment[..DescriptionMax] : fresh.Comment,
                MinIlvl = fresh.MinItemLevel,
                Beginners = seen?.Beginners ?? onBoard?.Beginners ?? fresh.Beginners,
                // The game's flags and Dalamud's are the same bits, so the fresh copy fills in
                // whatever the board does not have yet.
                Objective = seen?.Objective ?? onBoard?.Objective ?? fresh.Objective,
                Conditions = seen?.Conditions ?? onBoard?.Conditions ?? fresh.Completion,
                LootRules = seen?.LootRules ?? onBoard?.LootRules ?? fresh.LootRule,
                // Plus the private flag, which only a read of the listing itself carries - the list
                // never says it, so this is how a private listing gets its lock for everybody.
                SearchArea = (seen?.SearchArea ?? onBoard?.SearchArea ?? fresh.JoinConditions) | (fresh.JoinConditions & 2),
                DutySettings = seen?.DutySettings ?? onBoard?.DutySettings ?? fresh.DutySettings,
                Parties = fresh.Parties,
                Slots = slots,
                SlotsFilled = fresh.Filled,
                SlotsTotal = Math.Max(1, fresh.Total),
                SecondsRemaining = (int)Math.Clamp(fresh.TimeLeft.TotalSeconds, 1, 3600),
            };

            lock (gate)
                pending[fresh.Id] = upload;
        }

        /// <summary>A listing's seats as the game's own job masks, eight of them: the board keeps
        /// Dalamud's JobFlags, and the seat arithmetic reads the game's bit order.</summary>
        private ulong[] GameMasks(List<PfBoardSlot> slots)
        {
            var masks = new ulong[8];
            for (int i = 0; i < slots.Count && i < 8; i++)
            {
                foreach (uint job in JobsAccepted(slots[i].Accepting))
                {
                    int bit = JobMasks.GetGameJobBitIndex(job);
                    if (bit >= 0)
                        masks[i] |= 1UL << bit;
                }
            }
            return masks;
        }

        private PfBoardUpload? ToUpload(IPartyFinderListing listing)
        {
            if (listing.Id == 0)
                return null;

            string leader = listing.Name.TextValue.Trim();
            string leaderWorld = worlds.GetWorldName(listing.HomeWorld.RowId);
            string createdWorld = worlds.GetWorldName(listing.World.RowId);

            if (leader.Length == 0 || leaderWorld.Length == 0 || createdWorld.Length == 0)
                return null;

            // A listing lives an hour at most; the server refuses anything else, so do not send it.
            if (listing.SecondsRemaining <= 0 || listing.SecondsRemaining > 3600)
                return null;

            var jobs = listing.RawJobsPresent.ToArray();
            var slots = new List<PfBoardSlot>(listing.Slots.Count);

            int i = 0;
            foreach (var slot in listing.Slots)
            {
                // Dalamud hands the seat's jobs over as individual flags; folded back into the one
                // mask the game sent, which is the compact thing to store.
                ulong mask = 0;
                foreach (var flag in slot.Accepting)
                    mask |= (ulong)flag;

                slots.Add(new PfBoardSlot
                {
                    Accepting = mask.ToString(),
                    Job = i < jobs.Length ? jobs[i] : 0,
                });
                i++;
            }

            // Decoded the way every other comment in the plugin is, so an auto-translate phrase
            // arrives as one bracket glyph each side and the game's symbols come through as they are.
            string description = CommentText.Decode(listing.Description.Encode()).Trim();
            if (description.Length > DescriptionMax)
                description = description[..DescriptionMax];

            return new PfBoardUpload
            {
                Id = listing.Id.ToString(),
                LeaderName = leader,
                LeaderWorld = leaderWorld,
                CreatedWorld = createdWorld,
                DutyId = listing.RawDuty,
                DutyType = (int)listing.DutyType,
                Category = (int)listing.Category,
                Description = description,
                MinIlvl = listing.MinimumItemLevel,
                Beginners = listing.BeginnersWelcome,
                Objective = (int)listing.Objective,
                Conditions = (int)listing.Conditions,
                LootRules = (int)listing.LootRules,
                SearchArea = (int)listing.SearchArea,
                DutySettings = (int)listing.DutyFinderSettings,
                Parties = listing.Parties,
                Slots = slots,
                SlotsFilled = listing.SlotsFilled,
                SlotsTotal = listing.SlotsAvailable,
                SecondsRemaining = listing.SecondsRemaining,
            };
        }

        /// <summary>Called every frame; sends at most once every ten seconds.</summary>
        public void Tick()
        {
            TickWatches();

            // A NEW DATA CENTRE UNDERFOOT FORGETS THE OLD PICK. Switching to a character elsewhere -
            // or travelling - left the board on the data centre picked before, so an EU character
            // opened the tab on NA. The pick is for looking around from where you are; when where
            // you are changes, the board comes back to it.
            string here = OwnDataCentre();
            if (here.Length > 0 && !string.Equals(here, lastOwnDc, StringComparison.OrdinalIgnoreCase))
            {
                bool moved = lastOwnDc != null;
                lastOwnDc = here;
                if (moved && ChosenDc != null)
                {
                    ChosenDc = null;
                    Refresh();
                }
            }

            // No re-sending of cached listings: a cached listing's clock is from when it was seen,
            // and sending it again as if fresh kept listings on the board past their real end. The
            // board is kept current by actually reading the Party Finder - see PfBoardFetch.
            var now = DateTime.UtcNow;
            if (now - lastFlush < FlushEvery)
                return;

            lastFlush = now;

            List<PfBoardUpload> batch;
            lock (gate)
            {
                if (flushing || pending.Count == 0)
                    return;

                batch = new List<PfBoardUpload>(Math.Min(pending.Count, BatchMax));

                foreach (var (id, upload) in pending.ToList())
                {
                    if (batch.Count >= BatchMax)
                        break;

                    pending.Remove(id);

                    // SecondsRemaining ticks down every time the same listing is seen, so it is
                    // left out of "has this changed" - otherwise nothing would ever be unchanged.
                    string signature = Signature(upload);
                    if (sent.TryGetValue(id, out var last)
                        && last.Signature == signature
                        && now - last.SentAt < ResendUnchangedAfter)
                        continue;

                    sent[id] = (signature, now);
                    batch.Add(upload);
                }

                // Forget listings long gone, so this does not grow across a session.
                if (sent.Count > 2000)
                {
                    foreach (var stale in sent.Where(kv => now - kv.Value.SentAt > TimeSpan.FromHours(1))
                                 .Select(kv => kv.Key).ToList())
                        sent.Remove(stale);
                }

                if (batch.Count == 0 || !Uploading)
                    return;

                flushing = true;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    var result = await api.ReportPfBoardAsync(new PfBoardUploadRequest { Listings = batch })
                        .ConfigureAwait(false);

                    if (!result.IsOk)
                    {
                        log.Debug($"[PF Board] Upload of {batch.Count} refused: {result.Status}");

                        // Let them go out again next time rather than being remembered as sent.
                        lock (gate)
                        {
                            foreach (var u in batch)
                                if (ulong.TryParse(u.Id, out ulong id))
                                    sent.Remove(id);
                        }
                    }
                }
                catch (Exception ex)
                {
                    log.Debug($"[PF Board] Upload failed: {ex.Message}");
                }
                finally
                {
                    lock (gate)
                        flushing = false;
                }
            });
        }

        private static string Signature(PfBoardUpload u)
            => string.Join("|", u.LeaderName, u.DutyId, u.Description, u.MinIlvl, u.SlotsFilled,
                u.Objective, u.Conditions, u.LootRules,
                string.Join(",", u.Slots.Select(s => $"{s.Accepting}:{s.Job}")));

        // ── Reading ───────────────────────────────────────────────

        /// <summary>The last board read. Replaced whole, never edited, so the draw thread can hold
        /// a reference to it without a lock.</summary>
        public PfBoardResponse? Board { get; private set; }

        public DateTime FetchedAt { get; private set; } = DateTime.MinValue;
        public bool Loading { get; private set; }
        public ApiStatus? LastError { get; private set; }

        /// <summary>The data centre the viewer picked, or null for "wherever I am standing".</summary>
        public string? ChosenDc { get; private set; }

        /// <summary>The data centre the character was standing on last tick.</summary>
        private string? lastOwnDc;

        private DateTime lastAttempt = DateTime.MinValue;

        public void ChooseDc(string? dc)
        {
            if (string.Equals(dc, ChosenDc, StringComparison.OrdinalIgnoreCase))
                return;

            ChosenDc = dc;
            Refresh();
        }

        /// <summary>Called by the tab every frame it is drawn; reads at most every thirty seconds.</summary>
        public void EnsureFresh(bool inactive = false)
        {
            if (Loading)
                return;

            var now = DateTime.UtcNow;
            if (LastError != null && now - lastAttempt < RetryAfterFailure)
                return;

            var freshFor = inactive ? InactiveBoardFreshFor : BoardFreshFor;
            if (now - FetchedAt < freshFor)
                return;

            Fetch();
        }

        /// <summary>Re-reads now, whatever the clock says.</summary>
        public void Refresh()
        {
            FetchedAt = DateTime.MinValue;
            LastError = null;
            if (!Loading)
                Fetch();
        }

        private void Fetch()
        {
            string world = currentWorld();
            string? dc = ChosenDc;
            string? home = identity()?.World;

            if (dc == null && world.Length == 0)
                return;

            Loading = true;
            lastAttempt = DateTime.UtcNow;

            _ = Task.Run(async () =>
            {
                try
                {
                    var result = await api.GetPfBoardAsync(new PfBoardRequest
                    {
                        Dc = dc,
                        World = world.Length > 0 ? world : null,
                        HomeWorld = home,
                    }).ConfigureAwait(false);

                    if (result.IsOk)
                    {
                        ApplyExactRead(result.Value!);
                        Board = result.Value;
                        FetchedAt = DateTime.UtcNow;
                        LastError = null;
                    }
                    else
                    {
                        LastError = result.Status;
                    }
                }
                catch (Exception ex)
                {
                    log.Debug($"[PF Board] Read failed: {ex.Message}");
                    LastError = ApiStatus.ServerError;
                }
                finally
                {
                    Loading = false;
                }
            });
        }

        // ── Watching ──────────────────────────────────────────────

        /// <summary>A listing popped out into its own window. Followed by its leader, so a
        /// re-posted listing is still the same watch; ended when the leader has none up.</summary>
        internal sealed class WatchedParty
        {
            public string LeaderName { get; init; } = string.Empty;
            public string LeaderWorld { get; init; } = string.Empty;
            public string Key => $"{LeaderName}@{LeaderWorld}".ToLowerInvariant();
            public PfBoardListing? Listing { get; set; }
            public bool Ended { get; set; }
            public bool Locked { get; set; }
            public string DutyLabel { get; set; } = string.Empty;
        }

        private static readonly TimeSpan WatchEvery = TimeSpan.FromSeconds(15);
        private readonly List<WatchedParty> watched = new();
        private DateTime nextWatchRead = DateTime.MinValue;
        private bool watchLoading;

        /// <summary>Replaced whole on every change, so the draw thread can hold it.</summary>
        public IReadOnlyList<WatchedParty> Watched { get; private set; } = Array.Empty<WatchedParty>();

        public bool IsWatching(PfBoardListing listing)
        {
            string key = $"{listing.LeaderName}@{listing.LeaderWorld}".ToLowerInvariant();
            return Watched.Any(w => w.Key == key);
        }

        public void Watch(PfBoardListing listing, string dutyLabel)
        {
            if (IsWatching(listing) || watched.Count >= 20)
                return;
            watched.Add(new WatchedParty
            {
                LeaderName = listing.LeaderName,
                LeaderWorld = listing.LeaderWorld,
                Listing = listing,
                DutyLabel = dutyLabel,
            });
            Watched = watched.ToList();
            nextWatchRead = DateTime.MinValue;
        }

        public void Unwatch(string key)
        {
            watched.RemoveAll(w => w.Key == key);
            Watched = watched.ToList();
        }

        /// <summary>Every 15 seconds while anything is watched: the watched listings, wherever
        /// they are, whatever board is on screen.</summary>
        private void TickWatches()
        {
            var now = DateTime.UtcNow;
            if (watched.Count == 0 || watchLoading || now < nextWatchRead)
                return;

            nextWatchRead = now + WatchEvery;
            watchLoading = true;
            var request = new PfBoardWatchRequest
            {
                Leaders = watched.Select(w => new PfBoardMember { Name = w.LeaderName, World = w.LeaderWorld }).ToList(),
            };
            var asked = watched.ToList();

            _ = Task.Run(async () =>
            {
                try
                {
                    var result = await api.WatchPfBoardAsync(request).ConfigureAwait(false);
                    if (!result.IsOk)
                        return;

                    var byLeader = result.Value!.Listings.ToDictionary(
                        l => $"{l.LeaderName}@{l.LeaderWorld}".ToLowerInvariant(), l => l);
                    foreach (var w in asked)
                    {
                        if (byLeader.TryGetValue(w.Key, out var listing))
                        {
                            w.Listing = listing;
                            w.Ended = false;
                        }
                        else
                        {
                            w.Ended = true;
                        }
                    }
                }
                catch (Exception ex)
                {
                    log.Debug($"[PF Board] Watch read failed: {ex.Message}");
                }
                finally
                {
                    watchLoading = false;
                }
            });
        }

        // ── Slots ─────────────────────────────────────────────────

        /// <summary>
        /// The ClassJob ids a slot's mask takes, decoded with Dalamud's own JobFlags table.
        ///
        /// Not <see cref="JobData"/>'s BitIndex: that is the bitfield the recruitment window
        /// writes, and the listing packet orders its bits differently. Whoever uploaded the mask
        /// encoded it as JobFlags, so it is read back the same way.
        /// </summary>
        public IEnumerable<uint> JobsAccepted(string accepting)
        {
            if (!ulong.TryParse(accepting, out ulong mask) || mask == 0)
                yield break;

            var map = JobByFlagBit();
            for (int bit = 0; bit < 64; bit++)
            {
                if ((mask & (1UL << bit)) != 0 && map.TryGetValue(bit, out uint job))
                    yield return job;
            }
        }

        /// <summary>
        /// A recruitment slot's game mask (the bitfield the recruitment window writes) as the JobFlags
        /// mask the board stores, so a listing the recruiter reports reads exactly like one a
        /// browser uploaded. Also says whether a job fits the seat.
        /// </summary>
        public ulong JobFlagsFromGameMask(ulong gameMask)
        {
            if (gameMask == 0)
                return 0;

            var flagBitByJob = JobByFlagBit().ToDictionary(kv => kv.Value, kv => kv.Key);
            ulong flags = 0;
            for (int bit = 0; bit < 64; bit++)
            {
                if ((gameMask & (1UL << bit)) == 0)
                    continue;
                uint job = JobMasks.GetJobIdFromGameBit(bit);
                if (job != 0 && flagBitByJob.TryGetValue(job, out int flagBit))
                    flags |= 1UL << flagBit;
            }
            return flags;
        }

        private Dictionary<int, uint> JobByFlagBit()
        {
            if (jobByFlagBit != null)
                return jobByFlagBit;

            var map = new Dictionary<int, uint>();
            foreach (JobFlags flag in Enum.GetValues(typeof(JobFlags)))
            {
                ulong value = (ulong)flag;
                if (value == 0 || (value & (value - 1)) != 0)
                    continue;

                try
                {
                    if (flag.ClassJob(dataManager) is { } cj)
                        map[System.Numerics.BitOperations.Log2(value)] = cj.RowId;
                }
                catch
                {
                    // A flag the sheet has no row for; it simply decodes to nothing.
                }
            }

            jobByFlagBit = map;
            return map;
        }
    }
}
#endif
