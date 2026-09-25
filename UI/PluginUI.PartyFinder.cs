#if PFP_RATINGS
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Gui.PartyFinder.Types;
using Dalamud.Interface;

namespace PfPresets
{
    /// <summary>
    /// The Party Finder tab: every recruitment listing PF Analysis users have seen on this data
    /// centre.
    ///
    /// RECRUITMENT NOTICES ONLY. What the game's own window shows anybody on the data centre -
    /// duty, leader, comment, seats - and nothing about who is in a party or what they are doing.
    ///
    /// WHAT IT ADDS OVER THE GAME'S OWN WINDOW is reach and the Join button. A listing is here for as
    /// long as its own timer runs, from any data centre, and a listing whose host runs a
    /// coordination-capable build takes applications: Join registers interest, and the host's plugin
    /// brings the party together through a private listing once every seat has an applicant. See
    /// <see cref="PfCoordination"/>.
    ///
    /// WHERE THE BOARD COMES FROM is every plugin user's Party Finder window - see
    /// <see cref="PfBoard"/>. It is as complete as what people have looked at lately, and the
    /// heading's help says so rather than letting an empty board read as an empty Party Finder.
    /// </summary>
    public partial class PluginUI
    {
        /// <summary>
        /// Regions and their data centres, in the order the game's data centre select lists them.
        ///
        /// Written out rather than read from the World sheet: the sheet also carries the Chinese
        /// and Korean services and a handful of test groups, none of which this server holds a
        /// board for. Must agree with the server's worlds.js.
        /// </summary>
        private static readonly (string Region, string[] Dcs)[] PfRegions =
        {
            ("NA", new[] { "Aether", "Crystal", "Primal", "Dynamis" }),
            ("EU", new[] { "Chaos", "Light" }),
            ("JP", new[] { "Elemental", "Gaia", "Mana", "Meteor" }),
            ("OC", new[] { "Materia" }),
        };

        /// <summary>The regions after your own, in this order: NA, JP, EU, OC.</summary>
        private static readonly string[] PfRegionPriority = { "NA", "JP", "EU", "OC" };

        /// <summary>
        /// The region tabs: the one the character is standing in first, then the rest by
        /// <see cref="PfRegionPriority"/> - an EU character sees EU, NA, JP, OC. Indexes into
        /// <see cref="PfRegions"/>, so everything else keeps addressing regions the same way.
        /// </summary>
        private int[] OrderedRegions()
        {
            string here = pfAutomation.PlayerState.CurrentWorld.ValueNullable?.DataCenter.ValueNullable?.Name.ToString() ?? string.Empty;
            int own = Array.FindIndex(PfRegions, r => r.Dcs.Contains(here, StringComparer.OrdinalIgnoreCase));
            var order = PfRegionPriority.Select(name => Array.FindIndex(PfRegions, r => r.Region == name)).Where(i => i >= 0).ToList();
            if (own >= 0)
            {
                order.Remove(own);
                order.Insert(0, own);
            }
            return order.ToArray();
        }

        /// <summary>A region's data centres with the one the character is standing on first, then
        /// the rest in the region's own order.</summary>
        private string[] OrderedDcs(int region)
        {
            var dcs = PfRegions[region].Dcs;
            string here = pfAutomation.PlayerState.CurrentWorld.ValueNullable?.DataCenter.ValueNullable?.Name.ToString() ?? string.Empty;
            int at = Array.FindIndex(dcs, d => string.Equals(d, here, StringComparison.OrdinalIgnoreCase));
            if (at <= 0)
                return dcs;

            return new[] { dcs[at] }.Concat(dcs.Where((_, i) => i != at)).ToArray();
        }

        private string pfBoardSearch = string.Empty;

        /// <summary>Set by a click inside the list, acted on after it - opening a profile switches
        /// tabs, and doing that mid-enumeration is how the search results once crashed.</summary>
        private CharacterIdentity? pfBoardClickedProfile;

        /// <summary>Cards naming their data centre. Off: the board is one data centre at a time.</summary>
        private bool pfBoardAcrossDcs;


        private void DrawPartyFinderTab()
        {
            // A private listing's password prompt, when a Join or Apply press has asked for one.
            DrawPfPasswordPrompt();

            var board = Board;
            if (board == null || !config.CommunityEnabled)
                return;

            board.EnsureFresh();

            // One centred column, like the mockup's max-w-4xl, at the plugin's scale.
            float avail = ImGui.GetContentRegionAvail().X;
            float width = Math.Max(160f, avail - FeedMargin * 2f);
            float column = MathF.Min(width, PfColumnMax);
            float inset = FeedMargin + (width - column) * 0.5f;
            width = column;

            ImGui.Indent(inset);
            ImGui.Dummy(new Vector2(0, Space.Gutter));

            // The options live in Settings > PF Radar now. What is left here is the one line that
            // can matter mid-evening: why coordination is waiting, when it is.
            if (config.PfCoordinationEnabled && Coordination?.Host == null && pfAutomation.IsRecruiting()
                && Coordination?.HostStatus is { Length: > 0 } why)
            {
                using (UiHelpFont.Push())
                {
                    ImGui.SetCursorPosX(ImGui.GetCursorPosX() + FbPad);
                    ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + width - FbPad * 2f);
                    ImGui.TextColored(FbSlate400, why);
                    ImGui.PopTextWrapPos();
                }
            }

            DrawSectionCaption("BOARD");
            ImGui.Dummy(new Vector2(0, Space.Tight));

            var response = board.Board;

            pfBoardAcrossDcs = false;
            DrawPfBoardDcPicker(board, response, width);
            DrawPfBoardSearchRow(board, width);

            ImGui.Dummy(new Vector2(0, Space.Gap));

            if (response == null)
            {
                if (board.LastError != null)
                    DrawPfBoardNotice(width, "Trying again shortly.", "Couldn't reach the server", FontAwesomeIcon.ExclamationTriangle);
                else
                    DrawPfBoardNotice(width, "Fetching the latest listings.", "Loading the board", FontAwesomeIcon.SyncAlt);
                ImGui.Unindent(inset);
                return;
            }

            var source = response.Listings;
            var shown = FilterPfBoard(source);

            // The listings the board can show at all - read, and not yet full - so "12 of 40"
            // counts the same things the list does, before a search narrows it.
            int listed = source.Count(l => l.OnBoard && (l.SlotsTotal <= 0 || l.SlotsFilled < l.SlotsTotal));
            DrawPfBoardStatusLine(board, response, shown.Count, listed, width);

            ImGui.Dummy(new Vector2(0, Space.Tight));

            if (shown.Count == 0)
            {
                DrawPfBoardNotice(width, pfBoardSearch.Length > 0
                    ? "Try adjusting your search or the data centre."
                    : "Nothing on this board yet. Open the Party Finder in game to fill it in.");
                ImGui.Unindent(inset);
                return;
            }

            pfBoardClickedProfile = null;

            // PAGES OF FIFTY, like the game's own list. The page resets whenever what is being paged
            // through changes - another data centre, another search - so it never points past the
            // end of a shorter list.
            int pageCount = Math.Max(1, (shown.Count + PfPageSize - 1) / PfPageSize);
            string pageKey = $"{response.Dc}|{pfBoardSearch}";
            if (pageKey != pfBoardPageKey)
            {
                pfBoardPageKey = pageKey;
                pfBoardPage = 0;
            }
            pfBoardPage = Math.Clamp(pfBoardPage, 0, pageCount - 1);
            var page = shown.Skip(pfBoardPage * PfPageSize).Take(PfPageSize).ToList();

            float pagerH = pageCount > 1 ? PfPagerHeight : 0f;
            // Measured against the rows, which sit beside the list's scrollbar - a page of compact
            // rows nearly always scrolls.
            if (config.PfBoardCompact)
                DrawPfCompactHeader(width - (page.Count > 8 ? ImGui.GetStyle().ScrollbarSize : 0f));

            ImGui.BeginChild("##PfBoardScroll", new Vector2(width, -Space.Gutter - pagerH), false);
            try
            {
                if (pfBoardScrollTop)
                {
                    ImGui.SetScrollY(0f);
                    pfBoardScrollTop = false;
                }

                float cardW = ImGui.GetContentRegionAvail().X;
                foreach (var listing in page)
                {
                    // Compact: one line - unless the player has opened this one to read in full.
                    string rowKey = $"{listing.LeaderName}@{listing.LeaderWorld}".ToLowerInvariant();
                    if (config.PfBoardCompact && !pfOpenedRows.Contains(rowKey))
                    {
                        DrawPfCompactRow(listing, cardW, rowKey);
                        ImGui.Dummy(new Vector2(0, PfCompactGap));
                        continue;
                    }

                    // hover:border-white/10
                    string cardKey = $"pfcard{listing.LeaderName}@{listing.LeaderWorld}";
                    Vector2 at = ImGui.GetCursorScreenPos();
                    bool hot = panelHeights.TryGetValue(cardKey, out float h)
                        && ImGui.IsWindowHovered() && ImGui.IsMouseHoveringRect(at, at + new Vector2(cardW, h));
                    MeasuredPanel(cardKey, cardW, FbCard, new Vector4(1, 1, 1, hot ? 0.1f : 0.05f),
                        PfCardRadius, PfCardPad, w => DrawPfBoardCard(listing, w));

                    // An opened compact row folds back when its card is clicked - anywhere the card's own
                    // buttons are not. Submitted after them, so they keep their clicks.
                    if (config.PfBoardCompact && panelHeights.TryGetValue(cardKey, out float openH))
                    {
                        Vector2 after = ImGui.GetCursorScreenPos();
                        ImGui.SetCursorScreenPos(at);
                        if (ImGui.InvisibleButton($"##pffold{rowKey}", new Vector2(cardW, openH)))
                            pfOpenedRows.Remove(rowKey);
                        if (ImGui.IsItemHovered())
                            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                        ImGui.SetCursorScreenPos(after);
                    }
                    ImGui.Dummy(new Vector2(0, config.PfBoardCompact ? PfCompactGap : PfCardGap));
                }
            }
            finally
            {
                ImGui.EndChild();
            }

            if (pageCount > 1)
                DrawPfBoardPager(width, pageCount, shown.Count);

            ImGui.Unindent(inset);

            if (pfBoardClickedProfile != null)
            {
                var who = pfBoardClickedProfile;
                pfBoardClickedProfile = null;
                OpenProfile(who);
            }
        }

        // ── Pages ─────────────────────────────────────────────────

        /// <summary>Listings on one page of the board - the game's own page size.</summary>
        private const int PfPageSize = 50;
        private const float PfPagerHeight = 40f;

        private int pfBoardPage;
        private string pfBoardPageKey = string.Empty;
        private bool pfBoardScrollTop;

        /// <summary>
        /// The pager under the board: round previous and next buttons either side of which
        /// listings are on screen - "51-100 of 310" - and a page number to jump straight to any of
        /// them, the current one filled with the accent.
        /// </summary>
        private void DrawPfBoardPager(float width, int pageCount, int total)
        {
            const float btn = 28f, gap = 6f;
            var dl = ImGui.GetWindowDrawList();
            Vector2 p = ImGui.GetCursorScreenPos();
            float cy = p.Y + (PfPagerHeight - btn) * 0.5f;

            int first = pfBoardPage * PfPageSize + 1;
            int last = Math.Min(total, (pfBoardPage + 1) * PfPageSize);
            string range = $"{first}-{last} of {total}";

            // The page numbers, trimmed to a window round the current one on a long board.
            int from = Math.Max(0, pfBoardPage - 3);
            int to = Math.Min(pageCount - 1, from + 6);
            from = Math.Max(0, to - 6);

            float numbersW = (to - from + 1) * (btn + gap) - gap;
            float rangeW;
            using (UiHelpFont.Push())
                rangeW = ImGui.CalcTextSize(range).X;
            float rowW = btn + gap + numbersW + gap + btn;
            float x = p.X + (width - rowW) * 0.5f;

            bool Round(string id, Vector2 at, FontAwesomeIcon? icon, string? text, bool chosen, bool enabled)
            {
                ImGui.SetCursorScreenPos(at);
                bool clicked = ImGui.InvisibleButton(id, new Vector2(btn)) && enabled && !chosen;
                bool hot = ImGui.IsItemHovered() && enabled && !chosen;
                if (hot)
                    ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                Vector4 fill = chosen ? Accent : hot ? FbNeutral700 : FbNeutral800;
                dl.AddCircleFilled(at + new Vector2(btn * 0.5f), btn * 0.5f,
                    ImGui.ColorConvertFloat4ToU32(fill with { W = enabled ? 1f : 0.4f }), 32);
                Vector4 ink = chosen ? new Vector4(1, 1, 1, 1) : enabled ? (hot ? new Vector4(1, 1, 1, 1) : PfSlate300) : FbSlate500;
                if (icon is { } g)
                    DrawGlyphAtOn(dl, g, at, btn, ink, UiIconSmall);
                else if (text != null)
                    using ((chosen ? UiSegmentFont : UiHelpFont).Push())
                    {
                        Vector2 ts = ImGui.CalcTextSize(text);
                        dl.AddText(at + (new Vector2(btn) - ts) * 0.5f, ImGui.ColorConvertFloat4ToU32(ink), text);
                    }
                return clicked;
            }

            int wanted = pfBoardPage;
            if (Round("##pfpgprev", new Vector2(x, cy), FontAwesomeIcon.ChevronLeft, null, false, pfBoardPage > 0))
                wanted = pfBoardPage - 1;
            float nx = x + btn + gap;
            for (int i = from; i <= to; i++)
            {
                if (Round($"##pfpg{i}", new Vector2(nx, cy), null, (i + 1).ToString(), i == pfBoardPage, true))
                    wanted = i;
                nx += btn + gap;
            }
            if (Round("##pfpgnext", new Vector2(nx, cy), FontAwesomeIcon.ChevronRight, null, false, pfBoardPage < pageCount - 1))
                wanted = pfBoardPage + 1;

            // Which listings these are, at the right edge, where there is room.
            using (UiHelpFont.Push())
            {
                float rx = p.X + width - rangeW - 6f;
                if (rx > nx + btn + 12f)
                    dl.AddText(new Vector2(rx, p.Y + (PfPagerHeight - ImGui.GetTextLineHeight()) * 0.5f),
                        ImGui.ColorConvertFloat4ToU32(FbSlate400), range);
            }

            if (wanted != pfBoardPage)
            {
                pfBoardPage = wanted;
                pfBoardScrollTop = true;
            }

            ImGui.SetCursorScreenPos(p);
            ImGui.Dummy(new Vector2(width, PfPagerHeight));
        }

