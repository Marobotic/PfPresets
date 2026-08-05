using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;

namespace PfPresets
{
    /// <summary>
    /// The per-slot job selector window: pick a whole role, individual jobs, Free, or Omit.
    /// </summary>
    public partial class PluginUI
    {
        private int jobSelectorSlotIndex = -1;
        private bool showJobSelector = false;
        private RoleType tempSelectorRole;
        private ulong tempSelectorJobFlags;

        // Fixed-size job selector geometry. Columns are pixel offsets from the window's
        // content-left edge; the wide gap between the label column and the JOB column keeps
        // role labels from sitting under the job icons (prevents misclicks). Everything is
        // laid out with explicit positions for consistent margins and vertical centering.
        private const float JobSelWindowW = 700f;
        private const float JobSelWindowH = 600f;
        private const float JobSelLabelX = 22f;
        private const float JobSelJobX = 245f;
        private const float JobSelClassX = 510f;
        private const float JobSelIconSize = 30f;
        private const float JobSelIconGap = 6f;
        private const float JobSelSubIndent = 30f;
        private const float JobSelRoleIcon = 26f;

        /// <summary>Bitfield of all jobs+classes that belong to the named selector category.
        /// "Healer" and "DPS" are super-groups spanning several job categories.</summary>
        private static ulong GetCategoryMask(string category) => category switch
        {
            "Tank" => JobMasks.GetRoleMask(RoleType.Tank),
            "Healer" => JobMasks.GetRoleMask(RoleType.Healer),
            "Pure Healer" => GetJobCategoryMask(JobCategory.PureHealer),
            "Barrier Healer" => GetJobCategoryMask(JobCategory.BarrierHealer),
            "DPS" => JobMasks.GetRoleMask(RoleType.MeleeDPS) | JobMasks.GetRoleMask(RoleType.PhysRangedDPS) | JobMasks.GetRoleMask(RoleType.MagicRangedDPS),
            "Melee DPS" => JobMasks.GetRoleMask(RoleType.MeleeDPS),
            "Physical Ranged DPS" => JobMasks.GetRoleMask(RoleType.PhysRangedDPS),
            "Magical Ranged DPS" => JobMasks.GetRoleMask(RoleType.MagicRangedDPS),
            _ => 0,
        };

        /// <summary>Bitfield of all jobs+classes in a single job sub-category.</summary>
        private static ulong GetJobCategoryMask(JobCategory category)
        {
            ulong mask = 0;
            foreach (var job in JobData.AllJobsAndClasses)
            {
                if (job.Category == category)
                    mask |= 1UL << job.BitIndex;
            }
            return mask;
        }

        private void SelectRoleCategory(string category, RoleType role)
        {
            // Exclusive role selection: replace any prior flags with this category's.
            tempSelectorRole = role;
            tempSelectorJobFlags = GetCategoryMask(category);
        }

        private bool IsJobSelectedInSelector(JobInfo job)
        {
            if (tempSelectorJobFlags != 0)
                return (tempSelectorJobFlags & (1UL << job.BitIndex)) != 0;

            if (tempSelectorRole == RoleType.Free)
                return true;

            if (tempSelectorRole == RoleType.Omit)
                return false;

            return JobData.GetRoleForCategory(job.Category) == tempSelectorRole;
        }

        /// <summary>Role represented by the currently selected job flags: a single role when every
        /// selected job shares it, otherwise Free.</summary>
        private RoleType RecalcRoleFromFlags()
        {
            if (tempSelectorJobFlags == 0)
                return RoleType.Free;

            RoleType? single = null;
            foreach (var job in JobData.AllJobsAndClasses)
            {
                if ((tempSelectorJobFlags & (1UL << job.BitIndex)) == 0) continue;
                var role = JobData.GetRoleForCategory(job.Category);
                if (single == null)
                    single = role;
                else if (single != role)
                    return RoleType.Free;
            }
            return single ?? RoleType.Free;
        }

