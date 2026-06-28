using System;
using System.Text;
using System.Linq;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Dalamud.Plugin.Services;
using Dalamud.Game.ClientState.Party;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game.Group;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using FFXIVClientStructs.FFXIV.Component.GUI;
using FFXIVClientStructs.FFXIV.Component.Text;

namespace PfPresets
{
    public class GamePfState
    {
        public string DutyCategory { get; set; } = "None";
        public string DutyName { get; set; } = "None";
        public string Objective { get; set; } = "None";
        public string Roles { get; set; } = "";
        public string Comment { get; set; } = "";
        public string Password { get; set; } = "None";
        public string Completion { get; set; } = "None";
        public string AvgIL { get; set; } = "None";
        public string Flags { get; set; } = "None";
        public string Loot { get; set; } = "Normal";
        public string Lang { get; set; } = "None";
    }

    /// <summary>
    /// Handles the automation of applying a saved preset to the Party Finder.
    /// Interacts directly with the native AgentLookingForGroup memory.
    /// </summary>
    public class PfAutomation : IDisposable
    {
        private readonly IGameGui gameGui;
        private readonly IPluginLog pluginLog;
        private readonly IChatGui chatGui;
        private readonly Configuration config;
        private readonly IClientState clientState;
        private readonly IPlayerState playerState;
        private readonly IFramework framework;
        private readonly DutyDataHelper dutyDataHelper;
        private readonly ICommandManager commandManager;
        private readonly IPartyList partyList;
        private readonly IObjectTable objectTable;
        private readonly ICondition condition;

        // Native "open party finder recruitment for content id" function, imported exactly from
        // the RecruitmentRefresher plugin (https://github.com/anya-hichu/RecruitmentRefresher).
        // This opens the player's own recruitment detail window, which is what makes the
        // auto-refresh actually work (AgentLookingForGroup.OpenListingByContentId does not).
        private const string OpenPartyFinderSignature = "40 53 48 83 EC 20 48 8B D9 E8 ?? ?? ?? ?? 84 C0 74 07 C6 83 ?? ?? ?? ?? ?? 48 83 C4 20 5B C3 CC CC CC CC CC CC CC CC CC CC CC CC CC CC CC CC CC 40 53";
        private unsafe delegate void OpenPartyFinderDelegate(void* agentLfg, ulong contentId);
        private readonly OpenPartyFinderDelegate? openPartyFinder;

        private int refreshCount = 0;
        private double minutesElapsed = 0;
        private string? previousCommentString = null;
        private bool isRefreshExecuting = false;
        private List<(RoleType Role, uint? JobId, string Tooltip)>? cachedAutoAdjustedSlots = null;
        private DateTime lastAutoAdjustUpdate = DateTime.MinValue;
        private readonly TimeSpan autoAdjustCacheDuration = TimeSpan.FromSeconds(1);

        public PfPresetData? ActivePreset { get; private set; }
        public bool ShowChecklist { get; set; } = false;

        public IPlayerState PlayerState => playerState;
        public IObjectTable ObjectTable => objectTable;
        public IPartyList PartyList => partyList;

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
            Done
        }

        private AutomationStep currentStep = AutomationStep.Idle;
        public string AutomationStatus { get; private set; } = "Idle";
        public GamePfState? InitialGameState { get; private set; }

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

