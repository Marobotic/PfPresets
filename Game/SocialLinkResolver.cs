#if PFP_RATINGS
using System;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Info;

namespace PfPresets
{
    /// <summary>
    /// Works out whether someone is a friend or an FC mate, which is what drives the collusion
    /// discount on a rating's weight.
    ///
    /// This has to run while the party still exists - once a duty ends and everyone scatters, the
    /// friend list is still there but the party member's FC tag is not - so the answer is resolved
    /// during duty sampling and stored on the encounter rather than worked out at submit time.
    ///
    /// The server applies the discount itself from its own ledger; what the client reports here is
    /// an input to that, not the final word. A determined colluder can lie about it, which is why
    /// the repeat-vote decay and the server's ledger exist as well.
    /// </summary>
    internal sealed class SocialLinkResolver
    {
        private readonly IObjectTable objectTable;
        private readonly IPluginLog log;

        public SocialLinkResolver(IObjectTable objectTable, IPluginLog log)
        {
            this.objectTable = objectTable;
            this.log = log;
        }

        /// <summary>
        /// The strongest link between the local player and the given character. Friend outranks
        /// FC because it is the stronger signal of a relationship that could produce a favour vote.
        /// </summary>
        public SocialLink Resolve(string name, uint homeWorldId, string memberFcTag)
        {
            try
            {
                if (IsFriend(name, homeWorldId))
                    return SocialLink.Friend;

                if (IsSameFreeCompany(memberFcTag, homeWorldId))
                    return SocialLink.FreeCompany;
            }
            catch (Exception ex)
            {
                // Failing to detect a link must not block the rating; it just means the vote is
                // weighted as an ordinary peer vote.
                log.Debug($"[Ratings] Social link check failed: {ex.Message}");
            }

            return SocialLink.None;
        }

        private unsafe bool IsFriend(string name, uint homeWorldId)
        {
            if (string.IsNullOrWhiteSpace(name) || homeWorldId == 0)
                return false;

            var friendList = InfoProxyFriendList.Instance();
            if (friendList == null)
                return false;

            return friendList->GetEntryByName(name, (ushort)homeWorldId) != null;
        }

        /// <summary>
        /// Same FC, judged by matching company tags on the same world. Free Companies are
        /// world-locked, so a shared tag across two different worlds is a coincidence rather than
        /// a shared FC - which is why the world has to match too.
        /// </summary>
        private bool IsSameFreeCompany(string memberFcTag, uint memberHomeWorldId)
        {
            if (string.IsNullOrWhiteSpace(memberFcTag))
                return false;

            var localPlayer = objectTable.LocalPlayer;
            if (localPlayer == null)
                return false;

            string localTag = localPlayer.CompanyTag.ToString();
            if (string.IsNullOrWhiteSpace(localTag))
                return false;

            if (localPlayer.HomeWorld.RowId != memberHomeWorldId)
                return false;

            return string.Equals(localTag.Trim(), memberFcTag.Trim(), StringComparison.Ordinal);
        }
    }
}
#endif
