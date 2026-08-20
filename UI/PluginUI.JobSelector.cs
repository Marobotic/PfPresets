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

        // Job selector geometry.
        //
        // THE JOB ICONS FLOW AND WRAP; they used to sit at two fixed columns, 245px and 510px from
        // the content edge, which needed a 700px window and got one. There is no 700px window any
        // more - the phone is 460 across - and a fixed column at 510 on a 428px content area is not
        // a cramped layout, it is icons drawn off the side of the sheet where nobody can click them.
        //
        // What is left fixed is the label column, because the labels are what the eye runs down and
        // a ragged left edge on eight role names is worse than a ragged right edge on job icons.
        private const float JobSelLabelX = 16f;
        private const float JobSelIconSize = 30f;
        private const float JobSelIconGap = 6f;
        private const float JobSelSubIndent = 22f;
        private const float JobSelRoleIcon = 26f;

        /// <summary>Gap between the last job of a row and the first of its base classes, so the two
        /// groups stay readable as two groups now that they share a flow.</summary>
        private const float JobSelClassGap = 18f;

        /// <summary>
        /// How much of the row the labels get, at the current width.
        ///
        /// Proportional with a floor and a ceiling rather than a constant: "Magical Ranged DPS" is
        /// the longest label and it has to fit on a phone, while on a tablet a label column that
        /// kept growing with the sheet would strand the icons against the right-hand edge.
        /// </summary>
        private static float JobSelLabelColumn(float contentW)
            => Math.Clamp(contentW * 0.42f, 158f, 228f);

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
                drawList.AddRect(pos, br, ImGui.ColorConvertFloat4ToU32(new Vector4(1, 1, 1, 0.5f)),
                    Radius.Tile, ImDrawFlags.None, 1.5f);
                ImGui.SetTooltip(job.Name);
            }
        }

        /// <summary>Draws a role icon inside a subtle rounded, role-coloured frame.</summary>
        private void DrawFramedRoleIcon(uint iconId, Vector2 topLeft, float size, Vector4 color)
        {
            var dl = ImGui.GetWindowDrawList();
            Vector2 br = new Vector2(topLeft.X + size, topLeft.Y + size);
            dl.AddRectFilled(topLeft, br,
                ImGui.ColorConvertFloat4ToU32(new Vector4(color.X, color.Y, color.Z, 0.14f)), Radius.Tile);
            if (TryGetIconHandle(iconId, out var h))
                dl.AddImage(h, new Vector2(topLeft.X + 2f, topLeft.Y + 2f), new Vector2(br.X - 2f, br.Y - 2f));
            dl.AddRect(topLeft, br,
                ImGui.ColorConvertFloat4ToU32(new Vector4(color.X, color.Y, color.Z, 0.65f)),
                Radius.Tile, ImDrawFlags.None, 1.5f);
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

        /// <summary>
        /// One job-selector row: a role name in the label column, and the jobs it covers flowing to
        /// the right of it, wrapping onto as many lines as they need.
        ///
        /// Header rows (Tank / Healer / DPS) select their whole role; sub-rows are indented and
        /// select their sub-category. The whole label area is clickable, so the role icon is part of
        /// the same target as the words beside it.
        /// </summary>
        /// <returns>The row's first-line centre Y, which is where the tree connectors meet it.
        /// The FIRST line, not the row's middle: a wrapped row is taller than one line of icons, and
        /// a connector drawn to its centre would come in below the icon it is pointing at.</returns>
        private float DrawJobSelectorRow(string label, uint roleIconId, JobInfo[] jobs, JobInfo[] classes,
            Vector4 roleColor, string categoryName, RoleType roleType, bool isHeader, float indent)
        {
            Vector2 p0 = ImGui.GetCursorScreenPos();
            float contentW = ImGui.GetContentRegionAvail().X;
            var drawList = ImGui.GetWindowDrawList();

            float labelCol = JobSelLabelColumn(contentW);
            float iconsX = JobSelLabelX + labelCol;
            float iconsAvail = MathF.Max(JobSelIconSize, contentW - iconsX - 4f);

            // Laid out before anything is drawn, so the row knows its own height in advance - the
            // label, the role icon and the hit area are all centred on the first line, and none of
            // them can be positioned until it is known whether there is a second.
            var slots = PlanIconFlow(jobs, classes, iconsAvail, out int lines);

            const float lineStride = JobSelIconSize + JobSelIconGap;
            bool hasIcons = slots.Length > 0;
            float firstLineH = hasIcons ? JobSelIconSize + 10f : 30f;
            float rowH = hasIcons ? firstLineH + (lines - 1) * lineStride : firstLineH;
            float midY = p0.Y + firstLineH * 0.5f;

            ulong categoryMask = GetCategoryMask(categoryName);
            bool isSelected = tempSelectorJobFlags != 0
                ? tempSelectorJobFlags == categoryMask
                : (!isHeader && tempSelectorRole == roleType);

            // Clickable/hover area spans the role icon AND the label. Only the first line of it:
            // pressing the empty space under a wrapped run of icons should do nothing, and the
            // label is on the line above it.
            float clickW = MathF.Max(90f, labelCol - 10f);
            ImGui.SetCursorScreenPos(new Vector2(p0.X + indent, p0.Y));
            if (ImGui.InvisibleButton($"##row_{categoryName}", new Vector2(clickW, firstLineH)))
                SelectRoleCategory(categoryName, roleType);
            if (ImGui.IsItemHovered())
            {
                drawList.AddRectFilled(
                    new Vector2(p0.X + indent - 6f, p0.Y),
                    new Vector2(p0.X + indent + clickW, p0.Y + firstLineH),
                    ImGui.ColorConvertFloat4ToU32(new Vector4(1, 1, 1, 0.05f)), Radius.Small);
                ImGui.SetTooltip($"Select all {label} for this slot");
            }

            // Role icon (framed), centred on the first line.
            DrawFramedRoleIcon(roleIconId, new Vector2(p0.X + indent, midY - JobSelRoleIcon * 0.5f),
                JobSelRoleIcon, roleColor);

            // Label text, ellipsised rather than allowed to run under the icons. "Magical Ranged
            // DPS" is within a few pixels of the phone's label column and the fit depends on the
            // player's font scale, which is not knowable from here.
            float labelX = indent + JobSelRoleIcon + 8f;
            Vector4 labelColor = isHeader || isSelected ? roleColor : JsMuted;
            string shown = Fit(label, JobSelLabelX + labelCol - labelX - 8f);
            Vector2 ts = ImGui.CalcTextSize(shown);
            drawList.AddText(new Vector2(p0.X + labelX, midY - ts.Y * 0.5f),
                ImGui.ColorConvertFloat4ToU32(labelColor), shown);

            foreach (var (job, col, line) in slots)
            {
                ImGui.SetCursorScreenPos(new Vector2(
                    p0.X + iconsX + col,
                    midY - JobSelIconSize * 0.5f + line * lineStride));
                DrawJobIcon(job, JobSelIconSize);
            }

            // Reserve the row so the next row flows below it.
            ImGui.SetCursorScreenPos(p0);
            ImGui.Dummy(new Vector2(contentW, rowH));
            return midY;
        }

        /// <summary>
        /// Where each of a row's icons goes: an x offset within the icon area, and which line.
        ///
        /// Jobs first, then the base classes after a wider gap. The gap is what keeps the two groups
        /// legible as two groups now that they share one flow instead of two columns - and it is
        /// allowed to be the thing that pushes the classes onto their own line, which on a phone is
        /// usually what happens and reads correctly.
        /// </summary>
        private static (JobInfo Job, float X, int Line)[] PlanIconFlow(
            JobInfo[] jobs, JobInfo[] classes, float avail, out int lines)
        {
            const float stride = JobSelIconSize + JobSelIconGap;

            var placed = new System.Collections.Generic.List<(JobInfo, float, int)>(
                jobs.Length + classes.Length);

            float x = 0f;
            int line = 0;

            void Place(JobInfo job, float leadingGap)
            {
                float at = x + leadingGap;
                if (at + JobSelIconSize > avail && placed.Count > 0)
                {
                    line++;
                    at = 0f;
                }
                placed.Add((job, at, line));
                x = at + stride;
            }

            foreach (var job in jobs)
                Place(job, 0f);

            bool first = true;
            foreach (var cls in classes)
            {
                Place(cls, first && jobs.Length > 0 ? JobSelClassGap - JobSelIconGap : 0f);
                first = false;
            }

            lines = line + 1;
            return placed.ToArray();
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

            // Clickable/hover area spans the whole row, description included. Free and Omit are the
            // two rows with no icons on them, so there is nothing else on the line to hit.
            float clickW = contentW - JobSelLabelX;
            ImGui.SetCursorScreenPos(new Vector2(p0.X + JobSelLabelX, p0.Y));
            if (ImGui.InvisibleButton("##row_Free", new Vector2(clickW, rowH)))
            {
                tempSelectorRole = RoleType.Free;
                tempSelectorJobFlags = 0;
            }
            if (ImGui.IsItemHovered())
            {
                drawList.AddRectFilled(new Vector2(p0.X + JobSelLabelX - 6f, p0.Y), new Vector2(p0.X + JobSelLabelX + clickW, p0.Y + rowH),
                    ImGui.ColorConvertFloat4ToU32(new Vector4(1, 1, 1, 0.05f)), Radius.Small);
                ImGui.SetTooltip("Set slot to Free (accepts any role and job)");
            }

            // Person glyph (gold) with "Free" label to its right, then a muted description.
            ImGui.SetCursorScreenPos(new Vector2(p0.X + JobSelLabelX, midY - 9f));
            DrawGlyph(FreeGlyph, AccentYellow);
            Vector2 ts = ImGui.CalcTextSize("Free");
            drawList.AddText(new Vector2(p0.X + JobSelLabelX + JobSelRoleIcon + 8f, midY - ts.Y * 0.5f),
                ImGui.ColorConvertFloat4ToU32(AccentYellow), "Free");

            float descX = JobSelLabelX + JobSelLabelColumn(contentW);
            drawList.AddText(new Vector2(p0.X + descX, midY - ts.Y * 0.5f),
                ImGui.ColorConvertFloat4ToU32(JsMuted),
                Fit("Accepts any role and job for this slot", contentW - descX - 4f));

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

            float clickW = contentW - JobSelLabelX;
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
                        ImGui.ColorConvertFloat4ToU32(new Vector4(1, 1, 1, 0.05f)), Radius.Small);
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

            float omitDescX = JobSelLabelX + JobSelLabelColumn(contentW);
            dl.AddText(new Vector2(p0.X + omitDescX, midY - ts.Y * 0.5f),
                ImGui.ColorConvertFloat4ToU32(JsMuted),
                Fit("Removes the slot from recruitment", contentW - omitDescX - 4f));

            if (isLastActiveSlot) ImGui.EndDisabled();

            ImGui.SetCursorScreenPos(p0);
            ImGui.Dummy(new Vector2(contentW, rowH));
        }

        /// <summary>
        /// Leaves the job selector without applying anything, and goes back to the editor rather
        /// than out to the body behind it.
        ///
        /// The selector is a step inside editing a preset, not a thing you opened on its own, so
        /// dismissing it means "back", not "close". Routed through <see cref="RequestSheetDismiss"/>
        /// so the close button, a tap on the scrim and Escape all do this same thing.
        /// </summary>
        private void CancelJobSelector()
        {
            showJobSelector = false;
            OpenSheet(SheetKind.Editor);
        }

        /// <summary>
        /// The job selector, as a sheet: the roles as rows, the jobs each covers beside them, and
        /// Free and Omit under a rule at the bottom.
        /// </summary>
        private void DrawJobSelectorSheet()
        {
            if (!showJobSelector || jobSelectorSlotIndex < 0 || jobSelectorSlotIndex >= editorSlots.Count)
            {
                // The editor is what is really open; the selector was a step inside it that has
                // gone away underneath us (the slot count changed, or the editor was cancelled).
                OpenSheet(isEditorWindowVisible ? SheetKind.Editor : SheetKind.None);
                return;
            }

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

            var noClasses = Array.Empty<JobInfo>();
            const float subIndent = JobSelLabelX + JobSelSubIndent;

            if (!BeginSheet("Jobs", $"Slot {jobSelectorSlotIndex + 1} — jobs & roles", 620f))
                return;

            try
            {
                if (BeginSheetBody(SheetFooterHeight))
                {
                    try
                    {
                        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(10, 5));
                        try
                        {
                            ImGui.Dummy(new Vector2(0, 2));
                            using (UiHelpFont.Push())
                                ImGui.TextColored(JsMuted,
                                    "Pick a role, the exact jobs it accepts, or omit the slot.");
                            ImGui.Dummy(new Vector2(0, 6));

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

                            ImGui.Dummy(new Vector2(0, 6));
                            DrawRuleHair();
                            ImGui.Dummy(new Vector2(0, 4));
                            DrawFreeRow();
                            DrawOmitButton(isLastActiveSlot);
                            ImGui.Dummy(new Vector2(0, 4));
                        }
                        finally
                        {
                            ImGui.PopStyleVar();
                        }
                    }
                    finally
                    {
                        EndSheetBody();
                    }
                }
                else
                {
                    EndSheetBody();
                }

                if (DrawSheetFooter("Apply to slot##ConfirmJobSelector", "Cancel##CancelJobSelector",
                        out bool cancelled))
                {
                    slot.Role = tempSelectorRole;
                    slot.AcceptedJobFlags = tempSelectorJobFlags;
                    showJobSelector = false;
                    OpenSheet(SheetKind.Editor);
                }
                else if (cancelled)
                {
                    CancelJobSelector();
                }
            }
            finally
            {
                EndSheet();
            }
        }
    }
}