        /// <summary>Draws a clickable job icon. Selected jobs render at full opacity, unselected at 60%
        /// (no coloured highlight ring) — selection is conveyed purely by brightness.</summary>
        private void DrawJobIcon(JobInfo job, float iconSize)
        {
            bool isSelected = IsJobSelectedInSelector(job);
            Vector2 pos = ImGui.GetCursorScreenPos();
            Vector2 br = new Vector2(pos.X + iconSize, pos.Y + iconSize);
            var drawList = ImGui.GetWindowDrawList();

            float alpha = isSelected ? 1.0f : 0.6f;
            uint tint = ImGui.ColorConvertFloat4ToU32(new Vector4(1, 1, 1, alpha));
            if (TryGetIconHandle(IconJobBase + (uint)job.Id, out var h))
                drawList.AddImage(h, pos, br, Vector2.Zero, Vector2.One, tint);

            if (ImGui.InvisibleButton($"##JobBtn_{job.Id}", new Vector2(iconSize, iconSize)))
            {
                if (tempSelectorJobFlags == 0)
                    tempSelectorJobFlags = JobMasks.GetRoleMask(tempSelectorRole);
                tempSelectorJobFlags ^= (1UL << job.BitIndex);
                tempSelectorRole = RecalcRoleFromFlags();
            }
            if (ImGui.IsItemHovered())
            {
                drawList.AddRect(pos, br, ImGui.ColorConvertFloat4ToU32(new Vector4(1, 1, 1, 0.5f)), 0f, ImDrawFlags.None, 1.5f);
                ImGui.SetTooltip(job.Name);
            }
        }

        /// <summary>Draws a role icon inside a subtle rounded, role-coloured frame.</summary>
        private void DrawFramedRoleIcon(uint iconId, Vector2 topLeft, float size, Vector4 color)
        {
            var dl = ImGui.GetWindowDrawList();
            Vector2 br = new Vector2(topLeft.X + size, topLeft.Y + size);
            dl.AddRectFilled(topLeft, br, ImGui.ColorConvertFloat4ToU32(new Vector4(color.X, color.Y, color.Z, 0.14f)), 0f);
            if (TryGetIconHandle(iconId, out var h))
                dl.AddImage(h, new Vector2(topLeft.X + 2f, topLeft.Y + 2f), new Vector2(br.X - 2f, br.Y - 2f));
            dl.AddRect(topLeft, br, ImGui.ColorConvertFloat4ToU32(new Vector4(color.X, color.Y, color.Z, 0.65f)), 0f, ImDrawFlags.None, 1.5f);
        }

        /// <summary>Draws the tree connector lines linking a role header to its sub-rows.</summary>
        private void DrawTreeConnector(float contentLeftX, float parentMidY, float[] childMidYs)
        {
            if (childMidYs.Length == 0) return;
            var dl = ImGui.GetWindowDrawList();
            uint col = ImGui.ColorConvertFloat4ToU32(JsConnector);
            float vx = contentLeftX + JobSelLabelX + JobSelRoleIcon * 0.5f;
            float childIconLeft = contentLeftX + JobSelLabelX + JobSelSubIndent;
            float top = parentMidY + JobSelRoleIcon * 0.5f + 1f;
            float bottom = childMidYs[childMidYs.Length - 1];
            dl.AddLine(new Vector2(vx, top), new Vector2(vx, bottom), col, 1.5f);
            foreach (var cy in childMidYs)
                dl.AddLine(new Vector2(vx, cy), new Vector2(childIconLeft, cy), col, 1.5f);
        }

