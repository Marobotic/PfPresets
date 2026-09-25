using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;

namespace PfPresets
{
    /// <summary>
    /// The window's frame, in its two fixed shapes.
    ///
    /// PHONE: a status bar, a brand header, the body, and a tab bar across the bottom with a home
    /// indicator under it. TABLET: a sidebar down the left and a titled header over the body.
    ///
    /// One set of bodies, two frames, and no third state in between. The old chrome had three - a
    /// rail above 760px, a tab strip below it, and a 52px collapsed bar - and picked between them
    /// from whatever width the window happened to have been dragged to. Two named layouts the player
    /// chooses in Settings is the whole reason the rest of this design can assume a width.
    /// </summary>
    public partial class PluginUI
    {
        // ── Tablet ────────────────────────────────────────────────

        /// <summary>The sidebar. Wider than the old 186px rail because a tablet has the room and a
        /// label should not have to be measured against the chip beside it.</summary>
        private const float RailWidth = 252f;

        /// <summary>The header over the body. Matches the phone's, so the two layouts are the same
        /// design at two sizes rather than two designs.</summary>
        private const float HeaderStripHeight = 58f;

        // ── Phone ─────────────────────────────────────────────────

        /// <summary>The brand header, level with the tablet's, and the first thing on the screen.
        ///
        /// There WAS a status bar above it - a clock, signal, wifi, battery - for the same reason a
        /// phone has one. It went: it is a picture of a phone rather than a part of this one, it
        /// said nothing the player did not already have on their taskbar, and it cost 44px off the
        /// top of a 900px window that the preset list would rather have.
        /// </summary>
        private const float PortraitHeaderHeight = 58f;

        /// <summary>The row of tabs.</summary>
        private const float TabBarHeight = 60f;

        /// <summary>The strip under the tabs that holds the home indicator. Empty space with one
        /// capsule in it, and it is not decoration: without it the bottom row of tabs sits hard
        /// against the edge of the screen, which is the one place a phone never puts a control.
        /// </summary>
        private const float HomeIndicatorHeight = 20f;

        private const string KofiUrl = "https://ko-fi.com/marobotic";

        /// <summary>The same words in the sidebar and on the phone's Settings tab. The button is an
        /// ask, and an ask that changes its wording with the layout reads as two different
        /// buttons.</summary>
        private const string KofiLabel = "Support me on Ko-fi";

        /// <summary>Total height the tab bar and its home indicator take off the body.</summary>
        private static float PortraitTabBarHeight() => TabBarHeight + HomeIndicatorHeight;

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
            // IT IS CALLED CLEARS. It was "Achievements", which is the word the game uses for a
            // list of five thousand things including catching a fish, and this tab has never been
            // about any of them - it is the ultimates and the savage tiers people in your Party
            // Finder have actually killed. Five tabs have to fit across a 460px phone, and "Clears"
            // is both the shorter word and the true one.
            if (config.CommunityEnabled)
            {
                tabs.Add(("My Profile", FontAwesomeIcon.Star, MainTab.Ratings));
                tabs.Add(("Clears", FontAwesomeIcon.Trophy, MainTab.Achievements));

                // Every listing plugin users have seen, and every party they are in. Community
                // like the two above: it is built out of what other installs share.
                if (Board != null)
                    tabs.Add(("Party Finder", FontAwesomeIcon.Binoculars, MainTab.PartyFinder));

                TickUnreadBadge();
            }

            // THE VOTE TAB IS TEMPORARY and is not gated with the rest. It appears while the
            // server says a poll is running and disappears when one is not, without a release
            // either way - and it is shown to people who have opted out, because the poll decides
            // the future of the system they opted out of. See PluginUI.Vote.cs.
            EnsurePollLoaded();
            if (PollAvailable)
                tabs.Add(("Vote", FontAwesomeIcon.CheckSquare, MainTab.Vote));

            // Always there, community or not: a bug report or a question is not a community feature.
            if (Ratings != null)
                tabs.Add(("Feedback", FontAwesomeIcon.CommentDots, MainTab.Feedback));

            tabs.Add(("Settings", FontAwesomeIcon.Cog, MainTab.Settings));

            // Optional components may append their own. Both layouts read this one list, so a tab
            // added here appears in the sidebar and in the tab bar together, sized to fit, without
            // either of them knowing what it is.
            AddExtraTabs(tabs);

            // Opting out while looking at one of them would otherwise leave the window on a tab
            // that is no longer in the list. The same applies to any extra that has just been
            // switched off: land on something that exists rather than on a blank body.
            if (!config.CommunityEnabled
                && (activeTab == MainTab.Ratings || activeTab == MainTab.Achievements
                    || activeTab == MainTab.PartyFinder))
                activeTab = MainTab.Presets;

            if (!tabs.Exists(t => t.Item3 == activeTab))
                activeTab = MainTab.Presets;

            return tabs;
        }

