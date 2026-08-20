using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.ManagedFontAtlas;

namespace PfPresets
{
    /// <summary>
    /// The one-time welcome: a run of cards, each a picture with a headline and one sentence.
    ///
    /// Written against the way it is actually met - a window over a game somebody is trying to
    /// play. Nobody reads an essay in that moment, so no card carries a paragraph. The picture does
    /// the explaining and the sentence names what you just looked at.
    ///
    /// Every figure is drawn on the draw list rather than shipped as an image: sharp at any UI
    /// scale, themed for free, and no assets added to the plugin.
    /// </summary>
    public partial class PluginUI
    {
        private bool isWelcomeVisible;
        private int welcomePage;

        /// <summary>Drives the entrance: each card fades and lifts into place rather than snapping,
        /// so paging reads as movement between two things rather than a redraw.</summary>
        private float welcomeAnim;

        private IFontHandle? welcomeTitleFont;
        private IFontHandle? welcomeStepFont;
        private IFontHandle? welcomeBodyFont;
        private IFontHandle? welcomeArtFont;

        /// <summary>Built at the size they are drawn at. Scaling a smaller face up is what made the
        /// first version's headline blurry - see <see cref="Font"/>.</summary>
        private IFontHandle TitleFont => Font(ref welcomeTitleFont, 30f);
        private IFontHandle StepFont => Font(ref welcomeStepFont, 12f);
        private IFontHandle BodyFont => Font(ref welcomeBodyFont, 16f);
        private IFontHandle ArtFont => Font(ref welcomeArtFont, 15f);

        private void DisposeWelcomeFonts()
        {
            welcomeTitleFont?.Dispose();
            welcomeStepFont?.Dispose();
            welcomeBodyFont?.Dispose();
            welcomeArtFont?.Dispose();
            welcomeTitleFont = welcomeStepFont = welcomeBodyFont = welcomeArtFont = null;
        }

        /// <summary>Bumped when the welcome has something genuinely new to say. Bumping it shows the
        /// window again to everyone, so it is not something to do lightly.</summary>
        private const int WelcomeVersion = 1;

        /// <summary>
        /// Which cards this build has, in order.
        ///
        /// The ratings card is absent rather than blank in a build compiled without ratings: a card
        /// explaining a feature that isn't there would be the only page in the run you couldn't act
        /// on.
        /// </summary>
        private static readonly int[] WelcomeCards =
#if PFP_RATINGS
            { 0, 1, 2, 3, 4 };
#else
            { 0, 1, 2, 4 };
#endif

        private static int WelcomePages => WelcomeCards.Length;

        private static readonly Vector2 WelcomeSize = new(620, 540);

        private void MaybeShowWelcome()
        {
            if (config.WelcomeSeenVersion >= WelcomeVersion)
                return;

            OpenWelcome();
            config.WelcomeSeenVersion = WelcomeVersion;
            config.Save();
        }

        public void ShowWelcomeAgain() => OpenWelcome();

        private void OpenWelcome()
        {
            welcomePage = 0;
            welcomeAnim = 0f;
            isWelcomeVisible = true;
        }

        private void GoToWelcomePage(int page)
        {
            welcomePage = Math.Clamp(page, 0, WelcomePages - 1);
            welcomeAnim = 0f;
        }

        private void DrawWelcomeWindow()
        {
            if (!isWelcomeVisible)
                return;

            ImGui.SetNextWindowSize(WelcomeSize, ImGuiCond.Always);

            ImGui.PushStyleColor(ImGuiCol.WindowBg, BgOuter);
            ImGui.PushStyleColor(ImGuiCol.Border, BorderDefault);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 0f);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 1f);

