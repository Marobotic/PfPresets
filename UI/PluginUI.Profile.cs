#if PFP_RATINGS
using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.GameFonts;
using Dalamud.Interface.ManagedFontAtlas;

namespace PfPresets
{
    /// <summary>
    /// The profile card in the Ratings tab: a score banner, the numbers behind it, and where you
    /// have run into them.
    ///
    /// Only the rating gets size and colour. Everything else - the split, the voter count, the
    /// unweighted tally - sits muted on one line, because only one of those figures is the score
    /// and sizing them alike would suggest several competing answers to one question.
    ///
    /// The rating is a net tally, never a percentage. An upvote is +1, a downvote -1, each scaled
    /// by how much that voter counts. Three unanimous friends and thirty unanimous strangers both
    /// read 100%, and only one of them means anything - which is exactly what a share cannot say.
    /// </summary>
    public partial class PluginUI
    {
        /// <summary>
        /// Roboto, embedded, at the two sizes the banner needs.
        ///
        /// Not SetWindowFontScale, which stretches the baked 12px bitmap glyphs and made the
        /// number visibly pixelated. Not the game's Jupiter face either - that is FFXIV's own HUD
        /// numeral face and reads as borrowed chrome here. Roboto is what the mockup was set in,
        /// so the score and its label are set in it too.
        /// </summary>
        private IFontHandle? scoreFont;
        private IFontHandle? labelFont;
        private static byte[]? robotoBytes;

        private static byte[] Roboto()
        {
            if (robotoBytes != null)
                return robotoBytes;

            using var stream = typeof(PluginUI).Assembly
                .GetManifestResourceStream("PfPresets.Data.Fonts.Roboto.ttf")
                ?? throw new InvalidOperationException("Roboto.ttf missing from the assembly.");

            using var buffer = new System.IO.MemoryStream();
            stream.CopyTo(buffer);
            return robotoBytes = buffer.ToArray();
        }

        private IFontHandle Font(ref IFontHandle? slot, float px)
        {
            slot ??= pluginInterface.UiBuilder.FontAtlas.NewDelegateFontHandle(tk =>
                tk.OnPreBuild(pre => pre.AddFontFromMemory(
                    Roboto(), new SafeFontConfig { SizePx = px }, "Roboto")));
            return slot;
        }

        private IFontHandle ScoreFont => Font(ref scoreFont, 40f);
        private IFontHandle LabelFont => Font(ref labelFont, 11f);

        private void DisposeProfileFonts()
        {
            scoreFont?.Dispose();
            labelFont?.Dispose();
            scoreFont = null;
            labelFont = null;
        }

        // Card geometry, in one place so the band, the columns and the content can't disagree.
        private const float BannerHeight = 72f;
        private const float CardPad = 14f;
        private const float DotsSize = 24f;

        /// <summary>
        /// Height of the card with only a banner and the vote line under it - no duty history.
        ///
        /// The compact form has to be measured rather than left at zero: BeginChild with a height
        /// of 0 fills every remaining pixel, which is how your own card ended up swallowing the
        /// whole tab and burying the recent players beneath it.
        /// </summary>
        private static float CompactProfileHeight()
            => BannerHeight + CardPad + 7f + 8f + ImGui.GetFrameHeight() + CardPad;

        /// <summary>Who the card was drawn for last, and on which frame - the two facts needed to
        /// tell "still open" from "opened again".</summary>
        private string profileCardKey = string.Empty;
        private int profileCardFrame = -1;

        /// <summary>
        /// Whether this draw is the card being opened rather than staying open.
        ///
        /// A gap in the frame numbers means the card wasn't on screen last frame - the tab was
        /// switched, the window was closed, Back was pressed - so coming back to it is a fresh
        /// look and worth re-reading the score for. Held open, it isn't: nothing about the card
        /// changes between two consecutive frames.
        /// </summary>
        private bool ProfileCardOpened(CharacterIdentity who)
        {
            int frame = ImGui.GetFrameCount();
            bool opened = profileCardKey != who.Key || frame - profileCardFrame > 1;

            profileCardKey = who.Key;
            profileCardFrame = frame;
            return opened;
        }

