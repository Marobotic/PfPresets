#if PFP_RATINGS
using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;

namespace PfPresets
{
    /// <summary>
    /// The party member list: everyone currently in your party, with their community rating, and -
    /// when you are the leader - the actions you can take about them.
    ///
    /// This is where ratings surface, rather than over Party Finder listings. A score attached to a
    /// listing you have not joined is a screening tool for strangers; a score next to someone you
    /// are actually playing with is context. The difference matters enough to have moved it.
    ///
    /// Drawn with ordinary ImGui widgets in its own panel rather than folded into the status card
    /// above it, because that card is painted straight onto the draw list at absolute coordinates
    /// and has no room for per-row buttons.
    /// </summary>
    public partial class PluginUI
    {
        internal PartyCommands? Party { get; set; }

        /// <summary>
        /// The character whose report dialog is open, or null.
        ///
        /// An identity rather than a party member: reports are now also raised from the recent
        /// players list, where the person is long gone from the party and only their name and
        /// world survive. Nothing in the dialog ever needed more than that.
        /// </summary>
        private CharacterIdentity? reportTarget;

        /// <summary>
        /// How many rows the party list will actually draw.
        ///
        /// Rows, not people, and the difference matters: Duty Support NPCs collapse to a single
        /// line and the progress control is a row of its own. The card measures its own height
        /// from this before drawing, and the moment the two disagree it draws past its bottom
        /// edge and over whatever comes next.
        /// </summary>
        private int PartyMemberCount(string? dutyName = null, uint dutyRowId = 0)
        {
            try
            {
                var members = pfAutomation.GetOtherPartyMemberDetails();

                int players = 0, npcs = 0;
                foreach (var m in members)
                {
                    if (m.IsSupportNpc) npcs++;
                    else players++;
                }

                int rows = players + (npcs > 0 ? 1 : 0);

                // Your own row, and the progress control, both only appear alongside a real party.
                if (players > 0)
                {
                    if (pfAutomation.GetLocalPartyMember() != null)
                        rows++;
                    if (ShowsProgressRow() && DutyHasProgress(dutyRowId))
                        rows++;
                }

                return rows;
            }
            catch (Exception) { return 0; }
        }

        /// <summary>
        /// The panel's heading. Inside a duty it names the duty, because that is the one thing in
        /// there the player cannot read off the party list itself - a bare "IN DUTY WITH" withheld
        /// it even though the snapshot had already resolved the name from the territory.
        ///
        /// The name is only present when that resolution succeeded, and the listing path uses the
        /// literal "None" for a listing with no specific duty, so both fall back to the unnamed
        /// wording rather than heading the panel with a gap or the word "None".
        /// </summary>
        private static string PartyPanelHeading(bool inDuty, string dutyName)
        {
            if (!inDuty)
                return "PARTY";

            bool named = !string.IsNullOrWhiteSpace(dutyName)
                && !dutyName.Equals("None", StringComparison.OrdinalIgnoreCase);

            return named ? $"In {dutyName} with" : "In duty with";
        }

        /// <summary>
        /// The standalone party panel, shown when the recruitment card isn't carrying the list
        /// itself - which in practice means inside a duty, or in a party with no listing up.
        /// </summary>
        private void DrawPartyPanel(RecruitmentSnapshot snap)
        {
            if (!config.RatingsEnabled || !config.PartyRatingsEnabled)
                return;

            // The card already shows these people; drawing them again here was the duplication.
            if (ShowsEmbeddedParty(snap))
                return;

            if (PartyMemberCount() == 0)
                return;

            bool inDuty = pfAutomation.IsInDuty();

            ImGui.Dummy(new Vector2(0, 2));
            ImGui.Indent(10);
            // Fit against the width left after the indent, so a long duty name ellipsises instead
            // of being clipped by the border.
            DrawSectionLabel(Fit(PartyPanelHeading(inDuty, snap.DutyName),
                ImGui.GetContentRegionAvail().X - 12f));
            ImGui.Unindent(10);

            // Kicking is impossible inside instanced content - the game refuses it - so the button
            // isn't offered there. The only exit available in a duty is your own.
            DrawPartyMembers(allowKick: !inDuty, width: ImGui.GetContentRegionAvail().X - 12f,
                dutyName: snap.DutyName, dutyRowId: snap.DutyRowId);

            if (inDuty)
                DrawLeaveDutyAction();

            ImGui.Dummy(new Vector2(0, 4));
        }

