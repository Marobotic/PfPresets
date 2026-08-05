#if PFP_RATINGS
using System;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

namespace PfPresets
{
    /// <summary>
    /// Party management actions the leader can already perform through the game's own party list,
    /// exposed as buttons next to the member they apply to.
    ///
    /// These call the game's native agent functions - the same ones the party list's right-click
    /// menu calls - rather than sending chat commands. Synthesising "/kick name" into the chat box
    /// would be both fragile (names with spaces, localisation) and a far worse thing to be doing
    /// automatically.
    ///
    /// Nothing here is destructive without a confirmation step in the UI, and every call is
    /// guarded: a null agent or a failed call is silently ignored rather than throwing into the
    /// render thread.
    /// </summary>
    internal sealed class PartyCommands
    {
        private readonly IPluginLog log;
        private readonly IChatGui chatGui;

        public PartyCommands(IPluginLog log, IChatGui chatGui)
        {
            this.log = log;
            this.chatGui = chatGui;
        }

        /// <summary>
        /// Removes a member from the party. The caller must already have confirmed this with the
        /// user and verified they are the leader - this method does neither.
        /// </summary>
        public unsafe bool Kick(string name, ulong contentId)
        {
            if (string.IsNullOrWhiteSpace(name))
                return false;

            try
            {
                var agent = AgentPartyMember.Instance();
                if (agent == null)
                {
                    chatGui.Print("[PF Analysis] The party list isn't available right now.");
                    return false;
                }

                // parentAddonId 0: the call isn't being made on behalf of an open addon window.
                agent->Kick(name, 0, contentId);
                log.Information($"[Party] Kick requested for {name}.");
                return true;
            }
            catch (Exception ex)
            {
                log.Error(ex, "[Party] Kick failed.");
                chatGui.Print("[PF Analysis] Couldn't remove that player. See /xllog for details.");
                return false;
            }
        }
    }
}
#endif