        // ── Toolbar ───────────────────────────────────────────────

        /// <summary>The board's column at its widest: the mockup's max-w-4xl at the plugin's scale.</summary>
        private const float PfColumnMax = 720f;

        private void DrawPfBoardDcPicker(PfBoard board, PfBoardResponse? response, float width)
        {
            // Whichever board is showing: the one picked, else the one the server answered for
            // (where the character is standing).
            string shownDc = board.ChosenDc ?? response?.Dc ?? string.Empty;

            var regions = OrderedRegions();
            int region = Array.FindIndex(PfRegions,
                r => r.Dcs.Contains(shownDc, StringComparer.OrdinalIgnoreCase));
            if (region < 0)
                region = regions[0];

            // md:grid-cols-2 - the two pickers side by side on a wide board, stacked on a narrow one.
            bool sideBySide = width >= 520f;
            float each = sideBySide ? (width - 10f) * 0.5f : width;

            // The tabs in their shown order; the picker works in positions, the rest in regions.
            string[] labels = regions.Select(i => PfRegions[i].Region).ToArray();
            int shownAt = Array.IndexOf(regions, region);
            int picked = shownAt;
            if (DrawIosSegmented("pfboardregion", labels, ref picked, each) && picked != shownAt && picked >= 0)
            {
                region = regions[picked];
                board.ChooseDc(OrderedDcs(region)[0]);
                shownDc = OrderedDcs(region)[0];
            }

            if (sideBySide)
                ImGui.SameLine(0, 10f);

            var dcs = OrderedDcs(region);
            int dc = Array.FindIndex(dcs, d => string.Equals(d, shownDc, StringComparison.OrdinalIgnoreCase));

            // -1 selects nothing, which is honest for the moment before the first answer arrives.
            int dcPicked = dc;
            if (DrawIosSegmented("pfboarddc", dcs, ref dcPicked, each) && dcPicked != dc && dcPicked >= 0)
                board.ChooseDc(dcs[dcPicked]);

            ImGui.Dummy(new Vector2(0, 2f));
        }

        /// <summary>A segmented picker's width sized to its own labels - every segment as wide as the
        /// widest label plus room either side - capped at <paramref name="max"/>.</summary>
        private float IosSegmentedFitWidth(string[] options, float max)
        {
            float widest = 0f;
            using (UiSegmentFont.Push())
            {
                foreach (string o in options)
                    widest = MathF.Max(widest, ImGui.CalcTextSize(o).X);
            }
            return MathF.Min(max, options.Length * (widest + 28f) + 6f);
        }

        private void DrawPfBoardSearchRow(PfBoard board, float width)
        {
            float refresh = ButtonHeight;
            float toggleW = refresh * 2f;
            DrawSearchFieldClearable("PfBoardSearch", "Search duty, leader, comment or player",
                ref pfBoardSearch, width - refresh - toggleW - 20f, out _);

            ImGui.SameLine(0, 10f);
            DrawPfLayoutToggle(toggleW, refresh);

            // A square the search field's height: dark, the glyph blue; blue with a white glyph on
            // hover.
            ImGui.SameLine(0, 10f);
            var dl = ImGui.GetWindowDrawList();
            Vector2 min = ImGui.GetCursorScreenPos();
            var size = new Vector2(refresh, refresh);
            // ON YOUR OWN DATA CENTRE, A REFRESH IS A READ OF THE GAME. The Party Finder is read
            // there and then, every page, and the board becomes exactly what it shows - a listing
            // that has gone comes off, one that changed is updated. Anywhere else, or while the
            // character is busy, it fetches the board as it stands.
            string here = pfAutomation.PlayerState.CurrentWorld.ValueNullable?.DataCenter.ValueNullable?.Name.ToString() ?? string.Empty;
            string viewing = board.ChosenDc ?? board.Board?.Dc ?? here;
            bool ownDc = here.Length > 0 && string.Equals(here, viewing, StringComparison.OrdinalIgnoreCase);
            bool reading = BoardFetch?.Reading == true;
            bool busy = board.Loading || reading;
            string why = string.Empty;
            bool canRead = ownDc && BoardFetch != null && BoardFetch.WouldRead(here, out why);

            if (ImGui.InvisibleButton("##PfBoardRefresh", size) && !busy)
            {
                if (!(canRead && BoardFetch!.ReadNow(out _)))
                    board.Refresh();
            }
            bool hot = ImGui.IsItemHovered();
            bool held = ImGui.IsItemActive();
            if (hot)
            {
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                PaddedTooltip(reading ? "Reading the Party Finder..."
                    : board.Loading ? "Refreshing..."
                    : canRead ? "Refresh from the game: read the Party Finder now and match it exactly"
                    : ownDc && why.Length > 0 ? $"Refresh from the board ({why})"
                    : "Refresh");
            }

            Vector2 bMin = min, bMax = min + size;
            if (held)
            {
                bMin += size * 0.025f;
                bMax -= size * 0.025f;
            }
            dl.AddRectFilled(bMin, bMax, ImGui.ColorConvertFloat4ToU32(hot ? FbBlue : FbCard), Radius.Card);
            if (!hot)
                dl.AddRect(bMin, bMax, ImGui.ColorConvertFloat4ToU32(FbBorder), Radius.Card, ImDrawFlags.None, 1f);
            DrawGlyphAtOn(dl, FontAwesomeIcon.SyncAlt, bMin, bMax.X - bMin.X,
                busy ? FbSlate500 : hot ? FbWhite : FbBlue, UiIconSmall);
        }

        /// <summary>
        /// Compact or expanded: a two-segment switch, a list glyph and a card glyph, the chosen one
        /// lifted on a lighter pill. Saved, so the board opens the way it was left.
        /// </summary>
        private void DrawPfLayoutToggle(float width, float height)
        {
            var dl = ImGui.GetWindowDrawList();
            Vector2 min = ImGui.GetCursorScreenPos();
            var max = min + new Vector2(width, height);
            dl.AddRectFilled(min, max, ImGui.ColorConvertFloat4ToU32(FbCard), Radius.Card);
            dl.AddRect(min, max, ImGui.ColorConvertFloat4ToU32(new Vector4(1, 1, 1, 0.1f)), Radius.Card, ImDrawFlags.None, 1f);

            const float pad = 3f;
            float seg = (width - pad * 2f) * 0.5f;
            (FontAwesomeIcon Icon, bool Compact, string Tip)[] options =
            {
                (FontAwesomeIcon.List, true, "Compact - one line per listing"),
                (FontAwesomeIcon.ThLarge, false, "Expanded - a card per listing"),
            };

            for (int i = 0; i < options.Length; i++)
            {
                var (icon, compact, tip) = options[i];
                var sMin = new Vector2(min.X + pad + seg * i, min.Y + pad);
                var sSize = new Vector2(seg, height - pad * 2f);
                ImGui.SetCursorScreenPos(sMin);
                bool clicked = ImGui.InvisibleButton($"##pflayout{i}", sSize);
                bool hot = ImGui.IsItemHovered();
                bool chosen = config.PfBoardCompact == compact;

                if (chosen)
                    dl.AddRectFilled(sMin, sMin + sSize, ImGui.ColorConvertFloat4ToU32(new Vector4(1, 1, 1, 0.15f)), Radius.Control - 1f);
                DrawGlyphAtOn(dl, icon, sMin + new Vector2((seg - sSize.Y) * 0.5f, 0f), sSize.Y,
                    chosen || hot ? FbWhite : FbSlate400, UiIconSmall);

                if (hot)
                {
                    if (!chosen)
                        ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                    PaddedTooltip(tip);
                }
                if (clicked && !chosen)
                {
                    config.PfBoardCompact = compact;
                    config.Save();
                }
            }

            ImGui.SetCursorScreenPos(min);
            ImGui.Dummy(new Vector2(width, height));
        }

        /// <summary>The mockup's empty card: a round grey badge with a glyph, a title, a line under
        /// it - for the board's empty and loading states.</summary>
        private void DrawPfBoardNotice(float width, string text, string title = "No listings found",
            FontAwesomeIcon icon = FontAwesomeIcon.SearchMinus)
        {
            MeasuredPanel("pfnotice", width, FbCard, FbBorder, PfCardRadius, 28f, w =>
            {
                var dl = ImGui.GetWindowDrawList();
                Vector2 p = ImGui.GetCursorScreenPos();
                const float badge = 40f;
                var c = new Vector2(p.X + w * 0.5f, p.Y + badge * 0.5f);
                dl.AddCircleFilled(c, badge * 0.5f, ImGui.ColorConvertFloat4ToU32(FbNeutral800), 32);
                DrawGlyphAtOn(dl, icon, c - new Vector2(badge * 0.5f), badge, FbSlate400, UiIconRow);
                ImGui.Dummy(new Vector2(w, badge + 6f));

                using (UiTitleFont.Push())
                    FbCentredText(dl, p.X, w, title, FbWhite);
                using (UiHelpFont.Push())
                    FbCentredText(dl, p.X, w, text, FbSlate400);
            });
        }

        private void DrawPfBoardStatusLine(PfBoard board, PfBoardResponse response, int shownCount, int total,
            float columnWidth)
        {
            int age = board.FetchedAt == DateTime.MinValue
                ? 0
                : (int)(DateTime.UtcNow - board.FetchedAt).TotalSeconds;

            var dl = ImGui.GetWindowDrawList();
            // The column's own width, not what is left to the right of the cursor: the column is
            // indented to centre it, and ImGui's indent moves only the left edge - measured from the
            // content region, "updated 7s ago" ran out to the window's edge, past everything else.
            Vector2 p = ImGui.GetCursorScreenPos() + new Vector2(6f, 0f);
            float width = columnWidth - 12f;

            using (UiHelpFont.Push())
            {
                float lh = ImGui.GetTextLineHeight();

                // "<b>12</b> of <b>40</b> recruitment listings on <blue>Aether</blue>"
                var parts = new List<(string, Vector4)>
                {
                    (shownCount.ToString(), FbWhite),
                    (" of ", FbSlate400),
                    (total.ToString(), FbWhite),
                };
                if (pfBoardAcrossDcs)
                    parts.Add((" recruitment listings you can join", FbSlate400));
                else
                {
                    parts.Add((" recruitment listings on ", FbSlate400));
                    parts.Add((response.Dc, FbBlue));
                }
                float x = p.X;
                foreach (var (text, colour) in parts)
                {
                    dl.AddText(new Vector2(x, p.Y), ImGui.ColorConvertFloat4ToU32(colour), text);
                    x += ImGui.CalcTextSize(text).X;
                }

                // A green dot that breathes, then how fresh the board is.
                string when = board.Loading ? "refreshing" : $"updated {Ago(age)}";
                float ww = ImGui.CalcTextSize(when).X;
                float right = p.X + width;
                if (right - ww - 14f > x + 12f)
                {
                    dl.AddText(new Vector2(right - ww, p.Y), ImGui.ColorConvertFloat4ToU32(FbSlate400), when);
                    float pulse = 0.55f + 0.45f * MathF.Sin((float)ImGui.GetTime() * 3f);
                    dl.AddCircleFilled(new Vector2(right - ww - 8f, p.Y + lh * 0.5f), 3.5f,
                        ImGui.ColorConvertFloat4ToU32(FbGreen with { W = pulse }), 12);
                }

                ImGui.Dummy(new Vector2(width, lh));
            }

            if (!board.Uploading)
            {
                ImGui.Dummy(new Vector2(0, 2f));
                float w = columnWidth;
                MeasuredPanel("pfnotsharing", w, AccentYellow with { W = 0.12f }, AccentYellow with { W = 0.25f },
                    Radius.Card, 10f, inner =>
                {
                    using (UiHelpFont.Push())
                        ImGui.TextColored(AccentYellow,
                            "You aren't sharing what your Party Finder shows. Turn on party sharing "
                            + "under Settings > PF Radar to help fill this in.");
                });
            }
        }

