#if PFP_RATINGS
using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.GameFonts;
using Dalamud.Interface.ManagedFontAtlas;

namespace PfPresets
{
    /// <summary>
    /// The profile card in the Ratings tab: a score banner, the numbers behind it, and where you
    /// have run into them.
    ///
    /// Only the rating gets size and colour. Everything else - the split, the voter count, the
    /// unweighted tally - sits muted on one line, because only one of those figures is the score
    /// and sizing them alike would suggest several competing answers to one question.
    ///
    /// The rating is a net tally, never a percentage. An upvote is +1, a downvote -1, each scaled
    /// by how much that voter counts. Three unanimous friends and thirty unanimous strangers both
    /// read 100%, and only one of them means anything - which is exactly what a share cannot say.
    /// </summary>
    public partial class PluginUI
    {
        /// <summary>
        /// The banner's two faces, at the sizes it needs them.
        ///
        /// Not SetWindowFontScale, which stretches the baked 12px bitmap glyphs and made the
        /// number visibly pixelated. Not the game's Jupiter face either - that is FFXIV's own HUD
        /// numeral face and reads as borrowed chrome here.
        ///
        /// Both semibold: this is the largest figure in the plugin and the caption that names it,
        /// and a 54px number set in the body weight looks like it was left at a default.
        /// </summary>
        private IFontHandle? scoreFont;
        private IFontHandle? labelFont;

        /// <summary>The figure's size, named so the card's measured height and the face it is
        /// drawn in cannot drift apart.</summary>
        // Fifty-four. The digit reads as 34px of ink because this face sets its figures a little
        // under cap height - which is exactly the 34px the reference shows.
        private const float ScorePx = 54f;

        /// <summary>The job mark beside a character's name on their card.</summary>
        private const float ProfileJobMarkSize = 26f;

        private IFontHandle ScoreFont =>
            Font(ref scoreFont, ScorePx, FontWeight.SemiBold, userText: false, gameGlyphs: false);
        private IFontHandle LabelFont => Font(ref labelFont, 11f, FontWeight.SemiBold, userText: false);

        /// <summary>
        /// An opted-out player's card: who they are, and the one sentence.
        ///
        /// Drawn as a real card - the same panel, the same identity block - rather than as a bare
        /// line, because there IS somebody here and the plugin knows who. What is missing is
        /// everything the community half would have said about them, and that absence is the point.
        /// </summary>
        private void DrawProfileOptedOut(CharacterIdentity who, bool showBack)
        {
            ImGui.Dummy(new Vector2(0, 8));

            if (showBack)
            {
                // The iOS back button: a glass pill, a chevron and the word.
                if (DrawIosButton("Back", "##ClearSearchOptedOut", FontAwesomeIcon.ChevronLeft, new Vector2(92, 32), primary: false))
                    CloseProfile();
                ImGui.Dummy(new Vector2(0, 8));
            }

            var dl = ImGui.GetWindowDrawList();
            Vector2 cardMin = ImGui.GetCursorScreenPos();
            float cardWidth = ImGui.GetContentRegionAvail().X;

            dl.ChannelsSplit(2);
            dl.ChannelsSetCurrent(1);

            ImGui.BeginGroup();
            try
            {
                ImGui.Dummy(new Vector2(cardWidth, CardPad));
                ImGui.Indent(CardPad);

                DrawProfileIdentity(who);

                ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X - CardPad);
                ImGui.TextColored(Dim, OptedOutNotice);
                ImGui.PopTextWrapPos();

                ImGui.Unindent(CardPad);
                ImGui.Dummy(new Vector2(cardWidth, CardPad));
            }
            finally
            {
                ImGui.EndGroup();
            }

            float cardHeight = ImGui.GetItemRectSize().Y;

            dl.ChannelsSetCurrent(0);
            var cardMax = new Vector2(cardMin.X + cardWidth, cardMin.Y + cardHeight);
            dl.AddRectFilled(cardMin, cardMax, ImGui.ColorConvertFloat4ToU32(Field), Radius.Card);
            dl.AddRect(cardMin, cardMax, ImGui.ColorConvertFloat4ToU32(CardBorder),
                Radius.Card, ImDrawFlags.None, 1f);
            dl.ChannelsMerge();

            ImGui.Dummy(new Vector2(0, 8));
        }