        /// <summary>
        /// Draws the member rows and returns the height used, so the recruitment card can reserve
        /// space for them when it embeds the list.
        /// </summary>
        private float DrawPartyMembers(bool allowKick, float width, float? originX = null,
            string? dutyName = null, uint dutyRowId = 0)
        {
            List<PartyMemberInfo> members;
            try
            {
                members = pfAutomation.GetOtherPartyMemberDetails();
            }
            catch (Exception)
            {
                return 0f;
            }

            if (members.Count == 0)
                return 0f;

            // Square Enix's NPCs are split out before anything else looks at the list: they are not
            // people, so they must not be rated, reported, prefetched or counted as company.
            var players = new List<PartyMemberInfo>(members.Count + 1);
            int supportNpcs = 0;
            foreach (var m in members)
            {
                if (m.IsSupportNpc)
                    supportNpcs++;
                else
                    players.Add(m);
            }

            // You, first, because that is where you are in the game's own party list. Only worth
            // drawing when there is a party to be part of - a one-row list of yourself standing
            // alone is noise.
            PartyMemberInfo? self = null;
            if (players.Count > 0)
            {
                try { self = pfAutomation.GetLocalPartyMember(); }
                catch (Exception) { self = null; }

                if (self != null)
                    players.Insert(0, self.Value);
            }

            // One batch request covers the whole party.
            if (Ratings != null && Worlds != null && players.Count > 0)
            {
                var identities = new List<CharacterIdentity>(players.Count);
                foreach (var m in players)
                {
                    var id = ToIdentity(m);
                    if (id != null)
                        identities.Add(id);
                }
                Ratings.Prefetch(identities);
            }

            float start = ImGui.GetCursorScreenPos().Y;

            foreach (var member in players)
            {
                bool isSelf = self != null && member.ContentId == self.Value.ContentId;

                // allowKick passed through unchanged, including for your own row: it decides how
                // much width the action column reserves, and that has to be the same on every row
                // or the columns left of it stop lining up. Whether the buttons are actually
                // drawn is decided per row.
                DrawPartyMemberRow(member, allowKick, width, originX, isSelf, dutyRowId);
            }

            if (supportNpcs > 0)
                DrawDutySupportNote(supportNpcs, dutyName, width, originX);

            // Nothing to look up means no row at all, rather than a row saying so - an empty
            // statement still costs a row of the card's height, and a button that can only fail
            // is worse than no button.
            if (players.Count > 0 && ShowsProgressRow() && DutyHasProgress(dutyRowId))
                DrawProgressRow(players, dutyName, width, originX);

            return ImGui.GetCursorScreenPos().Y - start;
        }

        /// <summary>
        /// The one action available from inside a duty.
        ///
        /// Hidden outright during combat rather than disabled. Mid-pull it is never the thing you
        /// meant to click, and a live "leave" button sitting under the cursor while you're fighting
        /// is a misclick waiting to happen.
        /// </summary>
        private void DrawLeaveDutyAction()
        {
            if (pfAutomation.IsInCombat())
                return;

            ImGui.Dummy(new Vector2(0, 6));

            const float w = 110f, h = 24f;

            // Right-aligned, matching every other action in the plugin.
            ImGui.SetCursorPosX(Math.Max(10f, ImGui.GetContentRegionMax().X - w - 10f));

            bool can = pfAutomation.CanLeaveDuty();
            ImGui.BeginDisabled(!can);

            // Red: leaving a duty costs a penalty and can't be undone, which is the same weight as
            // every other destructive action here.
            if (DrawDangerButton("Leave duty##LeaveDuty", new Vector2(w, h)))
            {
                AskConfirm("Leave duty", "Leave this duty?", "Yes, leave",
                    () => pfAutomation.LeaveDuty(),
                    detail: "You'll take the usual duty finder penalty.");
            }
            ImGui.EndDisabled();

            if (!can && ImGui.IsItemHovered())
                PaddedTooltip("The game won't let you leave right now.");
        }

