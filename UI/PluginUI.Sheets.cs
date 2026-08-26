using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;

namespace PfPresets
{
    /// <summary>
    /// Sheets: every prompt the plugin has, drawn inside the screen instead of beside it.
    ///
    /// A phone does not open a second window to ask you something. It slides a panel up over what
    /// you were doing, dims the thing behind it so it is obvious the panel is the only thing you can
    /// touch, and gives you a handle to pull it back down. That is what this file builds, and every
    /// dialog in the plugin - the editor, the job selector, share, import, the confirmations - is
    /// now one of them.
    ///
    /// HOW IT IS ACTUALLY DONE, and why it is not what it looks like. A sheet is a real ImGui
    /// window, positioned inside the main window's rectangle, not a region drawn into the main
    /// window's own draw list. Drawing it inline would have been fewer moving parts and would have
    /// been wrong: ImGui's hit-testing is per-window, so every control underneath the "sheet" would
    /// have gone on taking clicks through it, and the only fix is the modal machinery ImGui already
    /// has - which is per-window. Pinning a borderless window to the parent's rect gets real input
    /// isolation for free, and nothing on screen gives it away.
    ///
    /// The scrim underneath is a window too, for the same reason: it has to swallow the clicks that
    /// miss the sheet, and a rectangle in a draw list cannot.
    /// </summary>
    public partial class PluginUI
    {
        /// <summary>Where the screen was last frame. Sheets are placed against this, so they are
        /// always inside the phone however the player has moved it.</summary>
        private Vector2 screenPos;
        private Vector2 screenSize;

        /// <summary>Whether the main window drew at all this frame. A sheet whose owner is closed
        /// has nothing to sit inside and must not be drawn floating on its own.</summary>
        private bool screenRectValid;

        /// <summary>
        /// The sheet on screen, if any, and the one that was up last frame.
        ///
        /// One at a time, always. The old dialogs were independent windows and could stack four
        /// deep - the editor, the job selector over it, a confirmation over that - which on a phone
        /// is not a thing that exists. Opening a sheet from inside a sheet replaces it, and the one
        /// place that genuinely needs to go back (the job selector returns to the editor) says so
        /// explicitly.
        /// </summary>
        private SheetKind activeSheet = SheetKind.None;

        /// <summary>Set on the frame a sheet opens, so the scrim and the sheet can be raised above
        /// the main window exactly once. Focusing them every frame would tear the keyboard focus out
        /// of whatever text field the sheet contains, on every keystroke.</summary>
        private bool sheetNeedsFocus;

        /// <summary>Which prompt is up. Ordered as they appear in the plugin, not alphabetically.
        /// </summary>
        private enum SheetKind
        {
            None,
            Editor,
            JobSelector,
            ShareExport,
            ShareImport,
            Confirm,
            Changelog,
#if PFP_RATINGS
            Report,
            PollShare,
#endif
        }

        /// <summary>Called by the main window once it knows where it is.</summary>
        private void RecordScreenRect()
        {
            screenPos = ImGui.GetWindowPos();
            screenSize = ImGui.GetWindowSize();
            screenRectValid = true;
        }

        /// <summary>
        /// Raises a sheet. Idempotent: asking for the one already up does not re-focus it or
        /// restart its animation.
        /// </summary>
        private void OpenSheet(SheetKind kind)
        {
            if (activeSheet == kind)
                return;

            activeSheet = kind;
            sheetNeedsFocus = true;
        }

        /// <summary>Drops whichever sheet is up.</summary>
        private void CloseSheet() => activeSheet = SheetKind.None;

        private bool SheetOpen(SheetKind kind) => activeSheet == kind;

        // ══════════════════════════════════════════════════════════
        //  GEOMETRY
        // ══════════════════════════════════════════════════════════

        /// <summary>
        /// How tall a sheet is allowed to be, as a fraction of the screen.
        ///
        /// The phone leaves a strip of the body showing at the top. That strip is the affordance:
        /// it is what tells you the thing underneath is still there and that this panel is
        /// temporary. A sheet at full height is a screen, and a screen needs a back button.
        /// </summary>
        private const float PortraitSheetMaxFraction = 0.86f;

