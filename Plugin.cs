using System;
using Dalamud.Game.Command;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace PfPresets
{
    public class Plugin : IDalamudPlugin
    {
        public string Name => "PF Presets";

        private readonly IDalamudPluginInterface pluginInterface;
        private readonly ICommandManager commandManager;
        private readonly IGameGui gameGui;
        private readonly IDataManager dataManager;
        private readonly IPluginLog pluginLog;
        private readonly IChatGui chatGui;
        private readonly ITextureProvider textureProvider;

        private readonly Configuration config;
        private readonly DutyDataHelper dutyDataHelper;
        private readonly PfAutomation pfAutomation;
        private readonly PluginUI ui;

        private readonly IPartyList partyList;
        private readonly IObjectTable objectTable;
        private readonly ICondition condition;
        private readonly IFramework framework;

        public Plugin(
            IDalamudPluginInterface pluginInterface,
            ICommandManager commandManager,
            IGameGui gameGui,
            IDataManager dataManager,
            IPluginLog pluginLog,
            IChatGui chatGui,
            ITextureProvider textureProvider,
            IClientState clientState,
            IPlayerState playerState,
            IFramework framework,
            IPartyList partyList,
            IObjectTable objectTable,
            ICondition condition,
            ISigScanner sigScanner)
        {
            this.pluginInterface = pluginInterface;
            this.commandManager = commandManager;
            this.gameGui = gameGui;
            this.dataManager = dataManager;
            this.pluginLog = pluginLog;
            this.chatGui = chatGui;
            this.textureProvider = textureProvider;
            this.partyList = partyList;
            this.objectTable = objectTable;
            this.condition = condition;
            this.framework = framework;

            // Load configuration
            this.config = this.pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
            this.config.Initialize(this.pluginInterface);

            // Initialize helpers
            this.dutyDataHelper = new DutyDataHelper(this.dataManager, this.pluginLog);
            this.pfAutomation = new PfAutomation(
                this.gameGui,
                this.pluginLog,
                this.chatGui,
                this.config,
                clientState,
                playerState,
                framework,
                this.dutyDataHelper,
                this.commandManager,
                this.partyList,
                this.objectTable,
                this.condition,
                sigScanner);

            // Initialize UI
            this.ui = new PluginUI(this.pluginInterface, this.config, this.dutyDataHelper, this.pfAutomation, this.textureProvider);

            // Register commands
            var mainCommandInfo = new CommandInfo(OnCommand)
            {
                HelpMessage = "Opens the PF Presets window.",
                ShowInHelp = true,
            };
            this.commandManager.AddHandler("/pfp", mainCommandInfo);
            this.commandManager.AddHandler("/pfpresets", mainCommandInfo);

            this.commandManager.AddHandler("/pfpdebug", new CommandInfo(OnDebugCommand)
            {
                HelpMessage = "Prints agent and player debug info to chat.",
                ShowInHelp = false
            });

            // Hook UI events
            this.pluginInterface.UiBuilder.Draw += this.ui.Draw;
            this.pluginInterface.UiBuilder.OpenMainUi += this.ui.ToggleMainWindow;
            this.pluginInterface.UiBuilder.OpenConfigUi += this.ui.ToggleSettingsWindow;

            // Hook Framework Update for AutoRefresher
            this.framework.Update += OnFrameworkUpdate;
        }

        private void OnCommand(string command, string args)
        {
            // "/pfp refresh" triggers the auto-refresh sequence immediately (for testing the
            // Edit -> Apply flow without waiting out the timer).
            if (args.Trim().Equals("refresh", StringComparison.OrdinalIgnoreCase))
            {
                if (this.pfAutomation.IsRecruiting())
                {
                    this.chatGui.Print("[PF Presets] Refreshing Party Finder listing...");
                    this.pfAutomation.ExecuteRefreshTask();
                }
                else
                {
                    this.chatGui.Print("[PF Presets] You are not currently recruiting on Party Finder.");
                }
                return;
            }

            this.ui.ToggleMainWindow();
        }

        private unsafe void OnDebugCommand(string command, string args)
        {
            var agent = FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentLookingForGroup.Instance();
            if (agent == null)
            {
                chatGui.Print("[PF Presets Debug] AgentLookingForGroup not available.");
                return;
            }

            chatGui.Print("[PF Presets Debug] Dumping Agent fields:");
            chatGui.Print($"  OwnListingId: {agent->OwnListingId}");
            chatGui.Print($"  ListingContentId: {agent->ListingContentId}");
            chatGui.Print($"  ListingAccountId: {agent->ListingAccountId}");
            chatGui.Print($"  NumberOfListingsDisplayed: {agent->NumberOfListingsDisplayed}");
            chatGui.Print($"  CategoryTab: {agent->CategoryTab}");
            chatGui.Print($"  GroupTypeTab: {agent->GroupTypeTab}");
            chatGui.Print($"  SelectedCategory: {agent->StoredRecruitmentInfo.SelectedCategory}");
            chatGui.Print($"  SelectedDutyId: {agent->StoredRecruitmentInfo.SelectedDutyId}");

            chatGui.Print("[PF Presets Debug] Searching ContentFinderCondition sheet:");
            try
            {
                var sheet = dataManager.GetExcelSheet<Lumina.Excel.Sheets.ContentFinderCondition>();
                if (sheet != null)
                {
                    string[] searchTerms = { "Cloud of Darkness", "Futures Rewritten", "Omega Protocol", "Dancing Mad" };
                    foreach (var row in sheet)
                    {
                        string name = row.Name.ToString();
                        foreach (var term in searchTerms)
                        {
                            if (name.Contains(term, StringComparison.OrdinalIgnoreCase))
                            {
                                chatGui.Print($"  [Excel] RowId: {row.RowId} | Name: '{name}' | ContentType: {row.ContentType.RowId}");
                            }
                        }
                    }
                }
                else
                {
                    chatGui.Print("  [Excel] ContentFinderCondition sheet not found!");
                }
            }
            catch (Exception ex)
            {
                chatGui.Print($"  [Excel] Error: {ex.Message}");
            }

            var localPlayer = this.objectTable.LocalPlayer;
            if (localPlayer != null)
            {
                chatGui.Print($"[PF Presets Debug] Local Player:");
                chatGui.Print($"  OnlineStatus ID: {localPlayer.OnlineStatus.RowId}");
                if (localPlayer.OnlineStatus.RowId > 0)
                {
                    chatGui.Print($"  OnlineStatus Name: {localPlayer.OnlineStatus.Value.Name.ToString()}");
                }
                if (localPlayer.ClassJob.RowId > 0)
                {
                    chatGui.Print($"  ClassJob: {localPlayer.ClassJob.Value.Name.ToString()}");
                }
            }
        }

        private void OnFrameworkUpdate(IFramework _)
        {
            this.pfAutomation.UpdateAutoRefresher(this.framework.UpdateDelta.TotalMinutes);
        }

        public void Dispose()
        {
            this.framework.Update -= OnFrameworkUpdate;

            this.pluginInterface.UiBuilder.Draw -= this.ui.Draw;
            this.pluginInterface.UiBuilder.OpenMainUi -= this.ui.ToggleMainWindow;
            this.pluginInterface.UiBuilder.OpenConfigUi -= this.ui.ToggleSettingsWindow;

            this.commandManager.RemoveHandler("/pfp");
            this.commandManager.RemoveHandler("/pfpresets");
            this.commandManager.RemoveHandler("/pfpdebug");
        }
    }
}