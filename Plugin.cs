using System;
using Dalamud.Game.Command;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace PfPresets
{
    /// <summary>
    /// Plugin entry point: wires up services, registers chat commands, and hooks the
    /// Dalamud UI/framework events.
    /// </summary>
    public class Plugin : IDalamudPlugin
    {
        public string Name => "PF Presets";

        private readonly IDalamudPluginInterface pluginInterface;
        private readonly ICommandManager commandManager;
        private readonly IChatGui chatGui;
        private readonly IObjectTable objectTable;
        private readonly IFramework framework;

        private readonly Configuration config;
        private readonly DutyDataHelper dutyDataHelper;
        private readonly PfAutomation pfAutomation;
        private readonly PluginUI ui;

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
            IObjectTable objectTable,
            ICondition condition,
            ISigScanner sigScanner)
        {
            this.pluginInterface = pluginInterface;
            this.commandManager = commandManager;
            this.chatGui = chatGui;
            this.objectTable = objectTable;
            this.framework = framework;

            // Load configuration
            this.config = this.pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
            this.config.Initialize(this.pluginInterface);

            // Initialize helpers
            this.dutyDataHelper = new DutyDataHelper(dataManager, pluginLog);
            this.pfAutomation = new PfAutomation(
                gameGui,
                pluginLog,
                this.chatGui,
                this.config,
                clientState,
                playerState,
                framework,
                this.dutyDataHelper,
                this.objectTable,
                condition,
                sigScanner);

            // Initialize UI
            this.ui = new PluginUI(this.pluginInterface, this.config, this.dutyDataHelper, this.pfAutomation, textureProvider);

            // Register commands
            var mainCommandInfo = new CommandInfo(OnCommand)
            {
                HelpMessage = "Opens the PF Presets window. Use \"/pfp refresh\" to re-post your listing now.",
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

            // Hook Framework Update for the Auto Refresher
            this.framework.Update += OnFrameworkUpdate;
        }

        private void OnCommand(string command, string args)
        {
            // "/pfp refresh" triggers the auto-refresh sequence immediately (the same
            // Edit -> Apply flow the timer runs).
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

            chatGui.Print("[PF Presets Debug] Agent fields:");
            chatGui.Print($"  OwnListingId: {agent->OwnListingId}");
            chatGui.Print($"  ListingContentId: {agent->ListingContentId}");
            chatGui.Print($"  ListingAccountId: {agent->ListingAccountId}");
            chatGui.Print($"  NumberOfListingsDisplayed: {agent->NumberOfListingsDisplayed}");
            chatGui.Print($"  CategoryTab: {agent->CategoryTab}");
            chatGui.Print($"  GroupTypeTab: {agent->GroupTypeTab}");
            chatGui.Print($"  SelectedCategory: {agent->StoredRecruitmentInfo.SelectedCategory}");
            chatGui.Print($"  SelectedDutyId: {agent->StoredRecruitmentInfo.SelectedDutyId} ({dutyDataHelper.GetDutyName(agent->StoredRecruitmentInfo.SelectedDutyId)})");

            var localPlayer = this.objectTable.LocalPlayer;
            if (localPlayer != null)
            {
                chatGui.Print("[PF Presets Debug] Local Player:");
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

            // Unsubscribes any in-flight automation from Framework.Update and stops the
            // background refresh task from touching the game after unload.
            this.pfAutomation.Dispose();
        }
    }
}
