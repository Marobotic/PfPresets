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
        // A flat 30px tall, and that is the whole footprint - the host window is the button, with no
        // padding, no border and no minimum size of its own to inflate it.
        //
        // Not derived from the game's button height any more. Tying it to the anchor meant the size
        // set here was only ever a bound: at a UI scale where Recruit Members is 36px, asking for 30
        // produced 36, and the number in this file described nothing anyone could see.
        //
        // The width is measured from the label rather than fixed: it is centred rather than
        // clipped, and a rect too narrow for it would let it hang out of both ends of its own
        // button at larger font scales.
        private const float PfOpenButtonMinW = 112f;
        private const float PfOpenButtonPadX = 14f;
        private const float PfOpenButtonH = 30f;

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
            var size = new Vector2(Math.Max(PfOpenButtonMinW, contentW + PfOpenButtonPadX * 2),
                PfOpenButtonH);

            // To the right of Recruit Members by default. When the window's edge is too close for
            // that, the button drops below the window rather than hanging off the side or covering
            // whatever the game put next to it.
            var pos = new Vector2(anchorPos.X + anchorSize.X + PfOpenButtonGap,
                anchorPos.Y + (anchorSize.Y - size.Y) * 0.5f);

            if (haveWindow && pos.X + size.X > winPos.X + winSize.X)
                pos = new Vector2(anchorPos.X, winPos.Y + winSize.Y + 4f);

            ImGui.SetNextWindowPos(pos, ImGuiCond.Always);
            ImGui.SetNextWindowSize(size, ImGuiCond.Always);

            ImGui.PushStyleColor(ImGuiCol.WindowBg, new Vector4(0f, 0f, 0f, 0f));
            ImGui.PushStyleColor(ImGuiCol.Border, new Vector4(0f, 0f, 0f, 0f));
            ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 0f);
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
                        // A window reopened from here should come back the size it was left at,
                        // not minimised to a title bar with no obvious way out.
                        isMinimized = false;
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