            try
            {
                // No `ref open`, and that is the fix for a real bug: the previous version captured
                // isWelcomeVisible into a local before drawing, then assigned it back afterwards -
                // so Start and Skip set the flag false and the assignment immediately undid it.
                // Both buttons did nothing. With no title bar there is no close box to serve
                // anyway, so the flag is owned by the buttons alone.
                if (ImGui.Begin("###PfAnalysisWelcome",
                        ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoDocking
                        | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoTitleBar
                        | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoSavedSettings))
                {
                    welcomeAnim = Math.Min(1f, welcomeAnim + ImGui.GetIO().DeltaTime * 4.5f);
                    DrawWelcomeCard();
                }
            }
            finally
            {
                ImGui.End();
                ImGui.PopStyleVar(3);
                ImGui.PopStyleColor(2);
            }
        }

        private void DrawWelcomeCard()
        {
            Vector2 origin = ImGui.GetWindowPos();
            var dl = ImGui.GetWindowDrawList();
            float w = WelcomeSize.X, h = WelcomeSize.Y;

            int card = WelcomeCards[Math.Clamp(welcomePage, 0, WelcomePages - 1)];
            float ease = Ease(welcomeAnim);

            const float artH = 250f;

            // The art band gets its own darker ground with a wash of the accent through it, so the
            // figure sits on something rather than floating on the window colour.
            dl.AddRectFilledMultiColor(origin, new Vector2(origin.X + w, origin.Y + artH),
                ImGui.ColorConvertFloat4ToU32(ColorFromHex("#161d2b")),
                ImGui.ColorConvertFloat4ToU32(ColorFromHex("#161d2b")),
                ImGui.ColorConvertFloat4ToU32(ColorFromHex("#1d2a40")),
                ImGui.ColorConvertFloat4ToU32(ColorFromHex("#1d2a40")));

            DrawWelcomeGlow(dl, new Vector2(origin.X + w * 0.5f, origin.Y + artH * 0.52f), 150f);

            // Everything in the band lifts a few pixels as it settles, which is the whole animation.
            var artCentre = new Vector2(origin.X + w * 0.5f,
                origin.Y + artH * 0.5f + (1f - ease) * 14f);

            ImGui.PushStyleVar(ImGuiStyleVar.Alpha, Math.Max(0.02f, ease));
            using (ArtFont.Push())
                DrawWelcomeArt(dl, card, artCentre, ease);
            ImGui.PopStyleVar();

            dl.AddLine(new Vector2(origin.X, origin.Y + artH),
                new Vector2(origin.X + w, origin.Y + artH),
                ImGui.ColorConvertFloat4ToU32(BorderDefault), 1f);

            var (headline, line) = WelcomeCopy(card);

            // Step counter above the headline: small, spaced, and the only place a number appears.
            using (StepFont.Push())
            {
                CentredAt(dl, $"STEP {welcomePage + 1} OF {WelcomePages}",
                    origin.Y + artH + 26f, TextMuted, w, origin.X);
            }

            using (TitleFont.Push())
            {
                CentredAt(dl, headline, origin.Y + artH + 50f + (1f - ease) * 6f,
                    AccentBlue, w, origin.X);
            }

            using (BodyFont.Push())
            {
                ImGui.SetCursorPos(new Vector2(56f, artH + 104f));
                ImGui.PushTextWrapPos(w - 56f);
                ImGui.PushStyleColor(ImGuiCol.Text, TextSecondary);
                CentredWrapped(line, w - 112f);
                ImGui.PopStyleColor();
                ImGui.PopTextWrapPos();
            }

            DrawWelcomeFooter(origin, w, h);
            DrawWelcomeSkip(origin, w);
        }

        /// <summary>A soft radial bloom behind the figure, drawn as concentric rings because ImGui
        /// has no gradient brush. Cheap, and it stops the band reading as a flat rectangle.</summary>
        private static void DrawWelcomeGlow(ImDrawListPtr dl, Vector2 centre, float radius)
        {
            const int rings = 14;
            for (int i = rings; i > 0; i--)
            {
                float t = i / (float)rings;
                var tint = new Vector4(AccentBlue.X, AccentBlue.Y, AccentBlue.Z,
                    0.035f * (1f - t) * (1f - t));
                dl.AddCircleFilled(centre, radius * t, ImGui.ColorConvertFloat4ToU32(tint), 48);
            }
        }

        /// <summary>Headline and its one supporting sentence. If a card wants a second, it is two
        /// cards - that rule is what keeps this readable at a glance.</summary>
        private static (string Headline, string Line) WelcomeCopy(int page) => page switch
        {
            0 => ("Welcome to PF Analysis",
                  "Everything you already had, plus a clearer picture of the party you're about "
                  + "to spend your evening with."),
            1 => ("Post your listing in one click",
                  "Set up a Party Finder listing once, save it, and put the whole thing back up "
                  + "whenever you want it."),
            2 => ("Know who you're taking in",
                  "See how far each party member has actually got in the fight you're recruiting "
                  + "for, before the first pull."),
            3 => ("Raid with people who show up",
                  "Give the group a thumbs up or down when the duty ends. Everyone's score shows "
                  + "on your party list."),
            _ => ("You're set",
                  "Nothing here is compulsory - anything you don't want can be switched off in "
                  + "Settings."),
        };

        // ══════════════════════════════════════════════════════════
        //  THE FIGURES
        // ══════════════════════════════════════════════════════════

        private void DrawWelcomeArt(ImDrawListPtr dl, int page, Vector2 c, float ease)
        {
            switch (page)
            {
                case 0: DrawArtHero(dl, c, ease); break;
                case 1: DrawArtOneClick(dl, c, ease); break;
                case 2: DrawArtProgress(dl, c, ease); break;
#if PFP_RATINGS
                case 3: DrawArtRatings(dl, c, ease); break;
#endif
                default: DrawArtDone(dl, c, ease); break;
            }
        }

        /// <summary>The name, set large, over the three things the plugin is.</summary>
        private void DrawArtHero(ImDrawListPtr dl, Vector2 c, float ease)
        {
            using (TitleFont.Push())
            {
                Vector2 ts = ImGui.CalcTextSize("PF ANALYSIS");
                dl.AddText(new Vector2(c.X - ts.X * 0.5f, c.Y - 62f),
                    ImGui.ColorConvertFloat4ToU32(TextPrimary), "PF ANALYSIS");
            }

            dl.AddLine(new Vector2(c.X - 90f, c.Y - 22f), new Vector2(c.X + 90f, c.Y - 22f),
                ImGui.ColorConvertFloat4ToU32(AccentBlue), 2f);

            string[] pillars = { "PRESETS", "PROGRESS", "PLAYERS" };
            Vector4[] tints = { AccentBlue, AccentGreen, AccentPurple };

            for (int i = 0; i < pillars.Length; i++)
            {
                float x = c.X + (i - 1) * 150f;
                float pop = Ease(Math.Clamp((ease - i * 0.12f) / 0.7f, 0f, 1f));

                var boxMin = new Vector2(x - 62f, c.Y + 4f);
                var boxMax = new Vector2(x + 62f, c.Y + 62f);
                dl.AddRectFilled(boxMin, boxMax, ImGui.ColorConvertFloat4ToU32(BgCard), Radius.Card);
                dl.AddRect(boxMin, boxMax,
                    ImGui.ColorConvertFloat4ToU32(new Vector4(tints[i].X, tints[i].Y, tints[i].Z, 0.55f * pop)),
                    Radius.Card, ImDrawFlags.None, 1.5f);

                dl.AddRectFilled(new Vector2(boxMin.X + 14f, boxMin.Y + 12f),
                    new Vector2(boxMin.X + 14f + 96f * pop, boxMin.Y + 15f),
                    ImGui.ColorConvertFloat4ToU32(tints[i]), Radius.Pill);

                Vector2 ls = ImGui.CalcTextSize(pillars[i]);
                dl.AddText(new Vector2(x - ls.X * 0.5f, c.Y + 30f),
                    ImGui.ColorConvertFloat4ToU32(TextSecondary), pillars[i]);
            }
        }

        /// <summary>A saved preset, an arrow, and the listing it becomes with its seats filling.</summary>
        private void DrawArtOneClick(ImDrawListPtr dl, Vector2 c, float ease)
        {
            var leftMin = new Vector2(c.X - 210f, c.Y - 52f);
            var leftMax = new Vector2(c.X - 60f, c.Y + 52f);
            dl.AddRectFilled(leftMin, leftMax, ImGui.ColorConvertFloat4ToU32(BgCard), Radius.Card);
            dl.AddRect(leftMin, leftMax, ImGui.ColorConvertFloat4ToU32(BorderDefault), Radius.Card, ImDrawFlags.None, 1f);
            ArtLabel(dl, new Vector2(leftMin.X + 14f, leftMin.Y + 12f), "SAVED PRESET", TextMuted);

            for (int i = 0; i < 3; i++)
            {
                float y = leftMin.Y + 40f + i * 16f;
                dl.AddRectFilled(new Vector2(leftMin.X + 14f, y),
                    new Vector2(leftMin.X + 14f + (i == 2 ? 70f : 106f), y + 6f),
                    ImGui.ColorConvertFloat4ToU32(BorderHover), Radius.Pill);
            }

            float slide = Ease(Math.Clamp(ease / 0.8f, 0f, 1f));
            DrawArrowRight(dl, new Vector2(c.X - 22f + slide * 6f, c.Y), 26f, AccentBlue);

            var rightMin = new Vector2(c.X + 46f, c.Y - 52f);
            var rightMax = new Vector2(c.X + 210f, c.Y + 52f);
            dl.AddRectFilled(rightMin, rightMax, ImGui.ColorConvertFloat4ToU32(BgCard), Radius.Card);
            dl.AddRect(rightMin, rightMax,
                ImGui.ColorConvertFloat4ToU32(new Vector4(AccentGreen.X, AccentGreen.Y, AccentGreen.Z, 0.6f)),
                Radius.Card, ImDrawFlags.None, 1.5f);
            ArtLabel(dl, new Vector2(rightMin.X + 14f, rightMin.Y + 12f), "LISTING UP", AccentGreen);

            // Seats filling left to right as the card settles - the point of the whole figure.
            for (int i = 0; i < 4; i++)
            {
                float pop = Ease(Math.Clamp((ease - 0.25f - i * 0.13f) / 0.5f, 0f, 1f));
                var at = new Vector2(rightMin.X + 26f + i * 32f, rightMin.Y + 62f);
                dl.AddCircleFilled(at, 11f, ImGui.ColorConvertFloat4ToU32(BgCardExpanded));
                if (pop > 0.01f)
                {
                    dl.AddCircleFilled(at, 11f * pop,
                        ImGui.ColorConvertFloat4ToU32(i == 3 ? AccentBlue : AccentGreen));
                }
            }
        }

        /// <summary>The party, each row carrying the prog point the plugin fetches for them.</summary>
        private void DrawArtProgress(ImDrawListPtr dl, Vector2 c, float ease)
        {
            (string Label, Vector4 Tint, float Fill)[] rows =
            {
                ("Cleared", AccentGreen, 1.00f),
                ("P4  12%", AccentYellow, 0.82f),
                ("Cleared", AccentGreen, 1.00f),
                ("P3  61%", AccentYellow, 0.55f),
            };

            const float rowH = 34f;
            float top = c.Y - rows.Length * rowH * 0.5f;

            for (int i = 0; i < rows.Length; i++)
            {
                float pop = Ease(Math.Clamp((ease - i * 0.1f) / 0.65f, 0f, 1f));
                var min = new Vector2(c.X - 185f, top + i * rowH);
                var max = new Vector2(c.X + 185f, min.Y + rowH - 7f);

                dl.AddRectFilled(min, max, ImGui.ColorConvertFloat4ToU32(BgCard), Radius.Small);

                // Progress runs behind the row as a tinted wash, so the bar and the words are the
                // same statement rather than two.
                var wash = new Vector4(rows[i].Tint.X, rows[i].Tint.Y, rows[i].Tint.Z, 0.16f * pop);
                dl.AddRectFilled(min,
                    new Vector2(min.X + (max.X - min.X) * rows[i].Fill * pop, max.Y),
                    ImGui.ColorConvertFloat4ToU32(wash), Radius.Small, ImDrawFlags.RoundCornersLeft);

                dl.AddCircleFilled(new Vector2(min.X + 18f, (min.Y + max.Y) * 0.5f), 7f,
                    ImGui.ColorConvertFloat4ToU32(BorderHover));
                dl.AddRectFilled(new Vector2(min.X + 34f, (min.Y + max.Y) * 0.5f - 3f),
                    new Vector2(min.X + 34f + 86f, (min.Y + max.Y) * 0.5f + 3f),
                    ImGui.ColorConvertFloat4ToU32(BorderHover), Radius.Pill);

                Vector2 ts = ImGui.CalcTextSize(rows[i].Label);
                dl.AddText(new Vector2(max.X - ts.X - 14f, (min.Y + max.Y) * 0.5f - ts.Y * 0.5f),
                    ImGui.ColorConvertFloat4ToU32(pop > 0.6f ? rows[i].Tint : TextMuted),
                    rows[i].Label);
            }
        }

