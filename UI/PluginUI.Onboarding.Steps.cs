using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.GameFonts;
using Dalamud.Interface.ManagedFontAtlas;

namespace PfPresets
{
    /// <summary>
    /// The step bodies.
    ///
    /// Three of them are real controls wired to the config and applied on the press - the window,
    /// the accent and the clear announcements. The rest are figures: a profile card, a party list
    /// mid-vote, a clears feed, a preset row and a summary, each drawn at the size and in the
    /// colours the real surface uses, so that meeting the real one later is recognition rather than
    /// introduction.
    ///
    /// Everything is positioned absolutely from the content box and drawn on the window's draw
    /// list. Hit areas are invisible buttons placed under what they cover; see <see cref="OnbHit"/>.
    /// </summary>
    public partial class PluginUI
    {
        // The mock content, in one place. Named so it is obvious in a diff that these are figures
        // rather than anything the plugin knows.
        private const string OnbSampleDuty = "Jeuno: The First Walk";
        private const string OnbSampleSub = "Alliance farm - Normal loot rules";

        /// <summary>Raids, which is what an alliance raid is filed under - the index into
        /// DutyCategoryIcons, not a duty id.</summary>
        private const int OnbSampleCategory = 5;

        // ClassJob row ids, which is what the game's icon sheet is keyed on. Spelled out rather
        // than looked up through JobData: these are the figures' cast, not a query, and a constant
        // here cannot go stale the way a name lookup could.
        private const uint OnbSampleJob = 37;        // Gunbreaker, the card's own character
        private const uint OnbSampleFeedJobOne = 25; // Black Mage
        private const uint OnbSampleFeedJobTwo = 40; // Sage
        private const uint OnbJobPld = 19;
        private const uint OnbJobWar = 21;
        private const uint OnbJobWhm = 24;
        private const uint OnbJobSch = 28;
        private const uint OnbJobMnk = 20;
        private const uint OnbJobDrg = 22;
        private const uint OnbJobBlm = 25;

        /// <summary>An invisible button over a screen-space rectangle. Converts to the window-local
        /// coordinates ImGui's cursor wants, so a step body can work entirely in screen space.</summary>
        private static bool OnbHit(string id, Vector2 min, Vector2 size, out bool hot)
        {
            ImGui.SetCursorPos(min - ImGui.GetWindowPos());
            ImGui.InvisibleButton(id, size);
            hot = ImGui.IsItemHovered();
            return ImGui.IsItemClicked();
        }

        // ══════════════════════════════════════════════════════════
        //  WELCOME
        // ══════════════════════════════════════════════════════════

        /// <summary>
        /// The fork, and the only page with no rail under it.
        ///
        /// Two doors, and the recommended one is ringed because it is the one to take if you have
        /// no opinion yet - which on a first launch is everybody. Neither is a dismissal: both lead
        /// to the same tour, so nobody is punished with ignorance for not wanting to answer
        /// questions first.
        /// </summary>
        private void DrawOnbWelcome(ImDrawListPtr dl, Vector2 box)
        {
            float left = MathF.Round(box.X + (OnbContentW - OnbNarrowW) * 0.5f);

            const string recommendedLine =
                "Landscape, purple, notifications on, then a quick tour of what the plugin does.";
            const string customLine =
                "Pick your window, colour and notifications first, then the same tour.";

            // Measured before anything is drawn so the whole column can be centred vertically. A
            // headline pinned to the top of a 421px box with two hundred pixels of air under it
            // reads as a page that failed to load the rest of itself.
            float cardOneH = OnbPathCardHeight(recommendedLine);
            float cardTwoH = OnbPathCardHeight(customLine);

            float titleLh, titleH, leadLh;
            using (OnbDisplay.Push())
            {
                titleLh = ImGui.GetTextLineHeight() * 1.1f;
                titleH = titleLh * 2f;
            }

            float leadH;
            using (OnbLead.Push())
            {
                leadLh = ImGui.GetTextLineHeight() * 1.5f;
                leadH = OnbWrap(
                    "Ratings, presets, clears and party progression. Two minutes to set up, or "
                    + "none at all.", OnbNarrowW).Count * leadLh;
            }

            float total = titleH + 14f + leadH + 30f + cardOneH + 10f + cardTwoH;
            float y = box.Y + Math.Max(0f, (OnbContentH - total) * 0.5f);

            using (OnbDisplay.Push())
            {
                uint ink = ImGui.ColorConvertFloat4ToU32(Ink);
                OnbText(dl, new Vector2(left, y), ink, "Welcome to Party");
                OnbText(dl, new Vector2(left, y + titleLh), ink, "Finder Analysis");
            }

            y += titleH + 14f;

            using (OnbLead.Push())
            {
                y += OnbWrapped(dl,
                    "Ratings, presets, clears and party progression. Two minutes to set up, or "
                    + "none at all.",
                    new Vector2(left, y), OnbNarrowW, Dim, leadLh);
            }

            y += 30f;

            if (OnbPathCard(dl, new Vector2(left, y), cardOneH, FontAwesomeIcon.Check,
                    "Use recommended settings", recommendedLine, ringed: true,
                    "##OnbPathRecommended"))
                ApplyOnboardingRecommended();

            y += cardOneH + 10f;

            if (OnbPathCard(dl, new Vector2(left, y), cardTwoH, FontAwesomeIcon.Cog,
                    "Customise your experience", customLine, ringed: false, "##OnbPathCustom"))
            {
                onboardingRecommended = false;
                GoToOnboardingStep(OnboardingSequence[0]);
            }
        }

        /// <summary>The width a path card's sentence wraps into. One expression, used by the measure
        /// and the draw so the two cannot disagree.</summary>
        private const float OnbPathTextW = OnbNarrowW - 18f * 2f - 36f - 14f - 16f - 8f;

        private float OnbPathCardHeight(string body)
        {
            float titleH, bodyH;

            using (OnbCard.Push())
                titleH = ImGui.GetTextLineHeight();

            using (OnbSmall.Push())
                bodyH = OnbWrap(body, OnbPathTextW).Count * ImGui.GetTextLineHeight() * 1.4f;

            return Math.Max(36f, titleH + 2f + bodyH) + 16f * 2f;
        }

        private bool OnbPathCard(ImDrawListPtr dl, Vector2 at, float height, FontAwesomeIcon icon,
            string title, string body, bool ringed, string id)
        {
            var size = new Vector2(OnbNarrowW, MathF.Round(height));
            at = OnbSnap(at);
            bool clicked = OnbHit(id, at, size, out bool hot);
            Vector2 max = at + size;

            dl.AddRectFilled(at, max,
                ImGui.ColorConvertFloat4ToU32(hot ? ColorFromHex("#18181b") : Panel), 14f);

            // The recommended card carries a soft accent halo as well as its border. Two rings
            // rather than one because a 1.5px outline in the accent, on a near-black card, at the
            // distance a window is actually looked at, is not a signal - it is a hairline.
            if (ringed)
            {
                dl.AddRect(at - new Vector2(3f, 3f), max + new Vector2(3f, 3f),
                    ImGui.ColorConvertFloat4ToU32(AccentAlpha(0.18f)), 17f, ImDrawFlags.None, 3f);
                dl.AddRect(at, max, ImGui.ColorConvertFloat4ToU32(Accent), 14f, ImDrawFlags.None,
                    1.5f);
            }
            else
            {
                dl.AddRect(at, max, ImGui.ColorConvertFloat4ToU32(hot ? BorderControl : RuleHair),
                    14f, ImDrawFlags.None, 1.5f);
            }

            var tile = OnbSnap(new Vector2(at.X + 18f, at.Y + (height - 36f) * 0.5f));
            dl.AddRectFilled(tile, tile + new Vector2(36f, 36f),
                ImGui.ColorConvertFloat4ToU32(ringed ? Accent : Raised), 11f);
            OnbIcon(dl, icon, tile + new Vector2(18f, 18f), ringed ? OnAccent : Ink,
                OnbIconMidPx);

            float textX = at.X + 18f + 36f + 14f;
            float y = at.Y + 16f;

            using (OnbCard.Push())
            {
                OnbText(dl, new Vector2(textX, y), Ink, title);
                y += ImGui.GetTextLineHeight() + 2f;
            }

            using (OnbSmall.Push())
                OnbWrapped(dl, body, new Vector2(textX, y), OnbPathTextW, Dim,
                    ImGui.GetTextLineHeight() * 1.4f);

            OnbIcon(dl, FontAwesomeIcon.ChevronRight,
                new Vector2(max.X - 18f - 8f, at.Y + height * 0.5f), ringed ? Accent : Faint);

            return clicked;
        }

        /// <summary>
        /// Takes the defaults and jumps to the tour.
        ///
        /// It writes them rather than assuming them. A config edited by hand, or carried over from
        /// an install that chose otherwise, would leave "recommended" meaning "whatever you already
        /// had" - and the summary at the end would then say something this button did not do.
        ///
        /// THE ANNOUNCEMENT FACE IS NOT RESET. Jupiter is the plugin's own considered default and
        /// the reason is written out in Configuration.cs; the three things this button is named
        /// after are the three it touches.
        /// </summary>
        private void ApplyOnboardingRecommended()
        {
            config.Device = DeviceLayout.Landscape;
            config.AccentColorHex = DefaultAccentHex;
#if PFP_RATINGS
            config.ClearAnnouncementsEnabled = true;
            config.ClearAnnouncementClickThrough = false;
#endif
            config.Save();

            onboardingRecommended = true;
            GoToOnboardingStep(OnboardingSequence[0]);
        }

        // ══════════════════════════════════════════════════════════
        //  PICK A WINDOW
        // ══════════════════════════════════════════════════════════

