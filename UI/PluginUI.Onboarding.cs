using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.ManagedFontAtlas;

namespace PfPresets
{
    /// <summary>
    /// First run: a sheet of its own that asks the three questions worth asking and then shows what
    /// the plugin does.
    ///
    /// IT IS NOT A WINDOW INSIDE THE PLUGIN. The run that came before this was drawn on top of the
    /// preset list, which meant the first thing a new install showed was a guide over a surface the
    /// guide was about to explain - two things at once, neither readable. This one is the only thing
    /// on screen: the plugin's own windows are not drawn at all while it is up (see
    /// <see cref="OnboardingActive"/> and PluginUI.Draw), so there is one thing to look at and one
    /// thing to do.
    ///
    /// IT WRITES SETTINGS. The old run was eight pages of prose that changed nothing; the questions
    /// people actually have on a first launch - which shape is this window, what colour, is it going
    /// to draw over my game - were left to be discovered in Settings, which is to say not
    /// discovered. Three of the steps here are the real controls, applied as they are pressed, and
    /// the summary at the end says what they came out as.
    ///
    /// TWO PATHS. "Recommended" takes the defaults and skips straight to the tour; "Customise"
    /// walks the three setup steps first and joins the same tour. Nobody is made to answer
    /// questions to get to the thing they installed.
    ///
    /// Every figure is drawn on the draw list rather than shipped as an image: sharp at any UI
    /// scale, themed for free, and no assets added to the plugin.
    /// </summary>
    public partial class PluginUI
    {
        // ══════════════════════════════════════════════════════════
        //  STATE
        // ══════════════════════════════════════════════════════════

        /// <summary>
        /// Bumped when the onboarding has something genuinely new to ask. Bumping it runs the whole
        /// thing again for everybody, so it is not something to do lightly - a step that only
        /// explains something belongs in the changelog, not here.
        /// </summary>
        private const int OnboardingVersion = 1;

        /// <summary>The steps, in the order they are written. Not every run shows all of them - see
        /// <see cref="OnboardingSequence"/>.</summary>
        private enum OnbStep
        {
            /// <summary>The fork: recommended, or customise. Outside the numbered run.</summary>
            Welcome = 0,

            Window = 1,
            Colour = 2,
            Announce = 3,
            Profile = 4,
            Voting = 5,
            Clears = 6,
            Preset = 7,

            /// <summary>The summary, and the only exit.</summary>
            Done = 8,
        }

        private bool onboardingActive;
        private OnbStep onboardingStep;

        /// <summary>Which path is being walked. Decided on the welcome page and never again -
        /// pressing Back from the first tour step returns to the fork, which resets it.</summary>
        private bool onboardingRecommended;

        /// <summary>Whether the first-run check has run this session. The check itself is one
        /// integer comparison, but it must not fire again after the run has been closed, and it
        /// must not fire at all for somebody who opened it from Settings.</summary>
        private bool onboardingChecked;

        /// <summary>Drives the entrance: each step fades and lifts into place rather than snapping,
        /// so paging reads as movement between two things rather than a redraw.</summary>
        private float onboardingAnim;

        /// <summary>
        /// True while the run owns the screen.
        ///
        /// Read by <see cref="Draw"/>, which draws nothing else while it is set. Everything the
        /// plugin puts on screen - the window, the settings window, the Party Finder button, the
        /// rating prompts, the clear announcements - is suppressed, because a first-run guide
        /// competing with a notification over the game is not a first run anybody finishes.
        /// </summary>
        internal bool OnboardingActive => onboardingActive;

        // ── Geometry ──────────────────────────────────────────────
        //
        // Fixed, like the two plugin windows are. The sheet is drawn for exactly this size and for
        // no other, which is what lets every panel below be positioned by number.

        private const float OnbW = 900f;
        private const float OnbH = 612f;
        private const float OnbHeaderH = 64f;
        private const float OnbFooterH = 72f;
        private const float OnbPadX = 32f;
        private const float OnbPadTop = 30f;
        private const float OnbPadBottom = 24f;

        /// <summary>The left column on every two-column step. The right panel takes what is left,
        /// so a step never has to know how wide it is.</summary>
        private const float OnbColW = 352f;
        private const float OnbColGap = 30f;

        /// <summary>The centred column on the welcome and the summary. Narrower than the content
        /// box on purpose: a 40px headline set across 836px is a banner, not a sentence.</summary>
        private const float OnbNarrowW = 520f;