        private List<PfBoardListing> FilterPfBoard(List<PfBoardListing> listings)
        {
            string q = pfBoardSearch.Trim();

            // Never a full listing: every seat taken means it is not recruiting. Across all its
            // parties, so an alliance with room in another party stays. The server leaves these
            // out too; this covers a board fetched before it did.
            //
            // ONLY LISTINGS. A party known only from its members' reports - names, but no listing
            // anybody has read - is not shown: without the recruiter's comment and seats it is not
            // a listing, just a list of people. Its names still reach the board when they match a
            // listing that has been read, and a member on a current build shares the listing itself.
            IEnumerable<PfBoardListing> result = listings.Where(l =>
                l.OnBoard && (l.SlotsTotal <= 0 || l.SlotsFilled < l.SlotsTotal));

            if (q.Length > 0)
            {
                result = result.Where(l =>
                    Contains(PfDutyName(l), q)
                    || Contains(l.LeaderName, q)
                    || Contains(l.Description, q)
                    || Contains(l.Dc, q)
                    || l.Members.Any(m => Contains(m.Name, q)));
            }

            // Four groups: the party you are in or recruiting for, then parties taking applications
            // (led by a PF Analysis user), then the other parties PF Analysis users are in (their
            // names are shown), then the rest of the board. Within each, the freshest first, the way
            // the game orders them.
            pfMyListingKey = MyListingKey();
            return result
                .OrderBy(PfBoardRank)
                .ThenByDescending(l => l.SecondsRemaining)
                .ToList();

            static bool Contains(string? s, string q)
                => s != null && s.Contains(q, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Where a listing sits: the party you are in or recruiting for first, then parties led or
        /// joined by PF Analysis users - the ones taking applications ahead of the ones merely
        /// reporting - then everything else.
        /// </summary>
        private int PfBoardRank(PfBoardListing l)
        {
            if (pfMyListingKey != null
                && new CharacterIdentity(l.LeaderName, l.LeaderWorld).Key == pfMyListingKey)
                return 0;
            // Duties this character has not unlocked last of all - it cannot join them - and below
            // those, nothing; then private listings, whoever runs them: most people cannot get in.
            if (ListingLockedHere(l))
                return 10;
            if (IsPrivateListing(l))
                return 9;
            if (!string.IsNullOrWhiteSpace(l.CoordinationId))
                return 1;
            if (l.Members.Count > 0 || !l.OnBoard)
                return 2;
            return 3;
        }

        /// <summary>The leader of the listing you are in or recruiting for, as a character key, or
        /// null. Worked out once per sort rather than per listing.</summary>
        private string? pfMyListingKey;

        private string? MyListingKey()
        {
            var snap = pfAutomation.GetSnapshot(ImGui.GetFrameCount());
            if (!snap.IsRecruiting)
                return null;
            if (snap.IsLeader)
                return LocalIdentity?.Invoke() is { IsValid: true } me ? me.Key : null;
            string world = snap.LeaderWorldId != 0 ? Worlds?.GetWorldName(snap.LeaderWorldId) ?? string.Empty : string.Empty;
            return snap.LeaderName.Length > 0 && world.Length > 0 ? new CharacterIdentity(snap.LeaderName, world).Key : null;
        }

        // ── Compact rows ──────────────────────────────────────────

        private const float PfCompactRowH = 46f;
        private const float PfCompactGap = 6f;
        private const float PfCompactIcon = 30f;
        // The game's own compact seats: small upright bars in role colours, not job icons.
        private const float PfCompactSeat = 7f;
        private const float PfCompactSeatH = 16f;
        private const float PfCompactSeatGap = 2f;
        private const float PfCompactRecruiterW = 130f;
        private const float PfCompactRightW = 130f;

        /// <summary>Compact rows the player has opened into the full card, by leader.</summary>
        private readonly HashSet<string> pfOpenedRows = new();

        /// <summary>Room the seats take: eight of them, however many the listing shows.</summary>
        private static float PfCompactSeatsW => 8f * PfCompactSeat + 7f * PfCompactSeatGap;

        /// <summary>Whether the row is wide enough for a column of its own for the recruiter.</summary>
        private static bool PfCompactShowsRecruiter(float width)
            => width - PfCompactIcon - PfCompactSeatsW - PfCompactRightW - PfCompactRecruiterW - 60f >= 150f;

        /// <summary>The column names over the compact list, the mockup's small caps.</summary>
        private void DrawPfCompactHeader(float width)
        {
            var dl = ImGui.GetWindowDrawList();
            Vector2 p = ImGui.GetCursorScreenPos();
            const float padX = 12f;
            bool recruiter = PfCompactShowsRecruiter(width);
            float right = p.X + width - padX;
            float seatsX = right - PfCompactRightW - 12f - (recruiter ? PfCompactRecruiterW + 12f : 0f) - PfCompactSeatsW;

            using (UiLabelFont.Push())
            {
                float h = ImGui.GetTextLineHeight();
                DrawTrackedCaps(dl, new Vector2(p.X + padX, p.Y), "Duty", FbSlate400);
                DrawTrackedCaps(dl, new Vector2(seatsX, p.Y), "Party", FbSlate400);
                if (recruiter)
                    DrawTrackedCaps(dl, new Vector2(seatsX + PfCompactSeatsW + 12f, p.Y), "Recruiter", FbSlate400);
                ImGui.Dummy(new Vector2(width, h + 4f));
            }
        }

        /// <summary>
        /// One listing on one line: the category image, the duty with its tag and comment under
        /// it, the seats as bare icons, the recruiter, and on the right the time left, watch and
        /// Join. Clicking anywhere else on the row opens it as the full card.
        /// </summary>
        private void DrawPfCompactRow(PfBoardListing listing, float width, string rowKey)
        {
            const float padX = 12f;
            var dl = ImGui.GetWindowDrawList();
            Vector2 min = ImGui.GetCursorScreenPos();
            var max = min + new Vector2(width, PfCompactRowH);
            float cy = min.Y + PfCompactRowH * 0.5f;
            string key = $"{listing.LeaderName}@{listing.LeaderWorld}";
            bool recruiterCol = PfCompactShowsRecruiter(width);

            bool rowHot = ImGui.IsWindowHovered() && ImGui.IsMouseHoveringRect(min, max);
            dl.AddRectFilled(min, max, ImGui.ColorConvertFloat4ToU32(rowHot ? ColorFromHex("#232327") : Field), Radius.Card);
            dl.AddRect(min, max, ImGui.ColorConvertFloat4ToU32(new Vector4(1, 1, 1, rowHot ? 0.1f : 0.05f)), Radius.Card, ImDrawFlags.None, 1f);

            // ── Right, left to right: Join, time left, watch (hit-tested before the row) ──
            // Laid out from the right edge inward: watch, then the time, then Join.
            float right = max.X - padX;

            if (Board != null && listing.OnBoard)
            {
                bool watching = Board.IsWatching(listing);
                const float eye = 24f;
                var eMin = new Vector2(right - eye, cy - eye * 0.5f);
                ImGui.SetCursorScreenPos(eMin);
                if (ImGui.InvisibleButton($"##pfcwatch{key}", new Vector2(eye)))
                {
                    if (watching)
                        Board.Unwatch(key.ToLowerInvariant());
                    else
                        Board.Watch(listing, PfDutyName(listing));
                }
                bool eHot = ImGui.IsItemHovered();
                if (eHot)
                {
                    ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                    PaddedTooltip(watching ? "Stop watching" : "Watch this listing in its own window");
                }
                DrawGlyphAtOn(dl, watching ? FontAwesomeIcon.EyeSlash : FontAwesomeIcon.Eye, eMin, eye,
                    watching ? Accent : eHot ? FbWhite : FbSlate400, UiIconSmall);
                right -= eye + 6f;
            }

            if (listing.OnBoard && listing.SecondsRemaining > 0)
            {
                using (UiHelpFont.Push())
                {
                    string t = PfTimeLeft(listing.SecondsRemaining).Replace(" left", string.Empty);
                    Vector2 ts = ImGui.CalcTextSize(t);
                    dl.AddText(new Vector2(right - ts.X, cy - ts.Y * 0.5f), ImGui.ColorConvertFloat4ToU32(
                        listing.SecondsRemaining < 600 ? AccentYellow : FbSlate400), t);
                    right -= ts.X + 10f;
                }
            }

            // Join (now, this data centre) and Apply (a plugin user's listing, from anywhere), each a
            // small pill: Join in the accent, Apply in glass. Laid out right to left, so Apply sits
            // outside Join and the pair reads "Join  Apply" left to right.
            if (!string.IsNullOrWhiteSpace(listing.CoordinationId))
            {
                var coordination = Coordination;
                var (enabled, label, tooltip) = coordination?.JoinState(listing)
                    ?? (false, "Apply", "This listing is not taking applications.");
                if (DrawPfCompactPill(dl, $"##pfcapply{listing.Id}{listing.CoordinationId}", label, ref right, cy,
                        enabled, primary: false, tooltip))
                    RequestPfApply(listing);
            }

            var direct = DirectJoin?.State(listing);
            if (direct is { Shown: true } d)
            {
                if (DrawPfCompactPill(dl, $"##pfcjoin{listing.Id}", d.Label, ref right, cy, d.Enabled, primary: true, d.Tooltip))
                    RequestPfJoin(listing, -1);
            }

            float rightColumnLeft = max.X - padX - PfCompactRightW;

            // ── Recruiter ──
            float seatsRight = rightColumnLeft - 12f;
            if (recruiterCol)
            {
                float rx = rightColumnLeft - 12f - PfCompactRecruiterW;
                using (UiHelpFont.Push())
                {
                    float lh = ImGui.GetTextLineHeight();
                    string name = Fit(DisplayName(listing.LeaderName), PfCompactRecruiterW);
                    Vector2 ns = ImGui.CalcTextSize(name);
                    ImGui.SetCursorScreenPos(new Vector2(rx, cy - lh));
                    if (ImGui.InvisibleButton($"##pfcleader{key}", new Vector2(MathF.Max(1f, ns.X), lh)))
                        pfBoardClickedProfile = new CharacterIdentity(listing.LeaderName, listing.LeaderWorld);
                    bool nHot = ImGui.IsItemHovered();
                    if (nHot)
                    {
                        ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                        PaddedTooltip("Open their profile");
                    }
                    dl.AddText(new Vector2(rx, cy - lh), ImGui.ColorConvertFloat4ToU32(nHot ? FbWhite : PfSlate200), name);
                    dl.AddText(new Vector2(rx, cy), ImGui.ColorConvertFloat4ToU32(FbSlate500),
                        Fit($"@{listing.LeaderWorld}", PfCompactRecruiterW));
                }
                seatsRight = rx - 12f;
            }

            // ── Seats: bare icons ──
            // An alliance's lettered badges need more room than eight seat bars once there are more
            // than three parties - the Forked Tower has six - so its column is as wide as they are,
            // and the duty text gives the room up rather than the recruiter being drawn over.
            float seatsX = seatsRight - (listing.Parties > 1 ? AllianceBadgesWidth(listing) : PfCompactSeatsW);
            DrawPfCompactSeats(dl, listing, new Vector2(seatsX, cy - PfCompactSeatH * 0.5f));

            // ── Duty, and its tag and comment under it ──
            var iconMin = new Vector2(min.X + padX, cy - PfCompactIcon * 0.5f);
            string duty = PfDutyName(listing);
            int categoryId = listing.Category > 0
                ? System.Numerics.BitOperations.TrailingZeroCount((uint)listing.Category)
                : dutyDataHelper.GetCategoryIdForDuty((uint)Math.Max(0, listing.DutyId), duty);
            uint categoryIcon = GetCategoryIcon(categoryId);
            if (categoryIcon != 0 && TryGetIconHandle(categoryIcon, out var tileIcon))
                dl.AddImageRounded(tileIcon, iconMin, iconMin + new Vector2(PfCompactIcon), Vector2.Zero, Vector2.One,
                    ImGui.ColorConvertFloat4ToU32(FbWhite), Radius.Small);

            float tx = iconMin.X + PfCompactIcon + 10f;
            float textRoom = seatsX - 12f - tx;
            // The duty, and the listing's tag right beside it - PRACTICE, LOOT - so the line under
            // it is the comment's alone.
            var chips = PfListingChips(listing);
            Vector2 tagSize = chips.Count > 0 ? PfTagSize(chips[0].Text) : Vector2.Zero;
            using (UiBodyFont.Push())
            {
                float lh = ImGui.GetTextLineHeight();
                bool tagFits = chips.Count > 0 && tagSize.X < textRoom * 0.45f;
                float lockW = IsPrivateListing(listing) ? DrawPfLock(dl, new Vector2(tx, cy - lh), lh) : 0f;
                if (listing.Beginners)
                    lockW += DrawPfSprout(dl, new Vector2(tx + lockW, cy - lh), lh);
                float nameRoom = textRoom - lockW - (tagFits ? tagSize.X + 8f : 0f);
                string shown = Fit(duty, nameRoom);
                dl.AddText(new Vector2(tx + lockW, cy - lh), ImGui.ColorConvertFloat4ToU32(rowHot ? Accent : FbWhite), shown);
                if (tagFits)
                {
                    float nameW = lockW + ImGui.CalcTextSize(shown).X;
                    DrawPfTag(dl, new Vector2(tx + nameW + 8f, cy - lh + (lh - tagSize.Y) * 0.5f), chips[0].Text, chips[0].Colour);
                }
            }

            // The leader in the plugin's face; the comment in the comment face, which is the one
            // that has the game's own symbols - see CommentFont. Set in ours they came out as "=".
            // What the last Join press found takes the second line while it is fresh: the reason
            // it could not join, right where the eye already is.
            if (DirectJoin?.Result(listing) is { } joinResult)
            {
                using (UiHelpFont.Push())
                    dl.AddText(new Vector2(tx, cy + 2f), ImGui.ColorConvertFloat4ToU32(joinResult.Good ? Positive : Negative),
                        Fit(joinResult.Message, textRoom));
            }
            float descX = DirectJoin?.Result(listing) != null ? tx + textRoom : tx;
            if (!recruiterCol && descX < tx + textRoom)
            {
                using (UiHelpFont.Push())
                {
                    string who = Fit($"{DisplayName(listing.LeaderName)} @ {listing.LeaderWorld}"
                        + (listing.Description.Length > 0 ? "  ·  " : string.Empty), textRoom);
                    dl.AddText(new Vector2(tx, cy + 2f), ImGui.ColorConvertFloat4ToU32(FbSlate400), who);
                    descX += ImGui.CalcTextSize(who).X;
                }
            }
            if (listing.Description.Length > 0 && descX < tx + textRoom - 8f)
            {
                using (CommentFont.Push())
                {
                    string desc = Fit(listing.Description.Replace('\n', ' '), tx + textRoom - descX);
                    DrawCommentLine(dl, new Vector2(descX, cy + 1f), desc, FbSlate400);
                }
            }

            // ── The row: anything else opens it as the full card ──
            ImGui.SetCursorScreenPos(min);
            if (ImGui.InvisibleButton($"##pfcrow{key}", new Vector2(width, PfCompactRowH)))
                pfOpenedRows.Add(rowKey);
            if (ImGui.IsItemHovered())
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        }

        /// <summary>
        /// The seats as the game's Party Finder list shows them: a small upright bar each, in the
        /// role colour of the job in it. An open seat is striped with the colours of the roles it
        /// takes, faded; an omitted one is an empty outline; one held for an applicant is their
        /// role with the accent dot under it. Hover names each. An alliance shows its first eight.
        /// </summary>
        private void DrawPfCompactSeats(ImDrawListPtr dl, PfBoardListing listing, Vector2 at)
        {
            if (listing.Parties > 1)
            {
                DrawPfCompactAlliance(dl, listing, at);
                return;
            }

            int seats = PfSeatCount(listing);
            int count = Math.Min(8, seats + listing.Applicants.Count);
            var size = new Vector2(PfCompactSeat, PfCompactSeatH);
            var order = PfSeatOrder(listing.Slots, 0, seats);

            Vector4 RoleOfJob(uint job) => JobData.FindById(job) is { } j ? GetRoleColor(RoleOf(j)) : FbSlate500;

            for (int i = 0; i < count; i++)
            {
                var min = at + new Vector2(i * (PfCompactSeat + PfCompactSeatGap), 0f);
                var max = min + size;
                string tip;

                if (i >= seats)
                {
                    var applicant = listing.Applicants[i - seats];
                    dl.AddRectFilled(min, max, ImGui.ColorConvertFloat4ToU32(RoleOfJob((uint)Math.Max(0, applicant.Job))), 2f);
                    PfHeldDot(dl, min, max);
                    tip = $"Held for {DisplayName(applicant.Name)} @ {applicant.World}";
                }
                else if (listing.Slots[order[i]].Job > 0)
                {
                    uint job = (uint)listing.Slots[order[i]].Job;
                    dl.AddRectFilled(min, max, ImGui.ColorConvertFloat4ToU32(RoleOfJob(job)), 2f);
                    tip = JobData.FindById(job)?.Name ?? "In the party";
                }
                else
                {
                    var slot = listing.Slots[order[i]];
                    bool omitted = IsOmittedMask(slot.Accepting);
                    var accepted = Board?.JobsAccepted(slot.Accepting).ToList() ?? new List<uint>();
                    if (omitted)
                    {
                        dl.AddRect(min, max, ImGui.ColorConvertFloat4ToU32(FbNeutral700), 2f, ImDrawFlags.None, 1f);
                        tip = OmittedSeatTip;
                    }
                    else
                    {
                        // One band per role the seat takes, top to bottom: tank, healer, DPS.
                        var roles = accepted.Select(j => JobData.FindById(j)).Where(j => j != null)
                            .Select(j => RoleOf(j!)).Select(r => r switch
                            {
                                RoleType.Tank => 0,
                                RoleType.Healer => 1,
                                _ => 2,
                            }).Distinct().OrderBy(r => r).ToList();
                        if (roles.Count == 0)
                            roles = new List<int> { 0, 1, 2 };
                        float band = PfCompactSeatH / roles.Count;
                        for (int r = 0; r < roles.Count; r++)
                        {
                            Vector4 c = roles[r] switch { 0 => RoleTank, 1 => RoleHealer, _ => RoleDPS };
                            var bMin = new Vector2(min.X, min.Y + band * r);
                            var bMax = new Vector2(max.X, min.Y + band * (r + 1));
                            ImDrawFlags corners = roles.Count == 1 ? ImDrawFlags.RoundCornersAll
                                : r == 0 ? ImDrawFlags.RoundCornersTop
                                : r == roles.Count - 1 ? ImDrawFlags.RoundCornersBottom
                                : ImDrawFlags.RoundCornersNone;
                            dl.AddRectFilled(bMin, bMax, ImGui.ColorConvertFloat4ToU32(c with { W = 0.35f }), 2f, corners);
                        }
                        tip = PfJobList(accepted);
                    }
                }

                if (ImGui.IsWindowHovered() && ImGui.IsMouseHoveringRect(min - new Vector2(1f), max + new Vector2(1f)))
                    PaddedTooltip(tip);
            }

            int more = seats + listing.Applicants.Count - count;
            if (more > 0)
                using (UiHelpFont.Push())
                    dl.AddText(at + new Vector2(count * (PfCompactSeat + PfCompactSeatGap) + 3f, 1f),
                        ImGui.ColorConvertFloat4ToU32(FbSlate500), $"+{more}");
        }

        // ── Card ──────────────────────────────────────────────────

        // The mockup's card at the plugin's scale: rounded-3xl, p-5, space-y-3.5.
        private const float PfCardRadius = 16f;
        private const float PfCardPad = 14f;
        private const float PfCardGap = 12f;
        private const float PfCardSpace = 10f;
        private const float PfTile = 44f;
        // The icons fill their seat now there is no tile round them: bigger, and closer together.
        private const float PfSeat = 30f;
        private const float PfSeatGap = 2f;
        private const float PfSeatRowGap = 6f;

        private static readonly Vector4 PfSlate200 = ColorFromHex("#e2e8f0");
        private static readonly Vector4 PfRowBg = new(0.173f, 0.173f, 0.18f, 0.6f);   // #2C2C2E/60

        /// <summary>
        /// One listing, laid out after the iPadOS mockup: the category image, the duty in bold, the
        /// leader, loot rule and item level under it, and the listing's tags; the time left and the
        /// watch button at the right. Then the party (or the leader alone) on a soft row, the comment,
        /// the seats as rounded tiles under their count, and Join across the bottom. The images are
        /// the game's own - the category, the jobs, the Party Finder's seat tiles.
        /// </summary>
        private void DrawPfBoardCard(PfBoardListing listing, float width)
        {
            var dl = ImGui.GetWindowDrawList();
            string key = $"{listing.LeaderName}@{listing.LeaderWorld}";

            Vector2 origin = ImGui.GetCursorScreenPos();
            float textLeft = origin.X + PfTile + 12f;

            // ── The category image ────────────────────────────────
            // With the duty's name as well as its id: the newest fights (Dancing Mad among them) are
            // not in the duty sheet by id, and without the name a party reported only by its
            // leader's plugin - which carries no category of its own - came out uncategorised and
            // drew the duty's first letter instead of the High-end Duty tile.
            string duty = PfDutyName(listing);
            int categoryId = listing.Category > 0
                ? System.Numerics.BitOperations.TrailingZeroCount((uint)listing.Category)
                : dutyDataHelper.GetCategoryIdForDuty((uint)Math.Max(0, listing.DutyId), duty);
            uint categoryIcon = GetCategoryIcon(categoryId);
            var tileMax = origin + new Vector2(PfTile, PfTile);
            if (categoryIcon != 0 && TryGetIconHandle(categoryIcon, out var tileIcon))
            {
                dl.AddImageRounded(tileIcon, origin, tileMax, Vector2.Zero, Vector2.One,
                    ImGui.ColorConvertFloat4ToU32(FbWhite), Radius.Card);
            }
            else
            {
                dl.AddRectFilled(origin, tileMax, ImGui.ColorConvertFloat4ToU32(OverlayInsetBg), Radius.Card);
                string initial = duty.Length > 0 ? duty[..1].ToUpperInvariant() : "?";
                using (UiNameFont.Push())
                {
                    Vector2 ts = ImGui.CalcTextSize(initial);
                    dl.AddText(origin + (new Vector2(PfTile) - ts) * 0.5f, ImGui.ColorConvertFloat4ToU32(Dim), initial);
                }
            }

            // ── Right: the time left, and watch ───────────────────
            float right = origin.X + width;
            const float eye = 28f;
            if (Board != null && listing.OnBoard)
            {
                bool watching = Board.IsWatching(listing);
                var eyeMin = new Vector2(right - eye, origin.Y);
                var back = ImGui.GetCursorScreenPos();
                ImGui.SetCursorScreenPos(eyeMin);
                if (ImGui.InvisibleButton($"##pfwatch{key}", new Vector2(eye)))
                {
                    if (watching)
                        Board.Unwatch(key.ToLowerInvariant());
                    else
                        Board.Watch(listing, duty);
                }
                bool eyeHot = ImGui.IsItemHovered();
                if (eyeHot)
                {
                    ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                    PaddedTooltip(watching ? "Stop watching" : "Watch this listing in its own window");
                }
                ImGui.SetCursorScreenPos(back);

                // rounded-xl bg-neutral-800/80, the glyph grey going white on hover (blue while watching)
                dl.AddRectFilled(eyeMin, eyeMin + new Vector2(eye), ImGui.ColorConvertFloat4ToU32(
                    (eyeHot ? FbNeutral700 : FbNeutral800) with { W = 0.8f }), Radius.Control);
                DrawGlyphAtOn(dl, watching ? FontAwesomeIcon.EyeSlash : FontAwesomeIcon.Eye, eyeMin, eye,
                    watching ? FbBlue : eyeHot ? FbWhite : FbSlate400, UiIconSmall);
                right -= eye + 8f;

                // Send the recruiter a tell, from anywhere - a tell reaches any world.
                var tellMin = new Vector2(right - eye, origin.Y);
                ImGui.SetCursorScreenPos(tellMin);
                if (ImGui.InvisibleButton($"##pftell{key}", new Vector2(eye)))
                    OpenTell(listing.LeaderName, listing.LeaderWorld);
                bool tellHot = ImGui.IsItemHovered();
                if (tellHot)
                {
                    ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                    PaddedTooltip($"Send {DisplayName(listing.LeaderName)} a tell");
                }
                ImGui.SetCursorScreenPos(back);
                dl.AddRectFilled(tellMin, tellMin + new Vector2(eye), ImGui.ColorConvertFloat4ToU32(
                    (tellHot ? FbNeutral700 : FbNeutral800) with { W = 0.8f }), Radius.Control);
                DrawGlyphAtOn(dl, FontAwesomeIcon.CommentDots, tellMin, eye, tellHot ? FbWhite : FbSlate400, UiIconSmall);
                right -= eye + 8f;
            }

            if (listing.OnBoard && listing.SecondsRemaining > 0)
            {
                string timeLeft = PfTimeLeft(listing.SecondsRemaining);
                Vector4 tint = listing.SecondsRemaining < 600 ? AccentYellow : FbBlue;
                Vector2 ps = PillSize(timeLeft);
                var pMin = new Vector2(right - ps.X, origin.Y + (eye - ps.Y) * 0.5f);
                DrawPillAt(dl, pMin, timeLeft, tint);
                dl.AddRect(pMin, pMin + ps, ImGui.ColorConvertFloat4ToU32(tint with { W = 0.2f }), ps.Y * 0.5f,
                    ImDrawFlags.None, 1f);
                right -= ps.X + 8f;
            }

            // ── The duty, bold ────────────────────────────────────
            float y = origin.Y;
            using (UiNameFont.Push())
            {
                float lockW = IsPrivateListing(listing) ? DrawPfLock(dl, new Vector2(textLeft, y), ImGui.GetTextLineHeight()) : 0f;
                if (listing.Beginners)
                    lockW += DrawPfSprout(dl, new Vector2(textLeft + lockW, y), ImGui.GetTextLineHeight());
                dl.AddText(new Vector2(textLeft + lockW, y), ImGui.ColorConvertFloat4ToU32(FbWhite), Fit(duty, right - textLeft - lockW));
                y += ImGui.GetTextLineHeight() + 1f;
            }

            // ── Leader @ World • Loot • i790 ──────────────────────
            float textRoom = origin.X + width - textLeft;
            using (UiHelpFont.Push())
            {
                float lh = ImGui.GetTextLineHeight();
                string leader = DisplayName(listing.LeaderName);
                Vector2 ls = ImGui.CalcTextSize(leader);

                // The leader's name opens their profile.
                ImGui.SetCursorScreenPos(new Vector2(textLeft, y));
                if (ImGui.InvisibleButton($"##pfleader{key}", new Vector2(MathF.Max(1f, ls.X), lh)))
                    pfBoardClickedProfile = new CharacterIdentity(listing.LeaderName, listing.LeaderWorld);
                bool leaderHot = ImGui.IsItemHovered();
                if (leaderHot)
                {
                    ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                    PaddedTooltip("Open their profile");
                }

                var parts = new List<(string, Vector4)> { (leader, leaderHot ? FbWhite : PfSlate200), ($" @ {listing.LeaderWorld}", FbSlate400) };
                if (listing.OnBoard)
                {
                    parts.Add(($"  •  {DisplayNames.GetLootRuleName(Math.Clamp(listing.LootRules, 0, 2))}", FbSlate400));
                    if (listing.MinIlvl > 0)
                    {
                        parts.Add(("  •  ", FbSlate400));
                        parts.Add(($"i{listing.MinIlvl}", FbBlue));
                    }
                }
                float x = textLeft;
                foreach (var (text, colour) in parts)
                {
                    string shown = Fit(text, textLeft + textRoom - x);
                    dl.AddText(new Vector2(x, y), ImGui.ColorConvertFloat4ToU32(colour), shown);
                    x += ImGui.CalcTextSize(shown).X;
                    if (shown != text)
                        break;
                }
                y += lh;
            }

            // ── Tags: what the party is for ───────────────────────
            var chips = PfListingChips(listing);
            if (chips.Count > 0)
            {
                float tx = textLeft, ty = y + 6f, tagH = 0f;
                foreach (var (text, colour) in chips)
                {
                    Vector2 ts = PfTagSize(text);
                    if (tx > textLeft && tx + ts.X > textLeft + textRoom)
                    {
                        tx = textLeft;
                        ty += ts.Y + 4f;
                    }
                    DrawPfTag(dl, new Vector2(tx, ty), text, colour);
                    tx += ts.X + 6f;
                    tagH = ts.Y;
                }
                y = ty + tagH;
            }

            float headerBottom = Math.Max(origin.Y + PfTile, y);
            ImGui.SetCursorScreenPos(new Vector2(origin.X, headerBottom));
            ImGui.Dummy(new Vector2(width, 0));

            // ── The party, or the leader alone, on a soft row ─────
            ImGui.Dummy(new Vector2(0, PfCardSpace - ImGui.GetStyle().ItemSpacing.Y * 2f));
            if (listing.Members.Count > 0 || listing.Applicants.Count > 0)
            {
                MeasuredPanel($"pfmembers{key}", width, PfRowBg, FbBorder, Radius.Control, 8f,
                    w => DrawPfMembers(listing, w));
            }
            else
            {
                DrawPfLeaderRow(listing, width, key);
            }

            // ── The comment ───────────────────────────────────────
            if (listing.Description.Length > 0)
            {
                ImGui.Dummy(new Vector2(0, PfCardSpace - ImGui.GetStyle().ItemSpacing.Y * 2f));
                // In the comment face and through the comment helpers, like every other listing
                // comment in the plugin: the game's symbols and its tinted auto-translate brackets.
                ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 3f);
                using (CommentFont.Push())
                    DrawCommentLines(WrapCommentToLines(listing.Description, width - 6f, 12), PfSlate300, width - 6f);
            }

            // ── The seats, under their count ──────────────────────
            if (listing.Slots.Count > 0 || listing.Applicants.Count > 0)
            {
                ImGui.Dummy(new Vector2(0, PfCardSpace - ImGui.GetStyle().ItemSpacing.Y * 2f));
                DrawPfSlots(listing, width);
            }

            DrawPfCardActions(listing, width);
        }

        /// <summary>"49m left", or "1h 10m left" past the hour.</summary>
        private static string PfTimeLeft(int seconds)
        {
            int minutes = Math.Max(1, seconds / 60);
            return minutes >= 60 ? $"{minutes / 60}h {minutes % 60}m left" : $"{minutes}m left";
        }

        /// <summary>The leader alone, for a listing nobody in it reports: a shield, their name,
        /// their world - the mockup's leader row. Opens their profile.</summary>
        private void DrawPfLeaderRow(PfBoardListing listing, float width, string key)
        {
            const float rowH = 26f;
            var dl = ImGui.GetWindowDrawList();
            Vector2 min = ImGui.GetCursorScreenPos();
            var max = min + new Vector2(width, rowH);

            if (ImGui.InvisibleButton($"##pfleaderrow{key}", new Vector2(width, rowH)))
                pfBoardClickedProfile = new CharacterIdentity(listing.LeaderName, listing.LeaderWorld);
            bool hot = ImGui.IsItemHovered();
            if (hot)
            {
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                PaddedTooltip("Open their profile");
            }

            dl.AddRectFilled(min, max, ImGui.ColorConvertFloat4ToU32(hot ? OverlayInsetBg : PfRowBg), Radius.Control);
            dl.AddRect(min, max, ImGui.ColorConvertFloat4ToU32(FbBorder), Radius.Control, ImDrawFlags.None, 1f);

            float cy = min.Y + rowH * 0.5f;

            // The leader's own job, from the first seat - the leader always sits in it. Nothing at
            // all when the listing does not say, rather than a stand-in glyph.
            float x = min.X + 10f;
            uint leaderJob = listing.Slots.Count > 0 && listing.Slots[0].Job > 0 ? (uint)listing.Slots[0].Job : 0u;
            if (leaderJob > 0 && TryGetIconHandle(IconJobBase + leaderJob, out var jobIcon))
            {
                const float jobSize = 18f;
                dl.AddImage(jobIcon, new Vector2(x, cy - jobSize * 0.5f), new Vector2(x + jobSize, cy + jobSize * 0.5f));
                x += jobSize + 7f;
            }

            using (UiHelpFont.Push())
            {
                float lh = ImGui.GetTextLineHeight();
                string name = DisplayName(listing.LeaderName);
                dl.AddText(new Vector2(x, cy - lh * 0.5f), ImGui.ColorConvertFloat4ToU32(FbWhite), name);
                x += ImGui.CalcTextSize(name).X + 4f;
                dl.AddText(new Vector2(x, cy - lh * 0.5f), ImGui.ColorConvertFloat4ToU32(FbSlate500), $"@ {listing.LeaderWorld}");
            }
        }

        /// <summary>The mockup's tag: uppercase, a tinted fill and border, rounded-md.</summary>
        private Vector2 PfTagSize(string text)
        {
            using (UiHelpFont.Push())
            {
                Vector2 ts = ImGui.CalcTextSize(text.ToUpperInvariant());
                return new Vector2(ts.X + 12f, ts.Y + 3f);
            }
        }

        private void DrawPfTag(ImDrawListPtr dl, Vector2 min, string text, Vector4 tint)
        {
            Vector2 size = PfTagSize(text);
            dl.AddRectFilled(min, min + size, ImGui.ColorConvertFloat4ToU32(tint with { W = 0.15f }), Radius.Chip);
            dl.AddRect(min, min + size, ImGui.ColorConvertFloat4ToU32(tint with { W = 0.3f }), Radius.Chip, ImDrawFlags.None, 1f);
            using (UiHelpFont.Push())
                dl.AddText(min + new Vector2(6f, 1.5f), ImGui.ColorConvertFloat4ToU32(tint), text.ToUpperInvariant());
        }

        /// <summary>The chips a listing earns, in the preset card's order and colour: the objective,
        /// the completion status the leader asked for, and beginners welcome - all tinted with the
        /// objective's colour, so a loot run and a practice party read apart at a glance.</summary>
        private List<(string Text, Vector4 Colour)> PfListingChips(PfBoardListing listing)
        {
            var chips = new List<(string, Vector4)>(4);
            if (!listing.OnBoard)
                return chips;

            // The game's flags (None 1, Completion 2, Practice 4, Loot 8) as the preset's ids.
            int objective = listing.Objective switch
            {
                2 => 1,
                4 => 2,
                8 => 3,
                _ => 0,
            };
            Vector4 colour = ObjectiveColour(objective);

            if (objective != 0)
                chips.Add((DisplayNames.GetObjectiveName(objective), colour));

            // Completion status: Complete 2, Incomplete 4, Complete (weekly unclaimed) 8.
            int completion = listing.Conditions switch
            {
                2 => 0,
                8 => 1,
                4 => 2,
                _ => -1,
            };
            if (completion >= 0)
                chips.Add((DisplayNames.GetCompletionStatusName(completion), colour));

            // Beginners welcome is the game's sprout before the duty's name, not a chip - see DrawPfSprout.

            if (pfBoardAcrossDcs && listing.Dc.Length > 0)
                chips.Add((listing.Dc, Dim));

            return chips;
        }

        /// <summary>
        /// A seat as a preset's slot, so it can be drawn by the same code: any job at all is a free
        /// seat, anything narrower is the set of jobs it takes. Built from the jobs a seat accepts.
        /// </summary>
        private static RoleSlot SeatAsSlot(IEnumerable<uint> acceptedJobs)
        {
            ulong flags = 0;
            int count = 0;
            foreach (uint id in acceptedJobs)
            {
                var job = JobData.FindById(id);
                if (job == null)
                    continue;
                flags |= 1UL << job.BitIndex;
                count++;
            }

            bool any = count == 0 || JobData.AllJobs.All(j => (flags & (1UL << j.BitIndex)) != 0);
            return new RoleSlot { Role = RoleType.Free, AcceptedJobFlags = any ? 0 : flags };
        }

        /// <summary>
        /// The seats as the mockup's rounded tiles, under their count. A filled seat is a blue-tinted
        /// tile with the member's job; an open one a dark tile with the Party Finder's own seat
        /// image; an omitted one dimmer still; a seat held for an applicant carries their job with a
        /// stronger ring. Hover names the job or lists what the seat takes.
        /// </summary>
        private void DrawPfSlots(PfBoardListing listing, float width, bool caption = true)
        {
            if (listing.Parties > 1)
            {
                DrawPfAllianceSlots(listing, width);
                return;
            }

            if (caption)
            using (UiHelpFont.Push())
            {
                int total = (listing.SlotsTotal > 0 ? listing.SlotsTotal : listing.Slots.Count) + listing.Applicants.Count;
                string held = listing.Applicants.Count > 0 ? $"  ·  {listing.Applicants.Count} held" : string.Empty;
                ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 3f);
                ImGui.TextColored(FbSlate400, $"{listing.SlotsFilled}/{total} in party{held}");
            }

            var dl = ImGui.GetWindowDrawList();
            Vector2 start = ImGui.GetCursorScreenPos();
            int perRow = Math.Max(1, (int)((width + PfSeatGap) / (PfSeat + PfSeatGap)));
            int seats = PfSeatCount(listing);
            int count = seats + listing.Applicants.Count;
            float inner = PfSeat;
            var order = PfSeatOrder(listing.Slots, 0, seats);

            for (int i = 0; i < count; i++)
            {
                int si = i < seats ? order[i] : i;
                var min = start + new Vector2((i % perRow) * (PfSeat + PfSeatGap), (i / perRow) * (PfSeat + PfSeatRowGap));
                var max = min + new Vector2(PfSeat);
                var innerMin = min;
                ImGui.SetCursorScreenPos(min);
                ImGui.Dummy(new Vector2(PfSeat));
                bool hot = ImGui.IsItemHovered();
                string tip;

                if (i >= seats)
                {
                    var applicant = listing.Applicants[i - seats];
                    if (applicant.Job > 0 && TryGetIconHandle(IconJobBase + (uint)applicant.Job, out var jh))
                        dl.AddImage(jh, innerMin, innerMin + new Vector2(inner));
                    PfHeldDot(dl, min, max);
                    tip = $"Held for {DisplayName(applicant.Name)} @ {applicant.World}";
                }
                else if (listing.Slots[si].Job > 0)
                {
                    var slot = listing.Slots[si];
                    if (TryGetIconHandle(IconJobBase + (uint)slot.Job, out var jh))
                        dl.AddImage(jh, innerMin, innerMin + new Vector2(inner));
                    tip = JobData.FindById((uint)slot.Job)?.Name ?? "In the party";
                }
                else
                {
                    var slot = listing.Slots[si];
                    bool omitted = IsOmittedMask(slot.Accepting);
                    var accepted = Board?.JobsAccepted(slot.Accepting).ToList() ?? new List<uint>();
                    int drawnFrom = dl.VtxBuffer.Size;
                    DrawSlotMiniIcon(omitted ? OmittedSlot : SeatAsSlot(accepted), innerMin, inner);
                    if (!omitted)
                        DimSince(dl, drawnFrom, OpenSeatAlpha);
                    tip = omitted ? OmittedSeatTip : PfJobList(accepted);
                }

                if (hot)
                    PaddedTooltip(tip);
            }

            int rows = (count + perRow - 1) / perRow;
            ImGui.SetCursorScreenPos(start);
            ImGui.Dummy(new Vector2(width, rows == 0 ? 0f : rows * PfSeat + (rows - 1) * PfSeatRowGap + 3f));
        }

