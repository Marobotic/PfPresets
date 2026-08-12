using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;

namespace PfPresets
{
    /// <summary>
    /// The main window's frame: the left rail and header strip when there is room for them, the
    /// compact title bar and top tabs when there isn't.
    ///
    /// Two layouts, one set of bodies. The rail exists because at 980px the tabs were three labels
    /// stranded across the top of a very wide window with the whole left edge empty; below ~760px
    /// a 186px rail is a quarter of the window spent on navigation, so it folds back into a row.
    /// Nothing about the tabs themselves changes - only where they are.
    /// </summary>
    public partial class PluginUI
    {
        private const float RailWidth = 186f;
        private const float HeaderStripHeight = 44f;

        /// <summary>Where the rail stops paying for itself. Measured on the window, so dragging the
        /// edge switches layouts live rather than at the next open.</summary>
        private const float WideLayoutMinWidth = 760f;

        private const string KofiUrl = "https://ko-fi.com/marobotic";

        /// <summary>The same words in the rail and in the narrow title bar. The button is an ask,
        /// and an ask that changes its wording with the window width reads as two different
        /// buttons.</summary>
        private const string KofiLabel = "Support me on Ko-fi";

#if PFP_RATINGS
        /// <summary>
        /// The destinations, in order, for whichever layout is asking.
        ///
        /// Ratings is absent, not disabled, when the feature is off. A tab that exists only to
        /// explain why it does nothing is worse than no tab: the setting already says what it does,
        /// and the navigation should describe the plugin you are actually running.
        /// </summary>
        private List<(string Label, FontAwesomeIcon Icon, MainTab Tab)> TabList()
        {
            var tabs = new List<(string, FontAwesomeIcon, MainTab)>
            {
                ("Recruit", FontAwesomeIcon.Users, MainTab.Presets),
            };

            // BOTH OR NEITHER. Opting out is opting out of the community half of the plugin, and
            // the feed is the community half - it is built out of other people's duties, and taking
            // part in it means posting your own and hearting theirs.
            //
            // This tab was briefly ungated on the reasoning that reading a feed is not rating
            // anybody, which is true and beside the point: somebody who has opted out is not
            // sending us their clears, so the feed they would be shown is one they can only watch.
            // Opting out leaves the presets, which is the part of this plugin that involves nobody
            // else, and nothing else.
            //
            // "Achievements", with beta drawn as a chip beside it rather than baked into the label
            // - see BetaChipWidth. The word belongs where somebody decides whether to click, but it
            // should not be the reason the whole row runs out of room.
            if (config.CommunityEnabled)
            {
                tabs.Add(("My Profile", FontAwesomeIcon.Star, MainTab.Ratings));
                tabs.Add(("Achievements", FontAwesomeIcon.Trophy, MainTab.Achievements));
            }

            // THE VOTE TAB IS TEMPORARY and is not gated with the rest. It appears while the
            // server says a poll is running and disappears when one is not, without a release
            // either way - and it is shown to people who have opted out, because the poll decides
            // the future of the system they opted out of. See PluginUI.Vote.cs.
            EnsurePollLoaded();
            if (PollAvailable)
                tabs.Add(("Vote", FontAwesomeIcon.CheckSquare, MainTab.Vote));

            tabs.Add(("Settings", FontAwesomeIcon.Cog, MainTab.Settings));

            // Optional components may append their own. Both layouts read this one list, so a tab
            // added here appears in the rail and in the narrow strip together, sized to fit,
            // without either of them knowing what it is.
            AddExtraTabs(tabs);

            // Opting out while looking at one of them would otherwise leave the window on a tab
            // that is no longer in the list. The same applies to any extra that has just been
            // switched off: land on something that exists rather than on a blank body.
            if (!config.CommunityEnabled
                && (activeTab == MainTab.Ratings || activeTab == MainTab.Achievements))
                activeTab = MainTab.Presets;

            if (!tabs.Exists(t => t.Item3 == activeTab))
                activeTab = MainTab.Presets;

            return tabs;
        }
#endif

        /// <summary>
        /// Set by /pfpdebug chrome. The next frame the main window draws, it reports where the
        /// navigation actually ended up and then clears itself.
        ///
        /// Here because the tabs have gone missing twice and reading the code has not found it
        /// either time. Every number the layout depends on is knowable at draw time; none of it is
        /// knowable from outside the game, so the plugin says it out loud on request.
        /// </summary>
        internal bool ChromeDiagnosticRequested { get; set; }