        private static float OnbContentTop => OnbHeaderH + 1f + OnbPadTop;
        private static float OnbContentH => OnbH - OnbFooterH - OnbContentTop - OnbPadBottom;
        private static float OnbContentW => OnbW - OnbPadX * 2f;
        private static float OnbPanelX => OnbPadX + OnbColW + OnbColGap;
        private static float OnbPanelW => OnbContentW - OnbColW - OnbColGap;

        // ── Faces ─────────────────────────────────────────────────
        //
        // Built at the size they are drawn at, never scaled. See PluginUI.Fonts.cs for why.

        private IFontHandle? onbDisplayFont, onbTitleFont, onbLeadFont, onbCardFont;
        private IFontHandle? onbBodyFont, onbBodyBoldFont, onbSmallFont, onbSmallBoldFont;
        private IFontHandle? onbTinyFont, onbScoreFont, onbFeedNameFont;

        /// <summary>The announcement preview's three faces, all held at once. See OnbPreviewFace
        /// in PluginUI.Onboarding.Steps.cs for why none of them is ever thrown away.</summary>
        private IFontHandle? onbPreviewPlugin, onbPreviewAxis, onbPreviewJupiter;

        /// <summary>The welcome and the summary headline, 40px.</summary>
        private IFontHandle OnbDisplay => Font(ref onbDisplayFont, 40f, FontWeight.SemiBold, userText: false);

        /// <summary>A step's headline, 32px.</summary>
        private IFontHandle OnbTitle => Font(ref onbTitleFont, 32f, FontWeight.SemiBold, userText: false);

        /// <summary>The sentence under a 40px headline. A step down from the headline and a step up
        /// from body copy, because it is the only line on those two pages.</summary>
        private IFontHandle OnbLead => Font(ref onbLeadFont, 15f, userText: false);

        private IFontHandle OnbCard => Font(ref onbCardFont, 15f, FontWeight.SemiBold, userText: false);
        private IFontHandle OnbBody => Font(ref onbBodyFont, 14f, userText: false);
        private IFontHandle OnbBodyBold => Font(ref onbBodyBoldFont, 14f, FontWeight.SemiBold, userText: false);
        private IFontHandle OnbSmall => Font(ref onbSmallFont, 13f);
        private IFontHandle OnbSmallBold => Font(ref onbSmallBoldFont, 13f, FontWeight.SemiBold);
        private IFontHandle OnbTiny => Font(ref onbTinyFont, 12f, userText: false);

        /// <summary>The weighted score on the sample profile, at 54px.
        ///
        /// NO GAME GLYPHS. Dalamud carries the game's face at five sizes and 54 is not one of them,
        /// and a handle that fails to build does not fall back to Roboto at the size asked for - it
        /// falls back to Dalamud's default, about 12px. A figure needs no fallback face anyway.
        /// </summary>
        private IFontHandle OnbScore
            => Font(ref onbScoreFont, 54f, FontWeight.SemiBold, userText: false, gameGlyphs: false);

        /// <summary>A name in the sample clears feed. Sixteen and regular, which is what a name is
        /// in the real feed - the one place in this file that draws something shaped like content
        /// rather than like a label.</summary>
        private IFontHandle OnbFeedName => Font(ref onbFeedNameFont, 16f);

        private void DisposeOnboardingFonts()
        {
            onbDisplayFont?.Dispose();
            onbTitleFont?.Dispose();
            onbLeadFont?.Dispose();
            onbCardFont?.Dispose();
            onbBodyFont?.Dispose();
            onbBodyBoldFont?.Dispose();
            onbSmallFont?.Dispose();
            onbSmallBoldFont?.Dispose();
            onbTinyFont?.Dispose();
            onbScoreFont?.Dispose();
            onbFeedNameFont?.Dispose();
            onbPreviewPlugin?.Dispose();
            onbPreviewAxis?.Dispose();
            onbPreviewJupiter?.Dispose();
            onbIconMid?.Dispose();
            onbIconLarge?.Dispose();
            onbDisplayFont = onbTitleFont = onbLeadFont = onbCardFont = null;
            onbBodyFont = onbBodyBoldFont = onbSmallFont = onbSmallBoldFont = null;
            onbTinyFont = onbScoreFont = onbFeedNameFont = null;
            onbPreviewPlugin = onbPreviewAxis = onbPreviewJupiter = null;
            onbIconMid = onbIconLarge = null;
        }

        // ══════════════════════════════════════════════════════════
        //  THE RUN
        // ══════════════════════════════════════════════════════════

