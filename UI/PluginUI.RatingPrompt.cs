#if PFP_RATINGS
using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;

namespace PfPresets
{
    /// <summary>
    /// The post-duty vote window: as soon as you leave a duty, the people you played with, each
    /// with an up and a down button.
    ///
    /// It only appears when there is actually someone to vote on. If everyone in that duty has
    /// already been voted on, or the duty had nobody else in it, nothing happens - a prompt with
    /// no available action is just an interruption.
    ///
    /// It waits for you to actually leave the instance. The game reports a duty complete the
    /// moment the last boss dies, which is a minute or so before anyone is out, and putting the
    /// window on screen mid-cutscene would be the most annoying possible timing.
    ///
    /// Skipping is permanent for that duty. It never reappears for the same run; the people in it
    /// stay in the Ratings tab for an hour if you change your mind.
    ///
    /// Not a modal: nothing dims, nothing takes focus, nothing blocks input, and it never appears
    /// while you're back inside content.
    /// </summary>
    public partial class PluginUI
    {
        /// <summary>Set by Plugin after construction.</summary>
        internal RatingService? Ratings { get; set; }
        internal WorldHelper? Worlds { get; set; }
        internal EncounterStore? Encounters { get; set; }
        internal RatingHistory? History { get; set; }

        /// <summary>Everyone ever met, kept permanently. Backs the Recent players list and is the
        /// whole of what search looks through.</summary>
        internal PlayerHistory? Players { get; set; }

        /// <summary>The character the plugin is acting as, or null when nobody is logged in. Set by
        /// Plugin, so the UI doesn't need its own copy of the game-state plumbing.</summary>
        internal Func<CharacterIdentity?>? LocalIdentity { get; set; }

        /// <summary>Which alliance sub-tab is showing, when the duty had more than a light party.</summary>
        private int promptAlliance;

        private const float PromptWidth = 380f;
        private const float PromptMargin = 24f;

        private DutyEncounter? promptEncounter;

        /// <summary>
        /// The rows the prompt is showing, captured when it opens.
        ///
        /// Fixed rather than re-derived each frame: the eligible list drops a player the instant
        /// they're rated, which would snatch the row away mid-animation. Holding the list still
        /// lets each row play its own exit and lets the window notice when they've all gone.
        /// </summary>
        private readonly List<Contact> promptRows = new();

        /// <summary>When the last row finished leaving, which starts the closing beat.</summary>
        private DateTime promptFinishedUtc = DateTime.MinValue;

        /// <summary>Status line shared with the Ratings tab, written from background tasks.</summary>
        private volatile string ratingStatusMessage = string.Empty;
        private DateTime ratingStatusExpiresUtc = DateTime.MinValue;

        /// <summary>
        /// Raised by <see cref="DutyTracker"/> on the framework thread when a duty finishes. Only
        /// queues the window - drawing happens on the render thread as usual.
        /// </summary>
        internal void OnEncounterCompleted(DutyEncounter encounter)
        {
            if (!config.CommunityEnabled || !config.PostDutyPromptEnabled)
                return;

            if (encounter.Dismissed)
                return;

            // Nobody left to rate means nothing worth showing.
            if (EligibleInEncounter(encounter.Id).Count == 0)
                return;

            promptEncounter = encounter;
            promptAlliance = 0;
            promptFinishedUtc = DateTime.MinValue;

            promptRows.Clear();
            promptRows.AddRange(EligibleInEncounter(encounter.Id));
        }