        private void DrawOnbWindow(ImDrawListPtr dl, Vector2 box)
        {
            float y = box.Y;
            y += OnbStepLabel(dl, new Vector2(box.X, y)) + 10f;

            using (OnbTitle.Push())
            {
                OnbText(dl, new Vector2(box.X, y), Ink, "Pick a window");
                y += ImGui.GetTextLineHeight() * 1.12f + 12f;
            }

            using (OnbBody.Push())
            {
                y += OnbWrapped(dl,
                    "Two fixed shapes and nothing in between. Portrait is one column with a tab bar "
                    + "along the bottom and fits beside the game. Landscape is wider, with a "
                    + "sidebar and two-column pages.",
                    new Vector2(box.X, y), OnbColW, Dim, ImGui.GetTextLineHeight() * 1.5f);
            }

            y += 20f;

            // The plugin's own segmented control, not a copy of it. The one thing this step decides
            // is a setting that already has a widget, and building a second one here is exactly how
            // two controls for the same setting end up different sizes.
            ImGui.SetCursorPos(new Vector2(box.X, y) - ImGui.GetWindowPos());
            int device = (int)config.Device;
            string[] labels =
            {
                $"Portrait - {DeviceMetrics.SizeLabel(DeviceLayout.Portrait)}",
                $"Landscape - {DeviceMetrics.SizeLabel(DeviceLayout.Landscape)}",
            };

            if (DrawSegmentedControl("onbdevice", labels, ref device, OnbColW))
            {
                config.Device = (DeviceLayout)device;
                config.Save();
            }

            y += 36f + 10f;

            using (OnbTiny.Push())
                OnbWrapped(dl,
                    "Landscape is the default. You can change it any time in Settings, under "
                    + "Appearance.",
                    new Vector2(box.X, y), OnbColW, Faint, ImGui.GetTextLineHeight() * 1.4f);

            OnbPanel(dl, box, out Vector2 pMin, out Vector2 pMax);

            // Both thumbnails sit on the same baseline rather than being centred against each
            // other, so the difference in shape is the difference between the two windows and not a
            // difference in where they happen to float.
            float baseline = MathF.Round(pMax.Y - 28f - 24f);
            float groupW = 150f + 22f + 262f;
            float startX = MathF.Round(pMin.X + (OnbPanelW - groupW) * 0.5f);

            bool portrait = config.Device == DeviceLayout.Portrait;

            if (OnbPortraitThumb(dl, new Vector2(startX, baseline - 294f), portrait))
            {
                config.Device = DeviceLayout.Portrait;
                config.Save();
            }

            if (OnbLandscapeThumb(dl, new Vector2(startX + 150f + 22f, baseline - 182f), !portrait))
            {
                config.Device = DeviceLayout.Landscape;
                config.Save();
            }
        }

        private bool OnbPortraitThumb(ImDrawListPtr dl, Vector2 at, bool chosen)
        {
            var size = new Vector2(150f, 294f);
            at = OnbSnap(at);
            bool clicked = OnbHit("##OnbThumbPortrait", at, size + new Vector2(0f, 28f), out _);
            Vector2 max = at + size;

            dl.AddRectFilled(at, max, ImGui.ColorConvertFloat4ToU32(Ground), 11f);
            dl.AddRect(at, max, ImGui.ColorConvertFloat4ToU32(CardBorder), 11f, ImDrawFlags.None, 1f);
            if (chosen)
                dl.AddRect(at - new Vector2(2f, 2f), max + new Vector2(2f, 2f),
                    ImGui.ColorConvertFloat4ToU32(Accent), 13f, ImDrawFlags.None, 2f);

            uint panel = ImGui.ColorConvertFloat4ToU32(Panel);
            uint field = ImGui.ColorConvertFloat4ToU32(Field);

            dl.AddRectFilled(at, new Vector2(max.X, at.Y + 23f), panel, 11f,
                ImDrawFlags.RoundCornersTop);

            float y = at.Y + 23f + 7f;
            dl.AddRectFilled(new Vector2(at.X + 7f, y), new Vector2(max.X - 7f, y + 15f), field, 5f);
            y += 15f + 6f;

            for (int i = 0; i < 3; i++)
            {
                dl.AddRectFilled(new Vector2(at.X + 7f, y), new Vector2(max.X - 7f, y + 54f), field,
                    6f);
                y += 54f + 6f;
            }

            // The tab bar and the home indicator: the two things that make the shape read as the
            // phone rather than as a tall rectangle.
            float barTop = max.Y - 32f;
            dl.AddRectFilled(new Vector2(at.X, barTop), new Vector2(max.X, max.Y), panel, 11f,
                ImDrawFlags.RoundCornersBottom);

            float centreX = at.X + 75f;
            for (int i = 0; i < 4; i++)
            {
                var tab = new Vector2(centreX - 33f + i * 20f, barTop + 10.5f);
                dl.AddRectFilled(tab, tab + new Vector2(12f, 3f),
                    ImGui.ColorConvertFloat4ToU32(i == 0 ? Accent : BorderControl), Radius.Pill);
            }

            dl.AddRectFilled(new Vector2(centreX - 26.5f, max.Y - 6f),
                new Vector2(centreX + 26.5f, max.Y - 4f),
                ImGui.ColorConvertFloat4ToU32(BorderControl), Radius.Pill);

            using (OnbTiny.Push())
            {
                string label = $"Portrait - {DeviceMetrics.SizeLabel(DeviceLayout.Portrait)}";
                Vector2 ts = ImGui.CalcTextSize(label);
                OnbText(dl, new Vector2(at.X + (150f - ts.X) * 0.5f, max.Y + 10f), chosen ? Ink : Faint, label);
            }

            return clicked;
        }

        private bool OnbLandscapeThumb(ImDrawListPtr dl, Vector2 at, bool chosen)
        {
            var size = new Vector2(262f, 182f);
            at = OnbSnap(at);
            bool clicked = OnbHit("##OnbThumbLandscape", at, size + new Vector2(0f, 28f), out _);
            Vector2 max = at + size;

            dl.AddRectFilled(at, max, ImGui.ColorConvertFloat4ToU32(Ground), 5f);
            dl.AddRect(at, max, ImGui.ColorConvertFloat4ToU32(CardBorder), 5f, ImDrawFlags.None, 1f);
            if (chosen)
                dl.AddRect(at - new Vector2(2f, 2f), max + new Vector2(2f, 2f),
                    ImGui.ColorConvertFloat4ToU32(Accent), 7f, ImDrawFlags.None, 2f);

            uint panel = ImGui.ColorConvertFloat4ToU32(Panel);
            uint field = ImGui.ColorConvertFloat4ToU32(Field);

            dl.AddRectFilled(at, new Vector2(at.X + 58f, max.Y), panel, 5f,
                ImDrawFlags.RoundCornersLeft);

            float y = at.Y + 5f + 14f;
            for (int i = 0; i < 4; i++)
            {
                dl.AddRectFilled(new Vector2(at.X + 5f, y), new Vector2(at.X + 53f, y + 11f),
                    i == 0 ? ImGui.ColorConvertFloat4ToU32(AccentAlpha(0.18f)) : field, 4f);
                y += 11f + 4f;
            }

            dl.AddRectFilled(new Vector2(at.X + 58f, at.Y), new Vector2(max.X, at.Y + 17f), panel, 5f,
                ImDrawFlags.RoundCornersTopRight);

            float bodyX = at.X + 58f + 6f;
            float bodyY = at.Y + 17f + 6f;
            float sideX = max.X - 6f - 74f;
            float colW = sideX - 6f - bodyX;

            dl.AddRectFilled(new Vector2(bodyX, bodyY), new Vector2(bodyX + colW, bodyY + 11f), field,
                4f);
            dl.AddRectFilled(new Vector2(bodyX, bodyY + 16f), new Vector2(bodyX + colW, bodyY + 78f),
                field, 5f);
            dl.AddRectFilled(new Vector2(bodyX, bodyY + 83f), new Vector2(bodyX + colW, bodyY + 145f),
                field, 5f);

            dl.AddRectFilled(new Vector2(sideX, bodyY), new Vector2(sideX + 74f, bodyY + 59f), field,
                5f);
            dl.AddRectFilled(new Vector2(sideX, bodyY + 64f), new Vector2(sideX + 74f, max.Y - 6f),
                field, 5f);

            using (OnbTiny.Push())
            {
                string label = $"Landscape - {DeviceMetrics.SizeLabel(DeviceLayout.Landscape)}";
                Vector2 ts = ImGui.CalcTextSize(label);
                OnbText(dl, new Vector2(at.X + (262f - ts.X) * 0.5f, max.Y + 10f), chosen ? Ink : Faint, label);
            }

            return clicked;
        }

        // ══════════════════════════════════════════════════════════
        //  PICK A COLOUR
        // ══════════════════════════════════════════════════════════

        private void DrawOnbColour(ImDrawListPtr dl, Vector2 box)
        {
            float y = box.Y;
            y += OnbStepLabel(dl, new Vector2(box.X, y)) + 10f;

            using (OnbTitle.Push())
            {
                OnbText(dl, new Vector2(box.X, y), Ink, "Pick a colour");
                y += ImGui.GetTextLineHeight() * 1.12f + 12f;
            }

            using (OnbBody.Push())
            {
                y += OnbWrapped(dl,
                    "It colours the primary action, the active tab and the refresh countdown. Role "
                    + "and vote colours never change.",
                    new Vector2(box.X, y), OnbColW, Dim, ImGui.GetTextLineHeight() * 1.5f);
            }

            y += 24f;

            // THE PLUGIN'S OWN ACCENT PICKER, not a copy of it - see DrawAccentSwatches in
            // PluginUI.Theme.cs. The copy that used to be here is what made this step the one
            // control in the run that looked pixelated: it outlined every unselected swatch with a
            // one-pixel stroke sitting on the fill's own rounded edge, which the real control does
            // not draw at all. Two implementations of one control, and only one of them right.
            //
            // It is cursor-based, like every widget in the plugin, so the cursor is put where this
            // step wants it and the control lays itself out from there.
            const float swatch = 28f;
            ImGui.SetCursorPos(OnbSnap(new Vector2(box.X, y)) - ImGui.GetWindowPos());
            DrawAccentSwatches();

            y += swatch + 18f;

            // Read AFTER the swatches, not before: a click lands inside DrawAccentSwatches, and a
            // name captured above it would spend the frame of the click still naming the old
            // colour while every other accented thing on screen had already changed.
            string current = string.IsNullOrWhiteSpace(config.AccentColorHex)
                ? DefaultAccentHex
                : config.AccentColorHex.Trim();

            string name = Array.Find(AccentChoices,
                c => string.Equals(c.Hex, current, StringComparison.OrdinalIgnoreCase)).Name;

            using (OnbSmallBold.Push())
            {
                OnbText(dl, new Vector2(box.X, y), Ink, string.IsNullOrEmpty(name) ? current : $"{name} - {current}");
                y += ImGui.GetTextLineHeight() + 4f;
            }

            using (OnbTiny.Push())
                OnbWrapped(dl,
                    "Purple is the default. Red and amber are not offered - they already mean "
                    + "something in this plugin.",
                    new Vector2(box.X, y), OnbColW, Faint, ImGui.GetTextLineHeight() * 1.4f);

            // ── the preview ──
            OnbPanel(dl, box, out Vector2 pMin, out _);

            // 124 RATHER THAN 110. At 110 the Apply button's top edge landed two pixels above the
            // bottom of the duty tile beside it - the button was not merely close to the name, it
            // was overlapping the artwork. The card is now tall enough for a 48px tile, its 12px of
            // air, and the button, which is the same stack the real preset row has.
            const float mockW = 330f, mockH = 124f, barH = 60f, tile = 48f;
            float mockX = MathF.Round(pMin.X + (OnbPanelW - mockW) * 0.5f);
            float mockY = MathF.Round(pMin.Y + (OnbContentH - (mockH + 18f + barH)) * 0.5f);

            OnbCardRect(dl, new Vector2(mockX, mockY), new Vector2(mockX + mockW, mockY + mockH),
                Field, CardBorder, Radius.Card);

            var art = OnbSnap(new Vector2(mockX + 14f, mockY + 14f));
            OnbDutyTile(dl, art, tile);

            using (UiNameFont.Push())
                OnbText(dl, new Vector2(art.X + tile + 12f, art.Y + 4f), Ink, OnbSampleDuty);

            using (UiHelpFont.Push())
                OnbText(dl, new Vector2(art.X + tile + 12f, art.Y + 26f), Faint, OnbSampleSub);

            var apply = OnbSnap(new Vector2(mockX + 14f, art.Y + tile + 12f));
            OnbApplyButton(dl, apply, mockW - 28f);

            // The tab bar under it: the second place the accent shows up, and the one people
            // actually look at all day.
            float barY = mockY + mockH + 18f;
            dl.AddRectFilled(new Vector2(mockX, barY), new Vector2(mockX + mockW, barY + barH),
                ImGui.ColorConvertFloat4ToU32(Panel), Radius.Card);

            (FontAwesomeIcon Icon, string Label)[] tabs =
            {
                (FontAwesomeIcon.Users, "Recruit"),
                (FontAwesomeIcon.Star, "My Profile"),
                (FontAwesomeIcon.Trophy, "Clears"),
                (FontAwesomeIcon.Cog, "Settings"),
            };

            for (int i = 0; i < tabs.Length; i++)
            {
                float cx = mockX + mockW * (i + 0.5f) / tabs.Length;
                Vector4 tint = i == 0 ? Accent : Faint;
                OnbIcon(dl, tabs[i].Icon, new Vector2(cx, barY + 22f), tint, OnbIconMidPx);

                using (UiLabelFont.Push())
                {
                    Vector2 ts = ImGui.CalcTextSize(tabs[i].Label);
                    OnbText(dl, new Vector2(cx - ts.X * 0.5f, barY + 34f), tint, tabs[i].Label);
                }
            }
        }