        /// <summary>
        /// Which steps this run walks, in order. The welcome is not in it - it is the fork that
        /// chooses between the two.
        ///
        /// The three setup steps are the difference between the paths. The tour is the same either
        /// way, so somebody who took the defaults still learns what they got.
        ///
        /// A build compiled without ratings drops the four steps that describe features it does not
        /// have, rather than showing four pages nobody can act on.
        /// </summary>
        private OnbStep[] OnboardingSequence => onboardingRecommended
            ? OnboardingTour
            : OnboardingSetup;

        private static readonly OnbStep[] OnboardingSetup =
#if PFP_RATINGS
        {
            OnbStep.Window, OnbStep.Colour, OnbStep.Announce,
            OnbStep.Profile, OnbStep.Voting, OnbStep.Clears, OnbStep.Preset, OnbStep.Done,
        };
#else
        {
            OnbStep.Window, OnbStep.Colour, OnbStep.Preset, OnbStep.Done,
        };
#endif

        private static readonly OnbStep[] OnboardingTour =
#if PFP_RATINGS
        {
            OnbStep.Profile, OnbStep.Voting, OnbStep.Clears, OnbStep.Preset, OnbStep.Done,
        };
#else
        {
            OnbStep.Preset, OnbStep.Done,
        };
#endif

        /// <summary>
        /// Opens the run on a fresh install, once.
        ///
        /// Called from Draw rather than from the main window, which is the whole point: the run has
        /// to be able to appear for somebody who has never opened the plugin, and the old one could
        /// not, because it was drawn inside the window it was introducing.
        /// </summary>
        private void MaybeStartOnboarding()
        {
            if (onboardingChecked)
                return;

            onboardingChecked = true;

            if (config.OnboardingSeenVersion >= OnboardingVersion)
                return;

            StartOnboarding();
        }

        /// <summary>Runs it again from the top. What Settings' Replay button calls.</summary>
        public void ReplayOnboarding() => StartOnboarding();

        private void StartOnboarding()
        {
            // Checked before opening, so a replay from Settings does not re-arm the first-run check
            // and open it a second time on the next frame.
            onboardingChecked = true;

            onboardingStep = OnbStep.Welcome;
            onboardingRecommended = false;
            onboardingAnim = 0f;
            onboardingActive = true;

            // The plugin's own windows go away underneath. Not cosmetic: they are still ImGui
            // windows if left visible, they would still take focus and clicks, and the sheet is not
            // modal - it is simply the only thing drawn.
            isMainWindowVisible = false;
            isSettingsWindowVisible = false;
            isEditorWindowVisible = false;
            CloseSheet();
        }

        /// <summary>
        /// Ends the run and hands over to the plugin, opened on Settings.
        ///
        /// SETTINGS, not the preset list. The last thing the summary says is where all of this
        /// lives, and landing on the page it just named is the difference between being told and
        /// being shown.
        /// </summary>
        private void FinishOnboarding()
        {
            onboardingActive = false;

            config.OnboardingSeenVersion = OnboardingVersion;
            config.Save();

            activeTab = MainTab.Settings;
            isMainWindowVisible = true;
        }

        private void GoToOnboardingStep(OnbStep step)
        {
            onboardingStep = step;
            onboardingAnim = 0f;
        }

        /// <summary>
        /// Moves along the current path.
        ///
        /// Back off the first step of a path returns to the fork rather than stopping dead, and the
        /// fork resets the path - which is what makes "I picked the wrong one" recoverable without a
        /// second control saying so.
        /// </summary>
        private void StepOnboarding(int delta)
        {
            var seq = OnboardingSequence;
            int at = Array.IndexOf(seq, onboardingStep);

            if (at < 0)
            {
                GoToOnboardingStep(seq[0]);
                return;
            }

            int next = at + delta;

            if (next < 0)
            {
                onboardingRecommended = false;
                GoToOnboardingStep(OnbStep.Welcome);
                return;
            }

            GoToOnboardingStep(seq[Math.Min(seq.Length - 1, next)]);
        }

        // ══════════════════════════════════════════════════════════
        //  THE SHEET
        // ══════════════════════════════════════════════════════════

        private void DrawOnboarding()
        {
            if (!onboardingActive)
                return;

            var viewport = ImGui.GetMainViewport();
            var size = new Vector2(OnbW, OnbH);

            ImGui.SetNextWindowSize(size, ImGuiCond.Always);
            ImGui.SetNextWindowPos(viewport.GetCenter() - size * 0.5f, ImGuiCond.Appearing);

            ImGui.PushStyleColor(ImGuiCol.WindowBg, Ground);
            ImGui.PushStyleColor(ImGuiCol.Border, CardBorder);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, Radius.Screen);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 1f);

