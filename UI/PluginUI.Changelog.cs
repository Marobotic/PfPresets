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