        /// <summary>The tablet centres its sheets and caps their width, the way a tablet does. Wider
        /// than a phone's, because the editor is a two-column surface and 460px is where its columns
        /// give up.</summary>
        private const float LandscapeSheetMaxWidth = 760f;
        private const float LandscapeSheetMaxFraction = 0.88f;

        private bool IsPortrait => config.Device == DeviceLayout.Portrait;

        /// <summary>Where a sheet of a given content height ends up. Height is requested, not
        /// demanded: a sheet never grows past its share of the screen, and never shrinks below
        /// something you can read.</summary>
        private (Vector2 Pos, Vector2 Size) SheetRect(float wantHeight)
        {
            if (IsPortrait)
            {
                float maxH = screenSize.Y * PortraitSheetMaxFraction;
                float h = Math.Clamp(wantHeight, 220f, maxH);
                return (new Vector2(screenPos.X, screenPos.Y + screenSize.Y - h),
                        new Vector2(screenSize.X, h));
            }

            float w = MathF.Min(LandscapeSheetMaxWidth, screenSize.X - 48f);
            float th = Math.Clamp(wantHeight, 240f, screenSize.Y * LandscapeSheetMaxFraction);
            return (new Vector2(screenPos.X + (screenSize.X - w) * 0.5f,
                                screenPos.Y + (screenSize.Y - th) * 0.5f),
                    new Vector2(w, th));
        }

        /// <summary>
        /// An alert is not a sheet, and it is the size iOS makes it.
        ///
        /// A confirmation used to come up as a bottom sheet: full screen width on the phone, 760px
        /// on the tablet, and never shorter than 220px however little it had to say. "Delete this
        /// preset?" arrived as a slab covering half the window - the size said "this is a screen",
        /// the content said "this is one sentence and two buttons", and the size is what people
        /// read first.
        ///
        /// 280px wide, centred on both axes, and exactly as tall as it needs to be. That is the
        /// system alert this design is modelled on, which has been that width since 2007 and does
        /// not grow with the display it is on: an alert is small BECAUSE it is important, and
        /// something small in the middle of a dimmed screen is the strongest thing an interface can
        /// point at.
        /// </summary>
        /// <summary>
        /// 320, not 280. An alert is centred text, and centred text in a narrow column breaks into
        /// a lot of very short lines - the ragged both-edges shape that makes a short message look
        /// like a wall of it. Forty more pixels is roughly two words more per line, which is the
        /// difference between five lines and three.
        /// </summary>
        private const float AlertWidth = 320f;

        /// <summary>Padding down the sides of an alert, and above its title.</summary>
        private const float AlertPad = 18f;

        /// <summary>Title to question. Enough that the heading reads as a heading rather than the
        /// first line of the message.</summary>
        private const float AlertTitleGap = 12f;

        /// <summary>Question to detail. The question is the statement and the detail qualifies it;
        /// at the old two pixels the pair ran together into one grey block.</summary>
        private const float AlertDetailGap = 10f;

        private const float AlertButtonHeight = 40f;
        private const float AlertButtonGap = 8f;

        /// <summary>The rule, the gap above the buttons, the buttons, and the gap below.</summary>
        private const float AlertFooterHeight = 1f + 12f + AlertButtonHeight + 14f;

        private (Vector2 Pos, Vector2 Size) AlertRect(float wantHeight)
        {
            float w = MathF.Min(AlertWidth, screenSize.X - 40f);
            float h = Math.Clamp(wantHeight, 120f, screenSize.Y - 60f);

            return (new Vector2(screenPos.X + (screenSize.X - w) * 0.5f,
                                screenPos.Y + (screenSize.Y - h) * 0.5f),
                    new Vector2(w, h));
        }

        // ══════════════════════════════════════════════════════════
        //  DRAWING
        // ══════════════════════════════════════════════════════════

