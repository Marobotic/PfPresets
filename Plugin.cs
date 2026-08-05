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
        public string Name => "PF Analysis";

        private readonly IDalamudPluginInterface pluginInterface;
        private readonly ICommandManager commandManager;
        private readonly IChatGui chatGui;
        private readonly IObjectTable objectTable;
        private readonly IFramework framework;
#if PFP_RATINGS
        private readonly IClientState clientState;
#endif

        private readonly Configuration config;
        private readonly DutyDataHelper dutyDataHelper;
        private readonly PfAutomation pfAutomation;
        private readonly PluginUI ui;
        private readonly AnalyticsClient analytics;

#if PFP_RATINGS
        private readonly WorldHelper worldHelper;
        private readonly EncounterStore encounterStore;
        private VoteQueue voteQueue = null!;
        private readonly RatingHistory ratingHistory;
        private readonly PlayerHistory playerHistory;
        private readonly PfApiClient ratingApi;
        private readonly RatingService ratingService;
        private readonly DutyTracker dutyTracker;
#endif

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
            ITargetManager targetManager,
            ICondition condition,
            ISigScanner sigScanner,
            IDutyState dutyState)
        {
            this.pluginInterface = pluginInterface;
            this.commandManager = commandManager;
            this.chatGui = chatGui;
            this.objectTable = objectTable;
            this.framework = framework;
#if PFP_RATINGS
            this.clientState = clientState;
#endif

            // Load configuration
            this.config = this.pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
            this.config.Initialize(this.pluginInterface);

            // Initialize helpers
            this.dutyDataHelper = new DutyDataHelper(dataManager, pluginLog);

            // Upgrade an older config now that duty data is available (v0 presets need it to
            // back-fill their duty row ids).
            this.config.Migrate(this.dutyDataHelper, pluginLog);

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
                targetManager,
                condition,
                sigScanner);

#if PFP_RATINGS
            // Community ratings. Every piece is constructed regardless of whether the feature is
            // switched on, because the services are inert until config.RatingsEnabled is true: the
            // tracker records nothing, and the lookup pump has nothing queued to send.
            this.worldHelper = new WorldHelper(dataManager, pluginLog);
            this.encounterStore = new EncounterStore(this.pluginInterface, pluginLog);
            this.ratingHistory = new RatingHistory(this.pluginInterface, pluginLog);
            this.playerHistory = new PlayerHistory(this.pluginInterface, pluginLog);

            // Carries an upgrading install's known names into the permanent list, which would
            // otherwise start empty and read as having forgotten everybody. No-op once it has run.
            this.playerHistory.SeedFrom(
                this.encounterStore.RecentContacts(), this.ratingHistory.Recent());

            this.ratingApi = new PfApiClient(
                this.config,
                pluginLog,
                pluginInterface.Manifest.AssemblyVersion?.ToString() ?? "unknown",
                () => GetLocalIdentity(clientState, playerState));

            this.voteQueue = new VoteQueue(this.pluginInterface, pluginLog);
            this.ratingService = new RatingService(this.ratingApi, this.config, pluginLog, this.encounterStore, this.ratingHistory, this.voteQueue, this.playerHistory);

            this.dutyTracker = new DutyTracker(
                dutyState,
                framework,
                clientState,
                pluginLog,
                this.config,
                this.pfAutomation,
                this.worldHelper,
                this.dutyDataHelper,
                this.encounterStore,
                new SocialLinkResolver(objectTable, pluginLog));

            // A character switch must not carry the previous character's session over.
            clientState.Login += this.ratingApi.OnCharacterChanged;
            clientState.Logout += OnLogoutResetSession;
#endif

            // Initialize UI
            this.ui = new PluginUI(this.pluginInterface, this.config, this.dutyDataHelper, this.pfAutomation, textureProvider);