        private void DrawProfileCard(CharacterIdentity who, bool showBack = true,
            float fixedHeight = 0f)
        {
            // Opening a profile re-reads the score. Votes cast by other people in your own party
            // were otherwise invisible for the ten minutes the cache holds an entry - you saw your
            // own vote land and then nothing, however many other people voted after you. The
            // service caps this at one refetch per player per five seconds.
            if (ProfileCardOpened(who))
                Ratings?.Refresh(who);

            var rating = Ratings?.Get(who);

            ImGui.Dummy(new Vector2(0, 8));

            // No Back button on your own card: it isn't somewhere you navigated to, it is what
            // the tab shows when there is nothing else to do.
            if (showBack)
            {
                ImGui.Indent(8);
                if (DrawSecondaryButton("< Back##ClearSearch", new Vector2(70, 22)))
                {
                    ratingSearchTarget = null;
                    ratingSearchInput = string.Empty;
                }
                ImGui.Unindent(8);
                ImGui.Dummy(new Vector2(0, 6));
            }

            ImGui.PushStyleColor(ImGuiCol.ChildBg, BgCard);
            ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 8.0f);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(0, 0));

            if (ImGui.BeginChild("ProfileCard", new Vector2(0, fixedHeight), true,
                    ImGuiWindowFlags.None))
            {
                try
                {
                    DrawScoreBanner(who, rating);

                    ImGui.SetCursorPos(new Vector2(CardPad, BannerHeight + CardPad));
                    ImGui.BeginGroup();
                    try
                    {
                        DrawProfileNumbers(who, rating);

                        // Not on your own card. You know where you've been, and the list exists
                        // to tell you who a stranger is.
                        if (!IsSelf(who))
                        {
                            ImGui.Dummy(new Vector2(0, 12));
                            DrawSeenRecentlyIn(who);
                        }
                    }
                    finally
                    {
                        ImGui.EndGroup();
                    }
                }
                finally
                {
                    ImGui.EndChild();
                }
            }
            else
            {
                ImGui.EndChild();
            }

