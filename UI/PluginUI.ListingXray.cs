using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;

namespace PfPresets
{
    /// <summary>
    /// The panel beside an open party finder listing: who is in it, and how far along they are.
    ///
    /// DELIBERATELY ONLY THAT. It carried the jobs, the slot count, the item level and the comment
    /// for a while, and all four are already on the game's own window six inches to the left -
    /// repeating them made the panel look busy while adding nothing. What the game does not show is
    /// who these people are and whether they have cleared the thing, so that is all this shows.
    ///
    /// Names come from two places and the panel does not distinguish them, because the reader does
    /// not care: characters this client already knows (party, free company, linkshells) and people
    /// who published themselves through <see cref="PfCrowdsource"/>.
    /// </summary>
    public partial class PluginUI
    {
        private const float ListingPanelWidth = 250f;
        private const float ListingPanelGap = 8f;

        private void DrawListingPanel()
        {
            var xray = Listings;
            if (xray == null || !config.ListingDetailsEnabled)
                return;

            // The listing window is the anchor AND the lifetime. No window, no panel - without this
            // the last listing looked at would sit on screen for the rest of the session.
            if (!pfAutomation.TryGetListingWindowRect(out var anchor, out var anchorSize))
                return;

            var snapshot = xray.Current;

            if (!xray.Active && !xray.SuppressedByPfRadar)
                return;

            if (xray.Active && snapshot == null)
                return;

            var pos = new Vector2(anchor.X + anchorSize.X + ListingPanelGap, anchor.Y);

            ImGui.SetNextWindowPos(pos, ImGuiCond.Always);
            ImGui.SetNextWindowSize(new Vector2(ListingPanelWidth, 0), ImGuiCond.Always);

            ImGui.PushStyleColor(ImGuiCol.WindowBg, BgOuter);
            ImGui.PushStyleColor(ImGuiCol.Border, BorderDefault);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 1f);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 0f);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(12, 10));

            const ImGuiWindowFlags flags =
                ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove
                | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoSavedSettings
                | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoFocusOnAppearing;

            if (ImGui.Begin("##PfpListingPanel", flags))
            {
                try
                {
                    if (xray.SuppressedByPfRadar)
                        DrawListingSuppressed();
                    else
                        DrawListingRoster(snapshot!);
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

            ImGui.PopStyleVar(3);
            ImGui.PopStyleColor(2);
        }

        private void DrawListingSuppressed()
        {
            ImGui.PushTextWrapPos(0);
            using (UiHelpFont.Push())
            {
                ImGui.TextColored(Faint,
                    "PFRadar is running, so this panel stays out of its way - both read the same "
                    + "part of the game and only one of them should. Disable PFRadar to use this "
                    + "instead.");
            }
            ImGui.PopTextWrapPos();
        }

        /// <summary>One person on the panel: who they are, and how far along, if we know.</summary>
        private readonly struct ListingPerson
        {
            public ListingPerson(string name, string world)
            {
                Name = name;
                World = world;
            }

            public string Name { get; }
            public string World { get; }

            public bool Valid => Name.Length > 0 && World.Length > 0;
        }

        private void DrawListingRoster(ListingSnapshot snapshot)
        {
            var people = CollectListingPeople(snapshot);

            if (people.Count == 0)
            {
                using (UiHelpFont.Push())
                    ImGui.TextColored(Faint, "Nobody in this listing is known here yet.");
                return;
            }

            uint dutyRowId = snapshot.DutyId;
            string dutyName = dutyDataHelper.GetDutyName(dutyRowId) ?? string.Empty;

#if PFP_RATINGS
            // One batched read for the whole panel, the same call the recruit tab makes. Asking per
            // line would be a request per person per frame.
            EnsureListingProgress(people, dutyName);
#endif

            foreach (var person in people)
                DrawListingPersonLine(person, dutyName, dutyRowId);

#if PFP_RATINGS
            DrawListingLookupButton(people, dutyName, dutyRowId);
#endif
        }

#if PFP_RATINGS
        private void EnsureListingProgress(List<ListingPerson> people, string dutyName)
        {
            if (Ratings == null || string.IsNullOrWhiteSpace(dutyName) || people.Count == 0)
                return;

            var party = new List<(CharacterIdentity Who, string Region)>();

            foreach (var person in people)
            {
                var who = new CharacterIdentity(person.Name, person.World);
                if (!who.IsValid)
                    continue;

                string? region = Worlds?.GetFfLogsRegion(who.World);
                if (string.IsNullOrWhiteSpace(region))
                    continue;

                party.Add((who, region));
            }

            if (party.Count > 0)
                Ratings.EnsureProgressLoaded(dutyName, party);
        }
#endif

#if PFP_RATINGS
        /// <summary>
        /// Fetches progression for everybody on the panel who has none.
        ///
        /// ONE BUTTON FOR THE WHOLE LIST, not one per line. A press is what sends names to FFLogs
        /// and Tomestone, so it is worth being a deliberate act - and asking about seven people one
        /// at a time would be seven deliberate acts to answer one question.
        ///
        /// It only asks about characters nothing is known about. Somebody already fetched is
        /// skipped, so pressing twice does not spend the lookup budget re-reading what is on
        /// screen.
        /// </summary>
        private void DrawListingLookupButton(List<ListingPerson> people, string dutyName,
            uint dutyRowId)
        {
            // THE DUTY HAS TO BE ONE THE SERVER CAN NAME, and for a listing somebody else owns it
            // often is not. `Detailed.DutyId` is the party finder's own id, not a
            // ContentFinderCondition row, so GetDutyName answers "Unknown (1234)" - a string that
            // looks like a duty, passes every emptiness check, and matches nothing on the server.
            // Requests built on it left silently, which is the "button works but does nothing".
            bool namedDuty = dutyName.Length > 0
                && !dutyName.StartsWith("Unknown", StringComparison.Ordinal)
                && !string.Equals(dutyName, "None", StringComparison.Ordinal);

            var wanted = new List<CharacterIdentity>();

            foreach (var person in people)
            {
                var who = new CharacterIdentity(person.Name, person.World);
                if (!who.IsValid)
                    continue;

                // A region is required or RequestOneProgress drops the request without a word.
                if (string.IsNullOrWhiteSpace(Worlds?.GetFfLogsRegion(who.World)))
                    continue;

                if (Ratings?.PlayerProgressPending(who) == true)
                    continue;

                // NO LOCAL COOLDOWN ON THIS BUTTON, deliberately.
                //
                // It briefly obeyed the recruit tab's per-press rest, which was wrong twice over:
                // that rest exists to stop one party being re-read every few seconds, and this
                // button is not a party - it is a listing somebody is deciding about right now,
                // and being told to wait a minute before finding out who is in it is the whole
                // feature refusing to work.
                //
                // The limit that matters is the server's, fifteen minutes per CHARACTER, and it is
                // enforced there before anything reaches a provider token. So this queues freely
                // and lets the server refuse what it has already answered recently - which costs
                // nothing, because a refusal never leaves our own API.
                wanted.Add(who);
            }

            bool blocked = wanted.Count == 0 || !namedDuty;

            if (blocked)
                ImGui.BeginDisabled();

            // RequestOneProgress is what the party panel's own progress button calls, so a lookup
            // started here is the same lookup, subject to the same cooldowns, filling the same
            // cache. Two buttons, one path.
            if (DrawSecondaryButton("Look up progress##listingLookupAll",
                    new Vector2(-1, ButtonHeight))
                && !blocked)
            {
                foreach (var who in wanted)
                    RequestOneProgress(who, dutyName);
            }

            if (blocked)
                ImGui.EndDisabled();

            if (ImGui.IsItemHovered())
            {
                PaddedTooltip(!namedDuty
                    ? "This listing's duty isn't one progression can be looked up against."
                    : wanted.Count > 0
                        ? $"Fetch progression for {wanted.Count} of them."
                        : "Nobody here can be looked up right now.");
            }
        }
#endif

        /// <summary>
        /// Everybody the panel can put a name to, de-duplicated.
        ///
        /// The leader first because the listing names them and they are the one certainty; then
        /// anybody this client already knew; then anybody who published themselves.
        /// </summary>
        private List<ListingPerson> CollectListingPeople(ListingSnapshot snapshot)
        {
            var people = new List<ListingPerson>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void Add(string name, string world)
            {
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(world))
                    return;

                if (seen.Add($"{name}@{world}"))
                    people.Add(new ListingPerson(name, world));
            }

            Add(snapshot.Leader, LeaderWorldFor(snapshot));

            foreach (var slot in snapshot.Slots)
            {
                if (slot.Named)
                    Add(slot.Name, Worlds?.GetWorldName(slot.HomeWorldId) ?? string.Empty);
            }

#if PFP_RATINGS
            foreach (var member in CrowdRosterFor(snapshot))
                Add(member.Name, member.World);
#endif

            return people;
        }

        /// <summary>
        /// One line: the character on the left, how far along on the right.
        ///
        /// The right-hand side is <see cref="ProgressCellFor"/> - the same call the party panel in
        /// the recruit tab makes, so the two read from one cache and say the same thing about the
        /// same person. This panel briefly had its own progress calculation off the clears data,
        /// which meant two lookups, two stores and two ways for a character to be described.
        /// </summary>
        private void DrawListingPersonLine(ListingPerson person, string dutyName, uint dutyRowId)
        {
            float width = ImGui.GetContentRegionAvail().X;

            var who = new CharacterIdentity(person.Name, person.World);
            var cell = who.IsValid ? ProgressCellFor(who, dutyName, dutyRowId) : null;

            // A cell the party panel would have drawn as a PRESSABLE button - "Fetch" and friends -
            // is not one here, because this panel has a single lookup button for the whole list.
            // Drawing its label anyway produced dead text that looked like a control and was not,
            // which is worse than saying nothing. Reduced to the same dash as "nothing known".
            string progress = cell is { IsButton: false } ? cell.Value.Text : "—";
            var colour = cell is { IsButton: false } ? cell.Value.Colour : Faint;

            using (UiBodyFont.Push())
            {
                float progressWidth = progress.Length > 0 ? ImGui.CalcTextSize(progress).X : 0f;
                Vector2 start = ImGui.GetCursorScreenPos();

                // The name is what gets cut when the panel is too narrow, never the number - a
                // truncated name is still recognisable and a truncated percentage is a lie.
                string label = Fit($"{person.Name} @ {person.World}",
                    width - progressWidth - 10f);

                ImGui.TextColored(Ink, label);

                if (ImGui.IsItemHovered() && cell is { Tip.Length: > 0 })
                    PaddedTooltip(cell.Value.Tip);

                if (progress.Length > 0)
                {
                    var dl = ImGui.GetWindowDrawList();
                    dl.AddText(new Vector2(start.X + width - progressWidth, start.Y),
                        ImGui.ColorConvertFloat4ToU32(colour), progress);
                }
            }

            // Breathing room between rows, matched to the button below rather than left at the
            // font's own line spacing - the list reads as a list, not as a paragraph.
            ImGui.Dummy(new Vector2(0, 4));
        }

#if PFP_RATINGS

        private IReadOnlyList<PfMember> CrowdRosterFor(ListingSnapshot snapshot)
        {
            if (snapshot.Leader.Length == 0)
                return Array.Empty<PfMember>();

            string world = LeaderWorldFor(snapshot);
            if (world.Length == 0)
                return Array.Empty<PfMember>();

            return Crowd?.RosterFor(snapshot.Leader, world) ?? Array.Empty<PfMember>();
        }
#endif

        /// <summary>
        /// The leader's world.
        ///
        /// Taken from the addon rather than from the snapshot, because the snapshot has a bare name
        /// and anything downstream needs name@world. The listing the window is showing IS the
        /// listing the hook captured, so the two cannot disagree.
        /// </summary>
        private string LeaderWorldFor(ListingSnapshot snapshot)
        {
            if (!pfAutomation.TryGetViewedListingLeader(out _, out uint homeWorldId))
                return string.Empty;

            return Worlds?.GetWorldName(homeWorldId) ?? string.Empty;
        }
    }
}