        /// <summary>A seat held for an applicant: a small accent dot under its icon - the icon is
        /// bare everywhere now, so the mark sits beside it rather than round it.</summary>
        private static void PfHeldDot(ImDrawListPtr dl, Vector2 min, Vector2 max)
            => dl.AddCircleFilled(new Vector2((min.X + max.X) * 0.5f, max.Y + 1f), 2.5f,
                ImGui.ColorConvertFloat4ToU32(Accent), 12);

        /// <summary>
        /// How many seats a listing's card draws.
        ///
        /// THE LISTING ALWAYS CARRIES EIGHT SEAT ENTRIES, whatever the party size, and the ones a
        /// party does not have take no job - exactly what an omitted seat looks like. So a dungeon
        /// drew four seats and four "omitted" ones. The duty's own party size decides instead: a
        /// four-player duty is four seats, and an eight-player one keeps its omitted seats, which
        /// the game leaves out of the seat count. Without a known duty, the listing's own count or
        /// the last seat that takes a job, whichever is further. Alliances keep every entry.
        /// </summary>
        private int PfSeatCount(PfBoardListing listing)
        {
            int entries = listing.Slots.Count;
            if (listing.Parties > 1 || entries <= 4)
                return entries;

            if (listing.DutyType == 2 && listing.DutyId > 0
                && dutyDataHelper.GetDutyEntry((uint)listing.DutyId) is { QueueMaxPlayers: > 0 } duty)
                return Math.Clamp((int)duty.QueueMaxPlayers, Math.Min(entries, Math.Max(1, listing.SlotsTotal)), entries);

            if (listing.SlotsTotal > 4)
                return entries;

            int last = 0;
            for (int i = 0; i < entries; i++)
            {
                if (listing.Slots[i].Job > 0 || !IsOmittedMask(listing.Slots[i].Accepting))
                    last = i + 1;
            }
            return Math.Max(Math.Max(listing.SlotsTotal, last), 1);
        }