        /// <summary>Friendly, normal-user-facing description of what the automation is doing now.</summary>
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
            AutomationStep.SubmittingAddon => "Posting your listing...",
            AutomationStep.Done => IsAutomationFailed ? "Something went wrong while posting." : "All set, PF is up!",
            _ => AutomationStatus,
        };

        private bool waitingForAddon = false;
        private int submitRetryCount = 0;
        private long delayExpirationTime = 0;

        // Retry counter for forcing the duty dropdown list to populate.
        private int dropdownPopulateRetryCount = 0;
        private const int MaxDropdownPopulateAttempts = 5;


        public PfAutomation(
            IGameGui gameGui,
            IPluginLog pluginLog,
            IChatGui chatGui,
            Configuration config,
            IClientState clientState,
            IPlayerState playerState,
            IFramework framework,
            DutyDataHelper dutyDataHelper,
            ICommandManager commandManager,
            IPartyList partyList,
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
            this.commandManager = commandManager;
            this.partyList = partyList;
            this.objectTable = objectTable;
            this.condition = condition;

            // Resolve the native OpenPartyFinder function once at load (imported from
            // RecruitmentRefresher). If the signature can't be found, auto-refresh is disabled
            // gracefully rather than throwing.
            try
            {
                var ptr = sigScanner.ScanText(OpenPartyFinderSignature);
                openPartyFinder = Marshal.GetDelegateForFunctionPointer<OpenPartyFinderDelegate>(ptr);
            }
            catch (Exception ex)
            {
                pluginLog.Error(ex, "[AutoRefresher] Failed to resolve OpenPartyFinder signature; auto-refresh disabled.");
                openPartyFinder = null;
            }
        }

        public void Dispose()
        {
            framework.Update -= OnFrameworkUpdate;
        }

        /// <summary>
        /// Reads the current recruitment settings in game memory.
        /// </summary>
        public unsafe GamePfState ReadGamePfState()
        {
            var state = new GamePfState();
            var agent = AgentLookingForGroup.Instance();
            if (agent == null) return state;

            var recruitment = &agent->StoredRecruitmentInfo;

            // 1. Category
            uint categoryVal = (uint)recruitment->SelectedCategory;
            for (int i = 0; i < DutyCategories.Names.Length; i++)
            {
                if (i == 0 && categoryVal == 0)
                {
                    state.DutyCategory = "None";
                    break;
                }
                else if (i > 0 && (categoryVal & (1 << i)) != 0)
                {
                    state.DutyCategory = DutyCategories.Names[i];
                    break;
                }
            }

            // 2. Duty
            ushort dutyId = recruitment->SelectedDutyId;
            if (dutyId != 0)
            {
                state.DutyName = dutyDataHelper.GetDutyName(dutyId);
            }
            else
            {
                state.DutyName = "None";
            }

            // 3. Objective
            int objVal = (int)recruitment->Objective;
            state.Objective = objVal switch
            {
                2 => "Duty Completion",
                4 => "Practice",
                8 => "Loot",
                _ => "None"
            };

            // 4. Roles
            var rolesList = new List<string>();
            ulong* pSlotFlags = (ulong*)((byte*)recruitment + 0x1B0);
            for (int i = 0; i < 8; i++)
            {
                rolesList.Add(GetGameSlotSummary(ConvertGameMaskToBitIndex(pSlotFlags[i])));
            }
            state.Roles = string.Join(" | ", rolesList);

            // 5. Comment
            byte* pComment = (byte*)recruitment + 0x330;
            state.Comment = GetFixedString(pComment, 192);

            // 6. Password
            ushort pwd = recruitment->Password;
            state.Password = pwd == 10000 ? "None" : pwd.ToString("D4");

            // 7. Completion Status
            int compVal = (int)recruitment->CompletionStatus;
            state.Completion = compVal switch
            {
                2 => "Duty Complete",
                4 => "Duty Incomplete",
                8 => "Weekly Unclaimed",
                _ => "None"
            };

            // 8. Avg Item Level
            state.AvgIL = agent->AvgItemLvEnabled != 0 ? agent->AvgItemLv.ToString() : "None";

            // 9. Flags
            var flagsList = new List<string>();
            var dfSetting = recruitment->DutyFinderSettingFlags;
            if (dfSetting.HasFlag(AgentLookingForGroup.DutyFinderSetting.UnrestrictedParty)) flagsList.Add("Unrestricted");
            if (dfSetting.HasFlag(AgentLookingForGroup.DutyFinderSetting.MinimumIL)) flagsList.Add("Minimum IL");
            if (dfSetting.HasFlag(AgentLookingForGroup.DutyFinderSetting.SilenceEcho)) flagsList.Add("Silence Echo");
            if (recruitment->OnePlayerPerJob != 0) flagsList.Add("One Per Job");
            if (recruitment->LimitRecruitingToWorld == 0) flagsList.Add("Limit to World");
            state.Flags = flagsList.Count > 0 ? string.Join(", ", flagsList) : "None";

            // 10. Loot
            state.Loot = GetLootRuleName((int)recruitment->LootRule);

            // 11. Languages
            var langList = new List<string>();
            var lang = recruitment->LanguageFlags;
            if (lang.HasFlag(AgentLookingForGroup.Language.Japanese)) langList.Add("J");
            if (lang.HasFlag(AgentLookingForGroup.Language.English)) langList.Add("E");
            if (lang.HasFlag(AgentLookingForGroup.Language.German)) langList.Add("D");
            if (lang.HasFlag(AgentLookingForGroup.Language.French)) langList.Add("F");
            state.Lang = langList.Count > 0 ? string.Join(",", langList) : "None";

            return state;
        }

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

                // Mark the preset as used
                config.MarkPresetUsed(preset.Id);
                ActivePreset = preset;
                submitRetryCount = 0;
                delayExpirationTime = 0;
                dropdownPopulateRetryCount = 0;

                var agent = AgentLookingForGroup.Instance();
                if (agent == null)
                {
                    pluginLog.Warning("AgentLookingForGroup not available.");
                    chatGui.Print("[PF Presets] Could not access Party Finder. Make sure you are logged in.");
                    return;
                }

                if (!clientState.IsLoggedIn)
                {
                    chatGui.Print("[PF Presets] You are not logged in.");
                    return;
                }

                // 1. Capture the initial state before writing preset settings
                InitialGameState = ReadGamePfState();
                pluginLog.Information($"Captured initial PF state: Comment='{InitialGameState.Comment}', Password='{InitialGameState.Password}'");

                // Check if the addon is already open
                var addonPtr = (nint)gameGui.GetAddonByName("LookingForGroupCondition");
                if (addonPtr != IntPtr.Zero)
                {
                    var addon = (AddonLookingForGroupCondition*)addonPtr;
                    if (addon != null && addon->AtkUnitBase.IsVisible)
                    {
                        pluginLog.Information("LookingForGroupCondition is already open. Closing it first to apply new settings.");
                        AutomationStatus = "Closing active PF window...";
                        currentStep = AutomationStep.ClosingAddon;
                        waitingForAddon = true;

                        if (addon->CancelButton != null)
                        {
                            ClickButton(&addon->AtkUnitBase, addon->CancelButton);
                        }

                        framework.Update -= OnFrameworkUpdate;
                        framework.Update += OnFrameworkUpdate;
                        ShowChecklist = true;
                        chatGui.Print($"[PF Presets] Applying preset: {preset.Name}...");
                        return;
                    }
                }

                // If not open, write settings and open it directly
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

            var recruitment = &agent->StoredRecruitmentInfo;

            // Duty Category & selected ID
            recruitment->SelectedCategory = preset.DutyCategoryId == 0
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
                var duties = dutyDataHelper.GetDutiesByType(preset.DutyCategoryName);
                var duty = duties.FirstOrDefault(d => d.Name.Equals(preset.DutyName, StringComparison.OrdinalIgnoreCase));

                // If exact duty not found, try to find a duty with similar name
                if (duty == null && duties.Count > 0)
                {
                    // Try fuzzy matching
                    duty = duties.FirstOrDefault(d => d.Name.Contains(preset.DutyName, StringComparison.OrdinalIgnoreCase))
                         ?? duties.FirstOrDefault(d => preset.DutyName.Contains(d.Name, StringComparison.OrdinalIgnoreCase));

                    if (duty != null)
                    {
                        pluginLog.Warning($"Exact duty '{preset.DutyName}' not found in category '{preset.DutyCategoryName}'. Using similar duty '{duty.Name}' instead.");
                    }
                    else
                    {
                        pluginLog.Warning($"Duty '{preset.DutyName}' not found in category '{preset.DutyCategoryName}'. No similar duties found. Will use first duty in category as fallback.");
                        // Use first duty as fallback to avoid "All" selection
                        if (duties.Count > 0)
                        {
                            duty = duties[0];
                        }
                    }
                }

                if (duty != null)
                {
                    dutyId = (ushort)duty.RowId;
                    pluginLog.Information($"Setting duty ID {dutyId} for duty '{duty.Name}'");
                }
                else
                {
                    pluginLog.Warning($"No duty could be determined. SelectedDutyId will remain 0 (All duties).");
                }
            }
            recruitment->SelectedDutyId = dutyId;

            // Undocumented byte at offset 0x12 (right after SelectedDutyId): the game sets this
            // to 2 when a *specific* duty is picked through the dropdown, and leaves it 0 for
            // "any duty in the category". Without it the listing falls back to "All" even though
            // SelectedDutyId is set. Discovered by diffing recruitment memory before/after a
            // manual selection.
            *((byte*)recruitment + 0x12) = (byte)(dutyId != 0 ? 2 : 0);
        }

        private unsafe void WriteGeneralSettingsToMemory(PfPresetData preset)
        {
            var agent = AgentLookingForGroup.Instance();
            if (agent == null) return;

            var recruitment = &agent->StoredRecruitmentInfo;

            // Objective - validate range
            if (preset.DutyCategoryId > 0)
            {
                int objectiveId = Math.Clamp(preset.ObjectiveId, 0, 3);
                recruitment->Objective = (AgentLookingForGroup.Objective)(1 << objectiveId);
            }
            else
            {
                recruitment->Objective = AgentLookingForGroup.Objective.None;
            }
            recruitment->BeginnerFriendly = 0;

            // Completion Status - validate range
            AgentLookingForGroup.CompletionStatus compStatus = AgentLookingForGroup.CompletionStatus.None;
            if (preset.CompletionStatusEnabled)
            {
                int completionType = Math.Clamp(preset.CompletionStatusType, 0, 2);
                compStatus = completionType switch
                {
                    0 => AgentLookingForGroup.CompletionStatus.DutyComplete,
                    1 => AgentLookingForGroup.CompletionStatus.DutyCompleteWeeklyUnclaimed,
                    2 => AgentLookingForGroup.CompletionStatus.DutyIncomplete,
                    _ => AgentLookingForGroup.CompletionStatus.None
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

            // Avg Item Level (set on agent directly) - validate range
            int avgItemLv = Math.Clamp(preset.AvgItemLv, 1, 99999);
            agent->AvgItemLv = (ushort)avgItemLv;
            agent->AvgItemLvEnabled = (byte)(preset.AvgItemLvEnabled ? 1 : 0);

            // Loot rules - validate range
            int lootRules = Math.Clamp(preset.LootRules, 0, 2);
            recruitment->LootRule = (AgentLookingForGroup.LootRule)lootRules;

            // Password - validate range
            ushort pwd = 10000;
            if (preset.FormPrivateParty)
            {
                if (ushort.TryParse(preset.PrivatePartyPassword, out ushort val))
                {
                    // Ensure password is in valid range (0-9999)
                    pwd = Math.Clamp(val, (ushort)0, (ushort)9999);
                }
                else
                {
                    pwd = 0;
                }
            }
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

            // Comment (write to 192-byte fixed buffer via offset 0x330) - validate length
            string safeComment = preset.Comment.Length > 191 ? preset.Comment.Substring(0, 191) : preset.Comment;
            byte* pComment = (byte*)recruitment + 0x330;
            SetFixedString(pComment, safeComment, 192);
        }

        private static JobCategory? GetJobCategory(uint jobId)
        {
            var job = JobData.AllJobsAndClasses.FirstOrDefault(j => j.Id == (int)jobId);
            return job?.Category;
        }

        public unsafe List<(RoleType Role, uint? JobId, string Tooltip)> GetAutoAdjustedSlots()
        {
            // Use cached result if it's still valid
            if (cachedAutoAdjustedSlots != null &&
                DateTime.Now - lastAutoAdjustUpdate < autoAdjustCacheDuration)
            {
                return cachedAutoAdjustedSlots;
            }

            var result = new List<(RoleType Role, uint? JobId, string Tooltip)>();

            // Determine local player's job
            uint localJobId = 0;
            if (playerState.IsLoaded && playerState.ClassJob.RowId > 0)
            {
                localJobId = playerState.ClassJob.RowId;
            }

            // Slot 1 (index 0) is local player
            var localJobInfo = JobData.AllJobsAndClasses.FirstOrDefault(j => j.Id == (int)localJobId);
            string localJobName = localJobInfo?.Name ?? "Unknown";
            result.Add((RoleType.Free, localJobId > 0 ? localJobId : null, $"Slot 1: You ({localJobName})\nThis slot is locked to your current class/job."));

            // Gather other party members (excluding local player)
            var otherPartyMembers = new List<(uint JobId, string Name, ulong ContentId)>();
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
                            otherPartyMembers.Add((member.ClassJobId, member.NameString, member.ContentId));
                        }
                    }
                }
            }
            else
            {
                // Use the InfoProxyPartyMember (the data backing the social/party tab).
                // Unlike IPartyList, this contains every party member's job regardless of
                // which zone/map they are in, so detection no longer breaks when members
                // are spread across different areas.
                var partyInfo = InfoProxyPartyMember.Instance();
                if (partyInfo != null)
                {
                    uint count = partyInfo->GetEntryCount();
                    for (uint i = 0; i < count; i++)
                    {
                        var entry = partyInfo->GetEntry(i);
                        if (entry == null) continue;
                        if (entry->ContentId == playerState.ContentId) continue;
                        otherPartyMembers.Add(((uint)entry->Job, entry->NameString, entry->ContentId));
                    }
                }
            }

            int partySize = 1 + otherPartyMembers.Count;
            if (partySize > 8) partySize = 8;

            // Slots for other party members
            for (int i = 1; i < partySize; i++)
            {
                var member = otherPartyMembers[i - 1];
                uint jobId = member.JobId;
                var jobInfo = JobData.AllJobsAndClasses.FirstOrDefault(j => j.Id == (int)jobId);
                string jobName = jobInfo?.Name ?? "Unknown";
                string memberName = member.Name;
                result.Add((RoleType.Free, jobId, $"Slot {i + 1}: {memberName} ({jobName})\nThis slot is locked to this party member's job."));
            }

            // Determine remaining categories to fill
            var remainingCategories = new List<JobCategory>
            {
                JobCategory.Tank,
                JobCategory.Tank,
                JobCategory.PureHealer,
                JobCategory.BarrierHealer,
                JobCategory.MeleeDPS,
                JobCategory.MeleeDPS,
                JobCategory.PhysRangedDPS,
                JobCategory.MagicRangedDPS
            };

            // Remove local player's category
            var localCategory = GetJobCategory(localJobId);
            if (localCategory.HasValue)
            {
                remainingCategories.Remove(localCategory.Value);
            }
            else
            {
                remainingCategories.Remove(JobCategory.MeleeDPS);
            }

            // Remove other party members' categories
            for (int i = 1; i < partySize; i++)
            {
                var member = otherPartyMembers[i - 1];
                var memberCategory = GetJobCategory(member.JobId);
                if (memberCategory.HasValue)
                {
                    remainingCategories.Remove(memberCategory.Value);
                }
            }

            // Fill the remaining empty slots (from partySize up to 8)
            for (int i = partySize; i < 8; i++)
            {
                JobCategory category = JobCategory.MeleeDPS;
                int catIdx = i - partySize;
                if (catIdx >= 0 && catIdx < remainingCategories.Count)
                {
                    category = remainingCategories[catIdx];
                }

                RoleType role = category switch
                {
                    JobCategory.Tank => RoleType.Tank,
                    JobCategory.PureHealer => RoleType.Healer,
                    JobCategory.BarrierHealer => RoleType.Healer,
                    JobCategory.MeleeDPS => RoleType.MeleeDPS,
                    JobCategory.PhysRangedDPS => RoleType.PhysRangedDPS,
                    JobCategory.MagicRangedDPS => RoleType.MagicRangedDPS,
                    _ => RoleType.Free
                };

                result.Add((role, null, $"Slot {i + 1}: Auto-Adjusted ({GetCategoryDisplayName(category)})\nThis slot will be automatically filled."));
            }

            // Cache the result to avoid frequent memory access
            cachedAutoAdjustedSlots = result;
            lastAutoAdjustUpdate = DateTime.Now;

            return result;
        }

        private static string GetCategoryDisplayName(JobCategory category)
        {
            return category switch
            {
                JobCategory.Tank => "Tank",
                JobCategory.PureHealer => "Pure Healer",
                JobCategory.BarrierHealer => "Barrier Healer",
                JobCategory.MeleeDPS => "Melee DPS",
                JobCategory.PhysRangedDPS => "Physical Ranged DPS",
                JobCategory.MagicRangedDPS => "Magical Ranged DPS",
                _ => category.ToString()
            };
        }

        private unsafe void WriteSlotFlagsToMemory(PfPresetData preset)
        {
            var agent = AgentLookingForGroup.Instance();
            if (agent == null) return;

            var recruitment = &agent->StoredRecruitmentInfo;
            ulong* pSlotFlags = (ulong*)((byte*)recruitment + 0x1B0);

            // Determine local player's job
            uint localJobId = 0;
            if (playerState.IsLoaded && playerState.ClassJob.RowId > 0)
            {
                localJobId = playerState.ClassJob.RowId;
            }

            if (preset.AutoAdjustRoles)
            {
                var autoSlots = GetAutoAdjustedSlots();
                for (int i = 0; i < 8; i++)
                {
                    if (i >= autoSlots.Count)
                    {
                        pSlotFlags[i] = 0;
                        continue;
                    }

                    var slot = autoSlots[i];
                    if (slot.JobId.HasValue)
                    {
                        // Specific locked job
                        int gameBit = GetGameJobBitIndex(slot.JobId.Value);
                        if (gameBit != -1)
                        {
                            pSlotFlags[i] = 1UL << gameBit;
                        }
                        else
                        {
                            pSlotFlags[i] = ConvertBitIndexToGameMask(AllJobsMask);
                        }
                    }
                    else
                    {
                        // Auto-filled role
                        if (slot.Role == RoleType.Free)
                            pSlotFlags[i] = ConvertBitIndexToGameMask(AllJobsMask);
                        else
                            pSlotFlags[i] = ConvertBitIndexToGameMask(GetGameRoleMask(slot.Role));
                    }
                }
                return;
            }

            // Slot 1 is locked to the player's current class/job (when not auto-adjusting)
            if (localJobId > 0)
            {
                int gameBit = GetGameJobBitIndex(localJobId);
                if (gameBit != -1)
                {
                    pSlotFlags[0] = 1UL << gameBit;
                }
                else
                {
                    pSlotFlags[0] = ConvertBitIndexToGameMask(AllJobsMask);
                }
            }
            else
            {
                pSlotFlags[0] = ConvertBitIndexToGameMask(AllJobsMask);
            }

            for (int i = 1; i < 8; i++)
            {
                if (i < preset.Slots.Count)
                {
                    var slot = preset.Slots[i];
                    if (slot.Role == RoleType.Omit)
                    {
                        pSlotFlags[i] = 0; // Empty / disabled slot
                    }
                    else if (slot.AcceptedJobFlags != 0)
                    {
                        pSlotFlags[i] = ConvertBitIndexToGameMask(slot.AcceptedJobFlags);
                    }
                    else
                    {
                        if (slot.Role == RoleType.Free)
                            pSlotFlags[i] = ConvertBitIndexToGameMask(AllJobsMask);
                        else
                            pSlotFlags[i] = ConvertBitIndexToGameMask(GetGameRoleMask(slot.Role));
                    }
                }
                else
                {
                    pSlotFlags[i] = 0;
                }
            }
        }

        private unsafe void OpenAddonWindow()
        {
            if (ActivePreset == null) return;

            var agent = AgentLookingForGroup.Instance();
            if (agent == null) return;

            pluginLog.Information("Opening LFG window first natively via agent...");

            // Check if LookingForGroup is already open and visible
            var lfgAddonPtr = (nint)gameGui.GetAddonByName("LookingForGroup");
            if (lfgAddonPtr != IntPtr.Zero)
            {
                var lfgAddon = (AtkUnitBase*)lfgAddonPtr;
                if (lfgAddon != null && lfgAddon->IsVisible)
                {
                    // It is already open! Just transition to OpeningLfg step and set a non-blocking delay
                    pluginLog.Information("LookingForGroup is already visible. Transitioning to OpeningLfg step.");
                    AutomationStatus = "Opening recruitment window...";
                    currentStep = AutomationStep.OpeningLfg;
                    waitingForAddon = true;
                    delayExpirationTime = Environment.TickCount64 + 500; // wait 500ms

                    framework.Update -= OnFrameworkUpdate;
                    framework.Update += OnFrameworkUpdate;
                    ShowChecklist = true;
                    chatGui.Print($"[PF Presets] Applying preset: {ActivePreset.Name}...");
                    return;
                }
            }

            // If not open, open via agent->Show()
            AutomationStatus = "Opening Party Finder...";
            currentStep = AutomationStep.OpeningLfg;
            waitingForAddon = true;
            delayExpirationTime = Environment.TickCount64 + 500; // wait 500ms for UI to open

            agent->Show();

            framework.Update -= OnFrameworkUpdate;
            framework.Update += OnFrameworkUpdate;
            ShowChecklist = true;
        }

        private unsafe void OnFrameworkUpdate(IFramework _)
        {
            if (ActivePreset == null || !waitingForAddon) return;
            if (Environment.TickCount64 < delayExpirationTime) return; // Non-blocking wait

            var addonPtr = (nint)gameGui.GetAddonByName("LookingForGroupCondition");
            var lfgAddonPtr = (nint)gameGui.GetAddonByName("LookingForGroup");

            if (currentStep == AutomationStep.ClosingAddon)
            {
                // Wait for the window to close fully
                if (addonPtr == IntPtr.Zero)
                {
                    pluginLog.Information("Addon closed. Proceeding to open LFG / PF window.");
                    OpenAddonWindow();
                }
                return;
            }

            if (currentStep == AutomationStep.OpeningLfg)
            {
                if (lfgAddonPtr == IntPtr.Zero) return;
                var lfgAddon = (AtkUnitBase*)lfgAddonPtr;
                if (lfgAddon == null || !lfgAddon->IsVisible) return;

                var recruitBtn = lfgAddon->GetComponentButtonById(46);
                if (recruitBtn != null && recruitBtn->IsEnabled)
                {
                    pluginLog.Information("LookingForGroup window is visible. Writing memory and clicking Recruit Members button.");
                    WriteSettingsToMemory(ActivePreset);
                    AutomationStatus = "Opening recruitment window...";
                    currentStep = AutomationStep.OpeningAddon;
                    delayExpirationTime = Environment.TickCount64 + 500; // wait 500ms for recruitment window to open
                    ClickButton(lfgAddon, recruitBtn);
                }
                return;
            }

            if (currentStep == AutomationStep.OpeningAddon)
            {
                if (addonPtr == IntPtr.Zero) return;
                var addon = (AddonLookingForGroupCondition*)addonPtr;
                if (addon == null || !addon->AtkUnitBase.IsVisible) return;

                pluginLog.Information("LookingForGroupCondition is visible. Transitioning to writing duty category.");
                AutomationStatus = "Selecting duty category...";
                currentStep = AutomationStep.WritingDutyCategory;
                delayExpirationTime = Environment.TickCount64 + 100; // wait 100ms
                return;
            }

            if (currentStep == AutomationStep.WritingDutyCategory)
            {
                if (addonPtr == IntPtr.Zero) return;
                var addon = (AddonLookingForGroupCondition*)addonPtr;
                if (addon == null || !addon->AtkUnitBase.IsVisible) return;

                pluginLog.Information("Writing duty category to memory...");
                WriteDutyCategoryToMemory(ActivePreset);

                currentStep = AutomationStep.DelayingAfterCategory;
                delayExpirationTime = Environment.TickCount64 + 500; // wait 500ms
                return;
            }

            if (currentStep == AutomationStep.DelayingAfterCategory)
            {
                if (addonPtr == IntPtr.Zero) return;
                var addon = (AddonLookingForGroupCondition*)addonPtr;
                if (addon == null || !addon->AtkUnitBase.IsVisible) return;

                // No category means there is no duty to select. Skip straight to writing the
                // (empty) duty id so SelectedDutyId is cleared to 0.
                if (ActivePreset.DutyCategoryId == 0)
                {
                    pluginLog.Information("No duty category set. Skipping duty dropdown selection.");
                    currentStep = AutomationStep.WritingDutyId;
                    delayExpirationTime = Environment.TickCount64 + 100; // wait 100ms
                    return;
                }

                pluginLog.Information("Delay after category complete. Preparing duty dropdown...");
                dropdownPopulateRetryCount = 0;
                currentStep = AutomationStep.OpeningDutyDropdown;
                delayExpirationTime = Environment.TickCount64 + 100; // wait 100ms
                return;
            }

            if (currentStep == AutomationStep.OpeningDutyDropdown)
            {
                if (addonPtr == IntPtr.Zero) return;
                var addon = (AddonLookingForGroupCondition*)addonPtr;
                if (addon == null || !addon->AtkUnitBase.IsVisible) return;

                var dropdown = addon->DutyDropDown;
                if (dropdown == null)
                {
                    pluginLog.Warning("Duty dropdown is null. Falling back to direct memory write only.");
                    currentStep = AutomationStep.WritingDutyId;
                    delayExpirationTime = Environment.TickCount64 + 100; // wait 100ms
                    return;
                }

                // The game populates the duty list lazily. If it is already populated we can
                // select immediately; otherwise open the dropdown to force the game to build
                // the list - this mirrors a manual user interaction with the menu.
                if (IsDutyListPopulated(dropdown))
                {
                    pluginLog.Information("Duty list is populated. Proceeding to selection.");
                    currentStep = AutomationStep.SelectingDuty;
                    delayExpirationTime = Environment.TickCount64 + 100; // wait 100ms
                    return;
                }

                if (dropdownPopulateRetryCount >= MaxDropdownPopulateAttempts)
                {
                    pluginLog.Warning($"Duty list did not populate after {MaxDropdownPopulateAttempts} attempts. Attempting selection anyway.");
                    currentStep = AutomationStep.SelectingDuty;
                    delayExpirationTime = Environment.TickCount64 + 100; // wait 100ms
                    return;
                }

                dropdownPopulateRetryCount++;
                pluginLog.Information($"Opening duty dropdown to populate list (attempt {dropdownPopulateRetryCount}/{MaxDropdownPopulateAttempts})...");
                OpenDropDownList(dropdown);
                delayExpirationTime = Environment.TickCount64 + 300; // wait for the list to build, then re-check
                return;
            }

            if (currentStep == AutomationStep.SelectingDuty)
            {
                if (addonPtr == IntPtr.Zero) return;
                var addon = (AddonLookingForGroupCondition*)addonPtr;
                if (addon == null || !addon->AtkUnitBase.IsVisible) return;

                // Update the dropdown's visible selection so the in-game window matches the
                // preset. This is purely cosmetic - the authoritative duty is written to memory
                // in WritingDutyId.
                var dropdown = addon->DutyDropDown;
                if (dropdown != null && IsDutyListPopulated(dropdown))
                {
                    int index = FindDropDownListIndex(dropdown, ActivePreset.DutyName);
                    if (index >= 0)
                    {
                        SelectDutyDropDownIndex(dropdown, index);
                    }
                    else
                    {
                        pluginLog.Warning($"Duty '{ActivePreset.DutyName}' not found in dropdown list.");
                    }
                }

                currentStep = AutomationStep.WritingDutyId;
                delayExpirationTime = Environment.TickCount64 + 300; // wait 300ms
                return;
            }

            if (currentStep == AutomationStep.WritingDutyId)
            {
                if (addonPtr == IntPtr.Zero) return;
                var addon = (AddonLookingForGroupCondition*)addonPtr;
                if (addon == null || !addon->AtkUnitBase.IsVisible) return;

                // Close the duty dropdown if it is still open from the populate/select steps.
                if (addon->DutyDropDown != null && addon->DutyDropDown->IsOpen)
                {
                    CloseDropDownList(addon->DutyDropDown);
                }

                // The submit reads agent->SelectedDutyId (plus the 0x12 "specific duty" flag)
                // directly; the dropdown's visual selection does not feed it. Write the resolved
                // duty to memory here - this is the authoritative step.
                WriteDutyIdToMemory(ActivePreset);

                currentStep = AutomationStep.DelayingAfterDutyId;
                delayExpirationTime = Environment.TickCount64 + 500; // wait 500ms
                return;
            }

            if (currentStep == AutomationStep.DelayingAfterDutyId)
            {
                pluginLog.Information("Delay after duty ID complete. Transitioning to writing roles.");
                AutomationStatus = "Setting up roles...";
                currentStep = AutomationStep.WritingRoles;
                delayExpirationTime = Environment.TickCount64 + 100; // wait 100ms
                return;
            }

            if (currentStep == AutomationStep.WritingRoles)
            {
                if (addonPtr == IntPtr.Zero) return;
                var addon = (AddonLookingForGroupCondition*)addonPtr;
                if (addon == null || !addon->AtkUnitBase.IsVisible) return;

                pluginLog.Information("Writing slot flags to memory...");
                WriteSlotFlagsToMemory(ActivePreset);

                currentStep = AutomationStep.DelayingAfterRoles;
                delayExpirationTime = Environment.TickCount64 + 500; // wait 500ms
                return;
            }

            if (currentStep == AutomationStep.DelayingAfterRoles)
            {
                pluginLog.Information("Delay after roles complete. Transitioning to writing general settings.");
                AutomationStatus = "Syncing settings directly to UI...";
                currentStep = AutomationStep.WritingEverythingElse;
                delayExpirationTime = Environment.TickCount64 + 100; // wait 100ms
                return;
            }

            if (currentStep == AutomationStep.WritingEverythingElse)
            {
                if (addonPtr == IntPtr.Zero) return;
                var addon = (AddonLookingForGroupCondition*)addonPtr;
                if (addon == null || !addon->AtkUnitBase.IsVisible) return;

                pluginLog.Information("Writing general settings to memory...");
                WriteGeneralSettingsToMemory(ActivePreset);

                // Direct updates to UI Controls to make absolutely sure they are synced and visually correct

                // 1. Comment
                if (addon->CommentTextInput != null)
                {
                    addon->CommentTextInput->SetText(ActivePreset.Comment);
                }

                // 2. Private party passcode
                if (addon->FormPrivatePartyCheckbox != null)
                {
                    addon->FormPrivatePartyCheckbox->SetChecked(ActivePreset.FormPrivateParty);
                    if (ActivePreset.FormPrivateParty && addon->PasswordNumericInput != null)
                    {
                        if (int.TryParse(ActivePreset.PrivatePartyPassword, out int pwdVal))
                        {
                            addon->PasswordNumericInput->SetValue(pwdVal);
                            addon->PasswordNumericInput->Value = pwdVal;
                            addon->PasswordNumericInput->UpdateTextNode();
                        }
                    }
                }

                // 3. Checkboxes
                if (addon->LimitToWorldServerCheckbox != null)
                {
                    addon->LimitToWorldServerCheckbox->SetChecked(ActivePreset.LimitRecruitingToWorld);
                }

                if (addon->OnePlayerPerJobCheckbox != null)
                {
                    addon->OnePlayerPerJobCheckbox->SetChecked(ActivePreset.OnePlayerPerJob);
                }

                if (addon->BeginnersWelcomeCheckBox != null)
                {
                    addon->BeginnersWelcomeCheckBox->SetChecked(false);
                }

                if (addon->CompletionStatusCheckBox != null)
                {
                    addon->CompletionStatusCheckBox->SetChecked(ActivePreset.CompletionStatusEnabled);
                }

                // Force Duty Finder settings to be unchecked if category is None (0) to prevent native button greying out
                bool hasDuty = ActivePreset.DutyCategoryId > 0;

                if (addon->UnrestrictedPartyCheckBox != null)
                {
                    addon->UnrestrictedPartyCheckBox->SetChecked(hasDuty && ActivePreset.UnrestrictedParty);
                }

                if (addon->MinimumItemLevelCheckBox != null)
                {
                    addon->MinimumItemLevelCheckBox->SetChecked(hasDuty && ActivePreset.MinimumIL);
                }

                if (addon->SilenceEchoCheckbox != null)
                {
                    addon->SilenceEchoCheckbox->SetChecked(hasDuty && ActivePreset.SilenceEcho);
                }

                if (addon->RemoveRoleRestrictionsCheckBox != null)
                {
                    addon->RemoveRoleRestrictionsCheckBox->SetChecked(ActivePreset.RemoveRoleRestrictions);
                }

                // 4. Avg Item Level
                if (addon->AvgItemLevelCheckbox != null)
                {
                    addon->AvgItemLevelCheckbox->SetChecked(ActivePreset.AvgItemLvEnabled);
                    if (ActivePreset.AvgItemLvEnabled && addon->AvgItemLevelNumericInput != null)
                    {
                        addon->AvgItemLevelNumericInput->SetValue(ActivePreset.AvgItemLv);
                        addon->AvgItemLevelNumericInput->Value = ActivePreset.AvgItemLv;
                        addon->AvgItemLevelNumericInput->UpdateTextNode();
                    }
                }

                // 5. Languages
                AtkComponentCheckBox** pLanguages = (AtkComponentCheckBox**)((byte*)addon + 0x378);
                if (pLanguages != null)
                {
                    if (pLanguages[0] != null) pLanguages[0]->SetChecked(ActivePreset.LangJapanese);
                    if (pLanguages[1] != null) pLanguages[1]->SetChecked(ActivePreset.LangEnglish);
                    if (pLanguages[2] != null) pLanguages[2]->SetChecked(ActivePreset.LangGerman);
                    if (pLanguages[3] != null) pLanguages[3]->SetChecked(ActivePreset.LangFrench);
                }

                pluginLog.Information("Sync complete. Delaying to allow UI changes to stabilize...");
                currentStep = AutomationStep.DelayingAfterSync;
                delayExpirationTime = Environment.TickCount64 + 500; // wait 500ms
                return;
            }

            if (currentStep == AutomationStep.DelayingAfterSync)
            {
                pluginLog.Information("Stabilization delay complete. Proceeding to submit.");
                currentStep = AutomationStep.SubmittingAddon;
                delayExpirationTime = Environment.TickCount64 + 200; // wait 200ms
                return;
            }

            if (currentStep == AutomationStep.SubmittingAddon)
            {
                if (addonPtr == IntPtr.Zero) return;
                var addon = (AddonLookingForGroupCondition*)addonPtr;
                if (addon == null || !addon->AtkUnitBase.IsVisible) return;

                // Programmatically click the Recruit button
                if (addon->RecruitMembersButton != null && addon->RecruitMembersButton->IsEnabled)
                {
                    // Re-assert the duty id one last time, on the same frame as the click, so
                    // it is guaranteed correct when the game reads it on submit.
                    WriteDutyIdToMemory(ActivePreset);
                    pluginLog.Information("Clicking PF Recruit button...");
                    AutomationStatus = "Submitting Party Finder listing...";
                    ClickButton(&addon->AtkUnitBase, addon->RecruitMembersButton);

                    currentStep = AutomationStep.Done;
                    AutomationStatus = "Recruitment Listing Created!";
                    framework.Update -= OnFrameworkUpdate;
                    waitingForAddon = false;
                }
                else
                {
                    submitRetryCount++;
                    if (submitRetryCount > 100)
                    {
                        pluginLog.Warning("RecruitMembersButton is not enabled or null after 100 frames! Aborting.");
                        currentStep = AutomationStep.Done;
                        AutomationStatus = "Recruitment Failed: Button not enabled";
                        framework.Update -= OnFrameworkUpdate;
                        waitingForAddon = false;
                    }
                }
            }
        }

        private unsafe void ClickButton(AtkUnitBase* addon, AtkComponentButton* button)
        {
            if (button == null) return;

            var resNode = button->AtkComponentBase.OwnerNode != null
                ? &button->AtkComponentBase.OwnerNode->AtkResNode
                : button->AtkComponentBase.AtkResNode;
            if (resNode == null) return;

            var evt = (AtkEvent*)resNode->AtkEventManager.Event;
            if (evt != null)
            {
                // Create a copy of the native event on the stack to preserve parameters
                // but force the event type to ButtonClick.
                AtkEvent simulatedEvent = *evt;
                simulatedEvent.State.EventType = AtkEventType.ButtonClick;
                simulatedEvent.State.ReturnFlags = 0;
                simulatedEvent.State.StateFlags = 0;
                addon->ReceiveEvent(AtkEventType.ButtonClick, (int)evt->Param, &simulatedEvent);
            }
            else
            {
                // Create a simulated click event on the stack
                AtkEvent simulatedEvent = new AtkEvent
                {
                    Node = resNode,
                    Target = (AtkEventTarget*)&resNode->AtkEventTarget,
                    Listener = (AtkEventListener*)addon,
                    Param = resNode->NodeId
                };
                simulatedEvent.State.EventType = AtkEventType.ButtonClick;
                simulatedEvent.State.ReturnFlags = 0;
                simulatedEvent.State.StateFlags = 0;

                addon->ReceiveEvent(AtkEventType.ButtonClick, (int)resNode->NodeId, &simulatedEvent);
            }
        }

        /// <summary>
        /// Reads the label for a single dropdown item using the game's native accessor.
        /// </summary>
        private static unsafe string GetDropDownItemLabel(AtkComponentList* list, int index)
        {
            var cstr = list->GetItemLabel(index);
            if (cstr.Value == null) return string.Empty;
            return System.Runtime.InteropServices.Marshal.PtrToStringUTF8((nint)cstr.Value) ?? string.Empty;
        }

        private unsafe int FindDropDownListIndex(AtkComponentDropDownList* dropdown, string targetLabel)
        {
            if (dropdown == null || dropdown->List == null) return -1;

            var list = dropdown->List;
            int count = list->GetItemCount();
            string normalizedTarget = NormalizeDutyName(targetLabel);

            for (int i = 0; i < count; i++)
            {
                string label = GetDropDownItemLabel(list, i);
                if (string.IsNullOrEmpty(label)) continue;

                string normalizedLabel = NormalizeDutyName(label);
                if (normalizedLabel.Equals(normalizedTarget, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }
            return -1;
        }

        private string NormalizeDutyName(string name)
        {
            if (string.IsNullOrEmpty(name))
                return name;

            // Normalize by removing extra spaces and standardizing formatting
            return name
                .Replace("  ", " ") // Remove double spaces
                .Replace(" (Ultimate)", "(Ultimate)") // Standardize ultimate formatting
                .Replace(" (Extreme)", "(Extreme)") // Standardize extreme formatting
                .Replace(" (Savage)", "(Savage)") // Standardize savage formatting
                .Trim();
        }

        /// <summary>
        /// Returns true when the duty dropdown's item list has been built by the game
        /// (i.e. it has at least one entry with a readable label).
        /// </summary>
        private unsafe bool IsDutyListPopulated(AtkComponentDropDownList* dropdown)
        {
            return dropdown != null &&
                   dropdown->List != null &&
                   dropdown->List->GetItemCount() > 0;
        }

        /// <summary>
        /// Selects an item in the duty dropdown using the game's own native SelectItem routine.
        /// This is the exact code path a manual click triggers, so the agent registers the
        /// selected duty correctly (something a raw memory write to SelectedDutyId cannot do
        /// until the dropdown has been interacted with at least once per session).
        /// </summary>
        private unsafe void SelectDutyDropDownIndex(AtkComponentDropDownList* dropdown, int index)
        {
            int count = (dropdown != null && dropdown->List != null) ? dropdown->List->GetItemCount() : 0;
            if (dropdown == null || dropdown->List == null || index < 0 || index >= count)
            {
                pluginLog.Warning($"SelectDutyDropDownIndex: invalid parameters. index={index}, count={count}");
                return;
            }

            try
            {
                // Update both the displayed text and the underlying list selection so the in-game
                // dropdown visually reflects the preset's duty.
                dropdown->SelectItem(index);
                dropdown->List->SelectItem(index, true);
            }
            catch (Exception ex)
            {
                pluginLog.Error(ex, $"Exception in SelectDutyDropDownIndex when selecting index {index}.");
            }
        }

        private unsafe void OpenDropDownList(AtkComponentDropDownList* dropdown)
        {
            if (dropdown == null || dropdown->Checkbox == null) return;
            if (dropdown->IsOpen) return;

            // Send only the click event. Calling SetChecked(true) first as well double-toggles
            // the checkbox, which pops the list open just long enough to build it and then
            // immediately closes it again.
            SendCheckBoxClick(dropdown, dropdown->Checkbox);
        }

        private unsafe void CloseDropDownList(AtkComponentDropDownList* dropdown)
        {
            if (dropdown == null || dropdown->Checkbox == null) return;
            if (!dropdown->IsOpen) return;

            pluginLog.Information("Toggling dropdown closed via checkbox...");
            dropdown->Checkbox->SetChecked(false);
            SendCheckBoxClick(dropdown, dropdown->Checkbox);
        }

        private unsafe void SendCheckBoxClick(AtkComponentDropDownList* dropdown, AtkComponentCheckBox* checkbox)
        {
            var checkBtn = (AtkComponentButton*)checkbox;
            var resNode = checkBtn->AtkComponentBase.OwnerNode != null
                ? &checkBtn->AtkComponentBase.OwnerNode->AtkResNode
                : checkBtn->AtkComponentBase.AtkResNode;
            if (resNode != null)
            {
                var evt = (AtkEvent*)resNode->AtkEventManager.Event;
                if (evt != null)
                {
                    // Create a copy of the native event on the stack to preserve parameters
                    // but force the event type to ButtonClick.
                    AtkEvent simulatedEvent = *evt;
                    simulatedEvent.State.EventType = AtkEventType.ButtonClick;
                    simulatedEvent.State.ReturnFlags = 0;
                    simulatedEvent.State.StateFlags = 0;
                    ((AtkEventListener*)dropdown)->ReceiveEvent(AtkEventType.ButtonClick, (int)evt->Param, &simulatedEvent);
                }
                else
                {
                    AtkEvent simulatedEvent = new AtkEvent
                    {
                        Node = resNode,
                        Target = (AtkEventTarget*)&resNode->AtkEventTarget,
                        Listener = (AtkEventListener*)dropdown,
                        Param = resNode->NodeId
                    };
                    simulatedEvent.State.EventType = AtkEventType.ButtonClick;
                    simulatedEvent.State.ReturnFlags = 0;
                    simulatedEvent.State.StateFlags = 0;

                    ((AtkEventListener*)dropdown)->ReceiveEvent(AtkEventType.ButtonClick, (int)resNode->NodeId, &simulatedEvent);
                }
            }
        }

        public void DismissChecklist()
        {
            ShowChecklist = false;
            ActivePreset = null;
            InitialGameState = null;
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

        private static unsafe void SetFixedString(byte* dest, string value, int maxLength)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            int len = Math.Min(bytes.Length, maxLength - 1);
            for (int i = 0; i < len; i++)
            {
                dest[i] = bytes[i];
            }
            dest[len] = 0;
        }

        private static unsafe string GetFixedString(byte* src, int maxLength)
        {
            int len = 0;
            while (len < maxLength && src[len] != 0)
            {
                len++;
            }
            return Encoding.UTF8.GetString(src, len);
        }

        private static readonly List<uint> SortedCombatJobIds = new()
        {
            1, 2, 3, 4, 5, 6, 7, // GLA, PGL, MRD, LNC, ARC, CNJ, THM
            19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32, 33, 34, 35, 36, 37, 38, 39, 40, 41, 42, 43 // PLD to BST
        };

        public static int GetGameJobBitIndex(uint jobId)
        {
            int idx = SortedCombatJobIds.IndexOf(jobId);
            if (idx != -1)
            {
                return idx + 1; // 1-based index (e.g. GLA index 0 -> bit 1)
            }
            return -1;
        }

        public static ulong ConvertBitIndexToGameMask(ulong bitIndexMask)
        {
            if (bitIndexMask == 0) return 0;
            ulong gameMask = 0;
            foreach (var job in JobData.AllJobsAndClasses)
            {
                if ((bitIndexMask & (1UL << job.BitIndex)) != 0)
                {
                    int gameBit = GetGameJobBitIndex((uint)job.Id);
                    if (gameBit != -1)
                    {
                        gameMask |= (1UL << gameBit);
                    }
                }
            }
            return gameMask;
        }

        public static ulong ConvertGameMaskToBitIndex(ulong gameMask)
        {
            if (gameMask == 0) return 0;
            ulong bitIndexMask = 0;
            foreach (var job in JobData.AllJobsAndClasses)
            {
                int gameBit = GetGameJobBitIndex((uint)job.Id);
                if (gameBit != -1)
                {
                    if ((gameMask & (1UL << gameBit)) != 0)
                    {
                        bitIndexMask |= (1UL << job.BitIndex);
                    }
                }
            }
            return bitIndexMask;
        }

        public static string GetGameSlotSummary(ulong mask)
        {
            if (mask == 0) return "Omit";
            if (mask == AllJobsMask) return "Free";
            if (mask == GetGameRoleMask(RoleType.Tank)) return "Tank";
            if (mask == GetGameRoleMask(RoleType.Healer)) return "Healer";
            if (mask == GetGameRoleMask(RoleType.MeleeDPS)) return "Melee DPS";
            if (mask == GetGameRoleMask(RoleType.PhysRangedDPS)) return "Phys Ranged";
            if (mask == GetGameRoleMask(RoleType.MagicRangedDPS)) return "Magic Ranged";

            var jobsList = new List<string>();
            foreach (var job in JobData.AllJobsAndClasses)
            {
                if ((mask & (1UL << job.BitIndex)) != 0)
                {
                    jobsList.Add(job.Abbreviation);
                }
            }

            if (jobsList.Count > 0)
            {
                return string.Join(",", jobsList);
            }
            return "None";
        }

        public static ulong GetGameRoleMask(RoleType role)
        {
            ulong mask = 0;
            switch (role)
            {
                case RoleType.Tank:
                    mask |= (1UL << 0) | (1UL << 1) | (1UL << 2) | (1UL << 3) | (1UL << 4) | (1UL << 5);
                    break;
                case RoleType.Healer:
                    mask |= (1UL << 10) | (1UL << 11) | (1UL << 12) | (1UL << 13) | (1UL << 14);
                    break;
                case RoleType.MeleeDPS:
                    mask |= (1UL << 20) | (1UL << 21) | (1UL << 22) | (1UL << 23) | (1UL << 24) | (1UL << 25) | (1UL << 26) | (1UL << 27) | (1UL << 28) | (1UL << 29);
                    break;
                case RoleType.PhysRangedDPS:
                    mask |= (1UL << 30) | (1UL << 31) | (1UL << 32) | (1UL << 33);
                    break;
                case RoleType.MagicRangedDPS:
                    mask |= (1UL << 40) | (1UL << 41) | (1UL << 42) | (1UL << 43) | (1UL << 44) | (1UL << 45) | (1UL << 46);
                    break;
            }
            return mask;
        }

        public static readonly ulong AllJobsMask =
            GetGameRoleMask(RoleType.Tank) |
            GetGameRoleMask(RoleType.Healer) |
            GetGameRoleMask(RoleType.MeleeDPS) |
            GetGameRoleMask(RoleType.PhysRangedDPS) |
            GetGameRoleMask(RoleType.MagicRangedDPS);

        public static string GetObjectiveName(int objectiveId)
        {
            return objectiveId switch
            {
                0 => "None",
                1 => "Duty Completion",
                2 => "Practice",
                3 => "Loot",
                _ => $"Unknown ({objectiveId})",
            };
        }

        public static string GetLootRuleName(int lootRules)
        {
            return lootRules switch
            {
                0 => "Normal",
                1 => "Greed Only",
                2 => "Lootmaster",
                _ => $"Unknown ({lootRules})",
            };
        }

        public static string GetCompletionStatusName(int type)
        {
            return type switch
            {
                0 => "Duty Complete",
                1 => "Duty Complete (Weekly)",
                2 => "Duty Incomplete",
                _ => $"Unknown ({type})",
            };
        }

        public static string GetGroupTypeName(int groupType)
        {
            return groupType switch
            {
                0 => "Normal",
                1 => "Alliance",
                2 => "Custom Match",
                _ => $"Unknown ({groupType})",
            };
        }

        public static string GetRoleName(RoleType role)
        {
            return role switch
            {
                RoleType.Free => "Free",
                RoleType.Tank => "Tank",
                RoleType.Healer => "Healer",
                RoleType.MeleeDPS => "Melee DPS",
                RoleType.PhysRangedDPS => "Physical Ranged DPS",
                RoleType.MagicRangedDPS => "Magical Ranged DPS",
                RoleType.Omit => "Omit",
                _ => "Unknown",
            };
        }

        public bool IsRecruiting()
        {
            return this.condition[ConditionFlag.UsingPartyFinder];
        }

        public unsafe void UpdateAutoRefresher(double deltaMins)
        {
            if (!config.AutoRefresherEnabled)
            {
                previousCommentString = null;
                refreshCount = 0;
                minutesElapsed = 0;
                return;
            }

            if (IsRecruiting())
            {
                if (minutesElapsed >= 30.0)
                {
                    ExecuteRefreshTask();
                    minutesElapsed = 0;
                }
                else
                {
                    minutesElapsed += deltaMins;
                }
            }
            else
            {
                previousCommentString = null;
                refreshCount = 0;
                minutesElapsed = 0;
            }
        }

        public void ExecuteRefreshTask()
        {
            if (isRefreshExecuting) return;
            isRefreshExecuting = true;

            System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    pluginLog.Information("[AutoRefresher] Starting recruitment auto-refresh...");

                    nint agentPtr = IntPtr.Zero;
                    unsafe
                    {
                        agentPtr = (nint)AgentLookingForGroup.Instance();
                    }
                    if (agentPtr == IntPtr.Zero)
                    {
                        pluginLog.Warning("[AutoRefresher] AgentLookingForGroup instance is null.");
                        return;
                    }

                    // Reapply comment if it got cleared out randomly
                    string comment = "";
                    unsafe
                    {
                        var agent = (AgentLookingForGroup*)agentPtr;
                        comment = agent->StoredRecruitmentInfo.CommentString;
                    }
                    if (previousCommentString != null && comment != previousCommentString && string.IsNullOrWhiteSpace(comment))
                    {
                        pluginLog.Information($"[AutoRefresher] Comment was cleared out, reapplying: '{previousCommentString}'");
                        unsafe
                        {
                            var agent = (AgentLookingForGroup*)agentPtr;
                            agent->StoredRecruitmentInfo.CommentString = previousCommentString;
                        }
                    }
                    else if (!string.IsNullOrWhiteSpace(comment))
                    {
                        previousCommentString = comment;
                    }

                    // Open Party Finder (recruitment details) using the native function imported
                    // from RecruitmentRefresher - this is the exact call that makes refresh work.
                    if (openPartyFinder == null)
                    {
                        pluginLog.Warning("[AutoRefresher] OpenPartyFinder function unavailable (signature not found).");
                        return;
                    }
                    unsafe
                    {
                        openPartyFinder((void*)agentPtr, playerState.ContentId);
                    }

                    // Wait for LookingForGroupDetail window to open (timeout: 5 seconds, check every 50ms)
                    nint detailAddonPtr = IntPtr.Zero;
                    for (int i = 0; i < 100; i++)
                    {
                        await System.Threading.Tasks.Task.Delay(50);
                        var ptr = (nint)gameGui.GetAddonByName("LookingForGroupDetail");
                        if (ptr != IntPtr.Zero)
                        {
                            bool isVisible = false;
                            unsafe
                            {
                                var addon = (AtkUnitBase*)ptr;
                                isVisible = addon->IsVisible;
                            }
                            if (isVisible)
                            {
                                detailAddonPtr = ptr;
                                break;
                            }
                        }
                    }

                    if (detailAddonPtr == IntPtr.Zero)
                    {
                        pluginLog.Warning("[AutoRefresher] Failed to capture LookingForGroupDetail window.");
                        return;
                    }

                    // Click Join/Edit button (ID: 109)
                    nint joinEditBtnPtr = IntPtr.Zero;
                    unsafe
                    {
                        var detailAddon = (AtkUnitBase*)detailAddonPtr;
                        joinEditBtnPtr = (nint)detailAddon->GetComponentButtonById(109);
                    }
                    if (joinEditBtnPtr == IntPtr.Zero)
                    {
                        pluginLog.Warning("[AutoRefresher] Join/Edit button not found.");
                        return;
                    }

                    bool isJoinEditBtnEnabled = false;
                    unsafe
                    {
                        var joinEditBtn = (AtkComponentButton*)joinEditBtnPtr;
                        isJoinEditBtnEnabled = joinEditBtn->IsEnabled;
                    }
                    if (!isJoinEditBtnEnabled)
                    {
                        pluginLog.Warning("[AutoRefresher] Join/Edit button is disabled.");
                        return;
                    }

                    pluginLog.Information("[AutoRefresher] Clicking Join/Edit button...");
                    unsafe
                    {
                        var detailAddon = (AtkUnitBase*)detailAddonPtr;
                        var joinEditBtn = (AtkComponentButton*)joinEditBtnPtr;
                        ClickButton(detailAddon, joinEditBtn);
                    }

                    // Wait for LookingForGroupCondition window to open
                    nint conditionAddonPtr = IntPtr.Zero;
                    for (int i = 0; i < 100; i++)
                    {
                        await System.Threading.Tasks.Task.Delay(50);
                        var ptr = (nint)gameGui.GetAddonByName("LookingForGroupCondition");
                        if (ptr != IntPtr.Zero)
                        {
                            bool isVisible = false;
                            unsafe
                            {
                                var addon = (AtkUnitBase*)ptr;
                                isVisible = addon->IsVisible;
                            }
                            if (isVisible)
                            {
                                conditionAddonPtr = ptr;
                                break;
                            }
                        }
                    }

                    if (conditionAddonPtr == IntPtr.Zero)
                    {
                        pluginLog.Warning("[AutoRefresher] Failed to capture LookingForGroupCondition window.");
                        return;
                    }

                    // Wait for RecruitMembersButton to be enabled (up to 2.5 seconds)
                    bool recruitBtnEnabled = false;
                    for (int i = 0; i < 50; i++)
                    {
                        unsafe
                        {
                            var conditionAddon = (AddonLookingForGroupCondition*)conditionAddonPtr;
                            if (conditionAddon->RecruitMembersButton != null && conditionAddon->RecruitMembersButton->IsEnabled)
                            {
                                recruitBtnEnabled = true;
                                break;
                            }
                        }
                        await System.Threading.Tasks.Task.Delay(50);
                    }

                    if (!recruitBtnEnabled)
                    {
                        pluginLog.Warning("[AutoRefresher] Recruit Members button is disabled.");
                        return;
                    }

                    pluginLog.Information("[AutoRefresher] Clicking Recruit Members button to submit/refresh recruitment...");
                    unsafe
                    {
                        var conditionAddon = (AddonLookingForGroupCondition*)conditionAddonPtr;
                        ClickButton(&conditionAddon->AtkUnitBase, conditionAddon->RecruitMembersButton);
                    }

                    refreshCount++;
                    pluginLog.Information($"[AutoRefresher] Recruitment auto-refreshed successfully (Count: {refreshCount}).");
                }
                catch (Exception ex)
                {
                    pluginLog.Error(ex, "[AutoRefresher] Error during auto-refresh task.");
                }
                finally
                {
                    isRefreshExecuting = false;
                }
            });
        }
    }
}