        /// <summary>
        /// The mark at the head of a preset card: the duty's category icon, from the game's own
        /// sheet.
        ///
        /// IT WAS THE LETTER "J". That is the plugin's fallback for a duty whose category cannot be
        /// resolved - the one place a letter ever stands in for an icon - and the figure was
        /// showing the failure state as though it were the normal one. The sample is an alliance
        /// raid, so it carries the Raids mark, and the letter is kept only where the real card
        /// keeps it: behind a sheet lookup that did not answer.
        /// </summary>
        private void OnbDutyTile(ImDrawListPtr dl, Vector2 at, float size)
        {
            var max = at + new Vector2(size, size);
            uint icon = GetCategoryIcon(OnbSampleCategory);

            if (icon != 0 && TryGetIconHandle(icon, out var handle))
            {
                dl.AddImage(handle, at, max);
                return;
            }

            dl.AddRectFilled(at, max, ImGui.ColorConvertFloat4ToU32(Panel), Radius.Tile);

            using (UiNameFont.Push())
            {
                string initial = OnbSampleDuty[..1].ToUpperInvariant();
                Vector2 ts = ImGui.CalcTextSize(initial);
                OnbText(dl, at + (new Vector2(size, size) - ts) * 0.5f, Dim, initial);
            }
        }

        /// <summary>The primary action, drawn twice in this run - once in the colour preview and
        /// once on the preset row - so it lives here rather than in both.</summary>
        private void OnbApplyButton(ImDrawListPtr dl, Vector2 at, float width)
        {
            at = OnbSnap(at);
            width = MathF.Round(width);

            dl.AddRectFilled(at, at + new Vector2(width, 36f),
                ImGui.ColorConvertFloat4ToU32(Accent), Radius.Control);

            using (OnbSmallBold.Push())
            {
                const string label = "Apply preset";
                Vector2 ts = ImGui.CalcTextSize(label);
                float cx = at.X + width * 0.5f;
                OnbText(dl, new Vector2(cx - ts.X * 0.5f + 10f, at.Y + (36f - ts.Y) * 0.5f), OnAccent, label);
                OnbIcon(dl, FontAwesomeIcon.Play, new Vector2(cx - ts.X * 0.5f - 3f, at.Y + 18f),
                    OnAccent);
            }
        }

#if PFP_RATINGS
        // ══════════════════════════════════════════════════════════
        //  CLEAR ANNOUNCEMENTS
        // ══════════════════════════════════════════════════════════

        /// <summary>Short names for the three faces. The Settings labels say which is the plugin's
        /// and which are the game's, which is the right thing to say there and too long for three
        /// segments in a 324px track.</summary>
        private static readonly string[] OnbFaceLabels = { "Roboto", "Axis", "Jupiter" };

        private const float OnbToggleRowH = 62f;

        /// <summary>
        /// The face the preview line is set in, following the typeface control.
        ///
        /// FIFTEEN PIXELS, NOT THE ANNOUNCEMENT'S TWENTY. The preview is a scaled-down picture of
        /// a screen, and at 20 the sentence is half again wider than the panel it has to sit in.
        /// Both game faces resolve to a cut LARGER than 15 and come down to it, which is a
        /// reduction rather than an enlargement and stays sharp; Roboto is built at exactly the
        /// size asked for, as everything else in this run is.
        ///
        /// ALL THREE ARE HELD AT ONCE, and none is ever thrown away. The first version built one
        /// and rebuilt it whenever the segments moved, mirroring EnsureAnnounceFonts - which is
        /// right for the real announcement, where the face changes once in a session from a click
        /// in Settings. Here the three segments sit beside the thing they change, and people press
        /// all of them: every press disposed a handle and asked the atlas for another, and the
        /// preview drew in the plugin's fallback face until it landed. The control looked like it
        /// was lagging a step behind the click. Three handles is a few hundred kilobytes of atlas
        /// and the switch is immediate.
        /// </summary>
        private IFontHandle OnbPreviewFace
        {
            get
            {
                int face = Math.Clamp(config.ClearAnnouncementFont, 0, OnbFaceLabels.Length - 1);

                if (face == AnnounceFacePlugin)
                    return OnbPreviewPluginFace;

                var slot = OnbPreviewGameFace(face);

                // Guarded rather than trusted, for the same reason the real announcement guards its
                // handle: one that is not ready yet does not draw at the size asked for, it draws
                // at Dalamud's default.
                return slot != null && slot.Available ? slot : OnbPreviewPluginFace;
            }
        }

        /// <summary>One of the two game faces at the preview's size, built on first ask and kept.
        /// Null if the atlas would not give us one.</summary>
        private IFontHandle? OnbPreviewGameFace(int face)
        {
            ref IFontHandle? slot = ref face == AnnounceFaceJupiter
                ? ref onbPreviewJupiter
                : ref onbPreviewAxis;

            if (slot != null)
                return slot;

            try
            {
                var family = face == AnnounceFaceJupiter
                    ? GameFontFamily.Jupiter
                    : GameFontFamily.Axis;

                slot = pluginInterface.UiBuilder.FontAtlas
                    .NewGameFontHandle(new GameFontStyle(family, OnbPreviewFacePx));
            }
            catch (Exception)
            {
                // Nothing to report and nothing to do about it: the plugin's own face draws this
                // frame and every frame after, at the right size.
                return null;
            }

            return slot;
        }

        /// <summary>Asks for both game faces up front, so the first press of a segment draws in the
        /// face it names rather than in the fallback. Called by PreloadFonts.</summary>
        private void PreloadOnboardingGameFaces()
        {
            _ = OnbPreviewGameFace(AnnounceFaceAxis);
            _ = OnbPreviewGameFace(AnnounceFaceJupiter);
        }

        private const float OnbPreviewFacePx = 15f;

        /// <summary>The plugin's own face at the preview's size, and the fallback for the other
        /// two. SemiBold, because that is the weight the real announcement is set in.</summary>
        private IFontHandle OnbPreviewPluginFace
            => Font(ref onbPreviewPlugin, OnbPreviewFacePx, FontWeight.SemiBold);

        /// <summary>
        /// The one step about something the plugin draws over the game rather than inside itself.
        ///
        /// It is asked here, before anybody has seen one, because an announcement that arrives
        /// unannounced mid-pull is the single most annoying thing this plugin can do - and the
        /// preview beside the switches is what makes "on" an informed answer rather than a default
        /// somebody finds out about later.
        /// </summary>
        private void DrawOnbAnnounce(ImDrawListPtr dl, Vector2 box)
        {
            float y = box.Y;
            y += OnbStepLabel(dl, new Vector2(box.X, y)) + 10f;

            using (OnbTitle.Push())
            {
                OnbText(dl, new Vector2(box.X, y), Ink, "Clear announcements");
                y += ImGui.GetTextLineHeight() * 1.12f + 10f;
            }

            using (OnbBody.Push())
            {
                y += OnbWrapped(dl,
                    "One line across the middle of your screen when somebody on the feed clears an "
                    + "Ultimate or a savage tier. Your own clears are never announced.",
                    new Vector2(box.X, y), OnbColW, Dim, ImGui.GetTextLineHeight() * 1.45f);
            }

            y += 16f;

            bool on = config.ClearAnnouncementsEnabled;

            if (OnbToggleRow(dl, new Vector2(box.X, y), "Announce other players' clears",
                    "On by default", on, live: true, "##OnbAnnounceOn"))
            {
                config.ClearAnnouncementsEnabled = !on;
                config.Save();
            }

            y += OnbToggleRowH + 8f;

            // The two below depend on the one above: with announcements off they are dimmed and
            // inert rather than hidden, so the shape of the step does not change under the cursor
            // as the switch is flipped.
            bool through = config.ClearAnnouncementClickThrough;

            if (OnbToggleRow(dl, new Vector2(box.X, y), "Click-through",
                    through
                        ? "On - clicks pass straight through to the game"
                        : "Off - keep the heart and the close button",
                    through, on, "##OnbAnnounceClick"))
            {
                config.ClearAnnouncementClickThrough = !through;
                config.Save();
            }

            y += OnbToggleRowH + 8f;

            const float faceH = 12f + 16f + 8f + 36f + 12f;
            OnbCardRect(dl, new Vector2(box.X, y), new Vector2(box.X + OnbColW, y + faceH),
                Panel, RuleHair, Radius.Card);

            ImGui.PushStyleVar(ImGuiStyleVar.Alpha, ImGui.GetStyle().Alpha * (on ? 1f : 0.4f));
            try
            {
                using (OnbBodyBold.Push())
                    OnbText(dl, new Vector2(box.X + 14f, y + 12f), Ink, "Typeface");

                int face = Math.Clamp(config.ClearAnnouncementFont, 0, OnbFaceLabels.Length - 1);
                ImGui.SetCursorPos(
                    new Vector2(box.X + 14f, y + 12f + 16f + 8f) - ImGui.GetWindowPos());

                if (DrawSegmentedControl("onbface", OnbFaceLabels, ref face, OnbColW - 28f))
                {
                    config.ClearAnnouncementFont = face;
                    config.Save();
                }
            }
            finally
            {
                ImGui.PopStyleVar();
            }

            y += faceH + 12f;

            using (OnbTiny.Push())
                OnbWrapped(dl, "Duration and placement live in Settings.",
                    new Vector2(box.X, y), OnbColW, Faint, ImGui.GetTextLineHeight() * 1.4f);

            DrawOnbAnnouncePreview(dl, box, on, through);
        }