#if PFP_RATINGS
        /// <summary>
        /// The party list with a score on each row, and the vote pair on the one being rated.
        ///
        /// It was two arrows the height of a hand and a meter, which said "voting" without showing
        /// anything anyone would recognise later. Blown up that far the arrow is also just a big
        /// polygon - the shape reads at the 20px it is used at in the plugin and not at three times
        /// that. This is the surface people actually meet, at the size they meet it.
        /// </summary>
        private void DrawArtRatings(ImDrawListPtr dl, Vector2 c, float ease)
        {
            (string Score, Vector4 Tint)[] rows =
            {
                ("+12", Positive),
                ("", TextMuted),     // the one being rated - the pair sits here instead
                ("-3", Negative),
            };

            const float panelW = 400f, rowH = 42f;
            float top = c.Y - rows.Length * rowH * 0.5f;

            var panelMin = new Vector2(c.X - panelW * 0.5f, top - 12f);
            var panelMax = new Vector2(c.X + panelW * 0.5f, top + rows.Length * rowH + 12f);
            dl.AddRectFilled(panelMin, panelMax, ImGui.ColorConvertFloat4ToU32(BgCard), Radius.Card);
            dl.AddRect(panelMin, panelMax, ImGui.ColorConvertFloat4ToU32(BorderDefault), Radius.Card, ImDrawFlags.None, 1f);

            for (int i = 0; i < rows.Length; i++)
            {
                float pop = Ease(Math.Clamp((ease - i * 0.12f) / 0.6f, 0f, 1f));
                float mid = top + i * rowH + rowH * 0.5f;
                float left = panelMin.X + 18f;
                float right = panelMax.X - 18f;

                bool rating = i == 1;
                if (rating)
                {
                    dl.AddRectFilled(new Vector2(panelMin.X + 1f, top + i * rowH),
                        new Vector2(panelMax.X - 1f, top + (i + 1) * rowH),
                        ImGui.ColorConvertFloat4ToU32(BgCardExpanded), Radius.Small);
                    dl.AddRectFilled(new Vector2(panelMin.X + 1f, top + i * rowH),
                        new Vector2(panelMin.X + 3f, top + (i + 1) * rowH),
                        ImGui.ColorConvertFloat4ToU32(AccentBlue), Radius.Pill);
                }

                // A square for the job icon and two bars for a name and a world: the shape of a row
                // rather than a caption pretending to be one.
                dl.AddRectFilled(new Vector2(left, mid - 9f), new Vector2(left + 18f, mid + 9f),
                    ImGui.ColorConvertFloat4ToU32(BorderHover), Radius.Tile);
                dl.AddRectFilled(new Vector2(left + 28f, mid - 8f),
                    new Vector2(left + 28f + 104f * pop, mid - 2f),
                    ImGui.ColorConvertFloat4ToU32(BorderHover), Radius.Pill);
                dl.AddRectFilled(new Vector2(left + 28f, mid + 2f),
                    new Vector2(left + 28f + 62f * pop, mid + 7f),
                    ImGui.ColorConvertFloat4ToU32(RuleHair), Radius.Pill);

                if (!rating)
                {
                    Vector2 ss = ImGui.CalcTextSize(rows[i].Score);
                    dl.AddText(new Vector2(right - ss.X, mid - ss.Y * 0.5f),
                        ImGui.ColorConvertFloat4ToU32(pop > 0.6f ? rows[i].Tint : TextMuted),
                        rows[i].Score);
                    continue;
                }

                // The two buttons, at the size they are in the plugin. The up vote lights as the
                // card settles - the whole gesture the card is about, and the only motion in it.
                float press = Ease(Math.Clamp((ease - 0.45f) / 0.45f, 0f, 1f));
                DrawArtVoteButton(dl, new Vector2(right - 62f, mid - 13f), 26f, true,
                    Lerp4(BorderHover, Positive, press));
                DrawArtVoteButton(dl, new Vector2(right - 28f, mid - 13f), 26f, false, BorderHover);
            }

            ArtLabel(dl, new Vector2(panelMin.X, panelMax.Y + 14f), "AFTER THE DUTY, ONE CLICK EACH",
                TextMuted);
        }

        /// <summary>One vote button: a square with the plugin's own arrow inside it, at the plugin's
        /// own size.</summary>
        private static void DrawArtVoteButton(ImDrawListPtr dl, Vector2 min, float side, bool up,
            Vector4 tint)
        {
            var max = new Vector2(min.X + side, min.Y + side);
            uint col = ImGui.ColorConvertFloat4ToU32(tint);

            dl.AddRectFilled(min, max, ImGui.ColorConvertFloat4ToU32(BgCard), Radius.Small);
            dl.AddRect(min, max, col, Radius.Small, ImDrawFlags.None, 1.2f);

            float arrow = side * 0.52f;
            DrawRedditArrow(dl, new Vector2(min.X + (side - arrow) * 0.5f, min.Y + (side - arrow) * 0.5f),
                arrow, up, col);
        }
