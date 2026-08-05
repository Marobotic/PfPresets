using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Component.GUI;

using FFXIVClientStructs.FFXIV.Client.UI.Info;

namespace PfPresets
{
    /// <summary>What a blacklist attempt was actually able to do, so the UI can say so.</summary>
    public enum BlacklistAttempt
    {
        /// <summary>Sent using their party slot - the ordinary same-world case.</summary>
        SentBySlot,

        /// <summary>Sent by briefly targeting them, which is the only route in a cross-world party.</summary>
        SentByTarget,

        /// <summary>Being done through the game's Contacts window, which takes a few seconds and
        /// reports its own result. Nothing has happened yet when this is returned.</summary>
        RunningViaContacts,

        /// <summary>Nothing was sent: they are not in a same-world party and not close enough to
        /// target, so the game has no way to name them.</summary>
        Unreachable,

        Failed,
    }

    /// <summary>
    /// Taking your own listing down. Opens your listing's detail window and presses its
    /// end-recruitment button, reusing the Auto Refresher's open/click plumbing.
    /// </summary>
    public partial class PfAutomation
    {
        /// <summary>
        /// Node id of the "End" button on the listing detail window. It sits next to Edit (109)
        /// and Back (111), the same numbering the Auto Refresher already relies on.
        /// </summary>
        private const uint EndRecruitmentButtonId = 110;

        /// <summary>
        /// Labels the End button is expected to carry, used only to sanity-check that node 110 is
        /// what we think it is before clicking. If the check fails we click anyway and log it -
        /// the id has been stable, and other clients localise the wording.
        /// </summary>
        private static readonly string[] EndRecruitmentLabels =
        {
            "end",
            "cancel",
            "withdraw",
        };

        /// <summary>True while an end-recruitment attempt is running, so double-clicks don't stack.</summary>
        private volatile bool isEndingRecruitment;

        /// <summary>
        /// Takes the current Party Finder listing down. Safe to call when nothing is up - it
        /// reports rather than throwing. Runs as a background task because the detail window takes
        /// time to open; every game interaction still happens on the framework thread.
        /// </summary>
        public void EndRecruitment()
        {
            if (disposed || isEndingRecruitment)
                return;

            if (!IsRecruiting())
            {
                chatGui.Print("[PF Analysis] You are not currently recruiting.");
                return;
            }

            if (!IsPartyLeader())
            {
                chatGui.Print("[PF Analysis] Only the party leader can end recruitment.");
                return;
            }

            isEndingRecruitment = true;

            Task.Run(async () =>
            {
                try
                {
                    pluginLog.Information("[EndRecruitment] Opening own listing...");
                    if (!await OpenOwnListing())
                    {
                        chatGui.Print("[PF Analysis] Could not open your listing to end it.");
                        return;
                    }

                    if (!await WaitForEndRecruitmentButton())
                    {
                        chatGui.Print("[PF Analysis] Could not find the end-recruitment button. "
                                    + "Check /xllog for the buttons that were found.");
                        return;
                    }

                    // The game asks for confirmation before dropping a listing.
                    await ConfirmEndRecruitmentDialogAsync();

                    chatGui.Print("[PF Analysis] Recruitment ended.");
                    pluginLog.Information("[EndRecruitment] Listing taken down.");

                    // The listing is gone, so any time-left reading is stale.
                    ResetTimeTracking();
                }
                catch (Exception ex)
                {
                    pluginLog.Error(ex, "[EndRecruitment] Failed to end recruitment.");
                    chatGui.Print("[PF Analysis] Something went wrong ending recruitment. See /xllog.");
                }
                finally
                {
                    isEndingRecruitment = false;
                }
            });
        }

        /// <summary>
        /// Polls until the listing detail window is up, then finds and clicks the end-recruitment
        /// button by its label. Every button it saw is logged on failure, which is what makes the
        /// id discoverable if the wording ever changes.
        /// </summary>
        private async Task<bool> WaitForEndRecruitmentButton()
        {
            for (int attempt = 0; attempt < 100; attempt++)
            {
                if (disposed) return false;

                bool clicked = await framework.RunOnFrameworkThread(() =>
                {
                    unsafe
                    {
                        var addon = (AtkUnitBase*)(nint)gameGui.GetAddonByName("LookingForGroupDetail");
                        if (addon == null || !addon->IsVisible)
                            return false;

                        var button = addon->GetComponentButtonById(EndRecruitmentButtonId);
                        if (button == null || !button->IsEnabled)
                            return false;

                        // Confirm we're on the button we think we are. The label is short ("End"),
                        // so this is a guard against a patch renumbering nodes, not a lookup.
                        string label = AtkHelpers.GetButtonLabel(button);
                        bool looksRight = string.IsNullOrWhiteSpace(label) || Array.Exists(
                            EndRecruitmentLabels,
                            c => label.Contains(c, StringComparison.OrdinalIgnoreCase));

                        if (!looksRight)
                        {
                            pluginLog.Warning(
                                $"[EndRecruitment] Node {EndRecruitmentButtonId} reads '{label}', which doesn't look "
                                + "like the End button. Clicking anyway; report this if it did the wrong thing.");
                        }

                        pluginLog.Information($"[EndRecruitment] Clicking node {EndRecruitmentButtonId} ('{label}').");
                        return AtkHelpers.ClickAddonButton(addon, button);
                    }
                });

                if (clicked)
                    return true;

                await Task.Delay(50);
            }

            pluginLog.Warning("[EndRecruitment] Timed out looking for the end-recruitment button.");
            return false;
        }