        /// <summary>A whole row that is the switch: the label, the line under it and the track are
        /// one target, because a 42px capsule is a small thing to ask somebody to hit.</summary>
        /// <param name="live">False when the row is subordinate to a switch that is off. It is
        /// dimmed and stops reporting clicks, rather than disappearing and taking the layout with
        /// it.</param>
        private bool OnbToggleRow(ImDrawListPtr dl, Vector2 at, string title, string subtitle,
            bool value, bool live, string id)
        {
            var size = new Vector2(OnbColW, OnbToggleRowH);
            at = OnbSnap(at);
            bool clicked = OnbHit(id, at, size, out bool hot);
            Vector2 max = at + size;

            ImGui.PushStyleVar(ImGuiStyleVar.Alpha, ImGui.GetStyle().Alpha * (live ? 1f : 0.4f));
            try
            {
                dl.AddRectFilled(at, max, ImGui.ColorConvertFloat4ToU32(
                    live && hot ? ColorFromHex("#18181b") : Panel), Radius.Card);
                dl.AddRect(at, max, ImGui.ColorConvertFloat4ToU32(RuleHair), Radius.Card,
                    ImDrawFlags.None, 1f);

                using (OnbBodyBold.Push())
                    OnbText(dl, new Vector2(at.X + 14f, at.Y + 12f), Ink, title);

                using (OnbTiny.Push())
                    OnbText(dl, new Vector2(at.X + 14f, at.Y + 32f), Faint, subtitle);

                const float trackW = 42f, trackH = 25f, knob = 21f, inset = 2f;
                var track = OnbSnap(new Vector2(max.X - 14f - trackW,
                    at.Y + (OnbToggleRowH - trackH) * 0.5f));

                dl.AddRectFilled(track, track + new Vector2(trackW, trackH),
                    ImGui.ColorConvertFloat4ToU32(value ? Accent : ColorFromHex("#3a3a3c")),
                    Radius.Pill);

                float knobX = value ? track.X + trackW - knob - inset : track.X + inset;
                dl.AddCircleFilled(new Vector2(knobX + knob * 0.5f, track.Y + trackH * 0.5f),
                    knob * 0.5f, ImGui.ColorConvertFloat4ToU32(Ink));
            }
            finally
            {
                ImGui.PopStyleVar();
            }

            return clicked && live;
        }

        /// <summary>
        /// What it looks like over the game, drawn as a stand-in for the game rather than as a
        /// picture of it.
        ///
        /// The bars are a hotbar, a party list and a target plate at roughly the sizes and places
        /// they sit at on a real screen. It is not there to be pretty - it is there to answer "how
        /// much of my screen does this take" without anybody having to clear a fight to find out.
        /// </summary>
        private void DrawOnbAnnouncePreview(ImDrawListPtr dl, Vector2 box, bool on, bool through)
        {
            OnbPanel(dl, box, out Vector2 pMin, out Vector2 pMax);

            using (UiLabelFont.Push())
                OnbTracked(dl, "PREVIEW - OVER YOUR GAME",
                    new Vector2(pMin.X + 24f, pMin.Y + 24f), Faint, 1.1f);

            // Sixteen either side rather than twenty-four: the line inside has to hold a name, a
            // verb, a fight and two buttons in one row, and the panel is 454px wide to begin with.
            var gMin = OnbSnap(new Vector2(pMin.X + 16f, pMin.Y + 60f));
            var gMax = OnbSnap(new Vector2(pMax.X - 16f, pMax.Y - 24f - 34f));

            dl.AddRectFilled(gMin, gMax, ImGui.ColorConvertFloat4ToU32(ColorFromHex("#0b0d12")), 14f);
            dl.AddRect(gMin, gMax, ImGui.ColorConvertFloat4ToU32(ColorFromHex("#ffffff0f")), 14f,
                ImDrawFlags.None, 1f);

            dl.PushClipRect(gMin, gMax, true);

            float gw = gMax.X - gMin.X, gh = gMax.Y - gMin.Y;
            uint furniture = ImGui.ColorConvertFloat4ToU32(ColorFromHex("#1b2130"));

            dl.AddRectFilled(gMin, new Vector2(gMax.X, gMin.Y + gh * 0.38f),
                ImGui.ColorConvertFloat4ToU32(ColorFromHex("#12161f")));

            dl.AddRectFilled(new Vector2(gMin.X + 22f, gMin.Y + 16f),
                new Vector2(gMin.X + 148f, gMin.Y + 24f), furniture, Radius.Pill);
            dl.AddRectFilled(new Vector2(gMin.X + 22f, gMin.Y + 30f),
                new Vector2(gMin.X + 102f, gMin.Y + 36f), furniture, Radius.Pill);
            dl.AddRectFilled(new Vector2(gMax.X - 126f, gMin.Y + 16f),
                new Vector2(gMax.X - 22f, gMin.Y + 60f), furniture, Radius.Small);
            dl.AddRectFilled(new Vector2(gMin.X + gw * 0.5f - 150f, gMax.Y - 34f),
                new Vector2(gMin.X + gw * 0.5f + 150f, gMax.Y - 20f), furniture, Radius.Pill);

            float lineY = gMin.Y + gh * 0.30f;

            if (!on)
            {
                using (OnbSmall.Push())
                {
                    const string off = "Announcements off - nothing is drawn over your game.";
                    Vector2 ts = ImGui.CalcTextSize(off);
                    OnbText(dl, new Vector2(gMin.X + (gw - ts.X) * 0.5f, lineY), ColorFromHex("#5a5a60"), off);
                }

                dl.PopClipRect();
                OnbAnnounceHint(dl, pMin, pMax, on, through);
                return;
            }

            // SET IN THE FACE THE TYPEFACE CONTROL IS POINTING AT.
            //
            // It was set in the plugin's own face whatever the three segments said, which made the
            // one control on this step that has a visible consequence the one control with no
            // visible consequence. OnbPreviewFace builds the same family the real announcement
            // would use - see the note on it for why the size is 16 rather than the announcement's
            // own 20.
            const string who = "Kaji Yumi";
            const string mid = " cleared ";
            const string fight = "Futures Rewritten";

            var face = OnbPreviewFace;

            float whoW, midW, fightW, lineH;
            using (face.Push())
            {
                whoW = ImGui.CalcTextSize(who).X;
                midW = ImGui.CalcTextSize(mid).X;
                fightW = ImGui.CalcTextSize(fight).X;
                lineH = ImGui.GetTextLineHeight();
            }

            // The fight's own art before its name, exactly as the real announcement carries it -
            // and the first thing dropped if the line will not fit the preview.
            //
            // The three faces are not the same width, and Jupiter is a display face that runs
            // noticeably wider than Roboto at the same size. Rather than pick a sample short enough
            // for the widest of them and leave the other two looking sparse, the line is measured
            // and the art comes off when it has to. Nothing that carries meaning is ever dropped:
            // the sentence and the buttons are what the step is about.
            var art = ArtNamed("fru");
            float artSize = MathF.Round(lineH);
            float artAdvance = art != null ? artSize + 7f : 0f;

            float actionsW = through ? 0f : 12f + 22f + 6f + 22f;
            float boxH = lineH + 16f;
            float boxW = 13f * 2f + whoW + midW + artAdvance + fightW + actionsW;

            if (boxW > gw - 16f && art != null)
            {
                art = null;
                artAdvance = 0f;
                boxW = 13f * 2f + whoW + midW + fightW + actionsW;
            }

            var bMin = OnbSnap(new Vector2(gMin.X + (gw - boxW) * 0.5f, lineY));
            var bMax = bMin + new Vector2(boxW, boxH);

            dl.AddRectFilled(bMin, bMax,
                ImGui.ColorConvertFloat4ToU32(new Vector4(0.102f, 0.102f, 0.122f, 0.82f)),
                Radius.Control);

            float tx = bMin.X + 13f;
            float ty = bMin.Y + 8f;

            using (face.Push())
            {
                OnbText(dl, new Vector2(tx, ty), Ink, who);
                tx += whoW;

                OnbText(dl, new Vector2(tx, ty), Dim, mid);
                tx += midW;

                if (art != null)
                {
                    var artAt = OnbSnap(new Vector2(tx, bMin.Y + (boxH - artSize) * 0.5f));
                    dl.AddImage(art.Handle, artAt, artAt + new Vector2(artSize, artSize));
                    tx += artAdvance;
                }

                OnbText(dl, new Vector2(tx, ty), Ink, fight);
                tx += fightW;
            }

            if (!through)
            {
                OnbIcon(dl, FontAwesomeIcon.Heart,
                    new Vector2(tx + 12f + 11f, bMin.Y + boxH * 0.5f), Accent);
                OnbIcon(dl, FontAwesomeIcon.Times,
                    new Vector2(tx + 12f + 22f + 6f + 11f, bMin.Y + boxH * 0.5f), Faint);
            }

            dl.PopClipRect();
            OnbAnnounceHint(dl, pMin, pMax, on, through);
        }

        private void OnbAnnounceHint(ImDrawListPtr dl, Vector2 pMin, Vector2 pMax, bool on,
            bool through)
        {
            string hint = !on
                ? "You can turn these back on any time in Settings, under Clears."
                : through
                    ? "Click-through: the line ignores the mouse entirely, so a click meant for the "
                      + "game reaches the game."
                    : "Point at it to pause the countdown; click it to open the feed at that post "
                      + "and heart it.";

            using (OnbTiny.Push())
                OnbWrapped(dl, hint, new Vector2(pMin.X + 24f, pMax.Y - 24f - 24f),
                    OnbPanelW - 48f, Faint, ImGui.GetTextLineHeight() * 1.35f);
        }

        // ══════════════════════════════════════════════════════════
        //  YOUR PROFILE CARD
        // ══════════════════════════════════════════════════════════

