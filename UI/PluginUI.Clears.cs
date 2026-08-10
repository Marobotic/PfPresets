#if PFP_RATINGS
using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Textures.TextureWraps;

namespace PfPresets
{
    /// <summary>
    /// The clears half of the profile card: what somebody has killed, section by section, with the
    /// parse they earned doing it.
    ///
    /// This panel shows clears and nothing else. A section appears once there is one in it, and
    /// inside that section only the fights they have actually cleared get a pill - the others are
    /// not dimmed, they are absent.
    ///
    /// That is the whole editorial rule, and it follows from what the data can support. A pill for
    /// a fight with no clear on record would be making a claim this cannot back: there is no
    /// Lodestone achievement and no logged kill, but people clear without logging and people hide
    /// their profiles, so "no record" is not "hasn't done it". A row of crosses beside somebody's
    /// name states the opposite in the plainest possible visual language. The "5 / 7" beside the
    /// heading carries the shape of what is missing without asserting anything about it.
    /// </summary>
    public partial class PluginUI
    {
        // Parse colours come from ParseColor in PluginUI.PartyPanel.cs - FFLogs' own bracket
        // colours, already the plugin's one parse scale. A second scale here, however carefully
        // tuned for a small chip, would mean the same parse looking like two different parses
        // depending on which panel you read it in.

        /// <summary>
        /// The fight's own totem, for an Ultimate.
        ///
        /// Named by the roster's slug, so an eighth Ultimate needs a file dropped in and nothing
        /// else - the roster already produces the slug, and a missing image falls back to the glyph
        /// below rather than to a gap.
        ///
        /// Only Ultimates have one. Savage and the trials keep their glyph: there is no per-fight
        /// mark for them that anybody would recognise, and inventing one would be decoration.
        /// </summary>
        private IDalamudTextureWrap? FightTotem(string sectionKey, string slug)
            => sectionKey == "ultimate"
                ? EmbeddedTexture($"PfPresets.Data.Icons.totems.{slug}.png")
                : null;

        /// <summary>
        /// The mark on a cleared pill, by section - and the fallback for an Ultimate whose totem
        /// has not decoded yet or was never added.
        /// </summary>
        private static FontAwesomeIcon SectionIcon(string key) => key switch
        {
            "ultimate" => FontAwesomeIcon.Crown,
            "savage" => FontAwesomeIcon.Book,
            "extreme" => FontAwesomeIcon.Bolt,
            _ => FontAwesomeIcon.Ghost,
        };

        // ── Geometry ──────────────────────────────────────────────

        private const float PillPadX = 7f;
        private const float PillPadY = 4f;
        private const float PillGap = 6f;

        /// <summary>
        /// Every section that has something to say about this character.
        ///
        /// Draws nothing at all - not a heading, not a placeholder - for a character nobody has
        /// fetched. The footer's button is the invitation; a card that showed four empty sections
        /// would be making a claim about somebody on no evidence.
        /// </summary>
        private void DrawClearsSections(ClearsResponse? clears)
        {
            if (clears == null || !clears.Fetched)
                return;

            float width = ImGui.GetContentRegionAvail().X - CardPad;
            if (width <= 0f)
                return;

            bool drewAnything = false;

            foreach (var section in clears.Sections)
            {
                // The rule: a section exists on this card once they have cleared something in it,
                // and carries only those clears.
                if (section.Cleared <= 0)
                    continue;

                if (drewAnything)
                    ImGui.Dummy(new Vector2(0, 10));
                else
                    DrawRuleHair(4f, 10f);

                drewAnything = true;
                DrawClearsSection(section, width);
            }

            // Fetched, and genuinely nothing to show. Said once, quietly, rather than by four empty
            // headings - and worded as what we know rather than as what they haven't done.
            if (!drewAnything)
            {
                DrawRuleHair(4f, 8f);
                using (UiBodyFont.Push())
                {
                    ImGui.TextColored(Faint, clears.Status switch
                    {
                        "hidden" => "They've hidden their logs.",
                        "notfound" => "No character found on either site.",
                        _ => "No high-end clears on record.",
                    });
                }
            }
        }