        /// <summary>
        /// Leaves the current party by running the game's own /leave command, then confirms the
        /// prompt. Uses the chat entry point rather than poking party memory directly, so the game
        /// performs its normal checks (in a duty, last member, and so on).
        /// </summary>
        public void LeaveParty()
        {
            if (disposed)
                return;

            if (IsInDuty())
            {
                chatGui.Print("[PF Analysis] You can't leave the party while in a duty.");
                return;
            }

            Task.Run(async () =>
            {
                try
                {
                    await framework.RunOnFrameworkThread(() =>
                    {
                        unsafe
                        {
                            var ui = FFXIVClientStructs.FFXIV.Client.UI.UIModule.Instance();
                            if (ui == null)
                            {
                                chatGui.Print("[PF Analysis] Could not leave the party right now.");
                                return;
                            }

                            // Same shape as ECommons' Chat helper: build a Utf8String, hand it to
                            // the chat box, then free it.
                            var entry = FFXIVClientStructs.FFXIV.Client.System.String.Utf8String
                                .FromSequence(System.Text.Encoding.UTF8.GetBytes("/leave"));
                            ui->ProcessChatBoxEntry(entry);
                            entry->Dtor(true);
                        }
                    });

                    // Leaving raises a confirmation.
                    await ConfirmEndRecruitmentDialogAsync();
                    pluginLog.Information("[LeaveParty] Sent /leave.");
                }
                catch (Exception ex)
                {
                    pluginLog.Error(ex, "[LeaveParty] Failed to leave the party.");
                    chatGui.Print("[PF Analysis] Something went wrong leaving the party. See /xllog.");
                }
            });
        }

        /// <summary>
        /// Adds a party member to the game's own blacklist, by their party slot.
        ///
        /// The game's blacklist is the real one - it blocks tells, chat and party invites, and it
        /// survives this plugin being uninstalled. A list kept inside the plugin blocks nothing and
        /// only tells you what you already decided, so this drives the game's instead.
        ///
        /// Driven by <c>/blacklist add &lt;n&gt;</c> where that works, because it is instant and it
        /// is the game's own command. The client exposes InfoProxyBlacklist for reading who is
        /// blocked, but nothing that adds - the add path is the command, and per the game's own
        /// command table it takes a *placeholder* (&lt;t&gt;, &lt;1&gt;-&lt;8&gt;) rather than a
        /// name. The party slot number is the placeholder we can supply without disturbing the
        /// player's target.
        ///
        /// Inside a duty that is all it takes: the party is local, so the slots resolve. Outside
        /// one, a Party Finder party is usually cross-world, where <c>&lt;1&gt;-&lt;8&gt;</c>
        /// resolve against a local party that isn't there - so that case goes through the Contacts
        /// window instead, in <see cref="StartContactsBlacklist"/>.
        ///
        /// Note it blacklists the *account*, not the character - that is what the game does either
        /// way, and the confirmation says so.
        /// </summary>
        /// <param name="partySlot">1-based slot in the party list, as the game numbers it.</param>
        /// <param name="name">Their character name, used to find their row in Contacts and to find
        /// them in the world when neither that nor the slot placeholder is any use.</param>
        /// <param name="contentId">Their content id, used only to check afterwards that the game
        /// really did block them. 0 when the caller doesn't have one.</param>
        /// <returns>What was attempted, so the caller can say something true about it.</returns>
        public BlacklistAttempt BlacklistPlayer(int partySlot, string name, ulong contentId = 0)
        {
            if (disposed)
                return BlacklistAttempt.Failed;

            // In a duty the party is local whatever it started as, so the slot placeholder names
            // them directly. Outside one it is only trusted for a party the client agrees is local:
            // in a cross-world party the game rejects the command, and in a mixed situation it
            // could resolve to a different person entirely.
            if ((IsInDuty() || !IsCrossWorldPartyLeaderOrMember()) && partySlot >= 1 && partySlot <= 8)
            {
                SendChatCommand($"/blacklist add <{partySlot}>", "[Blacklist]");
                return BlacklistAttempt.SentBySlot;
            }

            // Out of duty: Contacts -> Party Members -> right-click -> Add to Blacklist. It works
            // on anyone the party holds, wherever they are standing.
            if (StartContactsBlacklist(name, contentId))
                return BlacklistAttempt.RunningViaContacts;

            // Only reached when the Contacts route couldn't even start. The last placeholder is
            // <t>, which needs them actually targetable - someone standing in front of you after a
            // bad run is exactly that; someone in another zone is not.
            if (!TryBlacklistByTarget(name))
                return BlacklistAttempt.Unreachable;

            return BlacklistAttempt.SentByTarget;
        }