        private void DrawOnbProfile(ImDrawListPtr dl, Vector2 box)
        {
            float y = box.Y;
            y += OnbStepLabel(dl, new Vector2(box.X, y)) + 10f;

            using (OnbTitle.Push())
            {
                OnbText(dl, new Vector2(box.X, y), Ink, "Your profile card");
                y += ImGui.GetTextLineHeight() * 1.12f + 12f;
            }

            using (OnbBody.Push())
            {
                float lh = ImGui.GetTextLineHeight() * 1.5f;

                y += OnbWrapped(dl,
                    "One number for how the community reads you, everything you have killed "
                    + "underneath it, and the parse you earned doing it.",
                    new Vector2(box.X, y), OnbColW, Dim, lh) + 12f;

                y += OnbWrapped(dl,
                    "Every player gets the same card. Right-click anybody in your party to open "
                    + "theirs.",
                    new Vector2(box.X, y), OnbColW, Dim, lh);
            }

            y += 22f;

            // The three sites the card links out to, with their own marks - drawn from the same
            // embedded PNGs the real card uses, so this is the row people will recognise.
            LinkSite[] sites = { LinkSite.Lodestone, LinkSite.Tomestone, LinkSite.FfLogs };
            string[] names = { "Lodestone", "Tomestone", "FFLogs" };

            // THE MARK AND ITS NAME SHARE A CENTRELINE, and the row has a height of its own.
            //
            // Both used to be drawn from the same top edge, which puts a 14px square and a 12px
            // word at two different heights - the icon sitting high and the name looking dropped.
            // The row is as tall as the taller of the two and each is centred in it, which is the
            // only arrangement where three of them read as one line.
            const float siteMark = 18f;
            float siteTextH;
            using (OnbTiny.Push())
                siteTextH = ImGui.GetTextLineHeight();

            float siteRowH = MathF.Max(siteMark, siteTextH);
            float siteMid = y + siteRowH * 0.5f;
            float x = box.X;

            for (int i = 0; i < sites.Length; i++)
            {
                var icon = EmbeddedTexture(ResourceFor(sites[i]));
                if (icon != null)
                {
                    var at = OnbSnap(new Vector2(x, siteMid - siteMark * 0.5f));
                    dl.AddImage(icon.Handle, at, at + new Vector2(siteMark, siteMark));
                    x += siteMark + 7f;
                }

                using (OnbTiny.Push())
                {
                    OnbText(dl, new Vector2(x, siteMid - siteTextH * 0.5f), Faint, names[i]);
                    x += ImGui.CalcTextSize(names[i]).X + 16f;
                }
            }

            // ── the card ──
            OnbPanel(dl, box, out Vector2 pMin, out _);

            // THE CARD IS AS TALL AS ITS CONTENT, not a constant that happened to fit once.
            //
            // The clear pills are sized by the words in them, and four savage floors with parses on
            // three of them do not fit one row of a 380px card - they wrap, and a card fixed at a
            // height measured before they wrapped would have run the second row out through its
            // own bottom edge. Both bands are measured here and the card is built around the total,
            // which is the same measure-then-draw discipline the real pill rows use.
            const float cardW = 380f;

            (string Name, string Slug, double Percentile)[] ultimates =
            {
                ("FRU", "fru", 99d),
                ("TOP", "top", 96d),
                ("DSR", "dsr", 78d),
            };

            // The last floor has no parse, which is a state the real card has and the figure needs:
            // a clear nobody logged is the neutral pill, not a seventh colour.
            (string Name, string Slug, double Percentile)[] savages =
            {
                ("M1S", "m1s", 100d),
                ("M2S", "m2s", 64d),
                ("M3S", "m3s", 41d),
                ("M4S", "m4s", double.NaN),
            };

            float bandOneH = OnbClearBandHeight("ultimate", ultimates, cardW);
            float bandTwoH = OnbClearBandHeight("savage", savages, cardW);

            // Everything above the first band, written as the sum the layout below actually walks
            // rather than as one number measured off it once. A constant here would be right until
            // the first time a row moved, and wrong silently afterwards - the failure being a card
            // whose bottom edge cuts through its own last row of pills.
            float smallLineH;
            using (OnbSmall.Push())
                smallLineH = ImGui.GetTextLineHeight();

            float aboveBands =
                50f                    // top padding and the row of site tiles
                + 30f                  // the job mark and the name
                + smallLineH + 16f     // world and job line
                + 54f + 10f            // the weighted score
                + 10f + 10f            // the bar under it
                + smallLineH + 14f     // the up and down counts
                + 12f;                 // the rule, and the air under it

            float cardH = MathF.Round(aboveBands + bandOneH + 12f + bandTwoH + 14f);
            float cardX = MathF.Round(pMin.X + (OnbPanelW - cardW) * 0.5f);
            float cardY = MathF.Round(pMin.Y + MathF.Max(0f, (OnbContentH - cardH) * 0.5f));

            OnbCardRect(dl, new Vector2(cardX, cardY), new Vector2(cardX + cardW, cardY + cardH),
                Field, CardBorder, Radius.Card);

            const float siteTile = 30f, siteTileMark = 20f;
            float ix = cardX + cardW - 14f;

            for (int i = sites.Length - 1; i >= 0; i--)
            {
                ix -= siteTile;
                var tile = new Vector2(ix, cardY + 14f);
                OnbCardRect(dl, tile, tile + new Vector2(siteTile, siteTile), Panel, RuleHair,
                    Radius.Small);

                var icon = EmbeddedTexture(ResourceFor(sites[i]));
                if (icon != null)
                {
                    float inset = (siteTile - siteTileMark) * 0.5f;
                    dl.AddImage(icon.Handle, tile + new Vector2(inset, inset),
                        tile + new Vector2(inset + siteTileMark, inset + siteTileMark));
                }

                ix -= 8f;
            }

            // The job mark beside the name, at 26 against a 24px name - the profile card's own
            // proportion, and the game's own icon rather than a coloured square standing in for it.
            float ny = cardY + 50f;
            OnbJobIcon(dl, OnbSampleJob, new Vector2(cardX + 14f, ny), 26f, RoleTank);

            using (UiPersonFont.Push())
                OnbText(dl, new Vector2(cardX + 48f, ny), Ink, "Mayo Botic");

            ny += 30f;

            using (OnbSmall.Push())
            {
                OnbText(dl, new Vector2(cardX + 14f, ny), Dim, "@Cerberus - Level 100 Gunbreaker");
                ny += ImGui.GetTextLineHeight() + 16f;
            }

            using (OnbScore.Push())
                OnbText(dl, new Vector2(cardX + 14f, ny), Positive, "127");

            using (OnbTiny.Push())
            {
                OnbText(dl, new Vector2(cardX + 104f, ny + 12f), Faint, "WEIGHTED");
                OnbText(dl, new Vector2(cardX + 104f, ny + 28f), Faint, "SCORE");
            }

            ny += 54f + 10f;

            // The bar is what the number means: the score against the ceiling it is measured on,
            // which a bare figure never says.
            dl.AddRectFilled(new Vector2(cardX + 14f, ny), new Vector2(cardX + cardW - 14f, ny + 10f),
                ImGui.ColorConvertFloat4ToU32(Raised), Radius.Pill);
            dl.AddRectFilled(new Vector2(cardX + 14f, ny),
                new Vector2(cardX + 14f + (cardW - 28f) * 0.88f, ny + 10f),
                ImGui.ColorConvertFloat4ToU32(Positive), Radius.Pill);

            ny += 10f + 10f;
            OnbTriangle(dl, new Vector2(cardX + 20f, ny + 7f), 11f, true, Positive);
            OnbTriangle(dl, new Vector2(cardX + 66f, ny + 7f), 11f, false, Negative);

            using (OnbSmall.Push())
            {
                OnbText(dl, new Vector2(cardX + 30f, ny), Dim, "142");
                OnbText(dl, new Vector2(cardX + 76f, ny), Dim, "15");
                ny += ImGui.GetTextLineHeight() + 14f;
            }

            dl.AddLine(new Vector2(cardX + 14f, ny), new Vector2(cardX + cardW - 14f, ny),
                ImGui.ColorConvertFloat4ToU32(RuleHair), 1f);
            ny += 12f;

            OnbClearBand(dl, cardX, cardW, ny, "ultimate", "ULTIMATE", "3 / 7", ultimates);
            OnbClearBand(dl, cardX, cardW, ny + bandOneH + 12f, "savage", "CURRENT SAVAGE TIER",
                "4 / 4", savages);
        }

        /// <summary>
        /// One band of the profile card: a small-caps heading with a count on the right, and the
        /// run of clear pills under it. Returns its height.
        ///
        /// THE PILLS ARE DRAWN THE WAY THE REAL ONES ARE, not approximated. They take their fill
        /// from <see cref="ParseColor"/> at the percentile given, their ink from
        /// <see cref="ReadableOn"/>, their mark from the fight's own totem, and their neutral
        /// state from <see cref="NoParseFill"/> - so a purple parse here is the same purple a
        /// purple parse is everywhere else in the plugin. The first version carried hand-picked
        /// hexes lifted off the mockup, which meant the one figure introducing the parse brackets
        /// was the one place in the plugin that did not use them.
        /// </summary>
        /// <param name="sectionKey">"ultimate" or "savage" - decides both the totem lookup and the
        /// glyph a fight with no totem falls back to, exactly as the real pill does.</param>
        /// <param name="clears">Name, slug, and percentile; NaN for a clear nobody logged.</param>
        private float OnbClearBand(ImDrawListPtr dl, float cardX, float cardW, float y,
            string sectionKey, string heading, string count,
            (string Name, string Slug, double Percentile)[] clears)
        {
            float headH;

            using (OnbTiny.Push())
            {
                OnbTracked(dl, heading, new Vector2(cardX + 14f, y), Dim, 1f);
                Vector2 cs = ImGui.CalcTextSize(count);
                OnbText(dl, new Vector2(cardX + cardW - 14f - cs.X, y), Faint, count);
                headH = cs.Y;
            }

            float room = cardW - 28f;
            float x = cardX + 14f;
            float pillY = y + headH + 6f;
            float pillH = OnbClearPillHeight();

            foreach (var (name, slug, pct) in clears)
            {
                float w = OnbClearPillWidth(sectionKey, name, slug, pct);

                // Wrapped the way the measure walks them, so the height reserved above is the
                // height actually used.
                if (x > cardX + 14f && x + w > cardX + 14f + room)
                {
                    x = cardX + 14f;
                    pillY += pillH + 6f;
                }

                OnbClearPill(dl, new Vector2(x, pillY), sectionKey, name, slug, pct);
                x += w + 6f;
            }

            return pillY + pillH - y;
        }