        /// <summary>
        /// Draws the scrim and whichever sheet is up. Called once, last, from Draw - after every
        /// body has had its say, so the sheet is submitted on top of them.
        /// </summary>
        private void DrawSheetLayer()
        {
            SheetKind kind = activeSheet;

            if (kind == SheetKind.None || !screenRectValid)
                return;

            // THE SCRIM IS NOT DRAWN FOR A SHEET THAT HAS NOTHING BEHIND IT.
            //
            // A sheet's own draw call checks its state and closes itself if the thing it was about
            // has gone away - but by then the scrim is already on screen, and if something is
            // re-opening the sheet every frame the result is a permanent invisible modal that eats
            // every click in the window. That is not hypothetical: an unbraced `if` in the Vote tab
            // called OpenSheet on every frame the tab drew, and the window locked solid.
            //
            // Asking first costs one predicate and makes the whole failure mode impossible - a
            // sheet with no state behind it now closes silently, with nothing drawn.
            if (!SheetIsLive(kind))
            {
                CloseSheet();
                return;
            }

            bool dismissedByScrim = DrawScrim();

            switch (kind)
            {
                case SheetKind.Editor: DrawEditorSheet(); break;
                case SheetKind.JobSelector: DrawJobSelectorSheet(); break;
                case SheetKind.ShareExport: DrawShareExportSheet(); break;
                case SheetKind.ShareImport: DrawShareImportSheet(); break;
                case SheetKind.Confirm: DrawConfirmSheet(); break;
                case SheetKind.Changelog: DrawChangelogSheet(); break;
#if PFP_RATINGS
                case SheetKind.Report: DrawReportSheet(); break;
                case SheetKind.PollShare: DrawPollShareSheet(); break;
#endif
            }

            // Acted on after the sheet has drawn, so a click that lands on the sheet is consumed by
            // the sheet and never reaches this. Tapping away from a sheet closes it, which is what a
            // phone does - except for the editor, where it would throw away typing.
            if (dismissedByScrim)
                RequestSheetDismiss(kind, viaScrim: true);

            sheetNeedsFocus = false;
        }

        /// <summary>
        /// Whether the state a sheet draws from still exists.
        ///
        /// Every sheet is a view onto something else - a preset being edited, a code to copy, a
        /// question waiting for an answer - and none of them owns that thing. This is the one place
        /// that knows which flag belongs to which sheet.
        /// </summary>
        private bool SheetIsLive(SheetKind kind) => kind switch
        {
            SheetKind.Editor => isEditorWindowVisible && editingPreset != null,
            SheetKind.JobSelector => showJobSelector,
            SheetKind.ShareExport => isShareExportVisible,
            SheetKind.ShareImport => isShareImportVisible,
            SheetKind.Confirm => pendingConfirm != null,
            SheetKind.Changelog => isChangelogVisible,
#if PFP_RATINGS
            SheetKind.Report => reportTarget != null,
            SheetKind.PollShare => pollShareOpen,
#endif
            _ => false,
        };

