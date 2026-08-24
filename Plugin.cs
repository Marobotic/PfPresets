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
    public partial class Plugin : IDalamudPlugin
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

        /// <summary>Publishes this character's own presence in a party finder listing, and reads
        /// back who else has published theirs.</summary>
        private readonly PfCrowdsource pfCrowdsource;
#endif

        /// <summary>Reads the listing window's own data. Outside the ratings guard: it asks the
        /// server nothing and stores nothing about anybody, so it is not part of that system.</summary>
        private readonly ListingXray listingXray;

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
            IDutyState dutyState,
            IGameInteropProvider interop)
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
#endif

            this.listingXray = new ListingXray(
                this.pluginInterface, interop, pluginLog,
                () => this.config.ListingDetailsEnabled);
#if PFP_RATINGS
            // Suppressed by PFRadar for the same reason the panel is: it already does this, and two
            // plugins publishing the same party is two rows saying the same thing.
            this.pfCrowdsource = new PfCrowdsource(
                this.ratingApi, this.config, pluginLog, this.pfAutomation, this.worldHelper,
                () => GetLocalIdentity(clientState, playerState),
                () => this.listingXray.SuppressedByPfRadar);

            // The server holds the opt-out, not the config file - that is the whole promise made to
            // somebody who turns ratings off, since a fresh install defaults to on. Asked once per
            // login, after the session has had a moment to establish.
            clientState.Login += this.SyncOptOutOnLogin;
            clientState.Logout += OnLogoutResetSession;
#endif

            // Initialize UI
            this.ui = new PluginUI(this.pluginInterface, this.config, this.dutyDataHelper, this.pfAutomation, textureProvider);

            // AFTER the UI exists, not beside where the service is built. Assigning this next to the
            // constructor above read better and dereferenced a field that is not set until here.
            this.ui.Listings = this.listingXray;
#if PFP_RATINGS
            this.ui.Crowd = this.pfCrowdsource;
#endif

#if PFP_RATINGS
            this.ui.Ratings = this.ratingService;
            this.ui.Worlds = this.worldHelper;
            this.ratingService.RegionOf = this.worldHelper.GetFfLogsRegion;
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

            // And the achievements feed. Every finished duty is offered; the server decides which
            // ones are worth a post and says no to nearly all of them, silently.
            this.dutyTracker.EncounterCompleted += this.ratingService.PostAchievement;

            // Combat is the service's one reason to hold a filed duty back - see
            // TickPendingDuties. Handed in as a predicate rather than a reference to the automation
            // layer, because that is the only thing it needs to know and the rating service has no
            // other business with the game's condition flags.
            this.ratingService.InCombat = () => this.pfAutomation.IsInCombat();