        /// <summary>How tall the band comes to once its pills have wrapped. Walks them exactly the
        /// way <see cref="OnbClearBand"/> draws them.</summary>
        private float OnbClearBandHeight(string sectionKey,
            (string Name, string Slug, double Percentile)[] clears, float cardW)
        {
            float headH;
            using (OnbTiny.Push())
                headH = ImGui.GetTextLineHeight();

            float pillH = OnbClearPillHeight();
            float room = cardW - 28f;
            float x = 0f;
            int rows = 1;

            foreach (var (name, slug, pct) in clears)
            {
                float w = OnbClearPillWidth(sectionKey, name, slug, pct);

                if (x > 0f && x + w > room)
                {
                    rows++;
                    x = 0f;
                }

                x += w + 6f;
            }

            return headH + 6f + rows * pillH + (rows - 1) * 6f;
        }

        private float OnbClearPillHeight()
        {
            using (UiPillFont.Push())
                return ImGui.GetTextLineHeight() + 8f;
        }

        /// <summary>The width one pill occupies, measured the way it will be drawn - same faces,
        /// same padding, same mark - so the two cannot disagree.</summary>
        private float OnbClearPillWidth(string sectionKey, string name, string slug,
            double percentile)
        {
            bool logged = !double.IsNaN(percentile);

            float lineH, nameW, parseW;
            using (UiPillFont.Push())
            {
                lineH = ImGui.GetTextLineHeight();
                nameW = ImGui.CalcTextSize(name).X;
                parseW = logged ? 5f + ImGui.CalcTextSize($"{Math.Floor(percentile):0}%").X : 0f;
            }

            return 7f * 2f + OnbPillMarkWidth(sectionKey, slug, lineH) + 5f + nameW + parseW;
        }

        /// <summary>How wide the mark at the head of a pill is: a totem keeps its own aspect at the
        /// pill's height, and anything without one falls back to the section's glyph.</summary>
        private float OnbPillMarkWidth(string sectionKey, string slug, float lineH)
        {
            var totem = FightTotem(sectionKey, slug);

            if (totem != null && totem.Height > 0)
                return MathF.Round((lineH + 4f) * totem.Width / totem.Height);

            using (UiIconSmall.Push())
                return ImGui.CalcTextSize(SectionIcon(sectionKey).ToIconString()).X;
        }

        /// <summary>One clear pill. Mirrors DrawPill in PluginUI.Clears.cs; see the note on
        /// <see cref="OnbClearBand"/> for why it is a mirror rather than a stand-in.</summary>
        private void OnbClearPill(ImDrawListPtr dl, Vector2 at, string sectionKey, string name,
            string slug, double percentile)
        {
            bool logged = !double.IsNaN(percentile);
            Vector4 fill = logged ? ParseColor(percentile) : NoParseFill;
            Vector4 ink = ReadableOn(fill);
            string parse = logged ? $"{Math.Floor(percentile):0}%" : string.Empty;

            float lineH, nameW;
            using (UiPillFont.Push())
            {
                lineH = ImGui.GetTextLineHeight();
                nameW = ImGui.CalcTextSize(name).X;
            }

            at = OnbSnap(at);
            float height = lineH + 8f;
            float width = OnbClearPillWidth(sectionKey, name, slug, percentile);

            // A totem keeps its own aspect at the pill's height rather than being squared off -
            // they are drawn objects, and squashing one to a glyph's proportions is both uglier and
            // smaller than the room allows.
            var totem = FightTotem(sectionKey, slug);
            float totemH = lineH + 4f;
            float markW = OnbPillMarkWidth(sectionKey, slug, lineH);

            var max = at + new Vector2(width, height);

            dl.AddRectFilled(at, max, ImGui.ColorConvertFloat4ToU32(fill), height * 0.5f);

            if (totem != null)
            {
                var topLeft = OnbSnap(new Vector2(at.X + 7f, at.Y + (height - totemH) * 0.5f));
                dl.AddImage(totem.Handle, topLeft, topLeft + new Vector2(markW, totemH));
            }
            else
            {
                // The glyph takes the parse's ink, so the pill reads as its bracket at a glance and
                // the number is confirmation rather than the only signal.
                OnbIcon(dl, SectionIcon(sectionKey),
                    new Vector2(at.X + 7f + markW * 0.5f, at.Y + height * 0.5f), ink);
            }

            float x = at.X + 7f + markW + 5f;

            using (UiPillFont.Push())
            {
                OnbText(dl, new Vector2(x, at.Y + 4f), ink, name);

                // Three-quarter strength, so the fight's name stays the first thing read.
                if (logged)
                    OnbText(dl, new Vector2(x + nameW + 5f, at.Y + 4f), ink with { W = 0.75f },
                        parse);
            }
        }

        // ══════════════════════════════════════════════════════════
        //  ONE PLAYER, ONE VOTE
        // ══════════════════════════════════════════════════════════

        private void DrawOnbVoting(ImDrawListPtr dl, Vector2 box)
        {
            float y = box.Y;
            y += OnbStepLabel(dl, new Vector2(box.X, y)) + 10f;

            using (OnbTitle.Push())
            {
                float lh = ImGui.GetTextLineHeight() * 1.12f;
                uint ink = ImGui.ColorConvertFloat4ToU32(Ink);
                OnbText(dl, new Vector2(box.X, y), ink, "One player,");
                OnbText(dl, new Vector2(box.X, y + lh), ink, "one vote");
                y += lh * 2f + 20f;
            }

            (string Lead, string Tail)[] points =
            {
                ("A score is a number of people, not a number of clicks.",
                 "Pressing the same arrow twice changes nothing."),
                ("Change your mind whenever.",
                 "The last thing you said is what counts."),
                ("Every vote weighs the same, from anybody.",
                 "The score is just the ups minus the downs."),
            };

            foreach (var (lead, tail) in points)
            {
                dl.AddCircleFilled(new Vector2(box.X + 3f, y + 8f), 3f,
                    ImGui.ColorConvertFloat4ToU32(Accent));
                y += OnbLeadIn(dl, new Vector2(box.X + 16f, y), OnbColW - 16f, lead, tail) + 12f;
            }

            y += 8f;

            using (OnbTiny.Push())
                OnbWrapped(dl, "You can only vote on someone whose duty you actually shared.",
                    new Vector2(box.X, y), OnbColW, Faint, ImGui.GetTextLineHeight() * 1.4f);

            // ── the party list, mid-vote ──
            OnbPanel(dl, box, out Vector2 pMin, out _);

            const float listW = 380f, rowH = 42f;
            float listX = MathF.Round(pMin.X + (OnbPanelW - listW) * 0.5f);
            const float listH = rowH * 3f + 24f;
            float listY = MathF.Round(pMin.Y + (OnbContentH - listH - 28f) * 0.5f);

            OnbCardRect(dl, new Vector2(listX, listY), new Vector2(listX + listW, listY + listH),
                Field, CardBorder, Radius.Card);

            // Job icons, not role squares - the real party list draws the game's own marks, and a
            // figure of a party list that does not is a figure of something else.
            (uint Job, Vector4 Role, string Score, Vector4 Tint, bool Rating)[] rows =
            {
                (OnbJobWhm, RoleHealer, "+12", Positive, false),
                (OnbJobBlm, RoleDPS, string.Empty, Dim, true),
                (OnbJobPld, RoleTank, "-3", Negative, false),
            };

            for (int i = 0; i < rows.Length; i++)
            {
                float top = listY + 12f + i * rowH;
                float mid = top + rowH * 0.5f;

                // The row being voted on is lifted and marked down its left edge - the same
                // treatment the real list gives it, so the pair of buttons reads as belonging to
                // that person rather than floating at the end of a list.
                if (rows[i].Rating)
                {
                    dl.AddRectFilled(new Vector2(listX + 1f, top),
                        new Vector2(listX + listW - 1f, top + rowH),
                        ImGui.ColorConvertFloat4ToU32(Raised), Radius.Small);
                    dl.AddRectFilled(new Vector2(listX + 1f, top), new Vector2(listX + 3f, top + rowH),
                        ImGui.ColorConvertFloat4ToU32(Accent), Radius.Pill);
                }

                OnbJobIcon(dl, rows[i].Job, new Vector2(listX + 18f, mid - 9f), 18f,
                    rows[i].Role);

                Vector4 bar = rows[i].Rating ? BorderControl : Raised;
                dl.AddRectFilled(new Vector2(listX + 46f, mid - 8f), new Vector2(listX + 150f, mid - 2f),
                    ImGui.ColorConvertFloat4ToU32(bar), Radius.Pill);
                dl.AddRectFilled(new Vector2(listX + 46f, mid + 2f), new Vector2(listX + 108f, mid + 7f),
                    ImGui.ColorConvertFloat4ToU32(bar with { W = 0.7f }), Radius.Pill);

                if (!rows[i].Rating)
                {
                    using (OnbSmallBold.Push())
                    {
                        Vector2 ts = ImGui.CalcTextSize(rows[i].Score);
                        OnbText(dl, new Vector2(listX + listW - 18f - ts.X, mid - ts.Y * 0.5f), rows[i].Tint, rows[i].Score);
                    }
                    continue;
                }

                OnbVoteButton(dl, new Vector2(listX + listW - 78f, mid - 13f), true, Positive);
                OnbVoteButton(dl, new Vector2(listX + listW - 44f, mid - 13f), false, BorderControl);
            }

            using (UiLabelFont.Push())
                OnbTracked(dl, "AFTER THE DUTY, ONE CLICK EACH",
                    new Vector2(listX, listY + listH + 14f), Faint, 1.1f);
        }

        /// <summary>
        /// A bullet whose first sentence carries the weight and the rest does not, wrapped as one
        /// paragraph. Returns its height.
        ///
        /// Weight rather than colour alone: three bullets of grey with a white phrase in each is
        /// still three paragraphs of grey, and the eye finds the heavier run first.
        /// </summary>
        private float OnbLeadIn(ImDrawListPtr dl, Vector2 at, float width, string lead, string rest)
        {
            float lh;
            using (OnbBody.Push())
                lh = ImGui.GetTextLineHeight() * 1.45f;

            float x = at.X, y = at.Y;
            int leadWords = lead.Split(' ').Length;
            string[] words = (lead + " " + rest).Split(' ');

            for (int i = 0; i < words.Length; i++)
            {
                bool leading = i < leadWords;

                using ((leading ? OnbBodyBold : OnbBody).Push())
                {
                    float w = ImGui.CalcTextSize(words[i] + " ").X;

                    if (x > at.X && x + w - at.X > width)
                    {
                        x = at.X;
                        y += lh;
                    }

                    OnbText(dl, new Vector2(x, y), leading ? Ink : Dim, words[i]);
                    x += w;
                }
            }

            return y - at.Y + lh;
        }

