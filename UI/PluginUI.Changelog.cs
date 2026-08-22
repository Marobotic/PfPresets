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
            new("3.5.1", "August 2026", new[]
            {
                "Looking at a listing? The panel beside it now shows who is already in that party. One person in it running the plugin is enough for everybody looking to see the rest.",
                "That means you show up in listings you sit in, too - your party is published while it's listed, and only while it's listed. Turn it off under Settings \u2192 PF Radar.",
                "Recruiting on your own now lists you, with your progression and a way to fetch it, instead of showing nothing until somebody joins.",
                "Progression stopped vanishing. Prog points would fall back to \"Fetch\" while the server had the answer all along, usually with a listing open next to your party list.",
                "\"Update progress\" no longer eats a press. If it can't go through right now, it says so.",
                "Omit works after somebody leaves. Omitted slots used to come back and start recruiting for the roles you had struck off.",
                "Omitted slots are also posted where the game puts them - at the end - so the roles you kept keep their order.",
            }),

            new("3.4.5", "August 2026", new[]
            {
                "New clears to see? The Achievements tab now wears a badge telling you how many people have posted since you last looked.",
                "The badge shows even when the window is rolled up, so you won't miss it while you're busy.",
                "The Vote tab gets one too, so you know when there's a new poll waiting for your say.",
                "Filled your party? The fight you recruited for stays on screen now, so you can see how everyone's doing while you get ready.",
                "Want to know where the plugin is heading? There's a \"Read the update\" button in About now.",
            }),

            new("3.4.4", "August 2026", new[]
            {
                "Security and performance improvements on the server side. Nothing to do at your end.",
            }),

            new("3.4.3", "August 2026", new[]
            {
                "New Vote tab. There is a poll running on the future of the plugin, and the option with the most votes will be implemented in the next update. You can share it with a link so other people can vote too.",
                "I have written a post about where the plugin is going and the thinking behind it. Please read it at pfa.marobotic.dev/blog before you vote.",
                "The vote reminder appears once, never again after you have voted, and has a \"Don't ask again\" button that stops it for good.",
            }),

            new("3.4.2", "August 2026", new[]
            {
                "New Achievements tab. Share your Ultimate clears and your savage tier clear on the achievement feed, and heart everybody else's.",
                "You can now opt out: turn off the ratings system in Settings, or visit pfa.marobotic.dev/optout. Opting out hides you completely, and opting back in restores everything.",
            }),

            new("3.4.0", "August 2026", new[]
            {
                "Profile cards now show clears: every Ultimate, the current savage tier, and every Extreme and Unreal of the expansion, with the best parse on each.",
                "Only clears are shown: a section appears once that player has cleared something in it, and lists what they've cleared. The count beside the heading says how many of the section there are.",
                "The button at the bottom of a card fetches those clears from Tomestone and FFLogs. Once anyone fetches a player everybody sees the same answer, and any player can be read again an hour later.",
                "A clear counts whether it was logged or not - an achievement on the Lodestone proves it as well as a kill on FFLogs does - so people who never upload logs no longer read as having cleared nothing.",
                "FFLogs, Tomestone and the Lodestone are now icons in the card's top corner rather than three buttons across it, which is where the clears went.",
                "Reporting a player has moved off the profile card. It's on the right-click menu in the party list, with the other things you do about somebody.",
            }),

            new("3.3.6", "August 2026", new[]
            {
                "A listing you're viewing now shows the leader's community score beside their name, so you see it before joining rather than after. Turn it off under Settings → Party Finder.",
                "Blacklisting from the player menu has been removed. The game only lets a plugin blacklist someone in narrow circumstances, and the result was an option that often couldn't do what it offered - blacklisting is better done from the game's own Contact List or blacklist window, where it always works.",
                "Profile cards lead with the job icon beside the name, at the name's own size, and the line below is left to the world, level and job.",
                "Recent players shows the community score on its own. Your own vote sat beside it as a second arrow in the same green, so a player you had rated appeared to have two; it now reads in the score's tooltip.",
                "Auto-translate phrases in a listing's comment now read as the phrase, in the game's green and red brackets, instead of as blank space or stray characters.",
                "A preset saved from a listing that used auto-translate posts that phrase back exactly as it was, rather than replacing it with ordinary text.",
                "The comment counter now counts bytes, which is what the game's limit actually measures: symbols and auto-translate cost three bytes each, so a comment can be full well before it looks it.",
            }),

            new("3.3.5", "August 2026", new[]
            {
                "Buttons anchored to game windows (PF Analysis, Save as Preset) now position correctly on multi-monitor and windowed setups.",
                "The listing watcher now probes once instead of repeating when no listing exists, keeping chat clean.",
                "Update Progress shows cooldown status (\"Updated · 12m\") with a tooltip when members are within the refresh window.",
            }),

            new("3.3.4", "August 2026", new[]
            {
                "A party member with no Tomestone record now reads \"Not listed yet\", instead of a Fetch button that never turns anything up.",
                "The button beside Recruit Members now reads \"PF Analysis\".",
            }),

            new("3.3.3", "August 2026", new[]
            {
                "Right-click anyone in your party for a menu: profile, report, kick, blacklist.",
                "Blacklist now works outside duties too. It uses the game's own blacklist.",
                "The button by Recruit Members now says \"PF Analysis\", and hides while the plugin is open.",
                "Settings: switches for that button and for the Save as Preset button.",
                "Clear and prog no longer trail a party around after recruiting. They stay while the party is full, queued for that duty, or in it.",
                "Solo in a duty, the window now names it and offers Leave duty instead of showing nothing.",
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
        private void DrawChangelogSheet()
        {
            if (!isChangelogVisible)
            {
                CloseSheet();
                return;
            }

            if (!BeginSheet("Changelog", "What's new", 620f))
                return;

            try
            {
                if (BeginSheetBody(0f))
                {
                    try
                    {
                        for (int i = 0; i < Changelog.Length; i++)
                            DrawChangelogVersion(Changelog[i], i);

                        ImGui.Dummy(new Vector2(0, 8));
                    }
                    finally
                    {
                        EndSheetBody();
                    }
                }
                else
                {
                    EndSheetBody();
                }
            }
            finally
            {
                EndSheet();
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
            ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, Radius.Control);

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