        /// <summary>
        /// What stands in for a hidden player's card: a line saying there is nothing, and Back.
        ///
        /// WORDED FOR A NAME THAT ISN'T THERE, not for a person who is. "No record for X" is what
        /// somebody sees after a typo, and that is deliberate - it has to be the same sentence, or
        /// the difference between the two becomes the test that tells you somebody is banned.
        /// </summary>
        private void DrawProfileUnavailable(CharacterIdentity who, bool showBack)
        {
            ImGui.Dummy(new Vector2(0, 8));

            if (showBack)
            {
                // The iOS back button: a glass pill, a chevron and the word.
                if (DrawIosButton("Back", "##ClearSearchHidden", FontAwesomeIcon.ChevronLeft, new Vector2(92, 32), primary: false))
                    CloseProfile();
                ImGui.Dummy(new Vector2(0, 12));
            }

            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X - CardPad);
            ImGui.TextColored(Dim, $"No community record for {who.Name}.");
            ImGui.PopTextWrapPos();

            ImGui.Dummy(new Vector2(0, 8));
        }

        /// <summary>What an opted-out player reads as, in the one place the sentence is written.</summary>
        internal const string OptedOutNotice = "This player has opted out.";

        /// <summary>The same thing in a score column, where there is room for two words.</summary>
        internal const string OptedOutShort = "Opted out";

        /// <summary>
        /// Whether this player takes part at all: false for anyone opted out, and for anyone banned.
        ///
        /// EVERYTHING THAT OFFERS SOMETHING READS THIS. Both states refuse a vote, a profile and a
        /// score identically, so the code that decides whether to draw a control asks one question.
        ///
        /// Null reads as visible: a rating that has not arrived yet is not evidence of anything, and
        /// blanking a row while its lookup is in flight would make every stranger flicker.
        /// </summary>
        private static bool IsHidden(PlayerRating? rating) => rating?.Hidden == true;

        /// <summary>
        /// Whether this player is hidden BY THEIR OWN CHOICE, which is the half that gets said.
        ///
        /// EVERYTHING THAT SAYS SOMETHING READS THIS instead. An opt-out is a decision, is reported
        /// as one, and is reversible - so "this player has opted out" is both true and useful.
        ///
        /// A ban is hidden without being opted out, and reads as a name nobody has rated: no card,
        /// no notice, nothing. Saying "banned" would be a punishment nobody decided on, and saying
        /// "opted out" would be a favour they have not earned.
        /// </summary>
        private static bool IsOptedOut(PlayerRating? rating) => rating?.OptedOut == true;

        /// <summary>
        /// "Opted out", right-aligned into a score column, in the muted grey the plugin uses for
        /// facts about the absence of data rather than about a person.
        /// </summary>
        /// <param name="column">The fixed width the score column occupies, so the label ends where
        /// the numbers on the rows above and below it end.</param>
        private void DrawOptedOutColumn(CharacterIdentity who, float column)
        {
            using (UiHelpFont.Push())
            {
                float used = ImGui.CalcTextSize(OptedOutShort).X;
                if (used < column)
                {
                    ImGui.Dummy(new Vector2(column - used, 0));
                    ImGui.SameLine(0, 0);
                }

                ImGui.TextColored(Faint, OptedOutShort);
            }

            if (ImGui.IsItemHovered())
                PaddedTooltip($"{who}\n\n{OptedOutNotice}");
        }

        private void DisposeProfileFonts()
        {
            scoreFont?.Dispose();
            labelFont?.Dispose();
            scoreFont = null;
            labelFont = null;
        }

        // Card geometry, in one place so the band, the columns and the content can't disagree.
        private const float CardPad = CardPadding;

        /// <summary>Who the card was drawn for last, and on which frame - the two facts needed to
        /// tell "still open" from "opened again".</summary>
        private string profileCardKey = string.Empty;
        private int profileCardFrame = -1;

        /// <summary>
        /// Whether this draw is the card being opened rather than staying open.
        ///
        /// A gap in the frame numbers means the card wasn't on screen last frame - the tab was
        /// switched, the window was closed, Back was pressed - so coming back to it is a fresh
        /// look and worth re-reading the score for. Held open, it isn't: nothing about the card
        /// changes between two consecutive frames.
        /// </summary>
        private bool ProfileCardOpened(CharacterIdentity who)
        {
            int frame = ImGui.GetFrameCount();
            bool opened = profileCardKey != who.Key || frame - profileCardFrame > 1;

            profileCardKey = who.Key;
            profileCardFrame = frame;
            return opened;
        }

        private void DrawProfileCard(CharacterIdentity who, bool showBack = true)
        {
            // Opening a profile re-reads the score. Votes cast by other people in your own party
            // were otherwise invisible for the ten minutes the cache holds an entry - you saw your
            // own vote land and then nothing, however many other people voted after you. The
            // service caps this at one refetch per player per five seconds.
            if (ProfileCardOpened(who))
                Ratings?.Refresh(who);

            var rating = Ratings?.Get(who);

            // Neither of these gets the ordinary card, and they do not get the same substitute.
            //
            // Opted out is a decision, so it is reported as one: their name and the sentence, and
            // nothing else - no score, no counts, no clears, no links. It is not deletion and does
            // not read as any; opting back in restores every one of those.
            //
            // Banned is not reported at all. It falls through to the empty state a name nobody has
            // heard of gets, because a distinct message would turn this window into a way of
            // testing who is banned.
            if (IsOptedOut(rating))
            {
                DrawProfileOptedOut(who, showBack);
                return;
            }

            if (IsHidden(rating))
            {
                DrawProfileUnavailable(who, showBack);
                return;
            }

            // What the server already knows about their clears, and nothing more. This is a read of
            // our own table - it costs a query on our box and never reaches Tomestone or FFLogs -
            // so a card always opens showing whatever anybody has already fetched. Only the footer
            // button can cause a provider to be asked.
            Ratings?.EnsureClearsLoaded(who, Worlds?.GetFfLogsRegion(who.World));
            var clears = Ratings?.ClearsFor(who);

            // NO LEADING SPACER. There was an 8px Dummy here, which with ImGui's own item spacing
            // put the card twelve pixels below the one in the column beside it - two cards under
            // two headings at the same height, starting at different heights. The heading above
            // already ends with its own gap; a second one belongs to nobody.

            // No Back button on your own card: it isn't somewhere you navigated to, it is what
            // the tab shows when there is nothing else to do.
            if (showBack)
            {
                // No indent: the button sat eight pixels inside the card it belongs to, which is
                // the kind of near-alignment that looks like a mistake rather than a margin.
                // The iOS back button: a glass pill, a chevron and the word.
                if (DrawIosButton("Back", "##ClearSearch", FontAwesomeIcon.ChevronLeft, new Vector2(92, 32), primary: false))
                    CloseProfile();
                ImGui.Dummy(new Vector2(0, 8));
            }

            // No child window, and so no height to get wrong.
            //
            // A child fills whatever it is given unless it is told a size, and every size this card
            // was told came from arithmetic that went stale the moment a font changed - too short
            // and it scrolled, too tall and it left the column half empty. The content is drawn as
            // an ordinary group instead, measured after the fact, and the background is painted
            // behind it on a separate draw channel. The card is then exactly its contents, always,
            // with nothing to maintain.
            var dl = ImGui.GetWindowDrawList();
            Vector2 cardMin = ImGui.GetCursorScreenPos();
            float cardWidth = ImGui.GetContentRegionAvail().X;

            dl.ChannelsSplit(2);
            dl.ChannelsSetCurrent(1);

            ImGui.BeginGroup();
            try
            {
                ImGui.Dummy(new Vector2(cardWidth, CardPad));
                ImGui.Indent(CardPad);

                DrawProfileIdentity(who);
                ImGui.Dummy(new Vector2(0, 6));

                // Who they are, then what they have cleared, then where you have run into them.
                //
                // The order is the order the questions get asked. "Seen recently in" reads last
                // because it is the only part of the card that is about you rather than them.
                DrawClearsSections(clears);
                DrawClearsFooter(who, clears);

                // Not on your own card. You know where you've been, and the list exists to tell
                // you who a stranger is.
                if (!IsSelf(who))
                {
                    DrawRuleHair(12f, 10f);
                    DrawSeenRecentlyIn(who);
                }

                ImGui.Unindent(CardPad);
                ImGui.Dummy(new Vector2(cardWidth, CardPad));
            }
            finally
            {
                ImGui.EndGroup();
            }

            float cardHeight = ImGui.GetItemRectSize().Y;

            dl.ChannelsSetCurrent(0);
            var cardMax = new Vector2(cardMin.X + cardWidth, cardMin.Y + cardHeight);
            // The redesign's card: rounded-3xl, a faint white border.
            dl.AddRectFilled(cardMin, cardMax, ImGui.ColorConvertFloat4ToU32(Field), 16f);
            dl.AddRect(cardMin, cardMax, ImGui.ColorConvertFloat4ToU32(new Vector4(1, 1, 1, 0.08f)),
                16f, ImDrawFlags.None, 1f);

            dl.ChannelsMerge();
        }

        /// <summary>
        /// Who this is: what kind of card it is, the name, and one line of world and job.
        ///
        /// Stacked in a single column rather than set beside the score in a band. The band put the
        /// name and the number on the same line at different weights, which made a long name push
        /// the figure around; here nothing moves when a name gets longer.
        /// </summary>
        private void DrawProfileIdentity(CharacterIdentity who)
        {
            // The same tracked caps as every other heading in the plugin, with the three places
            // this character can be read sitting opposite as their own marks.
            //
            // They used to be three full-width buttons under the score, which gave opening a web
            // page the same visual weight as the number the card exists for. As favicons in the
            // corner they are recognisable at a glance, take a fifth of the room, and leave the
            // body of the card to the two things worth reading: the score and the clears.
            var dl = ImGui.GetWindowDrawList();

            // Both taken before the heading is drawn, and both from the cursor rather than from the
            // window: this is inside the card's indent, so the cursor plus the space available is
            // the card's own right edge and stays right if the card is ever drawn in a column.
            Vector2 headerPos = ImGui.GetCursorScreenPos();
            float headerRight = headerPos.X + ImGui.GetContentRegionAvail().X - CardPad;

            // The caption moved OUT of the card - see DrawProfilePane. What it named is what the
            // whole card is, and a heading belongs above the thing it heads, the same way the list
            // headings in the column beside it do. The line it occupied stays, because the site
            // marks sit on it.
            using (UiCaptionFont.Push())
                ImGui.Dummy(new Vector2(ImGui.GetContentRegionAvail().X, ImGui.GetTextLineHeight()));

            DrawProfileSiteLinks(who, headerPos.Y, headerRight);

            ImGui.Dummy(new Vector2(0, 6));

            // Resolved before anything is drawn, because the job's icon now leads the name rather
            // than the line below it.
            var info = Ratings?.CharacterFor(who);
            uint jobId = info?.JobId ?? 0;
            string jobName = info?.JobName ?? string.Empty;
            int jobLevel = info?.JobLevel ?? 0;

            // Your own card reads the game instead of the network. A locally-known job carries the
            // id and nothing else (see CharacterFor), so on the one character we can always answer
            // for, the line showed the world and stopped - no job, no level, and no lookup coming
            // that would fill them in.
            if (IsSelf(who))
            {
                var (liveJob, liveLevel) = pfAutomation.GetLocalJobAndLevel();
                if (liveJob != 0)
                {
                    jobId = liveJob;
                    jobName = JobData.FindById(liveJob)?.Name ?? jobName;
                    jobLevel = liveLevel;
                }
            }

            bool hasJob = jobId != 0 && !string.IsNullOrWhiteSpace(jobName);

            // The job icon at the name's own size, beside the name.
            //
            // It used to sit small at the head of the world line, where it was a second, quieter
            // statement of something the same line already said in words. Beside the name it is the
            // thing the game itself uses to say who somebody is, at the size that reading a name
            // happens at - and the line below is left to be prose.
            //
            // Sized off the person font's line height, not a constant: the icon has to match
            // whatever the name is set in, and a fixed number would only be right at one size. The
            // draw happens inside the same font push so that the abbreviation fallback, for a job
            // whose icon won't load, comes out just as large.
            float room = ImGui.GetContentRegionAvail().X - CardPad;

            using (UiPersonFont.Push())
            {
                float nameHeight = ImGui.GetTextLineHeight();

                if (hasJob)
                {
                    // 26px, from the mockup - a hair LARGER than the 24px name beside it, which is
                    // not a proportion a line height ever produces. The mark is the first thing the
                    // card says about somebody and it is sized to be read, not to fit.
                    DrawJobIconInline(jobId, ProfileJobMarkSize);
                    ImGui.SameLine(0, 8);
                    room -= ProfileJobMarkSize + 8f;
                }

                ImGui.TextColored(Ink, Fit(DisplayName(who.Name), room));
            }

            ImGui.Dummy(new Vector2(0, 4));

            // "Level 100 Gunbreaker" rather than "Lv100 Gunbreaker": this is the only prose on the
            // card, and the abbreviation was saving four characters on a line with room to spare.
            string job = !hasJob
                ? string.Empty
                : jobLevel > 0 ? $" · Level {jobLevel} {jobName}" : $" · {jobName}";

            using (UiBodyFont.Push())
            {
                ImGui.AlignTextToFramePadding();
                ImGui.TextColored(Dim,
                    Fit($"@{who.World}{job}", ImGui.GetContentRegionAvail().X - CardPad));
            }

            ImGui.Dummy(new Vector2(0, 16));
        }

        /// <summary>
        /// The three places this character can be read, as favicons in the card's top corner.
        ///
        /// Right-aligned on the heading's own line, so they cost no vertical space at all. Drawn
        /// after the heading and positioned absolutely rather than laid out with it, because the
        /// heading is drawn into the draw list rather than as a widget and there is nothing for
        /// SameLine to hang off.
        ///
        /// Reporting a player is deliberately NOT here. It was the third button when these were
        /// buttons, which put a destructive action in a row of harmless links; it lives on the
        /// party row's context menu, where it sits with the other things you do *about* somebody.
        /// </summary>
        private void DrawProfileSiteLinks(CharacterIdentity who, float rowY, float right)
        {
            // Big enough to be aimed at. They were 20px squares, which is smaller than the text
            // beside them and half the 44px a tap target is supposed to be.
            const float iconSize = 28f;
            const float gap = 8f;

            // Right to left, so the rightmost is the last drawn and the row grows leftwards from
            // the card's edge whatever the icon size ends up being.
            var sites = new[] { LinkSite.FfLogs, LinkSite.Tomestone, LinkSite.Lodestone };

            string? region = Worlds?.GetFfLogsRegion(who.World);
            Vector2 restore = ImGui.GetCursorScreenPos();

            // Erased entirely in an ordinary build - see PluginUI.AdminHooks.cs. Takes the right
            // edge and moves it left by whatever it used, so the site icons follow on without
            // either row having to know the other's width.
            DrawSubjectActions(who, rowY, ref right);

            for (int i = 0; i < sites.Length; i++)
            {
                var site = sites[i];
                float x = right - (i + 1) * iconSize - i * gap;

                // FFLogs addresses a character by region, and we have no way to guess one for a
                // world we don't know. The icon is left out entirely rather than shown dead: a
                // control that cannot work is worse than one that isn't there.
                if (site == LinkSite.FfLogs && region == null)
                    continue;

                ImGui.SetCursorScreenPos(new Vector2(x, rowY - 2f));

                string tooltip = $"Open this character on {SiteName(site)}.";

                if (DrawSiteLink(site, who.Key, iconSize, tooltip))
                {
                    Dalamud.Utility.Util.OpenLink(site switch
                    {
                        LinkSite.FfLogs => CharacterLinks.FfLogs(who.Name, who.World, region!),
                        LinkSite.Tomestone => CharacterLinks.Tomestone(who.Name, who.World),
                        _ => CharacterLinks.LodestoneSearch(who.Name, who.World),
                    });
                }
            }

            ImGui.SetCursorScreenPos(restore);
        }

        private void DrawSeenRecentlyIn(CharacterIdentity who)
        {
            var seen = Encounters?.SeenIn(who);

            // No heading over an empty list, and no line explaining that the list is empty. The
            // section simply isn't there until there is something in it.
            if (seen == null || seen.Count == 0)
                return;

            var dl = ImGui.GetWindowDrawList();
            Vector2 headingPos = ImGui.GetCursorScreenPos();

            using (UiCaptionFont.Push())
            {
                float lineH = ImGui.GetTextLineHeight();
                float used = DrawTrackedCaps(dl, headingPos, "Seen recently in", FbSlate400);
                ImGui.Dummy(new Vector2(used, lineH));
            }

            // Where the record lives is the sort of thing one reader in twenty wants to know, so
            // it goes on the dot with every other explanation.
            SameLineHelpDot("seenrecently",
                "Duties you have run with this character, recorded on this machine only. Nothing "
                + "about where you have played is ever sent anywhere.");

            ImGui.Dummy(new Vector2(0, 6));

            float right = ImGui.GetContentRegionAvail().X - CardPad;

            foreach (var (duty, when) in seen)
            {
                string label = string.IsNullOrWhiteSpace(duty) ? "A duty" : duty;
                string ago = Ago(when);
                float agoW = ImGui.CalcTextSize(ago).X;

                float x = ImGui.GetCursorPosX();
                ImGui.TextColored(TextSecondary, Fit(label, right - agoW - 14f));
                ImGui.SameLine();
                ImGui.SetCursorPosX(x + right - agoW);
                ImGui.TextColored(TextMuted, ago);
            }

        }

        /// <summary>Width the caret pair occupies, so callers can reserve a fixed column for it
        /// instead of measuring per row.</summary>
        internal static float ArrowCountWidth(int count)
        {
            ImGui.PushFont(UiBuilder.IconFont);
            float icon = ImGui.CalcTextSize(FontAwesomeIcon.CaretUp.ToIconString()).X;
            ImGui.PopFont();
            return icon + 5f + ImGui.CalcTextSize(count.ToString()).X;
        }

        /// <summary>Whether this is the logged-in character.</summary>
        private bool IsSelf(CharacterIdentity who)
        {
            var me = LocalIdentity?.Invoke();
            return me != null && me.Key.Equals(who.Key, StringComparison.OrdinalIgnoreCase);
        }

    }
}
#endif
