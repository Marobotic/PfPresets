using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace PfPresets
{
    /// <summary>
    /// What changed, version by version.
    ///
    /// Written for the person using the plugin, not the person who wrote it. Nothing here mentions
    /// a class, a schema or a refactor: a release note is only worth reading if it says what is
    /// different when you next open the window, and "reworked the encounter store" tells a player
    /// nothing they can act on.
    ///
    /// Newest first, and the newest one starts open - it is the only entry most people will read.
    /// </summary>
    public partial class PluginUI
    {
        private sealed record ChangeEntry(string Version, string Date, string[] Changes);

        private static readonly ChangeEntry[] Changelog =
        {
            new("3.2.3", "July 2026", new[]
            {
                "The party card always shows the full member list. The collapsed strip of job icons and the chevron that switched between them are gone - one layout, always the names.",
                "Joining somebody else's Party Finder now shows what the party is recruiting for. Before, only the leader saw the listing and everyone else got \"in a party\" - across worlds, where the leader isn't loaded on your client, there was no way to read it at all. The plugin now fetches the listing itself, briefly opening its window, and only when something suggests there is one to find.",
                "Opening a player's profile re-reads their rating. Votes cast by other people showed up for them and not for you until the plugin was reloaded; leaving the profile and coming back now refreshes it, at most once every five seconds.",
                "Update player progress goes through a shared queue. The button says Queued until the answer arrives, and everyone using the plugin feeds the same queue - so if somebody else has just asked about a player, you wait on their lookup instead of paying for a second one.",
                "A player who has been looked up once is looked at again later. Their progress used to be fetched once and then never re-read, so anyone progging through an evening stayed frozen at their first pull.",
            }),

            new("3.2.2", "July 2026", new[]
            {
                "Progress percentages are tied to the fight they were read for, and stay put when the party fills or goes idle.",
                "PvP instances - Frontline and Crystalline Conflict - now bring up the rating prompt when they finish.",
            }),

            new("3.2.1", "July 2026", new[]
            {
                "Downvotes were being rejected on the way out. Fixed.",
                "Community ratings are on by default.",
            }),

            new("3.2.0", "July 2026", new[]
            {
                "Community ratings. Rate the people you finish duties with, and look up any character by name and world. A rating is a score, not a percentage: an upvote is +1 and a downvote -1, and votes from friends, Free Company members and repeat voters count for less, so it reflects agreement among strangers.",
                "A prompt appears after a duty offering to rate the group. It asks once per duty, closes itself if you ignore it, and Skip all declines the lot.",
                "Player profiles. Click anyone in Recent players, or choose View profile from the right-click menu, for their rating, their job, and which duties you have run together - that last part is read from your own client and never leaves it.",
                "Party progression. On savage, ultimate, extreme, criterion and current raids, Update player progress shows how far each member has got: their best pull, or a clear shown in their parse colour.",
                "Recent players remembers who you rated and when, with links out to FFLogs, Tomestone and the Lodestone.",
                "Report a player to the plugin author, with the option to send it anonymously.",
                "Ratings can be switched off entirely in Settings, which removes the tab and every score and stops the plugin contacting the rating server. Presets are unaffected.",
                "This changelog.",
            }),

            new("3.0.0", "July 2026", new[]
            {
                "Rebuilt window: presets and settings as tabs rather than separate windows.",
                "Presets can be shared as codes and imported from them.",
                "Auto-refresh keeps a Party Finder listing alive without retyping it.",
                "Locked job slots adjust themselves while recruiting.",
            }),
        };

        private int changelogOpen;
        private bool isChangelogVisible;

        /// <summary>
        /// Its own window, opened from Settings.
        ///
        /// Not a tab: a changelog is read once after an update and then not again for weeks, and a
        /// permanent seat in the strip alongside the things people use every session overstates
        /// how often it matters.
        /// </summary>
        private void DrawChangelogWindow()
        {
            if (!isChangelogVisible)
                return;

            ImGui.SetNextWindowSize(new Vector2(460, 420), ImGuiCond.FirstUseEver);
            ImGui.SetNextWindowSizeConstraints(new Vector2(380, 240), new Vector2(760, 900));

            ImGui.PushStyleColor(ImGuiCol.WindowBg, BgOuter);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 8f);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(14, 12));

            try
            {
                if (ImGui.Begin("PF Presets - What's new###PfPresetsChangelog", ref isChangelogVisible,
                        ImGuiWindowFlags.NoCollapse))
                {
                    for (int i = 0; i < Changelog.Length; i++)
                        DrawChangelogVersion(Changelog[i], i);
                }
            }
            finally
            {
                ImGui.End();
                ImGui.PopStyleVar(2);
                ImGui.PopStyleColor();
            }
        }

        private void DrawChangelogVersion(ChangeEntry entry, int index)
        {
            bool open = changelogOpen == index;

            // The whole header is the control, not a chevron beside it. A row that looks like a
            // heading but only responds on one glyph is a row people click twice.
            float width = ImGui.GetContentRegionAvail().X - 8f;
            Vector2 at = ImGui.GetCursorScreenPos();
            const float headerH = 30f;

            ImGui.PushStyleColor(ImGuiCol.Button, open ? BgCardExpanded : BgCard);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, BorderHover);
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, BorderHover);
            ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 6f);

            if (ImGui.Button($"##ver{entry.Version}", new Vector2(width, headerH)))
                changelogOpen = open ? -1 : index;

            ImGui.PopStyleVar();
            ImGui.PopStyleColor(3);

            var dl = ImGui.GetWindowDrawList();

            DrawGlyphCentered(
                open ? Dalamud.Interface.FontAwesomeIcon.ChevronDown
                     : Dalamud.Interface.FontAwesomeIcon.ChevronRight,
                new Vector2(at.X + 6f, at.Y),
                new Vector2(at.X + 6f + 22f, at.Y + headerH),
                TextMuted);

            float textY = at.Y + ((headerH - ImGui.GetTextLineHeight()) * 0.5f);
            dl.AddText(new Vector2(at.X + 32f, textY),
                ImGui.ColorConvertFloat4ToU32(open ? TextPrimary : TextSecondary), entry.Version);

            float dateW = ImGui.CalcTextSize(entry.Date).X;
            dl.AddText(new Vector2(at.X + width - dateW - 12f, textY),
                ImGui.ColorConvertFloat4ToU32(TextMuted), entry.Date);

            if (!open)
            {
                ImGui.Dummy(new Vector2(0, 4));
                return;
            }

            ImGui.Dummy(new Vector2(0, 6));
            ImGui.Indent(14);
            ImGui.PushTextWrapPos(ImGui.GetContentRegionMax().X - 10);

            foreach (string change in entry.Changes)
            {
                ImGui.TextColored(AccentBlue, "•");
                ImGui.SameLine(0, 8);
                ImGui.TextColored(TextSecondary, change);
                ImGui.Dummy(new Vector2(0, 3));
            }

            ImGui.PopTextWrapPos();
            ImGui.Unindent(14);
            ImGui.Dummy(new Vector2(0, 10));
        }
    }
}