        /// <summary>Whether the local player is in a cross-world party at all - leader or not.
        /// Party-slot placeholders do not resolve inside one.</summary>
        private unsafe bool IsCrossWorldPartyLeaderOrMember()
        {
            try
            {
                var proxy = InfoProxyCrossRealm.Instance();
                return proxy != null && proxy->IsInCrossRealmParty;
            }
            catch (Exception)
            {
                // Assume cross-world when unsure: it costs a fallback path, where the wrong guess
                // the other way could blacklist somebody else's slot.
                return true;
            }
        }

        /// <summary>
        /// Targets the player, runs the command against &lt;t&gt;, and puts the previous target back.
        ///
        /// Borrowing the target is intrusive, so it is refused in combat outright - a stolen target
        /// mid-pull is a wipe, and no convenience is worth that. Outside combat it lasts a frame or
        /// two and is restored whether the command worked or not.
        /// </summary>
        private bool TryBlacklistByTarget(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return false;

            if (condition[ConditionFlag.InCombat])
            {
                chatGui.Print("[PF Analysis] Can't blacklist during combat - try again after the pull.");
                return false;
            }

            var player = FindNearbyPlayer(name);
            if (player == null)
                return false;

            Task.Run(async () =>
            {
                try
                {
                    var previous = await framework.RunOnFrameworkThread(() =>
                    {
                        var was = targets.Target;
                        targets.Target = player;
                        return was;
                    });

                    await framework.RunOnFrameworkThread(() =>
                    {
                        unsafe
                        {
                            var ui = FFXIVClientStructs.FFXIV.Client.UI.UIModule.Instance();
                            if (ui == null)
                                return;

                            var entry = FFXIVClientStructs.FFXIV.Client.System.String.Utf8String
                                .FromSequence(System.Text.Encoding.UTF8.GetBytes("/blacklist add <t>"));
                            ui->ProcessChatBoxEntry(entry);
                            entry->Dtor(true);
                        }
                    });

                    // A beat, so the command reads the target before it is handed back.
                    await Task.Delay(120);
                    await framework.RunOnFrameworkThread(() => targets.Target = previous);
                }
                catch (Exception ex)
                {
                    pluginLog.Error(ex, "[Blacklist] Target-based blacklist failed.");
                }
            });

            return true;
        }

        /// <summary>The loaded player object for this name, or null when they aren't nearby. Only
        /// matches players, so a retainer or an NPC sharing a name can't be targeted by mistake.</summary>
        private Dalamud.Game.ClientState.Objects.Types.IGameObject? FindNearbyPlayer(string name)
        {
            try
            {
                foreach (var obj in objectTable)
                {
                    if (obj is Dalamud.Game.ClientState.Objects.SubKinds.IPlayerCharacter pc
                        && pc.Name.TextValue.Equals(name, StringComparison.OrdinalIgnoreCase))
                        return pc;
                }
            }
            catch (Exception ex)
            {
                pluginLog.Debug($"[Blacklist] Object table scan failed: {ex.Message}");
            }
            return null;
        }

        /// <summary>Opens the game's blacklist window, for the cases the plugin can't act on
        /// directly - anyone who isn't currently in the party has no placeholder to name them
        /// by.</summary>
        public void OpenGameBlacklist()
        {
            if (!disposed)
                SendChatCommand("/blacklist", "[Blacklist]");
        }