        /// <summary>How opaque a seat nobody is in is drawn: dimmer than a filled one, so what is
        /// taken and what is open read apart at a glance.</summary>
        private const float OpenSeatAlpha = 0.7f;

        /// <summary>
        /// The order a listing's seats are drawn in: taken seats first, then open ones, then omitted
        /// ones, each group keeping the listing's own order. The game lists seats where each role
        /// was put, which scatters the people in a party between the open seats; grouped, how full
        /// it is reads left to right. Returns slot indexes, <paramref name="count"/> of them from
        /// <paramref name="from"/>.
        /// </summary>
        private static int[] PfSeatOrder(List<PfBoardSlot> slots, int from, int count)
        {
            int end = Math.Min(slots.Count, from + count);
            int Rank(int i) => slots[i].Job > 0 ? 0 : IsOmittedMask(slots[i].Accepting) ? 2 : 1;
            return Enumerable.Range(from, Math.Max(0, end - from)).OrderBy(Rank).ThenBy(i => i).ToArray();
        }

        /// <summary>Fades everything drawn on <paramref name="dl"/> since vertex
        /// <paramref name="from"/> to <paramref name="alpha"/> of its own opacity - a whole icon,
        /// however it was drawn.</summary>
        private static unsafe void DimSince(ImDrawListPtr dl, int from, float alpha)
        {
            var vtx = (ImDrawVert*)dl.VtxBuffer.Data;
            for (int i = from; i < dl.VtxBuffer.Size; i++)
            {
                uint col = vtx[i].Col;
                uint a = (uint)((col >> 24) * alpha);
                vtx[i].Col = (col & 0x00FFFFFF) | (a << 24);
            }
        }