        /// <summary>Draws one job-selector row with explicit positioning so every row shares the same
        /// columns, row height and vertical centering. Header rows (Tank / Healer / DPS) select their
        /// whole role; sub-rows are indented and select their sub-category. The whole label-and-icon
        /// area is hoverable/clickable so the role icon is part of the hover. Returns the row's icon
        /// centre Y (used to draw the tree connectors).</summary>
        private float DrawJobSelectorRow(string label, uint roleIconId, JobInfo[] jobs, JobInfo[] classes,
            Vector4 roleColor, string categoryName, RoleType roleType, bool isHeader, float indent)
        {
            bool hasIcons = jobs.Length > 0 || classes.Length > 0;
            float rowH = hasIcons ? JobSelIconSize + 10f : 30f;
            Vector2 p0 = ImGui.GetCursorScreenPos();
            float contentW = ImGui.GetContentRegionAvail().X;
            float midY = p0.Y + rowH * 0.5f;
            var drawList = ImGui.GetWindowDrawList();

            ulong categoryMask = GetCategoryMask(categoryName);
            bool isSelected = tempSelectorJobFlags != 0
                ? tempSelectorJobFlags == categoryMask
                : (!isHeader && tempSelectorRole == roleType);

            // Clickable/hover area spans the role icon AND the label (icon is part of the hover).
            float clickW = (JobSelJobX - 14f) - indent;
            if (clickW < 90f) clickW = 90f;
            ImGui.SetCursorScreenPos(new Vector2(p0.X + indent, p0.Y));
            if (ImGui.InvisibleButton($"##row_{categoryName}", new Vector2(clickW, rowH)))
                SelectRoleCategory(categoryName, roleType);
            if (ImGui.IsItemHovered())
            {
                drawList.AddRectFilled(new Vector2(p0.X + indent - 6f, p0.Y), new Vector2(p0.X + indent + clickW, p0.Y + rowH),
                    ImGui.ColorConvertFloat4ToU32(new Vector4(1, 1, 1, 0.05f)), 4f);
                ImGui.SetTooltip($"Select all {label} for this slot");
            }

            // Role icon (framed), vertically centered.
            DrawFramedRoleIcon(roleIconId, new Vector2(p0.X + indent, midY - JobSelRoleIcon * 0.5f), JobSelRoleIcon, roleColor);

            // Label text.
            float labelX = indent + JobSelRoleIcon + 8f;
            Vector4 labelColor = isHeader || isSelected ? roleColor : JsMuted;
            Vector2 ts = ImGui.CalcTextSize(label);
            drawList.AddText(new Vector2(p0.X + labelX, midY - ts.Y * 0.5f), ImGui.ColorConvertFloat4ToU32(labelColor), label);

            // Job icons.
            for (int k = 0; k < jobs.Length; k++)
            {
                ImGui.SetCursorScreenPos(new Vector2(p0.X + JobSelJobX + k * (JobSelIconSize + JobSelIconGap), midY - JobSelIconSize * 0.5f));
                DrawJobIcon(jobs[k], JobSelIconSize);
            }
            // Class icons.
            for (int k = 0; k < classes.Length; k++)
            {
                ImGui.SetCursorScreenPos(new Vector2(p0.X + JobSelClassX + k * (JobSelIconSize + JobSelIconGap), midY - JobSelIconSize * 0.5f));
                DrawJobIcon(classes[k], JobSelIconSize);
            }

            // Reserve the row so the next row flows below it.
            ImGui.SetCursorScreenPos(p0);
            ImGui.Dummy(new Vector2(contentW, rowH));
            return midY;
        }

        /// <summary>Draws the "Free" (any job) row in the same column layout as the role rows,
        /// using the FontAwesome person glyph.</summary>
        private void DrawFreeRow()
        {
            const float rowH = 32f;
            Vector2 p0 = ImGui.GetCursorScreenPos();
            float contentW = ImGui.GetContentRegionAvail().X;
            float midY = p0.Y + rowH * 0.5f;
            var drawList = ImGui.GetWindowDrawList();

            // Clickable/hover area spans the person glyph and the "Free" label.
            float clickW = 160f;
            ImGui.SetCursorScreenPos(new Vector2(p0.X + JobSelLabelX, p0.Y));
            if (ImGui.InvisibleButton("##row_Free", new Vector2(clickW, rowH)))
            {
                tempSelectorRole = RoleType.Free;
                tempSelectorJobFlags = 0;
            }
            if (ImGui.IsItemHovered())
            {
                drawList.AddRectFilled(new Vector2(p0.X + JobSelLabelX - 6f, p0.Y), new Vector2(p0.X + JobSelLabelX + clickW, p0.Y + rowH),
                    ImGui.ColorConvertFloat4ToU32(new Vector4(1, 1, 1, 0.05f)), 4f);
                ImGui.SetTooltip("Set slot to Free (accepts any role and job)");
            }

            // Person glyph (gold) with "Free" label to its right, then a muted description.
            ImGui.SetCursorScreenPos(new Vector2(p0.X + JobSelLabelX, midY - 9f));
            DrawGlyph(FreeGlyph, AccentYellow);
            Vector2 ts = ImGui.CalcTextSize("Free");
            drawList.AddText(new Vector2(p0.X + JobSelLabelX + JobSelRoleIcon + 8f, midY - ts.Y * 0.5f),
                ImGui.ColorConvertFloat4ToU32(AccentYellow), "Free");
            drawList.AddText(new Vector2(p0.X + JobSelJobX, midY - ts.Y * 0.5f),
                ImGui.ColorConvertFloat4ToU32(JsMuted), "Accepts any role and job for this slot");

            ImGui.SetCursorScreenPos(p0);
            ImGui.Dummy(new Vector2(contentW, rowH));
        }

