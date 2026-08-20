using System;
using System.Collections.Generic;
using Dalamud.Hooking;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Info;

namespace PfPresets
{
    /// <summary>One slot in a listing, as the game already knows it.</summary>
    public sealed class ListingSlot
    {
        /// <summary>ClassJob row id, or 0 for a slot nobody has taken.</summary>
        public uint JobId { get; set; }

        public bool Filled => JobId != 0;

        /// <summary>The character's name, when this machine happened to be able to say - see
        /// <see cref="ListingXray.TryResolveName"/>. Empty is the ordinary case, not a failure.</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>Their home world id, or 0. Left as an id because turning one into a name is the
        /// UI's job and this class has no world table.</summary>
        public ushort HomeWorldId { get; set; }

        public bool Named => Name.Length > 0;
    }

    /// <summary>
    /// What the game handed us about the listing currently on screen.
    ///
    /// Copied out of the struct rather than held by pointer. The game's buffer is reused for the
    /// next listing the moment one is opened, so a pointer kept across frames describes whatever
    /// was looked at most recently - which is the one bug this whole class would otherwise be.
    /// </summary>
    public sealed class ListingSnapshot
    {
        public string Leader { get; set; } = string.Empty;
        public string Comment { get; set; } = string.Empty;

        public ushort DutyId { get; set; }
        public ushort AverageItemLevel { get; set; }

        public byte TotalSlots { get; set; }
        public byte SlotsFilled { get; set; }

        public List<ListingSlot> Slots { get; set; } = new();

        /// <summary>How many members the listing named, whether or not we can say who they are.</summary>
        public int MemberCount { get; set; }

        /// <summary>How many of those this machine could put a name to. The diagnostic that decides
        /// whether naming a listing is possible locally at all.</summary>
        public int NamedCount { get; set; }

        public DateTime SeenUtc { get; set; } = DateTime.UtcNow;
    }

    /// <summary>
    /// Reads the party finder listing the player is already looking at.
    ///
    /// WHAT THIS IS. When a listing is opened the game fills a structure with everything it knows
    /// about that party - the jobs sitting in each slot, the leader, the comment, the item level -
    /// and then draws about half of it. None of this is fetched, guessed at, or asked of the
    /// server: it is on this machine already because the client needed it to render the window.
    /// The hook is on the game's own populate function, so it sees exactly what the window sees.
    ///
    /// WHAT THIS DELIBERATELY IS NOT. The structure also carries a content id per member, and
    /// content ids are not names - resolving a stranger's would mean keeping a map of ids to
    /// characters built out of what other people's clients saw, which is a record about players who
    /// never installed this plugin. The count is reported. The ids are read and dropped.
    ///
    /// AND IT STANDS DOWN FOR PFRADAR. That plugin does the same job and hooks the same function;
    /// two hooks on one function is how one of them ends up chained behind the other and the game
    /// ends up dependent on unload order. When PFRadar is loaded this one unhooks entirely - not
    /// hidden, not drawn-but-idle, actually not hooked.
    /// </summary>
    internal sealed unsafe class ListingXray : IDisposable
    {
        /// <summary>The other plugin's InternalName, as its own manifest declares it.</summary>
        private const string PfRadarInternalName = "PFRadar";

        private delegate void PopulateListingDataDelegate(
            AgentLookingForGroup* agent, AgentLookingForGroup.Detailed* listing);

        private readonly IDalamudPluginInterface pluginInterface;
        private readonly IPluginLog log;
        private readonly Func<bool> enabled;

        private Hook<PopulateListingDataDelegate>? hook;

        /// <summary>Re-checked on a timer rather than once: a plugin can be enabled or disabled
        /// from the installer while the game is running, and this has to follow it.</summary>
        private static readonly TimeSpan ConflictRecheck = TimeSpan.FromSeconds(5);
        private DateTime conflictCheckedAt = DateTime.MinValue;