        /// <summary>
        /// The clear badge, drawn to the left of the rating chip.
        ///
        /// Only ever states what the data supports. A logged clear is a fact and gets a tick;
        /// everything else is an absence, and an absence is shown as an absence rather than as
        /// an accusation - "no clear logged", never "hasn't cleared".
        ///
        /// <paramref name="dutyRowId"/> gates the badge: if the current context is not a fight
        /// that has progression (roulette, frontline, casual content), the badge is suppressed
        /// entirely so stale data from a previous session never bleeds through.
        /// </summary>
        private void DrawProgressBadge(CharacterIdentity who, float chipLeft, float rowY,
            uint dutyRowId = 0)
        {
            // No fight context → never show stale prog from a previous duty.
            if (!DutyHasProgress(dutyRowId))
                return;

            var p = Ratings?.ProgressFor(who);
            if (p == null)
                return;

            // Only states that carry information get a mark.
            //
            // "No clear logged", "no profile" and "couldn't reach FFLogs" all mean the same thing
            // to a reader - nothing is known - and drawing a dash for each of them filled the list
            // with marks that looked like data and weren't. Absence is shown as absence.
            string text;
            Vector4 colour;
            string tip;

            switch (p.Status)
            {
                case "cleared":
                    // Named rather than ticked, and tinted with their parse. A check mark says
                    // only "yes"; the colour is the thing raiders actually read, and it carries
                    // more than the word does.
                    text = "(Cleared)";
                    colour = p.Percentile >= 0 ? ParseColor(p.Percentile) : AccentGreen;
                    tip = "Has a logged clear of this duty."
                        + (p.Percentile >= 0
                            ? $"\n\nBest parse: {p.Percentile:0.#}% ({ParseName(p.Percentile)})"
                            : "\n\nNo parse available for this fight.");
                    break;

                case "progging":
                    text = p.ProgLabel;
                    colour = AccentYellow;
                    tip = "Furthest logged pull: " + p.ProgLabel
                        + "\n\nPercentage is the boss's remaining HP in that phase,\nso lower is further in."
                        + (p.LastSeenMs > 0 ? $"\nLast logged pull {AgoFromUnixMs(p.LastSeenMs)}." : string.Empty);
                    break;

                default:
                    // noclear / hidden / notfound / unknown - nothing worth drawing.
                    return;
            }

            bool isIcon = false;

            if (isIcon) ImGui.PushFont(UiBuilder.IconFont);
            float w = ImGui.CalcTextSize(text).X;
            if (isIcon) ImGui.PopFont();

            ImGui.SameLine();
            ImGui.SetCursorScreenPos(new Vector2(chipLeft - w - 8f, rowY));
            ImGui.AlignTextToFramePadding();

            if (isIcon) ImGui.PushFont(UiBuilder.IconFont);
            ImGui.TextColored(colour, text);
            if (isIcon) ImGui.PopFont();

            if (ImGui.IsItemHovered())
                PaddedTooltip(tip);
        }

        /// <summary>The party as identities with FFLogs regions, for a progress request.</summary>
        private List<(CharacterIdentity Who, string Region)> PartyIdentities(List<PartyMemberInfo> players)
        {
            var party = new List<(CharacterIdentity, string)>(players.Count);
            foreach (var m in players)
            {
                var id = ToIdentity(m);
                string? region = id == null ? null : Worlds?.GetFfLogsRegion(id.World);
                if (id != null && region != null)
                    party.Add((id, region));
            }
            return party;
        }

        /// <summary>
        /// Whether the progress control has any business being on screen.
        ///
        /// Does NOT test RatingApiBaseUrl. That setting is an override, and it is empty on every
        /// normal install because the client falls back to its built-in endpoint - so requiring it
        /// hid this control from everyone who hadn't hand-edited their config, which is everyone.
        /// </summary>
        private bool ShowsProgressRow() => config.RatingsEnabled && Ratings != null;

