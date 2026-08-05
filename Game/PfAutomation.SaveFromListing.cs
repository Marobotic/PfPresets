using System;
using System.Linq;
using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace PfPresets
{
    /// <summary>
    /// Turning a Party Finder listing you're looking at into a preset.
    ///
    /// Everything comes from AgentLookingForGroup.LastViewedListing, which the game fills in
    /// whenever a listing's detail window is opened. Nothing here trusts that data: every field is
    /// range-checked on the way into a preset, and any failure is reported to chat rather than
    /// thrown, because this runs from a button drawn over the game's own window.
    /// </summary>
    public partial class PfAutomation
    {
        /// <summary>Screen rectangle of the listing detail window, or null when it isn't open.
        /// Used to park the "Save as Preset" button against it.</summary>
        public unsafe bool TryGetListingWindowRect(out Vector2 position, out Vector2 size)
        {
            position = default;
            size = default;

            var addonPtr = (nint)gameGui.GetAddonByName("LookingForGroupDetail");
            if (addonPtr == IntPtr.Zero)
                return false;

            var addon = (AtkUnitBase*)addonPtr;
            if (!addon->IsVisible || addon->RootNode == null)
                return false;

            float scale = addon->Scale <= 0f ? 1f : addon->Scale;
            position = new Vector2(addon->X, addon->Y);
            size = new Vector2(addon->RootNode->Width * scale, addon->RootNode->Height * scale);
            return size.X > 0f && size.Y > 0f;
        }

        /// <summary>True when there is a viewed listing worth offering to save.</summary>
        public unsafe bool HasViewedListing()
        {
            var agent = AgentLookingForGroup.Instance();
            return agent != null && agent->LastViewedListing.ListingId != 0;
        }

        /// <summary>
        /// Name of an existing preset that already describes the open listing, or null when it
        /// hasn't been saved. Compared on what a preset actually posts - duty, objective, comment,
        /// conditions, loot and languages - so re-saving the same listing is recognised whether it
        /// was saved a moment ago or last week.
        /// </summary>
        public unsafe string? FindPresetMatchingViewedListing(Configuration config)
        {
            try
            {
                var agent = AgentLookingForGroup.Instance();
                if (agent == null || agent->LastViewedListing.ListingId == 0)
                    return null;

                var candidate = BuildPresetFromListing(agent->LastViewedListing);
                string signature = PresetSignature(candidate);

                return config.Presets.FirstOrDefault(p => PresetSignature(p) == signature)?.Name;
            }
            catch (Exception ex)
            {
                pluginLog.Error(ex, "[SaveFromListing] Failed while checking for an existing preset.");
                return null;
            }
        }

        /// <summary>
        /// Everything about a preset that affects the listing it posts, flattened for comparison.
        /// Deliberately excludes name, id and timestamps - two presets that post the same listing
        /// are the same listing regardless of what they're called.
        /// </summary>
        private static string PresetSignature(PfPresetData p) => string.Join('|',
            p.DutyRowId,
            p.DutyName,
            p.DutyCategoryId,
            p.ObjectiveId,
            p.Comment,
            p.CompletionStatusEnabled ? p.CompletionStatusType : -1,
            p.AvgItemLvEnabled ? p.AvgItemLv : -1,
            p.LootRules,
            p.UnrestrictedParty, p.MinimumIL, p.SilenceEcho,
            p.LangJapanese, p.LangEnglish, p.LangGerman, p.LangFrench,
            p.AutoAdjustRoles,
            // Two presets that differ only by the fake-melee slot post different listings, so the
            // signature has to see it - otherwise saving one would look like a duplicate of the other.
            p.AllowDoubleCaster,
            p.NoteDoubleCasterInComment);

        /// <summary>
        /// Builds a preset from the listing currently open and adds it to the configuration.
        /// Returns the new preset, or null with a user-facing reason in <paramref name="error"/>.
        /// </summary>
        public unsafe PfPresetData? SaveViewedListingAsPreset(Configuration config, out string error)
        {
            error = string.Empty;

            try
            {
                var agent = AgentLookingForGroup.Instance();
                if (agent == null)
                {
                    error = "The Party Finder isn't available right now.";
                    chatGui.Print($"[PF Analysis] {error}");
                    return null;
                }

                var listing = agent->LastViewedListing;
                if (listing.ListingId == 0)
                {
                    error = "No listing is open. Open one in the Party Finder first.";
                    chatGui.Print($"[PF Analysis] {error}");
                    return null;
                }

                var preset = BuildPresetFromListing(listing);
                config.Presets.Add(preset);
                config.CountPresetCreated();
                config.Save();

                pluginLog.Information(
                    $"[SaveFromListing] Saved '{preset.Name}' from listing {listing.ListingId}.");
                return preset;
            }
            catch (Exception ex)
            {
                // This runs from a button over the game's window - a throw here would take the
                // render thread with it, so nothing is allowed to escape.
                pluginLog.Error(ex, "[SaveFromListing] Failed to build a preset from the listing.");
                error = "Could not read that listing. See /xllog for details.";
                // Reported in chat rather than on the overlay: the overlay is a bare button with
                // nowhere to put a message.
                chatGui.Print($"[PF Analysis] {error}");
                return null;
            }
        }

        /// <summary>Maps a viewed listing onto a preset, clamping every field as it goes.</summary>
        private unsafe PfPresetData BuildPresetFromListing(AgentLookingForGroup.Detailed listing)
        {
            int categoryId = CategoryIdFromFlags(listing.Category);
            var duty = listing.DutyId != 0 ? dutyDataHelper.GetDutyEntry(listing.DutyId) : null;

            string dutyName = duty?.Name
                ?? (listing.DutyId != 0 ? $"Unknown duty ({listing.DutyId})" : "None");

            // Prefer the duty's own content type - the listing's category flag is coarser and can
            // disagree with how the editor groups duties.
            string categoryName = duty?.ContentTypeName
                ?? (categoryId >= 0 && categoryId < DutyCategories.Names.Length
                    ? DutyCategories.Names[categoryId]
                    : "None");

            if (duty != null)
            {
                int matched = Array.FindIndex(DutyCategories.Names,
                    n => string.Equals(n, duty.ContentTypeName, StringComparison.OrdinalIgnoreCase));
                if (matched >= 0)
                    categoryId = matched;
            }

            var preset = new PfPresetData
            {
                Name = BuildPresetName(dutyName, listing),
                DutyCategoryId = Math.Clamp(categoryId, 0, DutyCategories.Names.Length - 1),
                DutyCategoryName = categoryName,
                // Synthetic high-end ids are unstable, so they save as 0 and fall back to the name.
                DutyRowId = duty != null && !DutyDataHelper.IsSyntheticRowId(duty.RowId) ? duty.RowId : 0,
                DutyName = dutyName,
                ObjectiveId = ObjectiveIdFromFlags(listing.Objective),
                Comment = TrimComment(listing.CommentString.ToString() ?? string.Empty),

                CompletionStatusEnabled = listing.CompletionStatus != AgentLookingForGroup.CompletionStatus.None,
                CompletionStatusType = CompletionTypeFromFlags(listing.CompletionStatus),

                AvgItemLvEnabled = listing.AvgItemLv > 0,
                AvgItemLv = listing.AvgItemLv > 0 ? Math.Clamp((int)listing.AvgItemLv, 1, 9999) : 1,

                LootRules = Math.Clamp((int)listing.LootRule, 0, 2),

                UnrestrictedParty = listing.DutyFinderSettingFlags.HasFlag(AgentLookingForGroup.DutyFinderSetting.UnrestrictedParty),
                MinimumIL = listing.DutyFinderSettingFlags.HasFlag(AgentLookingForGroup.DutyFinderSetting.MinimumIL),
                SilenceEcho = listing.DutyFinderSettingFlags.HasFlag(AgentLookingForGroup.DutyFinderSetting.SilenceEcho),

                LangJapanese = listing.LanguageFlags.HasFlag(AgentLookingForGroup.Language.Japanese),
                LangEnglish = listing.LanguageFlags.HasFlag(AgentLookingForGroup.Language.English),
                LangGerman = listing.LanguageFlags.HasFlag(AgentLookingForGroup.Language.German),
                LangFrench = listing.LanguageFlags.HasFlag(AgentLookingForGroup.Language.French),

                // Seats are deliberately not copied. A listing's slot masks describe the state it
                // was in when we looked - seats already taken, roles narrowed as it filled - which
                // is a poor thing to freeze into a reusable preset. Auto-adjust fills around
                // whoever is actually in your party instead.
                AutoAdjustRoles = true,
            };

            // A listing with no languages ticked can't be posted; fall back to accepting all.
            if (!preset.LangJapanese && !preset.LangEnglish && !preset.LangGerman && !preset.LangFrench)
                preset.LangJapanese = preset.LangEnglish = preset.LangGerman = preset.LangFrench = true;

            return preset;
        }

        /// <summary>A name that says where the preset came from, kept unique against what's saved.</summary>
        private static string BuildPresetName(string dutyName, AgentLookingForGroup.Detailed listing)
        {
            string baseName = string.IsNullOrWhiteSpace(dutyName) || dutyName == "None"
                ? "Copied listing"
                : dutyName;

            if (baseName.Length > 48)
                baseName = baseName.Substring(0, 48).TrimEnd();

            return baseName;
        }

        private static string TrimComment(string comment) =>
            comment.Length > MaxCommentLength ? comment.Substring(0, MaxCommentLength) : comment;

        /// <summary>DutyCategory is a flags enum where the category id is the set bit's position.</summary>
        private static int CategoryIdFromFlags(AgentLookingForGroup.DutyCategory category)
        {
            uint value = (uint)category;
            if (value == 0) return 0;
            return System.Numerics.BitOperations.TrailingZeroCount(value);
        }

        /// <summary>Objective is also a single-bit flag; the preset stores the bit position.</summary>
        private static int ObjectiveIdFromFlags(AgentLookingForGroup.Objective objective)
        {
            uint value = (uint)objective;
            if (value == 0) return 0;
            return Math.Clamp(System.Numerics.BitOperations.TrailingZeroCount(value), 0, 3);
        }

        private static int CompletionTypeFromFlags(AgentLookingForGroup.CompletionStatus status) => status switch
        {
            AgentLookingForGroup.CompletionStatus.DutyComplete => 0,
            AgentLookingForGroup.CompletionStatus.DutyCompleteWeeklyUnclaimed => 1,
            AgentLookingForGroup.CompletionStatus.DutyIncomplete => 2,
            _ => 0,
        };
    }
}