        /// <summary>
        /// The dimmed layer over the body. Returns true when it was clicked.
        ///
        /// It covers the whole screen including the tab bar, deliberately. A tab bar you can still
        /// press while a prompt is open is a prompt you can walk away from and leave running.
        /// </summary>
        private bool DrawScrim()
        {
            ImGui.SetNextWindowPos(screenPos, ImGuiCond.Always);
            ImGui.SetNextWindowSize(screenSize, ImGuiCond.Always);

            // THE FLAG GOES ON ONE FRAME LATE, AND THAT IS THE WHOLE REASON THE WASH NEVER
            // APPEARED.
            //
            // NoBringToFrontOnFocus is what stops a click on the dimmed area raising the scrim over
            // the prompt it is meant to sit behind. What it ALSO does is stop SetNextWindowFocus
            // from raising it at all: ImGui's FocusWindow skips the display-order bring-to-front for
            // any window carrying it. So with the flag set from the start the scrim took focus and
            // stayed exactly where it already was in the z-order - underneath the main window,
            // covered by it, dimming nothing. It drew every frame and was never once visible.
            //
            // Splitting it in two gets both halves: the opening frame raises the scrim above the
            // window, the sheet is submitted immediately after and raises above the scrim, and from
            // the next frame the flag pins that order against anything the player clicks.
            ImGuiWindowFlags scrimFlags = SheetWindowFlags;
            if (sheetNeedsFocus)
                ImGui.SetNextWindowFocus();
            else
                scrimFlags |= ImGuiWindowFlags.NoBringToFrontOnFocus;

            // Neutral, and heavy. A wash tinted towards the old warm ground does nothing over
            // #000000 - the only thing it can darken is the content sitting on top.
            //
            // NO BLUR, AND THERE CANNOT BE ONE. A frosted backdrop means sampling what is already
            // on screen and running a kernel over it; ImGui hands out a draw list, not the frame
            // it ends up in, and there is no render target to read back. What a blur buys is
            // separation - the layer behind stops being readable and stops competing - and depth
            // is the other way of buying it, so the wash goes to where the body underneath is
            // present but not worth looking at.
            ImGui.PushStyleColor(ImGuiCol.WindowBg, ScrimWash);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, DeviceMetrics.ScreenRadius(config.Device));
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);

            bool clicked = false;
            try
            {
                if (ImGui.Begin("##PfaScrim", scrimFlags))
                {
                    ImGui.InvisibleButton("##scrimhit", screenSize);
                    clicked = ImGui.IsItemClicked();
                }
            }
            finally
            {
                ImGui.End();
                ImGui.PopStyleVar(3);
                ImGui.PopStyleColor();
            }

