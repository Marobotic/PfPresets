using System;
using System.Collections.Generic;
using Dalamud.Game.Gui.PartyFinder.Types;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace PfPresets
{
    /// <summary>
    /// Puts the duty's real name back on a Party Finder listing the character has not unlocked.
    ///
    /// WHAT THE GAME DOES. A listing for content this character has not reached is drawn with the
    /// duty name replaced by the Addon sheet's "Locked Duty" - row 11090, read out of the sheet
    /// here rather than typed in, so this works on a French or Japanese client without a table of
    /// translations. The listing itself is not withheld: it is in the list, it can be joined, and
    /// the game will tell you why it will not let you in. Only the name is held back.
    ///
    /// WHY THIS CAN DO IT AT ALL, AND WHY THAT IS NOT A LEAK. The duty is in the listing packet as
    /// a ContentFinderCondition row id whatever the character has unlocked - the client needs it to
    /// decide it should print "Locked Duty" in the first place. This plugin has been reading it for
    /// as long as "Save as Preset" has existed: saving a locked listing writes the real fight name
    /// into the preset, because PfAutomation.SaveFromListing resolves the id through
    /// DutyDataHelper without ever asking whether the character has the fight open. So nothing is
    /// fetched, nothing is asked of the server, and nothing about anybody else is stored. The only
    /// thing that changes is whether the window keeps the name to itself.
    ///
    /// WHICH IS EXACTLY WHY IT IS OFF BY DEFAULT. The name the game is hiding is a spoiler - that
    /// is the entire reason it is hidden - so this is opt-in, it asks before it goes on, and it
    /// puts the reason in front of the person answering. See Configuration.ShowLockedDutyNames.
    ///
    /// HOW IT FINDS THE TEXT. By the placeholder, never by node id. A node whose text is exactly
    /// the game's own "Locked Duty" string has identified itself, the same reasoning
    /// PfAutomation.ListingLeader uses to find the leader's name; a patch that renumbers the
    /// window's nodes therefore changes nothing here. It also means the rewrite can only ever land
    /// on a node the game had already given up on - a node showing a real duty name is not a
    /// candidate and cannot be touched.
    ///
    /// WHAT IT WILL NOT DO. It will not guess. A row it cannot resolve to a single duty with
    /// certainty is left saying "Locked Duty", because a listing labelled with the WRONG fight is
    /// far worse than one labelled with none - somebody would join it.
    /// </summary>
    internal sealed unsafe class LockedDutyReveal : IDisposable
    {
        /// <summary>The browse window, the list of everybody's listings.</summary>
        private const string ListAddon = "LookingForGroup";

        /// <summary>The detail window, the one opened on a single listing.</summary>
        private const string DetailAddon = "LookingForGroupDetail";

        /// <summary>Addon sheet row for "Locked Duty", the string the game prints instead of the
        /// name. Read from the sheet so the match works on every client language.</summary>
        private const uint LockedDutyAddonRow = 11090;

        /// <summary>
        /// How many listing-to-duty pairs to remember.
        ///
        /// The game shows fifty per page and the sheet's own array is fifty long, so a few hundred
        /// covers paging back and forth across a session without the map growing without bound on
        /// somebody who leaves the Party Finder open all evening.
        /// </summary>
        private const int MaxRemembered = 512;

        private readonly IPartyFinderGui partyFinderGui;
        private readonly IGameGui gameGui;
        private readonly IDataManager dataManager;
        private readonly IPluginLog log;
        private readonly DutyDataHelper duties;
        private readonly Func<bool> enabled;

        /// <summary>
        /// Listing id to ContentFinderCondition row id, filled from the listings the client
        /// receives.
        ///
        /// THIS IS THE ONLY PLACE THE PER-ROW ANSWER COMES FROM. The agent's own listings array
        /// carries ids and nothing else, and the one call that turns a listing id into a duty
        /// opens the game's detail window to do it - which is not something a redraw can go and do
        /// fifty times. Dalamud hands us the whole listing as it arrives instead, duty included,
        /// which is the same packet the window is about to be drawn from.
        /// </summary>
        private readonly Dictionary<ulong, uint> dutyByListing = new();

        /// <summary>Insertion order, so the map can be trimmed oldest-first without sorting.</summary>
        private readonly Queue<ulong> rememberedOrder = new();

        /// <summary>
        /// Text nodes this class has rewritten: what the game had in them, what we put there, and
        /// which window it belongs to.
        ///
        /// All three are needed to undo it safely. The original is what goes back; the written text
        /// is the proof the node is still ours (see <see cref="RestoreAll"/>); the window is what
        /// says whether the pointer is still alive at all.
        /// </summary>
        private readonly Dictionary<nint, Rewrite> rewritten = new();

        /// <summary>One rewritten text node, with everything needed to put it back.</summary>
        private readonly struct Rewrite
        {
            public Rewrite(string addon, string original, string written)
            {
                Addon = addon;
                Original = original;
                Written = written;
            }

            /// <summary>Addon the node lives on. A node outlives nothing: once its window has
            /// closed the memory is not ours to write to, so this decides whether it is touched.
            /// </summary>
            public string Addon { get; }

            /// <summary>The game's own text - the placeholder - which is what restoring writes.
            /// </summary>
            public string Original { get; }

            /// <summary>What we replaced it with. A node whose text is no longer this has been
            /// repainted or recycled by the game and must be left alone.</summary>
            public string Written { get; }
        }

        /// <summary>The placeholder itself, resolved once. Empty means the sheet would not answer,
        /// and an empty needle matches everything, so an empty one disables the whole feature.</summary>
        private string? lockedPlaceholder;

        /// <summary>Whether the map is being fed. Subscribing costs a delegate call per listing
        /// received, so it only happens while the setting is on.</summary>
        private bool subscribed;

        public LockedDutyReveal(
            IPartyFinderGui partyFinderGui,
            IGameGui gameGui,
            IDataManager dataManager,
            IPluginLog log,
            DutyDataHelper duties,
            Func<bool> enabled)
        {
            this.partyFinderGui = partyFinderGui;
            this.gameGui = gameGui;
            this.dataManager = dataManager;
            this.log = log;
            this.duties = duties;
            this.enabled = enabled;
        }

        /// <summary>
        /// One frame's worth of work: follow the setting, then rewrite whatever is on screen.
        ///
        /// Called every frame from the framework update, and does nothing at all on the frames that
        /// matter - the setting is off, or neither Party Finder window is open - which is nearly
        /// all of them.
        /// </summary>
        public void Tick()
        {
            bool on = false;
            try
            {
                on = this.enabled();
            }
            catch
            {
                // A config read should not be able to throw, but this runs every frame from the
                // game's own update and a throw here would be a per-frame exception forever.
            }

            if (!on)
            {
                // Turned off, or never on. Anything already rewritten goes back.
                if (this.rewritten.Count > 0)
                    RestoreAll();

                Unsubscribe();
                return;
            }

            Subscribe();

            string? needle = Placeholder();
            if (string.IsNullOrEmpty(needle))
                return;

            try
            {
                bool touched = RevealInDetailWindow(needle);
                touched |= RevealInListWindow(needle);

                // Neither window is open - that is what both of those returning false means. Their
                // nodes went with them, so the undo map is describing memory that no longer belongs
                // to those addons: drop it rather than carry it to the next time the Party Finder
                // opens, where the same addresses may well be back as different nodes. This is also
                // what keeps the map from growing across a session of opening and closing it.
                if (!touched)
                    this.rewritten.Clear();
            }
            catch (Exception ex)
            {
                // Never let a bad frame become a crash in the game's update. One log line, and the
                // window is left exactly as the game drew it.
                this.log.Error(ex, "[LockedDutyReveal] Failed while rewriting the Party Finder.");
            }
        }

        // ── The map ──────────────────────────────────────────────────────────

        private void Subscribe()
        {
            if (this.subscribed)
                return;

            this.partyFinderGui.ReceiveListing += OnReceiveListing;
            this.subscribed = true;
        }

        private void Unsubscribe()
        {
            if (!this.subscribed)
                return;

            this.partyFinderGui.ReceiveListing -= OnReceiveListing;
            this.subscribed = false;

            // The map is dropped with the subscription. Holding a half-stale map across a period
            // when we were not watching would mean rows resolved from listings that have since
            // been replaced, and a wrong name is the one outcome this class will not produce.
            this.dutyByListing.Clear();
            this.rememberedOrder.Clear();
        }

        /// <summary>
        /// Records what duty a listing is for, as the client receives it.
        ///
        /// READ-ONLY, DELIBERATELY. The event args also let a plugin hide a listing from the
        /// window; this one never touches <c>args.Visible</c>. Nothing here changes which listings
        /// the player sees, only what one of them is called.
        /// </summary>
        private void OnReceiveListing(IPartyFinderListing listing, IPartyFinderListingEventArgs args)
        {
            try
            {
                if (listing.RawDuty == 0)
                    return;

                if (!this.dutyByListing.ContainsKey(listing.Id))
                    this.rememberedOrder.Enqueue(listing.Id);

                this.dutyByListing[listing.Id] = listing.RawDuty;

                while (this.rememberedOrder.Count > MaxRemembered)
                {
                    ulong oldest = this.rememberedOrder.Dequeue();

                    // Only if it is still the entry we queued: an id re-listed since then was
                    // re-enqueued, and dropping it here would forget the newer one.
                    if (this.dutyByListing.ContainsKey(oldest) && !this.rememberedOrder.Contains(oldest))
                        this.dutyByListing.Remove(oldest);
                }
            }
            catch (Exception ex)
            {
                this.log.Error(ex, "[LockedDutyReveal] Failed while recording a listing.");
            }
        }

        // ── The windows ──────────────────────────────────────────────────────

        /// <summary>
        /// The opened listing. The simplest of the two: the agent has already put the listing being
        /// viewed in LastViewedListing, duty id included, so there is nothing to correlate.
        /// </summary>
        /// <returns>True when the window was open, whether or not anything needed rewriting.</returns>
        private bool RevealInDetailWindow(string needle)
        {
            var addon = GetVisible(DetailAddon);
            if (addon == null)
                return false;

            var agent = AgentLookingForGroup.Instance();
            if (agent == null)
                return true;

            var listing = agent->LastViewedListing;
            if (listing.ListingId == 0 || listing.DutyId == 0)
                return true;

            string? revealed = RevealedName(listing.DutyId, needle);
            if (revealed == null)
                return true;

            ReplaceMatchingText(&addon->UldManager, needle, revealed, DetailAddon);
            return true;
        }

        /// <summary>
        /// The browse list.
        ///
        /// Each rendered row knows its own index, and the agent holds the listing ids in the order
        /// the rows are drawn, so index -> listing id -> duty. Both halves have to agree: a row
        /// whose index is past the end of what the agent says is displayed is left alone rather
        /// than resolved against whatever happens to be at that slot.
        /// </summary>
        /// <returns>True when the window was open, whether or not anything needed rewriting.</returns>
        private bool RevealInListWindow(string needle)
        {
            var addon = (AddonLookingForGroup*)GetVisible(ListAddon);
            if (addon == null)
                return false;

            var agent = AgentLookingForGroup.Instance();
            if (agent == null)
                return true;

            var listingIds = agent->Listings.ListingIds;
            int displayed = Math.Min(agent->NumberOfListingsDisplayed, listingIds.Length);
            if (displayed <= 0)
                return true;

            // Both views exist as separate lists and the window swaps between them, so whichever
            // one is actually on screen is the one to walk. Asking both is cheaper than working out
            // which mode the window is in, and the hidden one has nothing visible to rewrite.
            RevealInList(addon->StandardViewList, listingIds, displayed, needle);
            RevealInList(addon->CompactViewList, listingIds, displayed, needle);
            return true;
        }

        private void RevealInList(
            AtkComponentList* list, Span<ulong> listingIds, int displayed, string needle)
        {
            if (list == null || list->ItemRendererList == null)
                return;

            int rows = Math.Min(list->ListLength, list->AllocatedItemRendererListLength);

            for (int i = 0; i < rows; i++)
            {
                var renderer = list->ItemRendererList[i].AtkComponentListItemRenderer;
                if (renderer == null)
                    continue;

                // The row's own idea of which listing it is showing, not our loop counter: the
                // renderers are a recycled pool and their order in the array is not the order on
                // screen once the list has been scrolled.
                int index = renderer->ListItemIndex;
                if (index < 0 || index >= displayed)
                    continue;

                ulong listingId = listingIds[index];
                if (listingId == 0)
                    continue;

                if (!this.dutyByListing.TryGetValue(listingId, out uint dutyId))
                    continue;

                string? revealed = RevealedName(dutyId, needle);
                if (revealed == null)
                    continue;

                var owner = renderer->OwnerNode;
                if (owner == null || owner->Component == null)
                    continue;

                ReplaceMatchingText(&owner->Component->UldManager, needle, revealed, ListAddon);
            }
        }

        // ── Naming ───────────────────────────────────────────────────────────

        /// <summary>
        /// What a locked listing should say, or null when there is nothing honest to put there.
        ///
        /// The "(Locked Duty)" suffix is not decoration. The listing is still locked - the game
        /// will refuse the join - so a row that said only the fight's name would be describing
        /// something the player cannot do while looking exactly like a row they can. The suffix
        /// reuses the game's own string for the same reason the search does: it is already in the
        /// player's language.
        /// </summary>
        private string? RevealedName(uint dutyRowId, string placeholder)
        {
            var duty = this.duties.GetDutyEntry(dutyRowId);
            if (duty == null || string.IsNullOrWhiteSpace(duty.Name))
                return null;

            return $"{duty.Name} ({placeholder})";
        }

        /// <summary>
        /// The game's "Locked Duty", resolved once and cached.
        ///
        /// Cached as the empty string on failure rather than retried: the Addon sheet does not
        /// change while the game is running, so a lookup that failed once will fail every frame,
        /// and an empty needle would match every text node on the window.
        /// </summary>
        private string? Placeholder()
        {
            if (this.lockedPlaceholder != null)
                return this.lockedPlaceholder;

            try
            {
                var sheet = this.dataManager.GetExcelSheet<Lumina.Excel.Sheets.Addon>();
                string text = sheet?.GetRowOrDefault(LockedDutyAddonRow)?.Text.ExtractText() ?? string.Empty;

                this.lockedPlaceholder = text.Trim();

                if (this.lockedPlaceholder.Length == 0)
                    this.log.Warning(
                        "[LockedDutyReveal] Addon row {0} is empty; locked duty names stay hidden.",
                        LockedDutyAddonRow);
            }
            catch (Exception ex)
            {
                this.log.Error(ex, "[LockedDutyReveal] Could not read the \"Locked Duty\" string.");
                this.lockedPlaceholder = string.Empty;
            }

            return this.lockedPlaceholder;
        }

        // ── Nodes ────────────────────────────────────────────────────────────

        /// <summary>
        /// Rewrites every visible text node under <paramref name="uld"/> whose text is exactly the
        /// placeholder, remembering what was there.
        ///
        /// EXACT MATCH, not "contains". The comment on a listing is a text node on the same window
        /// and somebody is eventually going to type the words "locked duty" into one; matching on
        /// equality means the only nodes this can reach are the ones the game filled in itself.
        /// </summary>
        private void ReplaceMatchingText(
            AtkUldManager* uld, string needle, string replacement, string addon)
        {
            if (uld == null || uld->NodeList == null)
                return;

            for (int i = 0; i < uld->NodeListCount; i++)
            {
                var node = uld->NodeList[i];
                if (node == null || !node->IsVisible())
                    continue;

                // Component nodes carry their own node list, and a list row's text lives inside one.
                if ((ushort)node->Type >= 1000)
                {
                    var component = ((AtkComponentNode*)node)->Component;
                    if (component != null)
                        ReplaceMatchingText(&component->UldManager, needle, replacement, addon);
                    continue;
                }

                if (node->Type != NodeType.Text)
                    continue;

                var text = (AtkTextNode*)node;
                string current = text->NodeText.ToString();

                if (string.Equals(current, replacement, StringComparison.Ordinal))
                    continue;

                if (!string.Equals(current, needle, StringComparison.Ordinal))
                    continue;

                this.rewritten[(nint)text] = new Rewrite(addon, current, replacement);
                text->SetText(replacement);
            }
        }

        /// <summary>
        /// Puts every node this class changed back to the game's own text.
        ///
        /// TWO GUARDS, AND BOTH ARE ABOUT WRITING TO MEMORY THAT IS NO LONGER OURS.
        ///
        /// The window must still be open. A text node belongs to its addon, and once that addon has
        /// closed the pointer is a stale address - so a node from a closed window is dropped
        /// unrestored rather than written to. Nothing is lost by that: the game rebuilds those
        /// nodes when it next opens the window, and it rebuilds them with the placeholder, which is
        /// exactly what restoring would have put back.
        ///
        /// And the node must still say what we wrote. These renderers are a recycled pool - the
        /// same address is handed to a different row as the list scrolls - so an address alone is
        /// not proof the node is still the one we changed. If its text is no longer ours, the game
        /// has already repainted it and writing the placeholder over the top would be inventing a
        /// locked listing where there is not one.
        /// </summary>
        private void RestoreAll()
        {
            bool listOpen = GetVisible(ListAddon) != null;
            bool detailOpen = GetVisible(DetailAddon) != null;

            foreach (var (pointer, entry) in this.rewritten)
            {
                try
                {
                    bool open = entry.Addon == ListAddon ? listOpen : detailOpen;
                    if (!open || pointer == 0)
                        continue;

                    var text = (AtkTextNode*)pointer;
                    if (!string.Equals(text->NodeText.ToString(), entry.Written, StringComparison.Ordinal))
                        continue;

                    text->SetText(entry.Original);
                }
                catch (Exception ex)
                {
                    this.log.Error(ex, "[LockedDutyReveal] Failed to restore a listing's text.");
                }
            }

            this.rewritten.Clear();
        }

        private AtkUnitBase* GetVisible(string name)
        {
            var addon = (AtkUnitBase*)(nint)this.gameGui.GetAddonByName(name);
            if (addon == null || !addon->IsVisible || addon->RootNode == null)
                return null;

            return addon;
        }

        public void Dispose()
        {
            try
            {
                RestoreAll();
            }
            catch (Exception ex)
            {
                this.log.Error(ex, "[LockedDutyReveal] Failed while restoring on unload.");
            }

            Unsubscribe();
        }
    }
}