            try
            {
                // No `ref open`: there is no title bar and so no close box, and the only ways out
                // are Skip (which goes to the summary) and the summary's own button. A run that can
                // be dismissed by an accident is a run that gets dismissed by an accident.
                if (ImGui.Begin("###PfAnalysisOnboarding",
                        ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoDocking
                        | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoTitleBar
                        | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse
                        | ImGuiWindowFlags.NoSavedSettings))
                {
                    onboardingAnim = Math.Min(1f, onboardingAnim + ImGui.GetIO().DeltaTime * 4.5f);
                    DrawOnboardingSheet();
                }
            }
            finally
            {
                ImGui.End();
                ImGui.PopStyleVar(3);
                ImGui.PopStyleColor(2);
            }
        }

        private void DrawOnboardingSheet()
        {
            // SNAPPED TO WHOLE PIXELS, AND THIS IS WHY EVERYTHING ELSE IN THE SHEET IS SHARP.
            //
            // The window is centred on the viewport, so its origin lands on a half pixel whenever
            // the screen's width is odd - and every rectangle, ring and glyph in here is placed by
            // adding a constant to that origin, so a single fractional origin puts the whole sheet
            // half a pixel off the grid. Rounded corners come out chewed, hairlines come out two
            // grey pixels instead of one, and text is resampled across pixel boundaries, which is
            // exactly the "pixelated and not sharp" everything looked.
            //
            // Rounding it here fixes all of it at once, because nothing in the sheet is positioned
            // any other way. See also OnbText, which does the same for the few positions that are
            // computed rather than inherited.
            Vector2 o = OnbSnap(ImGui.GetWindowPos());
            var dl = ImGui.GetWindowDrawList();

            // ANTIALIASING, FORCED ON FOR THE WHOLE SHEET. This is the other half of "sharp".
            //
            // Snapping put every edge on the pixel grid; this is what makes the edges that are not
            // straight lines smooth. A rounded rectangle goes down through AddConvexPolyFilled,
            // which only feathers its outline when the draw list carries AntiAliasedFill, and a
            // stroked one through AddPolyline, which needs AntiAliasedLines. Dalamud does not
            // guarantee either - see the same note on the vote arrow in PluginUI.Voting.cs - and
            // without them every corner in here is a staircase.
            //
            // It is invisible on low-contrast shapes and obvious on high-contrast ones, which is
            // why the accent Apply button looked fine while the white Skip pill and the six
            // saturated colour swatches on black looked chewed: the steps were always there, and
            // only those had enough contrast to show them.
            //
            // Restored afterwards, because the draw list is shared with every other plugin drawing
            // this frame and leaving it changed would change how their shapes render too.
            var previousFlags = dl.Flags;
            dl.Flags |= ImDrawListFlags.AntiAliasedFill | ImDrawListFlags.AntiAliasedLines;

            try
            {
                DrawOnboardingSheetBody(o, dl);
            }
            finally
            {
                dl.Flags = previousFlags;
            }
        }

        private void DrawOnboardingSheetBody(Vector2 o, ImDrawListPtr dl)
        {
            DrawOnboardingHeader(o, dl);

            dl.AddLine(new Vector2(o.X, o.Y + OnbHeaderH), new Vector2(o.X + OnbW, o.Y + OnbHeaderH),
                ImGui.ColorConvertFloat4ToU32(Field), 1f);

            float ease = Ease(onboardingAnim);

            // The lift is the whole animation: content settles a few pixels as it fades in, and the
            // alpha is pushed rather than baked into every colour so a step body never has to know
            // it is animating.
            ImGui.PushStyleVar(ImGuiStyleVar.Alpha, Math.Max(0.02f, ease));

            // Snapped as well, so the settling animation moves a whole pixel at a time rather than
            // resampling every glyph in the step on the way in.
            var box = OnbSnap(new Vector2(o.X + OnbPadX, o.Y + OnbContentTop + (1f - ease) * 10f));

            try
            {
                switch (onboardingStep)
                {
                    case OnbStep.Welcome: DrawOnbWelcome(dl, box); break;
                    case OnbStep.Window: DrawOnbWindow(dl, box); break;
                    case OnbStep.Colour: DrawOnbColour(dl, box); break;
#if PFP_RATINGS
                    case OnbStep.Announce: DrawOnbAnnounce(dl, box); break;
                    case OnbStep.Profile: DrawOnbProfile(dl, box); break;
                    case OnbStep.Voting: DrawOnbVoting(dl, box); break;
                    case OnbStep.Clears: DrawOnbClears(dl, box); break;
#endif
                    case OnbStep.Preset: DrawOnbPreset(dl, box); break;
                    default: DrawOnbDone(dl, box); break;
                }
            }
            finally
            {
                ImGui.PopStyleVar();
            }

            DrawOnboardingFooter(o, dl);
        }

        /// <summary>The strip along the top: the mark, the name, the version, and the way out.</summary>
        private void DrawOnboardingHeader(Vector2 o, ImDrawListPtr dl)
        {
            var tile = OnbSnap(new Vector2(o.X + 24f, o.Y + (OnbHeaderH - 28f) * 0.5f));
            dl.AddRectFilled(tile, tile + new Vector2(28f, 28f),
                ImGui.ColorConvertFloat4ToU32(Accent), Radius.Small);

            // LogoIcon, not a glyph chosen here. It is the plugin's mark - the same one on the
            // sidebar's brand, on the applying-preset checklist and on the button added to the
            // game's own Party Finder - and the whole reason it is one constant is that somebody
            // has to be able to recognise the window that just opened as the thing they installed.
            // This sheet is the FIRST place anybody sees that mark, so it is the last place that
            // should have been carrying its own.
            OnbIcon(dl, LogoIcon, tile + new Vector2(14f, 14f), OnAccent, OnbIconMidPx);

            float x = o.X + 24f + 28f + 11f;

            using (UiTitleFont.Push())
            {
                const string name = "PF Analysis";
                Vector2 ts = ImGui.CalcTextSize(name);
                OnbText(dl, new Vector2(x, o.Y + (OnbHeaderH - ts.Y) * 0.5f), Ink, name);
                x += ts.X + 10f;
            }

            using (UiLabelFont.Push())
            {
                Vector2 ts = ImGui.CalcTextSize(VersionLabel);
                OnbText(dl, new Vector2(x, o.Y + (OnbHeaderH - ts.Y) * 0.5f + 1f), Faint, VersionLabel);
            }

            DrawOnboardingSkip(o, dl);
        }

        /// <summary>
        /// Skip: a white pill in the top corner, and the only white thing in the sheet.
        ///
        /// It goes to the summary rather than closing, so the run always ends the same way - the
        /// settings that are in force get said out loud even to somebody who skipped past choosing
        /// them, and there is exactly one place the plugin is handed over.
        /// </summary>
        private void DrawOnboardingSkip(Vector2 o, ImDrawListPtr dl)
        {
            if (onboardingStep == OnbStep.Welcome || onboardingStep == OnbStep.Done)
                return;

            var size = new Vector2(84f, 32f);
            var pos = new Vector2(OnbW - size.X - 20f, (OnbHeaderH - size.Y) * 0.5f);
            Vector2 at = OnbSnap(o + pos);

            ImGui.SetCursorPos(pos);
            ImGui.InvisibleButton("##OnbSkip", size);
            bool hot = ImGui.IsItemHovered();

            if (ImGui.IsItemClicked())
                GoToOnboardingStep(OnbStep.Done);

            dl.AddRectFilled(at, at + size, ImGui.ColorConvertFloat4ToU32(
                hot ? new Vector4(1f, 1f, 1f, 1f) : new Vector4(0.96f, 0.96f, 0.97f, 1f)),
                Radius.Pill);

            using (OnbSmallBold.Push())
            {
                const string label = "Skip";
                Vector2 ts = ImGui.CalcTextSize(label);
                OnbText(dl, at + (size - ts) * 0.5f, Ground, label);
            }
        }

        /// <summary>
        /// The rail on the left, Back and Next on the right.
        ///
        /// Progress as a segmented rail rather than dots: it shows how far along you are and how
        /// much is left in the same object, which dots only manage by being counted. The rail is
        /// the length of the path being walked, so the recommended path is visibly the shorter one.
        /// </summary>
        private void DrawOnboardingFooter(Vector2 o, ImDrawListPtr dl)
        {
            if (onboardingStep == OnbStep.Welcome)
                return;

            var seq = OnboardingSequence;
            int at = Array.IndexOf(seq, onboardingStep);
            float midY = o.Y + OnbH - OnbFooterH * 0.5f - 4f;

            const float segW = 24f, segH = 4f, segGap = 5f;
            for (int i = 0; i < seq.Length; i++)
            {
                var min = OnbSnap(new Vector2(o.X + OnbPadX + i * (segW + segGap),
                    midY - segH * 0.5f));
                dl.AddRectFilled(min, min + new Vector2(segW, segH),
                    ImGui.ColorConvertFloat4ToU32(i <= at ? Accent : RuleStrong), Radius.Pill);
            }

            bool last = onboardingStep == OnbStep.Done;
            string nextLabel = last ? "Open Settings" : "Next";

            const float btnH = 38f;
            float nextW = MathF.Round(OnbPillWidth(nextLabel));
            float nextX = OnbW - OnbPadX - nextW;

            if (OnbPill($"{nextLabel}##OnbNext", new Vector2(nextX, midY - btnH * 0.5f - o.Y),
                    new Vector2(nextW, btnH), Accent, AccentHover, OnAccent))
            {
                if (last)
                    FinishOnboarding();
                else
                    StepOnboarding(1);
            }

            float backW = MathF.Round(OnbPillWidth("Back", 26f));
            float backX = nextX - 10f - backW;

            if (OnbPill("Back##OnbBack", new Vector2(backX, midY - btnH * 0.5f - o.Y),
                    new Vector2(backW, btnH), Raised, ColorFromHex("#3a3a3c"), Ink))
                StepOnboarding(-1);
        }

        // ══════════════════════════════════════════════════════════
        //  SHARED PARTS
        // ══════════════════════════════════════════════════════════

        /// <summary>
        /// A point on the pixel grid.
        ///
        /// Applied to the sheet's origin and to anything centred - which is most of this file,
        /// because centring is a division by two and half of all widths are odd.
        /// </summary>
        private static Vector2 OnbSnap(Vector2 v) => new(MathF.Round(v.X), MathF.Round(v.Y));

        /// <summary>
        /// Text, on the pixel grid.
        ///
        /// EVERY STRING IN THE ONBOARDING GOES THROUGH HERE. ImGui draws a glyph run at exactly the
        /// position it is given, and a run that starts on a half pixel is resampled across the
        /// boundary for its whole length - which reads as soft, slightly smeared text rather than
        /// as anything obviously wrong, and is most of why the sheet looked unfocused. Nearly every
        /// label in here is centred or right-aligned, so nearly every one of them landed there.
        /// </summary>
        private static void OnbText(ImDrawListPtr dl, Vector2 at, uint colour, string text)
            => dl.AddText(OnbSnap(at), colour, text);

        /// <inheritdoc cref="OnbText(ImDrawListPtr, Vector2, uint, string)"/>
        private static void OnbText(ImDrawListPtr dl, Vector2 at, Vector4 colour, string text)
            => dl.AddText(OnbSnap(at), ImGui.ColorConvertFloat4ToU32(colour), text);

        /// <summary>How wide a footer pill has to be to hold its label at the padding the design
        /// uses. Measured rather than fixed: "Open Settings" and "Next" are not the same word.</summary>
        private float OnbPillWidth(string label, float padX = 30f)
        {
            using (OnbBodyBold.Push())
                return ImGui.CalcTextSize(label).X + padX * 2f;
        }

        /// <summary>A capsule button. The id suffix is stripped before the label is drawn, the way
        /// every other button helper in the plugin does it.</summary>
        private bool OnbPill(string label, Vector2 pos, Vector2 size, Vector4 fill, Vector4 hover,
            Vector4 ink)
        {
            int marker = label.IndexOf("##", StringComparison.Ordinal);
            string shown = marker >= 0 ? label.Substring(0, marker) : label;

            ImGui.SetCursorPos(pos);
            ImGui.InvisibleButton(marker >= 0 ? label.Substring(marker) : $"##{label}", size);
            bool hot = ImGui.IsItemHovered();
            bool clicked = ImGui.IsItemClicked();

            Vector2 at = OnbSnap(ImGui.GetWindowPos() + pos);
            var dl = ImGui.GetWindowDrawList();
            dl.AddRectFilled(at, at + size, ImGui.ColorConvertFloat4ToU32(hot ? hover : fill),
                Radius.Pill);

            using (OnbBodyBold.Push())
            {
                Vector2 ts = ImGui.CalcTextSize(shown);
                OnbText(dl, at + (size - ts) * 0.5f, ink, shown);
            }

            return clicked;
        }

        /// <summary>The panel the right-hand half of every two-column step is drawn inside.</summary>
        private void OnbPanel(ImDrawListPtr dl, Vector2 box, out Vector2 min, out Vector2 max)
        {
            min = OnbSnap(new Vector2(box.X + OnbPanelX - OnbPadX, box.Y));
            max = min + new Vector2(OnbPanelW, OnbContentH);

            dl.AddRectFilled(min, max, ImGui.ColorConvertFloat4ToU32(Panel), 22f);
            dl.AddRect(min, max, ImGui.ColorConvertFloat4ToU32(ColorFromHex("#ffffff14")), 22f,
                ImDrawFlags.None, 1f);
        }

        /// <summary>
        /// STEP n OF m, tracked out.
        ///
        /// ImGui has no letter-spacing, so it is drawn a character at a time with the advance added
        /// by hand. Worth the loop for one string per page: at 11px, small caps with no tracking
        /// read as a squashed word rather than as a label.
        /// </summary>
        private float OnbStepLabel(ImDrawListPtr dl, Vector2 at)
        {
            var seq = OnboardingSequence;
            int index = Array.IndexOf(seq, onboardingStep);
            string text = $"STEP {Math.Max(0, index) + 1} OF {seq.Length}";

            using (UiLabelFont.Push())
            {
                OnbTracked(dl, text, at, Faint, 1.8f);
                return ImGui.GetTextLineHeight();
            }
        }

        /// <summary>Draws a run with extra space between the characters, and returns where it
        /// ended.</summary>
        private static float OnbTracked(ImDrawListPtr dl, string text, Vector2 at, Vector4 colour,
            float tracking)
        {
            uint col = ImGui.ColorConvertFloat4ToU32(colour);
            float x = at.X;

            foreach (char ch in text)
            {
                string one = ch.ToString();
                OnbText(dl, new Vector2(x, at.Y), col, one);
                x += ImGui.CalcTextSize(one).X + tracking;
            }

            return x - tracking;
        }

        /// <summary>
        /// A paragraph, wrapped by hand onto the draw list.
        ///
        /// The cursor-based path would need a child window per column to get the wrap width right,
        /// and every panel here is positioned absolutely. Returns the height used so a caller can
        /// stack the next thing under it without knowing how many lines it came to.
        /// </summary>
        private static float OnbWrapped(ImDrawListPtr dl, string text, Vector2 at, float width,
            Vector4 colour, float lineHeight)
        {
            uint col = ImGui.ColorConvertFloat4ToU32(colour);
            float y = at.Y;

            foreach (string line in OnbWrap(text, width))
            {
                OnbText(dl, new Vector2(at.X, y), col, line);
                y += lineHeight;
            }

            return y - at.Y;
        }

        /// <summary>Greedy word wrap against the pushed face. Words longer than the column are left
        /// to overrun rather than broken - there are none in this file, and a hyphenator for copy we
        /// wrote ourselves is a lot of machinery for a case that cannot happen.</summary>
        private static System.Collections.Generic.List<string> OnbWrap(string text, float width)
        {
            var lines = new System.Collections.Generic.List<string>();
            string current = string.Empty;

            foreach (string word in text.Split(' '))
            {
                string candidate = current.Length == 0 ? word : current + " " + word;
                if (current.Length > 0 && ImGui.CalcTextSize(candidate).X > width)
                {
                    lines.Add(current);
                    current = word;
                }
                else
                {
                    current = candidate;
                }
            }

            if (current.Length > 0)
                lines.Add(current);

            return lines;
        }

        /// <summary>A rounded rectangle with a hairline border - the shape every mock card in the
        /// steps is built from.</summary>
        private static void OnbCardRect(ImDrawListPtr dl, Vector2 min, Vector2 max, Vector4 fill,
            Vector4 border, float radius)
        {
            // SNAPPED, AND THE BORDER IS WHY. A one-pixel stroke on a half-pixel edge is drawn as
            // two half-lit pixels - which at the density this file draws cards reads as a soft grey
            // haze around everything rather than as a hairline. The fill would survive the half
            // pixel; the stroke on top of it does not.
            min = OnbSnap(min);
            max = OnbSnap(max);

            dl.AddRectFilled(min, max, ImGui.ColorConvertFloat4ToU32(fill), radius);
            dl.AddRect(min, max, ImGui.ColorConvertFloat4ToU32(border), radius, ImDrawFlags.None, 1f);
        }

        /// <summary>A pill of text on a fill: a chip, a badge, a clear.</summary>
        private static float OnbChip(ImDrawListPtr dl, Vector2 at, string text, Vector4 fill,
            Vector4 ink, float height, float padX, float radius)
        {
            Vector2 ts = ImGui.CalcTextSize(text);
            float w = MathF.Round(ts.X + padX * 2f);

            at = OnbSnap(at);
            dl.AddRectFilled(at, at + new Vector2(w, height),
                ImGui.ColorConvertFloat4ToU32(fill), radius);
            OnbText(dl, new Vector2(at.X + padX, at.Y + (height - ts.Y) * 0.5f), ink, text);

            return w;
        }

        /// <summary>An icon from FontAwesome, centred on a point at the small icon size.</summary>
        /// <param name="px">Which of the three cached sizes to draw it at.
        ///
        /// SIZED TO WHAT IT SITS IN, rather than always at the 12px the plugin's body copy uses.
        /// Every icon in the sheet was drawn from the one small handle, which is right inside a
        /// 22px button and visibly lost inside a 36px tile or a 44px badge - the glyph reads as
        /// having been dropped into the middle of an empty square rather than as filling it. A
        /// glyph asked for at 22 and rasterised at 12 would be worse again: soft as well as small.
        /// </param>
        private void OnbIcon(ImDrawListPtr dl, FontAwesomeIcon icon, Vector2 centre, Vector4 colour,
            float px = OnbIconSmallPx)
        {
            var face = px >= OnbIconLargePx ? OnbIconLarge
                : px >= OnbIconMidPx ? OnbIconMid
                : UiIconSmall;

            using (face.Push())
            {
                string glyph = icon.ToIconString();
                Vector2 gs = ImGui.CalcTextSize(glyph);
                OnbText(dl, centre - gs * 0.5f, colour, glyph);
            }
        }

        private const float OnbIconSmallPx = 12f;
        private const float OnbIconMidPx = 17f;
        private const float OnbIconLargePx = 22f;

        private IFontHandle OnbIconMid => IconFont(ref onbIconMid, OnbIconMidPx);
        private IFontHandle OnbIconLarge => IconFont(ref onbIconLarge, OnbIconLargePx);
        private IFontHandle? onbIconMid, onbIconLarge;

        /// <summary>
        /// A job's own game icon, at a size, or the role's tile if the texture has not decoded yet.
        ///
        /// The figures used flat role-coloured squares where the plugin puts the game's own job
        /// icons - which made the profile card, the party list and the preset strip read as
        /// abstractions of the real thing rather than as the real thing. The point of every figure
        /// in this run is that meeting the surface later is recognition, and a blue square is not
        /// something anybody recognises.
        /// </summary>
        private void OnbJobIcon(ImDrawListPtr dl, uint jobId, Vector2 at, float size,
            Vector4 fallback)
        {
            var max = at + new Vector2(size, size);

            if (jobId > 0 && TryGetIconHandle(IconJobBase + jobId, out var handle))
            {
                dl.AddImage(handle, at, max);
                return;
            }

            dl.AddRectFilled(at, max, ImGui.ColorConvertFloat4ToU32(fallback), Radius.Tile);
        }

        /// <summary>The game's role mark, for a seat that asks for a role rather than a job. Falls
        /// back to the role's colour as a tile the same way the job icon does.</summary>
        private void OnbRoleIcon(ImDrawListPtr dl, uint roleIcon, Vector2 at, float size,
            Vector4 fallback)
        {
            var max = at + new Vector2(size, size);

            if (roleIcon != 0 && TryGetIconHandle(roleIcon, out var handle))
            {
                dl.AddImage(handle, at, max);
                return;
            }

            dl.AddRectFilled(at, max, ImGui.ColorConvertFloat4ToU32(fallback), Radius.Tile);
        }

        /// <summary>A solid triangle, up or down: the vote arrows, drawn the same way the real ones
        /// are rather than as a glyph that happens to look similar.</summary>
        private static void OnbTriangle(ImDrawListPtr dl, Vector2 centre, float size, bool up,
            Vector4 colour)
        {
            uint col = ImGui.ColorConvertFloat4ToU32(colour);
            centre = OnbSnap(centre);
            float h = size * 0.5f;

            if (up)
            {
                dl.AddTriangleFilled(
                    new Vector2(centre.X, centre.Y - h),
                    new Vector2(centre.X - h, centre.Y + h * 0.75f),
                    new Vector2(centre.X + h, centre.Y + h * 0.75f), col);
            }
            else
            {
                dl.AddTriangleFilled(
                    new Vector2(centre.X, centre.Y + h),
                    new Vector2(centre.X - h, centre.Y - h * 0.75f),
                    new Vector2(centre.X + h, centre.Y - h * 0.75f), col);
            }
        }
    }
}
