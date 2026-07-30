using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Group;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace PfPresets
{
    /// <summary>
    /// A party member as the game's party proxies report them. The home world is a World sheet
    /// row id, not a name - see <see cref="WorldHelper"/> for turning it into one.
    ///
    /// <paramref name="FcTag"/> is empty for cross-world party members: the cross-realm proxy
    /// doesn't carry it. That costs nothing, because Free Companies are world-locked and a
    /// cross-world member is on a different world by definition.
    ///
    /// <paramref name="AllianceIndex"/> is 0, 1 or 2 in an alliance raid and 0 otherwise. It comes
    /// from the cross-realm proxy's group array; GroupManager only exposes MainGroup, so a
    /// pre-formed same-world alliance reports everyone as group 0 and the UI falls back to one list.
    ///
    /// <paramref name="IsOffline"/> is only ever true for a same-world party. The cross-realm proxy
    /// carries no status field at all - see GetOtherPartyMemberDetails - so cross-world members
    /// always report as online whether they are or not.
    /// </summary>
    public readonly record struct PartyMemberInfo(
        ulong ContentId,
        uint JobId,
        string Name,
        uint HomeWorldId,
        string FcTag,
        int AllianceIndex,
        bool IsOffline)
    {
        /// <summary>
        /// Whether this "member" is a Duty Support / Trust NPC rather than a person.
        ///
        /// The party proxies list them alongside real members, but with no content id and no name -
        /// which is why they rendered as blank rows carrying the local player's world and a Report
        /// button pointed at nobody. Square Enix's own NPCs are not rateable, reportable, or worth
        /// remembering, so everything downstream needs to be able to tell them apart.
        ///
        /// Both halves matter: the content id is the reliable discriminator, and the blank name
        /// catches an entry that is unusable for our purposes either way.
        /// </summary>
        public bool IsSupportNpc => ContentId == 0 || string.IsNullOrWhiteSpace(Name);
    }

    /// <summary>
    /// Applies a saved preset to the Party Finder by populating the native
    /// AgentLookingForGroup memory and driving the Recruitment Criteria window.
    /// The auto-refresher half of this class lives in PfAutomation.Refresher.cs.
    /// </summary>
    public partial class PfAutomation : IDisposable
    {
        // ── Game memory layout ────────────────────────────────────
        // Fields not yet mapped by FFXIVClientStructs, as byte offsets from the
        // owning struct's start. Verified by diffing recruitment memory before/after
        // manual interactions; a game patch can move these.

        /// <summary>StoredRecruitmentInfo: the game sets this byte to 2 when a *specific* duty is
        /// picked through the dropdown, and leaves it 0 for "any duty in the category". Without it
        /// the listing falls back to "All" even though SelectedDutyId is set.</summary>
        private const int OffsetSpecificDutyFlag = 0x12;

        /// <summary>StoredRecruitmentInfo: ulong[8], one accepted-job game mask per party slot.</summary>
        private const int OffsetSlotFlags = 0x1B0;

        /// <summary>StoredRecruitmentInfo: 192-byte UTF-8 comment buffer.</summary>
        private const int OffsetComment = 0x330;

        /// <summary>Maximum comment length in characters (the buffer keeps 1 byte for the terminator).</summary>
        public const int MaxCommentLength = 191;

        /// <summary>AgentLookingForGroup.SearchAreaTab value for the Data Centre tab, the only one
        /// recruitment can be set up from (1 = World, 2 = Private).</summary>
        private const byte SearchAreaTabDataCentre = 0;

        /// <summary>AddonLookingForGroupCondition: AtkComponentCheckBox*[4] for the J/E/D/F
        /// language checkboxes.</summary>
        private const int OffsetLanguageCheckboxes = 0x378;

        // ── Services ──────────────────────────────────────────────
        private readonly IGameGui gameGui;
        private readonly IPluginLog pluginLog;
        private readonly IChatGui chatGui;
        private readonly Configuration config;
        private readonly IClientState clientState;
        private readonly IPlayerState playerState;
        private readonly IFramework framework;
        private readonly DutyDataHelper dutyDataHelper;
        private readonly IObjectTable objectTable;
        private readonly ICondition condition;

        /// <summary>Exposed for the UI (slot 1 shows the local player's current job).</summary>
        public IPlayerState PlayerState => playerState;

        // ── Automation state ──────────────────────────────────────
        private enum AutomationStep
        {
            Idle,
            ClosingAddon,
            OpeningLfg,
            OpeningAddon,
            WritingDutyCategory,
            DelayingAfterCategory,
            OpeningDutyDropdown,
            SelectingDuty,
            WritingDutyId,
            DelayingAfterDutyId,
            WritingRoles,
            DelayingAfterRoles,
            WritingEverythingElse,
            DelayingAfterSync,
            SubmittingAddon,
            ConfirmingComposition,
            Done,
        }

        private AutomationStep currentStep = AutomationStep.Idle;
        private bool waitingForAddon = false;
        private int submitRetryCount = 0;
        private int confirmAttempts = 0;
        private long delayExpirationTime = 0;

        // Retry counter for forcing the duty dropdown list to populate.
        private int dropdownPopulateRetryCount = 0;
        private const int MaxDropdownPopulateAttempts = 5;
        private const int MaxSubmitAttempts = 100;

        public PfPresetData? ActivePreset { get; private set; }
        public bool ShowChecklist { get; set; } = false;
        public string AutomationStatus { get; private set; } = "Idle";

        /// <summary>Progress of the apply-preset automation, 0..1, for the status window's bar.</summary>
        public float AutomationProgress
        {
            get
            {
                int max = (int)AutomationStep.Done;
                int cur = Math.Clamp((int)currentStep, 0, max);
                return max == 0 ? 0f : (float)cur / max;
            }
        }

        /// <summary>True once the automation has finished, whether it succeeded or failed.</summary>
        public bool IsAutomationDone => currentStep == AutomationStep.Done;

        /// <summary>True if the automation finished but could not create the listing.</summary>
        public bool IsAutomationFailed =>
            currentStep == AutomationStep.Done &&
            AutomationStatus.StartsWith("Recruitment Failed", StringComparison.Ordinal);

        /// <summary>Friendly, user-facing description of what the automation is doing now.</summary>
        public string AutomationStage => currentStep switch
        {
            AutomationStep.Idle => "Getting ready...",
            AutomationStep.ClosingAddon => "Closing the old Party Finder window...",
            AutomationStep.OpeningLfg or AutomationStep.OpeningAddon => "Opening Party Finder...",
            AutomationStep.WritingDutyCategory or AutomationStep.DelayingAfterCategory => "Choosing the duty category...",
            AutomationStep.OpeningDutyDropdown or AutomationStep.SelectingDuty
                or AutomationStep.WritingDutyId or AutomationStep.DelayingAfterDutyId => "Selecting your duty...",
            AutomationStep.WritingRoles or AutomationStep.DelayingAfterRoles => "Setting up roles...",
            AutomationStep.WritingEverythingElse or AutomationStep.DelayingAfterSync => "Applying your settings...",
            AutomationStep.SubmittingAddon or AutomationStep.ConfirmingComposition => "Posting your listing...",
            AutomationStep.Done => IsAutomationFailed ? "Something went wrong while posting." : "All set, PF is up!",
            _ => AutomationStatus,
        };

        // ── Auto-adjust slot cache ────────────────────────────────
        private List<(RoleType Role, uint? JobId, string Tooltip)>? cachedAutoAdjustedSlots = null;
        private DateTime lastAutoAdjustUpdate = DateTime.MinValue;
        private static readonly TimeSpan AutoAdjustCacheDuration = TimeSpan.FromSeconds(1);

        public PfAutomation(
            IGameGui gameGui,
            IPluginLog pluginLog,
            IChatGui chatGui,
            Configuration config,
            IClientState clientState,
            IPlayerState playerState,
            IFramework framework,
            DutyDataHelper dutyDataHelper,
            IObjectTable objectTable,
            ICondition condition,
            ISigScanner sigScanner)
        {
            this.gameGui = gameGui;
            this.pluginLog = pluginLog;
            this.chatGui = chatGui;
            this.config = config;
            this.clientState = clientState;
            this.playerState = playerState;
            this.framework = framework;
            this.dutyDataHelper = dutyDataHelper;
            this.objectTable = objectTable;
            this.condition = condition;

            openPartyFinder = ResolveOpenPartyFinder(sigScanner);
        }

        public void Dispose()
        {
            disposed = true;
            framework.Update -= OnFrameworkUpdate;
        }

        // ══════════════════════════════════════════════════════════
        //  PRECONDITIONS
        // ══════════════════════════════════════════════════════════

        public unsafe bool CanRecruit(out string reason)
        {
            reason = string.Empty;
            if (!clientState.IsLoggedIn)
            {
                reason = "You are not logged in.";
                return false;
            }

            // Check if player's online status is "Recruiting Party Members" (ID 26)
            if (objectTable.LocalPlayer != null && objectTable.LocalPlayer.OnlineStatus.RowId == 26)
            {
                reason = "You are already recruiting on the Party Finder.";
                return false;
            }

            // Inside a duty the Party Finder can't be set up at all, and while queued the game
            // drops your registration the moment a listing is posted - so neither is a state we
            // should let the user apply from.
            if (IsInDuty())
            {
                reason = "You are in a duty.";
                return false;
            }

            if (IsInDutyQueue())
            {
                reason = "You are in the Duty Finder queue.";
                return false;
            }

            // A full party (8/8) has no seat to recruit for - the Party Finder can't post a
            // listing with nothing to fill. Only alliance raids could, and those aren't supported.
            // Duty Support NPCs would pad this count, but they only exist inside a duty, which is
            // already excluded above, so seven others plus you is a real, full party.
            if (GetOtherPartyMemberDetails().Count >= 7)
            {
                reason = "Your party is already full.";
                return false;
            }

            var crossRealmProxy = InfoProxyCrossRealm.Instance();
            if (crossRealmProxy != null && crossRealmProxy->IsInCrossRealmParty)
            {
                if (!InfoProxyCrossRealm.IsLocalPlayerPartyLeader())
                {
                    reason = "You are in a cross-world party but you are not the leader.";
                    return false;
                }
            }
            else
            {
                // Use GroupManager (the live party data) rather than IPartyList so that:
                //  1. Leadership detection is always current - PartyLeaderIndex is read at
                //     the moment of the check, so a lead that was just passed to us is
                //     reflected immediately (no caching, no need to watch chat messages).
                //  2. It works regardless of which zone party members are in.
                // We compare the current leader's ContentId to our own instead of assuming
                // the local player sits at index 0 (which is not guaranteed).
                var groupManager = GroupManager.Instance();
                if (groupManager != null && groupManager->MainGroup.MemberCount > 0)
                {
                    var leader = groupManager->MainGroup.GetPartyMemberByIndex((int)groupManager->MainGroup.PartyLeaderIndex);
                    if (leader == null || leader->ContentId != playerState.ContentId)
                    {
                        reason = "You are in a party but you are not the leader.";
                        return false;
                    }
                }
            }

            return true;
        }

        /// <summary>
        /// Every party member except the local player, with their job and name. Reads the
        /// cross-world proxy when in a cross-world party, otherwise the social party list - which,
        /// unlike IPartyList, still reports jobs for members standing in a different zone.
        /// </summary>
        public List<(uint JobId, string Name)> GetOtherPartyMembers()
        {
            var details = GetOtherPartyMemberDetails();
            var members = new List<(uint JobId, string Name)>(details.Count);
            foreach (var m in details)
                members.Add((m.JobId, m.Name));
            return members;
        }

        /// <summary>
        /// The same party read as <see cref="GetOtherPartyMembers"/>, but carrying the home world
        /// and content id as well. The rating system addresses characters as "name@world", and
        /// names alone are ambiguous across worlds, so it needs the fuller picture.
        /// </summary>
        public unsafe List<PartyMemberInfo> GetOtherPartyMemberDetails()
        {
            var members = new List<PartyMemberInfo>();

            // The party proxies keep the last party's members for a beat after logout, and with no
            // local ContentId to filter against ("you" reads as 0) they'd even list yourself. This
            // is the one source feeding every party read - the rows, the Leave Party button, the
            // duty sampler - so a stale party anywhere traces back to here.
            if (!clientState.IsLoggedIn)
                return members;

            var crossRealmProxy = InfoProxyCrossRealm.Instance();
            if (crossRealmProxy != null && crossRealmProxy->IsInCrossRealmParty)
            {
                for (int i = 0; i < crossRealmProxy->GroupCount; i++)
                {
                    var group = crossRealmProxy->CrossRealmGroups[i];
                    for (int c = 0; c < group.GroupMemberCount; c++)
                    {
                        var member = group.GroupMembers[c];
                        if (member.ContentId != playerState.ContentId)
                        {
                            members.Add(new PartyMemberInfo(
                                member.ContentId,
                                member.ClassJobId,
                                member.NameString,
                                (uint)(ushort)member.HomeWorld,
                                string.Empty,
                                i,
                                // CrossRealmMember has no online-status field. Reporting everyone
                                // as online is the honest default: a wrong "disconnected" badge on
                                // someone who is fine is worse than no badge.
                                false));
                        }
                    }
                }
                return members;
            }

            var partyInfo = InfoProxyPartyMember.Instance();
            if (partyInfo != null)
            {
                uint count = partyInfo->GetEntryCount();
                for (uint i = 0; i < count; i++)
                {
                    var entry = partyInfo->GetEntry(i);
                    if (entry == null) continue;
                    if (entry->ContentId == playerState.ContentId) continue;
                    members.Add(new PartyMemberInfo(
                        entry->ContentId,
                        (uint)entry->Job,
                        entry->NameString,
                        entry->HomeWorld,
                        entry->FCTagString,
                        0,
                        IsOfflineStatus(entry->State)));
                }
            }
            return members;
        }

        /// <summary>
        /// Whether a party member's status means they aren't actually there.
        ///
        /// OnlineStatus is a flags enum, but Offline is 0 - so it can't be tested with a bitwise
        /// AND and has to be compared directly. Disconnected and OfflineExd are real bits.
        /// </summary>
        private static bool IsOfflineStatus(InfoProxyCommonList.CharacterData.OnlineStatus state)
        {
            const InfoProxyCommonList.CharacterData.OnlineStatus gone =
                InfoProxyCommonList.CharacterData.OnlineStatus.Disconnected
                | InfoProxyCommonList.CharacterData.OnlineStatus.OfflineExd;

            return state == InfoProxyCommonList.CharacterData.OnlineStatus.Offline
                || (state & gone) != 0;
        }

        /// <summary>True while the player is inside a duty. The game uses several "bound by duty"
        /// flags depending on the content type, so all of them are checked. PvP instances
        /// (Frontline, Crystalline Conflict) use a separate flag that is also checked here.</summary>
        public bool IsInDuty() =>
            condition[ConditionFlag.BoundByDuty] ||
            condition[ConditionFlag.BoundByDuty56] ||
            condition[ConditionFlag.BoundByDuty95] ||
            clientState.IsPvP;

        /// <summary>True while the player is in combat.</summary>
        public bool IsInCombat() => condition[ConditionFlag.InCombat];

        /// <summary>True while the player is registered in the Duty Finder queue, including the
        /// window where the duty has popped and is waiting to be accepted.</summary>
        public bool IsInDutyQueue() =>
            condition[ConditionFlag.InDutyQueue] ||
            condition[ConditionFlag.WaitingForDutyFinder];

        /// <summary>True when the local player leads the current party (or is solo). Unlike
        /// <see cref="CanRecruit"/> this makes no claim about whether a listing is already up, so
        /// it's usable while actively recruiting (e.g. the locked-slot auto-adjuster).</summary>
        public unsafe bool IsPartyLeader()
        {
            var crossRealmProxy = InfoProxyCrossRealm.Instance();
            if (crossRealmProxy != null && crossRealmProxy->IsInCrossRealmParty)
                return InfoProxyCrossRealm.IsLocalPlayerPartyLeader();

            var groupManager = GroupManager.Instance();
            if (groupManager != null && groupManager->MainGroup.MemberCount > 0)
            {
                var leader = groupManager->MainGroup.GetPartyMemberByIndex((int)groupManager->MainGroup.PartyLeaderIndex);
                return leader != null && leader->ContentId == playerState.ContentId;
            }

            return true; // solo: you are effectively the leader of your own listing
        }

        /// <summary>True when any current party member (including you) is on a non-combat job -
        /// crafter or gatherer. Applying a battle-duty listing with such a job in the party makes
        /// the game raise the "party composition" warning, so the UI flags it in advance.</summary>
        public unsafe bool PartyHasNonBattleJob()
        {
            if (IsNonBattleJob(GetLocalPlayerJobId()))
                return true;

            var crossRealmProxy = InfoProxyCrossRealm.Instance();
            if (crossRealmProxy != null && crossRealmProxy->IsInCrossRealmParty)
            {
                for (int i = 0; i < crossRealmProxy->GroupCount; i++)
                {
                    var group = crossRealmProxy->CrossRealmGroups[i];
                    for (int c = 0; c < group.GroupMemberCount; c++)
                        if (IsNonBattleJob(group.GroupMembers[c].ClassJobId))
                            return true;
                }
                return false;
            }

            var partyInfo = InfoProxyPartyMember.Instance();
            if (partyInfo != null)
            {
                uint count = partyInfo->GetEntryCount();
                for (uint i = 0; i < count; i++)
                {
                    var entry = partyInfo->GetEntry(i);
                    if (entry != null && IsNonBattleJob((uint)entry->Job))
                        return true;
                }
            }
            return false;
        }

        /// <summary>A ClassJob row id that is a real job but not a combat one (crafter/gatherer):
        /// it has no entry in <see cref="JobData"/>, which only lists Disciples of War/Magic.</summary>
        private static bool IsNonBattleJob(uint jobId) => jobId > 0 && JobData.FindById(jobId) == null;

        // ══════════════════════════════════════════════════════════
        //  APPLY PRESET
        // ══════════════════════════════════════════════════════════

        /// <summary>
        /// Applies the given preset by populating the Agent's StoredRecruitmentInfo,
        /// opening the PF Recruitment Criteria window, and clicking Recruit.
        /// </summary>
        public unsafe void ApplyPreset(PfPresetData preset)
        {
            try
            {
                if (!CanRecruit(out var reason))
                {
                    chatGui.Print($"[PF Presets] Cannot apply preset: {reason}");
                    return;
                }

                var agent = AgentLookingForGroup.Instance();
                if (agent == null)
                {
                    pluginLog.Warning("AgentLookingForGroup not available.");
                    chatGui.Print("[PF Presets] Could not access Party Finder. Make sure you are logged in.");
                    return;
                }

                config.MarkPresetUsed(preset.Id);
                ActivePreset = preset;
                submitRetryCount = 0;
                delayExpirationTime = 0;
                dropdownPopulateRetryCount = 0;

                // If the recruitment window is already open, close it first so the new
                // settings are applied to a freshly-opened window.
                var addon = GetVisibleConditionAddon();
                if (addon != null)
                {
                    pluginLog.Information("LookingForGroupCondition is already open. Closing it first to apply new settings.");
                    AutomationStatus = "Closing active PF window...";
                    currentStep = AutomationStep.ClosingAddon;
                    waitingForAddon = true;

                    if (addon->CancelButton != null)
                        AtkHelpers.ClickButton(&addon->AtkUnitBase, addon->CancelButton);

                    SubscribeFrameworkUpdate();
                    ShowChecklist = true;
                    chatGui.Print($"[PF Presets] Applying preset: {preset.Name}...");
                    return;
                }

                // If not open, write settings and open it directly.
                WriteSettingsToMemory(preset);
                OpenAddonWindow();
                chatGui.Print($"[PF Presets] Applying preset: {preset.Name}...");
            }
            catch (Exception ex)
            {
                pluginLog.Error(ex, "Failed to apply preset.");
                chatGui.Print("[PF Presets] Error applying preset. Check the plugin log for details.");
            }
        }

        /// <summary>Cancels a running (or finished) automation and hides the status window.</summary>
        public void DismissChecklist()
        {
            ShowChecklist = false;
            ActivePreset = null;
            waitingForAddon = false;
            submitRetryCount = 0;
            currentStep = AutomationStep.Idle;
            AutomationStatus = "Idle";
            framework.Update -= OnFrameworkUpdate;
        }

        public void ClearAutoAdjustCache()
        {
            cachedAutoAdjustedSlots = null;
            lastAutoAdjustUpdate = DateTime.MinValue;
        }

        private void SubscribeFrameworkUpdate()
        {
            framework.Update -= OnFrameworkUpdate;
            framework.Update += OnFrameworkUpdate;
        }

        private unsafe void OpenAddonWindow()
        {
            if (ActivePreset == null) return;

            var agent = AgentLookingForGroup.Instance();
            if (agent == null) return;

            pluginLog.Information("Opening LFG window first natively via agent...");

            // Recruiting is only possible from the Data Centre tab. If the player last left the
            // Party Finder on World or Private, the Recruit button drives a different flow and
            // the setup sequence falls apart, so force the tab back before the window opens.
            EnsureDataCentreTab(agent);

            // If LookingForGroup is already open and visible we can skip agent->Show().
            var lfgAddon = GetVisibleAddon("LookingForGroup");
            if (lfgAddon != null)
            {
                pluginLog.Information("LookingForGroup is already visible. Transitioning to OpeningLfg step.");
                AutomationStatus = "Opening recruitment window...";
            }
            else
            {
                AutomationStatus = "Opening Party Finder...";
                agent->Show();
            }

            currentStep = AutomationStep.OpeningLfg;
            waitingForAddon = true;
            delayExpirationTime = Environment.TickCount64 + 500; // wait 500ms for UI to settle

            SubscribeFrameworkUpdate();
            ShowChecklist = true;
            if (lfgAddon != null)
                chatGui.Print($"[PF Presets] Applying preset: {ActivePreset.Name}...");
        }

        // ══════════════════════════════════════════════════════════
        //  STATE MACHINE (runs on the framework thread)
        // ══════════════════════════════════════════════════════════

        private unsafe void OnFrameworkUpdate(IFramework _)
        {
            if (ActivePreset == null || !waitingForAddon) return;
            if (Environment.TickCount64 < delayExpirationTime) return; // Non-blocking wait

            switch (currentStep)
            {
                case AutomationStep.ClosingAddon: StepClosingAddon(); break;
                case AutomationStep.OpeningLfg: StepOpeningLfg(); break;
                case AutomationStep.OpeningAddon: StepOpeningAddon(); break;
                case AutomationStep.WritingDutyCategory: StepWritingDutyCategory(); break;
                case AutomationStep.DelayingAfterCategory: StepDelayingAfterCategory(); break;
                case AutomationStep.OpeningDutyDropdown: StepOpeningDutyDropdown(); break;
                case AutomationStep.SelectingDuty: StepSelectingDuty(); break;
                case AutomationStep.WritingDutyId: StepWritingDutyId(); break;
                case AutomationStep.DelayingAfterDutyId: StepDelay("Delay after duty ID complete. Transitioning to writing roles.", "Setting up roles...", AutomationStep.WritingRoles); break;
                case AutomationStep.WritingRoles: StepWritingRoles(); break;
                case AutomationStep.DelayingAfterRoles: StepDelay("Delay after roles complete. Transitioning to writing general settings.", "Syncing settings directly to UI...", AutomationStep.WritingEverythingElse); break;
                case AutomationStep.WritingEverythingElse: StepWritingEverythingElse(); break;
                case AutomationStep.DelayingAfterSync: StepDelay("Stabilization delay complete. Proceeding to submit.", null, AutomationStep.SubmittingAddon, 200); break;
                case AutomationStep.SubmittingAddon: StepSubmittingAddon(); break;
                case AutomationStep.ConfirmingComposition: StepConfirmingComposition(); break;
            }
        }

        /// <summary>Advances a pure delay step: logs, optionally updates the status text, and
        /// schedules the next step.</summary>
        private void StepDelay(string logMessage, string? status, AutomationStep next, int delayMs = 100)
        {
            pluginLog.Information(logMessage);
            if (status != null) AutomationStatus = status;
            currentStep = next;
            delayExpirationTime = Environment.TickCount64 + delayMs;
        }

        private unsafe void StepClosingAddon()
        {
            // Wait for the window to close fully.
            if ((nint)gameGui.GetAddonByName("LookingForGroupCondition") == IntPtr.Zero)
            {
                pluginLog.Information("Addon closed. Proceeding to open LFG / PF window.");
                OpenAddonWindow();
            }
        }

        private unsafe void StepOpeningLfg()
        {
            var lfgAddon = GetVisibleAddon("LookingForGroup");
            if (lfgAddon == null) return;

            var recruitBtn = lfgAddon->GetComponentButtonById(46);
            if (recruitBtn != null && recruitBtn->IsEnabled)
            {
                pluginLog.Information("LookingForGroup window is visible. Writing memory and clicking Recruit Members button.");
                // Re-assert the tab on the click frame: the window may already have been open on
                // World or Private, where the memory write in OpenAddonWindow lands before the
                // addon reads it.
                EnsureDataCentreTab(AgentLookingForGroup.Instance());
                WriteSettingsToMemory(ActivePreset!);
                AutomationStatus = "Opening recruitment window...";
                currentStep = AutomationStep.OpeningAddon;
                delayExpirationTime = Environment.TickCount64 + 500; // wait for recruitment window to open
                AtkHelpers.ClickButton(lfgAddon, recruitBtn);
            }
        }

        private unsafe void StepOpeningAddon()
        {
            if (GetVisibleConditionAddon() == null) return;

            pluginLog.Information("LookingForGroupCondition is visible. Transitioning to writing duty category.");
            AutomationStatus = "Selecting duty category...";
            currentStep = AutomationStep.WritingDutyCategory;
            delayExpirationTime = Environment.TickCount64 + 100;
        }

        private unsafe void StepWritingDutyCategory()
        {
            if (GetVisibleConditionAddon() == null) return;

            pluginLog.Information("Writing duty category to memory...");
            WriteDutyCategoryToMemory(ActivePreset!);

            currentStep = AutomationStep.DelayingAfterCategory;
            delayExpirationTime = Environment.TickCount64 + 500;
        }

        private unsafe void StepDelayingAfterCategory()
        {
            if (GetVisibleConditionAddon() == null) return;

            // No category means there is no duty to select. Skip straight to writing the
            // (empty) duty id so SelectedDutyId is cleared to 0.
            if (ActivePreset!.DutyCategoryId == 0)
            {
                pluginLog.Information("No duty category set. Skipping duty dropdown selection.");
                currentStep = AutomationStep.WritingDutyId;
                delayExpirationTime = Environment.TickCount64 + 100;
                return;
            }

            pluginLog.Information("Delay after category complete. Preparing duty dropdown...");
            dropdownPopulateRetryCount = 0;
            currentStep = AutomationStep.OpeningDutyDropdown;
            delayExpirationTime = Environment.TickCount64 + 100;
        }

        private unsafe void StepOpeningDutyDropdown()
        {
            var addon = GetVisibleConditionAddon();
            if (addon == null) return;

            var dropdown = addon->DutyDropDown;
            if (dropdown == null)
            {
                pluginLog.Warning("Duty dropdown is null. Falling back to direct memory write only.");
                currentStep = AutomationStep.WritingDutyId;
                delayExpirationTime = Environment.TickCount64 + 100;
                return;
            }

            // The game populates the duty list lazily. If it is already populated we can
            // select immediately; otherwise open the dropdown to force the game to build
            // the list - this mirrors a manual user interaction with the menu.
            if (AtkHelpers.IsListPopulated(dropdown))
            {
                pluginLog.Information("Duty list is populated. Proceeding to selection.");
                currentStep = AutomationStep.SelectingDuty;
                delayExpirationTime = Environment.TickCount64 + 100;
                return;
            }

            if (dropdownPopulateRetryCount >= MaxDropdownPopulateAttempts)
            {
                pluginLog.Warning($"Duty list did not populate after {MaxDropdownPopulateAttempts} attempts. Attempting selection anyway.");
                currentStep = AutomationStep.SelectingDuty;
                delayExpirationTime = Environment.TickCount64 + 100;
                return;
            }

            dropdownPopulateRetryCount++;
            pluginLog.Information($"Opening duty dropdown to populate list (attempt {dropdownPopulateRetryCount}/{MaxDropdownPopulateAttempts})...");
            AtkHelpers.OpenDropDownList(dropdown);
            delayExpirationTime = Environment.TickCount64 + 300; // wait for the list to build, then re-check
        }

        private unsafe void StepSelectingDuty()
        {
            var addon = GetVisibleConditionAddon();
            if (addon == null) return;

            // Update the dropdown's visible selection so the in-game window matches the
            // preset. This is purely cosmetic - the authoritative duty is written to memory
            // in WritingDutyId.
            var dropdown = addon->DutyDropDown;
            if (dropdown != null && AtkHelpers.IsListPopulated(dropdown))
            {
                int index = FindDropDownListIndex(dropdown, ActivePreset!.DutyName);
                if (index >= 0)
                {
                    try
                    {
                        if (!AtkHelpers.TrySelectDropDownItem(dropdown, index))
                            pluginLog.Warning($"Could not select duty dropdown index {index}.");
                    }
                    catch (Exception ex)
                    {
                        pluginLog.Error(ex, $"Exception when selecting duty dropdown index {index}.");
                    }
                }
                else
                {
                    pluginLog.Warning($"Duty '{ActivePreset!.DutyName}' not found in dropdown list.");
                }
            }

            currentStep = AutomationStep.WritingDutyId;
            delayExpirationTime = Environment.TickCount64 + 300;
        }

        private unsafe void StepWritingDutyId()
        {
            var addon = GetVisibleConditionAddon();
            if (addon == null) return;

            // Close the duty dropdown if it is still open from the populate/select steps.
            if (addon->DutyDropDown != null && addon->DutyDropDown->IsOpen)
                AtkHelpers.CloseDropDownList(addon->DutyDropDown);

            // The submit reads agent->SelectedDutyId (plus the "specific duty" flag)
            // directly; the dropdown's visual selection does not feed it. Write the resolved
            // duty to memory here - this is the authoritative step.
            WriteDutyIdToMemory(ActivePreset!);

            currentStep = AutomationStep.DelayingAfterDutyId;
            delayExpirationTime = Environment.TickCount64 + 500;
        }

        // Note: the WritingRoles step is dispatched from OnFrameworkUpdate below.
        private unsafe void StepWritingRoles()
        {
            if (GetVisibleConditionAddon() == null) return;

            pluginLog.Information("Writing slot flags to memory...");
            WriteSlotFlagsToMemory(ActivePreset!);

            currentStep = AutomationStep.DelayingAfterRoles;
            delayExpirationTime = Environment.TickCount64 + 500;
        }

        private unsafe void StepWritingEverythingElse()
        {
            var addon = GetVisibleConditionAddon();
            if (addon == null) return;
            var preset = ActivePreset!;

            pluginLog.Information("Writing general settings to memory...");
            WriteGeneralSettingsToMemory(preset);

            // Direct updates to UI controls to make absolutely sure they are synced and
            // visually correct.

            // 1. Comment
            if (addon->CommentTextInput != null)
                addon->CommentTextInput->SetText(preset.ResolveComment(MaxCommentLength));

            // 2. Private party passcode
            if (addon->FormPrivatePartyCheckbox != null)
            {
                addon->FormPrivatePartyCheckbox->SetChecked(preset.FormPrivateParty);
                if (preset.FormPrivateParty && addon->PasswordNumericInput != null &&
                    int.TryParse(preset.PrivatePartyPassword, out int pwdVal))
                {
                    addon->PasswordNumericInput->SetValue(pwdVal);
                    addon->PasswordNumericInput->Value = pwdVal;
                    addon->PasswordNumericInput->UpdateTextNode();
                }
            }

            // 3. Checkboxes
            SetChecked(addon->LimitToWorldServerCheckbox, preset.LimitRecruitingToWorld);
            SetChecked(addon->OnePlayerPerJobCheckbox, preset.OnePlayerPerJob);
            SetChecked(addon->BeginnersWelcomeCheckBox, false);
            SetChecked(addon->CompletionStatusCheckBox, preset.CompletionStatusEnabled);

            // Force Duty Finder settings to be unchecked if category is None (0) to prevent
            // the native button greying out.
            bool hasDuty = preset.DutyCategoryId > 0;
            SetChecked(addon->UnrestrictedPartyCheckBox, hasDuty && preset.UnrestrictedParty);
            SetChecked(addon->MinimumItemLevelCheckBox, hasDuty && preset.MinimumIL);
            SetChecked(addon->SilenceEchoCheckbox, hasDuty && preset.SilenceEcho);
            SetChecked(addon->RemoveRoleRestrictionsCheckBox, preset.RemoveRoleRestrictions);

            // 4. Avg Item Level
            if (addon->AvgItemLevelCheckbox != null)
            {
                addon->AvgItemLevelCheckbox->SetChecked(preset.AvgItemLvEnabled);
                if (preset.AvgItemLvEnabled && addon->AvgItemLevelNumericInput != null)
                {
                    addon->AvgItemLevelNumericInput->SetValue(preset.AvgItemLv);
                    addon->AvgItemLevelNumericInput->Value = preset.AvgItemLv;
                    addon->AvgItemLevelNumericInput->UpdateTextNode();
                }
            }

            // 5. Languages
            AtkComponentCheckBox** pLanguages = (AtkComponentCheckBox**)((byte*)addon + OffsetLanguageCheckboxes);
            if (pLanguages != null)
            {
                SetChecked(pLanguages[0], preset.LangJapanese);
                SetChecked(pLanguages[1], preset.LangEnglish);
                SetChecked(pLanguages[2], preset.LangGerman);
                SetChecked(pLanguages[3], preset.LangFrench);
            }

            pluginLog.Information("Sync complete. Delaying to allow UI changes to stabilize...");
            currentStep = AutomationStep.DelayingAfterSync;
            delayExpirationTime = Environment.TickCount64 + 500;
        }

        private unsafe void StepSubmittingAddon()
        {
            var addon = GetVisibleConditionAddon();
            if (addon == null) return;

            if (addon->RecruitMembersButton != null && addon->RecruitMembersButton->IsEnabled)
            {
                // Re-assert the duty id one last time, on the same frame as the click, so
                // it is guaranteed correct when the game reads it on submit.
                WriteDutyIdToMemory(ActivePreset!);
                pluginLog.Information("Clicking PF Recruit button...");
                AutomationStatus = "Submitting Party Finder listing...";
                AtkHelpers.ClickButton(&addon->AtkUnitBase, addon->RecruitMembersButton);

                // Marks the only moment OwnListingId may be believed on its own - see IsRecruiting.
                MarkListingSubmitted();

                // The game may raise a "cannot carry out the selected objective with this party
                // composition" confirmation (e.g. a crafter/gatherer is in the party). Watch for it
                // for a short window and auto-confirm so the listing still posts.
                confirmAttempts = 0;
                currentStep = AutomationStep.ConfirmingComposition;
                delayExpirationTime = Environment.TickCount64 + 150;
            }
            else
            {
                submitRetryCount++;
                if (submitRetryCount > MaxSubmitAttempts)
                {
                    pluginLog.Warning($"RecruitMembersButton is not enabled or null after {MaxSubmitAttempts} frames! Aborting.");
                    FinishAutomation("Recruitment Failed: Button not enabled");
                }
            }
        }

        /// <summary>After clicking Recruit, briefly watches for the party-composition confirmation
        /// dialog and clicks Yes if it appears; otherwise finishes once the short window elapses.</summary>
        private unsafe void StepConfirmingComposition()
        {
            if (TryConfirmCompositionDialog())
            {
                FinishAutomation("Recruitment Listing Created!");
                return;
            }

            // Wait briefly for the dialog to appear; if it never does, the listing posted cleanly.
            confirmAttempts++;
            if (confirmAttempts >= 12) // ~12 * 50ms ≈ 0.6s after the Recruit click
            {
                FinishAutomation("Recruitment Listing Created!");
                return;
            }
            delayExpirationTime = Environment.TickCount64 + 50;
        }

        /// <summary>
        /// If the game's "You cannot carry out the selected objective with this party composition.
        /// Proceed anyway?" dialog is up, clicks Yes and returns true. Safe to call every frame;
        /// returns false when the dialog isn't present or its Yes button isn't ready yet. Shared by
        /// the apply flow, the Auto Refresher, and the locked-slot adjuster.
        /// </summary>
        private unsafe bool TryConfirmCompositionDialog()
        {
            var addonPtr = (nint)gameGui.GetAddonByName("SelectYesno");
            if (addonPtr == IntPtr.Zero) return false;
            var addon = (AtkUnitBase*)addonPtr;
            if (!addon->IsVisible) return false;

            var yesno = (AddonSelectYesno*)addonPtr;

            // The prompt is identical every time; match a distinctive part so we never confirm an
            // unrelated yes/no dialog. If the text can't be read, still confirm - this only runs in
            // the brief window right after we click Recruit.
            string prompt = yesno->PromptText != null ? yesno->PromptText->NodeText.ToString() : string.Empty;
            bool isCompositionWarning = string.IsNullOrEmpty(prompt)
                || prompt.Contains("party composition", StringComparison.OrdinalIgnoreCase)
                || prompt.Contains("cannot carry out", StringComparison.OrdinalIgnoreCase);
            if (!isCompositionWarning) return false;

            if (yesno->YesButton == null || !AtkHelpers.ClickAddonButton(addon, yesno->YesButton))
                return false;

            pluginLog.Information($"[PF Presets] Auto-confirmed party-composition warning: \"{prompt}\"");
            return true;
        }

        private void FinishAutomation(string status)
        {
            currentStep = AutomationStep.Done;
            AutomationStatus = status;
            framework.Update -= OnFrameworkUpdate;
            waitingForAddon = false;
        }

        /// <summary>The Recruitment Criteria addon, or null when it is not open and visible.</summary>
        private unsafe AddonLookingForGroupCondition* GetVisibleConditionAddon()
        {
            var addonPtr = (nint)gameGui.GetAddonByName("LookingForGroupCondition");
            if (addonPtr == IntPtr.Zero) return null;
            var addon = (AddonLookingForGroupCondition*)addonPtr;
            return addon->AtkUnitBase.IsVisible ? addon : null;
        }

        /// <summary>A named addon, or null when it is not open and visible.</summary>
        private unsafe AtkUnitBase* GetVisibleAddon(string name)
        {
            var addonPtr = (nint)gameGui.GetAddonByName(name);
            if (addonPtr == IntPtr.Zero) return null;
            var addon = (AtkUnitBase*)addonPtr;
            return addon->IsVisible ? addon : null;
        }

        // ══════════════════════════════════════════════════════════
        //  MEMORY WRITES
        // ══════════════════════════════════════════════════════════

        /// <summary>
        /// Forces the Party Finder's search-area tab to Data Centre (0; the others are 1 = World,
        /// 2 = Private). Recruitment is set up from this tab, and the window remembers whichever
        /// one was last used, so anyone who left it on World or Private would otherwise get a
        /// half-applied preset. Safe to call repeatedly - it only writes when the tab differs.
        /// </summary>
        private unsafe void EnsureDataCentreTab(AgentLookingForGroup* agent)
        {
            if (agent == null || agent->SearchAreaTab == SearchAreaTabDataCentre)
                return;

            pluginLog.Information($"Party Finder was on search-area tab {agent->SearchAreaTab}; switching to Data Centre.");
            agent->SearchAreaTab = SearchAreaTabDataCentre;
        }

        private unsafe void WriteSettingsToMemory(PfPresetData preset)
        {
            // Note: the duty id is intentionally NOT written here. The category is set via
            // memory (which works reliably), but the specific duty is selected through the
            // native dropdown so the game commits it properly. Writing the duty id to memory
            // only competes with the game's committed dropdown state and gets reset to 0.
            WriteDutyCategoryToMemory(preset);
            WriteSlotFlagsToMemory(preset);
            WriteGeneralSettingsToMemory(preset);
        }

        private unsafe void WriteDutyCategoryToMemory(PfPresetData preset)
        {
            var agent = AgentLookingForGroup.Instance();
            if (agent == null) return;

            agent->StoredRecruitmentInfo.SelectedCategory = preset.DutyCategoryId == 0
                ? AgentLookingForGroup.DutyCategory.None
                : (AgentLookingForGroup.DutyCategory)(1 << preset.DutyCategoryId);
        }

        private unsafe void WriteDutyIdToMemory(PfPresetData preset)
        {
            var agent = AgentLookingForGroup.Instance();
            if (agent == null) return;

            var recruitment = &agent->StoredRecruitmentInfo;

            ushort dutyId = 0;
            if (preset.DutyCategoryId > 0)
            {
                var duty = ResolveDuty(preset);
                if (duty != null && duty.RowId <= ushort.MaxValue)
                {
                    dutyId = (ushort)duty.RowId;
                    pluginLog.Information($"Setting duty ID {dutyId} for duty '{duty.Name}'");
                }
                else if (duty != null)
                {
                    // Synthetic fallback entries (see DutyDataHelper) have row ids above
                    // ushort range and must never be written to the game.
                    pluginLog.Warning($"Duty '{duty.Name}' has synthetic row id {duty.RowId}; leaving SelectedDutyId at 0 (All duties).");
                }
                else
                {
                    pluginLog.Warning("No duty could be determined. SelectedDutyId will remain 0 (All duties).");
                }
            }
            recruitment->SelectedDutyId = dutyId;

            // Mark whether a specific duty (vs "any duty in the category") is selected.
            *((byte*)recruitment + OffsetSpecificDutyFlag) = (byte)(dutyId != 0 ? 2 : 0);
        }

        /// <summary>
        /// Resolves the preset's duty. The stored ContentFinderCondition row id is authoritative and
        /// is language- and rename-proof; the display name is only consulted for presets saved before
        /// row ids existed (or for the synthetic high-end entries, which have no stable id).
        ///
        /// Returns null when the duty genuinely can't be identified. The caller then leaves the
        /// listing on "All duties" rather than guessing - posting a *wrong* duty is far worse than
        /// posting an unset one.
        /// </summary>
        private DutyEntry? ResolveDuty(PfPresetData preset)
        {
            if (preset.DutyRowId != 0 && !DutyDataHelper.IsSyntheticRowId(preset.DutyRowId))
            {
                var byId = dutyDataHelper.GetDutyEntry(preset.DutyRowId);
                if (byId != null)
                    return byId;

                pluginLog.Warning($"Duty row id {preset.DutyRowId} ('{preset.DutyName}') is no longer in the game data; falling back to a name lookup.");
            }

            var byName = dutyDataHelper.FindDutyByName(preset.DutyCategoryName, preset.DutyName);
            if (byName == null)
                pluginLog.Warning($"Duty '{preset.DutyName}' not found in category '{preset.DutyCategoryName}'. Leaving the listing's duty unset.");

            return byName;
        }

        private unsafe void WriteGeneralSettingsToMemory(PfPresetData preset)
        {
            var agent = AgentLookingForGroup.Instance();
            if (agent == null) return;

            var recruitment = &agent->StoredRecruitmentInfo;

            // Objective - only meaningful when a duty category is selected
            recruitment->Objective = preset.DutyCategoryId > 0
                ? (AgentLookingForGroup.Objective)(1 << Math.Clamp(preset.ObjectiveId, 0, 3))
                : AgentLookingForGroup.Objective.None;
            recruitment->BeginnerFriendly = 0;

            // Completion Status
            AgentLookingForGroup.CompletionStatus compStatus = AgentLookingForGroup.CompletionStatus.None;
            if (preset.CompletionStatusEnabled)
            {
                compStatus = Math.Clamp(preset.CompletionStatusType, 0, 2) switch
                {
                    0 => AgentLookingForGroup.CompletionStatus.DutyComplete,
                    1 => AgentLookingForGroup.CompletionStatus.DutyCompleteWeeklyUnclaimed,
                    2 => AgentLookingForGroup.CompletionStatus.DutyIncomplete,
                    _ => AgentLookingForGroup.CompletionStatus.None,
                };
            }
            recruitment->CompletionStatus = compStatus;

            // Duty Finder Settings (Unrestricted, Min IL, Silence Echo)
            AgentLookingForGroup.DutyFinderSetting dfSettings = AgentLookingForGroup.DutyFinderSetting.None;
            if (preset.DutyCategoryId > 0)
            {
                if (preset.UnrestrictedParty) dfSettings |= AgentLookingForGroup.DutyFinderSetting.UnrestrictedParty;
                if (preset.MinimumIL) dfSettings |= AgentLookingForGroup.DutyFinderSetting.MinimumIL;
                if (preset.SilenceEcho) dfSettings |= AgentLookingForGroup.DutyFinderSetting.SilenceEcho;
            }
            recruitment->DutyFinderSettingFlags = dfSettings;

            // Avg Item Level (set on agent directly)
            agent->AvgItemLv = (ushort)Math.Clamp(preset.AvgItemLv, 1, 9999);
            agent->AvgItemLvEnabled = (byte)(preset.AvgItemLvEnabled ? 1 : 0);

            // Loot rules
            recruitment->LootRule = (AgentLookingForGroup.LootRule)Math.Clamp(preset.LootRules, 0, 2);

            // Password (10000 = no password)
            ushort pwd = 10000;
            if (preset.FormPrivateParty)
                pwd = ushort.TryParse(preset.PrivatePartyPassword, out ushort val) ? Math.Clamp(val, (ushort)0, (ushort)9999) : (ushort)0;
            recruitment->Password = pwd;

            // Languages
            AgentLookingForGroup.Language lang = 0;
            if (preset.LangJapanese) lang |= AgentLookingForGroup.Language.Japanese;
            if (preset.LangEnglish) lang |= AgentLookingForGroup.Language.English;
            if (preset.LangGerman) lang |= AgentLookingForGroup.Language.German;
            if (preset.LangFrench) lang |= AgentLookingForGroup.Language.French;
            recruitment->LanguageFlags = lang;

            // Party settings (Normal group type is 1 group of 8)
            recruitment->LimitRecruitingToWorld = (byte)(preset.LimitRecruitingToWorld ? 0 : 1);
            recruitment->OnePlayerPerJob = (byte)(preset.OnePlayerPerJob ? 1 : 0);
            recruitment->NumberOfSlotsInMainParty = 8;
            recruitment->NumberOfGroups = 1;

            // Comment (192-byte fixed buffer)
            string comment = preset.ResolveComment(MaxCommentLength);
            string safeComment = comment.Length > MaxCommentLength ? comment.Substring(0, MaxCommentLength) : comment;
            AtkHelpers.SetFixedString((byte*)recruitment + OffsetComment, safeComment, MaxCommentLength + 1);
        }

        private unsafe void WriteSlotFlagsToMemory(PfPresetData preset)
        {
            var agent = AgentLookingForGroup.Instance();
            if (agent == null) return;

            ulong* pSlotFlags = (ulong*)((byte*)&agent->StoredRecruitmentInfo + OffsetSlotFlags);

            if (preset.AutoAdjustRoles)
            {
                var autoSlots = GetAutoAdjustedSlots();
                for (int i = 0; i < 8; i++)
                    pSlotFlags[i] = i < autoSlots.Count ? GetAutoSlotGameMask(autoSlots[i]) : 0;

                if (preset.AllowDoubleCaster)
                    ApplyDoubleCasterSlot(pSlotFlags, autoSlots);

                return;
            }

            // Slot 1 is locked to the player's current class/job (when not auto-adjusting).
            pSlotFlags[0] = GetLockedJobGameMask(GetLocalPlayerJobId());

            for (int i = 1; i < 8; i++)
            {
                if (i >= preset.Slots.Count)
                {
                    pSlotFlags[i] = 0;
                    continue;
                }

                var slot = preset.Slots[i];
                if (slot.Role == RoleType.Omit)
                    pSlotFlags[i] = 0; // Empty / disabled slot
                else if (slot.AcceptedJobFlags != 0)
                    pSlotFlags[i] = JobMasks.ToGameMask(slot.AcceptedJobFlags);
                else
                    pSlotFlags[i] = JobMasks.ToGameMask(JobMasks.GetRoleMask(slot.Role));
            }
        }

        /// <summary>Game mask for one auto-adjusted slot: the locked job when known, else the
        /// sought role's mask.</summary>
        /// <summary>
        /// Widens the last melee slot so it accepts casters too - the "fake melee" seat.
        ///
        /// The last one rather than the first: parties fill top-down, so the seat most likely to
        /// still be open is the one furthest down, and widening a seat someone already occupies
        /// achieves nothing. Does nothing when the composition has no melee slot to give up.
        /// </summary>
        private static unsafe void ApplyDoubleCasterSlot(
            ulong* pSlotFlags, List<(RoleType Role, uint? JobId, string Tooltip)> autoSlots)
        {
            int target = -1;
            for (int i = 0; i < autoSlots.Count && i < 8; i++)
            {
                // Only an unlocked melee slot can be traded away; one pinned to a specific job is
                // there because someone is already standing in it.
                if (autoSlots[i].Role == RoleType.MeleeDPS && !autoSlots[i].JobId.HasValue)
                    target = i;
            }

            if (target < 0)
                return;

            ulong melee = JobMasks.GetRoleMask(RoleType.MeleeDPS);
            ulong caster = JobMasks.GetRoleMask(RoleType.MagicRangedDPS);
            pSlotFlags[target] = JobMasks.ToGameMask(melee | caster);
        }

        private static ulong GetAutoSlotGameMask((RoleType Role, uint? JobId, string Tooltip) slot)
        {
            if (slot.JobId.HasValue)
                return GetLockedJobGameMask(slot.JobId.Value);
            return JobMasks.ToGameMask(JobMasks.GetRoleMask(slot.Role));
        }

        /// <summary>Game mask locking a slot to a single job, falling back to "any job" when the
        /// job is unknown.</summary>
        private static ulong GetLockedJobGameMask(uint jobId)
        {
            int gameBit = JobMasks.GetGameJobBitIndex(jobId);
            if (gameBit != -1)
                return 1UL << gameBit;
            return JobMasks.ToGameMask(JobMasks.AllJobsMask);
        }

        private uint GetLocalPlayerJobId()
        {
            return playerState.IsLoaded && playerState.ClassJob.RowId > 0 ? playerState.ClassJob.RowId : 0;
        }

        /// <summary>
        /// The local player as a party member, or null when they aren't loaded yet.
        ///
        /// The party reads deliberately exclude the local player - a list of people to rate should
        /// not offer you yourself. A progression list is the opposite case: "how far is this party"
        /// is a question about the whole party, and leaving yourself out of the answer makes the
        /// list disagree with the 4-of-8 count sitting above it.
        /// </summary>
        public PartyMemberInfo? GetLocalPartyMember()
        {
            if (!clientState.IsLoggedIn)
                return null;

            var self = objectTable.LocalPlayer;
            if (self == null)
                return null;

            string name = self.Name.TextValue;
            if (string.IsNullOrWhiteSpace(name))
                return null;

            return new PartyMemberInfo(
                playerState.ContentId,
                GetLocalPlayerJobId(),
                name,
                self.HomeWorld.RowId,
                string.Empty,
                0,
                false);
        }

        // ══════════════════════════════════════════════════════════
        //  AUTO-ADJUSTED SLOTS
        // ══════════════════════════════════════════════════════════

        /// <summary>
        /// Computes what the game's "Seek Job Distributions" option would do: the local player and
        /// current party members lock their slots, and remaining slots are filled from a standard
        /// 2T/2H/4D composition. Cached briefly to avoid per-frame native reads.
        /// </summary>
        public unsafe List<(RoleType Role, uint? JobId, string Tooltip)> GetAutoAdjustedSlots()
        {
            if (cachedAutoAdjustedSlots != null &&
                DateTime.Now - lastAutoAdjustUpdate < AutoAdjustCacheDuration)
            {
                return cachedAutoAdjustedSlots;
            }

            var result = new List<(RoleType Role, uint? JobId, string Tooltip)>();

            // Slot 1 (index 0) is the local player.
            uint localJobId = GetLocalPlayerJobId();
            string localJobName = JobData.FindById(localJobId)?.Name ?? "Unknown";
            result.Add((RoleType.Free, localJobId > 0 ? localJobId : null, $"Slot 1: You ({localJobName})\nThis slot is locked to your current class/job."));

            // Gather other party members (excluding the local player).
            var otherPartyMembers = GetOtherPartyMembers();

            int partySize = Math.Min(1 + otherPartyMembers.Count, 8);

            // Slots for other party members.
            for (int i = 1; i < partySize; i++)
            {
                var member = otherPartyMembers[i - 1];
                string jobName = JobData.FindById(member.JobId)?.Name ?? "Unknown";
                result.Add((RoleType.Free, member.JobId, $"Slot {i + 1}: {member.Name} ({jobName})\nThis slot is locked to this party member's job."));
            }

            // Standard composition to fill the remaining seats: 2 tanks, pure + barrier healer,
            // 2 melee, 1 phys ranged, 1 magic ranged. Each present member consumes their category.
            var remainingCategories = new List<JobCategory>
            {
                JobCategory.Tank,
                JobCategory.Tank,
                JobCategory.PureHealer,
                JobCategory.BarrierHealer,
                JobCategory.MeleeDPS,
                JobCategory.MeleeDPS,
                JobCategory.PhysRangedDPS,
                JobCategory.MagicRangedDPS,
            };

            var localCategory = JobData.FindById(localJobId)?.Category;
            remainingCategories.Remove(localCategory ?? JobCategory.MeleeDPS);

            for (int i = 1; i < partySize; i++)
            {
                var memberCategory = JobData.FindById(otherPartyMembers[i - 1].JobId)?.Category;
                if (memberCategory.HasValue)
                    remainingCategories.Remove(memberCategory.Value);
            }

            // Fill the remaining empty slots (from partySize up to 8).
            for (int i = partySize; i < 8; i++)
            {
                int catIdx = i - partySize;
                JobCategory category = catIdx < remainingCategories.Count ? remainingCategories[catIdx] : JobCategory.MeleeDPS;
                RoleType role = JobData.GetRoleForCategory(category);
                result.Add((role, null, $"Slot {i + 1}: Auto-Adjusted ({DisplayNames.GetCategoryName(category)})\nThis slot will be automatically filled."));
            }

            cachedAutoAdjustedSlots = result;
            lastAutoAdjustUpdate = DateTime.Now;
            return result;
        }

        // ══════════════════════════════════════════════════════════
        //  DUTY DROPDOWN MATCHING
        // ══════════════════════════════════════════════════════════

        private unsafe int FindDropDownListIndex(AtkComponentDropDownList* dropdown, string targetLabel)
        {
            if (dropdown == null || dropdown->List == null) return -1;

            var list = dropdown->List;
            int count = list->GetItemCount();
            string normalizedTarget = NormalizeDutyName(targetLabel);

            for (int i = 0; i < count; i++)
            {
                string label = AtkHelpers.GetDropDownItemLabel(list, i);
                if (string.IsNullOrEmpty(label)) continue;

                if (NormalizeDutyName(label).Equals(normalizedTarget, StringComparison.OrdinalIgnoreCase))
                    return i;
            }
            return -1;
        }

        /// <summary>Normalizes duty names so the plugin's cached names match the dropdown's
        /// labels regardless of spacing around the difficulty suffix.</summary>
        private static string NormalizeDutyName(string name)
        {
            if (string.IsNullOrEmpty(name))
                return name;

            return name
                .Replace("  ", " ")
                .Replace(" (Ultimate)", "(Ultimate)")
                .Replace(" (Extreme)", "(Extreme)")
                .Replace(" (Savage)", "(Savage)")
                .Trim();
        }
        /// <summary>Null-tolerant SetChecked for optional addon checkboxes.</summary>
        private static unsafe void SetChecked(AtkComponentCheckBox* checkbox, bool value)
        {
            if (checkbox != null)
                checkbox->SetChecked(value);
        }
    }
}