        /// <summary>Where a diagnostic line goes. Supplied by the plugin, which owns the chat
        /// handle; the UI has no business holding one for anything else.</summary>
        internal Action<string>? DiagnosticSink { get; set; }

        private void ReportChromeDiagnostic(string what)
        {
            if (!ChromeDiagnosticRequested)
                return;

            DiagnosticSink?.Invoke($"[PF Analysis debug] {what}");
        }

        // ── Rail (wide) ───────────────────────────────────────────

        /// <summary>
        /// The left rail: who this is, where you can go, and the one ask.
        ///
        /// Drawn into its own child so the Ko-fi block can be pinned to the bottom without the
        /// body's height entering into it.
        /// </summary>
        private void DrawRail()
        {
            ImGui.PushStyleColor(ImGuiCol.ChildBg, Panel);

            // Nothing in this column may scroll, and the wheel may not move it.
            //
            // This is what kept eating the tabs. The rail lays itself out with explicit Dummies,
            // but ImGui also adds ItemSpacing.Y after every one of them - fifteen or so items'
            // worth, none of it in the spacer arithmetic. The column therefore ran a few dozen
            // pixels past its own height, and a wheel scroll anywhere over it slid the nav rows up
            // out of sight: the tabs had not disappeared, they were above the top of the column.
            // Zero spacing makes the hand-written layout exact, and NoScrollWithMouse means even
            // if something below still overflows, the top of the rail stays put.
            ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, Vector2.Zero);
            ImGui.BeginChild("RailColumn", new Vector2(RailWidth, -1), false,
                ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
            try
            {
                float railHeight = ImGui.GetContentRegionAvail().Y;

                if (ChromeDiagnosticRequested) ReportChromeDiagnostic($"rail: h={railHeight:F0} availX={ImGui.GetContentRegionAvail().X:F0} "
                    + $"scrollY={ImGui.GetScrollY():F0} scrollMaxY={ImGui.GetScrollMaxY():F0}");

                ImGui.Dummy(new Vector2(0, 14));
                ImGui.Indent(14);
                DrawBrand();
                ImGui.Unindent(14);

                ImGui.Dummy(new Vector2(0, 14));
                DrawRuleStrong(padBelow: 8f);

#if PFP_RATINGS
                var tabs = TabList();
                if (ChromeDiagnosticRequested) ReportChromeDiagnostic($"rail nav: {tabs.Count} rows, cursorY={ImGui.GetCursorPosY():F0}");

                foreach (var (label, icon, tab) in tabs)
                    DrawRailNavItem(label, icon, tab);

                DrawRuleHair(padAbove: 8f);
#endif

                // The ask is placed from the bottom rather than pushed there by a spacer, so a
                // mistake in its height can never displace anything above it. If the column is too
                // short to hold both, the navigation keeps its place and the ask simply follows it.
                float blockHeight = KofiBlockHeight();
                float navBottom = ImGui.GetCursorPosY();
                float top = MathF.Max(navBottom + 12f, railHeight - blockHeight);

                ImGui.SetCursorPosY(top);
                DrawRuleStrong(padBelow: 10f);
                ImGui.Indent(14);
                DrawKofiBlock(RailWidth - 28f);
                ImGui.Unindent(14);

                // One frame's worth, then it turns itself off.
                ChromeDiagnosticRequested = false;
            }
            finally
            {
                ImGui.EndChild();
                ImGui.PopStyleVar();
                ImGui.PopStyleColor();
            }
        }

        /// <summary>
        /// Height of the rule, the button and the two lines under it.
        ///
        /// Measured rather than guessed at, because this number decides where the block starts and
        /// a guess that runs long is what makes the column scroll.
        /// </summary>
        private float KofiBlockHeight()
        {
            float help;
            using (UiHelpFont.Push())
                help = ImGui.GetTextLineHeight();

            return 2f + 10f      // rule and its padding
                 + 30f           // the button
                 + 6f            // gap under it
                 + help * 2f     // "Passion project," / "support appreciated"
                 + 12f;          // breathing room at the bottom of the column
        }