        private static readonly RoleSlot OmittedSlot = new() { Role = RoleType.Omit };
        private const string OmittedSeatTip = "Omitted - not recruiting for this seat";

        /// <summary>True for a seat's job mask that takes no job: the listing omits that seat.</summary>
        private static bool IsOmittedMask(string? mask)
            => !ulong.TryParse(mask, out ulong m) || m == 0;

        /// <summary>A seat's jobs, as the tooltip lists them - or "Any job".</summary>
        private static string PfJobList(List<uint> accepted)
        {
            var names = accepted.Select(j => JobData.FindById(j)?.Abbreviation).Where(a => a != null).Distinct().ToList();
            return names.Count == 0 || JobData.AllJobs.All(j => accepted.Contains((uint)j.Id))
                ? "Any job"
                : string.Join(", ", names);
        }

        /// <summary>The party's names and jobs, two to a row. Nothing marks who runs the plugin.</summary>
        private void DrawPfMembers(PfBoardListing listing, float width)
        {
            const float icon = 16f;
            float column = Math.Max(120f, (width - Space.Tight) / 2f);
            float left = ImGui.GetCursorPosX();

            int i = 0;
            int partySize = listing.Members.Count;
            foreach (var m in listing.Members.Concat(listing.Applicants))
            {
                bool applied = i >= partySize;
                if (i % 2 == 1)
                    ImGui.SameLine(left + column);

                if (m.Job > 0)
                    DrawJobIconInline((uint)m.Job, icon);
                else
                    ImGui.Dummy(new Vector2(icon, icon));
                ImGui.SameLine(0, 4f);

                using (UiHelpFont.Push())
                {
                    ImGui.AlignTextToFramePadding();
                    string label = applied ? $"{DisplayName(m.Name)} @ {m.World} · applied" : $"{DisplayName(m.Name)} @ {m.World}";
                    ImGui.TextColored(applied ? Accent : Dim, Fit(label, column - icon - 10f));
                    if (ImGui.IsItemHovered())
                    {
                        ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                        PaddedTooltip("Open their profile");
                    }
                    if (ImGui.IsItemClicked())
                        pfBoardClickedProfile = new CharacterIdentity(m.Name, m.World);
                }
                i++;
            }
        }

        /// <summary>
        /// The card's actions, across its foot. Join - now, on this data centre, for any listing - in
        /// the accent; Apply - a plugin user's coordinated listing, from anywhere you can travel -
        /// in glass beside it. One of them takes the whole width; both split it. Under them, what the
        /// last Join press found, while it is recent.
        /// </summary>
        private void DrawPfCardActions(PfBoardListing listing, float width)
        {
            var join = DirectJoin?.State(listing) ?? (false, false, "Join", string.Empty);
            // An alliance's Join is on each of its parties' rows instead.
            if (listing.Parties > 1)
                join.Shown = false;
            bool coordinated = !string.IsNullOrWhiteSpace(listing.CoordinationId);
            (bool Enabled, string Label, string Tooltip) apply = coordinated
                ? Coordination?.JoinState(listing) ?? (false, "Apply", "This listing is not taking applications.")
                : (false, "Apply", string.Empty);

            if (!join.Shown && !coordinated)
            {
                if (DirectJoin?.Result(listing) is { } alone)
                {
                    ImGui.Dummy(new Vector2(0, 4f));
                    DrawIosNote(alone.Message, alone.Good ? Positive : Negative, width);
                }
                return;
            }

            ImGui.Dummy(new Vector2(0, PfCardSpace - ImGui.GetStyle().ItemSpacing.Y * 2f));

            float gap = 8f;
            float each = join.Shown && coordinated ? (width - gap) * 0.5f : width;
            Vector2 start = ImGui.GetCursorScreenPos();

            if (join.Shown)
            {
                if (DrawPfActionButton($"##pfjoin{listing.Id}", join.Label, FontAwesomeIcon.UserPlus,
                        new Vector2(each, 36f), join.Enabled, primary: true, join.Tooltip))
                    RequestPfJoin(listing, -1);
            }

            if (coordinated)
            {
                if (join.Shown)
                    ImGui.SetCursorScreenPos(start + new Vector2(each + gap, 0f));
                if (DrawPfActionButton($"##pfapply{listing.Id}{listing.CoordinationId}", apply.Label,
                        FontAwesomeIcon.PaperPlane, new Vector2(each, 36f), apply.Enabled, primary: !join.Shown, apply.Tooltip))
                    RequestPfApply(listing);
            }

            if (DirectJoin?.Result(listing) is { } result)
            {
                ImGui.Dummy(new Vector2(0, 2f));
                DrawIosNote(result.Message, result.Good ? Positive : Negative, width);
            }
        }

        /// <summary>One of the card's action buttons: the accent with a soft glow when primary, glass
        /// otherwise; a glyph before the label; dimmed with the reason on hover when it cannot be
        /// pressed. True when pressed.</summary>
        private bool DrawPfActionButton(string id, string label, FontAwesomeIcon icon, Vector2 size,
            bool enabled, bool primary, string tooltip)
        {
            var dl = ImGui.GetWindowDrawList();
            Vector2 min = ImGui.GetCursorScreenPos();
            bool clicked = ImGui.InvisibleButton(id, size) && enabled;
            bool hovered = ImGui.IsItemHovered();
            bool hot = hovered && enabled;
            if (hot)
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            if (hovered && tooltip.Length > 0)
                PaddedTooltip(tooltip);

            float alpha = enabled ? 1f : 0.45f;
            Vector2 bMin = min, bMax = min + size;
            if (primary)
            {
                for (int i = 1; i <= 3; i++)
                    dl.AddRectFilled(bMin + new Vector2(-i, 3f - i * 0.5f), bMax + new Vector2(i, 3f + i),
                        ImGui.ColorConvertFloat4ToU32(FbBlue with { W = 0.07f * alpha }), Radius.Card + i);
                dl.AddRectFilled(bMin, bMax, ImGui.ColorConvertFloat4ToU32((hot ? FbBlueHover : FbBlue) with { W = alpha }), Radius.Card);
            }
            else
            {
                dl.AddRectFilled(bMin, bMax, ImGui.ColorConvertFloat4ToU32((hot ? FbNeutral700 : FbNeutral800) with { W = alpha }), Radius.Card);
                dl.AddRect(bMin, bMax, ImGui.ColorConvertFloat4ToU32(new Vector4(1, 1, 1, 0.1f * alpha)), Radius.Card, ImDrawFlags.None, 1f);
            }

            const float glyph = 13f, gap = 7f;
            using (UiSegmentFont.Push())
            {
                Vector2 ts = ImGui.CalcTextSize(label);
                Vector2 c = (bMin + bMax) * 0.5f;
                float x = c.X - (glyph + gap + ts.X) * 0.5f;
                DrawGlyphAtOn(dl, icon, new Vector2(x, c.Y - glyph * 0.5f), glyph, FbWhite with { W = alpha }, UiIconSmall);
                dl.AddText(new Vector2(x + glyph + gap, c.Y - ts.Y * 0.5f), ImGui.ColorConvertFloat4ToU32(FbWhite with { W = alpha }), label);
            }
            return clicked;
        }

        // ── Alliances ─────────────────────────────────────────────
        //
        // WHAT THE BOARD KNOWS ABOUT AN ALLIANCE IS LESS THAN IT KNOWS ABOUT A PARTY. The Party
        // Finder's list sends one seat entry per party - which jobs party A, B and C take - and the
        // alliance-wide count, and nothing about who sits in which party. That is why the game's own
        // list shows an alliance as three lettered badges rather than seats. The full listing, with
        // eight seats a party, only arrives when the listing itself is opened, which a Join press
        // does; from then on the card shows each party exactly.

        private static readonly string[] AllianceLetters = { "A", "B", "C", "D", "E", "F" };