        private void OnbVoteButton(ImDrawListPtr dl, Vector2 at, bool up, Vector4 tint)
        {
            var size = new Vector2(26f, 26f);
            at = OnbSnap(at);
            OnbCardRect(dl, at, at + size, Field, tint, Radius.Small);
            OnbTriangle(dl, at + size * 0.5f, 12f, up, tint);
        }

        // ══════════════════════════════════════════════════════════
        //  THE CLEARS FEED
        // ══════════════════════════════════════════════════════════

        private void DrawOnbClears(ImDrawListPtr dl, Vector2 box)
        {
            float y = box.Y;
            y += OnbStepLabel(dl, new Vector2(box.X, y)) + 10f;

            using (OnbTitle.Push())
            {
                OnbText(dl, new Vector2(box.X, y), Ink, "The Clears feed");
                y += ImGui.GetTextLineHeight() * 1.12f + 12f;
            }

            using (OnbBody.Push())
            {
                float lh = ImGui.GetTextLineHeight() * 1.5f;

                y += OnbWrapped(dl,
                    "Clear an Ultimate or a savage tier and it goes up on the feed for everyone to "
                    + "see, with a heart on each. My clears keeps every one of yours in one place.",
                    new Vector2(box.X, y), OnbColW, Dim, lh) + 12f;

                y += OnbWrapped(dl,
                    "A clear counts whether it was logged or not - people who never upload logs "
                    + "don't read as having cleared nothing.",
                    new Vector2(box.X, y), OnbColW, Dim, lh);
            }

            y += 20f;

            using (OnbTiny.Push())
                OnbWrapped(dl,
                    "Broadcasting is a setting. Turning it off takes your existing posts down too.",
                    new Vector2(box.X, y), OnbColW, Faint, ImGui.GetTextLineHeight() * 1.4f);

            // ── the feed ──
            OnbPanel(dl, box, out Vector2 pMin, out _);

            // A TALLER POST THAN THE FIGURE FIRST HAD.
            //
            // At 92 the body was 58px and the 44px art was placed from a 14px top padding, which
            // put its bottom edge exactly on the hairline dividing the body from the heart and the
            // share. At 106 the body is 72 and the art is centred in it, so the same 14px of air
            // sits above and below - which is the margin the real card has.
            const float switchW = 270f, switchH = 34f, postH = 106f;
            float feedX = MathF.Round(pMin.X + 24f);
            float postW = OnbPanelW - 48f;
            const float total = switchH + 10f + postH + 10f + postH;
            float top = MathF.Round(pMin.Y + (OnbContentH - total) * 0.5f);

            // The two-up switch: it is how somebody finds their own clears, and it is the only
            // control on the real page, so it is the one thing on this figure that is not a post.
            dl.AddRectFilled(new Vector2(feedX, top), new Vector2(feedX + switchW, top + switchH),
                ImGui.ColorConvertFloat4ToU32(Field), Radius.Control);
            dl.AddRectFilled(new Vector2(feedX + 3f, top + 3f),
                new Vector2(feedX + switchW * 0.5f, top + switchH - 3f),
                ImGui.ColorConvertFloat4ToU32(Accent), Radius.Small);

            using (OnbTiny.Push())
            {
                Vector2 a = ImGui.CalcTextSize("Everybody");
                OnbText(dl, new Vector2(feedX + switchW * 0.25f + 1.5f - a.X * 0.5f,
                        top + (switchH - a.Y) * 0.5f), OnAccent, "Everybody");

                Vector2 b = ImGui.CalcTextSize("My clears");
                OnbText(dl, new Vector2(feedX + switchW * 0.75f - b.X * 0.5f, top + (switchH - b.Y) * 0.5f), Dim, "My clears");
            }

            top += switchH + 10f;

            OnbClearPost(dl, new Vector2(feedX, top), postW, "ultimate", "fru",
                OnbSampleFeedJobOne, "Rima Verdant", "Odin", "Futures Rewritten (Ultimate)",
                "FIRST CLEAR", badgeFilled: true, "Today 23:41", "128", hearted: true);

            top += postH + 10f;

            OnbClearPost(dl, new Vector2(feedX, top), postW, "savage", "m4s",
                OnbSampleFeedJobTwo, "Kaji Yumi", "Cerberus", "Cruiserweight tier - all four floors",
                "SAVAGE TIER", badgeFilled: false, "Yesterday 21:07", "41", hearted: false);
        }

        /// <param name="sectionKey">"ultimate" or "savage": which glyph stands in when the fight has
        /// no art of its own.</param>
        /// <param name="slug">The fight, for the art lookup - the same key the real feed uses, so
        /// this figure carries the real picture rather than a placeholder square.</param>
        /// <param name="jobId">The job they cleared it on. It belongs beside the person, which is
        /// where the real feed puts it and where every party list in the game puts it.</param>
        private void OnbClearPost(ImDrawListPtr dl, Vector2 at, float width, string sectionKey,
            string slug, uint jobId, string name, string world, string what, string badge,
            bool badgeFilled, string when, string hearts, bool hearted)
        {
            const float h = 106f, actionsH = 34f, artSize = 44f;
            Vector2 max = at + new Vector2(width, h);
            OnbCardRect(dl, at, max, Field, CardBorder, Radius.Card);

            // The body is what is left above the action bar, and everything in it is centred on
            // that - not measured down from the card's top edge, which is what put the art hard
            // against the divider.
            float bodyH = h - actionsH;
            float centreY = at.Y + bodyH * 0.5f;

            var artMin = OnbSnap(new Vector2(at.X + 14f, centreY - artSize * 0.5f));
            var artMax = artMin + new Vector2(artSize, artSize);

            dl.AddRectFilled(artMin, artMax, ImGui.ColorConvertFloat4ToU32(Panel), Radius.Tile);

            var art = ArtNamed(slug);
            if (art != null)
                dl.AddImage(art.Handle, artMin, artMax);
            else
                OnbIcon(dl, SectionIcon(sectionKey),
                    artMin + new Vector2(artSize * 0.5f, artSize * 0.5f), Dim, OnbIconLargePx);

            dl.AddRect(artMin, artMax, ImGui.ColorConvertFloat4ToU32(CardBorder), Radius.Tile,
                ImDrawFlags.None, 1f);

            // ── the two text lines, centred as a pair on the same body ──
            float nameH, smallH, fightH;
            using (OnbFeedName.Push())
                nameH = ImGui.GetTextLineHeight();
            using (UiLabelFont.Push())
                smallH = ImGui.GetTextLineHeight();
            using (OnbSmall.Push())
                fightH = ImGui.GetTextLineHeight();

            const float lineGap = 4f;
            float textX = artMax.X + 14f;
            float topY = centreY - (nameH + lineGap + fightH) * 0.5f;
            float tx = textX;

            const float jobMark = 18f;
            OnbJobIcon(dl, jobId, new Vector2(tx, topY + (nameH - jobMark) * 0.5f), jobMark, Dim);
            tx += jobMark + 7f;

            using (OnbFeedName.Push())
            {
                OnbText(dl, new Vector2(tx, topY), Ink, name);
                tx += ImGui.CalcTextSize(name).X + 8f;
            }

            using (UiLabelFont.Push())
                OnbText(dl, new Vector2(tx, topY + (nameH - smallH) * 0.5f), Faint, world);

            using (OnbSmall.Push())
                OnbText(dl, new Vector2(textX, topY + nameH + lineGap), badgeFilled ? Ink : Dim,
                    what);

            // ── the kind and the clock, as one stack centred on the same body ──
            using (UiLabelFont.Push())
            {
                const float chipH = 19f;
                float stackTop = centreY - (chipH + 6f + smallH) * 0.5f;
                float bw = ImGui.CalcTextSize(badge).X + 12f;
                var bAt = new Vector2(max.X - 14f - bw, stackTop);

                if (badgeFilled)
                {
                    OnbChip(dl, bAt, badge, Accent, OnAccent, chipH, 6f, Radius.Chip);
                }
                else
                {
                    dl.AddRect(bAt, bAt + new Vector2(bw, chipH),
                        ImGui.ColorConvertFloat4ToU32(BorderControl), Radius.Chip, ImDrawFlags.None,
                        1f);
                    OnbText(dl, new Vector2(bAt.X + 6f, bAt.Y + (chipH - smallH) * 0.5f), Dim,
                        badge);
                }

                Vector2 ws = ImGui.CalcTextSize(when);
                OnbText(dl, new Vector2(max.X - 14f - ws.X, stackTop + chipH + 6f), Faint, when);
            }

            float barY = at.Y + bodyH;
            dl.AddLine(new Vector2(at.X, barY), new Vector2(max.X, barY),
                ImGui.ColorConvertFloat4ToU32(RuleHair), 1f);
            dl.AddLine(new Vector2(at.X + width * 0.5f, barY), new Vector2(at.X + width * 0.5f, max.Y),
                ImGui.ColorConvertFloat4ToU32(RuleHair), 1f);

            using (OnbSmall.Push())
            {
                Vector4 heartTint = hearted ? Accent : Faint;
                Vector2 hs = ImGui.CalcTextSize(hearts);
                float cx = at.X + width * 0.25f;
                OnbIcon(dl, FontAwesomeIcon.Heart, new Vector2(cx - hs.X * 0.5f - 11f, barY + 17f),
                    heartTint);
                OnbText(dl, new Vector2(cx - hs.X * 0.5f + 4f, barY + 17f - hs.Y * 0.5f), heartTint, hearts);

                Vector2 ss = ImGui.CalcTextSize("Share");
                float sx = at.X + width * 0.75f;
                OnbIcon(dl, FontAwesomeIcon.ShareSquare,
                    new Vector2(sx - ss.X * 0.5f - 11f, barY + 17f), Faint);
                OnbText(dl, new Vector2(sx - ss.X * 0.5f + 4f, barY + 17f - ss.Y * 0.5f), Faint, "Share");
            }
        }
#endif

        // ══════════════════════════════════════════════════════════
        //  HERE'S A FREE PRESET
        // ══════════════════════════════════════════════════════════