        public ListingXray(IDalamudPluginInterface pluginInterface, IGameInteropProvider interop,
            IPluginLog log, Func<bool> enabled)
        {
            this.pluginInterface = pluginInterface;
            this.log = log;
            this.enabled = enabled;

            try
            {
                hook = interop.HookFromAddress<PopulateListingDataDelegate>(
                    AgentLookingForGroup.Addresses.PopulateListingData.Value, OnPopulate);
            }
            catch (Exception ex)
            {
                // A signature that no longer matches after a patch. The feature goes quiet; nothing
                // else in the plugin depends on it, and a failed hook must never stop the rest
                // loading.
                log.Warning($"[Listing] Couldn't hook listing details: {ex.Message}");
                hook = null;
            }

            Sync();
        }

        /// <summary>The last listing opened, or null if none has been since login.</summary>
        public ListingSnapshot? Current { get; private set; }

        /// <summary>True while PFRadar is loaded, so the UI can say why it is not showing
        /// anything rather than simply showing nothing.</summary>
        public bool SuppressedByPfRadar { get; private set; }

        /// <summary>Whether a hook is actually installed right now.</summary>
        public bool Active => hook?.IsEnabled == true;

        /// <summary>
        /// Brings the hook in line with the setting and with whether PFRadar is loaded.
        ///
        /// Safe to call every frame; the conflict check itself is throttled, and enabling a hook
        /// that is already enabled is a no-op.
        /// </summary>
        public void Sync()
        {
            if (hook == null)
                return;

            if (DateTime.UtcNow - conflictCheckedAt >= ConflictRecheck)
            {
                conflictCheckedAt = DateTime.UtcNow;
                SuppressedByPfRadar = PfRadarLoaded();
            }

            bool want = enabled() && !SuppressedByPfRadar;

            try
            {
                if (want && !hook.IsEnabled)
                {
                    hook.Enable();
                }
                else if (!want && hook.IsEnabled)
                {
                    hook.Disable();
                    Current = null;
                }
            }
            catch (Exception ex)
            {
                log.Debug($"[Listing] Couldn't change hook state: {ex.Message}");
            }
        }