        /// <summary>
        /// Whether this duty is worth asking a progression provider about.
        ///
        /// "None" is a known nothing, not an unknown - there is no point spending a request to be
        /// told so, and no point offering a button that can only disappoint. Same for content
        /// nobody logs: a dungeon or a guildhest has no prog point by construction.
        ///
        /// Gated on content type rather than the duty's name, because the name is just a string
        /// and "None" reads the same as a fight we simply haven't heard of.
        /// </summary>
        private bool DutyHasProgress(uint dutyRowId)
        {
            if (dutyRowId == 0)
                return false;

            var entry = dutyDataHelper.GetDutyEntry(dutyRowId);
            if (entry == null)
                return false;

            bool current = entry.ExVersionId >= CurrentExVersion();

            return entry.ContentTypeName switch
            {
                // Every Ultimate, whatever its expansion. They never stop being progged - people
                // are still working on UCOB - so age is no reason to stop answering.
                "Ultimate Raids" => true,

                // Criterion, normal and savage alike, and likewise not age-limited: the older
                // ones are still run deliberately rather than out-levelled.
                "V&C Dungeon Finder" => true,

                // Raids and trials only while they are current. Savage, normal and alliance share
                // the "Raids" type, and an old tier is finished content - asking about it spends a
                // request to learn that somebody cleared it years ago.
                "Raids" => current,
                "Trials" => current,
                "Chaotic Alliance Raid" => current,

                // Current expansion and at the level cap. A levelling dungeon has no prog point
                // and nobody logs one, so asking can only come back empty.
                "Dungeons" => current && entry.ClassJobLevelRequired >= MaxDutyLevel(),

                _ => false,
            };
        }

        private uint currentExVersion;

        /// <summary>
        /// The newest expansion present in the duty table.
        ///
        /// Derived rather than named, for the same reason as the level cap: writing "Dawntrail"
        /// here would quietly exclude every new raid tier on launch day, and the symptom - a
        /// missing button - points nowhere near this line.
        /// </summary>
        private uint CurrentExVersion()
        {
            if (currentExVersion > 0)
                return currentExVersion;

            try
            {
                foreach (var duty in dutyDataHelper.GetAllDuties())
                {
                    if (duty.ExVersionId > currentExVersion)
                        currentExVersion = duty.ExVersionId;
                }
            }
            catch (Exception)
            {
                // Sheet unavailable. Zero would let everything through, which is the safer
                // failure here than blocking the content this exists to allow.
                currentExVersion = 0;
            }

            return currentExVersion;
        }

        private int maxDutyLevel;

        /// <summary>
        /// The current level cap, read from the duty table rather than hardcoded.
        ///
        /// A literal 100 here would silently start excluding the whole of the next expansion's
        /// dungeons the day it launches, and nothing would point at this line as the cause.
        /// </summary>
        private int MaxDutyLevel()
        {
            if (maxDutyLevel > 0)
                return maxDutyLevel;

            try
            {
                foreach (var duty in dutyDataHelper.GetAllDuties())
                {
                    if (duty.ClassJobLevelRequired > maxDutyLevel)
                        maxDutyLevel = duty.ClassJobLevelRequired;
                }
            }
            catch (Exception)
            {
                // Sheet unavailable; fall back to something that lets current content through
                // rather than blocking everything.
                maxDutyLevel = 100;
            }

            return maxDutyLevel > 0 ? maxDutyLevel : 100;
        }

        /// <summary>
        /// FFLogs' own parse colours, so a number means here what it means everywhere else.
        ///
        /// These are the site's bracket colours rather than this plugin's palette on purpose: a
        /// raider reads "orange parse" as a fact about the number, and re-tinting it would make
        /// the same parse look like a different one.
        /// </summary>
        private static Vector4 ParseColor(double percentile) => percentile switch
        {
            >= 100 => ColorFromHex("#e5cc80"),  // gold
            >= 99 => ColorFromHex("#e268a8"),   // pink
            >= 95 => ColorFromHex("#ff8000"),   // orange
            >= 75 => ColorFromHex("#a335ee"),   // purple
            >= 50 => ColorFromHex("#0070ff"),   // blue
            >= 25 => ColorFromHex("#1eff00"),   // green
            _ => ColorFromHex("#999999"),       // grey
        };

        /// <summary>The bracket's name, for the hover.</summary>
        private static string ParseName(double percentile) => percentile switch
        {
            >= 100 => "gold",
            >= 99 => "pink",
            >= 95 => "orange",
            >= 75 => "purple",
            >= 50 => "blue",
            >= 25 => "green",
            _ => "grey",
        };

        /// <summary>"3 days ago" for a unix-ms timestamp, reusing the wording the rest of the
        /// plugin already uses for relative times.</summary>
        private static string AgoFromUnixMs(long unixMs)
            => Ago(DateTimeOffset.FromUnixTimeMilliseconds(unixMs).UtcDateTime);