        /// <summary>The mark, the name, and the version underneath in the label face.</summary>
        private void DrawBrand()
        {
            var dl = ImGui.GetWindowDrawList();
            Vector2 p = ImGui.GetCursorScreenPos();

            const float mark = 26f;
            dl.AddRectFilled(p, new Vector2(p.X + mark, p.Y + mark),
                ImGui.ColorConvertFloat4ToU32(Accent));

            string glyph = LogoIcon.ToIconString();
            using (pluginInterface.UiBuilder.IconFontHandle.Push())
            {
                Vector2 gs = ImGui.CalcTextSize(glyph);
                dl.AddText(new Vector2(p.X + (mark - gs.X) * 0.5f, p.Y + (mark - gs.Y) * 0.5f),
                    ImGui.ColorConvertFloat4ToU32(OnAccent), glyph);
            }

            ImGui.SetCursorScreenPos(new Vector2(p.X + mark + 10f, p.Y - 1f));
            using (UiNameFont.Push())
                ImGui.TextColored(Ink, "PF Analysis");

            ImGui.SetCursorScreenPos(new Vector2(p.X + mark + 10f, p.Y + 15f));
            using (UiLabelFont.Push())
                ImGui.TextColored(Faint, VersionLabel.ToUpperInvariant());

                // Erased entirely in an ordinary build - see PluginUI.AdminHooks.cs.
                if (ImGui.IsItemClicked())
                    OnVersionLabelClicked();

            ImGui.SetCursorScreenPos(new Vector2(p.X, p.Y + mark));
        }

#if PFP_RATINGS
        /// <summary>
        /// One rail destination: a full-width flush-left row, with a 3px accent edge and a raised
        /// fill when it is the one you are on.
        ///
        /// The edge does the work a highlight colour would do in a rounded design - it marks the
        /// row without tinting the label, so an active item and a hovered one stay distinguishable.
        /// </summary>
        private void DrawRailNavItem(string label, FontAwesomeIcon icon, MainTab tab)
        {
            const float rowHeight = ButtonHeight;
            bool active = activeTab == tab;

            Vector2 p = ImGui.GetCursorScreenPos();
            float width = ImGui.GetContentRegionAvail().X;
            var dl = ImGui.GetWindowDrawList();

            if (ChromeDiagnosticRequested) ReportChromeDiagnostic($"  row {label}: screen=({p.X:F0},{p.Y:F0}) w={width:F0} "
                + $"localY={ImGui.GetCursorPosY():F0} clipped={!ImGui.IsRectVisible(new Vector2(width, rowHeight))}");

            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0, 0, 0, 0));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0, 0, 0, 0));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0, 0, 0, 0));
            if (ImGui.Button($"##rail{tab}", new Vector2(width, rowHeight)))
                activeTab = tab;
            ImGui.PopStyleColor(3);

            bool hovered = ImGui.IsItemHovered();

            if (active || hovered)
                dl.AddRectFilled(p, new Vector2(p.X + width, p.Y + rowHeight),
                    ImGui.ColorConvertFloat4ToU32(Raised));

            if (active)
                dl.AddRectFilled(p, new Vector2(p.X + 3f, p.Y + rowHeight),
                    ImGui.ColorConvertFloat4ToU32(Accent));

            // Inactive rows sit at Dim, not Faint. Faint is the help-text tone and on the rail's
            // panel it reads as disabled - navigation that looks switched off is navigation nobody
            // finds.
            Vector4 color = active ? Ink : hovered ? Ink : Dim;
            string glyph = icon.ToIconString();

            float textY;
            using (pluginInterface.UiBuilder.IconFontHandle.Push())
            {
                Vector2 gs = ImGui.CalcTextSize(glyph);
                textY = p.Y + (rowHeight - gs.Y) * 0.5f;
                dl.AddText(new Vector2(p.X + 16f, textY),
                    ImGui.ColorConvertFloat4ToU32(active ? Accent : color), glyph);
            }

            float labelEnd;
            using (UiBodyFont.Push())
            {
                Vector2 ts = ImGui.CalcTextSize(label);
                dl.AddText(new Vector2(p.X + 42f, p.Y + (rowHeight - ts.Y) * 0.5f),
                    ImGui.ColorConvertFloat4ToU32(color), label);
                labelEnd = p.X + 42f + ts.X;
            }

            // The beta mark, when the row has room for it. Drawn rather than written into the
            // label so the word cannot be what decides whether the navigation fits.
            if (tab == MainTab.Achievements
                && labelEnd + 6f + BetaChipWidth() <= p.X + width - 8f)
                DrawBetaChip(dl, new Vector2(labelEnd + 6f, p.Y), rowHeight, active ? 1f : 0.7f);
        }
