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
            int themeColors = PushPluginTheme();
            try
            {
                DrawMainWindow();
                DrawEditorWindow();
                DrawSettingsWindow();
                DrawChecklistOverlay();
                DrawJobSelectorWindow();
                DrawShareExportWindow();
                DrawShareImportWindow();
            }
            finally
            {
                ImGui.PopStyleColor(themeColors);
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

        /// <summary>Draws the combined-role icon: the role colours fill the background as equal
        /// triangles (half/half for two roles, thirds for three) with a neutral person silhouette on
        /// top. The person is never tinted — only the background carries the colours.</summary>
        private void DrawSplitRolePerson(Vector2 topLeft, float size, Vector4[] colors)
        {
            var dl = ImGui.GetWindowDrawList();
            Vector2 tl = topLeft;
            Vector2 tr = new Vector2(topLeft.X + size, topLeft.Y);
            Vector2 br = new Vector2(topLeft.X + size, topLeft.Y + size);
            Vector2 bl = new Vector2(topLeft.X, topLeft.Y + size);
            Vector2 c = new Vector2(topLeft.X + size * 0.5f, topLeft.Y + size * 0.5f);
            float h = size * 0.5f;

            uint Col(Vector4 v) => ImGui.ColorConvertFloat4ToU32(v);

            if (colors.Length >= 3)
            {
                // Three equal wedges from the centre (one pointing up, two down).
                Vector2 Boundary(float deg)
                {
                    float r = deg * (MathF.PI / 180f);
                    float dx = MathF.Cos(r), dy = MathF.Sin(r);
                    float t = h / MathF.Max(MathF.Abs(dx), MathF.Abs(dy));
                    return new Vector2(c.X + dx * t, c.Y + dy * t);
                }
                float[] cornerAng = { -135f, -45f, 45f, 135f };
                Vector2[] cornerPt = { tl, tr, br, bl };
                int n = colors.Length;
                float seg = 360f / n;
                for (int i = 0; i < n; i++)
                {
                    float a0 = -150f + i * seg;
                    float a1 = a0 + seg;
                    var perim = new List<Vector2> { Boundary(a0) };
                    var mids = new List<(float Ang, Vector2 P)>();
                    for (int k = 0; k < 4; k++)
                    {
                        float ca = cornerAng[k];
                        while (ca < a0) ca += 360f;
                        if (ca > a0 && ca < a1) mids.Add((ca, cornerPt[k]));
                    }
                    mids.Sort((x, y) => x.Ang.CompareTo(y.Ang));
                    foreach (var m in mids) perim.Add(m.P);
                    perim.Add(Boundary(a1));
                    for (int j = 0; j < perim.Count - 1; j++)
                        dl.AddTriangleFilled(c, perim[j], perim[j + 1], Col(colors[i]));
                }
            }
            else if (colors.Length == 2)
            {
                // Diagonal half/half (two triangles).
                dl.AddTriangleFilled(tl, tr, br, Col(colors[0]));
                dl.AddTriangleFilled(tl, br, bl, Col(colors[1]));
            }
            else
            {
                dl.AddRectFilled(tl, br, Col(colors[0]), 0f);
            }

            // Border.
            dl.AddRect(tl, br, Col(ColorFromHex("#1b2230")), 4f, ImDrawFlags.None, 1.5f);

            // Neutral person silhouette on top (with a subtle shadow for contrast).
            string glyph = FreeGlyph.ToIconString();
            using (pluginInterface.UiBuilder.IconFontHandle.Push())
            {
                Vector2 ts = ImGui.CalcTextSize(glyph);
                Vector2 tp = new Vector2(topLeft.X + (size - ts.X) * 0.5f, topLeft.Y + (size - ts.Y) * 0.5f);
                dl.AddText(new Vector2(tp.X + 1f, tp.Y + 1f), Col(new Vector4(0, 0, 0, 0.45f)), glyph);
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