        /// <summary>
        /// The fetch control, and whatever the last fetch had to say.
        ///
        /// A button rather than an automatic call on party change. This is the one request the
        /// plugin makes that ends with someone else's character name at a third party, so it is
        /// something a person does deliberately - and it happens to be the cheap option for the
        /// API budget too.
        /// </summary>
        private void DrawProgressRow(List<PartyMemberInfo> players, string? dutyName, float width,
            float? originX)
        {
            if (Ratings == null)
                return;

            // Positioned in screen space from the row origin, exactly like DrawHoverRow, and
            // advancing by exactly one row height at the end.
            //
            // Indent() and ordinary cursor flow resolve against the window, not against the card
            // that embeds this list - which is why this row started left of the card and got
            // clipped. Ad-hoc padding also made it a different height from the row the card had
            // measured, so the last line fell outside the clip rect entirely.
            Vector2 cursor = ImGui.GetCursorScreenPos();
            float rowH = HoverRowHeight();
            float left = (originX ?? cursor.X) + 8f;
            float maxWidth = Math.Max(60f, width - 16f);

            ImGui.SetCursorScreenPos(new Vector2(left, cursor.Y + (rowH - 22f) * 0.5f));

            string duty = dutyName ?? string.Empty;

            // Reads whatever the server already has, once. Costs a database lookup there and no
            // provider call at all, so the panel shows the last known prog instead of nothing.
            var party = PartyIdentities(players);
            Ratings.EnsureProgressLoaded(duty, party);

            bool loaded = Ratings.HasProgressFor(duty);
            string? note = Ratings.ProgressNote;

            if (Ratings.ProgressQueued)
            {
                // Queued, not loading. The server fetches one character at a time for everybody
                // using the plugin, so a press joins a line rather than starting a lookup - and
                // the button has to say which of those two things happened.
                DrawQueuedButton(Ratings.ProgressQueuedCount, maxWidth);
            }
            // No state for the request itself.
            //
            // There used to be one, saying the plugin was busy fetching, and it stopped being
            // true: the request hands the party to the server's queue and returns in a couple of
            // hundred milliseconds without fetching anything. It was also reached by the panel
            // simply opening - the automatic read runs through the same call - so opening the
            // party list announced work nobody had asked for. The button is already disabled
            // across that gap, which says everything a sentence did.
            else if (note != null)
            {
                // The note is the only text here that isn't ours - a server message can be any
                // length, so it is fitted to the row rather than allowed to run off the edge.
                DrawProgressStatusText(note, maxWidth, note);
            }
            else
            {
                // No result summary. It said "7 progging" next to a list that already showed
                // every one of those seven prog points - the row's own badges are the report.
                float room = maxWidth;

                string label = "Update player progress";
                float needed = ImGui.CalcTextSize(label).X + 24f;

                // Never wider than the room left after the summary, and never so narrow the label
                // is clipped by the button itself - below that it falls back to the short form.
                if (needed > room)
                {
                    label = "Update progress";
                    needed = ImGui.CalcTextSize(label).X + 24f;
                }

                float buttonW = Math.Max(60f, Math.Min(needed, room));

                bool ready = Ratings.ProgressButtonReady;

                ImGui.BeginDisabled(!ready);
                bool pressed = DrawSecondaryButton(
                    ready ? $"{label}##FetchProgress"
                          : $"Updated · {(int)Ratings.ProgressButtonWait.TotalSeconds}s##FetchProgress",
                    new Vector2(buttonW, 22));
                ImGui.EndDisabled();

                if (pressed && ready)
                    Ratings.RequestPartyProgress(duty, party);
                else if (ImGui.IsItemHovered())
                {
                    PaddedTooltip(
                        (loaded ? "Re-checks this party against FFLogs.\n\n" : "Asks FFLogs how far this party has got.\n\n")
                        + "Sends their names and worlds to FFLogs - the one time this\n"
                        + "plugin sends anything to anyone but your own server. Cached\n"
                        + "results are shared, so this updates them for everyone.");
                }
            }

            ImGui.SetCursorScreenPos(new Vector2(originX ?? cursor.X, cursor.Y + rowH + 1f));
        }

