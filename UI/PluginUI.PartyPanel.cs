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

                // Your own row, and the progress control, appear alongside a real party - or when
                // you are recruiting for one on your own.
                //
                // THE SOLO LISTING IS THE CASE THIS EXISTS FOR. Posting a party finder and waiting
                // is exactly when somebody wants to see their own prog point: the listing is up,
                // the fight is decided, and there is nobody else in the party yet to look at. It
                // used to draw nothing at all - no name, no progress, no way to fetch it - until
                // the first person joined, so the one moment the information was worth having was
                // the one moment it was missing.
                //
                // Still nothing when you are simply standing about in a party of one: a one-row
                // list of yourself, with no listing and no fight in question, is the noise this
                // guard was written to keep out.
                if (players > 0 || SoloRecruiting(players))
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
        /// The standalone party panel, shown when the recruitment card isn't carrying the list
        /// itself - which in practice means inside a duty, or in a party with no listing up.
        /// </summary>
        private void DrawPartyPanel(RecruitmentSnapshot snap)
        {
            if (!config.CommunityEnabled || !config.PartyRatingsEnabled)
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

            // NO LABEL INSIDE A DUTY. The panel used to head itself with the duty name - "In
            // Euphrosyne with" - directly underneath the column heading, which inside a duty
            // already reads "In Euphrosyne" (see LeftColumnTitle). Two lines, the same words, one
            // above the other. The column heading is the one that survives: it is there in every
            // state, and it is the line that names where you are.
            //
            // Outside a duty the two say different things - "Your recruitment" over "PARTY" - and
            // the label earns its place by marking where the member list starts.
            if (!inDuty)
            {
                ImGui.Indent(10);
                DrawSectionLabel("PARTY");
                ImGui.Unindent(10);
            }

            // THE PANEL OWNS THE ACTION ROW INSIDE A DUTY, so the member list must not draw its
            // own copy of Update progress underneath. Same mechanism the recruitment card uses when
            // it embeds this list - see cardOwnsProgressAction - and for the same reason: the
            // button belongs to exactly one row, and which row that is depends on who is drawing.
            bool ownRow = inDuty && !pfAutomation.IsInCombat();
            panelOwnsProgressAction = ownRow;

            try
            {
                // Kicking is impossible inside instanced content - the game refuses it - so the
                // button isn't offered there. The only exit available in a duty is your own.
                DrawPartyMembers(allowKick: !inDuty, width: ImGui.GetContentRegionAvail().X - 12f,
                    dutyName: snap.DutyName, dutyRowId: snap.DutyRowId);
            }
            finally
            {
                panelOwnsProgressAction = false;
            }

            if (ownRow)
                DrawDutyActionRow(snap);

            ImGui.Dummy(new Vector2(0, 4));
        }

        /// <summary>
        /// Whether the list should show you on your own: no party around you, but a listing up.
        ///
        /// Asked in both the measure pass and the draw pass, and they must agree - a count that
        /// says one row while the drawing says none leaves a gap in the card, and the other way
        /// round draws a row over whatever is beneath it.
        /// </summary>
        private bool SoloRecruiting(int otherPlayers)
        {
            if (otherPlayers > 0)
                return false;

            try { return pfAutomation.IsRecruiting(); }
            catch (Exception) { return false; }
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

            // Nothing to draw, and nothing to say about it. Checked here rather than on the raw
            // member list, so a listing posted by somebody standing alone still gets their own row
            // below - see SoloRecruiting.
            if (players.Count == 0 && supportNpcs == 0 && !SoloRecruiting(players.Count))
                return 0f;

            // You, first, because that is where you are in the game's own party list. Worth drawing
            // whenever there is a party to be part of, and when there is a listing up with nobody
            // in it yet - which is the case where your own prog point is the only thing there is
            // to show.
            PartyMemberInfo? self = null;
            if (players.Count > 0 || SoloRecruiting(players.Count))
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
                DrawPartyMemberRow(member, allowKick, width, originX, isSelf, dutyName, dutyRowId);
            }

            // Jobs observed above are written down at most every twenty seconds, not per frame.
            Players?.FlushIfDue();

            if (supportNpcs > 0)
                DrawDutySupportNote(supportNpcs, dutyName, width, originX);

            // Nothing to look up means no row at all, rather than a row saying so - an empty
            // statement still costs a row of the card's height, and a button that can only fail
            // is worse than no button.
            if (players.Count > 0 && ShowsProgressRow() && DutyHasProgress(dutyRowId)
                && !cardOwnsProgressAction && !panelOwnsProgressAction)
                DrawProgressRow(players, dutyName, width, originX);

            return ImGui.GetCursorScreenPos().Y - start;
        }

        /// <summary>Set while the party panel is drawing its own action row, so the member list
        /// leaves Update progress to it.</summary>
        private bool panelOwnsProgressAction;

        /// <summary>
        /// The duty's action row: update the party's progress, and leave.
        ///
        /// ONE ROW, EQUAL HALVES, ALIGNED WITH THE MEMBER BOXES ABOVE. They were two separate
        /// things on two separate lines - a 22px outlined chip pinned left, and a 150px filled
        /// button pinned right, a row apart - which is two different answers to "what can I do
        /// here" laid out as if they were unrelated. They are the same row.
        ///
        /// Update progress goes when there is no room for it rather than shrinking: three actions
        /// across a column this narrow is three things too small to read, and the per-player Fetch
        /// in each row already covers it.
        /// </summary>
        private void DrawDutyActionRow(RecruitmentSnapshot snap)
        {
            var players = PartyPlayers();
            bool hasProgress = HasProgressAction(snap.DutyRowId, players.Count);

            ImGui.Dummy(new Vector2(0, Space.Tight));

            // EXACTLY THE MEMBER ROWS' RECTANGLE, worked out the same way DrawHoverRow works it
            // out: the list is given (avail - 12) to draw in and then insets six pixels on BOTH
            // sides of that. The buttons took the same width but only the left inset, so they
            // finished twelve pixels past the right edge of every box above them - close enough to
            // look like a mistake rather than a margin, which is what it was.
            Vector2 cursor = ImGui.GetCursorScreenPos();
            const float inset = 6f;
            float rowW = MathF.Max(120f, ImGui.GetContentRegionAvail().X - 12f);
            float left = cursor.X + inset;
            float room = rowW - inset * 2f;
            float gap = Space.Tight;
            float w = hasProgress ? (room - gap) * 0.5f : room;

            if (hasProgress)
            {
                ImGui.SetCursorScreenPos(new Vector2(left, cursor.Y));
                DrawProgressAction(new Vector2(w, ButtonHeight), snap.DutyName, snap.DutyRowId, players);
            }

            var leavePos = new Vector2(hasProgress ? left + w + gap : left, cursor.Y);
            var leaveSize = new Vector2(hasProgress ? room - w - gap : room, ButtonHeight);
            ImGui.SetCursorScreenPos(leavePos);
            DrawLeaveDutyButton(leaveSize);

            ImGui.SetCursorScreenPos(new Vector2(cursor.X, cursor.Y + ButtonHeight));
            ImGui.Dummy(new Vector2(rowW, 0f));
        }

        /// <summary>
        /// The one action available from inside a duty.
        ///
        /// Hidden outright during combat rather than disabled. Mid-pull it is never the thing you
        /// meant to click, and a live "leave" button sitting under the cursor while you're fighting
        /// is a misclick waiting to happen.
        /// </summary>
        private void DrawLeaveDutyButton(Vector2 size)
        {
            bool can = pfAutomation.CanLeaveDuty();

            // Red: leaving a duty costs a penalty and can't be undone, which is the same weight as
            // every other destructive action here.
            ImGui.EndDisabled();

            if (DrawActionButton(FontAwesomeIcon.SignOutAlt, "Leave duty", "LeaveDuty", size,
                    ActionStyle.Danger, can))
            {
                AskConfirm("Leave duty", "Leave this duty?", "Yes, leave",
                    () => pfAutomation.LeaveDuty(),
                    detail: "You'll take the usual duty finder penalty.");
            }

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

            // NAMED ONCE AND USED FOR EVERY QUESTION BELOW. Progress is stored per fight as well
            // as per character, so asking about a character without saying which fight is not a
            // question the store can answer - and every "what do we know about this person" here
            // has to be about the fight this row is being drawn against.
            string duty = dutyName ?? string.Empty;

            var p = Ratings.ProgressFor(duty, who);

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
                        p.Percentile >= 0 ? ParseInk(p.Percentile) : TextPrimary,
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
            if (!Ratings.HasEncounterFor(duty) || Worlds?.GetFfLogsRegion(who.World) == null)
                return null;

            if (Ratings.PlayerProgressPending(duty, who) || (p?.Queued ?? false))
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
            if (Ratings.PlayerProgressFailed(duty, who))
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
                "noclear" => "Nothing logged for this fight yet.\n\n"
                    + "No kill and no pull on record, which on a new tier usually\n"
                    + "means exactly what it looks like - they are progging it.\n\n"
                    + "It is not the same as \"hasn't cleared\": plenty of people\n"
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
            // THREE ANSWERS, NOT TWO, and the middle one was wearing the first one's clothes.
            //
            //   noclear   the character was found and has nothing on THIS fight - no kill, no
            //             logged pull. That is a settled answer about somebody who is starting the
            //             tier, and it showed as "Fetch": a button implying nobody had looked,
            //             offered to somebody who had just looked and got a real reply. On a fresh
            //             tier that is most of the party.
            //   notfound  the character itself is not in the logs at all.
            //   neither   nobody has asked yet, which is the only one that should say "Fetch".
            bool freshProg = p?.Status is "noclear";
            bool notListed = p?.Status is "notfound" or "unknown";

            string label = freshProg ? "Fresh prog"
                : notListed ? "Not listed yet"
                : "Fetch";

            // Offered but refused while the server is inside its own cooldown for them. It would
            // take the press either way and match it against the stored row without queueing
            // anything, which from here is indistinguishable from a button that does nothing.
            TimeSpan wait = Ratings.PlayerRefreshWait(duty, who);
            if (wait > TimeSpan.Zero)
            {
                return new ProgressCell(label, TextMuted,
                    why + "\n\nAlready checked recently. The server re-reads one\n"
                        + $"character no more often than that, so this is worth\n"
                        + $"another go in {ShortWait(wait)}.",
                    isButton: true, disabled: true);
            }

            // Muted for anything that came back with a real answer, so it reads as a settled state
            // rather than the inviting call-to-action a never-tried row gets.
            return new ProgressCell(label, freshProg || notListed ? TextMuted : TextSecondary,
                why, isButton: true);
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
            ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, Radius.Chip);

            // THE LABEL IS DRAWN BY HAND, and this is the third attempt at centring it.
            //
            // ImGui's ButtonTextAlign centres the EM BOX, which runs from the font's ascent to its
            // descent - so a word with no descender, which "Fetch" and "Retry" both are, has its
            // visible ink sitting a couple of pixels below the middle of a 20px chip. Horizontally
            // it was already right; vertically it was two pixels low and looked it.
            //
            // Measured on the ink instead, the same way DrawGlyphCentred does it for the help
            // marker - and for the same reason, which is written out at length over there.
            const float chipH = 20f;
            Vector2 at = ImGui.GetCursorScreenPos();

            ImGui.BeginDisabled(cell.Disabled);
            bool pressed = ImGui.Button($"##prog{who.Key}", new Vector2(width, chipH));
            ImGui.EndDisabled();

            ImGui.PopStyleVar();
            ImGui.PopStyleColor(2);

            DrawTextCentredOnInk(cell.Text,
                new Vector2(at.X + width * 0.5f, at.Y + chipH * 0.5f),
                cell.Disabled ? cell.Colour with { W = cell.Colour.W * 0.5f } : cell.Colour);

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
        private bool ShowsProgressRow() => config.CommunityEnabled && Ratings != null;

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
            >= 100 => ColorFromHex("#6f5c33"),  // gold
            >= 99 => ColorFromHex("#52354b"),   // pink
            >= 95 => ColorFromHex("#6e4a30"),   // orange
            >= 75 => ColorFromHex("#412a6b"),   // purple
            >= 50 => ColorFromHex("#244085"),   // blue
            >= 25 => ColorFromHex("#2f5140"),   // green
            _ => ColorFromHex("#3a4250"),       // grey
        };

        /// <summary>
        /// The parse brackets, TONED DOWN TO SURFACES YOU CAN PUT WORDS ON.
        ///
        /// FFLogs' own hexes are signal colours - #1eff00 and #ff8000 - meant to be read as a few
        /// characters of text on a dark page. Behind a whole pill they are a wall of pure hue, and
        /// nothing legible goes on top: white disappears into the green, black into the blue.
        ///
        /// These are the same seven brackets at the tone Tomestone uses for the same job, three of
        /// them sampled straight off it - the plum, the indigo and the deep blue. Every one sits
        /// between 0.04 and 0.12 relative luminance, which is 6.5:1 against white at the worst of
        /// them and over 10:1 at most, so one light ink reads on all seven and the pills stop
        /// competing with the text they carry.
        ///
        /// The ORDER is untouched: grey, green, blue, purple, orange, pink, gold, at the same
        /// thresholds. Somebody who knows the brackets still reads the pill at a glance.
        /// </summary>

        /// <summary>
        /// The same seven brackets as INK rather than as a surface.
        ///
        /// One scale cannot do both jobs. A parse drawn as three characters on the plugin's black
        /// ground needs a light colour; a parse drawn as a whole pill needs a dark one, or nothing
        /// can be written on it. The fills above are the dark half - these are the light half, the
        /// same hue at the other end of the range, so a purple parse reads purple whichever form
        /// it takes.
        /// </summary>
        private static Vector4 ParseInk(double percentile) => percentile switch
        {
            >= 100 => ColorFromHex("#ccb673"),  // gold
            >= 99 => ColorFromHex("#c98fb4"),   // pink
            >= 95 => ColorFromHex("#d09a6e"),   // orange
            >= 75 => ColorFromHex("#9b86d9"),   // purple
            >= 50 => ColorFromHex("#6f9be0"),   // blue
            >= 25 => ColorFromHex("#6fae86"),   // green
            _ => ColorFromHex("#8d97a8"),       // grey
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
        /// A button rather than an automatic call on party change. This and the profile card's
        /// clears button are the only requests the plugin makes that end with someone else's
        /// character name at a third party, so both are things a person does deliberately - and it
        /// happens to be the cheap option for the API budget too.
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
        private TimeSpan PartyRefreshWait(string dutyName,
            List<(CharacterIdentity Who, string Region)> party)
        {
            if (Ratings == null || party.Count == 0 || string.IsNullOrWhiteSpace(dutyName))
                return TimeSpan.Zero;

            TimeSpan soonest = TimeSpan.MaxValue;
            foreach (var (who, _) in party)
            {
                TimeSpan wait = Ratings.PlayerRefreshWait(dutyName, who);

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

        /// <summary>
        /// Whether <see cref="DrawProgressAction"/> would draw anything, asked without drawing it.
        ///
        /// The card's action row divides its width between however many buttons it is about to
        /// have, which means it has to know the count before it places the first one - and this
        /// button is the one whose presence depends on the state of a lookup rather than on the
        /// state of the party.
        /// </summary>
        private bool HasProgressAction(uint dutyRowId, int playerCount)
            => Ratings != null && ShowsProgressRow() && DutyHasProgress(dutyRowId) && playerCount > 0;

        private bool DrawProgressAction(Vector2 size, string? dutyName, uint dutyRowId,
            List<PartyMemberInfo> players)
        {
            // Captured rather than re-read through the property. Moving the guard out to
            // HasProgressAction took the compiler's null-flow with it, and the twenty lines below
            // dereference this - a local the guard has proved is better than a null-forgiving `!`
            // on every one of them.
            var ratings = Ratings;
            if (ratings == null || !HasProgressAction(dutyRowId, players.Count))
                return false;

            string duty = dutyName ?? string.Empty;
            var party = PartyIdentities(players);
            ratings.EnsureProgressLoaded(duty, party);

            bool loaded = ratings.HasProgressFor(duty);

            int waiting = ratings.ProgressQueuedCountFor(duty, party);
            if (waiting > 0)
            {
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
            TimeSpan cooling = PartyRefreshWait(duty, party);
            bool ready = ratings.ProgressButtonReady && cooling <= TimeSpan.Zero;

            string label = ready
                ? "Update progress"
                : cooling > TimeSpan.Zero
                    ? $"Updated · {CompactWait(cooling)}"
                    : $"Updated · {(int)ratings.ProgressButtonWait.TotalSeconds}s";

            // The circular arrows are the same mark every application uses for "go and check
            // again", and the pair is laid out by the same helper Leave duty beside it uses.
            bool pressed = DrawActionButton(FontAwesomeIcon.SyncAlt, label, "FetchProgress", size,
                ActionStyle.Primary, ready);

            if (pressed && ready)
                ratings.RequestPartyProgress(duty, party);
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
            string? note = Ratings.ProgressNoteFor(duty);

            int queuedHere = Ratings.ProgressQueuedCountFor(duty, party);
            if (queuedHere > 0)
            {
                // Queued, not loading. The server fetches one character at a time for everybody
                // using the plugin, so a press joins a line rather than starting a lookup - and
                // the button has to say which of those two things happened.
                //
                // Counted for THIS party on THIS fight. The service-wide figure counts everybody
                // this session has queued anywhere, so fetching one person off a party finder
                // listing used to put the party's own button into "Queued · 3".
                DrawQueuedButton(queuedHere, maxWidth);
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
                TimeSpan cooling = PartyRefreshWait(duty, party);
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

            ImGui.SetCursorScreenPos(new Vector2(originX ?? cursor.X, cursor.Y + rowH + HoverRowGap));
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

            ImGui.SetCursorScreenPos(new Vector2(originX ?? cursor.X, cursor.Y + rowH + HoverRowGap));
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
            float? originX, bool isSelf = false, string? dutyName = null, uint dutyRowId = 0)
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
                        kick: isSelf || !canKick ? null : member),

                // A box each. The party list is the one place in the plugin where every row is a
                // person rather than a fact about one, and a column of names on a flat card read as
                // a paragraph - Raised sits one step above the card under it, so each of them is
                // an object you could point at.
                restColor: Raised);
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
            DrawRowKebab(rightEdge, btnH, "Report, kick");
        }
    }
}
#endif
