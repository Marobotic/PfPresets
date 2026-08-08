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
                    if (ShowsProgressRow() && DutyHasProgress(dutyRowId) && !cardOwnsProgressAction)
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
        ///
        /// "with" is dropped when there is nobody to be with - solo content, an unrestricted
        /// dungeon run alone - since the panel below it is then just the way out.
        /// </summary>
        private static string PartyPanelHeading(bool inDuty, string dutyName, bool hasCompany)
        {
            if (!inDuty)
                return "PARTY";

            bool named = !string.IsNullOrWhiteSpace(dutyName)
                && !dutyName.Equals("None", StringComparison.OrdinalIgnoreCase);

            if (!hasCompany)
                return named ? $"In {dutyName}" : "In duty";

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

            bool inDuty = pfAutomation.IsInDuty();
            int rows = PartyMemberCount();

            // No party is only a reason to draw nothing when there is no duty either. The status
            // card is suppressed inside duties by design - it has no listing to talk about - so a
            // solo run (a Trust dungeon, an unrestricted one, any solo instance) fell through both
            // and the window showed nothing at all about where the player was standing, and no way
            // out of it. In here the panel is worth drawing for the duty name and the exit alone.
            if (rows == 0 && !inDuty)
                return;

            ImGui.Dummy(new Vector2(0, 2));
            ImGui.Indent(10);
            // Fit against the width left after the indent, so a long duty name ellipsises instead
            // of being clipped by the border.
            DrawSectionLabel(Fit(PartyPanelHeading(inDuty, snap.DutyName, rows > 0),
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
                // The game numbers party slots from 1 in the order the party list shows them,
                // which is the order being iterated here - that number is the only handle the
                // blacklist command has on a person.
                DrawPartyMemberRow(member, allowKick, width, originX, isSelf, dutyName, dutyRowId,
                    partySlot: players.IndexOf(member) + 1);
            }

            // Jobs observed above are written down at most every twenty seconds, not per frame.
            Players?.FlushIfDue();

            if (supportNpcs > 0)
                DrawDutySupportNote(supportNpcs, dutyName, width, originX);

            // Nothing to look up means no row at all, rather than a row saying so - an empty
            // statement still costs a row of the card's height, and a button that can only fail
            // is worse than no button.
            if (players.Count > 0 && ShowsProgressRow() && DutyHasProgress(dutyRowId)
                && !cardOwnsProgressAction)
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

            const float w = CardActionWidth;
            const float h = ButtonHeight;

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
        /// What the progress column has to say about one character: a prog point, a word, or the
        /// button that would get one.
        /// </summary>
        private readonly struct ProgressCell
        {
            public ProgressCell(string text, Vector4 colour, string tip, bool isButton = false,
                bool disabled = false)
            {
                Text = text;
                Colour = colour;
                Tip = tip;
                IsButton = isButton;
                Disabled = disabled;
            }

            public string Text { get; }
            public Vector4 Colour { get; }
            public string Tip { get; }

            /// <summary>Pressable, and asks the server about this one character.</summary>
            public bool IsButton { get; }

            /// <summary>Drawn but refused. Keeps the row the same shape while the server is inside
            /// its own cooldown for this character - see PlayerRefreshWait.</summary>
            public bool Disabled { get; }
        }

        /// <summary>
        /// Works out the progress column for one character, without drawing it.
        ///
        /// Separated from the drawing because the row has to know how wide this is *before* it
        /// fits the name: the cell sits left of the rating chip, so a name measured against the
        /// chip alone simply runs underneath it.
        ///
        /// Only ever states what the data supports. A logged clear is a fact and is named as one;
        /// everything else is an absence, and an absence is shown as an absence rather than as an
        /// accusation - "no clear logged", never "hasn't cleared".
        ///
        /// <paramref name="dutyRowId"/> gates the whole column: outside a fight with progression
        /// (roulette, frontline, casual content) nothing is shown at all, so stale data from a
        /// previous session can never bleed through.
        /// </summary>
        private ProgressCell? ProgressCellFor(CharacterIdentity who, string? dutyName, uint dutyRowId)
        {
            if (Ratings == null || !ShowsProgressRow() || !DutyHasProgress(dutyRowId))
                return null;

            var p = Ratings.ProgressFor(who);

            switch (p?.Status)
            {
                case "cleared":
                    // Named rather than ticked, and tinted with their parse. A check mark says
                    // only "yes"; the colour is the thing raiders actually read, and it carries
                    // more than the word does.
                    //
                    // Plain white when no parse came back, and deliberately not grey. Grey is a
                    // parse bracket - it means "bottom quarter" to everyone who plays this game -
                    // so spending it on "we didn't find one" tells a lie about somebody rather
                    // than declining to comment. A colour is a claim; white is the absence of one.
                    return new ProgressCell("(Cleared)",
                        p.Percentile >= 0 ? ParseColor(p.Percentile) : TextPrimary,
                        "Has a logged clear of this duty."
                        + (p.Percentile >= 0
                            ? $"\n\nBest parse: {p.Percentile:0.#}% ({ParseName(p.Percentile)})"
                            : "\n\nNo parse found for this clear - it isn't a low one,\n"
                                + "it's an unknown one. Parses come from FFLogs and only\n"
                                + "exist for kills somebody uploaded them with."));

                case "progging":
                    return new ProgressCell(p.ProgLabel, AccentYellow,
                        "Furthest logged pull: " + p.ProgLabel
                        + "\n\nPercentage is the boss's remaining HP in that phase,\nso lower is further in."
                        + (p.LastSeenMs > 0 ? $"\nLast logged pull {AgoFromUnixMs(p.LastSeenMs)}." : string.Empty));

                case "hidden":
                    // A settled answer, and the player's own decision. Asking again returns the
                    // same refusal, so this is a word rather than a button.
                    //
                    // Rarer than it looks: someone who has only hidden their pull history still
                    // publishes their clears and current prog point on their profile, and that is
                    // read instead. This is the case where neither is public.
                    return new ProgressCell("Private", TextMuted,
                        "This character keeps their logs private.\n\n"
                        + "A privacy setting on their profile rather than a fault,\n"
                        + "and only they can change it. Re-checking won't get a\n"
                        + "different answer.");
            }

            // Nobody has a number for this person. Whether that is worth a button depends on
            // there being a fight to ask about and a region to ask in.
            string duty = dutyName ?? string.Empty;
            if (!Ratings.HasEncounterFor(duty) || Worlds?.GetFfLogsRegion(who.World) == null)
                return null;

            if (Ratings.PlayerProgressPending(who) || (p?.Queued ?? false))
            {
                // The length of the line, when the server has told us. A bare "Queued" gives no
                // way to tell a five-second wait from a two-minute one, which is the whole of
                // what makes it feel broken.
                int ahead = Ratings.ProgressQueueSize;
                string how = ahead > 1
                    ? $"\n\n{ahead} characters are on the queue right now, so this is\n"
                        + $"roughly {ShortWait(Ratings.ProgressQueueEta)} away."
                    : string.Empty;

                return new ProgressCell("Queued", TextMuted,
                    "On the server's lookup queue.\n\n"
                    + "One character is fetched at a time for everyone using the\n"
                    + "plugin, so this is a place in a line rather than a lookup."
                    + how);
            }

            // Tried and failed, rather than never tried. Same press underneath - the belt will
            // take them again - but it must not be dressed up as an untouched row.
            if (Ratings.PlayerProgressFailed(who))
            {
                return new ProgressCell("Retry", AccentYellow,
                    "The server tried this character and got nothing back.\n\n"
                    + "It gives a name several attempts over several minutes before\n"
                    + "abandoning it, so this usually means the provider is down or\n"
                    + "doesn't know the character rather than a slow moment.\n\n"
                    + "Pressing puts them back on the queue.",
                    isButton: true);
            }

            // A button in place of the number they don't have. The blank space it replaces was
            // the same shape whether nobody had ever looked them up or the lookup came back
            // empty, and those want different things done about them.
            string why = p?.Status switch
            {
                "noclear" => "No clear logged for this fight.\n\n"
                    + "That is not the same as \"hasn't cleared\" - plenty of people\n"
                    + "clear without ever uploading a log. Check again if they've\n"
                    + "logged something since.",
                "notfound" or "unknown" => "No logs found for this character.\n\n"
                    + "Check again - a name that came back empty once often has\n"
                    + "something by the time it matters.",
                _ => "Nobody has looked this player up for this fight yet.\n\n"
                    + "Asks about this one character rather than the whole party,\n"
                    + "so it doesn't spend everyone else's turn in the queue.",
            };

            // A name the server looked up and had no record for reads as a state, not an untouched
            // row. "Fetch" -> queued -> back to "Fetch" looked like the press did nothing; "Not
            // listed yet" says the lookup happened and the character simply isn't in the data. The
            // "yet" keeps it honestly re-checkable - a name that came back empty once often has
            // something later - so it stays a button. Provider-agnostic on purpose: whether
            // Tomestone or FFLogs came up empty is not the user's problem (see RatingService).
            bool notListed = p?.Status is "notfound" or "unknown";
            string label = notListed ? "Not listed yet" : "Fetch";

            // Offered but refused while the server is inside its own cooldown for them. It would
            // take the press either way and match it against the stored row without queueing
            // anything, which from here is indistinguishable from a button that does nothing.
            TimeSpan wait = Ratings.PlayerRefreshWait(who);
            if (wait > TimeSpan.Zero)
            {
                return new ProgressCell(label, TextMuted,
                    why + "\n\nAlready checked recently. The server re-reads one\n"
                        + $"character no more often than that, so this is worth\n"
                        + $"another go in {ShortWait(wait)}.",
                    isButton: true, disabled: true);
            }

            // Muted for a name that came back empty, so it reads as a settled state rather than the
            // inviting call-to-action a never-tried row gets.
            return new ProgressCell(label, notListed ? TextMuted : TextSecondary, why, isButton: true);
        }

        /// <summary>"4 minutes", "40 seconds" - a wait as a person would say it.</summary>
        private static string ShortWait(TimeSpan wait)
        {
            if (wait.TotalSeconds < 90)
                return $"{Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds))} seconds";

            int mins = (int)Math.Ceiling(wait.TotalMinutes);
            return mins == 1 ? "a minute" : $"{mins} minutes";
        }

        /// <summary>Room the cell needs, measured the same way it will be drawn.</summary>
        private static float ProgressCellWidth(ProgressCell cell)
            => cell.IsButton
                ? Math.Max(46f, ImGui.CalcTextSize(cell.Text).X + 16f)
                : ImGui.CalcTextSize(cell.Text).X;

        /// <summary>Draws the cell at a left edge the row has already worked out.</summary>
        private void DrawProgressCell(ProgressCell cell, CharacterIdentity who, string? dutyName,
            float left, float rowY, float width)
        {
            ImGui.SameLine();

            if (!cell.IsButton)
            {
                ImGui.SetCursorScreenPos(new Vector2(left, rowY));
                ImGui.AlignTextToFramePadding();
                ImGui.TextColored(cell.Colour, cell.Text);

                if (ImGui.IsItemHovered())
                    PaddedTooltip(cell.Tip);
                return;
            }

            // Nudged down to sit on the same centre line as the text branch: the button is
            // shorter than a frame, and at the row's top edge it rode visibly high of the name
            // beside it.
            ImGui.SetCursorScreenPos(new Vector2(left, rowY + 2f));

            ImGui.PushStyleColor(ImGuiCol.Button, BgCardExpanded);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, BorderHover);
            ImGui.PushStyleColor(ImGuiCol.Text, cell.Colour);
            ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 0f);

            ImGui.BeginDisabled(cell.Disabled);
            bool pressed = ImGui.Button($"{cell.Text}##prog{who.Key}", new Vector2(width, 20f));
            ImGui.EndDisabled();

            ImGui.PopStyleVar();
            ImGui.PopStyleColor(3);

            if (pressed && !cell.Disabled)
                RequestOneProgress(who, dutyName);
            else if (ImGui.IsItemHovered())
                PaddedTooltip(cell.Tip);
        }

        /// <summary>Puts one character on the server's queue for the fight in front of us.</summary>
        private void RequestOneProgress(CharacterIdentity who, string? dutyName)
        {
            string duty = dutyName ?? string.Empty;
            string? region = Worlds?.GetFfLogsRegion(who.World);

            if (Ratings == null || region == null || string.IsNullOrWhiteSpace(duty))
                return;

            Ratings.RequestPlayerProgress(duty, who, region);
        }

        /// <summary>
        /// Whether "Update progress for X" belongs in this player's menu.
        ///
        /// The menu is shared with Recent players and the search results, where there is no fight
        /// in front of us at all - so every condition the party row checks has to be checked again
        /// here rather than assumed.
        /// </summary>
        private bool CanFetchProgressFor(CharacterIdentity who, string? dutyName, uint dutyRowId)
            => Ratings != null
               && ShowsProgressRow()
               && DutyHasProgress(dutyRowId)
               && !string.IsNullOrWhiteSpace(dutyName)
               && Worlds?.GetFfLogsRegion(who.World) != null;

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
        /// <summary>
        /// Whether the recruitment card is drawing the progress action this frame.
        ///
        /// Set by the card before it measures itself, so the party list both counts and draws the
        /// same number of rows - the count feeds the card's height, and the two disagreeing is how
        /// a row ends up outside the clip rect.
        /// </summary>
        private bool cardOwnsProgressAction;

        /// <summary>The party's real players, NPCs excluded. The card needs the same list the
        /// member rows are built from before it can ask about their progress.</summary>
        private List<PartyMemberInfo> PartyPlayers()
        {
            try
            {
                var members = pfAutomation.GetOtherPartyMemberDetails();
                var players = new List<PartyMemberInfo>(members.Count + 1);
                foreach (var m in members)
                    if (!m.IsSupportNpc)
                        players.Add(m);

                var self = pfAutomation.GetLocalPartyMember();
                if (self is { } me && players.Count > 0)
                    players.Add(me);

                return players;
            }
            catch (Exception)
            {
                return new List<PartyMemberInfo>();
            }
        }

        /// <summary>
        /// The Update progress button on its own, at a size the caller chooses.
        ///
        /// Returns false when there is nothing to draw, so the caller can lay out whatever else it
        /// has without leaving a hole. The queued and cooling-down states are the same button in
        /// the same place - swapping it for text made the row change height under the reader.
        /// </summary>
        /// <summary>
        /// The shortest wait before anyone in this party could actually be re-read, or zero when at
        /// least one of them is fetchable right now.
        ///
        /// The server keeps a per-character cooldown so a name is not read from the provider more
        /// often than it can meaningfully change - see PlayerRefreshWait. A press while every last
        /// member is inside that window is accepted by the route, matched against the stored rows,
        /// and queues nothing at all, which from this side is indistinguishable from a button that
        /// does nothing. The per-row Fetch buttons already know this about their own character;
        /// this is the same fact for the party, so the party's button can say so too.
        ///
        /// Anybody with no stored row at all reports zero, so a party containing one never-fetched
        /// character still offers the press - there is real work to do for them.
        /// </summary>
        private TimeSpan PartyRefreshWait(List<(CharacterIdentity Who, string Region)> party)
        {
            if (Ratings == null || party.Count == 0)
                return TimeSpan.Zero;

            TimeSpan soonest = TimeSpan.MaxValue;
            foreach (var (who, _) in party)
            {
                TimeSpan wait = Ratings.PlayerRefreshWait(who);

                // Somebody is fetchable now: the press has work to do, so there is nothing to wait
                // for and no reason to look at the rest.
                if (wait <= TimeSpan.Zero)
                    return TimeSpan.Zero;

                if (wait < soonest)
                    soonest = wait;
            }

            return soonest == TimeSpan.MaxValue ? TimeSpan.Zero : soonest;
        }

        /// <summary>A wait at button width: "12m", "45s". <see cref="ShortWait"/> spells it out for
        /// prose, which is too wide here - the button is sized to its label, so a longer string
        /// moves the control rather than just reading differently.</summary>
        private static string CompactWait(TimeSpan wait)
            => wait.TotalSeconds >= 90
                ? $"{Math.Max(1, (int)Math.Ceiling(wait.TotalMinutes))}m"
                : $"{Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds))}s";

        /// <summary>The tooltip for a party whose every member was checked too recently to be worth
        /// asking about again.</summary>
        private static string AllOnCooldownTip(TimeSpan wait)
            => "Everyone here was checked recently.\n\n"
                + "The server re-reads one character no more often than that, so\n"
                + "pressing now would fetch nothing for anybody. This becomes\n"
                + $"worth another go in {ShortWait(wait)}, as each of them falls\n"
                + "out of that window.";

        private bool DrawProgressAction(Vector2 size, string? dutyName, uint dutyRowId,
            List<PartyMemberInfo> players)
        {
            if (Ratings == null || !ShowsProgressRow() || !DutyHasProgress(dutyRowId)
                || players.Count == 0)
                return false;

            string duty = dutyName ?? string.Empty;
            var party = PartyIdentities(players);
            Ratings.EnsureProgressLoaded(duty, party);

            bool loaded = Ratings.HasProgressFor(duty);

            if (Ratings.ProgressQueued)
            {
                int waiting = Ratings.ProgressQueuedCount;
                string queued = waiting > 1 ? $"Queued · {waiting}" : "Queued";

                ImGui.BeginDisabled(true);
                DrawPrimaryButton($"{queued}##FetchProgress", size);
                ImGui.EndDisabled();

                if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                    PaddedTooltip(
                        "Waiting on the server's lookup queue.\n\n"
                        + "One character is fetched at a time for everyone using the plugin,\n"
                        + "so the answer arrives shortly rather than instantly.");
                return true;
            }

            // Two different reasons the press can be refused, and they want different words: the
            // short throttle on the button itself, and the server's much longer per-character
            // window with every member already inside it.
            TimeSpan cooling = PartyRefreshWait(party);
            bool ready = Ratings.ProgressButtonReady && cooling <= TimeSpan.Zero;

            string label = ready
                ? "Update progress##FetchProgress"
                : cooling > TimeSpan.Zero
                    ? $"Updated · {CompactWait(cooling)}##FetchProgress"
                    : $"Updated · {(int)Ratings.ProgressButtonWait.TotalSeconds}s##FetchProgress";

            ImGui.BeginDisabled(!ready);
            bool pressed = DrawPrimaryButton(label, size);
            ImGui.EndDisabled();

            if (pressed && ready)
                Ratings.RequestPartyProgress(duty, party);
            else if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            {
                PaddedTooltip(cooling > TimeSpan.Zero
                    ? AllOnCooldownTip(cooling)
                    : (loaded ? "Re-checks this party against FFLogs.\n\n" : "Asks FFLogs how far this party has got.\n\n")
                        + "Sends their names and worlds to FFLogs - the one time this\n"
                        + "plugin sends anything to anyone but your own server. Cached\n"
                        + "results are shared, so this updates them for everyone.");
            }

            return true;
        }

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

                // One label at both widths. The long form only ever said "player" twice - the row
                // it sits under is the party, so there was nothing else the progress could be for.
                const string label = "Update progress";
                float needed = ImGui.CalcTextSize(label).X + 24f;
                float buttonW = Math.Max(60f, Math.Min(needed, room));

                // Same two refusals as the card's copy of this button - the short throttle, and the
                // server's per-character window with everybody already inside it.
                TimeSpan cooling = PartyRefreshWait(party);
                bool ready = Ratings.ProgressButtonReady && cooling <= TimeSpan.Zero;

                ImGui.BeginDisabled(!ready);
                // Accent, not grey: this is the party card's own action, and the outlined secondary
                // style put it at the same weight as the things around it that only navigate.
                bool pressed = DrawAccentOutlineButton(
                    ready ? $"{label}##FetchProgress"
                          : cooling > TimeSpan.Zero
                              ? $"Updated · {CompactWait(cooling)}##FetchProgress"
                              : $"Updated · {(int)Ratings.ProgressButtonWait.TotalSeconds}s##FetchProgress",
                    new Vector2(buttonW, 22));
                ImGui.EndDisabled();

                if (pressed && ready)
                    Ratings.RequestPartyProgress(duty, party);
                else if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                {
                    PaddedTooltip(cooling > TimeSpan.Zero
                        ? AllOnCooldownTip(cooling)
                        : (loaded ? "Re-checks this party against FFLogs.\n\n" : "Asks FFLogs how far this party has got.\n\n")
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
            DrawSecondaryButton($"{label}##FetchProgress", new Vector2(width, ButtonHeight));
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

        /// <summary>
        /// A party row's name as it fits: full, then without the world, then ellipsised.
        ///
        /// Dropping "@World" before cutting into the name is the whole point. Between
        /// "Lillianne Nel…" and "Lillianne Nelligan", the second identifies the person and the
        /// first identifies nobody - and the world is a tiebreak that hardly ever has to be
        /// broken, still one hover away on the rare row where it does.
        /// </summary>
        private static string FitPlayerLabel(string name, string world, string suffix, float maxWidth)
        {
            string full = (string.IsNullOrEmpty(world) ? name : $"{name}  @{world}") + suffix;
            if (maxWidth <= 0f || ImGui.CalcTextSize(full).X <= maxWidth)
                return full;

            string bare = name + suffix;
            return ImGui.CalcTextSize(bare).X <= maxWidth ? bare : Fit(bare, maxWidth);
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
            float? originX, bool isSelf = false, string? dutyName = null, uint dutyRowId = 0,
            int partySlot = 0)
        {
            var identity = ToIdentity(member);
            bool canKick = allowKick && pfAutomation.IsPartyLeader();

            // The game is telling us what they are on, right now. That beats anything Tomestone can
            // say, so it is written down here and the network lookup stops happening for a week -
            // which is most of the point of caching jobs at all: the people you actually play with
            // never need fetching.
            if (identity != null && member.JobId != 0)
                Ratings?.ObserveJob(identity, member.JobId);

            DrawHoverRow($"party{member.ContentId}{member.Name}",
                rightEdge => DrawPartyMemberBody(member, identity, canKick, rightEdge, isSelf,
                    dutyName, dutyRowId),
                width: width, originX: originX,

                // Same menu as the recent players list, plus the two items that only make sense
                // here: the fight in front of us, and kicking. Null identity means the world hasn't
                // resolved yet, and a menu of links built from a blank world is worse than none.
                //
                // Kick is passed only when it can actually happen, so the item is absent rather
                // than present-and-doing-nothing on a row you don't lead or inside a duty.
                contextMenu: identity == null
                    ? null
                    : () => DrawPlayerMenuItems(identity, dutyName, dutyRowId,
                        kick: isSelf || !canKick ? null : member,
                        member: isSelf ? null : member,
                        partySlot: isSelf ? 0 : partySlot));
        }

        /// <summary>Room reserved at the end of every row for the menu button. Constant whether or
        /// not the row actually draws one, so the columns to its left stay in line.</summary>
        private const float PartyMenuWidth = 22f;

        private void DrawPartyMemberBody(PartyMemberInfo member, CharacterIdentity? identity,
            bool isLeader, float rightEdge, bool isSelf = false, string? dutyName = null,
            uint dutyRowId = 0)
        {
            float iconSize = ImGui.GetTextLineHeight() + 4f;
            const float btnH = 22f;
            const float gap = 6f;

            // Laid out from the right edge inward, in screen space, so the menu button always lands
            // the same distance from the border no matter how long a name is.
            //
            // Your own row reserves the same width as everyone else's even though it has no menu to
            // draw. Collapsing it instead pushed your rating and prog point right, out of line with
            // the column they belong to - one row disagreeing with the rest is more distracting
            // than an empty space at the end of it.
            float blockLeft = rightEdge - PartyMenuWidth;
            float chipLeft = blockLeft - gap - RatingChipWidth;

            Vector2 start = ImGui.GetCursorScreenPos();

            // Someone who logged out, teleported away or otherwise stopped being visible to the
            // client reports no job at all, and the "?" that put in the icon's place said "we never
            // knew what they play" about a person we had just finished a duty with. The last job we
            // saw on them stands in until the game hands us a live one again - a job from ten
            // minutes ago is a far better answer than no answer, and the hover says which it is.
            uint jobId = member.JobId;
            bool rememberedJob = false;
            if (jobId == 0 && identity != null)
            {
                jobId = Players?.JobFor(identity) ?? 0;
                rememberedJob = jobId != 0;
            }

            DrawJobIconInline(jobId, iconSize, member.IsOffline, rememberedJob);
            ImGui.SameLine(0, 7);

            // The progress column is measured before the name is fitted rather than drawn after
            // it. It lands left of the rating chip either way, so a name fitted against the chip
            // alone ran straight underneath it - which is what put "@Gilgamesh" through the
            // middle of "P3 29.8%".
            ProgressCell? cell = identity == null
                ? null
                : ProgressCellFor(identity, dutyName, dutyRowId);

            float cellW = cell == null ? 0f : ProgressCellWidth(cell.Value);
            float cellLeft = chipLeft - (cellW > 0f ? cellW + 8f : 0f);

            string world = Worlds?.GetWorldName(member.HomeWorldId) ?? string.Empty;
            string shownName = DisplayName(member.Name);
            string suffix = isSelf ? "  (you)" : string.Empty;
            string label = (string.IsNullOrEmpty(world) ? shownName : $"{shownName}  @{world}") + suffix;

            ImGui.AlignTextToFramePadding();
            string shown = FitPlayerLabel(shownName, world, suffix,
                cellLeft - (start.X + iconSize + 7f) - 8f);
            ImGui.TextColored(member.IsOffline ? TextMuted : TextPrimary, shown);
            if (shown != label && ImGui.IsItemHovered())
                PaddedTooltip(label);

            if (identity != null)
            {
                ImGui.SameLine();
                ImGui.SetCursorScreenPos(new Vector2(chipLeft, start.Y));
                DrawRatingChip(identity);

                if (cell != null)
                    DrawProgressCell(cell.Value, identity, dutyName, cellLeft, start.Y, cellW);
            }

            // Nothing to do to yourself. The space is still reserved above, so the columns to the
            // left stay in line with every other row.
            if (isSelf || identity == null)
                return;

            ImGui.SameLine();
            ImGui.SetCursorScreenPos(new Vector2(blockLeft, start.Y));
            DrawRowKebab(rightEdge, btnH, "Report, kick, blacklist");
        }
    }
}
#endif
