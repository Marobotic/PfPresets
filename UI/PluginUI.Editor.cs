using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;

namespace PfPresets
{
    /// <summary>
    /// The preset editor window: duty, objective, comment, role slots, and all the
    /// recruitment options of the in-game window.
    /// </summary>
    public partial class PluginUI
    {
        private const int PartySlotCount = 8;

        private PfPresetData? editingPreset = null;
        private bool isNewPreset = false;
        private string editorPresetName = string.Empty;
        private string editorComment = string.Empty;
        private string editorPassword = string.Empty;
        private int editorDutyCategoryId = 0;
        private string editorDutyName = "None";
        private string editorDutyCategoryName = "None";
        private int editorObjectiveId = 0;
        private List<RoleSlot> editorSlots = new();
        private bool editorOnePlayerPerJob = false;
        private bool editorRemoveRoleRestrictions = false;
        private bool editorAutoAdjustRoles = false;
        private bool editorLimitToWorld = false;
        private bool editorPrivateParty = false;
        private bool editorCompletionStatus = false;
        private int editorCompletionStatusType = 0;
        private bool editorAvgItemLvEnabled = false;
        private int editorAvgItemLv = 1;
        private bool editorUnrestrictedParty = false;
        private bool editorMinimumIL = false;
        private bool editorSilenceEcho = false;
        private int editorLootRules = 0;
        private bool editorLangJapanese = true;
        private bool editorLangEnglish = true;
        private bool editorLangGerman = true;
        private bool editorLangFrench = true;

        private void OpenEditor(PfPresetData preset, bool isNew)
        {
            editingPreset = preset;
            isNewPreset = isNew;
            isEditorWindowVisible = true;

            editorPresetName = preset.Name;
            editorComment = preset.Comment;
            editorPassword = preset.PrivatePartyPassword;
            editorDutyCategoryId = preset.DutyCategoryId;
            editorDutyName = preset.DutyName;
            editorDutyCategoryName = preset.DutyCategoryName;
            editorObjectiveId = preset.ObjectiveId;
            editorSlots = preset.Slots.Select(CloneSlot).ToList();
            editorOnePlayerPerJob = preset.OnePlayerPerJob;
            editorRemoveRoleRestrictions = preset.RemoveRoleRestrictions;
            editorAutoAdjustRoles = preset.AutoAdjustRoles;
            editorLimitToWorld = preset.LimitRecruitingToWorld;
            editorPrivateParty = preset.FormPrivateParty;
            editorCompletionStatus = preset.CompletionStatusEnabled;
            editorCompletionStatusType = preset.CompletionStatusType;
            editorAvgItemLvEnabled = preset.AvgItemLvEnabled;
            editorAvgItemLv = preset.AvgItemLv;
            editorUnrestrictedParty = preset.UnrestrictedParty;
            editorMinimumIL = preset.MinimumIL;
            editorSilenceEcho = preset.SilenceEcho;
            editorLootRules = preset.LootRules;
            editorLangJapanese = preset.LangJapanese;
            editorLangEnglish = preset.LangEnglish;
            editorLangGerman = preset.LangGerman;
            editorLangFrench = preset.LangFrench;
            showJobSelector = false;
            jobSelectorSlotIndex = -1;
        }

        private static RoleSlot CloneSlot(RoleSlot s)
            => new RoleSlot { SlotIndex = s.SlotIndex, Role = s.Role, AcceptedJobFlags = s.AcceptedJobFlags };

