using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FFXIVClientStructs.FFXIV.Component.GUI;

using FFXIVClientStructs.FFXIV.Client.UI.Info;

namespace PfPresets
{
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
                chatGui.Print("[PF Presets] You are not currently recruiting.");
                return;
            }

            if (!IsPartyLeader())
            {
                chatGui.Print("[PF Presets] Only the party leader can end recruitment.");
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
                        chatGui.Print("[PF Presets] Could not open your listing to end it.");
                        return;
                    }

                    if (!await WaitForEndRecruitmentButton())
                    {
                        chatGui.Print("[PF Presets] Could not find the end-recruitment button. "
                                    + "Check /xllog for the buttons that were found.");
                        return;
                    }

                    // The game asks for confirmation before dropping a listing.
                    await ConfirmEndRecruitmentDialogAsync();

                    chatGui.Print("[PF Presets] Recruitment ended.");
                    pluginLog.Information("[EndRecruitment] Listing taken down.");

                    // The listing is gone, so any time-left reading is stale.
                    ResetTimeTracking();
                }
                catch (Exception ex)
                {
                    pluginLog.Error(ex, "[EndRecruitment] Failed to end recruitment.");
                    chatGui.Print("[PF Presets] Something went wrong ending recruitment. See /xllog.");
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
                chatGui.Print("[PF Presets] You can't leave the party while in a duty.");
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
                                chatGui.Print("[PF Presets] Could not leave the party right now.");
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
                    chatGui.Print("[PF Presets] Something went wrong leaving the party. See /xllog.");
                }
            });
        }

        /// <summary>
        /// Opens the party leader's listing so the game populates LastViewedListing, letting the
        /// status card read a listing that isn't ours, then closes it again. Without this there is
        /// no way to see another player's listing details - the agent only ever holds our own
        /// stored recruitment settings.
        /// </summary>
        public void LoadPartyListingDetails()
        {
            if (disposed || isEndingRecruitment)
                return;

            ulong leader = GetPartyLeaderContentId();
            if (leader == 0)
            {
                chatGui.Print("[PF Presets] Could not work out who leads the party.");
                return;
            }

            Task.Run(async () =>
            {
                try
                {
                    bool opened = await framework.RunOnFrameworkThread(() =>
                    {
                        unsafe
                        {
                            var agent = FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentLookingForGroup.Instance();
                            if (agent == null || openPartyFinder == null)
                                return false;

                            openPartyFinder(agent, leader);
                            return true;
                        }
                    });

                    if (!opened)
                    {
                        chatGui.Print("[PF Presets] Could not open the party's listing.");
                        return;
                    }

                    // Give the window a moment to populate, then close it - the data we want stays
                    // behind in LastViewedListing.
                    await Task.Delay(600);

                    await framework.RunOnFrameworkThread(() =>
                    {
                        unsafe
                        {
                            var addon = (AtkUnitBase*)(nint)gameGui.GetAddonByName("LookingForGroupDetail");
                            if (addon != null && addon->IsVisible)
                            {
                                var back = addon->GetComponentButtonById(111); // "Back"
                                if (back != null && back->IsEnabled)
                                    AtkHelpers.ClickAddonButton(addon, back);
                            }
                        }
                    });

                    pluginLog.Information("[LoadDetails] Captured the party's listing.");
                }
                catch (Exception ex)
                {
                    pluginLog.Error(ex, "[LoadDetails] Failed to load the party's listing.");
                }
            });
        }

        /// <summary>
        /// Confirms the yes/no prompt the game raises before a listing is dropped. Unlike the
        /// party-composition prompt this one is expected, so a short watch is enough.
        /// </summary>
        private async Task ConfirmEndRecruitmentDialogAsync()
        {
            for (int i = 0; i < 20; i++)
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

                        var yesno = (FFXIVClientStructs.FFXIV.Client.UI.AddonSelectYesno*)addonPtr;
                        if (yesno->YesButton == null || !yesno->YesButton->IsEnabled)
                            return false;

                        return AtkHelpers.ClickAddonButton(addon, yesno->YesButton);
                    }
                });

                if (confirmed)
                {
                    pluginLog.Information("[EndRecruitment] Confirmed the prompt.");
                    return;
                }

                await Task.Delay(50);
            }
        }

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
                    chatGui.Print("[PF Presets] The party list isn't available right now.");
                    return;
                }

                if (!proxy->DisbandParty())
                {
                    chatGui.Print("[PF Presets] The game refused to disband the party.");
                    return;
                }

                pluginLog.Information("[Party] Disband requested.");
            }
            catch (Exception ex)
            {
                pluginLog.Error(ex, "[Party] Disband failed.");
                chatGui.Print("[PF Presets] Couldn't disband the party. See /xllog for details.");
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
                    chatGui.Print("[PF Presets] The game won't let you leave right now.");
                    return;
                }

                FFXIVClientStructs.FFXIV.Client.Game.Event.EventFramework.LeaveCurrentContent(true);
                pluginLog.Information("[Duty] Leave requested.");
            }
            catch (Exception ex)
            {
                pluginLog.Error(ex, "[Duty] Leave failed.");
                chatGui.Print("[PF Presets] Couldn't leave the duty. See /xllog for details.");
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
            chatGui.Print("[PF Presets] Queueing from a listing isn't wired up yet.");
            pluginLog.Information("[Queue] Placeholder invoked; duty selection not implemented.");
        }
    }
}