#endif

        /// <summary>The one ask: a solid red button with the heart and the words, and a line saying
        /// what the money is actually for.</summary>
        private void DrawKofiBlock(float width)
        {
            Vector2 p = ImGui.GetCursorScreenPos();
            var size = new Vector2(width, 30f);

            if (DrawKofiButton("##RailKofi", size))
                Dalamud.Utility.Util.OpenLink(KofiUrl);

            DrawIconLabelLeft(FontAwesomeIcon.Heart, KofiLabel, p, size, Ink);

            ImGui.Dummy(new Vector2(0, 6));
            using (UiHelpFont.Push())
                ImGui.TextColored(Faint, "Passion project,");

            // The heart comes from the icon font rather than from the sentence. A literal U+2665 is
            // not in the text face's glyph range and would render as a box on the one line that is
            // supposed to be warm.
            using (UiHelpFont.Push())
                ImGui.TextColored(Faint, "support appreciated");
            ImGui.SameLine(0, 5);
            using (pluginInterface.UiBuilder.IconFontHandle.Push())
                ImGui.TextColored(KoFi, FontAwesomeIcon.Heart.ToIconString());
        }

        // ── Header strip (wide) ───────────────────────────────────

        /// <summary>
        /// The body's own 44px header: which tab you are on, and the two window controls.
        ///
        /// The tab name is repeated here rather than left to the rail because the rail is at the
        /// far edge of a 980px window, and the thing you are looking at should be named above what
        /// you are looking at.
        /// </summary>
        private void DrawHeaderStrip()
        {
            Vector2 p = ImGui.GetCursorScreenPos();
            float width = ImGui.GetContentRegionAvail().X;
            var dl = ImGui.GetWindowDrawList();

            dl.AddRectFilled(p, new Vector2(p.X + width, p.Y + HeaderStripHeight),
                ImGui.ColorConvertFloat4ToU32(Panel));

            // The strip names what you are looking at, so it is a title, not a field label - at
            // label size it read as a caption for the window rather than as its heading. Set in
            // tracked caps at the secondary tone: it identifies the surface without competing with
            // anything on it.
            using (UiHeadingFont.Push())
            {
                float lineH = ImGui.GetTextLineHeight();
                DrawTrackedCaps(dl, new Vector2(p.X + 18f, p.Y + (HeaderStripHeight - lineH) * 0.5f),
                    ActiveTabName, Dim);
            }

            const float btn = 26f;
            float y = p.Y + (HeaderStripHeight - btn) * 0.5f;

            ImGui.SetCursorScreenPos(new Vector2(p.X + width - btn * 2f - 8f - 12f, y));
            DrawWindowGlyphButton(isMinimized ? FontAwesomeIcon.Expand : FontAwesomeIcon.Minus,
                "HeaderMinimize", isMinimized ? "Restore" : "Minimise", btn, () =>
                {
                    isMinimized = !isMinimized;
                    if (!isMinimized) shouldRestoreSize = true;
                });

            ImGui.SetCursorScreenPos(new Vector2(p.X + width - btn - 12f, y));
            DrawWindowGlyphButton(FontAwesomeIcon.Times, "HeaderClose", "Close", btn,
                () => isMainWindowVisible = false);

            ImGui.SetCursorScreenPos(new Vector2(p.X, p.Y + HeaderStripHeight));
            DrawRuleStrong();
        }

        /// <summary>Minimise and close: square, unfilled, and the only centred glyphs in the
        /// window.</summary>
        private void DrawWindowGlyphButton(FontAwesomeIcon icon, string id, string tooltip,
            float size, System.Action onClick)
        {
            Vector2 p = ImGui.GetCursorScreenPos();
            ImGui.InvisibleButton($"##{id}", new Vector2(size, size));
            bool hot = ImGui.IsItemHovered();

            if (ImGui.IsItemClicked())
                onClick();

            var dl = ImGui.GetWindowDrawList();
            if (hot)
                dl.AddRectFilled(p, new Vector2(p.X + size, p.Y + size),
                    ImGui.ColorConvertFloat4ToU32(Raised));

            string glyph = icon.ToIconString();
            using (pluginInterface.UiBuilder.IconFontHandle.Push())
            {
                Vector2 gs = ImGui.CalcTextSize(glyph);
                dl.AddText(new Vector2(p.X + (size - gs.X) * 0.5f, p.Y + (size - gs.Y) * 0.5f),
                    ImGui.ColorConvertFloat4ToU32(hot ? Ink : Dim), glyph);
            }

            if (hot)
                PaddedTooltip(tooltip);
        }

        // ── Title bar (narrow) ────────────────────────────────────

        /// <summary>
        /// The narrow window's header: everything the rail carries, on one line.
        ///
        /// The Ko-fi copy is dropped rather than wrapped - there is no room for a sentence, and a
        /// heart on a red button in the corner of a plugin window is not ambiguous.
        /// </summary>
        private void DrawNarrowTitleBar()
        {
            const float barHeight = 52f;

            Vector2 p = ImGui.GetCursorScreenPos();
            float width = ImGui.GetContentRegionAvail().X;
            var dl = ImGui.GetWindowDrawList();

            dl.AddRectFilled(p, new Vector2(p.X + width, p.Y + barHeight),
                ImGui.ColorConvertFloat4ToU32(Panel));

            ImGui.SetCursorScreenPos(new Vector2(p.X + 12f, p.Y + 9f));
            DrawBrand();

            const float btn = 26f;
            float y = p.Y + (barHeight - btn) * 0.5f;

            // Wide enough for the whole sentence at any window width. A bare heart is a guess and
            // "Ko-fi" alone assumes the reader knows what Ko-fi is; this is the one control here
            // that has to say what it does.
            // Padding on both sides of the words, not just the left. The width was the label plus
            // one gap, so the last letter finished flush against the button's right edge.
            float kofiW;
            using (pluginInterface.UiBuilder.IconFontHandle.Push())
                kofiW = ImGui.CalcTextSize(FontAwesomeIcon.Heart.ToIconString()).X;
            kofiW += 8f + ImGui.CalcTextSize(KofiLabel).X + ButtonPadX * 2f;
            Vector2 kofiPos = new(p.X + width - btn * 2f - kofiW - 8f - 8f - 12f, y);
            ImGui.SetCursorScreenPos(kofiPos);
            var kofiSize = new Vector2(kofiW, btn);
            if (DrawKofiButton("##NarrowKofi", kofiSize))
                Dalamud.Utility.Util.OpenLink(KofiUrl);
            bool kofiHot = ImGui.IsItemHovered();
            DrawIconLabelLeft(FontAwesomeIcon.Heart, KofiLabel, kofiPos, kofiSize, Ink, ButtonPadX);
            if (kofiHot)
                PaddedTooltip("Support on Ko-fi");

            ImGui.SetCursorScreenPos(new Vector2(p.X + width - btn * 2f - 8f - 12f, y));
            DrawWindowGlyphButton(isMinimized ? FontAwesomeIcon.Expand : FontAwesomeIcon.Minus,
                "NarrowMinimize", isMinimized ? "Restore" : "Minimise", btn, () =>
                {
                    isMinimized = !isMinimized;
                    if (!isMinimized) shouldRestoreSize = true;
                });

            ImGui.SetCursorScreenPos(new Vector2(p.X + width - btn - 12f, y));
            DrawWindowGlyphButton(FontAwesomeIcon.Times, "NarrowClose", "Close", btn,
                () => isMainWindowVisible = false);

            ImGui.SetCursorScreenPos(new Vector2(p.X, p.Y + barHeight));
            DrawRuleStrong();
        }

        /// <summary>The name of the tab currently on screen, for the header strip.</summary>
        private string ActiveTabName
        {
#if PFP_RATINGS
            get => activeTab switch
            {
                MainTab.Ratings => "My Profile",
                MainTab.Settings => "Settings",
                _ => "Recruit",
            };
#else
            get => "Recruit";
#endif
        }

        /// <summary>
        /// An icon and a label drawn flush left inside a rect, for buttons whose label mixes the
        /// icon font with the text font (which a plain ImGui.Button cannot do).
        ///
        /// The left-aligned counterpart to <see cref="DrawIconLabelCentered"/>: everything in this
        /// design shares one left edge, including the labels inside wide buttons.
        /// </summary>
        private void DrawIconLabelLeft(FontAwesomeIcon icon, string text, Vector2 rectMin,
            Vector2 rectSize, Vector4 color, float padLeft = 12f)
        {
            var dl = ImGui.GetWindowDrawList();
            string glyph = icon.ToIconString();
            uint colU = ImGui.ColorConvertFloat4ToU32(color);
            float midY = rectMin.Y + rectSize.Y * 0.5f;

            Vector2 iconSize;
            using (pluginInterface.UiBuilder.IconFontHandle.Push())
            {
                iconSize = ImGui.CalcTextSize(glyph);
                dl.AddText(new Vector2(rectMin.X + padLeft, midY - iconSize.Y * 0.5f), colU, glyph);
            }

            if (string.IsNullOrEmpty(text))
                return;

            Vector2 ts = ImGui.CalcTextSize(text);
            dl.AddText(new Vector2(rectMin.X + padLeft + iconSize.X + 8f, midY - ts.Y * 0.5f),
                colU, text);
        }
    }
}