#if PFP_RATINGS
            this.ui.Ratings = this.ratingService;
            this.ui.Worlds = this.worldHelper;
            this.ui.Encounters = this.encounterStore;
            this.ui.History = this.ratingHistory;
            this.ui.Players = this.playerHistory;
            this.ui.LocalIdentity = () => GetLocalIdentity(clientState, playerState);
            this.ui.Party = new PartyCommands(pluginLog, this.chatGui);
            this.ui.DiagnosticSink = line => this.chatGui.Print(line);
            this.dutyTracker.EncounterCompleted += this.ui.OnEncounterCompleted;

            // The permanent who-have-I-met list is fed from the same event as everything else, so
            // one judgement about whether a duty was worth recording serves every store.
            this.dutyTracker.EncounterCompleted += this.playerHistory.RecordEncounter;
#endif

            // Anonymous usage counts. Constructed last and entirely fire-and-forget: it never
            // blocks load, and the plugin behaves identically when it's off or the server is down.
            this.analytics = new AnalyticsClient(
                this.config,
                pluginLog,
                pluginInterface.Manifest.AssemblyVersion?.ToString() ?? "unknown");
            this.ui.Analytics = this.analytics;

            // Register commands
            var mainCommandInfo = new CommandInfo(OnCommand)
            {
                HelpMessage = "Opens the PF Analysis window. \"/pfa apply <name>\" posts a preset, "
                            + "\"/pfa refresh\" re-posts your listing, \"/pfa list\" shows your presets. "
                            + "/pfp still works.",
                ShowInHelp = true,
            };
            // /pfa and /pfanalysis are the names the plugin goes by now; /pfp and /pfpresets stay
            // registered as aliases. Dropping them would break every macro and every forum post
            // that ever mentioned this plugin, to gain nothing.
            this.commandManager.AddHandler("/pfa", mainCommandInfo);
            this.commandManager.AddHandler("/pfanalysis", mainCommandInfo);
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
            string trimmed = args.Trim();

            // Split the leading verb from its argument so "apply Savage Prog" keeps the spaces in
            // the preset name.
            int space = trimmed.IndexOf(' ');
            string verb = space < 0 ? trimmed : trimmed.Substring(0, space);
            string rest = space < 0 ? string.Empty : trimmed.Substring(space + 1).Trim();

            // "/pfp refresh" triggers the auto-refresh sequence immediately (the same
            // Edit -> Apply flow the timer runs).
            if (verb.Equals("refresh", StringComparison.OrdinalIgnoreCase))
            {
                if (this.pfAutomation.IsRecruiting())
                {
                    this.chatGui.Print("[PF Analysis] Refreshing Party Finder listing...");
                    this.pfAutomation.ExecuteRefreshTask();
                }
                else
                {
                    this.chatGui.Print("[PF Analysis] You are not currently recruiting on Party Finder.");
                }
                return;
            }

            // "/pfp apply <name>" posts a preset without opening the window, so presets can live
            // on a hotbar macro.
            if (verb.Equals("apply", StringComparison.OrdinalIgnoreCase))
            {
                OnApplyCommand(rest);
                return;
            }

            // "/pfp list" - so you can find the exact name to pass to apply.
            if (verb.Equals("list", StringComparison.OrdinalIgnoreCase))
            {
                if (this.config.Presets.Count == 0)
                {
                    this.chatGui.Print("[PF Analysis] You have no presets yet.");
                    return;
                }
                this.chatGui.Print($"[PF Analysis] {this.config.Presets.Count} preset(s):");
                foreach (var preset in this.config.Presets)
                    this.chatGui.Print($"  {preset.Name}  ({preset.DutyName})");
                return;
            }

            if (trimmed.Length > 0)
            {
                this.chatGui.Print($"[PF Analysis] Unknown command \"{trimmed}\". Try: /pfp apply <name>, /pfp refresh, /pfp list");
                return;
            }

            this.ui.ToggleMainWindow();
        }

        /// <summary>Handles "/pfp apply &lt;name&gt;": resolves the name and runs the same apply flow
        /// the card's Apply button uses, reporting anything that stops it to chat.</summary>
        private void OnApplyCommand(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                this.chatGui.Print("[PF Analysis] Usage: /pfp apply <preset name>");
                return;
            }

            var preset = this.config.FindPresetByName(name, out bool ambiguous);
            if (preset == null)
            {
                this.chatGui.Print(ambiguous
                    ? $"[PF Analysis] \"{name}\" matches more than one preset. Use the full name."
                    : $"[PF Analysis] No preset named \"{name}\". Use /pfp list to see them.");
                return;
            }

            // ApplyPreset reports its own precondition failures (not the leader, already recruiting…).
            this.pfAutomation.ApplyPreset(preset);
        }

        private unsafe void OnDebugCommand(string command, string args)
        {
            // "/pfpdebug chrome" asks the main window to report where its navigation actually
            // landed on the next frame it draws. The tabs have gone missing twice without the code
            // explaining why, and this is the only way to see the numbers from outside the game.
            if (args.Trim().Equals("chrome", StringComparison.OrdinalIgnoreCase))
            {
                this.ui.ChromeDiagnosticRequested = true;
                chatGui.Print("[PF Analysis Debug] Chrome report armed - open or focus the main window.");
                return;
            }

            var agent = FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentLookingForGroup.Instance();
            if (agent == null)
            {
                chatGui.Print("[PF Analysis Debug] AgentLookingForGroup not available.");
                return;
            }

            chatGui.Print("[PF Analysis Debug] Agent fields:");
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
                chatGui.Print("[PF Analysis Debug] Local Player:");
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
            this.pfAutomation.UpdateLockedSlotAdjuster();
            this.pfAutomation.UpdateListingWatch();
        }

