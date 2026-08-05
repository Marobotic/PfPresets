using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace PfPresets
{
    /// <summary>
    /// What changed, version by version.
    ///
    /// One short line per change, in the plainest words that are still true. Nobody opens a
    /// changelog to read: they skim it once after an update to find out what moved. Paragraphs
    /// explaining why a change was made get skipped along with everything around them, so the
    /// reasoning stays in the code where it belongs and this file gets the outcome only.
    ///
    /// Two rules for what goes in:
    /// - only what a user can see. No refactors, no internals.
    /// - only what a user could have run into. A bug introduced and fixed between releases never
    ///   existed as far as anyone outside this repository is concerned, and listing it invents a
    ///   problem people then go looking for.
    ///
    /// Newest first, and the newest one starts open - it is the only entry most people will read.
    /// </summary>
    public partial class PluginUI
    {
        private sealed record ChangeEntry(string Version, string Date, string[] Changes);

        private static readonly ChangeEntry[] Changelog =
        {
            new("3.3.3", "August 2026", new[]
            {
                "Right-click anyone in your party for a menu: profile, report, kick, blacklist.",
                "Blacklist now works outside duties too. It uses the game's own blacklist.",
                "The button by Recruit Members now says \"Apply a recruitment preset\", and hides while the plugin is open.",
                "Settings: switches for that button and for the Save as Preset button.",
                "Leading a cross-world party, Leave Party now says Disband Party - because that is what it does.",
                "Auto-adjust now asks for one pure healer and one barrier healer, not two of the same.",
                "Recent players keeps everyone you meet, not just the people you rated.",
                "Search covers all of them, with job icons.",
                "Ratings and jobs show the moment you open someone.",
                "Parse percentages come from the current expansion's page, so old clears read correctly.",
                "A clear with no parse shows as a clear, not as a zero.",
            }),

            new("3.3.2", "August 2026", new[]
            {
                "Anonymous usage stats is now Full, Basic or Off. Off sends nothing.",
                "Still counts only - no names, no duties, nothing you type.",
            }),

            new("3.2.3", "July 2026", new[]
            {
                "The party card always shows everyone's name.",
                "Joining someone else's Party Finder shows what they are recruiting for, across worlds too.",
                "Opening a profile re-reads their rating, so other people's votes show up.",
                "Update player progress queues up instead of failing when it is busy.",
                "Someone you looked up is checked again later, instead of never.",
            }),

            new("3.2.2", "July 2026", new[]
            {
                "Progress stays put when the party fills or goes quiet.",
                "PvP duties bring up the rating prompt when they finish.",
            }),

            new("3.2.1", "July 2026", new[]
            {
                "Downvotes were being rejected. Fixed.",
                "Community ratings are on by default.",
            }),

            new("3.2.0", "July 2026", new[]
            {
                "Community ratings. Rate the people you finish duties with, and look up anyone by name and world.",
                "A prompt after a duty offers to rate the group. It asks once.",
                "Player profiles: rating, job, and the duties you have run together.",
                "Party progression on savage, ultimate, extreme, criterion and current raids.",
                "Recent players, with links to FFLogs, Tomestone and the Lodestone.",
                "Report a player, anonymously if you want.",
                "Ratings can be switched off entirely in Settings. Presets are unaffected.",
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
            ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 0f);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(14, 12));

            try
            {
                if (ImGui.Begin("PF Analysis - What's new###PfPresetsChangelog", ref isChangelogVisible,
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
            ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 0f);

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