            ImGui.PopStyleVar(2);
            ImGui.PopStyleColor();
        }

        /// <summary>
        /// The banner: score, identity, and the links menu.
        ///
        /// Drawn against the child window's own rectangle rather than the cursor, so the band
        /// reaches the card's real edges. Deriving it from the cursor and a padding constant meant
        /// two places had to agree about the padding, and when they didn't the band floated inset
        /// on every side and read as a box rather than a header.
        /// </summary>
        private void DrawScoreBanner(CharacterIdentity who, PlayerRating? rating)
        {
            bool rated = rating != null && !rating.OptedOut && rating.Count > 0;
            int score = rating?.Score ?? 0;

            // A score of zero is an absence, not a verdict: ScoreColor would paint every unrated
            // stranger in the red it reserves for someone genuinely disliked.
            var accent = rated ? NetScoreColor(score) : TextMuted;

            var dl = ImGui.GetWindowDrawList();
            Vector2 win = ImGui.GetWindowPos();
            float width = ImGui.GetWindowSize().X;

            var min = new Vector2(win.X, win.Y);
            var max = new Vector2(win.X + width, win.Y + BannerHeight);

            // A gradient, not a flat fill. The band is a surface the score sits on; a solid block
            // of tint reads as a second element competing with the number.
            uint top = ImGui.ColorConvertFloat4ToU32(new Vector4(accent.X, accent.Y, accent.Z, 0.20f));
            uint bottom = ImGui.ColorConvertFloat4ToU32(new Vector4(accent.X, accent.Y, accent.Z, 0.03f));

            dl.PushClipRect(min, max, true);
            dl.AddRectFilledMultiColor(min, max, top, top, bottom, bottom);
            dl.PopClipRect();

            dl.AddLine(new Vector2(min.X, max.Y), new Vector2(max.X, max.Y),
                ImGui.ColorConvertFloat4ToU32(BorderDefault), 1f);

            // ── score ──
            //
            // The figure sits high in the band with its caption directly beneath, both aligned on
            // the same left edge. It was previously pushed down and crowded against the label, so
            // the two read as one lump rather than a number and its caption.
            // The number plain, with no sign. A leading "+" made it look like a delta - how much
            // it had moved - when it is the rating itself.
            string text = rated ? score.ToString() : "0";

            float numberW, labelW;
            using (LabelFont.Push())
                labelW = ImGui.CalcTextSize("RATING").X;

            using (ScoreFont.Push())
            {
                numberW = ImGui.CalcTextSize(text).X;

                // Centred on the caption rather than sharing its left edge: a single digit over a
                // six-letter word looked indented, which is what makes the pair read as crooked.
                float column = Math.Max(numberW, labelW);
                ImGui.SetCursorPos(new Vector2(CardPad + ((column - numberW) * 0.5f), 8f));
                ImGui.PushStyleColor(ImGuiCol.Text, accent);
                ImGui.TextUnformatted(text);
                ImGui.PopStyleColor();
            }

            using (LabelFont.Push())
            {
                float column = Math.Max(numberW, labelW);
                ImGui.SetCursorPos(new Vector2(CardPad + ((column - labelW) * 0.5f), 53f));
                ImGui.TextColored(TextMuted, "RATING");
            }

            if (ImGui.IsItemHovered())
            {
                PaddedTooltip(rated
                    ? "Every upvote is +1 and every downvote -1.\n\n"
                      + "Votes from friends and Free Company members count for less,\n"
                      + "and voting on the same person again counts for much less - so\n"
                      + "the number reflects agreement among strangers, not a small\n"
                      + "group voting often."
                    : "Nobody has rated them yet.");
            }

            // ── identity ──
            float textX = CardPad + Math.Max(Math.Max(numberW, labelW), 44f) + 20f;

            ImGui.SetCursorPos(new Vector2(textX, 17f));
            ImGui.TextColored(TextPrimary, DisplayName(who.Name));
            ImGui.SameLine(0, 5);
            ImGui.TextColored(TextMuted, $"@{who.World}");

            DrawJobLine(who, textX);

            DrawProfileLinksMenu(who, width);
        }

        /// <summary>
        /// The job, the way Tomestone lays it out: icon, then the level and job name on one line,
        /// vertically centred against the icon rather than hung off its baseline.
        ///
        /// "Level 100 Gunbreaker" reads as a sentence. "Gunbreaker 100" reads as a score, and a
        /// second number on this card is the one thing it must not have.
        /// </summary>
        private void DrawJobLine(CharacterIdentity who, float x)
        {
            var info = Ratings?.CharacterFor(who);
            const float iconSize = 20f;
            const float rowY = 41f;

            ImGui.SetCursorPos(new Vector2(x, rowY));

            if (info == null)
            {
                ImGui.TextColored(TextMuted, "...");
                return;
            }
            if (string.IsNullOrWhiteSpace(info.JobName))
                return;

            DrawJobIconInline(info.JobId, iconSize, false);

            float textH = ImGui.GetTextLineHeight();
            ImGui.SetCursorPos(new Vector2(x + iconSize + 8f, rowY + ((iconSize - textH) * 0.5f)));
            ImGui.TextColored(TextSecondary, info.JobLevel > 0
                ? $"Level {info.JobLevel} {info.JobName}"
                : info.JobName);
        }

        /// <summary>
        /// The links, behind a kebab in the banner's corner.
        ///
        /// Three full-width buttons for FFLogs, Tomestone and Lodestone took a whole row of the
        /// card and gave equal weight to three things nobody presses often. The same menu shape as
        /// the preset list uses, so it reads as the card's overflow rather than a new control.
        /// </summary>
        private void DrawProfileLinksMenu(CharacterIdentity who, float cardWidth)
        {
            ImGui.SetCursorPos(new Vector2(cardWidth - DotsSize - 10f, 10f));

            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0, 0, 0, 0));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(1, 1, 1, 0.07f));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(1, 1, 1, 0.11f));
            ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 5f);

            Vector2 at = ImGui.GetCursorScreenPos();
            bool clicked = ImGui.Button("##ProfileLinks", new Vector2(DotsSize, DotsSize));

            ImGui.PopStyleVar();
            ImGui.PopStyleColor(3);

            DrawGlyphCentered(FontAwesomeIcon.EllipsisH, at,
                new Vector2(at.X + DotsSize, at.Y + DotsSize), TextSecondary);

            if (clicked)
                ImGui.OpenPopup("profilelinks");

            if (ImGui.IsItemHovered())
                PaddedTooltip("Open this character elsewhere.");

            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(6, 6));
            if (ImGui.BeginPopup("profilelinks"))
            {
                string? region = Worlds?.GetFfLogsRegion(who.World);
                if (region != null && ImGui.Selectable("  FFLogs"))
                    Dalamud.Utility.Util.OpenLink(CharacterLinks.FfLogs(who.Name, who.World, region));
                if (ImGui.Selectable("  Tomestone"))
                    Dalamud.Utility.Util.OpenLink(CharacterLinks.Tomestone(who.Name, who.World));
                if (ImGui.Selectable("  Lodestone"))
                    Dalamud.Utility.Util.OpenLink(CharacterLinks.LodestoneSearch(who.Name, who.World));
                ImGui.EndPopup();
            }
            ImGui.PopStyleVar();
        }

        /// <summary>The bar and the one muted line under it. Checkable, not read at a glance.</summary>
        private void DrawProfileNumbers(CharacterIdentity who, PlayerRating? rating)
        {
            if (rating != null && rating.OptedOut)
            {
                ImGui.TextColored(TextSecondary, "They've opted out of ratings.");
                return;
            }

            int up = rating?.Upvotes ?? 0;
            int down = rating?.Downvotes ?? 0;
            int voters = rating?.Count ?? 0;

            DrawSplitBar(up, down);
            ImGui.Dummy(new Vector2(0, 8));

            // Carets rather than the words "up" and "down", and "total votes" rather than
            // "unweighted" - which was the schema's word for it, not one anybody says out loud.
            DrawArrowCount(FontAwesomeIcon.CaretUp, up, AccentGreen);
            ImGui.SameLine(0, 16);
            DrawArrowCount(FontAwesomeIcon.CaretDown, down, AccentRed);
            // A separator, not just a gap. "3" followed by "3 total votes" ran together into
            // "33total votes" at a glance, because two numbers sat side by side with nothing
            // between them.
            ImGui.SameLine(0, 14);
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(BorderHover, "|");
            ImGui.SameLine(0, 14);
            ImGui.AlignTextToFramePadding();

            // The count and the words are drawn as two items with a real gap between them.
            //
            // A single space inside the string kept disappearing - "0 total votes" rendered as
            // "0total votes" - so the separation no longer depends on a space character surviving
            // the text path at all. SameLine with an explicit width always produces a gap.
            ImGui.PushStyleColor(ImGuiCol.Text, TextMuted);
            ImGui.TextUnformatted(voters.ToString());
            ImGui.SameLine(0, 6);
            ImGui.TextUnformatted(voters == 1 ? "total vote" : "total votes");
            ImGui.PopStyleColor();
        }

        /// <summary>
        /// A coloured caret and its count, as one inline pair.
        ///
        /// The plugin's one way of showing a vote direction. It replaced a hand-drawn triangle in
        /// the recent list and a percentage chip on party rows - three different renderings of the
        /// same idea, none of which agreed with the others.
        /// </summary>
        internal void DrawArrowCount(FontAwesomeIcon icon, int count, Vector4 colour)
        {
            ImGui.PushFont(UiBuilder.IconFont);
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(count > 0 ? colour : TextMuted, icon.ToIconString());
            ImGui.PopFont();

            ImGui.SameLine(0, 5);
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(count > 0 ? TextSecondary : TextMuted, count.ToString());
        }

        /// <summary>The proportion of up to down, at a weight that reads as a rule rather than a
        /// panel. Full width, muted track when there is nothing to show.</summary>
        private void DrawSplitBar(int up, int down)
        {
            var dl = ImGui.GetWindowDrawList();
            Vector2 pos = ImGui.GetCursorScreenPos();
            float width = ImGui.GetContentRegionAvail().X - CardPad;
            const float h = 7f;

            int total = up + down;
            if (total == 0)
            {
                dl.AddRectFilled(pos, new Vector2(pos.X + width, pos.Y + h),
                    ImGui.ColorConvertFloat4ToU32(BgCardExpanded), 3.5f);
                ImGui.Dummy(new Vector2(width, h));
                return;
            }

            float upW = width * ((float)up / total);

            dl.AddRectFilled(pos, new Vector2(pos.X + width, pos.Y + h),
                ImGui.ColorConvertFloat4ToU32(AccentRed), 3.5f);
            if (upW > 0.5f)
            {
                dl.AddRectFilled(pos, new Vector2(pos.X + upW, pos.Y + h),
                    ImGui.ColorConvertFloat4ToU32(AccentGreen), 3.5f,
                    down == 0 ? ImDrawFlags.RoundCornersAll : ImDrawFlags.RoundCornersLeft);
            }

            ImGui.Dummy(new Vector2(width, h));
        }

        /// <summary>
        /// Duties this install has seen them in.
        ///
        /// Local only. A central record of which duties a named player has been in is a tracking
        /// database, and the guarantee is structural rather than a policy: the server has no route
        /// that accepts this, so it cannot be asked for it.
        /// </summary>
        private void DrawSeenRecentlyIn(CharacterIdentity who)
        {
            // The label drawn directly rather than via DrawSectionLabel, which ends with a
            // Dummy - so a SameLine after it attached to the spacer and dropped the note onto its
            // own line, which is exactly what it was moved up here to avoid.
            ImGui.TextColored(AccentBlue, "SEEN RECENTLY IN");
            ImGui.SameLine(0, 10);
            ImGui.TextColored(TextMuted, "kept on this machine only");
            ImGui.Dummy(new Vector2(0, 4));

            var seen = Encounters?.SeenIn(who);
            if (seen == null || seen.Count == 0)
            {
                ImGui.TextColored(TextMuted, "You haven't run anything with them yet.");
                return;
            }

            float right = ImGui.GetContentRegionAvail().X - CardPad;

            foreach (var (duty, when) in seen)
            {
                string label = string.IsNullOrWhiteSpace(duty) ? "A duty" : duty;
                string ago = Ago(when);
                float agoW = ImGui.CalcTextSize(ago).X;

                float x = ImGui.GetCursorPosX();
                ImGui.TextColored(TextSecondary, Fit(label, right - agoW - 14f));
                ImGui.SameLine();
                ImGui.SetCursorPosX(x + right - agoW);
                ImGui.TextColored(TextMuted, ago);
            }

        }

        /// <summary>Width the caret pair occupies, so callers can reserve a fixed column for it
        /// instead of measuring per row.</summary>
        internal static float ArrowCountWidth(int count)
        {
            ImGui.PushFont(UiBuilder.IconFont);
            float icon = ImGui.CalcTextSize(FontAwesomeIcon.CaretUp.ToIconString()).X;
            ImGui.PopFont();
            return icon + 5f + ImGui.CalcTextSize(count.ToString()).X;
        }

        /// <summary>Whether this is the logged-in character.</summary>
        private bool IsSelf(CharacterIdentity who)
        {
            var me = LocalIdentity?.Invoke();
            return me != null && me.Key.Equals(who.Key, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Colour for a net score.
        ///
        /// Zero is the pivot rather than fifty, because this is a tally and not a share: a single
        /// downvote should not look the same as a run of them.
        /// </summary>
        private static Vector4 NetScoreColor(int score) => score switch
        {
            >= 20 => ColorFromHex("#3fb56a"),
            >= 5 => ColorFromHex("#7ec96f"),
            >= 1 => ColorFromHex("#a8d67f"),
            0 => ColorFromHex("#9aa7bb"),
            >= -4 => ColorFromHex("#ffbd2e"),
            _ => ColorFromHex("#e06a5a"),
        };
    }
}
#endif
