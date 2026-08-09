using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace PfPresets
{
    /// <summary>
    /// Root of the plugin's ImGui interface. This file holds the shared state and the
    /// per-frame Draw entry point; each window lives in its own partial:
    ///   PluginUI.Theme.cs       - colors, icon ids, reusable styled widgets
    ///   PluginUI.MainWindow.cs  - the preset list
    ///   PluginUI.Editor.cs      - the preset editor
    ///   PluginUI.JobSelector.cs - the per-slot job/role selector
    ///   PluginUI.Settings.cs    - the settings window
    ///   PluginUI.Checklist.cs   - the "applying preset" status overlay
    ///   PluginUI.Share.cs       - the preset export / import windows
    /// </summary>
    public partial class PluginUI
    {
        private readonly IDalamudPluginInterface pluginInterface;
        private readonly Configuration config;
        private readonly DutyDataHelper dutyDataHelper;
        private readonly PfAutomation pfAutomation;
        private readonly ITextureProvider textureProvider;

        /// <summary>Set by Plugin after construction; used only by the settings window's
        /// "reset install id" control.</summary>
        internal AnalyticsClient? Analytics { get; set; }

        // ── Window Visibility ─────────────────────────────────────
        private bool isMainWindowVisible = false;
        private bool isSettingsWindowVisible = false;
        private bool isEditorWindowVisible = false;

        /// <summary>The running plugin's version, shown under the title. Read from the assembly so
        /// it always matches the build in use and never needs updating by hand. The trailing
        /// revision is dropped when it's 0, so 3.0.0.0 reads "v3.0.0" but 3.0.0.1 keeps its digit.</summary>
        private static readonly string VersionLabel = BuildVersionLabel();

        private static string BuildVersionLabel()
        {
            var v = typeof(PluginUI).Assembly.GetName().Version;
            if (v == null)
                return string.Empty;
            return v.Revision > 0
                ? $"v{v.Major}.{v.Minor}.{v.Build}.{v.Revision}"
                : $"v{v.Major}.{v.Minor}.{v.Build}";
        }

        /// <summary>
        /// A player name as it should be drawn, honouring the name-display setting.
        ///
        /// Wrap every name that reaches the screen in this. Never wrap one that is about to be
        /// compared, stored, looked up, turned into a URL or passed to the game - see
        /// <see cref="PlayerNameFormat"/>.
        /// </summary>
        private string DisplayName(string name)
            => PlayerNameFormat.Apply(name, config.PlayerNameStyle);

        // ── Per-frame caches (reset at the top of Draw) ───────────
        private bool? rrActiveThisFrame;
        private (bool Ok, string Reason)? canRecruitThisFrame;
        private bool? partyHasNonBattleJobThisFrame;

        public PluginUI(
            IDalamudPluginInterface pluginInterface,
            Configuration config,
            DutyDataHelper dutyDataHelper,
            PfAutomation pfAutomation,
            ITextureProvider textureProvider)
        {
            this.pluginInterface = pluginInterface;
            this.config = config;
            this.dutyDataHelper = dutyDataHelper;
            this.pfAutomation = pfAutomation;
            this.textureProvider = textureProvider;
        }

        public void Dispose()
        {
            DisposeWelcomeFonts();
            DisposeScaleFonts();
#if PFP_RATINGS
            DisposeProfileFonts();
#endif
        }

        public void ToggleMainWindow() => isMainWindowVisible = !isMainWindowVisible;
        public void ToggleSettingsWindow() => isSettingsWindowVisible = !isSettingsWindowVisible;

        public void Draw()
        {
            rrActiveThisFrame = null;
            canRecruitThisFrame = null;
            partyHasNonBattleJobThisFrame = null;

            // PushPluginTheme pushes onto ImGui's process-global color stack, shared with Dalamud
            // and every other plugin. The finally guarantees we pop exactly what we pushed even if a
            // draw call throws, so a bug in our UI can never leak styling into other plugins.
            // The accent is a config value, so it is re-read here rather than at construction: a
            // swatch clicked in Settings has to repaint every window on the very next frame.
            RefreshAccent();

            var theme = PushPluginTheme();
            try
            {
                DrawMainWindow();
                DrawEditorWindow();
                DrawSettingsWindow();
                DrawChecklistOverlay();
                DrawJobSelectorWindow();
                DrawShareExportWindow();
                DrawShareImportWindow();
                DrawSaveFromListingOverlay();
                DrawPartyFinderOpenButton();
                ReportOverlayDiagnostic();
                DrawConfirmDialog();
                DrawChangelogWindow();
                DrawWelcomeWindow();
#if PFP_RATINGS
                DrawRatingPrompt();
                DrawReportDialog();
                DrawListingLeaderRatingOverlay();
#endif
            }
            finally
            {
                PopPluginTheme(theme);
            }
        }

        /// <summary>True when the standalone RecruitmentRefresher plugin is installed and loaded.
        /// When it is, our own Auto Refresher is redundant and is hidden. Computed at most once
        /// per frame.</summary>
        private bool IsRecruitmentRefresherActive()
        {
            rrActiveThisFrame ??= ComputeRecruitmentRefresherActive();
            return rrActiveThisFrame.Value;
        }

        private bool ComputeRecruitmentRefresherActive()
        {
            try
            {
                return pluginInterface.InstalledPlugins
                    .Any(p => (p.Name == "RecruitmentRefresher" || p.InternalName == "RecruitmentRefresher") && p.IsLoaded);
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>Whether recruiting is currently possible, computed at most once per frame
        /// (the check reads native party data, so per-card calls would add up).</summary>
        private bool CanRecruitCached(out string reason)
        {
            if (canRecruitThisFrame == null)
            {
                bool ok = pfAutomation.CanRecruit(out var r);
                canRecruitThisFrame = (ok, r);
            }
            reason = canRecruitThisFrame.Value.Reason;
            return canRecruitThisFrame.Value.Ok;
        }

        /// <summary>Whether a non-battle job (crafter/gatherer) is currently in the party, computed
        /// at most once per frame. Used to flag the Apply button when the game's party-composition
        /// warning is expected.</summary>
        private bool PartyHasNonBattleJobCached()
        {
            partyHasNonBattleJobThisFrame ??= pfAutomation.PartyHasNonBattleJob();
            return partyHasNonBattleJobThisFrame.Value;
        }

        // ══════════════════════════════════════════════════════════
        //  GAME OVERLAY COORDINATES
        // ══════════════════════════════════════════════════════════

        /// <summary>
        /// Converts a position read off the game's own UI into the space ImGui positions windows in.
        ///
        /// The game reports node and addon positions relative to its client area - (0,0) is the top
        /// left of the rendered frame. ImGui, with viewports enabled, positions top-level windows in
        /// virtual-desktop coordinates, where (0,0) is the top left of the primary monitor. The two
        /// agree exactly when the game's client area starts at the desktop origin, and only then.
        ///
        /// That is why the buttons parked against the Party Finder looked pixel-perfect for a long
        /// time and then quietly drifted: nothing about them changed, the game window stopped
        /// starting at (0,0). A second monitor, a rearranged display layout, running windowed
        /// instead of borderless - any of those puts the client area somewhere else on the desktop,
        /// and the whole offset lands on every overlay anchored to a game rectangle. Every other
        /// window in the plugin already goes through the viewport (see the centred dialogs and the
        /// rating prompt); these two were the ones reading raw game coordinates.
        ///
        /// A no-op when the viewport does start at the origin, so it cannot make a correct case wrong.
        /// </summary>
        private static Vector2 GameToScreen(Vector2 gamePos) => ImGui.GetMainViewport().Pos + gamePos;

        /// <summary>Set by /pfpdebug overlay. The next frame reports the numbers the two
        /// game-anchored buttons are positioned from, then clears itself.</summary>
        internal bool OverlayDiagnosticRequested { get; set; }

        /// <summary>
        /// Says out loud where the game thinks its windows are and where ImGui thinks the screen
        /// starts.
        ///
        /// The two disagreeing is invisible from the code and invisible from a screenshot - it
        /// looks exactly like a button drawn at the wrong offset - and it depends on the machine
        /// the plugin is running on, which is the one thing that cannot be read from here.
        /// </summary>
        private void ReportOverlayDiagnostic()
        {
            if (!OverlayDiagnosticRequested)
                return;

            OverlayDiagnosticRequested = false;

            var vp = ImGui.GetMainViewport();
            void Say(string what) => DiagnosticSink?.Invoke($"[PF Analysis debug] {what}");

            Say($"viewport pos=({vp.Pos.X:F0},{vp.Pos.Y:F0}) size=({vp.Size.X:F0},{vp.Size.Y:F0})");
            Say($"display=({ImGui.GetIO().DisplaySize.X:F0},{ImGui.GetIO().DisplaySize.Y:F0})");

            if (pfAutomation.TryGetPartyFinderWindowRect(out var winPos, out var winSize))
                Say($"LookingForGroup game rect=({winPos.X:F0},{winPos.Y:F0}) {winSize.X:F0}x{winSize.Y:F0}");
            else
                Say("LookingForGroup: not open");

            if (pfAutomation.TryGetRecruitButtonRect(out var anchorPos, out var anchorSize))
                Say($"Recruit Members game rect=({anchorPos.X:F0},{anchorPos.Y:F0}) {anchorSize.X:F0}x{anchorSize.Y:F0}");
            else
                Say("Recruit Members: not visible");

            if (pfAutomation.TryGetListingWindowRect(out var listPos, out var listSize))
                Say($"LookingForGroupDetail game rect=({listPos.X:F0},{listPos.Y:F0}) {listSize.X:F0}x{listSize.Y:F0}");
            else
                Say("LookingForGroupDetail: not open");
        }

        // ══════════════════════════════════════════════════════════
        //  GAME ICON HELPERS
        // ══════════════════════════════════════════════════════════

        /// <summary>Tries to resolve a game icon handle. Returns false if the icon can't be loaded,
        /// letting callers fall back to a text label instead of rendering a blank image.</summary>
        private bool TryGetIconHandle(uint iconId, out ImTextureID handle)
        {
            try
            {
                var tex = textureProvider.GetFromGameIcon(new Dalamud.Interface.Textures.GameIconLookup { IconId = iconId });
                if (tex != null && tex.TryGetWrap(out var wrap, out _))
                {
                    handle = wrap.Handle;
                    return true;
                }
            }
            catch {}
            handle = default;
            return false;
        }

        // ══════════════════════════════════════════════════════════
        //  FONTAWESOME GLYPH HELPERS
        // ══════════════════════════════════════════════════════════

        /// <summary>Draws a FontAwesome glyph centered inside a box at a screen position.</summary>
        private void DrawGlyphAt(FontAwesomeIcon icon, Vector2 topLeft, float size, Vector4 color)
        {
            var dl = ImGui.GetWindowDrawList();
            string g = icon.ToIconString();
            using (pluginInterface.UiBuilder.IconFontHandle.Push())
            {
                Vector2 ts = ImGui.CalcTextSize(g);
                dl.AddText(new Vector2(topLeft.X + (size - ts.X) * 0.5f, topLeft.Y + (size - ts.Y) * 0.5f),
                    ImGui.ColorConvertFloat4ToU32(color), g);
            }
        }

        /// <summary>Draws a FontAwesome glyph centered within a rectangle (used to overlay icons on buttons).</summary>
        private void DrawGlyphCentered(FontAwesomeIcon icon, Vector2 rectMin, Vector2 rectMax, Vector4 color)
        {
            var dl = ImGui.GetWindowDrawList();
            string g = icon.ToIconString();
            using (pluginInterface.UiBuilder.IconFontHandle.Push())
            {
                Vector2 ts = ImGui.CalcTextSize(g);
                dl.AddText(new Vector2((rectMin.X + rectMax.X - ts.X) * 0.5f, (rectMin.Y + rectMax.Y - ts.Y) * 0.5f),
                    ImGui.ColorConvertFloat4ToU32(color), g);
            }
        }

        /// <summary>Draws a FontAwesome icon followed by a text label, centered as a group within
        /// a rectangle. Used for buttons that mix an icon (icon font) with normal-font text, which
        /// a single ImGui.Button label cannot do.</summary>
        private void DrawIconLabelCentered(FontAwesomeIcon icon, string text, Vector2 rectMin, Vector2 rectSize, Vector4 color, float alphaMul = 1f)
        {
            var dl = ImGui.GetWindowDrawList();
            string glyph = icon.ToIconString();
            Vector2 iconSize;
            using (pluginInterface.UiBuilder.IconFontHandle.Push())
                iconSize = ImGui.CalcTextSize(glyph);
            Vector2 textSize = ImGui.CalcTextSize(text);
            const float gap = 8f;
            float startX = rectMin.X + (rectSize.X - (iconSize.X + gap + textSize.X)) * 0.5f;
            float midY = rectMin.Y + rectSize.Y * 0.5f;

            var col = color; col.W *= alphaMul;
            uint colU = ImGui.ColorConvertFloat4ToU32(col);
            using (pluginInterface.UiBuilder.IconFontHandle.Push())
                dl.AddText(new Vector2(startX, midY - iconSize.Y * 0.5f), colU, glyph);
            dl.AddText(new Vector2(startX + iconSize.X + gap, midY - textSize.Y * 0.5f), colU, text);
        }

        /// <summary>Renders a FontAwesome glyph as a square button. Always available (bundled font),
        /// so it never renders blank like an unknown game icon would.</summary>
        private bool DrawGlyphButton(FontAwesomeIcon icon, string id, Vector2 size, Vector4 textColor)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, textColor);
            bool clicked;
            using (pluginInterface.UiBuilder.IconFontHandle.Push())
            {
                clicked = ImGui.Button($"{icon.ToIconString()}##{id}", size);
            }
            ImGui.PopStyleColor();
            return clicked;
        }

        /// <summary>Draws a FontAwesome glyph inline at the cursor (no button chrome).</summary>
        private void DrawGlyph(FontAwesomeIcon icon, Vector4 color)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, color);
            using (pluginInterface.UiBuilder.IconFontHandle.Push())
            {
                ImGui.TextUnformatted(icon.ToIconString());
            }
            ImGui.PopStyleColor();
        }

        // ══════════════════════════════════════════════════════════
        //  SLOT ICON LOGIC (shared by the preset cards and the editor)
        // ══════════════════════════════════════════════════════════

        /// <summary>Returns which of the three top-level role groups (Tank / Healer / DPS) are
        /// represented by the given accepted-job bitfield. All DPS sub-roles count as one "DPS" group.</summary>
        private static (bool Tank, bool Healer, bool Dps) GetRoleGroupsFromFlags(ulong flags)
        {
            bool tank = false, healer = false, dps = false;
            foreach (var job in JobData.AllJobsAndClasses)
            {
                if ((flags & (1UL << job.BitIndex)) == 0) continue;
                switch (job.Category)
                {
                    case JobCategory.Tank: tank = true; break;
                    case JobCategory.PureHealer:
                    case JobCategory.BarrierHealer: healer = true; break;
                    case JobCategory.MeleeDPS:
                    case JobCategory.PhysRangedDPS:
                    case JobCategory.MagicRangedDPS: dps = true; break;
                }
            }
            return (tank, healer, dps);
        }

        /// <summary>Game icon for a slot, or null when the slot is Free/Omit (the caller draws those
        /// with FontAwesome glyphs instead): a single job → that job's icon; one role group → that
        /// role's icon; two or more role groups → the All-Rounder icon.</summary>
        private uint? GetSlotDisplayIcon(RoleSlot slot)
        {
            if (slot.Role == RoleType.Omit)
                return null; // drawn with the Ban glyph

            // No custom job restriction: use the role's icon (Free is drawn with the person glyph).
            if (slot.AcceptedJobFlags == 0)
            {
                return slot.Role switch
                {
                    RoleType.Tank => IconRoleTank,
                    RoleType.Healer => IconRoleHealer,
                    RoleType.MeleeDPS or RoleType.PhysRangedDPS or RoleType.MagicRangedDPS => IconRoleDps,
                    _ => null, // Free
                };
            }

            // Exactly one job selected → show that job's icon.
            if ((slot.AcceptedJobFlags & (slot.AcceptedJobFlags - 1)) == 0)
            {
                foreach (var job in JobData.AllJobsAndClasses)
                {
                    if ((1UL << job.BitIndex) == slot.AcceptedJobFlags)
                        return IconJobBase + (uint)job.Id;
                }
            }

            // Multiple jobs → one role group shows that role icon; 2+ role groups show All-Rounder.
            var (tank, healer, dps) = GetRoleGroupsFromFlags(slot.AcceptedJobFlags);
            int groups = (tank ? 1 : 0) + (healer ? 1 : 0) + (dps ? 1 : 0);
            if (groups >= 2) return IconAllRounder;
            if (tank) return IconRoleTank;
            if (healer) return IconRoleHealer;
            return IconRoleDps;
        }

        /// <summary>True when a slot accepts any job (Free) and so should show the person glyph.</summary>
        private static bool IsFreeAnySlot(RoleSlot slot)
            => slot.Role == RoleType.Free && slot.AcceptedJobFlags == 0;

        /// <summary>If a slot's jobs span two or three role groups, returns the role colours (in
        /// Tank → Healer → DPS order) used to tint the split-person icon; otherwise null.</summary>
        private Vector4[]? GetSplitRoleColors(RoleSlot slot)
        {
            if (slot.AcceptedJobFlags == 0) return null;
            var (tank, healer, dps) = GetRoleGroupsFromFlags(slot.AcceptedJobFlags);
            int n = (tank ? 1 : 0) + (healer ? 1 : 0) + (dps ? 1 : 0);
            if (n < 2) return null;
            var list = new List<Vector4>(3);
            if (tank) list.Add(SplitTank);
            if (healer) list.Add(SplitHealer);
            if (dps) list.Add(SplitDPS);
            return list.ToArray();
        }

        /// <summary>
        /// Draws the combined-role icon: the role colours stack as equal horizontal bands — one per
        /// role, so two roles read as two stripes and three as three — with a neutral person
        /// silhouette on top. The person is never tinted; only the bands carry the colours.
        ///
        /// Bands rather than pie wedges: at 16px a radial split is unreadable, and the diagonal
        /// seam fought the person's outline. Horizontal stripes stay legible at icon size and let
        /// you count the roles at a glance.
        /// </summary>
        private void DrawSplitRolePerson(Vector2 topLeft, float size, Vector4[] colors)
        {
            var dl = ImGui.GetWindowDrawList();
            uint Col(Vector4 v) => ImGui.ColorConvertFloat4ToU32(v);

            Vector2 tl = topLeft;
            Vector2 br = new Vector2(topLeft.X + size, topLeft.Y + size);
            const float rounding = 0f;

            int n = Math.Max(1, colors.Length);
            float band = size / n;

            for (int i = 0; i < n; i++)
            {
                Vector2 bmin = new Vector2(tl.X, tl.Y + band * i);
                // Overshoot each band slightly so rounding never leaves a hairline seam between them.
                Vector2 bmax = new Vector2(br.X, tl.Y + band * (i + 1) + (i < n - 1 ? 0.5f : 0f));

                ImDrawFlags corners = n == 1 ? ImDrawFlags.RoundCornersAll
                    : i == 0 ? ImDrawFlags.RoundCornersTop
                    : i == n - 1 ? ImDrawFlags.RoundCornersBottom
                    : ImDrawFlags.RoundCornersNone;

                dl.AddRectFilled(bmin, bmax, Col(colors[i]), rounding, corners);
            }

            // Hairline separators between bands, so adjacent colours of similar value stay distinct.
            for (int i = 1; i < n; i++)
            {
                float sy = tl.Y + band * i;
                dl.AddLine(new Vector2(tl.X, sy), new Vector2(br.X, sy),
                    Col(new Vector4(0f, 0f, 0f, 0.35f)), 1f);
            }

            dl.AddRect(tl, br, Col(ColorFromHex("#1b2230")), rounding, ImDrawFlags.None, 1.5f);

            // Neutral person silhouette on top, with a soft shadow so it reads over any band colour.
            string glyph = FreeGlyph.ToIconString();
            using (pluginInterface.UiBuilder.IconFontHandle.Push())
            {
                Vector2 ts = ImGui.CalcTextSize(glyph);
                Vector2 tp = new Vector2(topLeft.X + (size - ts.X) * 0.5f, topLeft.Y + (size - ts.Y) * 0.5f);
                dl.AddText(new Vector2(tp.X + 1f, tp.Y + 1f), Col(new Vector4(0, 0, 0, 0.5f)), glyph);
                dl.AddText(tp, Col(ColorFromHex("#eef2f8")), glyph);
            }
        }

        /// <summary>Draws a slot's composition icon (role/job/split-person/free/omit) at a screen position.</summary>
        private void DrawSlotMiniIcon(RoleSlot slot, Vector2 topLeft, float size)
        {
            if (slot.Role == RoleType.Omit) { DrawGlyphAt(OmitGlyph, topLeft, size, TextMuted); return; }
            if (IsFreeAnySlot(slot)) { DrawGlyphAt(FreeGlyph, topLeft, size, TextSecondary); return; }
            var split = GetSplitRoleColors(slot);
            if (split != null) { DrawSplitRolePerson(topLeft, size, split); return; }
            uint? gi = GetSlotDisplayIcon(slot);
            if (gi.HasValue && TryGetIconHandle(gi.Value, out var h))
                ImGui.GetWindowDrawList().AddImage(h, topLeft, new Vector2(topLeft.X + size, topLeft.Y + size));
            else
                DrawGlyphAt(FreeGlyph, topLeft, size, TextSecondary);
        }

        /// <summary>Draws an auto-adjusted slot's icon: the specific job if known, else the role icon.</summary>
        private void DrawAutoSlotMiniIcon(RoleType role, uint? jobId, Vector2 topLeft, float size)
        {
            var dl = ImGui.GetWindowDrawList();
            Vector2 br = new Vector2(topLeft.X + size, topLeft.Y + size);
            if (jobId.HasValue && jobId.Value > 0 && TryGetIconHandle(IconJobBase + jobId.Value, out var jh))
            {
                dl.AddImage(jh, topLeft, br);
                return;
            }
            uint ric = role switch
            {
                RoleType.Tank => IconRoleTank,
                RoleType.Healer => IconRoleHealer,
                RoleType.MeleeDPS or RoleType.PhysRangedDPS or RoleType.MagicRangedDPS => IconRoleDps,
                _ => 0u,
            };
            if (ric != 0 && TryGetIconHandle(ric, out var rh))
                dl.AddImage(rh, topLeft, br);
            else
                DrawGlyphAt(FreeGlyph, topLeft, size, TextSecondary);
        }
    }
}