        /// <summary>Whether the board holds every seat of the alliance, rather than one entry a party.</summary>
        private static bool AllianceDetailed(PfBoardListing l) => l.Slots.Count >= 16 && l.Slots.Count >= l.Parties * 8;

        /// <summary>How many of an alliance's seats the last full read found taken - which the
        /// listing's own count, kept current by every read of the list, can have moved on from.</summary>
        private static int AllianceSeatedAtCheck(PfBoardListing l) => l.Slots.Count(x => x.Job > 0);

        /// <summary>Whether party <paramref name="p"/> of an alliance still has a seat to fill.</summary>
        private static bool AlliancePartyOpen(PfBoardListing l, int p)
        {
            if (AllianceDetailed(l))
            {
                for (int i = p * 8; i < p * 8 + 8; i++)
                    if (l.Slots[i].Job == 0 && !IsOmittedMask(l.Slots[i].Accepting))
                        return true;
                return false;
            }
            return p < l.Slots.Count && !IsOmittedMask(l.Slots[p].Accepting);
        }

        /// <summary>An alliance on one line, as the game lists one: a lettered badge per party, lit
        /// while that party is still recruiting.</summary>
        private const float AllianceBadge = 16f, AllianceBadgeGap = 3f;

        /// <summary>The room a compact row's lettered badges take.</summary>
        private static float AllianceBadgesWidth(PfBoardListing l)
        {
            int n = Math.Clamp(l.Parties, 1, AllianceLetters.Length);
            return MathF.Max(PfCompactSeatsW, n * AllianceBadge + (n - 1) * AllianceBadgeGap);
        }

        private void DrawPfCompactAlliance(ImDrawListPtr dl, PfBoardListing listing, Vector2 at)
        {
            const float badge = AllianceBadge, gap = AllianceBadgeGap;
            int parties = Math.Min(listing.Parties, AllianceLetters.Length);
            var gold = ColorFromHex("#e8c35a");
            for (int p = 0; p < parties; p++)
            {
                var min = at + new Vector2(p * (badge + gap), 0f);
                var max = min + new Vector2(badge);
                bool open = AlliancePartyOpen(listing, p);
                dl.AddRectFilled(min, max, ImGui.ColorConvertFloat4ToU32(open ? gold : FbNeutral700), 3f);
                using (UiLabelFont.Push())
                {
                    Vector2 ts = ImGui.CalcTextSize(AllianceLetters[p]);
                    dl.AddText(min + (new Vector2(badge) - ts) * 0.5f,
                        ImGui.ColorConvertFloat4ToU32(open ? ColorFromHex("#3a2a00") : FbSlate500), AllianceLetters[p]);
                }
                if (ImGui.IsMouseHoveringRect(min, max) && ImGui.IsWindowHovered())
                    PaddedTooltip($"Alliance {AllianceLetters[p]}: {(open ? "recruiting" : "full")}");
            }
        }

        /// <summary>
        /// An alliance on the card, as the game's own window draws it: each party under its letter,
        /// eight seats a row. Until the listing has been read in full, a party's row is its eight
        /// seats as that party takes them, and the caption says who sits where is not known yet.
        /// </summary>
        private void DrawPfAllianceSlots(PfBoardListing listing, float width)
        {
            bool detailed = AllianceDetailed(listing);
            int total = listing.SlotsTotal > 0 ? listing.SlotsTotal : listing.Parties * 8;
            var join = DirectJoin?.State(listing) ?? (false, false, "Join", string.Empty);
            var dl = ImGui.GetWindowDrawList();

            using (UiHelpFont.Push())
            {
                ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 3f);
                // Who sits where is from the last full read; the count is from the latest read of the
                // list. When they disagree the seating is out of date, and it says so.
                int changes = detailed ? Math.Abs(listing.SlotsFilled - AllianceSeatedAtCheck(listing)) : 0;
                // Where the seats will come from, said plainly. On this data centre that is one press
                // away; anywhere else only a read over there can see inside an alliance - the game
                // opens listings on your own data centre only.
                string unknown = join.Shown
                    ? "who is in which party isn't known until it's checked"
                    : $"who is in which party shows once a plugin user on {(listing.Dc.Length > 0 ? listing.Dc : "its data centre")} reads its Party Finder";
                ImGui.TextColored(FbSlate400, $"{listing.SlotsFilled}/{total} in alliance"
                    + (!detailed ? $"  ·  {unknown}"
                        : changes > 0 ? $"  ·  seats as last checked, {changes} change{(changes == 1 ? "" : "s")} since"
                        : string.Empty));
            }

            // Reading the listing in full is what shows each party's seats. On this data centre that
            // is one press away; anywhere else it waits for somebody there to look.
            if ((!detailed || AllianceSeatedAtCheck(listing) != listing.SlotsFilled) && join.Shown)
            {
                Vector2 at = ImGui.GetCursorScreenPos();
                float right = at.X + width;
                bool busy = DirectJoin!.ActiveParty(listing) != null;
                if (DrawPfCompactPill(dl, $"##pfcheck{listing.Id}", busy ? "Checking..." : "Check parties", ref right,
                        at.Y + 13f, !busy, primary: false, "Read this listing from the game to see who is in each party."))
                    DirectJoin.Check(listing, PfDutyName(listing));
                ImGui.Dummy(new Vector2(width, 30f));
            }

            int parties = Math.Min(listing.Parties, AllianceLetters.Length);
            for (int p = 0; p < parties; p++)
            {
                using (UiHelpFont.Push())
                {
                    ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 3f);
                    ImGui.TextColored(PfSlate300, $"Alliance {AllianceLetters[p]}");
                }

                Vector2 start = ImGui.GetCursorScreenPos();
                var partyOrder = detailed ? PfSeatOrder(listing.Slots, p * 8, 8) : null;
                for (int i = 0; i < 8; i++)
                {
                    var min = start + new Vector2(i * (PfSeat + PfSeatGap), 0f);
                    ImGui.SetCursorScreenPos(min);
                    ImGui.Dummy(new Vector2(PfSeat));
                    bool hot = ImGui.IsItemHovered();

                    PfBoardSlot? slot = detailed ? listing.Slots[partyOrder![i]]
                        : p < listing.Slots.Count ? listing.Slots[p] : null;
                    string tip;
                    if (slot != null && detailed && slot.Job > 0)
                    {
                        if (TryGetIconHandle(IconJobBase + (uint)slot.Job, out var jh))
                            dl.AddImage(jh, min, min + new Vector2(PfSeat));
                        tip = JobData.FindById((uint)slot.Job)?.Name ?? "In the party";
                    }
                    else
                    {
                        bool omitted = slot == null || IsOmittedMask(slot.Accepting);
                        var accepted = slot != null ? Board?.JobsAccepted(slot.Accepting).ToList() ?? new List<uint>() : new List<uint>();
                        int drawnFrom = dl.VtxBuffer.Size;
                        DrawSlotMiniIcon(omitted ? OmittedSlot : SeatAsSlot(accepted), min, PfSeat);
                        if (!omitted)
                            DimSince(dl, drawnFrom, OpenSeatAlpha);
                        tip = omitted ? OmittedSeatTip : PfJobList(accepted);
                    }
                    if (hot)
                        PaddedTooltip(tip);
                }
                // This party's own Join: the game lets you pick which party of an alliance to join.
                if (join.Shown)
                {
                    bool thisOne = DirectJoin!.ActiveParty(listing) == p;
                    bool open = AlliancePartyOpen(listing, p);
                    float right = start.X + width;
                    string label = thisOne ? "Joining..." : $"Join {AllianceLetters[p]}";
                    string tip = !open ? $"Alliance {AllianceLetters[p]} has no open seat."
                        : join.Enabled ? $"Join Alliance {AllianceLetters[p]} now - checked against the listing as it stands first."
                        : join.Tooltip;
                    if (DrawPfCompactPill(dl, $"##pfjoin{listing.Id}p{p}", label, ref right, start.Y + PfSeat * 0.5f,
                            join.Enabled && open && !thisOne, primary: true, tip))
                        RequestPfJoin(listing, p);
                }