        private void DrawClearsSection(ClearsSection section, float width)
        {
            var dl = ImGui.GetWindowDrawList();
            Vector2 headingPos = ImGui.GetCursorScreenPos();

            // Heading left, tally right, on one line - the same tracked caps as every other heading
            // in the plugin.
            using (UiCaptionFont.Push())
            {
                float lineH = ImGui.GetTextLineHeight();
                DrawTrackedCaps(dl, headingPos, section.Label, Dim);

                string tally = $"{section.Cleared} / {section.Total}";
                float tallyW = ImGui.CalcTextSize(tally).X;
                dl.AddText(new Vector2(headingPos.X + width - tallyW, headingPos.Y),
                    ImGui.ColorConvertFloat4ToU32(Faint), tally);

                ImGui.Dummy(new Vector2(width, lineH));
            }

            ImGui.Dummy(new Vector2(0, 6));

            DrawPillRow(section, width);
        }

        /// <summary>
        /// The pills, wrapped by hand.
        ///
        /// ImGui has no flow layout, and the widths here are not uniform - "M1S" and "Guardian
        /// Arkveld" are the same kind of thing at three times the size - so each pill is measured
        /// before it is placed and the row is broken when the next one would overhang. Laying them
        /// out on a fixed grid instead would size every column to the widest label on the card,
        /// which on a card containing "Guardian Arkveld" is most of the card.
        /// </summary>
        private void DrawPillRow(ClearsSection section, float width)
        {
            float startX = ImGui.GetCursorPosX();
            float used = 0f;
            bool first = true;

            foreach (var fight in section.Fights)
            {
                // Clears only. A fight with no clear on record is left out entirely rather than
                // drawn dim - see the note at the top of this file.
                if (!fight.Cleared)
                    continue;

                float pillWidth = MeasurePill(section.Key, fight);

                if (!first && used + PillGap + pillWidth > width)
                {
                    // New row.
                    used = 0f;
                    ImGui.SetCursorPosX(startX);
                }
                else if (!first)
                {
                    ImGui.SameLine(0, PillGap);
                    used += PillGap;
                }

                DrawPill(section.Key, fight, pillWidth);
                used += pillWidth;
                first = false;
            }
        }

        /// <summary>Width of a pill, measured the same way it is drawn - one function so the two
        /// cannot disagree and leave a row overhanging the card.</summary>
        private float MeasurePill(string sectionKey, ClearedFight fight)
        {
            float w = PillPadX * 2f;

            using (UiCaptionFont.Push())
            {
                w += MarkWidth(sectionKey, fight, ImGui.GetTextLineHeight()) + 5f;
                w += ImGui.CalcTextSize(fight.Label).X;

                if (fight.HasParse)
                    w += 5f + ImGui.CalcTextSize(ParseLabel(fight.Percentile)).X + 6f;
            }

            return w;
        }

        /// <summary>
        /// How wide the mark at the head of a pill is.
        ///
        /// A totem keeps its aspect ratio at the pill's own height rather than being squared off:
        /// they are drawn objects, and squashing them to a glyph's proportions is both uglier and
        /// smaller than the space allows. Called by the measure and the draw so the two agree.
        /// </summary>
        private float MarkWidth(string sectionKey, ClearedFight fight, float lineHeight)
        {
            var totem = FightTotem(sectionKey, fight.Slug);
            if (totem == null || totem.Height <= 0)
                return IconWidth();

            return MathF.Round(TotemHeight(lineHeight) * totem.Width / totem.Height);
        }

        /// <summary>The totem fills the pill's height less a hair of margin.</summary>
        private static float TotemHeight(float lineHeight) => lineHeight + 4f;

        private float IconWidth()
        {
            using (pluginInterface.UiBuilder.IconFontHandle.Push())
                return ImGui.CalcTextSize(FontAwesomeIcon.Crown.ToIconString()).X;
        }

        /// <summary>"98%", and "100%" without a decimal point that would only ever read as noise.</summary>
        private static string ParseLabel(double percentile) => $"{Math.Floor(percentile):0}%";