        /// <summary>Draws the "Omit" row left-aligned: the FontAwesome circle-with-slash glyph with the
        /// "Omit" label to its right (matching the Free row). Omitting removes the slot from recruitment.</summary>
        private void DrawOmitButton(bool isLastActiveSlot)
        {
            const float rowH = 32f;
            Vector2 p0 = ImGui.GetCursorScreenPos();
            float contentW = ImGui.GetContentRegionAvail().X;
            float midY = p0.Y + rowH * 0.5f;
            var dl = ImGui.GetWindowDrawList();
            bool selected = tempSelectorRole == RoleType.Omit;
            Vector4 tint = selected ? AccentRed : JsMuted;

            if (isLastActiveSlot) ImGui.BeginDisabled(true);

            float clickW = 160f;
            ImGui.SetCursorScreenPos(new Vector2(p0.X + JobSelLabelX, p0.Y));
            bool clicked = ImGui.InvisibleButton("##row_Omit", new Vector2(clickW, rowH));
            bool hovered = ImGui.IsItemHovered(isLastActiveSlot ? ImGuiHoveredFlags.AllowWhenDisabled : ImGuiHoveredFlags.None);
            if (clicked && !isLastActiveSlot)
            {
                tempSelectorRole = RoleType.Omit;
                tempSelectorJobFlags = 0;
            }
            if (hovered)
            {
                if (!isLastActiveSlot)
                    dl.AddRectFilled(new Vector2(p0.X + JobSelLabelX - 6f, p0.Y), new Vector2(p0.X + JobSelLabelX + clickW, p0.Y + rowH),
                        ImGui.ColorConvertFloat4ToU32(new Vector4(1, 1, 1, 0.05f)), 4f);
                ImGui.SetTooltip(isLastActiveSlot
                    ? "Cannot omit the last remaining active slot."
                    : "Omit — removes this slot from recruitment (empty slot)");
            }

            // Ban glyph at the left, "Omit" text to its right.
            ImGui.SetCursorScreenPos(new Vector2(p0.X + JobSelLabelX, midY - 9f));
            DrawGlyph(OmitGlyph, tint);
            Vector2 ts = ImGui.CalcTextSize("Omit");
            dl.AddText(new Vector2(p0.X + JobSelLabelX + JobSelRoleIcon + 8f, midY - ts.Y * 0.5f),
                ImGui.ColorConvertFloat4ToU32(selected ? AccentRed : JsText), "Omit");

            if (isLastActiveSlot) ImGui.EndDisabled();

            ImGui.SetCursorScreenPos(p0);
            ImGui.Dummy(new Vector2(contentW, rowH));
        }