        private void SaveEditor()
        {
            if (editingPreset == null) return;

            editingPreset.Name = editorPresetName;
            editingPreset.Comment = editorComment;
            editingPreset.PrivatePartyPassword = editorPassword;
            editingPreset.DutyCategoryId = editorDutyCategoryId;
            editingPreset.DutyName = editorDutyName;
            editingPreset.DutyCategoryName = editorDutyCategoryName;
            editingPreset.ObjectiveId = editorObjectiveId;
            editingPreset.Slots = editorSlots.Select(CloneSlot).ToList();
            editingPreset.OnePlayerPerJob = editorOnePlayerPerJob;
            editingPreset.RemoveRoleRestrictions = editorRemoveRoleRestrictions;
            editingPreset.AutoAdjustRoles = editorAutoAdjustRoles;
            editingPreset.LimitRecruitingToWorld = editorLimitToWorld;
            editingPreset.FormPrivateParty = editorPrivateParty;
            editingPreset.CompletionStatusEnabled = editorCompletionStatus;
            editingPreset.CompletionStatusType = editorCompletionStatusType;
            editingPreset.AvgItemLvEnabled = editorAvgItemLvEnabled;
            editingPreset.AvgItemLv = editorAvgItemLv;
            editingPreset.UnrestrictedParty = editorUnrestrictedParty;
            editingPreset.MinimumIL = editorMinimumIL;
            editingPreset.SilenceEcho = editorSilenceEcho;
            editingPreset.LootRules = editorLootRules;
            editingPreset.LangJapanese = editorLangJapanese;
            editingPreset.LangEnglish = editorLangEnglish;
            editingPreset.LangGerman = editorLangGerman;
            editingPreset.LangFrench = editorLangFrench;

            config.UpdatePreset(editingPreset);
            CloseEditor();
        }

        private void CancelEditor()
        {
            if (isNewPreset && editingPreset != null)
                config.DeletePreset(editingPreset.Id);
            CloseEditor();
        }

        private void CloseEditor()
        {
            isEditorWindowVisible = false;
            editingPreset = null;
            showJobSelector = false;
            jobSelectorSlotIndex = -1;
            pfAutomation.ClearAutoAdjustCache();
        }