#endif

            // Anonymous usage counts. Constructed last and entirely fire-and-forget: it never
            // blocks load, and the plugin behaves identically when it's off or the server is down.
            this.analytics = new AnalyticsClient(
                this.config,
                pluginLog,
                pluginInterface.Manifest.AssemblyVersion?.ToString() ?? "unknown",
                dataManager,
                this.objectTable,
                playerState,
                this.framework);
            this.ui.Analytics = this.analytics;

            // Erased entirely in an ordinary build - the implementation is not in this repository.
            // See UI/PluginUI.AdminHooks.cs for why the moderator build works this way.
            InitPanel(pluginLog);

            // Register commands
            // /pfa and /pfanalysis are the names the plugin goes by now; /pfp and /pfpresets stay
            // registered as aliases. Dropping them would break every macro and every forum post
            // that ever mentioned this plugin, to gain nothing. Only /pfa carries the help text -
            // repeating the same paragraph four times just made the command list unreadable.
            this.commandManager.AddHandler("/pfa", new CommandInfo(OnCommand)
            {
                HelpMessage = "Open the PF Analysis window. Subcommands: apply <name>, refresh, list.",
                ShowInHelp = true,
            });

            foreach (var alias in new[] { "/pfanalysis", "/pfp", "/pfpresets" })
            {
                this.commandManager.AddHandler(alias, new CommandInfo(OnCommand)
                {
                    HelpMessage = "Alias for /pfa.",
                    ShowInHelp = true,
                });
            }

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

            // "/pfpdebug overlay" reports the coordinates the two buttons drawn over the game's own
            // windows are placed from. Whether the game's client area starts at the desktop origin
            // decides whether those two spaces agree, and that is a property of the machine rather
            // than of the code - so the only way to see it is to ask the running client.
            if (args.Trim().Equals("overlay", StringComparison.OrdinalIgnoreCase))
            {
                this.ui.OverlayDiagnosticRequested = true;
                chatGui.Print("[PF Analysis Debug] Overlay report armed - open the Party Finder.");
                return;
            }

            // "/pfpdebug criteria" reads the recruitment window's own answer back out.
            //
            // THE ONE THING THAT CANNOT BE WORKED OUT FROM THE SHEETS. The duty field is a single
            // ushort read against whatever category the listing carries, and for most categories it
            // is a ContentFinderCondition row - which the sheets give us. For Crystalline Conflict
            // and for a FATE location they are not: those two are a ContentRoulette row and a
            // TerritoryType row in the game's data, and neither number is what the field wants.
            // Guessing has now cost several attempts, and the client already knows the answer.
            //
            // Set the criteria by hand in the game's own window - PvP then Crystalline Conflict, or
            // FATEs then a zone - and run this before pressing Recruit. It prints the two numbers
            // the client put there.
            if (args.Trim().Equals("criteria", StringComparison.OrdinalIgnoreCase))
            {
                var lfg = FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentLookingForGroup.Instance();
                if (lfg == null)
                {
                    chatGui.Print("[PF Analysis Debug] Party Finder agent not available.");
                    return;
                }

                var stored = lfg->StoredRecruitmentInfo;
                chatGui.Print("[PF Analysis Debug] Recruitment criteria as the client has them:");
                chatGui.Print($"  SelectedCategory = {(uint)stored.SelectedCategory} ({stored.SelectedCategory})");
                chatGui.Print($"  SelectedDutyId   = {stored.SelectedDutyId}");
                chatGui.Print($"  Objective        = {(uint)stored.Objective} ({stored.Objective})");
                chatGui.Print($"  SlotsInMainParty = {stored.NumberOfSlotsInMainParty}");
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
            chatGui.Print($"  ListingAccountId: {agent->ListingAccountIdUInt64}");
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

            // Cheap, and throttles its own expensive half. Done here rather than from a draw call
            // so the hook follows the setting even while no window of ours is open.
            this.listingXray.Sync();

#if PFP_RATINGS
            // Framework rather than draw, for the same reason: a listing goes up and comes down
            // whether or not any window of ours is open, and a report that only refreshed while
            // somebody was looking at the plugin would be wrong exactly when it mattered.
            this.pfCrowdsource.Tick();

            // Drains at most one filed duty per frame, and only out of combat. Returns on a count
            // check the rest of the time.
            this.ratingService.TickPendingDuties();
#endif
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

        /// <summary>Moderator tooling, present only on the machines that hold a key.</summary>
        /// <summary>
        /// Reads the opt-out setting back from the server after a login.
        ///
        /// Delayed a few seconds because the session is established lazily and asking before there
        /// is one gets an honest "no character to answer about". Nothing depends on it being
        /// prompt: it corrects a local flag, and the server is the one that decides anything.
        /// </summary>
        private void SyncOptOutOnLogin()
        {
            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                await System.Threading.Tasks.Task.Delay(TimeSpan.FromSeconds(6)).ConfigureAwait(false);
                this.ratingService.SyncOptOutSetting();
            });
        }

        partial void InitPanel(IPluginLog log);
        partial void DisposePanel();

        public void Dispose()
        {
            DisposePanel();
            this.framework.Update -= OnFrameworkUpdate;

            // Before the UI goes, so nothing can be mid-draw against a snapshot while the hook that
            // produced it is being torn down.
            this.listingXray.Dispose();

#if PFP_RATINGS
            // Takes our row down on the way out. Unloading the plugin is a clearer "I am no longer
            // publishing this" than letting the row sit until the server expires it, and somebody
            // looking at that listing stops being told we are in a party we have left.
            this.pfCrowdsource.Withdraw();
#endif

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
            clientState.Login -= this.SyncOptOutOnLogin;
            this.dutyTracker.EncounterCompleted -= this.playerHistory.RecordEncounter;
            this.dutyTracker.EncounterCompleted -= this.ratingService.PostAchievement;
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