        /// <summary>
        /// The unread badge's tick, alongside the poll's and for the same reason: the answer changes
        /// what the navigation looks like, so it is asked where the navigation is drawn.
        ///
        /// Never while the FIRST CLEARS list is being read, where the column in front of them is the
        /// answer and a second one would be paid for and thrown away.
        ///
        /// That is narrower than "while the clears tab is open", and deliberately so now the tab
        /// holds three lists. The badge counts first clears; somebody sitting on the Ultimate
        /// reclears or on their own clears is being shown none of them, so the count is neither
        /// answered nor claimed by their being there and has every reason to keep ticking.
        /// </summary>
        private void TickUnreadBadge()
        {
            if (!config.CommunityEnabled)
                return;

            if (activeTab == MainTab.Achievements && clearsView == ClearsView.First)
                return;

            Ratings?.EnsureUnseenChecked();
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

        // ══════════════════════════════════════════════════════════
        //  PHONE
        // ══════════════════════════════════════════════════════════

        /// <summary>The phone's header: the mark, the name, the version, and the one window control
        /// left - close.</summary>
        private void DrawPortraitHeader()
        {
            Vector2 p = ImGui.GetCursorScreenPos();
            float width = ImGui.GetContentRegionAvail().X;
            var dl = ImGui.GetWindowDrawList();

            // Rounded to the screen's own radius at the top, square at the bottom: this is the
            // first thing in the window now, so the fill has to follow the corner rather than poke
            // through it.
            dl.AddRectFilled(p, new Vector2(p.X + width, p.Y + PortraitHeaderHeight),
                ImGui.ColorConvertFloat4ToU32(Panel),
                Radius.Screen, ImDrawFlags.RoundCornersTop);

            ImGui.SetCursorScreenPos(new Vector2(p.X + 16f, p.Y + (PortraitHeaderHeight - 28f) * 0.5f));
            DrawBrand();

            const float btn = 28f;
            float ctrlY = p.Y + (PortraitHeaderHeight - btn) * 0.5f;
            DrawRoundCloseButton("##PhoneClose", new Vector2(p.X + width - btn - 16f, ctrlY), btn);

            DrawPortraitKofiButton(p, width, btn);

            // The mockup's hairline under the bar, drawn rather than laid out.
            dl.AddRectFilled(new Vector2(p.X, p.Y + PortraitHeaderHeight),
                new Vector2(p.X + width, p.Y + PortraitHeaderHeight + 1f),
                ImGui.ColorConvertFloat4ToU32(new Vector4(1, 1, 1, 0.08f)));
            ImGui.SetCursorScreenPos(new Vector2(p.X, p.Y + PortraitHeaderHeight));
            DrawRuleHair();
        }

        /// <summary>
        /// Ko-fi in the phone header, immediately left of the close button.
        ///
        /// THE SAME BUTTON THE SIDEBAR HAS, not a hint of it. It was the heart glyph alone on the
        /// reasoning that a 460px header has no room for words - which was true of the sidebar's
        /// whole block, the button plus two lines about what the money is for, and not true of the
        /// button. A bare heart beside a close cross reads as a control of the window: favourite it,
        /// pin it, something. The red fill and the words are the entire point, and they fit.
        ///
        /// Measured before it is placed, and it steps back to the heart alone if the header is ever
        /// too narrow to hold it beside the mark - which no current layout is, but the header is one
        /// screen size away from being.
        /// </summary>
        private void DrawPortraitKofiButton(Vector2 headerMin, float width, float closeSize)
        {
            const float kofiH = 34f;
            const float padX = 12f;

            float iconW;
            using (pluginInterface.UiBuilder.IconFontHandle.Push())
                iconW = ImGui.CalcTextSize(FontAwesomeIcon.Heart.ToIconString()).X;

            float kofiW = padX + iconW + 8f + ImGui.CalcTextSize(KofiLabel).X + padX;
            float right = headerMin.X + width - closeSize - 16f - Space.Tight;

            // The mark and the name on the left keep their room; the button is what gives way,
            // because the header without it still says what the window is. Measured rather than
            // reserved: the version chip under the name is the widest part of the mark on some
            // builds and a constant would be wrong for exactly those.
            bool fits = right - kofiW >= headerMin.X + 16f + BrandWidth() + Space.Gutter;

            if (!fits)
            {
                DrawKofiPill("##PhoneKofi", new Vector2(right - closeSize,
                    headerMin.Y + (PortraitHeaderHeight - closeSize) * 0.5f), new Vector2(closeSize), labelled: false);
                return;
            }

            DrawKofiPill("##PhoneKofi", new Vector2(right - kofiW, headerMin.Y + (PortraitHeaderHeight - kofiH) * 0.5f),
                new Vector2(kofiW, kofiH), labelled: true);
        }

        /// <summary>
        /// The tab bar: an icon over a word, five across, the active one in the accent.
        ///
        /// Every cell is the same width, and that is a decision the fixed screen pays for. A tab bar
        /// that measured its labels and shared out the slack - which is what the old strip did -
        /// moved every tab sideways whenever one of them appeared or went away, so the Settings tab
        /// was in a different place depending on whether a poll happened to be running.
        /// </summary>
#if PFP_RATINGS
        private void DrawPortraitTabBar(List<(string Label, FontAwesomeIcon Icon, MainTab Tab)> tabs)
        {
            Vector2 p = ImGui.GetCursorScreenPos();
            float width = ImGui.GetContentRegionAvail().X;
            var dl = ImGui.GetWindowDrawList();
            float total = TabBarHeight + HomeIndicatorHeight;

            dl.AddRectFilled(p, new Vector2(p.X + width, p.Y + total),
                ImGui.ColorConvertFloat4ToU32(Panel),
                Radius.Screen, ImDrawFlags.RoundCornersBottom);
            dl.AddRectFilled(p, new Vector2(p.X + width, p.Y + 1f),
                ImGui.ColorConvertFloat4ToU32(new Vector4(1, 1, 1, 0.08f)));

            if (tabs.Count == 0)
                return;

            if (activeTab == MainTab.Settings)
            {
                // Settings' own bar: back, then its pages - the phone's version of the sidebar
                // swapping its tabs for them.
                DrawPortraitSettingsBar(dl, p, width);
            }
            else
            {
                float cell = width / tabs.Count;

                if (ChromeDiagnosticRequested)
                    ReportChromeDiagnostic($"tab bar: {tabs.Count} tabs at {cell:F0}px each");

                for (int i = 0; i < tabs.Count; i++)
                    DrawPortraitTab(dl, tabs[i].Label, tabs[i].Icon, tabs[i].Tab,
                        new Vector2(p.X + cell * i, p.Y), new Vector2(cell, TabBarHeight));
            }

            // The home indicator. Nothing happens if you press it - there is no home to go to - so
            // it is drawn rather than built as a control, and it is Faint rather than Ink so it
            // never looks like one.
            const float indW = 132f, indH = 5f;
            var indPos = new Vector2(p.X + (width - indW) * 0.5f,
                p.Y + TabBarHeight + (HomeIndicatorHeight - indH) * 0.5f);
            dl.AddRectFilled(indPos, new Vector2(indPos.X + indW, indPos.Y + indH),
                ImGui.ColorConvertFloat4ToU32(new Vector4(1, 1, 1, 0.3f)), Radius.Pill);

            ImGui.SetCursorScreenPos(new Vector2(p.X, p.Y + total));
        }
#else
        /// <summary>Without the community half there is nothing to navigate between - the plugin is
        /// the preset list and nothing else - so the phone has no tab bar and the body simply runs
        /// to the home indicator.</summary>
        private void DrawPortraitTabBar()
        {
            Vector2 p = ImGui.GetCursorScreenPos();
            float width = ImGui.GetContentRegionAvail().X;
            var dl = ImGui.GetWindowDrawList();
            float total = TabBarHeight + HomeIndicatorHeight;

            dl.AddRectFilled(p, new Vector2(p.X + width, p.Y + total),
                ImGui.ColorConvertFloat4ToU32(Panel),
                Radius.Screen, ImDrawFlags.RoundCornersBottom);

            const float indW = 132f, indH = 5f;
            var indPos = new Vector2(p.X + (width - indW) * 0.5f,
                p.Y + TabBarHeight + (HomeIndicatorHeight - indH) * 0.5f);
            dl.AddRectFilled(indPos, new Vector2(indPos.X + indW, indPos.Y + indH),
                ImGui.ColorConvertFloat4ToU32(BorderControl), Radius.Pill);

            ImGui.SetCursorScreenPos(new Vector2(p.X, p.Y + total));
        }
#endif

#if PFP_RATINGS
        /// <summary>One tab: icon over label, both in one colour, with the badge over the icon's
        /// corner where a phone puts it.</summary>
        private void DrawPortraitTab(ImDrawListPtr dl, string label, FontAwesomeIcon icon, MainTab tab,
            Vector2 pos, Vector2 size)
        {
            bool active = activeTab == tab;

            ImGui.SetCursorScreenPos(pos);
            if (ImGui.InvisibleButton($"##tabbar{tab}", size))
            {
                // Choosing a tab by hand is its own navigation: whatever Back was remembering is
                // no longer where you came from.
                activeTab = tab;
                profileReturnTab = null;
            }

            bool hovered = ImGui.IsItemHovered();
            if (hovered && !active)
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);

            // The sidebar's language in a bar: the page you are on sits on a soft accent pill and
            // takes the accent; the rest are slate, going lighter under the cursor.
            if (active || hovered)
            {
                var pillMin = pos + new Vector2(6f, 6f);
                var pillMax = pos + size - new Vector2(6f, 6f);
                dl.AddRectFilled(pillMin, pillMax, ImGui.ColorConvertFloat4ToU32(
                    active ? Accent with { W = 0.14f } : new Vector4(1, 1, 1, 0.04f)), Radius.Card);
            }

            Vector4 colour = active ? new Vector4(1, 1, 1, 1) : hovered ? PfSlate300 : FbSlate500;
            uint col = ImGui.ColorConvertFloat4ToU32(colour);

            // Laid out from the group's own height rather than from the cell's: the icon face and
            // the label face are different sizes, and centring each on its own half leaves a gap
            // that grows with the difference.
            float iconH, labelH, iconW = 0f, labelW;
            string glyph = icon.ToIconString();

            using (pluginInterface.UiBuilder.IconFontHandle.Push())
            {
                Vector2 gs = ImGui.CalcTextSize(glyph);
                iconH = gs.Y;
                iconW = gs.X;
            }

            using (UiLabelFont.Push())
            {
                Vector2 ts = ImGui.CalcTextSize(label);
                labelH = ts.Y;
                labelW = ts.X;
            }

            const float gap = 4f;
            float top = pos.Y + (size.Y - (iconH + gap + labelH)) * 0.5f;
            float iconX = pos.X + (size.X - iconW) * 0.5f;

            if (LucideFor(tab) is not { } lucideName
                || !DrawLucide(dl, lucideName, new Vector2(pos.X + (size.X - iconH) * 0.5f, top), iconH, new Vector4(1, 1, 1, 1)))
            {
                using (pluginInterface.UiBuilder.IconFontHandle.Push())
                    dl.AddText(new Vector2(iconX, top), col, glyph);
            }

            using (UiLabelFont.Push())
                dl.AddText(new Vector2(pos.X + (size.X - labelW) * 0.5f, top + iconH + gap),
                    col, label);

            DrawCornerBadge(dl, TabBadgeFor(tab), new Vector2(iconX + iconW, top), size, pos);

            if (hovered)
                PaddedTooltip(TabTooltip(label, tab));
        }