        /// <summary>
        /// The button while the party is sitting on the server's queue.
        ///
        /// Still a button, still the same size, just disabled - swapping it for a line of text
        /// made the row change height and shove the party list around at the exact moment the
        /// user was reading it.
        /// </summary>
        private void DrawQueuedButton(int waiting, float maxWidth)
        {
            string label = waiting > 1 ? $"Queued · {waiting}" : "Queued";
            float width = Math.Max(60f, Math.Min(ImGui.CalcTextSize(label).X + 24f, maxWidth));

            ImGui.BeginDisabled(true);
            DrawSecondaryButton($"{label}##FetchProgress", new Vector2(width, 22));
            ImGui.EndDisabled();

            if (ImGui.IsItemHovered())
            {
                PaddedTooltip(
                    "Waiting on the server's lookup queue.\n\n"
                    + "One character is fetched at a time for everyone using the plugin,\n"
                    + "so the answer arrives shortly rather than instantly. Anyone else\n"
                    + "asking about the same player in the meantime joins this same wait\n"
                    + "instead of costing a second lookup.");
            }
        }

        /// <summary>Status line for the progress row, clipped to the row and hovering the full
        /// text when it had to be shortened.</summary>
        private void DrawProgressStatusText(string text, float maxWidth, string? fullText)
        {
            ImGui.AlignTextToFramePadding();
            string shown = Fit(text, maxWidth);
            ImGui.TextColored(TextMuted, shown);

            if (ImGui.IsItemHovered())
            {
                if (shown != text)
                    PaddedTooltip(fullText ?? text);
                else if (fullText == null)
                {
                    PaddedTooltip(
                        "\"No clear logged\" is not the same as \"hasn't cleared\" - plenty\n"
                        + "of people clear without ever uploading a log, and this cannot\n"
                        + "tell the difference.");
                }
            }
        }

        /// <summary>
        /// Stands in for the NPC rows.
        ///
        /// One line rather than N blank ones: the useful fact is that you are running this with
        /// Duty Support rather than with people, and that is a sentence, not a list.
        /// </summary>
        private void DrawDutySupportNote(int count, string? dutyName, float width, float? originX)
        {
            // Screen-space positioned and exactly one row tall, for the same reason as the
            // progress row - see the note there.
            Vector2 cursor = ImGui.GetCursorScreenPos();
            float rowH = HoverRowHeight();
            float left = (originX ?? cursor.X) + 8f;
            float maxWidth = Math.Max(60f, width - 16f);

            ImGui.SetCursorScreenPos(new Vector2(left, cursor.Y + (rowH - 22f) * 0.5f));

            string what = string.IsNullOrWhiteSpace(dutyName) ? "this duty" : dutyName;
            string label = $"Running {what} with Duty Support.";

            ImGui.AlignTextToFramePadding();
            string shown = Fit(label, maxWidth);
            ImGui.TextColored(TextMuted, shown);

            if (ImGui.IsItemHovered())
            {
                PaddedTooltip(
                    (shown != label ? label + "\n\n" : string.Empty)
                    + $"{count} of your party {(count == 1 ? "member is" : "members are")} a Square Enix NPC.\n\n"
                    + "They aren't listed individually because there is nothing to rate,\n"
                    + "report or remember about them.");
            }

            ImGui.SetCursorScreenPos(new Vector2(originX ?? cursor.X, cursor.Y + rowH + 1f));
        }

        private CharacterIdentity? ToIdentity(PartyMemberInfo member)
        {
            string world = Worlds?.GetWorldName(member.HomeWorldId) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(member.Name) || string.IsNullOrWhiteSpace(world))
                return null;
            return new CharacterIdentity(member.Name, world);
        }

        /// <summary>One party member. Shares the list-row primitive with Recent players, so the
        /// two lists match in height, padding and hover behaviour by construction.</summary>
        private void DrawPartyMemberRow(PartyMemberInfo member, bool allowKick, float width,
            float? originX, bool isSelf = false, uint dutyRowId = 0)
        {
            var identity = ToIdentity(member);
            bool canKick = allowKick && pfAutomation.IsPartyLeader();

            DrawHoverRow($"party{member.ContentId}{member.Name}",
                rightEdge => DrawPartyMemberBody(member, identity, canKick, rightEdge, isSelf,
                    dutyRowId),
                width: width, originX: originX,

                // Same menu as the recent players list. Null identity means the world hasn't
                // resolved yet, and a menu of links built from a blank world is worse than none.
                contextMenu: identity == null ? null : () => DrawPlayerMenuItems(identity));
        }