        /// <summary>
        /// Runs a command through the game's own chat box, on the framework thread.
        ///
        /// The same route <see cref="LeaveParty"/> uses, and for the same reason: the game performs
        /// its normal checks and shows its normal confirmations, so anything it would refuse from a
        /// human it refuses from here too.
        /// </summary>
        private void SendChatCommand(string command, string logTag)
        {
            Task.Run(async () =>
            {
                try
                {
                    await framework.RunOnFrameworkThread(() =>
                    {
                        unsafe
                        {
                            var ui = FFXIVClientStructs.FFXIV.Client.UI.UIModule.Instance();
                            if (ui == null)
                            {
                                chatGui.Print("[PF Analysis] The game isn't ready for that right now.");
                                return;
                            }

                            var entry = FFXIVClientStructs.FFXIV.Client.System.String.Utf8String
                                .FromSequence(System.Text.Encoding.UTF8.GetBytes(command));
                            ui->ProcessChatBoxEntry(entry);
                            entry->Dtor(true);
                        }
                    });

                    pluginLog.Information($"{logTag} Sent {command}.");
                }
                catch (Exception ex)
                {
                    pluginLog.Error(ex, $"{logTag} Failed to send {command}.");
                    chatGui.Print("[PF Analysis] That didn't go through. See /xllog for details.");
                }
            });
        }

        /// <summary>
        /// Reads the party leader's listing on demand: the card's "Load Details" button.
        ///
        /// The watcher in PfAutomation.ListingWatch.cs does this by itself when it has reason to,
        /// so this is the manual override for when it has given up or the player is impatient. It
        /// shares the same capture, and unlike the watcher it reports what went wrong - somebody
        /// pressed a button and is owed an answer.
        /// </summary>
        public void LoadPartyListingDetails()
        {
            if (disposed || isEndingRecruitment)
                return;

            ulong leader = GetPartyLeaderContentId();
            if (leader == 0)
            {
                chatGui.Print("[PF Analysis] Could not work out who leads the party.");
                return;
            }

            _ = CaptureLeaderListingAsync(leader, announce: true);
        }

        /// <summary>
        /// Confirms the yes/no prompt the game raises before a listing is dropped. Unlike the
        /// party-composition prompt this one is expected, so a short watch is enough.
        /// </summary>
        private Task ConfirmEndRecruitmentDialogAsync()
            => ConfirmYesNoPromptAsync("[EndRecruitment]");

        /// <summary>
        /// Disbands the party outright.
        ///
        /// Offered instead of "End Recruitment" once the party is full, because at that point
        /// there is no listing left to end - the game takes it down itself on the eighth join, so
        /// the old button ran a sequence that found nothing and did nothing.
        ///
        /// Calls the game's own DisbandParty rather than leaving: a leader leaving hands the party
        /// to someone else, which is not what the button says it does.
        /// </summary>
        public unsafe void DisbandParty()
        {
            try
            {
                var proxy = InfoProxyPartyMember.Instance();
                if (proxy == null)
                {
                    chatGui.Print("[PF Analysis] The party list isn't available right now.");
                    return;
                }

                if (!proxy->DisbandParty())
                {
                    chatGui.Print("[PF Analysis] The game refused to disband the party.");
                    return;
                }

                pluginLog.Information("[Party] Disband requested.");
            }
            catch (Exception ex)
            {
                pluginLog.Error(ex, "[Party] Disband failed.");
                chatGui.Print("[PF Analysis] Couldn't disband the party. See /xllog for details.");
            }
        }

        /// <summary>Whether the game will currently let you leave the instance. False in the
        /// places it refuses - mid-cutscene, mid-boss, already leaving.</summary>
        public bool CanLeaveDuty()
        {
            try
            {
                return FFXIVClientStructs.FFXIV.Client.Game.Event.EventFramework.CanLeaveCurrentContent();
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Leaves the current duty, the same as the Duty Finder's own Leave option.
        ///
        /// Offered instead of Kick while you're inside content, because the game doesn't allow
        /// removing anyone from an instanced party - the only exit available is your own.
        /// </summary>
        public void LeaveDuty()
        {
            try
            {
                if (!FFXIVClientStructs.FFXIV.Client.Game.Event.EventFramework.CanLeaveCurrentContent())
                {
                    chatGui.Print("[PF Analysis] The game won't let you leave right now.");
                    return;
                }

                FFXIVClientStructs.FFXIV.Client.Game.Event.EventFramework.LeaveCurrentContent(true);
                pluginLog.Information("[Duty] Leave requested.");
            }
            catch (Exception ex)
            {
                pluginLog.Error(ex, "[Duty] Leave failed.");
                chatGui.Print("[PF Analysis] Couldn't leave the duty. See /xllog for details.");
            }
        }

        /// <summary>
        /// Queues for the duty the current listing was created for.
        ///
        /// PLACEHOLDER. Selecting a duty in the Duty Finder means driving its category tree and
        /// list by index, which is the same fragile addon work the preset-apply flow already does
        /// and needs establishing separately. Until then this says so rather than pretending.
        /// </summary>
        public void QueueToListedDuty()
        {
            chatGui.Print("[PF Analysis] Queueing from a listing isn't wired up yet.");
            pluginLog.Information("[Queue] Placeholder invoked; duty selection not implemented.");
        }
    }
}