#if PFP_RATINGS
        /// <summary>The character the rating system is currently acting as, or null when nobody is
        /// logged in. Read on demand rather than cached so a character switch is picked up without
        /// any explicit invalidation.</summary>
        private static CharacterIdentity? GetLocalIdentity(IClientState clientState, IPlayerState playerState)
        {
            if (!clientState.IsLoggedIn || !playerState.IsLoaded)
                return null;

            string name = playerState.CharacterName;
            string world = playerState.HomeWorld.ValueNullable?.Name.ToString() ?? string.Empty;

            var identity = new CharacterIdentity(name, world);
            return identity.IsValid ? identity : null;
        }

        private void OnLogoutResetSession(int type, int code) => this.ratingApi.OnCharacterChanged();
#endif

        public void Dispose()
        {
            this.framework.Update -= OnFrameworkUpdate;

            this.pluginInterface.UiBuilder.Draw -= this.ui.Draw;
            this.pluginInterface.UiBuilder.OpenMainUi -= this.ui.ToggleMainWindow;
            this.pluginInterface.UiBuilder.OpenConfigUi -= this.ui.ToggleSettingsWindow;

            this.commandManager.RemoveHandler("/pfp");
            this.commandManager.RemoveHandler("/pfpresets");
            this.commandManager.RemoveHandler("/pfa");
            this.commandManager.RemoveHandler("/pfanalysis");
            this.commandManager.RemoveHandler("/pfpdebug");

#if PFP_RATINGS
            this.clientState.Login -= this.ratingApi.OnCharacterChanged;
            this.clientState.Logout -= OnLogoutResetSession;
            this.dutyTracker.EncounterCompleted -= this.ui.OnEncounterCompleted;
            this.dutyTracker.EncounterCompleted -= this.playerHistory.RecordEncounter;
            this.dutyTracker.Dispose();
            this.ratingService.Dispose();
            this.ratingApi.Dispose();
            this.encounterStore.Flush();
            this.ratingHistory.Flush();
            this.playerHistory.Flush();
#endif

            // Unsubscribes any in-flight automation from Framework.Update and stops the
            // background refresh task from touching the game after unload.
            this.pfAutomation.Dispose();
            this.analytics.Dispose();
        }
    }
}