        /// <summary>
        /// Whether PFRadar is installed AND running.
        ///
        /// Installed-but-disabled does not count, which is the whole point of asking IsLoaded
        /// rather than merely finding it in the list: somebody who turned it off has said they want
        /// something else to do this.
        /// </summary>
        private bool PfRadarLoaded()
        {
            try
            {
                foreach (var plugin in pluginInterface.InstalledPlugins)
                {
                    if (string.Equals(plugin.InternalName, PfRadarInternalName,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        return plugin.IsLoaded;
                    }
                }
            }
            catch (Exception ex)
            {
                // Never assume it is absent on an error - assuming it is PRESENT is the safe
                // direction, because the cost of being wrong is two hooks on one function.
                log.Debug($"[Listing] Couldn't read the plugin list: {ex.Message}");
                return true;
            }

            return false;
        }

        private void OnPopulate(AgentLookingForGroup* agent, AgentLookingForGroup.Detailed* listing)
        {
            // The game's call goes first and unconditionally. Everything below is a read of what it
            // just wrote, and a throw here would take the listing window with it.
            hook!.Original(agent, listing);

            try
            {
                if (listing == null)
                    return;

                Current = Capture(listing);
            }
            catch (Exception ex)
            {
                log.Debug($"[Listing] Couldn't read a listing: {ex.Message}");
            }
        }

        /// <summary>
        /// The lists this client already holds that pair a content id with a name.
        ///
        /// Only proxies that genuinely embed an <see cref="InfoProxyCommonList"/> are here. Several
        /// others - the party invite list, the blacklist, player search - are laid out differently,
        /// and casting one of those to a CommonList would be reading a name out of whatever
        /// happens to sit at that offset. Verified per type rather than assumed from the name.
        /// </summary>
        private static readonly InfoProxyId[] NameSources =
        {
            InfoProxyId.PartyMember,
            InfoProxyId.FriendList,
            InfoProxyId.FreeCompanyMember,
            InfoProxyId.LinkshellMember,
            InfoProxyId.CrossWorldLinkshellMember,
            InfoProxyId.ContentMember,
            InfoProxyId.NoviceNetworkMember,
        };

        /// <summary>
        /// Turns a content id into a name, if this machine already knows the character.
        ///
        /// THE WHOLE QUESTION THIS FEATURE TURNS ON. The listing gives content ids and no names.
        /// The game can map one to the other - GetEntryByContentId - but only within lists the
        /// client is already holding: your party, friends, free company, linkshells, the novice
        /// network, the people in your current duty.
        ///
        /// So a stranger in a party finder listing is expected to come back empty. That is not a
        /// bug and must never be reported as one; it is the honest limit of what this machine
        /// knows. Whether it comes back empty MOST of the time is the measurement - see the count
        /// logged in <see cref="Capture"/>.
        /// </summary>
        private static bool TryResolveName(ulong contentId, out string name, out ushort homeWorld)
        {
            name = string.Empty;
            homeWorld = 0;

            if (contentId == 0)
                return false;

            var info = InfoModule.Instance();
            if (info == null)
                return false;

            foreach (var id in NameSources)
            {
                var proxy = info->GetInfoProxyById(id);
                if (proxy == null)
                    continue;

                var list = (InfoProxyCommonList*)proxy;
                var entry = list->GetEntryByContentId(contentId);
                if (entry == null)
                    continue;

                string found = entry->NameString;
                if (string.IsNullOrWhiteSpace(found))
                    continue;

                name = found;
                homeWorld = entry->HomeWorld;
                return true;
            }

            return false;
        }

        private ListingSnapshot Capture(AgentLookingForGroup.Detailed* listing)
        {
            var snapshot = new ListingSnapshot
            {
                Leader = listing->LeaderString ?? string.Empty,
                Comment = listing->CommentString ?? string.Empty,
                DutyId = listing->DutyId,
                AverageItemLevel = listing->AvgItemLv,
                TotalSlots = listing->TotalSlots,
                SlotsFilled = listing->SlotsFilled,
            };

            var jobs = listing->Jobs;
            var ids = listing->MemberContentIds;

            // TotalSlots is what the party advertises; the arrays behind it are a fixed 48 whatever
            // that says. Clamped to all three so a malformed listing cannot walk us off the end.
            int slots = Math.Min(listing->TotalSlots, Math.Min(jobs.Length, ids.Length));

            int present = 0;
            int named = 0;

            for (int i = 0; i < slots; i++)
            {
                var slot = new ListingSlot { JobId = jobs[i] };

                ulong id = ids[i];
                if (id != 0)
                {
                    present++;

                    if (TryResolveName(id, out string name, out ushort world))
                    {
                        slot.Name = name;
                        slot.HomeWorldId = world;
                        named++;
                    }
                }

                snapshot.Slots.Add(slot);
            }

            snapshot.MemberCount = present;
            snapshot.NamedCount = named;

            // THE MEASUREMENT, and the reason this line exists at all. The listing hands over
            // content ids; whether this machine can put names to them decides whether naming a
            // party is possible locally or needs something none of us should be building. Open a
            // few listings and read the ratio - strangers are expected to be unnamed, and if that
            // is nearly all of them then local naming is not the answer.
            // INFORMATION, NOT DEBUG, AND THAT IS THE WHOLE REASON THIS LINE WORKS. Dalamud filters
            // Debug out by default, so every log.Debug in this plugin has been writing to nowhere -
            // which is why this measurement came back empty the first time and looked like the hook
            // had failed. It had not; the hook was fine and the log was silent.
            log.Information($"[Listing] {named}/{present} members named locally "
                + $"({snapshot.SlotsFilled}/{snapshot.TotalSlots} slots filled).");

            return snapshot;
        }

        public void Dispose()
        {
            try
            {
                hook?.Disable();
                hook?.Dispose();
            }
            catch (Exception ex)
            {
                log.Debug($"[Listing] Couldn't dispose the hook: {ex.Message}");
            }
            finally
            {
                hook = null;
                Current = null;
            }
        }
    }
}