        private const float PartyKickWidth = 44f;
        private const float PartyReportWidth = 56f;

        private void DrawPartyMemberBody(PartyMemberInfo member, CharacterIdentity? identity,
            bool isLeader, float rightEdge, bool isSelf = false, uint dutyRowId = 0)
        {
            float iconSize = ImGui.GetTextLineHeight() + 4f;
            const float btnH = 22f;
            const float gap = 6f;

            // Laid out from the right edge inward, in screen space, so the last button always
            // lands the same distance from the border no matter how long a name is.
            //
            // Your own row reserves the same width as everyone else's even though it has no
            // Report or Kick to draw. Collapsing it instead pushed your rating and prog point
            // right, out of line with the column they belong to - one row disagreeing with the
            // rest is more distracting than an empty space at the end of it.
            float actions = PartyReportWidth + (isLeader ? PartyKickWidth + gap : 0f);
            float blockLeft = rightEdge - actions;
            float chipLeft = blockLeft - gap - RatingChipWidth;

            Vector2 start = ImGui.GetCursorScreenPos();

            DrawJobIconInline(member.JobId, iconSize, member.IsOffline);
            ImGui.SameLine(0, 7);

            string world = Worlds?.GetWorldName(member.HomeWorldId) ?? string.Empty;
            string shownName = DisplayName(member.Name);
            string label = string.IsNullOrEmpty(world) ? shownName : $"{shownName}  @{world}";
            if (isSelf)
                label += "  (you)";

            ImGui.AlignTextToFramePadding();
            string shown = Fit(label, chipLeft - (start.X + iconSize + 7f) - 8f);
            ImGui.TextColored(member.IsOffline ? TextMuted : TextPrimary, shown);
            if (shown != label && ImGui.IsItemHovered())
                PaddedTooltip(label);

            if (identity != null)
            {
                ImGui.SameLine();
                ImGui.SetCursorScreenPos(new Vector2(chipLeft, start.Y));
                DrawRatingChip(identity);

                DrawProgressBadge(identity, chipLeft, start.Y, dutyRowId);
            }

            // Nothing to report or kick on yourself. The space is still reserved above, so the
            // columns to the left stay in line with every other row.
            if (isSelf)
                return;

            ImGui.SameLine();
            ImGui.SetCursorScreenPos(new Vector2(blockLeft, start.Y));
            DrawReportButton(member);

            if (!isLeader)
                return;

            ImGui.SameLine(0, gap);
            // Lighter than the old near-black: it needs to read as destructive, not as a hole.
            ImGui.PushStyleColor(ImGuiCol.Button, ColorFromHex("#7d3a33"));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, AccentRed);
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, ColorFromHex("#c75446"));
            ImGui.PushStyleColor(ImGuiCol.Text, ColorFromHex("#ffe6e1"));
            ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 5.0f);
            if (ImGui.Button("Kick", new Vector2(PartyKickWidth, btnH)))
            {
                var target = member;
                // Kick takes the real name - it's how the game finds the member. Only the question
                // put to the player is abbreviated.
                AskConfirm("Kick player", $"Kick {DisplayName(target.Name)} from the party?",
                    "Yes, kick them",
                    () => Party?.Kick(target.Name, target.ContentId),
                    detail: "They won't be told it was you.");
            }
            ImGui.PopStyleVar();
            ImGui.PopStyleColor(4);
            if (ImGui.IsItemHovered())
                PaddedTooltip($"Remove {DisplayName(member.Name)} from the party.");
        }

        private void DrawReportButton(PartyMemberInfo member)
        {
            ImGui.PushStyleColor(ImGuiCol.Button, BgCardExpanded);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, BorderHover);
            ImGui.PushStyleColor(ImGuiCol.Text, TextSecondary);
            ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 5.0f);

            if (ImGui.Button("Report", new Vector2(PartyReportWidth, 22)))
            {
                var who = ToIdentity(member);
                if (who != null)
                    OpenReportDialog(who);
            }

            ImGui.PopStyleVar();
            ImGui.PopStyleColor(3);

            if (ImGui.IsItemHovered())
            {
                PaddedTooltip("Report to the plugin author, not Square Enix.");
            }
        }

    }
}
#endif