        private void DrawOnbPreset(ImDrawListPtr dl, Vector2 box)
        {
            float y = box.Y;
            y += OnbStepLabel(dl, new Vector2(box.X, y)) + 10f;

            using (OnbTitle.Push())
            {
                OnbText(dl, new Vector2(box.X, y), Ink, "Here's a free preset");
                y += ImGui.GetTextLineHeight() * 1.12f + 12f;
            }

            using (OnbBody.Push())
            {
                float lh = ImGui.GetTextLineHeight() * 1.5f;

                y += OnbWrapped(dl,
                    "For a duty you have already unlocked. Press Apply and the plugin fills in the "
                    + "duty, objective, comment, seats, item level, loot rules, languages and "
                    + "password, then posts the listing for you.",
                    new Vector2(box.X, y), OnbColW, Dim, lh) + 12f;

                y += OnbWrapped(dl,
                    "No more setting up Party Finder again and again. Save it once, apply it with a "
                    + "single click.",
                    new Vector2(box.X, y), OnbColW, Dim, lh);
            }

            y += 22f;

            OnbIcon(dl, FontAwesomeIcon.InfoCircle, new Vector2(box.X + 7f, y + 7f), Faint);
            using (OnbTiny.Push())
                OnbWrapped(dl,
                    "There is an Apply button beside the game's own Recruit Members, too.",
                    new Vector2(box.X + 22f, y), OnbColW - 22f, Faint,
                    ImGui.GetTextLineHeight() * 1.4f);

            // ── the preset row ──
            OnbPanel(dl, box, out Vector2 pMin, out _);

            const float cardW = 380f, cardH = 230f;
            float cardX = MathF.Round(pMin.X + (OnbPanelW - cardW) * 0.5f);
            float cardY = MathF.Round(pMin.Y + (OnbContentH - cardH) * 0.5f);

            OnbCardRect(dl, new Vector2(cardX, cardY), new Vector2(cardX + cardW, cardY + cardH),
                Field, CardBorder, Radius.Card);

            var art = OnbSnap(new Vector2(cardX + 14f, cardY + 14f));
            OnbDutyTile(dl, art, 48f);

            float tx = cardX + 74f;

            using (UiNameFont.Push())
            {
                // No FREE PRESET badge. There is no such thing in the plugin - a preset is a preset
                // - and a chip inventing a tier the product does not have is the one line in this
                // run that would have to be un-learned later. A duty name is still ellipsised: it
                // is the one string on this figure that is genuinely variable in the real card.
                OnbText(dl, new Vector2(tx, cardY + 14f), Ink,
                    Fit(OnbSampleDuty, cardW - 74f - 14f));
            }

            using (UiHelpFont.Push())
                OnbText(dl, new Vector2(tx, cardY + 36f), Faint, OnbSampleSub);

            float chipX = tx;
            using (UiLabelFont.Push())
            {
                chipX += OnbChip(dl, new Vector2(chipX, cardY + 54f), "Duty Completion",
                    AccentAlpha(0.18f), Accent, 17f, 6f, Radius.Chip) + 6f;
                chipX += OnbChip(dl, new Vector2(chipX, cardY + 54f), "24 players",
                    ColorFromHex("#a8a8ad26"), Dim, 17f, 6f, Radius.Chip) + 6f;
                OnbChip(dl, new Vector2(chipX, cardY + 54f), "EN",
                    ColorFromHex("#a8a8ad26"), Dim, 17f, 6f, Radius.Chip);
            }

            // THE SEAT STRIP, WITH THE GAME'S OWN MARKS ON IT.
            //
            // It was eight flat role-coloured squares, which is what a seat strip looks like from
            // memory and not what it looks like in the plugin: the real one draws the job's icon
            // where a seat asks for a job and the role's icon where it asks for a role, and those
            // are the pictures somebody has to recognise when they meet a real preset. A square is
            // only the fallback for a texture that has not decoded yet.
            (uint Job, uint Role, Vector4 Tint)[] seats =
            {
                (OnbJobPld, IconRoleTank, RoleTank),
                (OnbJobWar, IconRoleTank, RoleTank),
                (OnbJobWhm, IconRoleHealer, RoleHealer),
                (OnbJobSch, IconRoleHealer, RoleHealer),
                (OnbJobMnk, IconRoleDps, RoleDPS),
                (OnbJobDrg, IconRoleDps, RoleDPS),
                (OnbJobBlm, IconRoleDps, RoleDPS),
                (0u, IconRoleDps, RoleFree),
            };

            float seatY = cardY + 82f;

            for (int i = 0; i < seats.Length; i++)
            {
                var seat = OnbSnap(new Vector2(cardX + 14f + i * 33f, seatY));

                // The last seat takes anybody, so it carries the role mark rather than a job.
                if (seats[i].Job == 0)
                    OnbRoleIcon(dl, seats[i].Role, seat, 28f, seats[i].Tint);
                else
                    OnbJobIcon(dl, seats[i].Job, seat, 28f, seats[i].Tint);
            }

            float rowY = seatY + 38f;
            float applyW = cardW - 28f - 3f * 44f;
            OnbApplyButton(dl, new Vector2(cardX + 14f, rowY), applyW);

            FontAwesomeIcon[] actions =
            {
                FontAwesomeIcon.Pen, FontAwesomeIcon.ShareSquare, FontAwesomeIcon.EllipsisV,
            };

            for (int i = 0; i < actions.Length; i++)
            {
                var slot = new Vector2(cardX + 14f + applyW + 8f + i * 44f, rowY);
                dl.AddRectFilled(slot, slot + new Vector2(36f, 36f),
                    ImGui.ColorConvertFloat4ToU32(Raised), Radius.Control);
                OnbIcon(dl, actions[i], slot + new Vector2(18f, 18f), Dim);
            }

            float commentY = rowY + 46f;
            dl.AddRectFilled(new Vector2(cardX + 14f, commentY),
                new Vector2(cardX + cardW - 14f, cardY + cardH - 14f),
                ImGui.ColorConvertFloat4ToU32(Raised), Radius.Small);

            using (UiLabelFont.Push())
                OnbTracked(dl, "COMMENT", new Vector2(cardX + 26f, commentY + 12f), Dim, 0.9f);

            using (OnbSmall.Push())
                OnbText(dl, new Vector2(cardX + 26f, commentY + 28f), Ink, "Jeuno farm, all welcome. Chest rules in party chat.");
        }

        // ══════════════════════════════════════════════════════════
        //  DONE
        // ══════════════════════════════════════════════════════════

        /// <summary>
        /// The summary, and the handover.
        ///
        /// It reads the config back rather than remembering what was pressed, so somebody who
        /// skipped the setup steps still sees what is actually in force - which is the whole reason
        /// Skip lands here instead of closing.
        /// </summary>
        private void DrawOnbDone(ImDrawListPtr dl, Vector2 box)
        {
            float left = MathF.Round(box.X + (OnbContentW - OnbNarrowW) * 0.5f);

            string title = onboardingRecommended ? "Recommended settings applied" : "You're set";
            string body = onboardingRecommended
                ? "The defaults are in place. Settings has all of it if you want to change "
                  + "anything - window, colour, announcements."
                : "Settings has the rest: announcement duration and placement, broadcasting your "
                  + "own clears, and everything the tour skipped.";

            float titleH, bodyH, leadLh;
            using (OnbDisplay.Push())
                titleH = ImGui.GetTextLineHeight();
            using (OnbLead.Push())
            {
                leadLh = ImGui.GetTextLineHeight() * 1.5f;
                bodyH = OnbWrap(body, OnbNarrowW).Count * leadLh;
            }

#if PFP_RATINGS
            const int summaryRows = 3;
#else
            const int summaryRows = 2;
#endif
            const float summaryRowH = 42f;
            float summaryH = 8f + summaryRows * summaryRowH;

            float footH;
            using (OnbTiny.Push())
                footH = ImGui.GetTextLineHeight();

            float total = 44f + 18f + titleH + 12f + bodyH + 24f + summaryH + 14f + footH;
            float y = box.Y + Math.Max(0f, (OnbContentH - total) * 0.5f);

            dl.AddRectFilled(new Vector2(left, y), new Vector2(left + 44f, y + 44f),
                ImGui.ColorConvertFloat4ToU32(Accent), 14f);
            OnbIcon(dl, FontAwesomeIcon.Check, new Vector2(left + 22f, y + 22f), OnAccent,
                OnbIconLargePx);

            y += 44f + 18f;

            using (OnbDisplay.Push())
            {
                OnbText(dl, new Vector2(left, y), Ink, title);
                y += titleH + 12f;
            }

            using (OnbLead.Push())
                y += OnbWrapped(dl, body, new Vector2(left, y), OnbNarrowW, Dim, leadLh);

            y += 24f;

            OnbCardRect(dl, new Vector2(left, y), new Vector2(left + OnbNarrowW, y + summaryH),
                Panel, RuleHair, 14f);

            string accentHex = string.IsNullOrWhiteSpace(config.AccentColorHex)
                ? DefaultAccentHex
                : config.AccentColorHex.Trim();
            string accentName = Array.Find(AccentChoices,
                c => string.Equals(c.Hex, accentHex, StringComparison.OrdinalIgnoreCase)).Name;

            float rowY = y + 4f;

            OnbSummaryRow(dl, left, rowY, "Window",
                $"{config.Device} - {DeviceMetrics.SizeLabel(config.Device)}", swatch: false);
            rowY += summaryRowH;

            OnbSummaryRule(dl, left, rowY);
            OnbSummaryRow(dl, left, rowY, "Accent",
                string.IsNullOrEmpty(accentName) ? accentHex : accentName, swatch: true);
            rowY += summaryRowH;

#if PFP_RATINGS
            string notif = !config.ClearAnnouncementsEnabled
                ? "Off"
                : config.ClearAnnouncementClickThrough ? "On - click-through" : "On";

            OnbSummaryRule(dl, left, rowY);
            OnbSummaryRow(dl, left, rowY, "Clear announcements", notif, swatch: false);
#endif

            y += summaryH + 14f;

            using (OnbTiny.Push())
                OnbWrapped(dl,
                    "Everything here is in Settings, under Appearance and Clears.",
                    new Vector2(left, y), OnbNarrowW, Faint, ImGui.GetTextLineHeight() * 1.4f);
        }

        private static void OnbSummaryRule(ImDrawListPtr dl, float left, float y)
            => dl.AddLine(new Vector2(left + 16f, y), new Vector2(left + OnbNarrowW - 16f, y),
                ImGui.ColorConvertFloat4ToU32(RuleHair), 1f);

        private void OnbSummaryRow(ImDrawListPtr dl, float left, float top, string label,
            string value, bool swatch)
        {
            using (OnbSmall.Push())
            {
                float mid = top + 21f;
                OnbText(dl, new Vector2(left + 16f, mid - ImGui.GetTextLineHeight() * 0.5f), Dim, label);

                Vector2 vs = ImGui.CalcTextSize(value);
                float x = left + OnbNarrowW - 16f - vs.X;
                OnbText(dl, new Vector2(x, mid - vs.Y * 0.5f), Ink, value);

                if (swatch)
                {
                    var at = new Vector2(x - 22f, mid - 7f);
                    dl.AddRectFilled(at, at + new Vector2(14f, 14f),
                        ImGui.ColorConvertFloat4ToU32(Accent), 5f);
                }
            }
        }
    }
}