        /// <summary>The phone's bar while Settings is open: Back, and a cell per page with its glyph
        /// in the page's own colour, the page on screen on the accent pill.</summary>
        private void DrawPortraitSettingsBar(ImDrawListPtr dl, Vector2 p, float width)
        {
            var pages = SettingsPages();
            int cells = pages.Count + 1;
            float cell = width / cells;

            for (int i = 0; i < cells; i++)
            {
                var pos = new Vector2(p.X + cell * i, p.Y);
                var size = new Vector2(cell, TabBarHeight);
                bool back = i == 0;
                bool active = !back && settingsPage == i - 1;

                ImGui.SetCursorScreenPos(pos);
                if (ImGui.InvisibleButton($"##settingsbar{i}", size))
                {
                    if (back)
                    {
                        activeTab = settingsReturnTab == MainTab.Settings ? MainTab.Presets : settingsReturnTab;
                        profileReturnTab = null;
                    }
                    else
                    {
                        settingsPage = i - 1;
                    }
                }
                bool hot = ImGui.IsItemHovered();
                if (hot && !active)
                    ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);

                if (active || hot)
                    dl.AddRectFilled(pos + new Vector2(6f), pos + size - new Vector2(6f), ImGui.ColorConvertFloat4ToU32(
                        active ? Accent with { W = 0.14f } : new Vector4(1, 1, 1, 0.04f)), Radius.Card);

                FontAwesomeIcon icon = back ? FontAwesomeIcon.ChevronLeft : pages[i - 1].Icon;
                string lucideName = back ? "chevron-left" : pages[i - 1].Lucide;
                string label = back ? "Back" : pages[i - 1].Label.Split(' ')[0];
                Vector4 colour = active || hot ? new Vector4(1, 1, 1, 1) : FbSlate400;

                const float glyph = 18f, gap = 4f;
                float labelH;
                using (UiLabelFont.Push())
                    labelH = ImGui.GetTextLineHeight();
                float top = pos.Y + (size.Y - (glyph + gap + labelH)) * 0.5f;
                var gMin = new Vector2(pos.X + (cell - glyph) * 0.5f, top);
                if (!DrawLucide(dl, lucideName, gMin, glyph, new Vector4(1, 1, 1, 1)))
                    DrawGlyphAtOn(dl, icon, gMin, glyph, new Vector4(1, 1, 1, 1), UiIconRow);
                using (UiLabelFont.Push())
                {
                    Vector2 ts = ImGui.CalcTextSize(label);
                    dl.AddText(new Vector2(pos.X + (cell - ts.X) * 0.5f, top + glyph + gap),
                        ImGui.ColorConvertFloat4ToU32(colour), label);
                }
            }
        }

        /// <summary>What a hovered tab says. The beta note lives here rather than in the label -
        /// there is no room for a chip beside a word in a 92px cell, and the word is the part that
        /// has to be readable.</summary>
        private string TabTooltip(string label, MainTab tab)
        {
            string tip = StatusChipFor(tab) is { } status ? $"{label} ({status.ToLowerInvariant()})" : label;

            var badge = TabBadgeFor(tab);
            if (badge.Kind == TabBadge.Count)
                tip += $" - {badge.Text} new";
            else if (badge.Kind == TabBadge.Dot)
                tip += " - not opened yet";

            return tip;
        }

        /// <summary>
        /// The badge, over the top-right corner of an icon.
        ///
        /// Overlaid rather than placed beside the label, which is what makes the cells able to be
        /// equal widths: a badge that took space would make the tab it is on wider than its
        /// neighbours, and the tab it is on is the one that keeps changing.
        /// </summary>
        private void DrawCornerBadge(ImDrawListPtr dl, (TabBadge Kind, string Text) badge,
            Vector2 iconTopRight, Vector2 cellSize, Vector2 cellPos)
        {
            if (badge.Kind == TabBadge.None)
                return;

            uint fill = ImGui.ColorConvertFloat4ToU32(Negative);

            if (badge.Kind == TabBadge.Dot)
            {
                dl.AddCircleFilled(new Vector2(iconTopRight.X, iconTopRight.Y + 1f),
                    BadgeDotSize * 0.5f, fill);
                return;
            }

            using (UiLabelFont.Push())
            {
                Vector2 ts = ImGui.CalcTextSize(badge.Text);

                // THE SAME SHAPE THE SIDEBAR DRAWS. This had its own arithmetic - a width from one
                // formula and a height from another - so the phone's badge came out an oval while
                // the tablet's was a circle. Same two calls now, so there is one badge in the
                // plugin and it cannot be two shapes.
                float diameter = BadgeCountSize();
                float w = BadgeCountWidth(badge.Text);

                // Clamped inside the cell: "99+" hanging off the end is a badge on the wrong tab.
                float x = MathF.Min(iconTopRight.X - 3f, cellPos.X + cellSize.X - w - 2f);
                x = MathF.Max(x, cellPos.X + 2f);

                var min = new Vector2(x, iconTopRight.Y - 2f);
                dl.AddRectFilled(min, new Vector2(min.X + w, min.Y + diameter),
                    fill, diameter * 0.5f);

                // Ink rather than OnAccent: this fill is a fixed red, not the player's accent, so
                // the text on it does not have to survive somebody choosing a pale one.
                dl.AddText(new Vector2(min.X + (w - ts.X) * 0.5f,
                                       min.Y + (diameter - ts.Y) * 0.5f),
                    ImGui.ColorConvertFloat4ToU32(Ink), badge.Text);
            }
        }
