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
        /// <summary>The most seats a party can have. Presets are not this long by default any
        /// more - a light party carries four - so this is a ceiling, not a size.</summary>
        private const int PartySlotCount = 8;

        private PfPresetData? editingPreset = null;
        private bool isNewPreset = false;
        private string editorPresetName = string.Empty;
        private string editorComment = string.Empty;
        private string editorPassword = string.Empty;
        private int editorDutyCategoryId = 0;
        private uint editorDutyRowId = 0;
        private string editorDutyName = "None";
        private string editorDutyCategoryName = "None";
        private int editorObjectiveId = 0;
        private List<RoleSlot> editorSlots = new();
        private bool editorOnePlayerPerJob = false;
        private bool editorRemoveRoleRestrictions = false;
        private bool editorAutoAdjustRoles = false;
        private bool editorAllowDoubleCaster = false;
        private bool editorNoteDoubleCaster = false;
        private bool editorLimitToWorld = false;

        /// <summary>What "limit recruiting to my world" was set to before a category forced it on,
        /// so it can be handed back when the category changes again. Null when nothing is being
        /// held.</summary>
        private bool? limitToWorldBeforeForce;
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
            OpenSheet(SheetKind.Editor);

            editorPresetName = preset.Name;
            editorComment = preset.Comment;
            editorPassword = preset.PrivatePartyPassword;
            editorDutyCategoryId = preset.DutyCategoryId;
            editorDutyRowId = preset.DutyRowId;
            editorDutyName = preset.DutyName;
            editorDutyCategoryName = preset.DutyCategoryName;
            editorObjectiveId = preset.ObjectiveId;
            editorSlots = preset.Slots.Select(CloneSlot).ToList();
            editorOnePlayerPerJob = preset.OnePlayerPerJob;
            editorRemoveRoleRestrictions = preset.RemoveRoleRestrictions;
            editorAutoAdjustRoles = preset.AutoAdjustRoles;
            editorAllowDoubleCaster = preset.AllowDoubleCaster;
            editorNoteDoubleCaster = preset.NoteDoubleCasterInComment;
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

            // Whatever the last preset was forced out of does not belong to this one.
            limitToWorldBeforeForce = null;
        }

        /// <summary>
        /// Reshapes the slots for whatever duty is now selected.
        ///
        /// ON EVERY CHANGE OF DUTY, and deliberately overwriting whatever was there. Changing the
        /// duty is changing what the listing is for, and a Crystalline Conflict preset carrying the
        /// eight seats of the raid it used to be is wrong in a way nobody notices until the listing
        /// is up. The composition is a starting point, not a lock - every slot is still editable
        /// afterwards, which is the whole of the screen this runs on.
        ///
        /// Content this plugin has no table for still gets a full party rather than a row of blank
        /// seats - see <see cref="DutyComposition.DefaultFor"/>.
        /// </summary>
        private void ApplyDefaultComposition()
        {
            editorSlots = DutyComposition.DefaultFor(editorDutyCategoryId, editorDutyName);

            // Auto-adjust does not survive a move to content the game will not seek distributions
            // for; the composition just written is the answer there.
            if (!DutyComposition.SupportsAutoAdjust(editorDutyCategoryId))
                editorAutoAdjustRoles = false;

            // The selector may be open on a slot that has just changed underneath it.
            showJobSelector = false;
            jobSelectorSlotIndex = -1;

            pfAutomation.ClearAutoAdjustCache();
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
            editingPreset.DutyRowId = editorDutyRowId;
            editingPreset.DutyName = editorDutyName;
            editingPreset.DutyCategoryName = editorDutyCategoryName;
            editingPreset.ObjectiveId = editorObjectiveId;
            editingPreset.Slots = editorSlots.Select(CloneSlot).ToList();
            editingPreset.OnePlayerPerJob = editorOnePlayerPerJob;
            editingPreset.RemoveRoleRestrictions = editorRemoveRoleRestrictions;
            editingPreset.AutoAdjustRoles = editorAutoAdjustRoles;
            editingPreset.AllowDoubleCaster = editorAllowDoubleCaster;
            editingPreset.NoteDoubleCasterInComment = editorNoteDoubleCaster;
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

            // A new preset joins the list here and nowhere earlier, so this is the first time it
            // is written to disk. An existing one is already in the list and only needs saving.
            if (isNewPreset)
                config.CommitNewPreset(editingPreset);
            else
                config.UpdatePreset(editingPreset);

            CloseEditor();
        }

        /// <summary>Leaves the editor without keeping anything. A new preset was never added to
        /// the list, so there is nothing to take back out.</summary>
        private void CancelEditor() => CloseEditor();

        private void CloseEditor()
        {
            isEditorWindowVisible = false;
            editingPreset = null;
            showJobSelector = false;
            jobSelectorSlotIndex = -1;
            pfAutomation.ClearAutoAdjustCache();

            // Only if the editor is what is on screen. Saving a preset can be the last step of
            // something that put another sheet up - and dropping whatever happens to be open,
            // because this one closed, is how a sheet disappears mid-sentence.
            if (SheetOpen(SheetKind.Editor) || SheetOpen(SheetKind.JobSelector))
                CloseSheet();
        }

        /// <summary>
        /// The preset editor, as a sheet.
        ///
        /// The two columns become one on the phone. This is the only body in the plugin that still
        /// switches layout on a measurement, and it is measured against the sheet rather than the
        /// window: a 460px phone gives each column 208px, which is narrower than the duty dropdown
        /// that has to live in one of them.
        /// </summary>
        private void DrawEditorSheet()
        {
            if (!isEditorWindowVisible || editingPreset == null)
            {
                CloseSheet();
                return;
            }

            string title = isNewPreset ? "New preset" : "Edit preset";

            if (!BeginSheet("Editor", title, 700f))
                return;

            try
            {
                DrawEditorContent();
            }
            finally
            {
                EndSheet();
            }
        }

        /// <summary>Below this, the editor stacks its two columns instead of setting them side by
        /// side. Measured on the sheet body, which is the phone's screen minus its padding.</summary>
        private const float EditorSplitMinWidth = 560f;

        private void DrawEditorContent()
        {
            // A false here means the body region is clipped away entirely this frame - there is
            // nothing to draw into it, but the child still has to be closed, and the footer still
            // has to be drawn or the sheet loses its way out.
            if (BeginSheetBody(SheetFooterHeight))
            {
                try
                {
                    DrawEditorForm();
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

            DrawEditorFooter();
        }

        /// <summary>Save and Cancel, on the sheet's footer.</summary>
        private void DrawEditorFooter()
        {
            if (DrawSheetFooter("Save preset##SavePreset", "Cancel##CancelPreset", out bool cancelled))
                SaveEditor();
            else if (cancelled)
                CancelEditor();
        }

        private void DrawEditorForm()
        {
            float contentWidth = ImGui.GetContentRegionAvail().X;
            bool split = contentWidth >= EditorSplitMinWidth;

            // Preset Name
            DrawSectionLabel("PRESET NAME");
            PushFramedInput();
            ImGui.SetNextItemWidth(contentWidth);
            ImGui.InputText("##PresetName", ref editorPresetName, 128);
            PopFramedInput();
            ImGui.Dummy(new Vector2(0, 8));

            float halfWidth = split ? (contentWidth - 12) / 2f : contentWidth;

            // ══ LEFT COLUMN ═══════════════════════════════════════
            if (split)
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
            // Counted in bytes, because that is what the game's buffer holds. Characters overstate
            // what fits by up to three times: every symbol in the game's set, and every character
            // of an auto-translate phrase, is three bytes in UTF-8.
            editorComment = CommentText.TruncateToBytes(editorComment, PfAutomation.MaxCommentLength);
            int byteCount = CommentText.ByteLength(editorComment);
            ImGui.TextColored(byteCount >= PfAutomation.MaxCommentLength ? AccentRed : TextMuted,
                $"{byteCount}/{PfAutomation.MaxCommentLength} bytes");
            ImGui.SameLine();
            ImGui.TextColored(TextSecondary, " | Wrapped Preview:");
            ImGui.Indent(10);
            using (CommentFont.Push())
                DrawCommentLines(WrapText(editorComment, 38).Split('\n'), TextMuted);
            ImGui.Unindent(10);
            ImGui.Dummy(new Vector2(0, 6));

            // Roles
            DrawSectionLabel("ROLES");
            DrawRoleSlotEditor();

            if (split)
            {
                ImGui.EndChild();
                ImGui.SameLine(0, 12);
                ImGui.BeginChild("EditorRight", new Vector2(halfWidth, 0), false, ImGuiWindowFlags.None);
            }
            else
            {
                // Stacked, so the two halves need a visible break between them - side by side the
                // gutter did that job, and one column of eleven section labels with no seam reads
                // as a single very long list.
                ImGui.Dummy(new Vector2(0, 10));
            }

            // ══ RIGHT COLUMN ══════════════════════════════════════

            // Search Area
            DrawSectionLabel("SEARCH AREA");
            // FATEs and the hunt are on your own world or they are nowhere - see
            // DutyComposition.RequiresHomeWorld. The box is ticked and held rather than left to be
            // unticked into a listing that cannot work.
            //
            // AND WHAT IT WAS SET TO IS GIVEN BACK. Forcing the box on overwrote the answer that
            // was already in it, so a preset built with world-limiting off, briefly pointed at the
            // hunt and pointed somewhere else again, came away limited - a setting changed by
            // passing through a category rather than by anybody choosing it. The value is put aside
            // on the way in and restored on the way out.
            bool homeWorldOnly = DutyComposition.RequiresHomeWorld(editorDutyCategoryId);

            if (homeWorldOnly)
            {
                limitToWorldBeforeForce ??= editorLimitToWorld;
                editorLimitToWorld = true;
            }
            else if (limitToWorldBeforeForce is { } restored)
            {
                editorLimitToWorld = restored;
                limitToWorldBeforeForce = null;
            }

            if (homeWorldOnly) ImGui.BeginDisabled();
            DrawStyledCheckbox("Limit Recruiting to World", ref editorLimitToWorld);
            if (homeWorldOnly) ImGui.EndDisabled();

            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled) && homeWorldOnly)
                PaddedTooltip(
                    $"{DutyCategories.Names[editorDutyCategoryId]} only happens where you are "
                    + "standing.\n\nAnyone travelling from another world arrives after it, so the "
                    + "listing stays on your own.");

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
            DrawLanguageFlag("J", ref editorLangJapanese, ColorFromHex("#cc3333"));
            ImGui.SameLine(0, Space.Tight);
            DrawLanguageFlag("E", ref editorLangEnglish, ColorFromHex("#3366cc"));
            ImGui.SameLine(0, Space.Tight);
            DrawLanguageFlag("D", ref editorLangGerman, ColorFromHex("#cc9933"));
            ImGui.SameLine(0, Space.Tight);
            DrawLanguageFlag("F", ref editorLangFrench, ColorFromHex("#3399cc"));

            if (split)
                ImGui.EndChild();

            ImGui.Dummy(new Vector2(0, 8));
        }

        /// <summary>The row id to persist for a chosen duty: real sheet ids are stable and worth
        /// storing, synthetic high-end ids are not (they shift with the game data), so those save as
        /// 0 and fall back to matching on the name.</summary>
        private static uint StorableRowId(uint rowId)
            => DutyDataHelper.IsSyntheticRowId(rowId) ? 0u : rowId;

        /// <summary>
        /// The duties the picker will offer for a category: all of them, or only the ones this
        /// character has unlocked when the setting asks for that.
        ///
        /// The filter is here rather than in <see cref="DutyDataHelper"/> on purpose - what the
        /// game data holds is not a matter of preference, and the helper answering differently
        /// depending on a checkbox would make every other caller of it wrong in a way that is very
        /// hard to see.
        /// </summary>
        private List<DutyEntry> OfferableDuties(string categoryName)
        {
            var all = dutyDataHelper.GetDutiesByType(categoryName);
            if (!config.HideLockedDuties)
                return all;

            return all.Where(dutyDataHelper.IsDutyUnlocked).ToList();
        }

        /// <summary>
        /// Whether a whole category is shut to this character. The answer itself belongs to
        /// <see cref="DutyDataHelper.IsCategoryUnlocked"/>; what is here is the per-frame cache.
        /// </summary>
        private bool IsCategoryLocked(int categoryId)
        {
            if (categoryId <= 0 || categoryId >= DutyCategories.Names.Length)
                return false;

            if (categoryLockedThisFrame.TryGetValue(categoryId, out bool cached))
                return cached;

            bool locked = !dutyDataHelper.IsCategoryUnlocked(categoryId);

            categoryLockedThisFrame[categoryId] = locked;
            return locked;
        }

        /// <summary>Category id -> shut, for this frame. The category dropdown asks about all
        /// fifteen every time it is open, and each answer walks that category's whole duty list.
        /// </summary>
        private readonly Dictionary<int, bool> categoryLockedThisFrame = new();

        // ── Duty Category Selector (matches in-game dropdown) ─────
        private void DrawDutyCategorySelector()
        {
            PushFramedInput();
            // All in-game duty categories (index in DutyCategories.Names == category id).
            int catId = editorDutyCategoryId;
            if (catId < 0 || catId >= DutyCategories.Names.Length) catId = 0;

            string catPreview = IsCategoryLocked(catId) && !config.HideLockedDuties
                ? $"{DutyCategories.Names[catId]}  (Locked)"
                : DutyCategories.Names[catId];

            // THE MARK GOES IN THE FIELD, BEFORE THE WORDS.
            //
            // It used to sit outside, a loose 18px image with the dropdown starting after it -
            // two controls where there is one thing being chosen, and the field itself began at a
            // different x from every other field on the sheet. The rows inside the list already
            // read as icon-then-name; the closed field now says the same thing.
            //
            // BOTH DRAWN BY HAND, over an empty preview.
            //
            // The first attempt padded the preview string with spaces and drew the icon on top of
            // them. A space is not a fixed width - it is whatever the face makes it - so the amount
            // of room six of them buy depends on the font and the size, and here it bought slightly
            // less than the picture needed: the mark landed on the first letter and the field read
            // "(icon)uildhests".
            //
            // ImGui gets an empty preview and draws only the frame and its arrow. The mark and the
            // words are then placed against the frame's own rectangle, so the gap between them is a
            // number rather than a guess.
            uint selIcon = GetCategoryIcon(catId);
            Dalamud.Bindings.ImGui.ImTextureID selHandle = default;
            bool hasIcon = selIcon != 0 && TryGetIconHandle(selIcon, out selHandle);

            ImGui.SetNextItemWidth(-1);
            ImGui.SetNextWindowSizeConstraints(new Vector2(0, 0), new Vector2(float.MaxValue, 320));

            // THE LIST'S DRAW LIST IS NOT THE FIELD'S. BeginCombo pushes the popup window when it
            // opens, and GetWindowDrawList after that returns the POPUP's - so the preview was
            // being drawn into the list that belongs to the menu, at the field's coordinates,
            // where the popup's own clip rect cut it in half. Which is exactly what it looked
            // like: the words vanishing from the field and reappearing behind the menu.
            //
            // The parent's list is taken before the call and drawn into afterwards. It is a
            // retained list, so writing to it later is fine, and it renders under the popup -
            // which is where the field is anyway.
            var fdl = ImGui.GetWindowDrawList();

            // THE FRAME IS MEASURED BEFORE IT IS SUBMITTED, not read back afterwards.
            //
            // GetItemRectMin/Max after BeginCombo report the LAST item - and once the menu is open
            // that is the popup, not the field. The centre line worked out from it fell somewhere
            // inside the menu, so the preview was drawn too low and the field's own bottom edge cut
            // it in half. It only looked right while the menu was shut.
            //
            // The cursor and GetFrameHeight give the same rectangle ImGui is about to use, and give
            // it whether the menu is open or not.
            Vector2 fieldMin = ImGui.GetCursorScreenPos();
            var fieldMax = new Vector2(fieldMin.X + ImGui.GetContentRegionAvail().X,
                                       fieldMin.Y + ImGui.GetFrameHeight());

            bool catOpen = ImGui.BeginCombo("##DutyCategory", string.Empty);

            {

                const float pad = 12f;
                const float side = 20f;
                const float gap = 9f;

                // The arrow is ImGui's, at the right of the frame; the words stop clear of it.
                float arrowRoom = fieldMax.Y - fieldMin.Y;
                float textX = fieldMin.X + pad + (hasIcon ? side + gap : 0f);
                float textRoom = fieldMax.X - arrowRoom - textX;

                if (hasIcon)
                {
                    float top = fieldMin.Y + (fieldMax.Y - fieldMin.Y - side) * 0.5f;
                    fdl.AddImage(selHandle, new Vector2(fieldMin.X + pad, top),
                        new Vector2(fieldMin.X + pad + side, top + side));
                }

                using (UiBodyFont.Push())
                {
                    float lineH = ImGui.GetTextLineHeight();
                    fdl.AddText(new Vector2(textX, (fieldMin.Y + fieldMax.Y) * 0.5f - lineH * 0.5f),
                        ImGui.ColorConvertFloat4ToU32(Ink), Fit(catPreview, textRoom));
                }
            }

            if (catOpen)
            {
                for (int i = 0; i < DutyCategories.Names.Length; i++)
                {
                    // Same bargain as the duties inside them: marked when the setting is off,
                    // gone when it is on. The one you are already on always stays - hiding the
                    // category a saved preset points at would leave the dropdown reading as
                    // something the preset is not.
                    // Not offered at all while the plugin cannot post them correctly - see
                    // DutyComposition.IsSupported. The one you are already on stays, so opening an
                    // old preset does not silently repoint it at something else.
                    if (!DutyComposition.IsSupported(i) && i != catId)
                        continue;

                    bool catLocked = IsCategoryLocked(i);
                    if (catLocked && config.HideLockedDuties && i != catId)
                        continue;

                    Vector2 rp = ImGui.GetCursorScreenPos();
                    bool sel = i == catId;
                    if (ImGui.Selectable($"##cat_{i}", sel, ImGuiSelectableFlags.None, new Vector2(0, 26)))
                    {
                        editorDutyCategoryId = i;
                        editorDutyCategoryName = DutyCategories.Names[i];
                        if (i == 0)
                        {
                            editorDutyName = "None";
                            editorDutyRowId = 0;
                        }
                        else
                        {
                            // Default to the category's first duty so the selection is never left
                            // pointing at a duty from the previous category.
                            var d = OfferableDuties(editorDutyCategoryName);
                            editorDutyName = d.Count > 0 ? d[0].Name : editorDutyCategoryName;
                            editorDutyRowId = d.Count > 0 ? StorableRowId(d[0].RowId) : 0;
                        }

                        // Only when it actually moved. Clicking the category you are already on
                        // still fires this Selectable, and resetting the slots because somebody
                        // opened a dropdown and closed it again would throw away their work.
                        if (i != catId)
                            ApplyDefaultComposition();
                    }
                    var cdl = ImGui.GetWindowDrawList();
                    float textX = rp.X + 2f;
                    uint ic = GetCategoryIcon(i);
                    if (ic != 0 && TryGetIconHandle(ic, out var ih))
                    {
                        cdl.AddImage(ih, new Vector2(rp.X + 2f, rp.Y + 4f), new Vector2(rp.X + 20f, rp.Y + 22f));
                        textX = rp.X + 26f;
                    }
                    string catLabel = catLocked && !config.HideLockedDuties
                        ? $"{DutyCategories.Names[i]}  (Locked)"
                        : DutyCategories.Names[i];
                    cdl.AddText(new Vector2(textX, rp.Y + 5f),
                        ImGui.ColorConvertFloat4ToU32(sel ? AccentBlue : catLocked ? TextMuted : TextPrimary),
                        catLabel);

                    // Opens on the category you are already on, rather than at the top of a list
                    // the current one may be scrolled off.
                    if (sel)
                        ImGui.SetItemDefaultFocus();
                }
                ImGui.EndCombo();
            }
            PopFramedInput();

            // The duty sub-dropdown, when the category has duties to choose between. The Hunt and
            // FATEs have none - they are not instances - so the row is left out entirely rather
            // than offered as a text box, which only ever produced a name that resolved to nothing
            // and a listing that posted as "any duty" regardless.
            var duties = editorDutyCategoryId > 0
                ? OfferableDuties(editorDutyCategoryName)
                : new List<DutyEntry>();

            if (duties.Count > 0)
            {
                ImGui.Dummy(new Vector2(0, 2));
                PushFramedInput();

                {
                    ImGui.SetNextItemWidth(-1);
                    // Dropdown menu, but with the popup capped to a max height so a long duty
                    // list scrolls inside the dropdown instead of filling the whole screen.
                    ImGui.SetNextWindowSizeConstraints(new Vector2(0, 0), new Vector2(float.MaxValue, 320));

                    // What the closed dropdown reads, which has to agree with the preset card: the
                    // fight and a mark when the setting is off, nothing but the mark when it is on.
                    var chosen = duties.FirstOrDefault(d =>
                        d.Name.Equals(editorDutyName, StringComparison.OrdinalIgnoreCase));
                    bool chosenLocked = chosen == null
                        ? !dutyDataHelper.IsDutyUnlocked(dutyDataHelper.GetDutyEntry(editorDutyRowId))
                        : !dutyDataHelper.IsDutyUnlocked(chosen);

                    string dutyPreview = chosenLocked
                        ? config.HideLockedDuties ? "(Locked duty)" : $"{editorDutyName}  (Locked)"
                        : editorDutyName;

                    if (ImGui.BeginCombo("##DutySelection", dutyPreview))
                    {
                        for (int di = 0; di < duties.Count; di++)
                        {
                            var duty = duties[di];
                            bool isSel = duty.Name.Equals(editorDutyName, StringComparison.OrdinalIgnoreCase);

                            // Marked, not withheld. You are allowed to write a preset for a fight
                            // you have not reached - on the alt that has it, this is the preset you
                            // want - so the picker says which ones those are and lets you pick one
                            // anyway. Applying is where it stops.
                            string label = dutyDataHelper.IsDutyUnlocked(duty)
                                ? duty.Name
                                : $"{duty.Name}  (Locked)";

                            if (ImGui.Selectable($"{label}##duty_{di}", isSel))
                            {
                                editorDutyName = duty.Name;
                                editorDutyRowId = StorableRowId(duty.RowId);

                                // Same rule as the category above: a re-pick is not a change.
                                if (!isSel)
                                    ApplyDefaultComposition();
                            }
                            if (isSel)
                                ImGui.SetItemDefaultFocus();
                        }
                        ImGui.EndCombo();
                    }
                }

                PopFramedInput();
            }
        }

        // ── Role Slot Editor ──────────────────────────────────────
        private void DrawRoleSlotEditor()
        {
            // However many seats this party has - four for a dungeon, two for Crystalline Conflict,
            // eight for a raid. Only the extremes are corrected: an empty list (a preset from a
            // share code that lost them) is reshaped from the duty, and anything past eight is
            // trimmed because the game has nowhere to put it.
            if (editorSlots.Count == 0)
                editorSlots = DutyComposition.DefaultFor(editorDutyCategoryId, editorDutyName);
            while (editorSlots.Count > PartySlotCount)
                editorSlots.RemoveAt(editorSlots.Count - 1);

            const int slotsPerRow = 8;
            const float slotSize = 28f;
            const float spacing = 4f;

            // Auto-adjust is the game client's own composition, offered only where the game offers
            // it. Anywhere else the checkbox is dead and the flag is forced off, so an old preset
            // that still carries it does not silently keep overriding its own slots.
            bool autoAdjustAvailable = DutyComposition.SupportsAutoAdjust(editorDutyCategoryId);
            if (!autoAdjustAvailable)
                editorAutoAdjustRoles = false;

            if (editorAutoAdjustRoles)
                ImGui.BeginDisabled();

            List<(RoleType Role, uint? JobId, string Tooltip, JobCategory? Category)>? autoSlots =
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
                    ImGui.GetWindowDrawList().AddRect(pos, new Vector2(pos.X + slotSize, pos.Y + slotSize), ImGui.ColorConvertFloat4ToU32(AccentBlue), 0f, ImDrawFlags.None, 1.5f);

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
                    ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, Radius.Tile);

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

                        // Replaces the editor rather than stacking over it. One sheet at a time -
                        // the selector's Apply and Cancel both bring the editor back.
                        OpenSheet(SheetKind.JobSelector);
                    }

                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip(tooltip);
                }
            }

            if (editorAutoAdjustRoles)
                ImGui.EndDisabled();

            ImGui.Dummy(new Vector2(0, 4));

            if (!autoAdjustAvailable) ImGui.BeginDisabled();
            DrawStyledCheckbox("Auto-Adjust Roles (Seek Job Distributions)", ref editorAutoAdjustRoles);
            if (!autoAdjustAvailable) ImGui.EndDisabled();

            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled) && !autoAdjustAvailable)
                PaddedTooltip(
                    "Only trials, raids and high-end duties seek job distributions.\n\n"
                    + "For everything else the slots above are the composition, and they stay "
                    + "yours to edit.");

            if (editorAutoAdjustRoles)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, TextMuted);
                ImGui.TextWrapped("Roles will be automatically sought by the game client based on the selected high-end duty when applying this preset.");
                ImGui.PopStyleColor();

                DrawDoubleCasterOptions();
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

        /// <summary>
        /// One of the four language flags: a square with a letter in the middle of it.
        ///
        /// DRAWN, NOT LABELLED. The plugin's theme aligns every button's label flush left, which is
        /// right for a wide button with a sentence on it and wrong for a 34px square holding a
        /// single character - J, E, D and F all sat against the left edge and the row read as four
        /// boxes with something spilling out of them.
        ///
        /// Centred on the letter's INK rather than on its em box, which is the only way a capital
        /// with no descender sits optically in the middle - see DrawTextCentredOnInk.
        /// </summary>
        private void DrawLanguageFlag(string letter, ref bool enabled, Vector4 color)
        {
            const float w = 36f, h = 32f;

            Vector2 p = ImGui.GetCursorScreenPos();
            ImGui.InvisibleButton($"##Lang_{letter}", new Vector2(w, h));
            bool hot = ImGui.IsItemHovered();

            if (ImGui.IsItemClicked())
                enabled = !enabled;

            var dl = ImGui.GetWindowDrawList();
            var max = new Vector2(p.X + w, p.Y + h);

            Vector4 fill = enabled
                ? color with { W = hot ? 0.42f : 0.30f }
                : hot ? Raised : Field;

            dl.AddRectFilled(p, max, ImGui.ColorConvertFloat4ToU32(fill), Radius.Small);
            dl.AddRect(p, max, ImGui.ColorConvertFloat4ToU32(enabled ? color : BorderControl),
                Radius.Small, ImDrawFlags.None, 1f);

            DrawTextCentredOnInk(letter, new Vector2(p.X + w * 0.5f, p.Y + h * 0.5f),
                enabled ? Ink : Dim);
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

        /// <summary>
        /// The double-caster options, nested under Auto-Adjust Roles because they only mean
        /// anything there - with manual slots you'd set the seat yourself in the job selector.
        /// </summary>
        private void DrawDoubleCasterOptions()
        {
            ImGui.Dummy(new Vector2(0, 2));
            ImGui.Indent(16);

            DrawStyledCheckbox("Allow double caster", ref editorAllowDoubleCaster);
            if (ImGui.IsItemHovered())
            {
                PaddedTooltip(
                    "Opens one melee slot to casters as well - the \"fake melee\" seat.\n\n"
                    + "The last melee slot is the one widened: parties fill from the top,\n"
                    + "so that's the seat most likely to still be free.");
            }

            if (editorAllowDoubleCaster)
            {
                ImGui.Indent(16);
                DrawStyledCheckbox($"Add \"{PfPresetData.DoubleCasterNote}\" to the comment",
                    ref editorNoteDoubleCaster);
                if (ImGui.IsItemHovered())
                {
                    PaddedTooltip(
                        "The slot already accepts casters; this is what tells someone\n"
                        + "scrolling the list. Skipped if it wouldn't fit, or if your\n"
                        + "comment already mentions casters.");
                }
                ImGui.Unindent(16);
            }

            ImGui.Unindent(16);
        }
    }
}