        private void DrawEditorWindow()
        {
            if (!isEditorWindowVisible || editingPreset == null) return;

            ImGui.SetNextWindowSize(new Vector2(580, 650), ImGuiCond.FirstUseEver);
            ImGui.SetNextWindowSizeConstraints(new Vector2(500, 520), new Vector2(750, 850));

            ImGui.PushStyleColor(ImGuiCol.WindowBg, BgOuter);
            ImGui.PushStyleColor(ImGuiCol.Border, BorderDefault);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 1.0f);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 8.0f);
            ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 4.0f);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(16, 12));

            string title = isNewPreset ? "Create New Preset##PfPresetEditor" : $"Edit: {editingPreset.Name}##PfPresetEditor";
            bool open = isEditorWindowVisible;
            if (ImGui.Begin(title, ref open, ImGuiWindowFlags.NoCollapse))
            {
                if (!open) { CancelEditor(); }
                else { DrawEditorContent(); }
            }
            ImGui.End();
            ImGui.PopStyleVar(4);
            ImGui.PopStyleColor(2);
        }

        private void DrawEditorContent()
        {
            float contentWidth = ImGui.GetContentRegionAvail().X;

            // Preset Name
            DrawSectionLabel("PRESET NAME");
            PushFramedInput();
            ImGui.SetNextItemWidth(contentWidth);
            ImGui.InputText("##PresetName", ref editorPresetName, 128);
            PopFramedInput();
            ImGui.Dummy(new Vector2(0, 8));

            float footerHeight = 46;
            float scrollHeight = ImGui.GetContentRegionAvail().Y - footerHeight;
            ImGui.BeginChild("EditorScroll", new Vector2(contentWidth, scrollHeight), false, ImGuiWindowFlags.None);

            float halfWidth = (contentWidth - 12) / 2f;

            // ══ LEFT COLUMN ═══════════════════════════════════════
            ImGui.BeginChild("EditorLeft", new Vector2(halfWidth, 0), false, ImGuiWindowFlags.None);

            // Duty (Category-based)
            DrawSectionLabel("DUTY");
            DrawDutyCategorySelector();
            ImGui.Dummy(new Vector2(0, 6));

            // Objective
            DrawSectionLabel("OBJECTIVE");
            PushFramedInput();
            string[] objectives = { "None", "Duty Completion", "Practice", "Loot" };
            ImGui.SetNextItemWidth(-1);
            ImGui.Combo("##Objective", ref editorObjectiveId, objectives, objectives.Length);
            PopFramedInput();
            ImGui.Dummy(new Vector2(0, 6));

            // Comment (2 lines that wrap)
            DrawSectionLabel("COMMENT");
            PushFramedInput();
            ImGui.SetNextItemWidth(-1);
            float twoLineHeight = ImGui.GetTextLineHeight() * 2 + ImGui.GetStyle().FramePadding.Y * 2;
            ImGui.InputTextMultiline("##Comment", ref editorComment, PfAutomation.MaxCommentLength + 1, new Vector2(-1, twoLineHeight), (ImGuiInputTextFlags)0x01000000);
            PopFramedInput();
            if (editorComment.Length > PfAutomation.MaxCommentLength)
                editorComment = editorComment.Substring(0, PfAutomation.MaxCommentLength);
            int charCount = editorComment.Length;
            ImGui.TextColored(charCount >= PfAutomation.MaxCommentLength ? AccentRed : TextMuted, $"{charCount}/{PfAutomation.MaxCommentLength}");
            ImGui.SameLine();
            ImGui.TextColored(TextSecondary, " | Wrapped Preview:");
            ImGui.Indent(10);
            ImGui.TextColored(TextMuted, WrapText(editorComment, 38));
            ImGui.Unindent(10);
            ImGui.Dummy(new Vector2(0, 6));

            // Roles
            DrawSectionLabel("ROLES");
            DrawRoleSlotEditor();

            ImGui.EndChild();
            ImGui.SameLine(0, 12);

            // ══ RIGHT COLUMN ══════════════════════════════════════
            ImGui.BeginChild("EditorRight", new Vector2(halfWidth, 0), false, ImGuiWindowFlags.None);

            // Search Area
            DrawSectionLabel("SEARCH AREA");
            DrawStyledCheckbox("Limit Recruiting to World", ref editorLimitToWorld);
            DrawStyledCheckbox("Form a Private Party", ref editorPrivateParty);
            if (editorPrivateParty)
            {
                ImGui.Indent(20);
                ImGui.TextColored(TextSecondary, "Party Password (4 digits):");
                PushFramedInput();
                ImGui.SetNextItemWidth(70);
                ImGui.InputText("##Password", ref editorPassword, 4, ImGuiInputTextFlags.CharsDecimal);
                PopFramedInput();
                string displayPw = int.TryParse(editorPassword, out int val) ? val.ToString("D4") : "0000";
                ImGui.TextColored(TextMuted, $"Display: {displayPw}");
                ImGui.Unindent(20);
            }
            ImGui.Dummy(new Vector2(0, 8));

            // Conditions
            DrawSectionLabel("CONDITIONS");
            DrawStyledCheckbox("Completion Status", ref editorCompletionStatus);
            if (editorCompletionStatus)
            {
                ImGui.Indent(20);
                PushFramedInput();
                string[] completionTypes = { "Duty Complete", "Duty Complete (Weekly Reward Unclaimed)", "Duty Incomplete" };
                ImGui.SetNextItemWidth(-1);
                ImGui.Combo("##CompletionType", ref editorCompletionStatusType, completionTypes, completionTypes.Length);
                PopFramedInput();
                ImGui.Unindent(20);
            }

            DrawStyledCheckbox("Avg. Item Lv.", ref editorAvgItemLvEnabled);
            if (editorAvgItemLvEnabled)
            {
                ImGui.Indent(20);
                PushFramedInput();
                ImGui.SetNextItemWidth(100);
                ImGui.InputInt("##AvgIL", ref editorAvgItemLv, 1, 10);
                editorAvgItemLv = Math.Clamp(editorAvgItemLv, 1, 9999);
                PopFramedInput();
                ImGui.Unindent(20);
            }
            ImGui.Dummy(new Vector2(0, 8));

            // Duty Finder Settings
            DrawSectionLabel("DUTY FINDER SETTINGS");
            DrawStyledCheckbox("Unrestricted Party", ref editorUnrestrictedParty);
            DrawStyledCheckbox("Minimum IL", ref editorMinimumIL);
            DrawStyledCheckbox("Silence Echo", ref editorSilenceEcho);
            ImGui.Dummy(new Vector2(0, 8));

            // Loot Rules
            DrawSectionLabel("LOOT RULES");
            PushFramedInput();
            string[] lootOptions = { "Normal", "Greed Only", "Lootmaster" };
            ImGui.SetNextItemWidth(-1);
            ImGui.Combo("##LootRules", ref editorLootRules, lootOptions, lootOptions.Length);
            PopFramedInput();
            ImGui.Dummy(new Vector2(0, 8));

            // Language
            DrawSectionLabel("LANGUAGE");
            ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(8, 4));
            DrawLanguageFlag("J", ref editorLangJapanese, ColorFromHex("#cc3333"));
            ImGui.SameLine();
            DrawLanguageFlag("E", ref editorLangEnglish, ColorFromHex("#3366cc"));
            ImGui.SameLine();
            DrawLanguageFlag("D", ref editorLangGerman, ColorFromHex("#cc9933"));
            ImGui.SameLine();
            DrawLanguageFlag("F", ref editorLangFrench, ColorFromHex("#3399cc"));
            ImGui.PopStyleVar();

            ImGui.EndChild();
            ImGui.EndChild(); // EditorScroll

            // Footer buttons
            ImGui.Dummy(new Vector2(0, 4));
            ImGui.GetWindowDrawList().AddLine(new Vector2(ImGui.GetWindowPos().X, ImGui.GetCursorScreenPos().Y), new Vector2(ImGui.GetWindowPos().X + ImGui.GetWindowWidth(), ImGui.GetCursorScreenPos().Y), ImGui.ColorConvertFloat4ToU32(BorderDefault), 1.0f);
            ImGui.Dummy(new Vector2(0, 6));

            float btnWidth = (contentWidth - 12) / 2f;
            if (DrawPrimaryButton("  Save Preset##SavePreset", new Vector2(btnWidth, 30)))
                SaveEditor();
            ImGui.SameLine(0, 12);
            if (DrawSecondaryButton("  Cancel##CancelPreset", new Vector2(btnWidth, 30)))
                CancelEditor();
        }

        // ── Duty Category Selector (matches in-game dropdown) ─────
        private void DrawDutyCategorySelector()
        {
            PushFramedInput();
            // All in-game duty categories (index in DutyCategories.Names == category id).
            int catId = editorDutyCategoryId;
            if (catId < 0 || catId >= DutyCategories.Names.Length) catId = 0;

            // Icon for the currently-selected category, drawn to the left of the dropdown.
            uint selIcon = GetCategoryIcon(catId);
            if (selIcon != 0 && TryGetIconHandle(selIcon, out var selHandle))
            {
                ImGui.SetCursorPosY(ImGui.GetCursorPosY() + 1f);
                ImGui.Image(selHandle, new Vector2(18, 18));
                ImGui.SameLine(0, 6);
            }

            ImGui.SetNextItemWidth(-1);
            if (ImGui.BeginCombo("##DutyCategory", DutyCategories.Names[catId]))
            {
                for (int i = 0; i < DutyCategories.Names.Length; i++)
                {
                    Vector2 rp = ImGui.GetCursorScreenPos();
                    bool sel = i == catId;
                    if (ImGui.Selectable($"##cat_{i}", sel, ImGuiSelectableFlags.None, new Vector2(0, 20)))
                    {
                        editorDutyCategoryId = i;
                        editorDutyCategoryName = DutyCategories.Names[i];
                        if (i == 0)
                            editorDutyName = "None";
                        else
                        {
                            var d = dutyDataHelper.GetDutiesByType(editorDutyCategoryName);
                            editorDutyName = d.Count > 0 ? d[0].Name : editorDutyCategoryName;
                        }
                    }
                    var cdl = ImGui.GetWindowDrawList();
                    float textX = rp.X + 2f;
                    uint ic = GetCategoryIcon(i);
                    if (ic != 0 && TryGetIconHandle(ic, out var ih))
                    {
                        cdl.AddImage(ih, new Vector2(rp.X + 2f, rp.Y + 1f), new Vector2(rp.X + 20f, rp.Y + 19f));
                        textX = rp.X + 26f;
                    }
                    cdl.AddText(new Vector2(textX, rp.Y + 2f),
                        ImGui.ColorConvertFloat4ToU32(sel ? AccentBlue : TextPrimary), DutyCategories.Names[i]);
                }
                ImGui.EndCombo();
            }
            PopFramedInput();

            // If a category is selected, show the duty sub-dropdown
            if (editorDutyCategoryId > 0)
            {
                ImGui.Dummy(new Vector2(0, 2));
                PushFramedInput();

                // Get duties for this category from Lumina
                var duties = dutyDataHelper.GetDutiesByType(editorDutyCategoryName);

                if (duties.Count > 0)
                {
                    ImGui.SetNextItemWidth(-1);
                    // Dropdown menu, but with the popup capped to a max height so a long duty
                    // list scrolls inside the dropdown instead of filling the whole screen.
                    ImGui.SetNextWindowSizeConstraints(new Vector2(0, 0), new Vector2(float.MaxValue, 320));
                    if (ImGui.BeginCombo("##DutySelection", editorDutyName))
                    {
                        for (int di = 0; di < duties.Count; di++)
                        {
                            var duty = duties[di];
                            bool isSel = duty.Name.Equals(editorDutyName, StringComparison.OrdinalIgnoreCase);
                            if (ImGui.Selectable($"{duty.Name}##duty_{di}", isSel))
                                editorDutyName = duty.Name;
                            if (isSel)
                                ImGui.SetItemDefaultFocus();
                        }
                        ImGui.EndCombo();
                    }
                }
                else
                {
                    // Manual text input fallback if Lumina doesn't have duties for this category
                    ImGui.SetNextItemWidth(-1);
                    ImGui.InputTextWithHint("##DutyManual", "Type duty name...", ref editorDutyName, 128);
                }

                PopFramedInput();
            }
        }

        // ── Role Slot Editor ──────────────────────────────────────
        private void DrawRoleSlotEditor()
        {
            while (editorSlots.Count < PartySlotCount)
                editorSlots.Add(new RoleSlot { SlotIndex = editorSlots.Count, Role = RoleType.Free });
            while (editorSlots.Count > PartySlotCount)
                editorSlots.RemoveAt(editorSlots.Count - 1);

            const int slotsPerRow = 8;
            const float slotSize = 28f;
            const float spacing = 4f;

            if (editorAutoAdjustRoles)
                ImGui.BeginDisabled();

            List<(RoleType Role, uint? JobId, string Tooltip)>? autoSlots =
                editorAutoAdjustRoles ? pfAutomation.GetAutoAdjustedSlots() : null;

            for (int i = 0; i < editorSlots.Count; i++)
            {
                if (i > 0 && i % slotsPerRow == 0)
                    ImGui.Dummy(new Vector2(0, 2));
                else if (i > 0)
                    ImGui.SameLine(0, spacing);

                var slot = editorSlots[i];
                bool isSlot1 = (i == 0);
                uint? iconId = null;
                FontAwesomeIcon? glyph = null;
                Vector4[]? splitColors = null;
                Vector4 slotColor = GetRoleColor(slot.Role);
                string tooltip = "";

                if (autoSlots != null && i < autoSlots.Count)
                {
                    var autoSlot = autoSlots[i];
                    slotColor = GetRoleColor(autoSlot.Role);
                    if (autoSlot.JobId.HasValue)
                    {
                        iconId = IconJobBase + autoSlot.JobId.Value;
                    }
                    else
                    {
                        switch (autoSlot.Role)
                        {
                            case RoleType.Tank: iconId = IconRoleTank; break;
                            case RoleType.Healer: iconId = IconRoleHealer; break;
                            case RoleType.MeleeDPS:
                            case RoleType.PhysRangedDPS:
                            case RoleType.MagicRangedDPS: iconId = IconRoleDps; break;
                            case RoleType.Omit: glyph = OmitGlyph; break;
                            default: glyph = FreeGlyph; break;
                        }
                    }
                    tooltip = autoSlot.Tooltip;
                }
                else if (isSlot1)
                {
                    if (pfAutomation.PlayerState.IsLoaded && pfAutomation.PlayerState.ClassJob.RowId > 0)
                        iconId = IconJobBase + pfAutomation.PlayerState.ClassJob.RowId;
                    else
                        glyph = FreeGlyph; // unknown job → grey person
                    slotColor = TextPrimary;
                    string jobName = (pfAutomation.PlayerState.IsLoaded && pfAutomation.PlayerState.ClassJob.RowId > 0) ? pfAutomation.PlayerState.ClassJob.Value.Name.ToString() : "Unknown";
                    tooltip = $"Slot 1: You ({jobName})\nThis slot is locked to your current class/job.";
                }
                else
                {
                    if (slot.Role == RoleType.Omit) glyph = OmitGlyph;
                    else if (IsFreeAnySlot(slot)) glyph = FreeGlyph;
                    else
                    {
                        splitColors = GetSplitRoleColors(slot);
                        if (splitColors == null) iconId = GetSlotDisplayIcon(slot);
                    }
                    string jobInfo = slot.AcceptedJobFlags == 0 ? "All jobs" : "Custom jobs";
                    tooltip = $"Slot {i + 1}: {DisplayNames.GetRoleName(slot.Role)}\nJobs: {jobInfo}\nClick: open job/role selector";
                }

                if (isSlot1)
                {
                    Vector2 pos = ImGui.GetCursorScreenPos();
                    if (glyph.HasValue)
                    {
                        DrawGlyphButton(glyph.Value, "Slot1Icon", new Vector2(slotSize, slotSize), TextSecondary);
                    }
                    else if (iconId.HasValue && TryGetIconHandle(iconId.Value, out var handle))
                    {
                        ImGui.Image(handle, new Vector2(slotSize, slotSize));
                    }
                    else
                    {
                        ImGui.Button("US", new Vector2(slotSize, slotSize));
                    }
                    ImGui.GetWindowDrawList().AddRect(pos, new Vector2(pos.X + slotSize, pos.Y + slotSize), ImGui.ColorConvertFloat4ToU32(AccentBlue), 4.0f, ImDrawFlags.None, 1.5f);

                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip(tooltip);
                }
                else
                {
                    Vector4 slotBg = new Vector4(slotColor.X, slotColor.Y, slotColor.Z, 0.3f);
                    ImGui.PushStyleColor(ImGuiCol.Button, slotBg);
                    ImGui.PushStyleColor(ImGuiCol.ButtonHovered, slotColor);
                    ImGui.PushStyleColor(ImGuiCol.ButtonActive, slotColor);
                    ImGui.PushStyleColor(ImGuiCol.Text, TextPrimary);
                    ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 4.0f);

                    bool clicked = false;
                    if (splitColors != null)
                    {
                        Vector2 sp = ImGui.GetCursorScreenPos();
                        clicked = ImGui.InvisibleButton($"Slot_{i}", new Vector2(slotSize, slotSize));
                        DrawSplitRolePerson(sp, slotSize, splitColors);
                    }
                    else if (glyph.HasValue)
                    {
                        clicked = DrawGlyphButton(glyph.Value, $"Slot_{i}", new Vector2(slotSize, slotSize), TextSecondary);
                    }
                    else if (iconId.HasValue && TryGetIconHandle(iconId.Value, out var handle))
                    {
                        ImGui.PushID($"SlotBtn_{i}");
                        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(2, 2));
                        clicked = ImGui.ImageButton(handle, new Vector2(slotSize - 4f, slotSize - 4f));
                        ImGui.PopStyleVar();
                        ImGui.PopID();
                    }
                    else
                    {
                        // Fallback to a short text label when the icon can't be loaded.
                        RoleType labelRole = (autoSlots != null && i < autoSlots.Count) ? autoSlots[i].Role : slot.Role;
                        clicked = ImGui.Button($"{GetRoleShortLabel(labelRole)}##Slot_{i}", new Vector2(slotSize, slotSize));
                    }

                    ImGui.PopStyleVar();
                    ImGui.PopStyleColor(4);

                    if (clicked)
                    {
                        jobSelectorSlotIndex = i;
                        showJobSelector = true;
                        tempSelectorRole = slot.Role;
                        tempSelectorJobFlags = slot.AcceptedJobFlags;
                    }

                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip(tooltip);
                }
            }

            if (editorAutoAdjustRoles)
                ImGui.EndDisabled();

            ImGui.Dummy(new Vector2(0, 4));

            DrawStyledCheckbox("Auto-Adjust Roles (Seek Job Distributions)", ref editorAutoAdjustRoles);
            if (editorAutoAdjustRoles)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, TextMuted);
                ImGui.TextWrapped("Roles will be automatically sought by the game client based on the selected high-end duty when applying this preset.");
                ImGui.PopStyleColor();
            }

            ImGui.Dummy(new Vector2(0, 4));
            DrawStyledCheckbox("One Player per Job", ref editorOnePlayerPerJob);

            bool disableRemoveRestrictions = editorAutoAdjustRoles;
            if (disableRemoveRestrictions) ImGui.BeginDisabled();
            bool oldRemove = editorRemoveRoleRestrictions;
            if (DrawStyledCheckbox("Remove role restrictions for all remaining openings.", ref editorRemoveRoleRestrictions))
            {
                if (editorRemoveRoleRestrictions && !oldRemove)
                {
                    foreach (var slot in editorSlots)
                    {
                        slot.Role = RoleType.Free;
                        slot.AcceptedJobFlags = 0;
                    }
                }
            }
            if (disableRemoveRestrictions) ImGui.EndDisabled();
        }

        private void DrawLanguageFlag(string letter, ref bool enabled, Vector4 color)
        {
            Vector4 bgColor = enabled ? new Vector4(color.X, color.Y, color.Z, 0.3f) : BgCard;
            Vector4 textColor = enabled ? TextPrimary : TextMuted;
            Vector4 borderColor = enabled ? color : BorderDefault;

            ImGui.PushStyleColor(ImGuiCol.Button, bgColor);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(color.X, color.Y, color.Z, 0.4f));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(color.X, color.Y, color.Z, 0.5f));
            ImGui.PushStyleColor(ImGuiCol.Text, textColor);
            ImGui.PushStyleColor(ImGuiCol.Border, borderColor);
            ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 1.0f);
            ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 4.0f);
            if (ImGui.Button($"{letter}##Lang_{letter}", new Vector2(30, 26)))
                enabled = !enabled;
            ImGui.PopStyleVar(2);
            ImGui.PopStyleColor(5);
        }

        /// <summary>Hard-wraps text at a fixed column, previewing how the game splits the
        /// comment into lines.</summary>
        private static string WrapText(string text, int lineLength)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            var sb = new StringBuilder();
            int index = 0;
            while (index < text.Length)
            {
                if (index > 0) sb.Append('\n');
                int chunkLen = Math.Min(lineLength, text.Length - index);
                sb.Append(text.Substring(index, chunkLen));
                index += chunkLen;
            }
            return sb.ToString();
        }
    }
}