#endif

        // ══════════════════════════════════════════════════════════
        //  TABLET
        // ══════════════════════════════════════════════════════════

        /// <summary>
        /// The sidebar: who this is, where you can go, and the one ask.
        ///
        /// Drawn into its own child so the Ko-fi block can be pinned to the bottom without the
        /// body's height entering into it.
        /// </summary>
        private void DrawRail()
        {
            // THE SIDEBAR IS ROUNDED ON ITS LEFT ONLY.
            //
            // Painted by hand rather than through ChildBg, which is the fix for a real bug: a child
            // window's background is drawn with ChildRounding, which rounds all four corners. The
            // sidebar's right edge is a seam against the body, and a rounded seam left a black
            // wedge above and below it - the two halves of the window looked like they had been cut
            // apart. Its left edge is the window's own edge and has to follow the window's radius.
            Vector2 railMin = ImGui.GetCursorScreenPos();
            float railFullHeight = ImGui.GetContentRegionAvail().Y;
            ImGui.GetWindowDrawList().AddRectFilled(railMin,
                new Vector2(railMin.X + RailWidth, railMin.Y + railFullHeight),
                ImGui.ColorConvertFloat4ToU32(Panel),
                Radius.ScreenWide, ImDrawFlags.RoundCornersLeft);

            // border-r: the hairline between the sidebar and the page.
            ImGui.GetWindowDrawList().AddRectFilled(new Vector2(railMin.X + RailWidth - 1f, railMin.Y),
                new Vector2(railMin.X + RailWidth, railMin.Y + railFullHeight),
                ImGui.ColorConvertFloat4ToU32(new Vector4(1, 1, 1, 0.08f)));

            ImGui.PushStyleColor(ImGuiCol.ChildBg, new Vector4(0, 0, 0, 0));

            // Nothing in this column may scroll, and the wheel may not move it.
            //
            // This is what kept eating the tabs. The sidebar lays itself out with explicit Dummies,
            // but ImGui also adds ItemSpacing.Y after every one of them - fifteen or so items'
            // worth, none of it in the spacer arithmetic. The column therefore ran a few dozen
            // pixels past its own height, and a wheel scroll anywhere over it slid the nav rows up
            // out of sight: the tabs had not disappeared, they were above the top of the column.
            // Zero spacing makes the hand-written layout exact, and NoScrollWithMouse means even
            // if something below still overflows, the top of the sidebar stays put.
            ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, Vector2.Zero);
            ImGui.BeginChild("RailColumn", new Vector2(RailWidth, -1), false,
                ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
            try
            {
                float railHeight = ImGui.GetContentRegionAvail().Y;

                if (ChromeDiagnosticRequested) ReportChromeDiagnostic($"rail: h={railHeight:F0} availX={ImGui.GetContentRegionAvail().X:F0} "
                    + $"scrollY={ImGui.GetScrollY():F0} scrollMaxY={ImGui.GetScrollMaxY():F0}");

                // THE BRAND BLOCK IS EXACTLY AS TALL AS THE HEADER STRIP BESIDE IT.
                //
                // It used to be laid out by padding - 18 above, the mark, 16 below - which came to
                // something in the seventies while the header next to it was 58. The rule under
                // each of them is the same rule as far as the eye is concerned, and it stepped down
                // by fifteen pixels halfway across the window.
                //
                // Centred inside that height rather than padded from the top, for the same reason
                // the tab name beside it is: both are one block of text on one line.
                const float mark = 28f;
                ImGui.Dummy(new Vector2(0, (HeaderStripHeight - mark) * 0.5f));
                ImGui.Indent(Space.Gutter);
                DrawBrand();
                ImGui.Unindent(Space.Gutter);
                ImGui.Dummy(new Vector2(0, (HeaderStripHeight - mark) * 0.5f));

                // A hairline, matching the header's. They are the same line.
                //
                // The padding under it is the settings list's top margin by another name: its rows
                // start one gutter below the header strip, and the sidebar's start one gutter below
                // this rule.
                DrawRuleHair(padBelow: Space.Gutter);

#if PFP_RATINGS
                var tabs = TabList();
                if (ChromeDiagnosticRequested) ReportChromeDiagnostic($"rail nav: {tabs.Count} rows, cursorY={ImGui.GetCursorPosY():F0}");

                // The nav block's margin, which is where the settings list gets its own from too -
                // its parent inset, not anything the rows do for themselves. Indented rather than
                // baked into the row so the two controls are the same control.
                ImGui.Indent(Space.Gutter);
                if (activeTab == MainTab.Settings)
                    DrawSettingsRail();
                else
                    foreach (var (label, icon, tab) in tabs)
                        DrawRailNavItem(label, icon, tab);
                ImGui.Unindent(Space.Gutter);
#endif

                // The ask is placed from the bottom rather than pushed there by a spacer, so a
                // mistake in its height can never displace anything above it. If the column is too
                // short to hold both, the navigation keeps its place and the ask simply follows it.
                float blockHeight = KofiBlockHeight();
                float navBottom = ImGui.GetCursorPosY();
                float top = MathF.Max(navBottom + 12f, railHeight - blockHeight);

                ImGui.SetCursorPosY(top);
                DrawRuleHair(padBelow: Space.Gutter);
                ImGui.Indent(Space.Gutter);
                DrawKofiBlock(RailWidth - Space.Gutter * 2f);
                ImGui.Unindent(Space.Gutter);
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

            return 1f + Space.Gutter   // rule and its padding
                 + 34f                 // the button
                 + 8f                  // gap under it
                 + help * 2f           // "Passion project," / "support appreciated"
                 + Space.Gutter;       // breathing room at the bottom of the column
        }

        /// <summary>
        /// How wide the mark, the name and the version chip come out.
        ///
        /// Measured with the same faces that draw them, because the header beside it has to know
        /// what room is left and a constant would be a guess that goes wrong the first time a
        /// version string gets longer. Text width is font-dependent and this plugin ships its own.
        /// </summary>
        private float BrandWidth()
        {
            const float mark = 28f;

            float nameW;
            using (UiNameFont.Push())
                nameW = ImGui.CalcTextSize("PF Analysis").X;

            string version = VersionLabel.ToUpperInvariant();
            DecorateVersionLabel(ref version);

            float versionW;
            using (UiLabelFont.Push())
                versionW = ImGui.CalcTextSize(version).X;

            return mark + 11f + MathF.Max(nameW, versionW);
        }

        /// <summary>The mark, the name, and the version underneath in the label face.</summary>
        private void DrawBrand()
        {
            var dl = ImGui.GetWindowDrawList();
            Vector2 p = ImGui.GetCursorScreenPos();

            // The dashboard mockup's mark: the accent, rounded-xl, lit a little toward the top
            // (its gradient, which a rounded rect cannot take directly), and a soft shadow.
            const float mark = 28f;
            var markMax = new Vector2(p.X + mark, p.Y + mark);
            dl.AddRectFilled(p + new Vector2(0, 2f), markMax + new Vector2(0, 2f),
                ImGui.ColorConvertFloat4ToU32(new Vector4(0, 0, 0, 0.35f)), Radius.Control);
            dl.AddRectFilled(p, markMax, ImGui.ColorConvertFloat4ToU32(Darken(Accent, 0.12f)), Radius.Control);
            dl.AddRectFilled(p, new Vector2(markMax.X, p.Y + mark * 0.55f),
                ImGui.ColorConvertFloat4ToU32(Accent), Radius.Control, ImDrawFlags.RoundCornersTop);

            string glyph = LogoIcon.ToIconString();
            using (pluginInterface.UiBuilder.IconFontHandle.Push())
            {
                Vector2 gs = ImGui.CalcTextSize(glyph);
                dl.AddText(new Vector2(p.X + (mark - gs.X) * 0.5f, p.Y + (mark - gs.Y) * 0.5f),
                    ImGui.ColorConvertFloat4ToU32(new Vector4(1, 1, 1, 1)), glyph);
            }

            ImGui.SetCursorScreenPos(new Vector2(p.X + mark + 11f, p.Y - 1f));
            using (UiNameFont.Push())
                ImGui.TextColored(Ink, "PF Analysis");

            // The label is decorated rather than rebuilt: an optional component may append to it -
            // see PluginUI.AdminHooks.cs - and in an ordinary build that call is erased and this is
            // exactly the string it always was.
            string version = VersionLabel.ToUpperInvariant();
            DecorateVersionLabel(ref version);

            // In a small chip, as the mockup sets it: white/5 fill and border, grey text.
            ImGui.SetCursorScreenPos(new Vector2(p.X + mark + 15f, p.Y + 17f));
            using (UiLabelFont.Push())
            {
                Vector2 vs = ImGui.CalcTextSize(version);
                var vMin = new Vector2(p.X + mark + 11f, p.Y + 16f);
                var vMax = vMin + new Vector2(vs.X + 8f, vs.Y + 2f);
                dl.AddRectFilled(vMin, vMax, ImGui.ColorConvertFloat4ToU32(new Vector4(1, 1, 1, 0.05f)), 4f);
                dl.AddRect(vMin, vMax, ImGui.ColorConvertFloat4ToU32(new Vector4(1, 1, 1, 0.05f)), 4f, ImDrawFlags.None, 1f);
                ImGui.TextColored(FbSlate400, version);
            }

                // Erased entirely in an ordinary build - see PluginUI.AdminHooks.cs.
                if (ImGui.IsItemClicked())
                    OnVersionLabelClicked();

            ImGui.SetCursorScreenPos(new Vector2(p.X, p.Y + mark));
        }

#if PFP_RATINGS
        /// <summary>
        /// One sidebar destination. The row itself is DrawNavRow - the same control the settings
        /// page list is built from - and everything here is what a sidebar row has that a settings
        /// row does not: an unread badge, and the beta chip on Clears.
        /// </summary>
        private void DrawRailNavItem(string label, FontAwesomeIcon icon, MainTab tab)
        {
            bool active = activeTab == tab;

            // One gutter off the right, by hand. ImGui.Indent only moves the left edge - the
            // content region's right edge does not come in with it - so the indented nav block
            // would otherwise run its rows straight off the panel onto the seam.
            float width = ImGui.GetContentRegionAvail().X - Space.Gutter;

            if (ChromeDiagnosticRequested)
                ReportChromeDiagnostic($"  row {label}: w={width:F0} localY={ImGui.GetCursorPosY():F0}");

            if (DrawRailRow($"rail{tab}", icon, label, active, width, out Vector2 rowMin, LucideFor(tab)))
            {
                activeTab = tab;
                profileReturnTab = null;
            }

            var dl = ImGui.GetWindowDrawList();

            // ORDER, AND WHY IT IS THIS WAY ROUND: the word, the badge against it, and beta pushed
            // to the far edge. The badge is news and beta is a standing fact, so the badge gets the
            // position next to the thing it is about and beta gets the corner.
            float labelEnd;
            using (UiBodyFont.Push())
                labelEnd = rowMin.X + 36f + ImGui.CalcTextSize(label).X;

            var badge = TabBadgeFor(tab);
            float badgeWidth = BadgeWidth(badge);

            DrawTabBadge(dl, badge, new Vector2(labelEnd, rowMin.Y), NavRowHeight);

            if (StatusChipFor(tab) is not { } status)
                return;

            float betaX = rowMin.X + width - 8f - StatusChipWidth(status);

            if (betaX >= labelEnd + badgeWidth + 6f)
                DrawStatusChip(dl, status, new Vector2(betaX, rowMin.Y), NavRowHeight, active ? 1f : 0.7f, onAccent: active);
        }
#endif

#if PFP_RATINGS
        /// <summary>
        /// The sidebar while Settings is open: a way back to where you were, and the settings
        /// pages in place of the tabs - each on a tinted tile, the one on screen filled with the
        /// accent, as the iPadOS settings mockup lays them out.
        /// </summary>
        private void DrawSettingsRail()
        {
            float width = ImGui.GetContentRegionAvail().X - Space.Gutter;

            // Back: returns to whichever tab was open before Settings.
            if (DrawRailRow("settingsback", FontAwesomeIcon.ChevronLeft, "Back", false, width, out _, "chevron-left"))
            {
                activeTab = settingsReturnTab == MainTab.Settings ? MainTab.Presets : settingsReturnTab;
                profileReturnTab = null;
            }

            ImGui.Dummy(new Vector2(0, 6f));
            var pages = SettingsPages();
            for (int i = 0; i < pages.Count; i++)
            {
                if (DrawRailRow($"settingsnav{i}", pages[i].Icon, pages[i].Label, settingsPage == i, width, out _, pages[i].Lucide))
                    settingsPage = i;
            }
        }
#endif

        /// <summary>
        /// One sidebar row, as the dashboard mockup draws it: rounded-2xl; the page you are on
        /// filled with the accent, white and lit with a soft glow; the rest grey, going white on a
        /// faint wash when hovered.
        /// </summary>
        private bool DrawRailRow(string id, FontAwesomeIcon icon, string label, bool active,
            float width, out Vector2 rowMin, string? lucide = null)
        {
            Vector2 p = ImGui.GetCursorScreenPos();
            rowMin = p;
            var dl = ImGui.GetWindowDrawList();
            var max = new Vector2(p.X + width, p.Y + NavRowHeight);

            bool clicked = ImGui.InvisibleButton($"##nav{id}", new Vector2(width, NavRowHeight));
            bool hovered = ImGui.IsItemHovered();
            if (hovered && !active)
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);

            if (active)
            {
                for (int i = 1; i <= 3; i++) // shadow-button
                    dl.AddRectFilled(p + new Vector2(-i, 3f - i * 0.5f), max + new Vector2(i, 2f + i),
                        ImGui.ColorConvertFloat4ToU32(Accent with { W = 0.07f }), Radius.Card + i);
                dl.AddRectFilled(p, max, ImGui.ColorConvertFloat4ToU32(Accent), Radius.Card);
            }
            else if (hovered)
            {
                dl.AddRectFilled(p, max, ImGui.ColorConvertFloat4ToU32(new Vector4(1, 1, 1, 0.05f)), Radius.Card);
            }

            Vector4 colour = active || hovered ? new Vector4(1, 1, 1, 1) : FbSlate400;

            // The icons stay white whatever the row's state; only the words dim.
            const float iconSize = 17f;
            var iconColour = new Vector4(1, 1, 1, 1);
            if (lucide == null || !DrawLucide(dl, lucide, new Vector2(p.X + 11f, p.Y + (NavRowHeight - iconSize) * 0.5f), iconSize, iconColour))
            {
                using (UiIconRow.Push())
                {
                    string glyph = icon.ToIconString();
                    Vector2 gs = ImGui.CalcTextSize(glyph);
                    dl.AddText(new Vector2(p.X + 12f, p.Y + (NavRowHeight - gs.Y) * 0.5f),
                        ImGui.ColorConvertFloat4ToU32(iconColour), glyph);
                }
            }

            using ((active ? UiSegmentFont : UiBodyFont).Push())
            {
                Vector2 ts = ImGui.CalcTextSize(label);
                dl.AddText(new Vector2(p.X + 36f, p.Y + (NavRowHeight - ts.Y) * 0.5f),
                    ImGui.ColorConvertFloat4ToU32(colour), Fit(label, width - 44f));
            }

            ImGui.SetCursorScreenPos(new Vector2(p.X, p.Y + NavRowHeight + NavRowGap));
            return clicked;
        }

        /// <summary>
        /// The Ko-fi button: one red, a full capsule, the heart - and the words, when labelled -
        /// in white. One
        /// drawing for the sidebar and the phone header, so it is the same button in both.
        /// </summary>
        private void DrawKofiPill(string id, Vector2 pos, Vector2 size, bool labelled)
        {
            var dl = ImGui.GetWindowDrawList();
            ImGui.SetCursorScreenPos(pos);
            bool clicked = ImGui.InvisibleButton(id, size);
            bool hot = ImGui.IsItemHovered();
            bool held = ImGui.IsItemActive();
            if (hot)
            {
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                if (!labelled)
                    PaddedTooltip(KofiLabel);
            }

            Vector2 bMin = pos, bMax = pos + size;
            if (held)
            {
                bMin += size * 0.025f;
                bMax -= size * 0.025f;
            }

            // One colour, fully rounded - a capsule.
            float radius = (bMax.Y - bMin.Y) * 0.5f;
            dl.AddRectFilled(bMin, bMax, ImGui.ColorConvertFloat4ToU32(hot ? Lighten(KoFi, 0.08f) : KoFi), radius);

            if (labelled)
                DrawIconLabelCentered(FontAwesomeIcon.Heart, KofiLabel, bMin, bMax - bMin, new Vector4(1, 1, 1, 1),
                    1f, UiIconSmall);
            else
                DrawGlyphAtOn(dl, FontAwesomeIcon.Heart, bMin, bMax.X - bMin.X, new Vector4(1, 1, 1, 1), UiIconSmall);

            if (clicked)
                Dalamud.Utility.Util.OpenLink(KofiUrl);
        }

        /// <summary>The window's round close control, as the dashboard mockup draws it: a circle in
        /// neutral-800, the cross grey going white, the circle lifting on hover.</summary>
        private void DrawRoundCloseButton(string id, Vector2 pos, float size)
        {
            var dl = ImGui.GetWindowDrawList();
            ImGui.SetCursorScreenPos(pos);
            bool clicked = ImGui.InvisibleButton(id, new Vector2(size));
            bool hot = ImGui.IsItemHovered();
            dl.AddCircleFilled(pos + new Vector2(size * 0.5f), size * 0.5f,
                ImGui.ColorConvertFloat4ToU32((hot ? FbNeutral700 : FbNeutral800) with { W = 0.8f }), 32);
            DrawGlyphAtOn(dl, FontAwesomeIcon.Times, pos, size, hot ? new Vector4(1, 1, 1, 1) : FbSlate400, UiIconSmall);
            if (hot)
            {
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                PaddedTooltip("Close");
            }
            if (clicked)
                isMainWindowVisible = false;
        }

        /// <summary>The one ask: a solid red button with the heart and the words, and a line saying
        /// what the money is actually for.</summary>
        private void DrawKofiBlock(float width)
        {
            Vector2 p = ImGui.GetCursorScreenPos();
            var size = new Vector2(width, 34f);

            DrawKofiPill("##RailKofi", p, size, labelled: true);

            // Centred under it, two short lines - the second ending in the heart from the icon
            // font, since a literal U+2665 is not in the text face and would render as a box.
            ImGui.Dummy(new Vector2(0, 8));
            using (UiHelpFont.Push())
            {
                const string one = "Passion project,";
                const string two = "support appreciated";
                float heartW;
                using (UiIconSmall.Push())
                    heartW = ImGui.CalcTextSize(FontAwesomeIcon.Heart.ToIconString()).X;
                float oneW = ImGui.CalcTextSize(one).X;
                float twoW = ImGui.CalcTextSize(two).X + 5f + heartW;
                float x0 = ImGui.GetCursorScreenPos().X;

                ImGui.SetCursorScreenPos(new Vector2(x0 + (width - oneW) * 0.5f, ImGui.GetCursorScreenPos().Y));
                ImGui.TextColored(FbSlate500, one);
                ImGui.SetCursorScreenPos(new Vector2(x0 + (width - twoW) * 0.5f, ImGui.GetCursorScreenPos().Y));
                ImGui.TextColored(FbSlate500, two);
                ImGui.SameLine(0, 5);
                using (UiIconSmall.Push())
                    ImGui.TextColored(KoFi, FontAwesomeIcon.Heart.ToIconString());
            }
        }

        /// <summary>
        /// The body's own header: which tab you are on, and the one window control left.
        ///
        /// The tab name is repeated here rather than left to the sidebar because the sidebar is at
        /// the far edge of a 1180px window, and the thing you are looking at should be named above
        /// what you are looking at.
        /// </summary>
        private void DrawHeaderStrip()
        {
            Vector2 p = ImGui.GetCursorScreenPos();
            float width = ImGui.GetContentRegionAvail().X;
            var dl = ImGui.GetWindowDrawList();

            dl.AddRectFilled(p, new Vector2(p.X + width, p.Y + HeaderStripHeight),
                ImGui.ColorConvertFloat4ToU32(Panel),
                Radius.ScreenWide, ImDrawFlags.RoundCornersTopRight);

            // The strip names what you are looking at, so it is a title, not a field label - at
            // label size it read as a caption for the window rather than as its heading. Set in
            // tracked caps at the secondary tone: it identifies the surface without competing with
            // anything on it.
            // THE TAB'S NAME IS THE BIGGER OF THE TWO HEADINGS.
            //
            // It was the smaller one - body size for the tab, heading size for the sections inside
            // it - which is the hierarchy upside down: "YOUR RECRUITMENT" is a section of the
            // Recruit tab, and it was announcing itself more loudly than the tab it sits in. The
            // two faces are simply swapped; nothing else about either heading changed.
            // THE PAGE'S OWN NAME, and the largest thing in the chrome.
            //
            // It was heading size, which is what a section inside the page uses - so "MY PROFILE"
            // and "YOUR PROFILE" beneath it were the same weight of statement, and the strip read
            // as one more heading rather than as the title of everything under it.
            using (UiTitleFont.Push())
            {
                float lineH = ImGui.GetTextLineHeight();
                DrawTrackedCaps(dl, new Vector2(p.X + 18f, p.Y + (HeaderStripHeight - lineH) * 0.5f),
                    ActiveTabName, new Vector4(1, 1, 1, 1), tracking: 0.14f);
            }

            const float btn = 28f;
            float y = p.Y + (HeaderStripHeight - btn) * 0.5f;
            DrawRoundCloseButton("##HeaderClose", new Vector2(p.X + width - btn - 16f, y), btn);

            // THE RULE IS DRAWN, NOT LAID OUT, and the cursor is placed rather than advanced.
            //
            // DrawRuleHair emits a Dummy, and a Dummy carries ImGui's item spacing after it - which
            // the sidebar opposite does not pay, because the rail zeroes ItemSpacing for its
            // hand-written layout. That is the whole of why the first sidebar row sat 24px above
            // the first settings row: not a margin anybody chose, just one column being charged for
            // spacing the other was not.
            dl.AddRectFilled(new Vector2(p.X, p.Y + HeaderStripHeight),
                new Vector2(p.X + width, p.Y + HeaderStripHeight + 1f),
                ImGui.ColorConvertFloat4ToU32(new Vector4(1, 1, 1, 0.08f)));

            ImGui.SetCursorScreenPos(new Vector2(p.X, p.Y + HeaderStripHeight + 1f));
        }

        /// <summary>The name of the tab currently on screen, for the header strip.</summary>
        private string ActiveTabName
        {
#if PFP_RATINGS
            get => activeTab switch
            {
                MainTab.Ratings => "My Profile",
                MainTab.Achievements => "Clears",
                MainTab.Vote => "Vote",
                MainTab.PartyFinder => "Party Finder",
                MainTab.Feedback => "Feedback",
                MainTab.Settings => SettingsPages() is { Count: > 0 } sp
                    ? sp[Math.Clamp(settingsPage, 0, sp.Count - 1)].Label
                    : "Settings",
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
        /// <param name="iconFace">The face to set the glyph in. Defaults to Dalamud's shared icon
        /// handle; pass <see cref="UiIconSmall"/> where the icon sits beside body copy.</param>
        private void DrawIconLabelLeft(FontAwesomeIcon icon, string text, Vector2 rectMin,
            Vector2 rectSize, Vector4 color, float padLeft = 12f,
            Dalamud.Interface.ManagedFontAtlas.IFontHandle? iconFace = null)
        {
            var dl = ImGui.GetWindowDrawList();
            string glyph = icon.ToIconString();
            uint colU = ImGui.ColorConvertFloat4ToU32(color);
            float midY = rectMin.Y + rectSize.Y * 0.5f;

            Vector2 iconSize;
            using ((iconFace ?? pluginInterface.UiBuilder.IconFontHandle).Push())
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