                ImGui.SetCursorScreenPos(start);
                ImGui.Dummy(new Vector2(width, PfSeat + 4f));
            }
        }

        // ── Private listings ──────────────────────────────────────

        /// <summary>A listing with a password: the game's private flag, or a coordinated listing in
        /// its private phase.</summary>
        private static bool IsPrivateListing(PfBoardListing l)
            => (l.SearchArea & 2) != 0 || l.CoordinationState == "private";

        /// <summary>A small lock before a private listing's duty name. Returns the room it took.</summary>
        private float DrawPfLock(ImDrawListPtr dl, Vector2 at, float lineH)
        {
            float size = MathF.Min(13f, lineH);
            DrawGlyphAtOn(dl, FontAwesomeIcon.Lock, new Vector2(at.X, at.Y + (lineH - size) * 0.5f), size, AccentYellow, UiIconSmall);
            return size + 6f;
        }

        /// <summary>The listing, party and action waiting on a password, while the prompt is up.</summary>
        private (PfBoardListing Listing, int Party, bool Apply)? pfPasswordFor;
        private string pfPasswordText = string.Empty;
        private bool pfPasswordOpen;

        /// <summary>Join, or ask for the password first when the listing is private.</summary>
        private void RequestPfJoin(PfBoardListing listing, int party)
        {
            if (IsPrivateListing(listing))
                AskPfPassword(listing, party, apply: false);
            else
                DirectJoin?.Join(listing, PfDutyName(listing), party);
        }

        /// <summary>Apply, or ask for the password first when the listing is private.</summary>
        private void RequestPfApply(PfBoardListing listing)
        {
            if (IsPrivateListing(listing))
                AskPfPassword(listing, -1, apply: true);
            else
                Coordination?.JoinFromBoard(listing, PfDutyName(listing));
        }

        private void AskPfPassword(PfBoardListing listing, int party, bool apply)
        {
            pfPasswordFor = (listing, party, apply);
            pfPasswordText = string.Empty;
            pfPasswordOpen = true;
        }

        /// <summary>
        /// The password prompt for a private listing: four digits, then Join or Apply. The game checks
        /// a Join's password at its own prompt; the server checks an Apply's against the host's.
        /// Drawn once per frame from the board.
        /// </summary>
        private void DrawPfPasswordPrompt()
        {
            if (pfPasswordFor is not { } ask)
                return;

            if (pfPasswordOpen)
            {
                ImGui.OpenPopup("##pfpassword");
                pfPasswordOpen = false;
            }

            PushIosMenuStyle();
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(14, 12));
            bool shown = ImGui.BeginPopup("##pfpassword");
            ImGui.PopStyleVar();
            if (!shown)
            {
                PopIosMenuStyle();
                pfPasswordFor = null;
                return;
            }

            string what = ask.Apply ? "Apply" : ask.Party >= 0 ? $"Join {AllianceLetters[ask.Party]}" : "Join";
            using (UiSegmentFont.Push())
                ImGui.TextColored(FbWhite, "Private party");
            using (UiHelpFont.Push())
                ImGui.TextColored(FbSlate400, ask.Apply
                    ? "Enter its 4-digit password to apply."
                    : "Enter its 4-digit password. The game checks it when you join.");
            ImGui.Dummy(new Vector2(0, 4f));

            ImGui.SetNextItemWidth(220f);
            if (ImGui.IsWindowAppearing())
                ImGui.SetKeyboardFocusHere();
            bool enter = ImGui.InputTextWithHint("##pfpw", "0000", ref pfPasswordText, 4,
                ImGuiInputTextFlags.CharsDecimal | ImGuiInputTextFlags.EnterReturnsTrue);
            bool valid = pfPasswordText.Length == 4 && int.TryParse(pfPasswordText, out _);

            ImGui.Dummy(new Vector2(0, 4f));
            bool go = DrawIosButton(what, "##pfpwgo", ask.Apply ? FontAwesomeIcon.PaperPlane : FontAwesomeIcon.UserPlus,
                new Vector2(106f, 30f), primary: true, enabled: valid) || (enter && valid);
            ImGui.SameLine(0, 8f);
            if (DrawIosButton("Cancel", "##pfpwcancel", FontAwesomeIcon.Times, new Vector2(106f, 30f), primary: false))
            {
                ImGui.CloseCurrentPopup();
                pfPasswordFor = null;
            }

            if (go)
            {
                int password = int.Parse(pfPasswordText);
                if (ask.Apply)
                    Coordination?.JoinFromBoard(ask.Listing, PfDutyName(ask.Listing), password);
                else
                    DirectJoin?.Join(ask.Listing, PfDutyName(ask.Listing), ask.Party, password);
                ImGui.CloseCurrentPopup();
                pfPasswordFor = null;
            }

            ImGui.EndPopup();
            PopIosMenuStyle();
        }

        /// <summary>The game's New Adventurer sprout before a listing that welcomes beginners, as
        /// the game's own list shows it. Returns the room it took.</summary>
        private float DrawPfSprout(ImDrawListPtr dl, Vector2 at, float lineH)
        {
            uint icon = dutyDataHelper.NewAdventurerIcon();
            if (icon == 0 || !TryGetIconHandle(icon, out var sprout))
                return 0f;
            float size = MathF.Min(lineH, 18f);
            var min = new Vector2(at.X, at.Y + (lineH - size) * 0.5f);
            dl.AddImage(sprout, min, min + new Vector2(size));
            if (ImGui.IsMouseHoveringRect(min, min + new Vector2(size)) && ImGui.IsWindowHovered())
                PaddedTooltip("Beginners welcome");
            return size + 4f;
        }

        /// <summary>A compact row's action pill, placed to the left of <paramref name="right"/> and
        /// moving it past itself. The accent when primary, glass otherwise. True when pressed.</summary>
        private bool DrawPfCompactPill(ImDrawListPtr dl, string id, string label, ref float right, float cy,
            bool enabled, bool primary, string tooltip)
        {
            float w;
            using (UiSegmentFont.Push())
                w = ImGui.CalcTextSize(label).X + 22f;
            var pMin = new Vector2(right - w, cy - 13f);
            var size = new Vector2(w, 26f);
            ImGui.SetCursorScreenPos(pMin);
            bool clicked = ImGui.InvisibleButton(id, size) && enabled;
            bool hot = ImGui.IsItemHovered();
            if (hot)
            {
                if (enabled)
                    ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                if (tooltip.Length > 0)
                    PaddedTooltip(tooltip);
            }

            float a = enabled ? 1f : 0.45f;
            if (primary)
            {
                dl.AddRectFilled(pMin, pMin + size,
                    ImGui.ColorConvertFloat4ToU32((hot && enabled ? AccentHover : Accent) with { W = a }), 13f);
            }
            else
            {
                dl.AddRectFilled(pMin, pMin + size,
                    ImGui.ColorConvertFloat4ToU32((hot && enabled ? FbNeutral700 : FbNeutral800) with { W = a }), 13f);
                dl.AddRect(pMin, pMin + size, ImGui.ColorConvertFloat4ToU32(new Vector4(1, 1, 1, 0.12f * a)), 13f, ImDrawFlags.None, 1f);
            }
            using (UiSegmentFont.Push())
            {
                Vector2 ts = ImGui.CalcTextSize(label);
                dl.AddText(pMin + (size - ts) * 0.5f, ImGui.ColorConvertFloat4ToU32(FbWhite with { W = a }), label);
            }

            right -= w + 8f;
            return clicked;
        }

        // ── Watched listings ──────────────────────────────────────

        private const float WatchWidth = 330f;

        /// <summary>
        /// Every watched listing, in a window of its own: the duty, its leader, how long it has,
        /// and its seats filling. Drag it anywhere; the lock keeps it where it is (and stops it
        /// being dragged by accident); the close button stops watching. Where each window sits is
        /// remembered between sessions, per leader.
        /// </summary>
        private void DrawWatchedListings()
        {
            var board = Board;
            if (board == null || !config.CommunityEnabled)
                return;

            int n = 0;
            foreach (var w in board.Watched.ToList())
            {
                var vp = ImGui.GetMainViewport();
                ImGui.SetNextWindowPos(new Vector2(vp.WorkPos.X + 60f + n * 30f, vp.WorkPos.Y + 160f + n * 30f), ImGuiCond.FirstUseEver);
                n++;

                bool close = false;
                try
                {
                    if (BeginOverlayWindow($"###pfwatch{w.Key}", WatchWidth, w.Locked))
                        close = DrawWatchedBody(w);
                }
                finally
                {
                    EndOverlayWindow();
                }

                if (close)
                    board.Unwatch(w.Key);
            }
        }

        /// <summary>A watched party: the duty and its leader, where and how long, then the seats
        /// filling in. Returns true when its close button was pressed.</summary>
        private bool DrawWatchedBody(PfBoard.WatchedParty w)
        {
            var listing = w.Listing;
            string title = listing != null ? PfDutyName(listing) : w.DutyLabel;

            var (close, lockPressed) = DrawOverlayHeader($"pfwatch{w.Key}", title,
                $"{DisplayName(w.LeaderName)} @ {w.LeaderWorld}", w.Locked);
            if (lockPressed)
                w.Locked = !w.Locked;

            if (listing == null || w.Ended)
            {
                OverlayInset(width =>
                {
                    using (UiBodyFont.Push())
                        ImGui.TextColored(Faint, "This listing has ended.");
                });
                return close;
            }

            // Where and how long as two small capsules, and on the same line, all the way right, how
            // full it is - the one number a watch is kept for.
            var dl = ImGui.GetWindowDrawList();
            Vector2 lineStart = ImGui.GetCursorScreenPos();
            float lineRight = lineStart.X + ImGui.GetContentRegionAvail().X;
            bool any = false;
            if (listing.Dc.Length > 0)
            {
                OverlayPill(listing.Dc, FbSlate400);
                any = true;
            }
            if (listing.SecondsRemaining > 0)
            {
                if (any)
                    ImGui.SameLine(0, 6f);
                OverlayPill($"{Math.Max(1, listing.SecondsRemaining / 60)}m left", FbBlue);
                any = true;
            }

            int total = listing.SlotsTotal > 0 ? listing.SlotsTotal : listing.Parties > 1 ? listing.Parties * 8 : listing.Slots.Count;
            if (total > 0)
            {
                string count = $"{listing.SlotsFilled}/{total}";
                Vector2 ps = PillSize(count);
                DrawPillAt(dl, new Vector2(lineRight - ps.X, lineStart.Y), count,
                    listing.SlotsFilled >= total ? FbGreen : FbSlate400);
                if (!any)
                    ImGui.Dummy(new Vector2(0, ps.Y));
            }
            ImGui.Dummy(new Vector2(0, 2f));

            // The recruiter's own words, as the game shows them.
            if (listing.Description.Length > 0)
            {
                float width = ImGui.GetContentRegionAvail().X;
                using (CommentFont.Push())
                    DrawCommentLines(WrapCommentToLines(listing.Description, width, 6), PfSlate300, width);
                ImGui.Dummy(new Vector2(0, 2f));
            }

            OverlayInset(width => DrawPfSlots(listing, width, caption: false));

            ImGui.Dummy(new Vector2(0, 4f));
            if (OverlayButton("Send tell", $"##pfwatchtell{w.Key}", ImGui.GetContentRegionAvail().X, false))
                OpenTell(listing.LeaderName, listing.LeaderWorld);
            return close;
        }

        // ── Tells ─────────────────────────────────────────────────

        /// <summary>Who the tell window is writing to, while it is open.</summary>
        private (string Name, string World)? pfTellTo;
        private string pfTellText = string.Empty;
        private string pfTellNote = string.Empty;
        private bool pfTellSending;

        private void OpenTell(string name, string world)
        {
            pfTellTo = (name, world);
            pfTellText = string.Empty;
            pfTellNote = string.Empty;
        }

        /// <summary>
        /// A small window to write a tell to a listing's recruiter, sent as the game sends one -
        /// "/tell Name@World", which reaches any world. The real name goes to the game; the name
        /// on screen follows the name setting.
        /// </summary>
        private void DrawTellWindow()
        {
            if (pfTellTo is not { } to)
                return;

            bool close = false;
            try
            {
                var vp = ImGui.GetMainViewport();
                ImGui.SetNextWindowPos(vp.WorkPos + vp.WorkSize * 0.5f, ImGuiCond.FirstUseEver, new Vector2(0.5f, 0.5f));
                if (BeginOverlayWindow("###pftellwindow", 340f, false))
                {
                    var (x, _) = DrawOverlayHeader("pftell", "Send tell", $"{DisplayName(to.Name)} @ {to.World}", null);
                    close = x;

                    float width = ImGui.GetContentRegionAvail().X;
                    if (ImGui.IsWindowAppearing())
                        ImGui.SetKeyboardFocusHere();
                    DrawIosTextBox("##pftelltext", ref pfTellText, 400, new Vector2(width, 78f), pfTellSending);
                    using (UiHelpFont.Push())
                        ImGui.TextColored(pfTellNote.StartsWith("Sent", StringComparison.Ordinal) ? FbGreen
                            : pfTellNote.Length > 0 ? Negative : FbSlate500,
                            pfTellNote.Length > 0 ? pfTellNote : $"{pfTellText.Length}/400");

                    ImGui.Dummy(new Vector2(0, 4f));
                    var (send, cancel) = OverlayButtonPair(pfTellSending ? "Sending..." : "Send", "Cancel", "pftellbtn");
                    if (cancel)
                        close = true;
                    if (send && !pfTellSending && pfTellText.Trim().Length > 0)
                    {
                        pfTellSending = true;
                        string message = pfTellText;
                        _ = pfAutomation.SendTellAsync(to.Name, to.World, message).ContinueWith(t =>
                        {
                            pfTellSending = false;
                            if (t.IsCompletedSuccessfully && t.Result)
                            {
                                pfTellNote = $"Sent to {DisplayName(to.Name)}.";
                                pfTellText = string.Empty;
                            }
                            else
                            {
                                pfTellNote = "Couldn't send that. Check you are logged in and the message is one line.";
                            }
                        });
                    }
                }
            }
            finally
            {
                EndOverlayWindow();
            }

            if (close)
                pfTellTo = null;
        }

        // ── Names ─────────────────────────────────────────────────

        /// <summary>
        /// What a listing is for, in words.
        ///
        /// Only an ordinary duty listing has a duty id that means a duty. A roulette's id is a
        /// roulette, and "other" listings - FATEs, hunts, treasure maps - carry an id whose meaning
        /// depends on the category, so those are named by their category, which is what the
        /// game's own list calls them too.
        /// </summary>
        private string PfDutyName(PfBoardListing listing)
        {
            // A plugin party nobody has browsed: its duty comes from its members' report.
            if (!listing.OnBoard)
            {
                string reported = dutyDataHelper.GetDutyName((uint)listing.DutyId);
                if (reported.Length > 0 && reported != "None" && !reported.StartsWith("Unknown", StringComparison.Ordinal))
                    return reported;
            }

            // The specific thing - a duty, a roulette, and for the categories that number their own
            // content the FATE zone, deep dungeon, treasure map or Gold Saucer game.
            string? specific = dutyDataHelper.ListingDutyName(listing.Category, listing.DutyType, (uint)listing.DutyId);
            if (specific != null)
                return ListingLockedHere(listing) ? (config.ShowLockedDutyNames ? $"{specific} (Locked Duty)" : "Locked Duty") : specific;

            // A whole category ("any dungeon"), or nothing at all - which the game itself calls None.
            string category = PfCategoryName(listing.Category);
            return category.Length > 0 ? category : "None";
        }

        /// <summary>
        /// Whether this character has not unlocked what a listing is for - a duty, or for a FATE
        /// listing the zone (its aetherytes), for a deep dungeon the dungeon, and so on. Such a
        /// listing reads "Locked Duty", the way the game's own window hides the spoiler, unless
        /// "Show names of locked duties" is on; it sorts last and cannot be joined. Asked for every
        /// listing every frame, so each answer is kept for a few seconds.
        /// </summary>
        private bool ListingLockedHere(PfBoardListing listing)
        {
            var key = (listing.Category, listing.DutyType, listing.DutyId);
            var now = DateTime.UtcNow;
            if (pfUnlockCache.TryGetValue(key, out var hit) && now - hit.At < TimeSpan.FromSeconds(10))
                return hit.Locked;
            var entry = dutyDataHelper.ListingDutyEntry(listing.Category, listing.DutyType, (uint)Math.Max(0, listing.DutyId));
            bool locked = entry != null && !dutyDataHelper.IsDutyUnlocked(entry);
            pfUnlockCache[key] = (locked, now);
            return locked;
        }

        private readonly Dictionary<(int, int, int), (bool Locked, DateTime At)> pfUnlockCache = new();

        private static Type? pfCategoryType;
        private static readonly Dictionary<int, string> PfCategoryNames = new();

        /// <summary>The category's name out of Dalamud's own enum, spaced into words.</summary>
        private static string PfCategoryName(int category)
        {
            if (category == 0)
                return string.Empty;

            if (PfCategoryNames.TryGetValue(category, out var cached))
                return cached;

            pfCategoryType ??= typeof(IPartyFinderListing).GetProperty(nameof(IPartyFinderListing.Category))?.PropertyType;

            string name = string.Empty;
            if (pfCategoryType is { IsEnum: true })
            {
                string? raw = Enum.GetName(pfCategoryType, Enum.ToObject(pfCategoryType, category));
                if (raw != null)
                    name = Regex.Replace(raw, "(?<=[a-z])(?=[A-Z])", " ");
            }

            PfCategoryNames[category] = name;
            return name;
        }

        private static string Ago(int seconds)
            => seconds < 5 ? "just now"
                : seconds < 60 ? $"{seconds}s ago"
                : $"{seconds / 60}m ago";
    }
}
#endif