        private void DrawPill(string sectionKey, ClearedFight fight, float pillWidth)
        {
            var dl = ImGui.GetWindowDrawList();
            Vector2 pos = ImGui.GetCursorScreenPos();

            float lineH;
            using (UiCaptionFont.Push())
                lineH = ImGui.GetTextLineHeight();

            float height = lineH + PillPadY * 2f;

            ImGui.InvisibleButton($"##pill{sectionKey}{fight.Slug}", new Vector2(pillWidth, height));
            bool hovered = ImGui.IsItemHovered();

            var max = new Vector2(pos.X + pillWidth, pos.Y + height);

            // Only cleared fights reach this - see DrawPillRow - so there is one appearance to
            // draw and no "absent" state to design around.
            dl.AddRectFilled(pos, max, ImGui.ColorConvertFloat4ToU32(hovered ? Raised : Field));
            dl.AddRect(pos, max, ImGui.ColorConvertFloat4ToU32(RuleHair), 0f, 0, 1f);

            float x = pos.X + PillPadX;
            float iconW = MarkWidth(sectionKey, fight, lineH);
            var totem = FightTotem(sectionKey, fight.Slug);

            if (totem != null)
            {
                float th = TotemHeight(lineH);
                var topLeft = new Vector2(x, pos.Y + (height - th) * 0.5f);
                dl.AddImage(totem.Handle, topLeft, new Vector2(topLeft.X + iconW, topLeft.Y + th));
            }
            else
            {
                using (pluginInterface.UiBuilder.IconFontHandle.Push())
                {
                    string glyph = SectionIcon(sectionKey).ToIconString();
                    Vector2 gs = ImGui.CalcTextSize(glyph);

                    // The glyph takes the parse's colour when there is one, so a pill reads as its
                    // bracket at a glance and the number is confirmation rather than the only
                    // signal. A totem is left alone - it is artwork, and tinting it would only
                    // muddy something already recognisable.
                    var tint = fight.HasParse ? ParseColor(fight.Percentile) : Dim;

                    dl.AddText(new Vector2(x + (iconW - gs.X) * 0.5f, pos.Y + (height - gs.Y) * 0.5f),
                        ImGui.ColorConvertFloat4ToU32(tint), glyph);
                }
            }

            x += iconW + 5f;

            using (UiCaptionFont.Push())
            {
                dl.AddText(new Vector2(x, pos.Y + PillPadY),
                    ImGui.ColorConvertFloat4ToU32(Ink), fight.Label);

                if (fight.HasParse)
                {
                    x += ImGui.CalcTextSize(fight.Label).X + 5f;

                    string parse = ParseLabel(fight.Percentile);
                    Vector2 ps = ImGui.CalcTextSize(parse);

                    // The chip sits on its own darker ground so the parse colour is legible at any
                    // band - the greys and greens of a low parse disappear against the pill fill.
                    var chipMin = new Vector2(x, pos.Y + 2f);
                    var chipMax = new Vector2(x + ps.X + 6f, max.Y - 2f);
                    dl.AddRectFilled(chipMin, chipMax, ImGui.ColorConvertFloat4ToU32(Ground));
                    dl.AddText(new Vector2(x + 3f, pos.Y + PillPadY),
                        ImGui.ColorConvertFloat4ToU32(ParseColor(fight.Percentile)), parse);
                }
            }

            if (hovered)
                PaddedTooltip(PillTooltip(fight));
        }

        /// <summary>
        /// What a pill knows, in full.
        ///
        /// Every line is hedged the way the data is: a clear with no kills is somebody who never
        /// uploaded a log, which is normal and is said so, and a clear with no parse is not a bad
        /// parse. There is no wording for an uncleared fight because there is no pill for one.
        /// </summary>
        private static string PillTooltip(ClearedFight fight)
        {
            string what = string.IsNullOrWhiteSpace(fight.Duty) ? fight.Label : fight.Duty;

            var lines = new List<string> { what };

            if (fight.ClearedAt is { } when)
                lines.Add($"Cleared {Ago(when)}");

            if (fight.Kills > 0)
                lines.Add(fight.Kills == 1 ? "1 kill logged" : $"{fight.Kills} kills logged");
            else
                lines.Add("Cleared, but never logged - so no parse.");

            if (fight.HasParse)
                lines.Add($"Best parse {fight.Percentile:0.#}% on the current listing");

            return string.Join("\n", lines);
        }

