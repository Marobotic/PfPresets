using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Arrays;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace PfPresets
{
    /// <summary>
    /// Blacklisting somebody the game will not let a command name.
    ///
    /// <c>/blacklist add</c> only takes a placeholder - a party slot or your target - so it covers
    /// exactly one situation: a party the client counts as local, which is what you have inside a
    /// duty. Standing in a city in a cross-world party, every placeholder misses: the slots resolve
    /// against a party that isn't there, and the person may be two zones away.
    ///
    /// The route that always exists is the one a player would use by hand: Contacts, Party Members,
    /// right-click the name, Add to Blacklist, confirm. That is what this file drives, through the
    /// game's own windows and its own confirmation - nothing here blacklists anyone that a person
    /// sitting at the keyboard could not have blacklisted the same way.
    /// </summary>
    public partial class PfAutomation
    {
        /// <summary>One at a time. The flow drives shared game windows, so two of them interleaved
        /// would be clicking each other's context menus.</summary>
        private volatile bool contactsBlacklistRunning;

        /// <summary>
        /// Words that mark the "add to blacklist" entry in the player context menu.
        ///
        /// Matched on text because the entry has no id we can rely on, and matched in several
        /// languages because the client the plugin is running in is not necessarily English.
        /// </summary>
        private static readonly string[] BlacklistMenuWords =
        {
            "blacklist",     // en, de ("Zur Blacklist hinzufügen")
            "liste noire",   // fr
            "ブラックリスト", // ja
        };

        /// <summary>
        /// Words that mark the *removal* entry, which sits in the same menu and reads almost the
        /// same. Clicking it would quietly un-blacklist somebody, so a match here disqualifies an
        /// entry outright rather than merely ranking it lower.
        /// </summary>
        private static readonly string[] BlacklistRemoveWords =
        {
            "remove", "delete",   // en
            "entfernen", "löschen", // de
            "retirer", "supprimer", // fr
            "解除", "削除",          // ja
        };

        /// <summary>Frames to wait for a window or list the game is still building. Polled at 50ms,
        /// so these are two, three and one and a half seconds respectively.</summary>
        private const int ContactsWindowAttempts = 60;
        private const int ContactsRowAttempts = 60;
        private const int ContextMenuAttempts = 30;

        /// <summary>
        /// Starts the Contacts route for somebody the placeholder commands can't reach.
        /// </summary>
        /// <returns>False when it could not be started at all - another attempt is already running,
        /// or there is no name to look for. The work itself happens on a background task and
        /// reports its own outcome.</returns>
        private bool StartContactsBlacklist(string name, ulong contentId)
        {
            if (disposed || contactsBlacklistRunning || string.IsNullOrWhiteSpace(name))
                return false;

            contactsBlacklistRunning = true;

            Task.Run(async () =>
            {
                try
                {
                    await RunContactsBlacklistAsync(name, contentId);
                }
                catch (Exception ex)
                {
                    pluginLog.Error(ex, "[Blacklist] The Contacts route threw.");
                    ContactsFailed(name, "Something went wrong driving the Contacts window.");
                }
                finally
                {
                    contactsBlacklistRunning = false;
                }
            });

            return true;
        }

        /// <summary>
        /// Contacts -> Party Members -> right-click -> Add to Blacklist -> confirm, one step at a
        /// time, each waiting for the game to catch up.
        ///
        /// Every step logs what it saw before giving up, because the only thing worse than this
        /// failing is it failing silently: the person pressed a button that promised to block
        /// somebody, and "nothing happened" is not an answer they can act on.
        /// </summary>
        private async Task RunContactsBlacklistAsync(string name, ulong contentId)
        {
            pluginLog.Information($"[Blacklist] Contacts route for '{name}'.");

            // Remembered so the window can be put back the way it was found. Somebody who already
            // had Contacts open was using it; somebody who did not should not be left with a window
            // they never opened.
            bool wasOpen = await framework.RunOnFrameworkThread(() => IsContactsOpen());

            if (!await OpenContactsPartyMembersAsync())
            {
                ContactsFailed(name, "Couldn't open the Contacts window.");
                return;
            }

            int row = await FindContactsRowAsync(name);
            if (row < 0)
            {
                ContactsFailed(name,
                    $"Couldn't find {name} in Contacts -> Party Members.");
                return;
            }

            if (!await OpenRowContextMenuAsync(row))
            {
                ContactsFailed(name, "The right-click menu didn't open.");
                return;
            }

            if (!await SelectBlacklistMenuEntryAsync())
            {
                ContactsFailed(name, "The menu had no \"Add to Blacklist\" entry.");
                return;
            }

            // The game asks before blocking somebody. Answering it here is the same yes the player
            // would give; the question itself is still the game's.
            await ConfirmYesNoPromptAsync("[Blacklist]");

            bool blocked = await WaitForBlacklistedAsync(contentId);

            if (!blocked && contentId != 0)
            {
                // The steps all ran but the game never reported them blocked. Saying "done" here
                // would be a guess, and the whole point of this feature is that it is not one.
                // The window stays open, since finishing it by hand is now one right-click.
                ContactsFailed(name, "The game didn't report them as blocked.");
                return;
            }

            // Put the window back the way it was found, and only now: a Contacts window closing on
            // a failure would take the player's way of finishing the job with it.
            if (!wasOpen)
                await framework.RunOnFrameworkThread(CloseContacts);

            chatGui.Print($"[PF Analysis] Blacklisted {name}.");
            pluginLog.Information($"[Blacklist] Contacts route finished for '{name}'.");
        }

        /// <summary>Says what went wrong and what to do instead. The window is deliberately left
        /// open on failure: whatever step broke, the player is now one right-click from finishing
        /// the job by hand.</summary>
        private void ContactsFailed(string name, string why)
        {
            pluginLog.Warning($"[Blacklist] {why} ({name})");
            chatGui.Print($"[PF Analysis] {why} Right-click {name} in Contacts -> Party Members "
                + "and choose Add to Blacklist.");
        }

        // ── The window ────────────────────────────────────────────

        /// <summary>True while the game's Contacts window is up.</summary>
        private unsafe bool IsContactsOpen()
        {
            var addon = (AtkUnitBase*)(nint)gameGui.GetAddonByName("Social");
            return addon != null && addon->IsVisible;
        }

        /// <summary>
        /// Opens Contacts on the Party Members tab.
        ///
        /// Asks the agent rather than typing a command: <c>AgentId.PartyMember</c> is the tab
        /// itself, so showing it opens the window already on the right list. The radio button is
        /// the fallback for the case where the window was already open on another tab, which
        /// showing an already-active agent does not change.
        /// </summary>
        private async Task<bool> OpenContactsPartyMembersAsync()
        {
            await framework.RunOnFrameworkThread(() =>
            {
                unsafe
                {
                    var agent = GetAgent(AgentId.PartyMember);
                    if (agent != null)
                        agent->Show();
                }
            });

            for (int attempt = 0; attempt < ContactsWindowAttempts; attempt++)
            {
                if (disposed) return false;

                bool ready = await framework.RunOnFrameworkThread(() =>
                {
                    unsafe
                    {
                        // Halfway through, press the tab itself. An already-open Contacts window
                        // sitting on Friend List ignores the agent being shown again, and no amount
                        // of waiting will move it.
                        if (attempt == ContactsWindowAttempts / 2)
                            ClickPartyMembersTab();

                        return GetPartyMemberListAddon() != null;
                    }
                });

                if (ready)
                    return true;

                await Task.Delay(50);
            }

            LogLoadedAddons();
            return false;
        }

        /// <summary>Presses the Party Members radio button on the Contacts window.</summary>
        private unsafe void ClickPartyMembersTab()
        {
            var social = (AddonSocial*)(nint)gameGui.GetAddonByName("Social");
            if (social == null || social->PartyMembersRadioButton == null)
                return;

            pluginLog.Debug("[Blacklist] Pressing the Party Members tab.");
            AtkHelpers.ClickAddonButton((AtkUnitBase*)social,
                &social->PartyMembersRadioButton->AtkComponentButton);
        }

        /// <summary>Closes Contacts again, through the agent that owns it.</summary>
        private unsafe void CloseContacts()
        {
            var social = GetAgent(AgentId.Social);
            if (social != null && social->IsAgentActive())
                social->Hide();
        }

        /// <summary>
        /// The addon holding the party-member list.
        ///
        /// Found through the agent's own addon id rather than by name: the tabs of the Contacts
        /// window are separate addons whose names are not ours to assume, and the agent knows which
        /// one it is currently driving.
        /// </summary>
        private unsafe AtkUnitBase* GetPartyMemberListAddon()
        {
            var agent = GetAgent(AgentId.PartyMember);
            if (agent == null || !agent->IsAgentActive())
                return null;

            uint addonId = agent->GetAddonId();
            if (addonId == 0)
                return null;

            var manager = RaptureAtkUnitManager.Instance();
            if (manager == null)
                return null;

            var addon = manager->GetAddonById((ushort)addonId);
            return addon != null && addon->IsVisible ? addon : null;
        }

        private unsafe AgentInterface* GetAgent(AgentId id)
        {
            var module = AgentModule.Instance();
            return module == null ? null : module->GetAgentByInternalId(id);
        }

        /// <summary>Every loaded window, by name. Only written when something could not be found,
        /// and it is the one thing that makes a renamed addon diagnosable from a user's log.</summary>
        private unsafe void LogLoadedAddons()
        {
            try
            {
                var manager = RaptureAtkUnitManager.Instance();
                if (manager == null)
                    return;

                var names = new List<string>();
                var list = &manager->AllLoadedUnitsList;
                for (int i = 0; i < list->Count && i < 200; i++)
                {
                    var unit = list->Entries[i].Value;
                    if (unit == null || !unit->IsVisible)
                        continue;
                    names.Add(unit->NameString);
                }

                pluginLog.Warning($"[Blacklist] Visible addons: {string.Join(", ", names)}");
            }
            catch (Exception ex)
            {
                pluginLog.Debug($"[Blacklist] Could not list addons: {ex.Message}");
            }
        }

        // ── The row ───────────────────────────────────────────────

        /// <summary>
        /// The list row for this player, or -1.
        ///
        /// Names come from the string array the list is drawn from rather than from the row nodes:
        /// only visible rows have nodes, so a party member scrolled out of sight would be invisible
        /// to a node walk and reported as missing.
        /// </summary>
        private async Task<int> FindContactsRowAsync(string name)
        {
            for (int attempt = 0; attempt < ContactsRowAttempts; attempt++)
            {
                if (disposed) return -1;

                int row = await framework.RunOnFrameworkThread(() => FindContactsRow(name));
                if (row >= 0)
                    return row;

                await Task.Delay(50);
            }

            await framework.RunOnFrameworkThread(() => LogContactsRows());
            return -1;
        }

        private unsafe int FindContactsRow(string name)
        {
            var strings = SocialListStringArray.Instance();
            var numbers = SocialListNumberArray.Instance();
            if (strings == null || numbers == null)
                return -1;

            int count = Math.Min(numbers->SocialListSize, strings->Friends.Length);

            for (int i = 0; i < count; i++)
            {
                string entry = strings->Friends[i].PlayerName.ToString() ?? string.Empty;
                if (entry.Equals(name, StringComparison.OrdinalIgnoreCase))
                    return i;
            }

            return -1;
        }

        /// <summary>What the list actually held, for when the name we wanted wasn't in it.</summary>
        private unsafe void LogContactsRows()
        {
            try
            {
                var strings = SocialListStringArray.Instance();
                var numbers = SocialListNumberArray.Instance();
                if (strings == null || numbers == null)
                    return;

                var names = new List<string>();
                int count = Math.Min(numbers->SocialListSize, strings->Friends.Length);
                for (int i = 0; i < count; i++)
                    names.Add(strings->Friends[i].PlayerName.ToString() ?? "?");

                pluginLog.Warning($"[Blacklist] Party Members list held: {string.Join(", ", names)}");
            }
            catch (Exception ex)
            {
                pluginLog.Debug($"[Blacklist] Could not read the list: {ex.Message}");
            }
        }

        // ── The menu ──────────────────────────────────────────────

        /// <summary>Right-clicks the row and waits for the context menu the game builds from it.</summary>
        private async Task<bool> OpenRowContextMenuAsync(int row)
        {
            await framework.RunOnFrameworkThread(() =>
            {
                unsafe
                {
                    var addon = GetPartyMemberListAddon();
                    if (addon == null)
                        return;

                    var list = FindComponentList(addon, out uint nodeId);
                    if (list == null)
                    {
                        pluginLog.Warning("[Blacklist] The party-member window has no list component.");
                        return;
                    }

                    ClickListItem(addon, list, nodeId, row, rightClick: true);
                }
            });

            for (int attempt = 0; attempt < ContextMenuAttempts; attempt++)
            {
                if (disposed) return false;

                bool open = await framework.RunOnFrameworkThread(() =>
                {
                    unsafe
                    {
                        var menu = (AtkUnitBase*)(nint)gameGui.GetAddonByName("ContextMenu");
                        return menu != null && menu->IsVisible;
                    }
                });

                if (open)
                    return true;

                await Task.Delay(50);
            }

            return false;
        }

        /// <summary>
        /// Finds "Add to Blacklist" in the open context menu and presses it.
        ///
        /// Read from the menu rather than assumed at a fixed position: what the game offers depends
        /// on who the person is - a friend, a free company member, somebody in your alliance - and
        /// the entry moves accordingly.
        /// </summary>
        private async Task<bool> SelectBlacklistMenuEntryAsync()
        {
            return await framework.RunOnFrameworkThread(() =>
            {
                unsafe
                {
                    var menu = (AtkUnitBase*)(nint)gameGui.GetAddonByName("ContextMenu");
                    if (menu == null || !menu->IsVisible)
                        return false;

                    var list = FindComponentList(menu, out uint nodeId);
                    if (list == null)
                        return false;

                    int count = list->GetItemCount();
                    var seen = new List<string>(count);
                    int found = -1;

                    for (int i = 0; i < count; i++)
                    {
                        string label = AtkHelpers.GetDropDownItemLabel(list, i);
                        seen.Add(label);

                        if (found < 0 && IsAddToBlacklistLabel(label))
                            found = i;
                    }

                    if (found < 0)
                    {
                        pluginLog.Warning($"[Blacklist] Menu entries: {string.Join(" | ", seen)}");
                        return false;
                    }

                    pluginLog.Information($"[Blacklist] Pressing menu entry {found} ('{seen[found]}').");
                    return ClickListItem(menu, list, nodeId, found, rightClick: false);
                }
            });
        }

        /// <summary>True for the entry that adds somebody, and deliberately false for the one that
        /// removes them - both read as "…blacklist" and only one of them is wanted.</summary>
        private static bool IsAddToBlacklistLabel(string label)
        {
            if (string.IsNullOrWhiteSpace(label))
                return false;

            bool mentions = false;
            foreach (var word in BlacklistMenuWords)
            {
                if (label.Contains(word, StringComparison.OrdinalIgnoreCase))
                {
                    mentions = true;
                    break;
                }
            }

            if (!mentions)
                return false;

            foreach (var word in BlacklistRemoveWords)
            {
                if (label.Contains(word, StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            return true;
        }

        // ── Native list plumbing ──────────────────────────────────

        /// <summary>
        /// The first list component in a window that currently holds rows.
        ///
        /// By component type rather than by node id: ids move between patches, and "the list" is
        /// unambiguous in both windows this is used on.
        /// </summary>
        private static unsafe AtkComponentList* FindComponentList(AtkUnitBase* addon, out uint nodeId)
        {
            nodeId = 0;
            if (addon == null)
                return null;

            var uld = addon->UldManager;
            for (int i = 0; i < uld.NodeListCount; i++)
            {
                var node = uld.NodeList[i];
                if (node == null || (ushort)node->Type < 1000)
                    continue;

                var component = ((AtkComponentNode*)node)->Component;
                if (component == null || component->GetComponentType() != ComponentType.List)
                    continue;

                var list = (AtkComponentList*)component;
                if (list->GetItemCount() <= 0)
                    continue;

                nodeId = node->NodeId;
                return list;
            }

            return null;
        }

        /// <summary>
        /// Clicks a row the way the mouse would: the list's own click event, carrying which row and
        /// which mouse button, handed to the window that owns the list.
        ///
        /// The button matters more than usual here - the right button is the entire difference
        /// between selecting a party member and being offered the menu that can block them.
        /// </summary>
        private static unsafe bool ClickListItem(AtkUnitBase* addon, AtkComponentList* list,
            uint nodeId, int index, bool rightClick)
        {
            if (addon == null || list == null || index < 0 || index >= list->GetItemCount())
                return false;

            var ownerNode = list->AtkComponentBase.OwnerNode;
            var resNode = ownerNode != null ? &ownerNode->AtkResNode : null;
            if (resNode == null)
                return false;

            // The row has to be the selected one before the menu is asked for, or the game builds
            // the menu around whatever was selected before.
            list->SelectItem(index, false);

            var evt = new AtkEvent
            {
                Node = resNode,
                Target = (AtkEventTarget*)&resNode->AtkEventTarget,
                Listener = (AtkEventListener*)addon,
                Param = nodeId,
            };
            evt.State.EventType = AtkEventType.ListItemClick;
            evt.State.ReturnFlags = 0;
            evt.State.StateFlags = 0;

            var data = new AtkEventData();
            data.ListItemData.ListItemRenderer = list->GetItemRenderer(index);
            data.ListItemData.SelectedIndex = index;
            data.ListItemData.MouseButtonId = (byte)(rightClick ? 1 : 0);

            addon->ReceiveEvent(AtkEventType.ListItemClick, (int)nodeId, &evt, &data);
            return true;
        }

        // ── Waiting on the game ───────────────────────────────────

        /// <summary>Answers the game's yes/no prompt. Shared with end-recruitment, which asks the
        /// same question through the same window.</summary>
        private async Task ConfirmYesNoPromptAsync(string tag)
        {
            for (int attempt = 0; attempt < 20; attempt++)
            {
                if (disposed) return;

                bool confirmed = await framework.RunOnFrameworkThread(() =>
                {
                    unsafe
                    {
                        var addonPtr = (nint)gameGui.GetAddonByName("SelectYesno");
                        if (addonPtr == IntPtr.Zero) return false;

                        var addon = (AtkUnitBase*)addonPtr;
                        if (!addon->IsVisible) return false;

                        var yesno = (AddonSelectYesno*)addonPtr;
                        if (yesno->YesButton == null || !yesno->YesButton->IsEnabled)
                            return false;

                        return AtkHelpers.ClickAddonButton(addon, yesno->YesButton);
                    }
                });

                if (confirmed)
                {
                    pluginLog.Information($"{tag} Confirmed the prompt.");
                    return;
                }

                await Task.Delay(50);
            }

            pluginLog.Warning($"{tag} No confirmation prompt appeared.");
        }

        /// <summary>
        /// Waits for the game to actually report them blocked.
        ///
        /// This is the only honest confirmation available: every step before it can succeed against
        /// a window that then refuses the action, and the plugin has no business telling somebody
        /// they are protected from a player who isn't blocked.
        /// </summary>
        private async Task<bool> WaitForBlacklistedAsync(ulong contentId)
        {
            if (contentId == 0)
                return false;

            for (int attempt = 0; attempt < 40; attempt++)
            {
                if (disposed) return false;

                if (await framework.RunOnFrameworkThread(() => IsBlacklisted(contentId)))
                    return true;

                await Task.Delay(50);
            }

            return false;
        }
    }
}