            return clicked;
        }

        /// <summary>
        /// The wash, shared by the scrim over the screen and the seal over every other surface.
        ///
        /// One constant because they are one layer conceptually - a prompt dims THE PLUGIN, not
        /// just the window it happened to open from - and two numbers that drifted apart would make
        /// the overlays look like a separate, lighter kind of unavailable.
        /// </summary>
        private static readonly Vector4 ScrimWash = new(0f, 0f, 0f, 0.78f);

        // ══════════════════════════════════════════════════════════
        //  BLOCKING THE REST OF THE PLUGIN
        // ══════════════════════════════════════════════════════════

        /// <summary>
        /// True while a prompt is up and nothing else this plugin draws may be touched.
        ///
        /// THE SCRIM WAS ONLY EVER HALF THE JOB. It covers the screen's rectangle, which makes the
        /// main window properly modal - but the plugin also draws seven windows anchored to the
        /// GAME rather than to the screen: the button beside Recruit Members, the Save as Preset
        /// button under a listing, the listing panel, the leader's score, the checklist, the
        /// welcome card and the rating prompt. All of those sit outside the scrim's rectangle, so a
        /// question waiting for an answer could be walked away from by pressing one of them - and
        /// several of them start work, which is exactly what a prompt is meant to hold back.
        ///
        /// Live-checked rather than just "a sheet is set". A sheet whose state has gone away closes
        /// itself on the next draw (see <see cref="SheetIsLive"/>); blocking on the flag alone would
        /// leave the whole plugin inert for a frame after the prompt was already over, and if
        /// anything ever re-opened a dead sheet every frame it would leave it inert for good.
        /// </summary>
        private bool PromptBlocksInput =>
            activeSheet != SheetKind.None && screenRectValid && SheetIsLive(activeSheet);

        /// <summary>
        /// What a game-anchored window adds to its flags while a prompt is up.
        ///
        /// NoInputs is the whole of it: it covers mouse, keyboard and nav in one, so the window
        /// cannot be clicked, dragged or tabbed into. NoBringToFrontOnFocus rides along for the
        /// same reason the main window takes it - a window that comes forward while a prompt is
        /// open can end up drawn over the prompt it was supposed to be waiting for.
        /// </summary>
        private ImGuiWindowFlags PromptBlockFlags => PromptBlocksInput
            ? ImGuiWindowFlags.NoInputs | ImGuiWindowFlags.NoBringToFrontOnFocus
            : ImGuiWindowFlags.None;

        /// <summary>
        /// Washes out the window currently being drawn, so it reads as unavailable rather than
        /// merely unresponsive.
        ///
        /// CALLED LAST, INSIDE THE WINDOW. It paints into that window's own draw list, which is
        /// appended to - so calling it at the end of the content puts it over the content, and
        /// keeps it correctly behind any window submitted later, including the prompt itself. A
        /// foreground draw list would have been one line shorter and would have painted over the
        /// prompt too.
        ///
        /// Style rounding rather than a constant: these windows round their corners by different
        /// amounts, and a square wash over a rounded card leaves four lit corners.
        /// </summary>
        private void SealOverlayIfPrompted()
        {
            if (!PromptBlocksInput)
                return;

            var dl = ImGui.GetWindowDrawList();
            Vector2 pos = ImGui.GetWindowPos();
            Vector2 size = ImGui.GetWindowSize();

            // WIDEN THE CLIP FIRST. While content is being submitted the draw list is clipped to
            // the window's inner region, so a wash drawn straight into it stops at the padding and
            // leaves a lit frame all the way round - which reads as a card that is still live.
            // Pushed and popped around the one rectangle so nothing else sees the wider clip.
            dl.PushClipRect(pos, pos + size, false);
            dl.AddRectFilled(pos, pos + size,
                ImGui.ColorConvertFloat4ToU32(ScrimWash), ImGui.GetStyle().WindowRounding);
            dl.PopClipRect();
        }

        private const ImGuiWindowFlags SheetWindowFlags =
            ImGuiWindowFlags.NoTitleBar
            | ImGuiWindowFlags.NoResize
            | ImGuiWindowFlags.NoMove
            | ImGuiWindowFlags.NoCollapse
            | ImGuiWindowFlags.NoScrollbar
            | ImGuiWindowFlags.NoScrollWithMouse
            | ImGuiWindowFlags.NoSavedSettings
            | ImGuiWindowFlags.NoDocking;

        /// <summary>
        /// Opens a sheet's window and draws its grabber, title and close button. The caller fills
        /// the body and must call <see cref="EndSheet"/> when <c>BeginSheet</c> returned true.
        /// </summary>
        /// <param name="wantHeight">Preferred height. Clamped by <see cref="SheetRect"/>.</param>
        private bool BeginSheet(string id, string title, float wantHeight)
        {
            var (pos, size) = SheetRect(wantHeight);

            ImGui.SetNextWindowPos(pos, ImGuiCond.Always);
            ImGui.SetNextWindowSize(size, ImGuiCond.Always);
            if (sheetNeedsFocus)
                ImGui.SetNextWindowFocus();

            // A SHEET IS PANEL, NOT GROUND. It used to take the window's own background, which
            // worked while that was #171614 and the scrim behind it went darker still. On a true
            // black ground a black sheet over a dimmed black body is one black rectangle: the
            // panel tone is what gives the sheet an edge without needing a stroke to find it.
            ImGui.PushStyleColor(ImGuiCol.WindowBg, Panel);
            ImGui.PushStyleColor(ImGuiCol.Border, RuleStrong);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, Radius.Sheet);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 1f);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);

            if (!ImGui.Begin($"##sheet{id}", SheetWindowFlags))
            {
                ImGui.End();
                ImGui.PopStyleVar(3);
                ImGui.PopStyleColor(2);
                return false;
            }

            DrawSheetHandle(title);
            return true;
        }

        /// <summary>
        /// Opens an alert's window. Small, centred, no grabber and no close cross - an alert is
        /// answered, not dismissed, and a third way out of a two-answer question is one more thing
        /// to reason about for no gain. Escape and a tap on the scrim still count as "no", because
        /// both are already routed through <see cref="RequestSheetDismiss"/>.
        ///
        /// Unwound with <see cref="EndSheet"/>, which pops exactly what this pushes.
        /// </summary>
        private bool BeginAlert(string id, float wantHeight)
        {
            var (pos, size) = AlertRect(wantHeight);

            ImGui.SetNextWindowPos(pos, ImGuiCond.Always);
            ImGui.SetNextWindowSize(size, ImGuiCond.Always);
            if (sheetNeedsFocus)
                ImGui.SetNextWindowFocus();

            ImGui.PushStyleColor(ImGuiCol.WindowBg, Panel);
            ImGui.PushStyleColor(ImGuiCol.Border, RuleStrong);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, Radius.Sheet);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 1f);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);

            if (!ImGui.Begin($"##sheet{id}", SheetWindowFlags))
            {
                ImGui.End();
                ImGui.PopStyleVar(3);
                ImGui.PopStyleColor(2);
                return false;
            }

            return true;
        }

        /// <summary>
        /// The grabber and the title row.
        ///
        /// The grabber is drawn on the tablet too, where a centred sheet has nothing to be dragged
        /// down from. It is not there to be used - it is there because it is the shape people have
        /// been taught means "this is a panel over something, and it goes away".
        /// </summary>
        private void DrawSheetHandle(string title)
        {
            var dl = ImGui.GetWindowDrawList();
            Vector2 p = ImGui.GetCursorScreenPos();
            float width = ImGui.GetContentRegionAvail().X;

            const float grabW = 38f, grabH = 4f;
            var g = new Vector2(p.X + (width - grabW) * 0.5f, p.Y + 10f);
            dl.AddRectFilled(g, new Vector2(g.X + grabW, g.Y + grabH),
                ImGui.ColorConvertFloat4ToU32(BorderControl), Radius.Pill);

            float rowTop = p.Y + 10f + grabH + 8f;
            const float rowH = 30f;

            using (UiHeadingFont.Push())
            {
                float lineH = ImGui.GetTextLineHeight();
                dl.AddText(new Vector2(p.X + 16f, rowTop + (rowH - lineH) * 0.5f),
                    ImGui.ColorConvertFloat4ToU32(Ink), title);
            }

            const float btn = 28f;
            ImGui.SetCursorScreenPos(new Vector2(p.X + width - btn - 16f, rowTop + (rowH - btn) * 0.5f));
            DrawSheetCloseButton();

            ImGui.SetCursorScreenPos(new Vector2(p.X, rowTop + rowH + 10f));
            DrawRuleHair();
            ImGui.Dummy(new Vector2(0, 2));
        }

        /// <summary>The sheet's own close. One shared control - see DrawIconSquareButton - so the
        /// cross on a sheet, on the rating prompt and on the checklist are the same object.</summary>
        private void DrawSheetCloseButton()
        {
            if (DrawIconSquareButton(FontAwesomeIcon.Times, "sheetclose", 28f))
                RequestSheetDismiss(activeSheet, viaScrim: false);
        }

        private void EndSheet()
        {
            ImGui.End();
            ImGui.PopStyleVar(3);
            ImGui.PopStyleColor(2);
        }

        /// <summary>
        /// The body region of an open sheet, minus room for a footer.
        ///
        /// Every sheet body scrolls, without exception. A phone sheet is 774px tall at most and the
        /// editor is not, and a body that runs past the bottom of a sheet with no scrollbar is the
        /// single most common way this pattern fails.
        /// </summary>
        private bool BeginSheetBody(float footerHeight)
        {
            ImGui.SetCursorPosX(16f);
            float h = ImGui.GetContentRegionAvail().Y - footerHeight;
            return ImGui.BeginChild("SheetBody",
                new Vector2(ImGui.GetWindowWidth() - 32f, MathF.Max(1f, h)), false);
        }

        private static void EndSheetBody() => ImGui.EndChild();

        /// <summary>
        /// A sheet's footer: one accent action and one way out, side by side and equal widths.
        ///
        /// Equal widths on purpose. Save and Cancel are two answers to the same question, and a
        /// design that makes one of them a wide button and the other a link is one that has decided
        /// on the player's behalf.
        /// </summary>
        private const float SheetFooterHeight = 62f;

        private bool DrawSheetFooter(string primaryLabel, string secondaryLabel,
            out bool secondaryClicked, bool primaryEnabled = true)
        {
            var size = BeginSheetFooter();

            if (!primaryEnabled) ImGui.BeginDisabled();
            bool primary = DrawPrimaryButton(primaryLabel, size);
            if (!primaryEnabled) ImGui.EndDisabled();

            ImGui.SameLine(0, SheetFooterGap);
            secondaryClicked = DrawSecondaryButton(secondaryLabel, size);

            return primary;
        }

        private const float SheetFooterButtonHeight = 38f;
        private const float SheetFooterGap = 10f;

        /// <summary>
        /// Rules off the footer and parks the cursor on its first button. Returns the size each of
        /// the two buttons gets.
        ///
        /// EVERY POSITION HERE IS ABSOLUTE, measured back from the bottom of the sheet. Laid out by
        /// flowing - a rule, then a spacer, then the buttons - the footer's real height is the sum
        /// of those plus an ItemSpacing after each one, which is three gaps nobody counted; the
        /// buttons ended up past the bottom edge and were clipped in half.
        /// </summary>
        private Vector2 BeginSheetFooter()
        {
            float h = ImGui.GetWindowHeight();

            ImGui.SetCursorPos(new Vector2(0, h - SheetFooterHeight));
            DrawRuleHair();

            float w = (ImGui.GetWindowWidth() - 32f - SheetFooterGap) * 0.5f;
            ImGui.SetCursorPos(new Vector2(16f, h - SheetFooterButtonHeight - 12f));

            return new Vector2(w, SheetFooterButtonHeight);
        }

        /// <summary>
        /// What "get me out of here" means for each sheet.
        ///
        /// Routed through one place because it is asked from three: the close button, a click on the
        /// scrim, and Escape. Those used to be three separate answers per dialog and they disagreed
        /// - closing the editor by its window X cancelled the edit, while clicking away from it did
        /// nothing at all.
        /// </summary>
        /// <param name="viaScrim">True when the ask was a click on the dimmed area rather than on a
        /// control. The distinction exists for one sheet: the editor holds unsaved typing, and a tap
        /// beside a panel is far too easy to make by accident to be allowed to throw a preset away.
        /// Its X and Escape still cancel, because both of those are deliberate.</param>
        private void RequestSheetDismiss(SheetKind kind, bool viaScrim)
        {
            switch (kind)
            {
                case SheetKind.Editor:
                    if (!viaScrim)
                        CancelEditor();
                    return;

                case SheetKind.JobSelector:
                    // Back to what opened it, not out to the body. The job selector is a step inside
                    // the editor, and closing it should return you to the thing you were editing.
                    CancelJobSelector();
                    return;

                case SheetKind.Confirm:
                    DismissConfirmDialog();
                    return;

                case SheetKind.ShareExport:
                    isShareExportVisible = false;
                    break;

                case SheetKind.ShareImport:
                    isShareImportVisible = false;
                    break;

                case SheetKind.Changelog:
                    isChangelogVisible = false;
                    break;

#if PFP_RATINGS
                case SheetKind.Report:
                    reportTarget = null;
                    break;

                case SheetKind.PollShare:
                    pollShareOpen = false;
                    break;
#endif
            }

            CloseSheet();
        }

        /// <summary>
        /// Escape closes the sheet. Checked here rather than per-sheet so it means the same thing
        /// everywhere, and only while one is up - Escape with no sheet open belongs to the game.
        /// </summary>
        private void HandleSheetKeyboard()
        {
            if (activeSheet == SheetKind.None)
                return;

            if (ImGui.IsKeyPressed(ImGuiKey.Escape, false))
                RequestSheetDismiss(activeSheet, viaScrim: false);
        }
    }
}