        /// <summary>
        /// The footer: when the clears were last read, and the button that reads them again.
        ///
        /// The line on the left is the honest half of the refresh button. Somebody looking at a
        /// stranger's card has no way of knowing whether they are seeing tonight's clear or one
        /// from a fortnight ago, and the button cannot explain itself - so the age is printed
        /// beside it, always.
        /// </summary>
        private void DrawClearsFooter(CharacterIdentity who, ClearsResponse? clears)
        {
            float width = ImGui.GetContentRegionAvail().X - CardPad;
            if (width <= 0f)
                return;

            DrawRuleHair(12f, 8f);

            const float buttonSize = 26f;
            Vector2 rowStart = ImGui.GetCursorScreenPos();

            bool pending = Ratings?.ClearsPending(who) ?? false;
            TimeSpan cooling = Ratings?.ClearsRefreshWait(who) ?? TimeSpan.Zero;

            string note = Ratings?.ClearsNote ?? string.Empty;

            string status = note.Length > 0
                ? note
                : pending
                    ? "Looking them up..."
                    : clears == null || !clears.Fetched
                        ? "Clears not fetched yet"
                        : $"Updated {ShortAge(clears.Age)} ago";

            using (UiCaptionFont.Push())
            {
                var dl = ImGui.GetWindowDrawList();
                float lineH = ImGui.GetTextLineHeight();
                dl.AddText(new Vector2(rowStart.X, rowStart.Y + (buttonSize - lineH) * 0.5f),
                    ImGui.ColorConvertFloat4ToU32(note.Length > 0 ? AccentYellow : Faint),
                    Fit(status, width - buttonSize - 12f));
            }

            // The button sits at the right edge of the card, on the same line.
            ImGui.SetCursorScreenPos(new Vector2(rowStart.X + width - buttonSize, rowStart.Y));

            bool blocked = pending || cooling > TimeSpan.Zero;

            if (DrawIconButton($"##clears{who.Key}", buttonSize, FontAwesomeIcon.Redo, blocked)
                && !blocked)
            {
                Ratings?.RequestClears(who, Worlds?.GetFfLogsRegion(who.World));
            }

            if (ImGui.IsItemHovered())
            {
                PaddedTooltip(pending
                    ? "They're on the queue. This can take a minute."
                    : cooling > TimeSpan.Zero
                        ? $"Checked recently - can be read again in {ShortWait(cooling)}.\n\n"
                          + "Clears are shared: once anyone fetches them,\neveryone sees the same answer."
                        : "Fetch their clears from Tomestone and FFLogs.\n\n"
                          + "Sends their name to both sites. The answer is stored\nfor everyone, and can be refreshed once an hour.");
            }
        }

        /// <summary>
        /// A small square button with a glyph in it, drawn by hand.
        ///
        /// Not DrawPrimaryButton at a small size: that one is a full-height accent slab, and an
        /// accent slab in the corner of a card would be the loudest thing on it. This is the card's
        /// own furniture - a field-coloured square that takes the accent only on hover.
        /// </summary>
        private bool DrawIconButton(string id, float size, FontAwesomeIcon icon, bool disabled)
        {
            var dl = ImGui.GetWindowDrawList();
            Vector2 pos = ImGui.GetCursorScreenPos();

            ImGui.InvisibleButton(id, new Vector2(size, size));
            bool hovered = ImGui.IsItemHovered() && !disabled;
            bool clicked = ImGui.IsItemClicked() && !disabled;

            var max = new Vector2(pos.X + size, pos.Y + size);

            dl.AddRectFilled(pos, max, ImGui.ColorConvertFloat4ToU32(hovered ? Raised : Field));
            dl.AddRect(pos, max, ImGui.ColorConvertFloat4ToU32(hovered ? Accent : RuleHair), 0f, 0, 1f);

            var tint = disabled ? Faint with { W = 0.45f } : hovered ? Accent : Dim;

            using (pluginInterface.UiBuilder.IconFontHandle.Push())
            {
                string glyph = icon.ToIconString();
                Vector2 gs = ImGui.CalcTextSize(glyph);
                dl.AddText(new Vector2(pos.X + (size - gs.X) * 0.5f, pos.Y + (size - gs.Y) * 0.5f),
                    ImGui.ColorConvertFloat4ToU32(tint), glyph);
            }

            return clicked;
        }

        /// <summary>"3m", "2h", "6d" - an age at a glance, in the width a footer has for it.</summary>
        private static string ShortAge(TimeSpan age)
        {
            if (age.TotalMinutes < 1) return "just now";
            if (age.TotalHours < 1) return $"{(int)age.TotalMinutes}m";
            if (age.TotalDays < 1) return $"{(int)age.TotalHours}h";
            return $"{(int)age.TotalDays}d";
        }
    }
}
#endif