        private void DrawRatingPrompt()
        {
            if (promptEncounter == null)
                return;

            var encounter = promptEncounter;

            // Turned off mid-flight - drop it.
            if (!config.CommunityEnabled || !config.PostDutyPromptEnabled)
            {
                promptEncounter = null;
                return;
            }

            // The game reports a duty complete while you are still standing in the instance, so
            // this waits rather than clearing: the window appears the moment you actually leave.
            // Clearing here instead would mean it never appeared at all.
            if (pfAutomation.IsInDuty())
                return;

            if (promptRows.Count == 0)
            {
                promptEncounter = null;
                return;
            }

            // Everyone rated and every row finished leaving: hold the thank-you briefly, then go.
            if (AllRowsGone())
            {
                if (promptFinishedUtc == DateTime.MinValue)
                    promptFinishedUtc = DateTime.UtcNow;

                if ((DateTime.UtcNow - promptFinishedUtc).TotalMilliseconds > 1100)
                {
                    promptEncounter = null;
                    return;
                }
            }

            if (Ratings != null)
            {
                var identities = new List<CharacterIdentity>(promptRows.Count);
                foreach (var c in promptRows)
                    identities.Add(c.Identity);
                Ratings.Prefetch(identities);
            }

            var viewport = ImGui.GetMainViewport();
            var pos = new Vector2(
                viewport.WorkPos.X + viewport.WorkSize.X - PromptWidth - PromptMargin,
                viewport.WorkPos.Y + viewport.WorkSize.Y - PromptMargin);

            ImGui.SetNextWindowPos(pos, ImGuiCond.Always, new Vector2(0f, 1f));
            ImGui.SetNextWindowSize(new Vector2(PromptWidth, 0), ImGuiCond.Always);

            const ImGuiWindowFlags flags =
                ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove |
                ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse |
                ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoFocusOnAppearing |
                ImGuiWindowFlags.NoNavFocus | ImGuiWindowFlags.NoSavedSettings |
                ImGuiWindowFlags.AlwaysAutoResize;

            ImGui.PushStyleColor(ImGuiCol.WindowBg, BgOuter);
            ImGui.PushStyleColor(ImGuiCol.Border, BorderDefault);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 1.0f);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 0f);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(14, 12));

            try
            {
                if (ImGui.Begin("##PfPresetsRatingPrompt", flags))
                {
                    try
                    {
                        DrawPromptBody(encounter, promptRows);
                    }
                    finally
                    {
                        ImGui.End();
                    }
                }
                else
                {
                    ImGui.End();
                }
            }
            finally
            {
                ImGui.PopStyleVar(3);
                ImGui.PopStyleColor(2);
            }
        }

        /// <summary>True once every row has been rated and finished collapsing away.</summary>
        private bool AllRowsGone()
        {
            foreach (var contact in promptRows)
            {
                var state = StateFor(contact.Identity);
                if (!state.Done || RowExitProgress(state) < 1f)
                    return false;
            }
            return true;
        }

        private void DrawPromptBody(DutyEncounter encounter, List<Contact> rows)
        {
            if (AllRowsGone())
            {
                DrawPromptFinished();
                return;
            }

            ImGui.TextColored(TextPrimary, "Rate your group");

            ImGui.SameLine();
            ImGui.SetCursorPosX(Math.Max(ImGui.GetCursorPosX(), PromptWidth - 44f));
            if (DrawGlyphButton(FontAwesomeIcon.Times, "PromptClose", new Vector2(22, 20), TextMuted))
                SkipPrompt(encounter);
            if (ImGui.IsItemHovered())
                PaddedTooltip("Skip. This duty won't ask again.");

            ImGui.Dummy(new Vector2(0, 6));

            var shown = DrawAllianceTabs(rows);

            foreach (var contact in shown)
                DrawRateRow(contact, showDutyLine: false);

            if (!string.IsNullOrEmpty(ratingStatusMessage) && DateTime.UtcNow <= ratingStatusExpiresUtc)
            {
                ImGui.PushTextWrapPos(PromptWidth - 28);
                ImGui.TextColored(AccentYellow, ratingStatusMessage);
                ImGui.PopTextWrapPos();
            }

            ImGui.Dummy(new Vector2(0, 2));
            if (DrawSecondaryButton("Skip##PromptSkip", new Vector2(-1, ButtonHeight)))
                SkipPrompt(encounter);
        }

        /// <summary>
        /// Splits an alliance raid across three sub-tabs. Twenty-four rows in one window would be
        /// taller than the screen; a light party gets no tabs at all, because eight rows don't need
        /// splitting and the chrome would be pure cost.
        /// </summary>
        private List<Contact> DrawAllianceTabs(List<Contact> eligible)
        {
            bool alliance = eligible.Exists(c => c.Member.AllianceIndex > 0);
            if (!alliance)
                return eligible;

            var groups = new List<Contact>[3];
            for (int i = 0; i < 3; i++)
                groups[i] = eligible.FindAll(c => Math.Clamp(c.Member.AllianceIndex, 0, 2) == i);

            // The count on each tab is who's still waiting, so rows mid-exit shouldn't be counted.
            int Remaining(List<Contact> group)
            {
                int n = 0;
                foreach (var c in group)
                {
                    if (!StateFor(c.Identity).Done)
                        n++;
                }
                return n;
            }

            float w = (PromptWidth - 28 - 8) / 3f;
            for (int i = 0; i < 3; i++)
            {
                if (i > 0)
                    ImGui.SameLine(0, 4);

                bool on = promptAlliance == i;
                ImGui.PushStyleColor(ImGuiCol.Button, on ? BgCard : BgCardExpanded);
                ImGui.PushStyleColor(ImGuiCol.ButtonHovered, on ? BgCard : BorderHover);
                ImGui.PushStyleColor(ImGuiCol.Text, on ? TextPrimary : TextMuted);
                ImGui.PushStyleColor(ImGuiCol.Border, on ? AccentBlue : BorderDefault);
                ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 1.0f);
                ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 0f);

                // The count is how many are still unrated there, so you can see where the
                // remaining work is without clicking through.
                int left = Remaining(groups[i]);
                string label = left > 0 ? $"{(char)('A' + i)}  {left}" : $"{(char)('A' + i)}";

                if (ImGui.Button($"{label}##ally{i}", new Vector2(w, 22)))
                    promptAlliance = i;

                ImGui.PopStyleVar(2);
                ImGui.PopStyleColor(4);
            }

            ImGui.Dummy(new Vector2(0, 6));

            int pick = Math.Clamp(promptAlliance, 0, 2);
            return groups[pick];
        }

        /// <summary>The people from one duty who can still be rated, cooldown included. Same rule
        /// as the Ratings tab, so the two can't disagree about who is rateable.</summary>
        private List<Contact> EligibleInEncounter(string encounterId)
        {
            var candidates = Encounters?.EligibleInEncounter(encounterId) ?? new List<Contact>();
            if (Ratings == null)
                return candidates;

            var result = new List<Contact>(candidates.Count);
            foreach (var c in candidates)
            {
                if (Ratings.LocalCooldownUntil(c.Identity) == null)
                    result.Add(c);
            }
            return result;
        }

        /// <summary>
        /// The closing beat: a tick that fades up once the last row has gone, held for a moment so
        /// the window doesn't simply blink out of existence the instant the final click lands.
        /// </summary>
        private void DrawPromptFinished()
        {
            // The other end of a task. Offered once, from the frame the thanks appears on.
            OfferVoteNudge();

            double ms = promptFinishedUtc == DateTime.MinValue
                ? 0d
                : (DateTime.UtcNow - promptFinishedUtc).TotalMilliseconds;

            float t = (float)Math.Clamp(ms / 220d, 0d, 1d);
            var colour = new Vector4(AccentGreen.X, AccentGreen.Y, AccentGreen.Z, t);

            ImGui.Dummy(new Vector2(0, 6));

            const string text = "Thanks";
            float w = ImGui.CalcTextSize(text).X + 26f;
            ImGui.SetCursorPosX(Math.Max(0f, (PromptWidth - 28f - w) * 0.5f));

            DrawGlyph(FontAwesomeIcon.Check, colour);
            ImGui.SameLine(0, 8);
            ImGui.TextColored(new Vector4(TextSecondary.X, TextSecondary.Y, TextSecondary.Z, t), text);

            ImGui.Dummy(new Vector2(0, 6));
        }

        /// <summary>Marks the duty skipped so it never prompts again, and closes the window. The
        /// people in it remain votable from the Ratings tab until the window closes.</summary>
        private void SkipPrompt(DutyEncounter encounter)
        {
            Encounters?.Dismiss(encounter.Id);
            promptEncounter = null;
        }
    }
}
#endif