        private void DrawJobSelectorWindow()
        {
            if (!showJobSelector || jobSelectorSlotIndex < 0 || jobSelectorSlotIndex >= editorSlots.Count)
                return;

            var slot = editorSlots[jobSelectorSlotIndex];

            // A slot cannot be omitted when it is the only remaining active slot besides the player.
            bool isLastActiveSlot = false;
            if (jobSelectorSlotIndex > 0)
            {
                int activeCount = 0;
                for (int idx = 1; idx < editorSlots.Count; idx++)
                {
                    if (editorSlots[idx].Role != RoleType.Omit)
                        activeCount++;
                }
                if (activeCount <= 1 && slot.Role != RoleType.Omit)
                    isLastActiveSlot = true;
            }

            ImGui.SetNextWindowSize(new Vector2(JobSelWindowW, JobSelWindowH), ImGuiCond.Always);
            ImGui.PushStyleColor(ImGuiCol.WindowBg, JsBg);
            ImGui.PushStyleColor(ImGuiCol.Border, JsBorder);
            ImGui.PushStyleColor(ImGuiCol.TitleBg, JsTitle);
            ImGui.PushStyleColor(ImGuiCol.TitleBgActive, JsTitle);
            ImGui.PushStyleColor(ImGuiCol.Text, JsText);
            ImGui.PushStyleColor(ImGuiCol.Separator, JsBorder);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 1.0f);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 0f);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(20, 16));

            var noClasses = Array.Empty<JobInfo>();
            const float subIndent = JobSelLabelX + JobSelSubIndent;

            bool open = showJobSelector;
            var flags = ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoResize
                      | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;
            if (ImGui.Begin("Job Selector##JobSelectorWindow", ref open, flags))
            {
                if (!open) showJobSelector = false;

                ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(10, 5));

                // ── Header ──
                ImGui.AlignTextToFramePadding();
                ImGui.TextColored(JsAccent, $"Editing Slot {jobSelectorSlotIndex + 1}");
                ImGui.SameLine(0, 8);
                ImGui.TextColored(JsMuted, "|");
                ImGui.SameLine(0, 8);
                ImGui.AlignTextToFramePadding();
                ImGui.TextColored(JsMuted, "Pick a role, individual jobs, or omit the slot");

                ImGui.Dummy(new Vector2(0, 6));
                ImGui.Separator();
                ImGui.Dummy(new Vector2(0, 6));

                // ── Roles ──
                float contentLeftX = ImGui.GetCursorScreenPos().X;

                DrawJobSelectorRow("Tank", IconRoleTank,
                    new[] { JobData.PLD, JobData.WAR, JobData.DRK, JobData.GNB },
                    new[] { JobData.GLA, JobData.MRD },
                    JsTank, "Tank", RoleType.Tank, isHeader: true, indent: JobSelLabelX);

                float healerY = DrawJobSelectorRow("Healer", IconRoleHealer, noClasses, noClasses,
                    JsHealer, "Healer", RoleType.Healer, isHeader: true, indent: JobSelLabelX);
                float pureY = DrawJobSelectorRow("Pure Healer", IconRoleHealer,
                    new[] { JobData.WHM, JobData.AST }, new[] { JobData.CNJ },
                    JsHealer, "Pure Healer", RoleType.Healer, isHeader: false, indent: subIndent);
                float barrierY = DrawJobSelectorRow("Barrier Healer", IconRoleHealer,
                    new[] { JobData.SCH, JobData.SGE }, noClasses,
                    JsHealer, "Barrier Healer", RoleType.Healer, isHeader: false, indent: subIndent);
                DrawTreeConnector(contentLeftX, healerY, new[] { pureY, barrierY });

                float dpsY = DrawJobSelectorRow("DPS", IconRoleDps, noClasses, noClasses,
                    JsDPS, "DPS", RoleType.MeleeDPS, isHeader: true, indent: JobSelLabelX);
                float meleeY = DrawJobSelectorRow("Melee DPS", IconRoleDps,
                    new[] { JobData.MNK, JobData.DRG, JobData.NIN, JobData.SAM, JobData.RPR, JobData.VPR, JobData.BST },
                    new[] { JobData.PGL, JobData.LNC, JobData.ROG },
                    JsDPS, "Melee DPS", RoleType.MeleeDPS, isHeader: false, indent: subIndent);
                float physY = DrawJobSelectorRow("Physical Ranged DPS", IconRoleDps,
                    new[] { JobData.BRD, JobData.MCH, JobData.DNC }, new[] { JobData.ARC },
                    JsDPS, "Physical Ranged DPS", RoleType.PhysRangedDPS, isHeader: false, indent: subIndent);
                float magicY = DrawJobSelectorRow("Magical Ranged DPS", IconRoleDps,
                    new[] { JobData.BLM, JobData.SMN, JobData.RDM, JobData.PCT, JobData.BLU },
                    new[] { JobData.THM, JobData.ACN },
                    JsDPS, "Magical Ranged DPS", RoleType.MagicRangedDPS, isHeader: false, indent: subIndent);
                DrawTreeConnector(contentLeftX, dpsY, new[] { meleeY, physY, magicY });

                // ── Free / Omit ──
                ImGui.Dummy(new Vector2(0, 6));
                ImGui.Separator();
                ImGui.Dummy(new Vector2(0, 4));
                DrawFreeRow();
                DrawOmitButton(isLastActiveSlot);

                // ── Footer (anchored to the bottom) ──
                float footerH = ButtonHeight;
                float bottomY = ImGui.GetWindowContentRegionMax().Y - footerH;
                if (ImGui.GetCursorPosY() < bottomY) ImGui.SetCursorPosY(bottomY);

                float btnW = (ImGui.GetContentRegionAvail().X - 10) / 2f;

                if (DrawPrimaryButton("OK##ConfirmJobSelector", new Vector2(btnW, footerH)))
                {
                    slot.Role = tempSelectorRole;
                    slot.AcceptedJobFlags = tempSelectorJobFlags;
                    showJobSelector = false;
                }
                ImGui.SameLine(0, 10);
                if (DrawSecondaryButton("Cancel##CancelJobSelector", new Vector2(btnW, footerH)))
                    showJobSelector = false;

                ImGui.PopStyleVar(); // ItemSpacing
            }
            ImGui.End();
            ImGui.PopStyleVar(3);
            ImGui.PopStyleColor(6);
        }
    }
}