#endif

        /// <summary>
        /// Where everything is, ticked off one line at a time.
        ///
        /// The card used to be a big circle with a tick drawn into it, which is a lot of ceremony
        /// for "you pressed Next four times" - and a round badge in a design with no round anything.
        /// A last page is the one place somebody is still reading and has nothing to do, so it says
        /// the four things that are otherwise found by accident.
        /// </summary>
        private void DrawArtDone(ImDrawListPtr dl, Vector2 c, float ease)
        {
            string[] lines =
            {
                "/pfa opens PF Analysis",
                "Post a preset from the Party Finder itself",
                "Right-click anyone in your party for their profile",
                "Every switch is in Settings, this guide included",
            };

            const float rowH = 38f, box = 18f;
            float top = c.Y - lines.Length * rowH * 0.5f;
            float left = c.X - 190f;

            for (int i = 0; i < lines.Length; i++)
            {
                float pop = Ease(Math.Clamp((ease - i * 0.11f) / 0.55f, 0f, 1f));
                float mid = top + i * rowH + rowH * 0.5f;

                var min = new Vector2(left, mid - box * 0.5f);
                var max = new Vector2(left + box, mid + box * 0.5f);
                dl.AddRect(min, max,
                    ImGui.ColorConvertFloat4ToU32(new Vector4(AccentGreen.X, AccentGreen.Y,
                        AccentGreen.Z, 0.25f + 0.75f * pop)), Radius.Chip, ImDrawFlags.None, 1.4f);

                // Two strokes, written rather than stamped, inside the box rather than filling it.
                var a = new Vector2(min.X + 4f, mid);
                var b = new Vector2(min.X + 7.5f, mid + 4f);
                var d = new Vector2(max.X - 4f, mid - 5f);
                uint tick = ImGui.ColorConvertFloat4ToU32(AccentGreen);

                dl.AddLine(a, Vector2.Lerp(a, b, Math.Min(1f, pop * 2f)), tick, 2f);
                if (pop > 0.5f)
                    dl.AddLine(b, Vector2.Lerp(b, d, (pop - 0.5f) * 2f), tick, 2f);

                Vector2 ts = ImGui.CalcTextSize(lines[i]);
                dl.AddText(new Vector2(left + box + 16f, mid - ts.Y * 0.5f),
                    ImGui.ColorConvertFloat4ToU32(pop > 0.4f ? TextSecondary : TextMuted), lines[i]);
            }
        }

        /// <summary>Straight blend between two colours, for a control that lights up as the card
        /// settles.</summary>
        private static Vector4 Lerp4(Vector4 from, Vector4 to, float t)
            => new(from.X + (to.X - from.X) * t, from.Y + (to.Y - from.Y) * t,
                   from.Z + (to.Z - from.Z) * t, from.W + (to.W - from.W) * t);

        // ── figure helpers ────────────────────────────────────────

        private static void ArtLabel(ImDrawListPtr dl, Vector2 at, string text, Vector4 color)
            => dl.AddText(at, ImGui.ColorConvertFloat4ToU32(color), text);

        private static void DrawArrowRight(ImDrawListPtr dl, Vector2 at, float size, Vector4 color)
        {
            uint col = ImGui.ColorConvertFloat4ToU32(color);
            float h = size * 0.5f;
            dl.AddLine(new Vector2(at.X - size, at.Y), new Vector2(at.X + h * 0.5f, at.Y), col, 3.5f);
            dl.AddTriangleFilled(
                new Vector2(at.X + size, at.Y),
                new Vector2(at.X + h * 0.3f, at.Y - h * 0.8f),
                new Vector2(at.X + h * 0.3f, at.Y + h * 0.8f), col);
        }

        // ── chrome ────────────────────────────────────────────────

        /// <summary>
        /// Skip: a white slab in the top corner, and the only white thing in the window.
        ///
        /// It was grey text on a dark band, which is invisible unless you are hunting for it -
        /// and somebody who wants out of a guide is not in the mood to hunt. Nothing else in the
        /// plugin is white, so there is no chance of mistaking it for anything but the way out.
        /// </summary>
        private void DrawWelcomeSkip(Vector2 origin, float w)
        {
            if (welcomePage >= WelcomePages - 1)
                return;

            var size = new Vector2(84f, 32f);
            var pos = new Vector2(w - size.X - 18f, 18f);
            Vector2 at = origin + pos;

            ImGui.SetCursorPos(pos);
            ImGui.InvisibleButton("##WelcomeSkip", size);
            bool hot = ImGui.IsItemHovered();

            if (ImGui.IsItemClicked())
                isWelcomeVisible = false;

            var dl = ImGui.GetWindowDrawList();
            var max = at + size;

            dl.AddRectFilled(at, max, ImGui.ColorConvertFloat4ToU32(
                hot ? new Vector4(1f, 1f, 1f, 1f) : new Vector4(0.93f, 0.94f, 0.96f, 1f)),
                Radius.Control);

            using (BodyFont.Push())
            {
                const string label = "Skip";
                Vector2 ts = ImGui.CalcTextSize(label);
                dl.AddText(new Vector2(at.X + (size.X - ts.X) * 0.5f, at.Y + (size.Y - ts.Y) * 0.5f),
                    ImGui.ColorConvertFloat4ToU32(BgOuter), label);
            }
        }

        private void DrawWelcomeFooter(Vector2 origin, float w, float h)
        {
            var dl = ImGui.GetWindowDrawList();

            // Progress as a segmented rail rather than dots: it shows how far along you are and how
            // much is left in the same object, which dots only manage by being counted.
            const float railW = 190f, segGap = 5f;
            float segW = (railW - segGap * (WelcomePages - 1)) / WelcomePages;
            float railX = origin.X + (w - railW) * 0.5f;
            float railY = origin.Y + h - 44f;

            for (int i = 0; i < WelcomePages; i++)
            {
                var min = new Vector2(railX + i * (segW + segGap), railY);
                var max = new Vector2(min.X + segW, railY + 4f);
                bool done = i <= welcomePage;
                dl.AddRectFilled(min, max,
                    ImGui.ColorConvertFloat4ToU32(done ? AccentBlue : BorderDefault), Radius.Pill);
            }

            const float btnW = 112f, btnH = 32f;
            float btnY = h - 78f;

            if (welcomePage > 0)
            {
                ImGui.SetCursorPos(new Vector2(28f, btnY));
                if (DrawSecondaryButton("Back##WelcomeBack", new Vector2(btnW, btnH)))
                    GoToWelcomePage(welcomePage - 1);
            }

            bool last = welcomePage >= WelcomePages - 1;
            ImGui.SetCursorPos(new Vector2(w - btnW - 28f, btnY));

            if (DrawPrimaryButton(last ? "Start##WelcomeNext" : "Next##WelcomeNext",
                    new Vector2(btnW, btnH)))
            {
                if (last)
                    isWelcomeVisible = false;
                else
                    GoToWelcomePage(welcomePage + 1);
            }
        }

        /// <summary>Draws text centred in the window at an absolute Y, on the draw list so it is not
        /// subject to the cursor's layout.</summary>
        private static void CentredAt(ImDrawListPtr dl, string text, float y, Vector4 color,
            float windowWidth, float originX)
        {
            Vector2 ts = ImGui.CalcTextSize(text);
            dl.AddText(new Vector2(originX + (windowWidth - ts.X) * 0.5f, y),
                ImGui.ColorConvertFloat4ToU32(color), text);
        }

        private static void CentredWrapped(string text, float width)
        {
            Vector2 ts = ImGui.CalcTextSize(text, false, width);
            ImGui.SetCursorPosX((ImGui.GetWindowSize().X - Math.Min(ts.X, width)) * 0.5f);
            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + width);
            ImGui.TextUnformatted(text);
            ImGui.PopTextWrapPos();
        }
    }
}
