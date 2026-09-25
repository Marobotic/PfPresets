using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace PfPresets
{
    /// <summary>
    /// Reading the Party Finder on purpose, for the board.
    ///
    /// The board only knows a listing when somebody's Party Finder window has been handed it, and
    /// only knows its clock from that moment. A data centre nobody is browsing empties out, and a
    /// listing its owner refreshed still disappears at its old time. So, on a schedule (see
    /// <see cref="PfBoardFetch"/>), this opens the Party Finder list itself, lets the game download
    /// every page, and closes it again - everything received flows to the board through Dalamud's
    /// ReceiveListing exactly as if the player had looked.
    ///
    /// PAGES ARE TURNED BY CONTENT, NOT BY NODE ID. The list shows fifty listings a page. Under it,
    /// the window says which ones are on screen ("1-50") and how many there are ("50(101)"), and
    /// has two unlabelled arrows on the range's row: back, then forward. The range and total are
    /// found by their shape, "forward" as the rightmost unlabelled button on the range's row. None
    /// of that found: the first page is all this reads, and it says so in the log rather than
    /// clicking something it could not identify.
    /// </summary>
    public partial class PfAutomation
    {
        private volatile bool isFetchingBoard;

        /// <summary>
        /// Set while a read has the Party Finder open: the window is kept out of sight from its
        /// first frame, so a read happens without the player seeing it.
        ///
        /// NOT HIDDEN - TRANSPARENT AND OFF-SCREEN. A hidden window is never set up: its buttons
        /// stay disabled and its list and pager are never filled, so there are no pages to turn.
        /// So it stays "shown", fully transparent and moved off the screen, and before it closes
        /// its real position and opacity are put back - the game remembers where a window was
        /// when it closed, and must remember the player's place, not ours. Only a window the read
        /// itself opened is ever touched.
        /// </summary>
        private volatile bool hideBoardWindow;
        private short savedBoardX, savedBoardY;
        private bool boardWindowMoved;

        /// <summary>True while a read has the Party Finder open out of sight; anything that
        /// decorates that window stands down.</summary>
        public bool IsReadingBoard => hideBoardWindow;

        private Dalamud.Plugin.Services.IAddonLifecycle? addonLifecycle;

        /// <summary>Hooks the Party Finder list's setup and draw, to keep a read's window hidden.</summary>
        public void AttachAddonLifecycle(Dalamud.Plugin.Services.IAddonLifecycle lifecycle)
        {
            addonLifecycle = lifecycle;
            lifecycle.RegisterListener(Dalamud.Game.Addon.Lifecycle.AddonEvent.PostSetup, "LookingForGroup", HideIfReading);
            lifecycle.RegisterListener(Dalamud.Game.Addon.Lifecycle.AddonEvent.PreDraw, "LookingForGroup", HideIfReading);
            lifecycle.RegisterListener(Dalamud.Game.Addon.Lifecycle.AddonEvent.PostSetup, "LookingForGroupDetail", HideDetailIfReading);
            lifecycle.RegisterListener(Dalamud.Game.Addon.Lifecycle.AddonEvent.PreDraw, "LookingForGroupDetail", HideDetailIfReading);
        }

        public void DetachAddonLifecycle()
        {
            addonLifecycle?.UnregisterListener(HideIfReading);
            addonLifecycle?.UnregisterListener(HideDetailIfReading);
            addonLifecycle = null;
        }

        private unsafe void HideIfReading(Dalamud.Game.Addon.Lifecycle.AddonEvent type,
            Dalamud.Game.Addon.Lifecycle.AddonArgTypes.AddonArgs args)
        {
            if (!hideBoardWindow)
                return;
            var addon = (AtkUnitBase*)args.Addon.Address;
            if (addon == null)
                return;

            if (!boardWindowMoved)
            {
                savedBoardX = addon->X;
                savedBoardY = addon->Y;
                boardWindowMoved = true;
            }

            // Every frame: the opening animation sets both back as it plays.
            addon->SetAlpha(0);
            if (addon->X != -30000 || addon->Y != -30000)
                addon->SetPosition(-30000, -30000);
        }

        // ── An alliance read in full, out of sight ────────────────
        //
        // THE LIST ONLY SAYS "ANY JOB" FOR AN ALLIANCE. It carries one entry per party, and nothing
        // about the eight seats inside each - which jobs they take, who is in them. That lives only
        // in the listing's own window, so after the pages are read, each alliance the board does
        // not know in full (or whose count has moved since) is opened there, read, and shared.
        // The window is kept out of sight the same way the list is, and put back where the player
        // had it before it closes, so that is what the game saves.

        private volatile bool hideDetailWindow;
        private bool detailWindowMoved;
        private short savedDetailX, savedDetailY;

        /// <summary>At most this many alliances are read in full per read of the list.</summary>
        private const int MaxDetailReads = 6;

        private unsafe void HideDetailIfReading(Dalamud.Game.Addon.Lifecycle.AddonEvent type,
            Dalamud.Game.Addon.Lifecycle.AddonArgTypes.AddonArgs args)
        {
            if (!hideDetailWindow)
                return;
            var addon = (AtkUnitBase*)args.Addon.Address;
            if (addon == null)
                return;
            if (!detailWindowMoved)
            {
                savedDetailX = addon->X;
                savedDetailY = addon->Y;
                detailWindowMoved = true;
            }
            addon->SetAlpha(0);
            if (addon->X != -30000 || addon->Y != -30000)
                addon->SetPosition(-30000, -30000);
        }

        /// <summary>
        /// Opens each listing in turn in the hidden detail window, reads it in full, and hands it to
        /// <paramref name="onDetail"/> on the framework thread. Stops early if the player comes back.
        /// Returns how many were read.
        /// </summary>
        private async Task<int> ReadDetailsHiddenAsync(IReadOnlyList<ulong> ids, Action<FreshListing> onDetail)
        {
            if (ids.Count == 0)
                return 0;

            int read = 0;
            hideDetailWindow = true;
            try
            {
                foreach (ulong id in ids.Take(MaxDetailReads))
                {
                    if (disposed || fetchAbort?.Invoke() == true)
                        break;

                    bool opened = await framework.RunOnFrameworkThread(() =>
                    {
                        unsafe
                        {
                            var agent = AgentLookingForGroup.Instance();
                            return agent != null && agent->OpenListing(id);
                        }
                    });
                    if (!opened)
                        continue;

                    FreshListing? fresh = null;
                    for (int i = 0; i < 30 && fresh == null && !disposed; i++)
                    {
                        await Task.Delay(100);
                        fresh = await framework.RunOnFrameworkThread(() =>
                            ListingDetailIsOpen() ? ReadFreshListing(id) : null);
                    }
                    if (fresh == null)
                        continue;

                    await framework.RunOnFrameworkThread(() => onDetail(fresh));
                    read++;
                }
            }
            catch (Exception ex)
            {
                pluginLog.Warning(ex, "[PF Board] Reading an alliance in full failed.");
            }
            finally
            {
                // Put back where the player had it, then shut, so the game saves the real position.
                await framework.RunOnFrameworkThread(() =>
                {
                    unsafe
                    {
                        var detail = (AtkUnitBase*)(nint)gameGui.GetAddonByName("LookingForGroupDetail");
                        hideDetailWindow = false;
                        if (detail != null)
                        {
                            if (detailWindowMoved)
                            {
                                detail->SetPosition(savedDetailX, savedDetailY);
                                detail->SetAlpha(255);
                            }
                            detail->Close(true);
                        }
                        detailWindowMoved = false;
                    }
                });
            }
            return read;
        }

        /// <summary>
        /// Reads listings in full on their own, without reading the list - the alliances the board
        /// is missing seats for, between reads. Refused (0) whenever a read of the list could not
        /// run either. Stops the moment <paramref name="playerBack"/> says the player is back.
        /// </summary>
        public async Task<int> ReadListingsInFullAsync(IReadOnlyList<ulong> ids, Action<FreshListing> onDetail,
            Func<bool>? playerBack = null)
        {
            if (ids.Count == 0 || disposed)
                return 0;
            // Opening a listing can bring the list up with it; if the player did not have it open,
            // it is kept out of sight like a read's, and shut again afterwards.
            bool listWasOpen = false;
            bool go = await framework.RunOnFrameworkThread(() =>
            {
                if (!CanFetchBoardNow(out _))
                    return false;
                unsafe { listWasOpen = GetBoardAddon() != null; }
                isFetchingBoard = true;
                hideBoardWindow = !listWasOpen;
                return true;
            });
            if (!go)
                return 0;

            fetchAbort = playerBack;
            try
            {
                return await ReadDetailsHiddenAsync(ids, onDetail);
            }
            finally
            {
                fetchAbort = null;
                if (!listWasOpen)
                {
                    await framework.RunOnFrameworkThread(() =>
                    {
                        unsafe
                        {
                            RestoreBoardWindow(GetBoardAddon());
                            var agent = AgentLookingForGroup.Instance();
                            if (agent != null && GetBoardAddon() != null)
                                agent->Hide();
                        }
                    });
                }
                hideBoardWindow = false;
                isFetchingBoard = false;
            }
        }

        /// <summary>Puts a read's window back where the player had it, visible, so that is what
        /// the game saves when it closes.</summary>
        private unsafe void RestoreBoardWindow(AtkUnitBase* addon)
        {
            hideBoardWindow = false;
            if (addon == null || !boardWindowMoved)
                return;

            addon->SetPosition(savedBoardX, savedBoardY);
            addon->SetAlpha(255);
            boardWindowMoved = false;
        }

        /// <summary>The Party Finder list whether or not it is drawn - a read's own window is
        /// open but hidden.</summary>
        private unsafe AtkUnitBase* GetBoardAddon()
        {
            var addon = (AtkUnitBase*)(nint)gameGui.GetAddonByName("LookingForGroup");
            return addon != null && addon->IsReady ? addon : null;
        }

        /// <summary>At most this many pages in one read. A data centre's busiest hour is well
        /// under this; the cap is so a counter misread can never page forever.</summary>
        private const int MaxFetchPages = 20;

        /// <summary>
        /// A read never keeps the Party Finder open longer than this, counted from the moment it
        /// opens and checked at every wait - whatever it has read by then is what it sends.
        ///
        /// LONG ENOUGH FOR EVERY PAGE, BECAUSE IT NEVER HOLDS THE WINDOW FROM A PLAYER WHO IS
        /// THERE. A read only starts after a quiet spell with no input, and stops the instant the
        /// player touches anything - see <see cref="fetchAbort"/>. It was capped at five seconds to
        /// stop it blocking the Party Finder, which meant it only ever read the first page: the
        /// game ignores page turns that come too fast, so every page past the first costs a few
        /// seconds, and pages two and three never reached the board - 69 listings for 101.
        /// </summary>
        private static readonly TimeSpan MaxFetchTime = TimeSpan.FromSeconds(45);

        /// <summary>True once the read should stop early - the player is back.</summary>
        private Func<bool>? fetchAbort;

        /// <summary>How many different listings the read has been handed so far.</summary>
        private Func<int>? fetchSeen;

        /// <summary>
        /// The read has every listing the game says there are: it stops there, whatever page it is
        /// on or however long it has left. The window is open for as long as that takes and not a
        /// moment more.
        /// </summary>
        private bool FetchHasEverything
            => LastReadTotal > 0 && fetchSeen != null && fetchSeen() >= LastReadTotal;

        /// <summary>Why the last read ended early, if it did.</summary>
        private bool fetchAborted;

        /// <summary>When the read in progress has to have closed the window by.</summary>
        private DateTime fetchDeadline = DateTime.MaxValue;

        private bool PastFetchDeadline
        {
            get
            {
                if (DateTime.UtcNow >= fetchDeadline)
                    return true;
                if (FetchHasEverything)
                {
                    LastReadComplete = true;
                    return true;
                }
                if (fetchAbort?.Invoke() == true)
                {
                    fetchAborted = true;
                    return true;
                }
                return false;
            }
        }

        /// <summary>A wait that never runs past the deadline. False when the deadline is what
        /// ended it.</summary>
        private async Task<bool> DelayWithinFetch(int ms)
        {
            // In short steps, so the player coming back is noticed within a quarter second rather
            // than at the end of a three-second wait for a page to turn.
            var until = DateTime.UtcNow + TimeSpan.FromMilliseconds(ms);
            while (DateTime.UtcNow < until)
            {
                if (PastFetchDeadline)
                    return false;
                var step = until - DateTime.UtcNow;
                await Task.Delay(step < TimeSpan.FromMilliseconds(100) ? step : TimeSpan.FromMilliseconds(100));
            }
            return !PastFetchDeadline;
        }

        /// <summary>"1-50": the listings on screen.</summary>
        private static readonly Regex PageRange = new(@"^\s*(\d+)\s*-\s*(\d+)\s*$", RegexOptions.Compiled);

        /// <summary>"50(101)": shown, and how many there are in all.</summary>
        private static readonly Regex PageTotal = new(@"^\s*\d+\s*\((\d+)\)\s*$", RegexOptions.Compiled);

        /// <summary>
        /// Whether now is a moment to open the Party Finder behind the player's back: not in an
        /// instance, not in combat, not crafting, gathering, in a cutscene or changing zone, not
        /// while they have the Party Finder open themselves, and not while any other Party Finder
        /// automation is running. Framework thread.
        /// </summary>
        public bool CanFetchBoardNow(out string why)
        {
            why = !clientState.IsLoggedIn ? "not logged in"
                : IsInDuty() ? "in an instance"
                : condition[ConditionFlag.InCombat] ? "in combat"
                : condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51] ? "changing zone"
                : condition[ConditionFlag.OccupiedInCutSceneEvent] || condition[ConditionFlag.WatchingCutscene] ? "in a cutscene"
                : condition[ConditionFlag.Crafting] || condition[ConditionFlag.Gathering] ? "crafting or gathering"
                : condition[ConditionFlag.OccupiedInQuestEvent] || condition[ConditionFlag.OccupiedInEvent]
                    || condition[ConditionFlag.Occupied] || condition[ConditionFlag.OccupiedSummoningBell] ? "busy"
                : PartyFinderWindowOnScreen() ? "the Party Finder is open"
                : isFetchingBoard || isRefreshExecuting || isEndingRecruitment || isCapturingListing
                    || IsCoordinationBusy || currentStep is not (AutomationStep.Idle or AutomationStep.Done)
                    ? "another Party Finder action is running"
                : string.Empty;
            return why.Length == 0;
        }

        /// <summary>
        /// Opens the Party Finder list, reads every page, and closes it. <paramref name="received"/>
        /// counts listings as Dalamud hands them over, which is how a page is known to have arrived.
        /// Returns the pages read and a note for the log.
        /// </summary>
        /// <summary>The Party Finder's category-tab value for "All", learned the first time a read
        /// presses it. -1 until then.</summary>
        private int allCategoryTab
        {
            get => config.PfAllCategoryTab;
            set
            {
                if (config.PfAllCategoryTab == value)
                    return;
                config.PfAllCategoryTab = value;
                config.Save();
            }
        }

        /// <summary>
        /// Whether the player has the Party Finder open themselves, and whether what they are
        /// looking at can stand for the whole data centre: the Data Centre tab, every category. And
        /// how far through the pages they are, and the game's count. Framework thread.
        /// </summary>
        public unsafe (bool Open, bool Whole, int End, int Total) ManualBrowse()
        {
            bool busy = isFetchingBoard || isRefreshExecuting || isEndingRecruitment || isCapturingListing
                || IsCoordinationBusy || currentStep is not (AutomationStep.Idle or AutomationStep.Done);
            var addon = GetBoardAddon();
            if (busy || addon == null || !addon->IsVisible)
                return (false, false, 0, 0);

            var agent = AgentLookingForGroup.Instance();
            bool whole = agent != null && agent->SearchAreaTab == SearchAreaTabDataCentre
                && allCategoryTab >= 0 && agent->CategoryTab == allCategoryTab;
            var pager = ReadPager();
            return (true, whole, pager.Found ? pager.End : 0, pager.Found ? pager.Total : 0);
        }

        /// <summary>The game's own listing count from the last read, and whether that read reached
        /// its last page - for the sweep that follows it.</summary>
        public int LastReadTotal { get; private set; }
        public bool LastReadComplete { get; private set; }

        public async Task<(int Pages, string Note)> FetchPartyFinderAsync(Func<int> received,
            Func<bool>? playerBack = null, Func<int>? seenSoFar = null,
            Func<IReadOnlyList<ulong>>? detailWanted = null, Action<FreshListing>? onDetail = null)
        {
            LastReadTotal = 0;
            LastReadComplete = false;
            fetchAbort = playerBack;
            fetchSeen = seenSoFar;
            fetchAborted = false;
            if (isFetchingBoard || disposed)
                return (0, "already reading");

            bool opened = await framework.RunOnFrameworkThread(() =>
            {
                unsafe
                {
                    if (!CanFetchBoardNow(out _))
                        return false;
                    var agent = AgentLookingForGroup.Instance();
                    if (agent == null)
                        return false;

                    // The Data Centre tab is the one that lists the data centre's listings.
                    EnsureDataCentreTab(agent);
                    isFetchingBoard = true;
                    hideBoardWindow = true;
                    fetchDeadline = DateTime.UtcNow + MaxFetchTime;
                    agent->Show();
                    return true;
                }
            });
            if (!opened)
                return (0, "not now");

            try
            {
                bool shown = false;
                for (int i = 0; i < 50 && !disposed && !shown; i++)
                {
                    if (!await DelayWithinFetch(100))
                        break;
                    shown = await framework.RunOnFrameworkThread(() => { unsafe { return GetBoardAddon() != null; } });
                }
                if (!shown)
                    return (0, "the Party Finder did not open in time");

                await WaitForListingsAsync(received);

                // Every category, not whichever tab the player last left it on: pressing "All"
                // re-lists from the first page with nothing filtered out.
                bool all = await framework.RunOnFrameworkThread(SelectAllCategories);
                if (all)
                {
                    await WaitForListingsAsync(received);

                    // Remember what "All" reads as, so the player's own browsing can be told apart:
                    // a count taken under a category filter is the count of that category only.
                    await framework.RunOnFrameworkThread(() =>
                    {
                        unsafe
                        {
                            var agent = AgentLookingForGroup.Instance();
                            if (agent != null)
                                allCategoryTab = agent->CategoryTab;
                        }
                    });
                }

                // The game's count, as soon as the first page is in - so even a read the deadline
                // cuts short can trim the board to it.
                var first = await framework.RunOnFrameworkThread(ReadPager);
                if (first.Found && first.Total > 0)
                {
                    LastReadTotal = first.Total;
                    LastReadComplete = first.End >= first.Total;
                }

                int pages = 1;
                string note = "one page";
                while (pages < MaxFetchPages && !disposed)
                {
                    if (PastFetchDeadline)
                    {
                        note = LastReadComplete ? $"every listing ({LastReadTotal}) read"
                            : fetchAborted ? "stopped: you came back"
                            : $"stopped at the {MaxFetchTime.TotalSeconds:F0}s limit";
                        break;
                    }

                    int startBefore = await framework.RunOnFrameworkThread(() => ReadPager().Start);
                    // Stop at once if the moment has passed - a pull, a zone change.
                    bool stillOk = await framework.RunOnFrameworkThread(() =>
                        clientState.IsLoggedIn && !IsInDuty() && !condition[ConditionFlag.InCombat]
                        && !condition[ConditionFlag.BetweenAreas]);
                    if (!stillOk)
                    {
                        note = "stopped: no longer a good moment";
                        break;
                    }

                    var before = await framework.RunOnFrameworkThread(ReadPager);
                    if (before.Found && before.Total > 0)
                    {
                        LastReadTotal = before.Total;
                        if (before.End >= before.Total)
                        {
                            // The last page is on screen: every listing has been handed over.
                            LastReadComplete = true;
                            note = pages == 1 ? "one page, all of it" : $"all {pages} pages";
                            break;
                        }
                    }

                    var turn = await framework.RunOnFrameworkThread(() => TryTurnPage(false));
                    if (!turn.Turned)
                    {
                        note = turn.Note;
                        break;
                    }

                    await WaitForListingsAsync(received);

                    // The page has to have actually moved. The game can ignore a page request that
                    // comes too soon after the last, so a turn that did not take is tried again after
                    // 3, 6 and 10 seconds, alternating the two ways of clicking - and the log says
                    // which one moved it and when.
                    int startAfter = await framework.RunOnFrameworkThread(() => ReadPager().Start);
                    int[] waits = { 3000, 6000, 10000 };
                    for (int attempt = 0; startAfter <= startBefore && attempt < waits.Length && !disposed; attempt++)
                    {
                        if (!await DelayWithinFetch(waits[attempt]))
                            break;
                        bool synthetic = attempt % 2 == 0;
                        var retry = await framework.RunOnFrameworkThread(() => TryTurnPage(synthetic));
                        if (!retry.Turned)
                            break;
                        await WaitForListingsAsync(received);
                        startAfter = await framework.RunOnFrameworkThread(() => ReadPager().Start);
                        if (startAfter > startBefore)
                            pluginLog.Information($"[PF Board] Page past {startBefore} turned on retry {attempt + 1} "
                                + $"({(synthetic ? "synthetic click" : "button event")}, after {waits[attempt] / 1000}s).");
                    }

                    if (startAfter <= startBefore)
                    {
                        if (PastFetchDeadline)
                        {
                            note = LastReadComplete ? $"every listing ({LastReadTotal}) read"
                                : fetchAborted ? "stopped: you came back"
                                : $"stopped at the {MaxFetchTime.TotalSeconds:F0}s limit";
                            break;
                        }
                        await framework.RunOnFrameworkThread(LogPager);
                        note = $"stopped: the page did not move past {startBefore}";
                        break;
                    }

                    pages++;
                }

                // The alliances, in full - while the window is still open and still hidden.
                if (detailWanted != null && onDetail != null && !disposed && fetchAbort?.Invoke() != true)
                {
                    var wanted = await framework.RunOnFrameworkThread(detailWanted);
                    int full = await ReadDetailsHiddenAsync(wanted, onDetail);
                    if (full > 0)
                        note += $", {full} alliance{(full == 1 ? "" : "s")} read in full";
                }

                return (pages, note);
            }
            catch (Exception ex)
            {
                pluginLog.Error(ex, "[PF Board] Reading the Party Finder failed.");
                return (0, "failed; see the log");
            }
            finally
            {
                fetchDeadline = DateTime.MaxValue;
                fetchAbort = null;
                fetchSeen = null;
                await framework.RunOnFrameworkThread(() =>
                {
                    unsafe
                    {
                        var agent = AgentLookingForGroup.Instance();
                        var addon = GetBoardAddon();
                        RestoreBoardWindow(addon);
                        if (agent != null && addon != null)
                            agent->Hide();
                    }
                });

                // Closed means closed: checked, asked again once if not, and said if it still is.
                for (int i = 0; i < 20; i++)
                {
                    await Task.Delay(100);
                    bool open = await framework.RunOnFrameworkThread(() => { unsafe { return GetBoardAddon() != null; } });
                    if (!open)
                        break;
                    if (i == 10)
                    {
                        await framework.RunOnFrameworkThread(() =>
                        {
                            unsafe
                            {
                                var agent = AgentLookingForGroup.Instance();
                                if (agent != null)
                                    agent->Hide();
                            }
                        });
                    }
                    if (i == 19)
                        pluginLog.Warning("[PF Board] The Party Finder was still open two seconds after a read closed it.");
                }
                isFetchingBoard = false;
                hideBoardWindow = false;
            }
        }

        /// <summary>Presses the list's "All" category button, found by its label. False when there is
        /// no such button, in which case the read covers whatever category is showing.</summary>
        private unsafe bool SelectAllCategories()
        {
            var addon = GetBoardAddon();
            if (addon == null)
                return false;

            var all = new System.Collections.Generic.List<nint>();
            CollectNodes(&addon->UldManager, all, 0);
            foreach (nint p in all)
            {
                var node = (AtkResNode*)p;
                if ((int)node->Type < 1000)
                    continue;

                var button = node->GetAsAtkComponentButton();
                if (button != null && button->IsEnabled
                    && string.Equals(AtkHelpers.GetButtonLabel(button).Trim(), "All", StringComparison.OrdinalIgnoreCase))
                {
                    AtkHelpers.ClickAddonButton(addon, button);
                    return true;
                }
            }

            pluginLog.Information("[PF Board] No \"All\" category button found; reading the category on screen.");
            return false;
        }

        /// <summary>Waits for a page to arrive: listings start coming, then stop for a moment.
        /// Gives up after a few seconds - an empty page never starts.</summary>
        private async Task WaitForListingsAsync(Func<int> received)
        {
            int last = received();
            int quiet = 0;
            bool started = false;
            for (int i = 0; i < 60 && !disposed; i++)
            {
                if (!await DelayWithinFetch(100))
                    return;
                int now = received();
                if (now != last)
                {
                    started = true;
                    quiet = 0;
                    last = now;
                }
                else if (++quiet >= (started ? 12 : 40))
                {
                    return;
                }
            }
        }

        /// <summary>Every visible node of an addon, components' insides included - the page
        /// counter and its arrows can sit inside a component rather than on the window itself.</summary>
        private static unsafe void CollectNodes(AtkUldManager* uld, System.Collections.Generic.List<nint> into, int depth)
        {
            if (uld == null || depth > 6)
                return;

            var nodes = uld->Nodes;
            for (int i = 0; i < uld->NodeListCount && i < nodes.Length; i++)
            {
                AtkResNode* node = nodes[i].Value;
                if (node == null || !node->IsVisible())
                    continue;

                into.Add((nint)node);
                if ((int)node->Type >= 1000)
                {
                    var component = ((AtkComponentNode*)node)->Component;
                    if (component != null)
                        CollectNodes(&component->UldManager, into, depth + 1);
                }
            }
        }

        private bool pageLayoutLogged;

        /// <summary>
        /// The pager under the list: the range on screen ("51-100", as its first number), how far
        /// it goes, the total, and the forward arrow. Read from the window's own top-level nodes
        /// only - never from inside the listing rows, where a comment like "need 2-3 more" or an
        /// item level "730-740" reads exactly like a range and once sent the reader clicking around
        /// a listing instead of the pager.
        /// </summary>
        private unsafe (bool Found, int Start, int End, int Total, nint Next) ReadPager()
        {
            var addon = GetBoardAddon();
            if (addon == null)
                return (false, -1, 0, -1, 0);

            var nodes = addon->UldManager.Nodes;
            AtkResNode* range = null;
            int start = -1, end = 0, total = -1;
            for (int i = 0; i < addon->UldManager.NodeListCount && i < nodes.Length; i++)
            {
                AtkResNode* node = nodes[i].Value;
                if (node == null || node->Type != NodeType.Text || !node->IsVisible())
                    continue;

                string text = ((AtkTextNode*)node)->NodeText.ToString();
                var r = PageRange.Match(text);
                if (r.Success && range == null)
                {
                    range = node;
                    start = int.Parse(r.Groups[1].Value);
                    end = int.Parse(r.Groups[2].Value);
                    continue;
                }

                var t = PageTotal.Match(text);
                if (t.Success && total < 0)
                    total = int.Parse(t.Groups[1].Value);
            }

            if (range == null || total < 0)
                return (false, start, end, total, 0);

            // The arrows share the range's row: back, then forward - unlabelled, and so told from
            // every other button by that and by where they are.
            float rowMid = range->ScreenY + range->Height * range->ScaleY * 0.5f;
            AtkComponentButton* next = null;
            float rightmost = float.MinValue;
            for (int i = 0; i < addon->UldManager.NodeListCount && i < nodes.Length; i++)
            {
                AtkResNode* node = nodes[i].Value;
                if (node == null || (int)node->Type < 1000 || !node->IsVisible())
                    continue;

                var button = node->GetAsAtkComponentButton();
                if (button == null || AtkHelpers.GetButtonLabel(button).Length > 0)
                    continue;

                float mid = node->ScreenY + node->Height * node->ScaleY * 0.5f;
                if (Math.Abs(mid - rowMid) > 16f || node->ScreenX <= rightmost)
                    continue;

                rightmost = node->ScreenX;
                next = button;
            }

            return (true, start, end, total, (nint)next);
        }

        /// <summary>When a page would not turn: the pager as the reader sees it - the range, every
        /// unlabelled button on its row, and the event chain of the one taken for "forward".</summary>
        private unsafe void LogPager()
        {
            var addon = GetBoardAddon();
            if (addon == null)
                return;

            var pager = ReadPager();
            pluginLog.Information($"[PF Board] Pager: found {pager.Found}, {pager.Start}-{pager.End} of {pager.Total}, forward 0x{pager.Next:X}.");

            var nodes = addon->UldManager.Nodes;
            for (int i = 0; i < addon->UldManager.NodeListCount && i < nodes.Length; i++)
            {
                AtkResNode* node = nodes[i].Value;
                if (node == null || (int)node->Type < 1000 || !node->IsVisible())
                    continue;
                var button = node->GetAsAtkComponentButton();
                if (button == null || AtkHelpers.GetButtonLabel(button).Length > 0)
                    continue;

                var events = new System.Collections.Generic.List<string>();
                int guard = 0;
                for (var evt = (AtkEvent*)node->AtkEventManager.Event; evt != null && guard++ < 16; evt = evt->NextEvent)
                    events.Add($"{evt->State.EventType}:{evt->Param}");

                pluginLog.Information($"[PF Board] Pager button #{node->NodeId} at ({node->ScreenX:F0},{node->ScreenY:F0}) "
                    + $"enabled {button->IsEnabled}{((nint)button == pager.Next ? " [forward]" : "")} events {string.Join(", ", events)}");
            }
        }

        /// <summary>Presses "forward" if there is more to read.</summary>
        private unsafe (bool Turned, string Note) TryTurnPage() => TryTurnPage(false);

        private unsafe (bool Turned, string Note) TryTurnPage(bool synthetic)
        {
            var addon = GetBoardAddon();
            if (addon == null)
                return (false, "the Party Finder closed");

            var pager = ReadPager();
            if (!pager.Found)
            {
                var all = new System.Collections.Generic.List<nint>();
                CollectNodes(&addon->UldManager, all, 0);
                LogPageLayoutOnce(all);
                return (false, "no page range found; read the first page only");
            }
            if (pager.End >= pager.Total)
                return (false, $"all {pager.Total} listing(s)");

            var next = (AtkComponentButton*)pager.Next;
            if (next == null)
                return (false, $"{pager.End} of {pager.Total}: no forward arrow on the range's row");
            if (!next->IsEnabled)
                return (false, $"{pager.End} of {pager.Total}: the forward arrow is greyed out");

            if (synthetic)
                AtkHelpers.ClickButton(addon, next);
            else
                AtkHelpers.ClickAddonButtonByClickEvent(addon, next);
            return (true, string.Empty);
        }

        /// <summary>Once per session, what the list window holds - texts and buttons with where
        /// they are - so a layout the page finder does not recognise can be recognised next time.</summary>
        private unsafe void LogPageLayoutOnce(System.Collections.Generic.List<nint> all)
        {
            if (pageLayoutLogged)
                return;
            pageLayoutLogged = true;

            foreach (nint p in all)
            {
                var node = (AtkResNode*)p;
                if (node->Type == NodeType.Text)
                {
                    string text = ((AtkTextNode*)node)->NodeText.ToString();
                    if (text.Length > 0 && text.Length < 40)
                        pluginLog.Information($"[PF Board] List text #{node->NodeId} at ({node->ScreenX:F0},{node->ScreenY:F0}) {node->Width}x{node->Height}: \"{text}\"");
                }
                else if ((int)node->Type >= 1000 && node->GetAsAtkComponentButton() is var b && b != null)
                {
                    pluginLog.Information($"[PF Board] List button #{node->NodeId} at ({node->ScreenX:F0},{node->ScreenY:F0}) {node->Width}x{node->Height} "
                        + $"enabled {b->IsEnabled} label \"{AtkHelpers.GetButtonLabel(b)}\"");
                }
            }
        }
    }
}
