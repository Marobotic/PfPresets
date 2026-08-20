using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;

namespace PfPresets
{
    /// <summary>
    /// The "PF Analysis" button that opens the plugin from the game's own Party Finder window,
    /// sitting beside "Recruit Members".
    ///
    /// It exists because the plugin was otherwise only reachable by typing a command, which is a
    /// thing people do once and then forget they can do. The Party Finder window is where someone
    /// already is when they want any of this, so that is where the way in belongs.
    ///
    /// It carries the plugin's name. It used to describe the job instead ("Apply a recruitment
    /// preset"), on the reasoning that nobody wants to be told which addon this is - but presets
    /// are no longer the half of it, and a button naming one feature of several undersells the
    /// window behind it as much as it explains it.
    ///
    /// Drawn the same way as the "Save as Preset" button: a fully transparent, borderless host
    /// window holding nothing but the button, so the only thing over the game is the control itself.
    /// </summary>
    public partial class PluginUI
    {
        // The host window is the button, with no padding, no border and no minimum size of its own
        // to inflate it.
        //
        // The width stays measured from the label rather than borrowed from the anchor. Recruit
        // Members is 204px of mostly empty button, and copying that would buy alignment on an edge
        // nothing sits against at the cost of a control four times the size of its own text.
        private const float PfOpenButtonMinW = 112f;
        private const float PfOpenButtonPadX = 14f;

        /// <summary>
        /// How much of Recruit Members' height this button takes.
        ///
        /// Deliberately not all of it. Matching the anchor exactly did line the two up, and made
        /// this read as the game's equal - which it is not. Recruit Members is what the window is
        /// for; this is a way into a plugin, parked beside it.
        ///
        /// A fraction rather than the flat 30px it was, because 30 is only the right number at one
        /// UI scale. At the anchor's usual 43 this lands on 30 - the size that looked right - and
        /// it stays the same proportion, still centred on the game's button, if the player scales
        /// their interface up.
        /// </summary>
        private const float PfOpenButtonHeightRatio = 0.7f;

        /// <summary>Height when the anchor's own is unreadable, and the bounds its share is clamped
        /// into. Only reached if the game hands us a rectangle we shouldn't trust.</summary>
        private const float PfOpenButtonFallbackH = 30f;
        private const float PfOpenButtonMinH = 22f;
        private const float PfOpenButtonMaxH = 60f;

        /// <summary>What the button says: the plugin's name, matching what it is called everywhere
        /// else it announces itself.</summary>
        private const string PfOpenButtonLabel = "PF Analysis";

        // Kept off the game's button by a hair, so the two read as neighbours rather than as one
        // control that grew an extra half.
        private const float PfOpenButtonGap = 6f;

        private void DrawPartyFinderOpenButton()
        {
            if (!config.ShowPartyFinderButton)
                return;

            // Gone entirely while the plugin is open, rather than dimmed into a way back. The
            // window it would open is already on screen and has its own close button, so the only
            // thing a second control next to Recruit Members adds there is something else to read.
            if (isMainWindowVisible)
                return;

            // AND GONE WHILE THE GAME HAS A SUB-WINDOW OPEN OVER THE LIST.
            //
            // Recruitment Criteria and a listing's details both sit on top of the Party Finder, and
            // "Recruit Members" stays visible behind them - so this button did too, hovering over a
            // window that had already asked for your attention. The listing details is also where
            // "Save as Preset" lives, and two of this plugin's buttons on screen at once, three
            // inches apart, doing unrelated things, is the thing to avoid most of all.
            if (pfAutomation.IsPartyFinderSubWindowOpen())
                return;

            if (!pfAutomation.TryGetRecruitButtonRect(out var anchorPos, out var anchorSize))
                return;

            // The window rect is only needed to keep the button inside it; if it can't be read,
            // the anchor alone is still enough to draw something sensible.
            bool haveWindow = pfAutomation.TryGetPartyFinderWindowRect(out var winPos, out var winSize);

            // Icon, gap and label, the same three pieces DrawIconLabelCentered lays out inside it.
            float iconW;
            using (pluginInterface.UiBuilder.IconFontHandle.Push())
                iconW = ImGui.CalcTextSize(LogoIcon.ToIconString()).X;

            float contentW = iconW + 8f + ImGui.CalcTextSize(PfOpenButtonLabel).X;

            // A share of the anchor's height, so the two stay in the same relationship at any UI
            // scale. The clamp is only there to catch a rectangle worth distrusting.
            float height = anchorSize.Y > 0f
                ? Math.Clamp(anchorSize.Y * PfOpenButtonHeightRatio,
                    PfOpenButtonMinH, PfOpenButtonMaxH)
                : PfOpenButtonFallbackH;

            var size = new Vector2(Math.Max(PfOpenButtonMinW, contentW + PfOpenButtonPadX * 2),
                height);

            // To the right of Recruit Members by default. When the window's edge is too close for
            // that, the button drops below the window rather than hanging off the side or covering
            // whatever the game put next to it.
            var pos = new Vector2(anchorPos.X + anchorSize.X + PfOpenButtonGap,
                anchorPos.Y + (anchorSize.Y - size.Y) * 0.5f);

            if (haveWindow && pos.X + size.X > winPos.X + winSize.X)
                pos = new Vector2(anchorPos.X, winPos.Y + winSize.Y + 4f);

            // Everything above is in the game's coordinates, including the bounds check - the two
            // rectangles being compared both come from the game, so converting either of them first
            // would only be work. The conversion happens once, here, on the way out.
            ImGui.SetNextWindowPos(GameToScreen(pos), ImGuiCond.Always);
            ImGui.SetNextWindowSize(size, ImGuiCond.Always);

            ImGui.PushStyleColor(ImGuiCol.WindowBg, new Vector4(0f, 0f, 0f, 0f));
            ImGui.PushStyleColor(ImGuiCol.Border, new Vector4(0f, 0f, 0f, 0f));
            ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, Radius.Control);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
            // ImGui refuses to make a top-level window smaller than style.WindowMinSize, which is
            // 32x32 by default - taller than the button we are asking for. Without this the host
            // window silently outgrows its contents and the height set above stops being the height.
            ImGui.PushStyleVar(ImGuiStyleVar.WindowMinSize, Vector2.Zero);

            const ImGuiWindowFlags flags =
                ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove |
                ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse |
                ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoFocusOnAppearing |
                ImGuiWindowFlags.NoNavFocus | ImGuiWindowFlags.NoBackground |
                ImGuiWindowFlags.NoSavedSettings;

            try
            {
                if (ImGui.Begin("##PfPresetsOpenFromPf", flags))
                {
                    Vector2 btnPos = ImGui.GetCursorScreenPos();

                    if (DrawPrimaryButton("##OpenPfAnalysis", size))
                    {
                        isMainWindowVisible = true;
                    }

                    DrawIconLabelCentered(LogoIcon, PfOpenButtonLabel, btnPos, size, JsOkText);

                    if (ImGui.IsItemHovered())
                        // The label names the plugin now, so the tooltip is where the job goes -
                        // otherwise the two say the same thing twice.
                        PaddedTooltip("Post a saved recruitment preset, and see who joins you.");
                }
            }
            finally
            {
                ImGui.End();
                ImGui.PopStyleVar(4);
                ImGui.PopStyleColor(2);
            }
        }
    }
}
