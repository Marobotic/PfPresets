#if PFP_RATINGS
using System;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;

namespace PfPresets
{
    /// <summary>
    /// The Vote tab: a community poll about the plugin itself.
    ///
    /// TEMPORARY BY CONSTRUCTION. The tab exists only while the server says a poll is open, so it
    /// appears when one starts and is gone when it ends without a release either way. Nothing about
    /// the question, the options or the closing date is compiled in - all of it arrives from
    /// `GET /pfp/v2/poll`, which is the same call the three web pages make, so the wording exists in
    /// one place and cannot drift between the plugin and the site.
    ///
    /// NO COUNTS WHILE IT RUNS, and that is not this file being careful: the response carries none
    /// until the poll is published. A running tally would tell anybody pushing an option whether the
    /// pushing is working, and it would put a bandwagon in front of everybody else.
    ///
    /// SHOWN TO PEOPLE WHO HAVE OPTED OUT, unlike every other community surface, and the reasoning
    /// is worth writing down because it cuts against Configuration.CommunityEnabled. This poll
    /// decides the future of the rating system. The people who opted out of that system are the
    /// people with the strongest opinion about it, and a vote on whether it should exist that
    /// excluded them would be indefensible the moment somebody noticed.
    ///
    /// What their opt-out still buys them is that their vote carries nothing identifying: see
    /// `Identified` below, where an opted-out install votes without a session and lands as an
    /// ordinary anonymous vote.
    /// </summary>
    public partial class PluginUI
    {
        private PollResponse? poll;
        private bool pollBusy;
        private DateTime pollCheckedAt = DateTime.MinValue;

        private string pollChoice = string.Empty;
        private string pollNote = string.Empty;
        private bool pollVoted;
        private bool pollSending;
        private bool pollShareOpen;

        /// <summary>
        /// How far every block in this tab stops short of the content edge.
        ///
        /// The same number as the tab's own Indent, so the left and right margins match. They did
        /// not: the card was measured off GetContentRegionMax (a local x, used as though it were a
        /// width) and the rows off GetContentRegionAvail, so the two ended four pixels apart and
        /// the rows ran flush into the window border.
        /// </summary>
        private const float RowInset = 12f;

        /// <summary>How long between asking the server whether a poll is running.</summary>
        private static readonly TimeSpan PollRecheck = TimeSpan.FromMinutes(10);

        /// <summary>Whether there is a poll worth giving a tab to.</summary>
        private bool PollAvailable => poll is { Open: true } || poll?.Results != null;

        /// <summary>
        /// Whether this install's vote carries its session.
        ///
        /// An install taking part in the community half votes as itself, which is a far stronger
        /// signal than a browser vote and is what lets somebody behind a shared address vote at all.
        /// An install that has opted out votes anonymously - the vote counts, and nothing about the
        /// character reaches the server with it.
        /// </summary>
        private bool PollIdentified => config.CommunityEnabled;

        /// <summary>
        /// Asks whether a poll is running. Safe to call every frame; throttled inside.
        ///
        /// Called from the tab list rather than from the tab body, because the answer is what
        /// decides whether the tab is in the list at all.
        /// </summary>
        private void EnsurePollLoaded()
        {
            if (pollBusy || Ratings == null) return;
            if (poll != null && DateTime.UtcNow - pollCheckedAt < PollRecheck) return;
            if (DateTime.UtcNow - pollCheckedAt < TimeSpan.FromSeconds(30)) return;

            pollBusy = true;
            pollCheckedAt = DateTime.UtcNow;

            _ = Task.Run(async () =>
            {
                try
                {
                    var got = await Ratings.GetPollAsync().ConfigureAwait(false);
                    if (got != null)
                    {
                        poll = got;

                        // ONE WINDOW ACROSS ALL FOUR SURFACES. A vote cast on the website closes
                        // this one, and the other way round - the server keys both on the same
                        // address, so it is the only thing that can know.
                        if (got.Voted)
                            pollVoted = true;
                    }
                }
                catch (Exception)
                {
                    // Swallowed on purpose, and this is the one place in this session where that is
                    // right: the answer decides whether a TAB EXISTS. A poll that cannot be reached
                    // leaves no tab, which is indistinguishable from there being no poll - and is
                    // the correct outcome either way. Nothing is shown, so nothing can be wrong.
                }
                finally
                {
                    pollBusy = false;
                }
            });
        }

        private void DrawVoteTab()
        {
            var p = poll;
            if (p == null)
                return;

            // THE BODY SCROLLS, NOT THE WINDOW, and this child is the whole of what makes that
            // true. Without it the tab's content simply overflowed the window, so the window
            // itself scrolled and took the title bar and the tab strip up and out of sight with
            // it - navigation you cannot reach because you scrolled down is the one failure a tab
            // strip must not have. Every other tab in this plugin opens a child for the same
            // reason; this one did not, and that was the whole bug.
            ImGui.BeginChild("VoteBody", new Vector2(0, 0), false);
            try
            {
                ImGui.Dummy(new Vector2(0, 8));
                ImGui.Indent(12);

                try
                {
                    DrawPollPost(p);
                    DrawRuleHair(14f, 14f);
                    DrawPollBody(p);
                }
                finally
                {
                    ImGui.Unindent(12);
                }

                // Room under the last control, so the tab scrolls past it rather than stopping
                // with it half inside the clip rect.
                ImGui.Dummy(new Vector2(0, 20));
            }
            finally
            {
                ImGui.EndChild();
            }
        }

        /// <summary>
        /// The way through to the post, above the poll.
        ///
        /// First because voting on five options without the reasoning behind them is how a poll
        /// gets a result nobody trusts.
        /// </summary>
        private void DrawPollPost(PollResponse p)
        {
            if (string.IsNullOrWhiteSpace(p.PostUrl))
                return;

            using (UiCaptionFont.Push())
                ImGui.TextColored(Faint, "FROM THE DEVELOPER");

            ImGui.Dummy(new Vector2(0, 8));

            const float markSize = 30f;
            const float markGap = 14f;

            float width = ImGui.GetContentRegionAvail().X - RowInset;
            var dl = ImGui.GetWindowDrawList();
            Vector2 cardMin = ImGui.GetCursorScreenPos();

            dl.ChannelsSplit(2);
            dl.ChannelsSetCurrent(1);

            ImGui.BeginGroup();
            try
            {
                ImGui.Dummy(new Vector2(width, 15f));

                // The heading and the button share a left edge, and the mark sits outside it. The
                // mark is decoration; the text column is the content, and they should not be
                // arguing about where the column starts.
                float textLeft = 16f + markSize + markGap;
                ImGui.Indent(textLeft);

                ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + width - textLeft - 14f);
                using (UiBodyFont.Push())
                    ImGui.TextColored(Ink, "The controversy, and the future of PF Analysis");
                ImGui.PopTextWrapPos();

                ImGui.Dummy(new Vector2(0, 9f));

                if (DrawAccentOutlineButton("Read the post \u2197##pollpost",
                        new Vector2(148, ButtonHeight)))
                    Dalamud.Utility.Util.OpenLink(p.PostUrl);

                ImGui.Unindent(textLeft);
                ImGui.Dummy(new Vector2(width, 15f));
            }
            finally
            {
                ImGui.EndGroup();
            }

            float cardHeight = ImGui.GetItemRectSize().Y;
            var cardMax = new Vector2(cardMin.X + width, cardMin.Y + cardHeight);

            dl.ChannelsSetCurrent(0);
            dl.AddRectFilled(cardMin, cardMax, ImGui.ColorConvertFloat4ToU32(Field));
            dl.AddRect(cardMin, cardMax, ImGui.ColorConvertFloat4ToU32(RuleHair), 0f, 0, 1f);
            dl.AddRectFilled(cardMin, new Vector2(cardMin.X + 3f, cardMax.Y),
                ImGui.ColorConvertFloat4ToU32(Accent));

            // The mark: a bordered square with the glyph centred in it, drawn on the background
            // channel so the group above never had to leave room for it.
            var markMin = new Vector2(cardMin.X + 16f, cardMin.Y + 15f);
            var markMax = new Vector2(markMin.X + markSize, markMin.Y + markSize);

            dl.AddRect(markMin, markMax, ImGui.ColorConvertFloat4ToU32(BorderControl), 0f, 0, 1f);

            using (pluginInterface.UiBuilder.IconFontHandle.Push())
            {
                string glyph = FontAwesomeIcon.PenNib.ToIconString();
                Vector2 g = ImGui.CalcTextSize(glyph);
                dl.AddText(new Vector2(markMin.X + (markSize - g.X) * 0.5f,
                                       markMin.Y + (markSize - g.Y) * 0.5f),
                    ImGui.ColorConvertFloat4ToU32(Accent), glyph);
            }

            dl.ChannelsMerge();
        }

        private void DrawPollBody(PollResponse p)
        {
            using (UiCaptionFont.Push())
                ImGui.TextColored(Faint, "COMMUNITY VOTE");

            ImGui.Dummy(new Vector2(0, 8));

            using (UiHeadingFont.Push())
                ImGui.TextColored(Ink, p.Question);

            ImGui.Dummy(new Vector2(0, 4));

            // ONE SENTENCE, NOT A ROW OF FACTS. The blurb and "results are published when voting
            // closes" are the same thought, and splitting them into a mono row of pipe-separated
            // items put three pieces of furniture where a line of prose belonged. The closing date
            // is the only one that is genuinely a fact rather than a sentence, and it has moved
            // next to the button, which is where somebody is when they want it.
            string blurb = p.Blurb.Length > 0 ? p.Blurb : string.Empty;
            string sentence = blurb.Length > 0
                ? $"{blurb} Results are published when voting closes, not before."
                : "Results are published when voting closes, not before.";

            ImGui.PushTextWrapPos(ImGui.GetContentRegionMax().X - RowInset);
            ImGui.TextColored(Dim, sentence);
            ImGui.PopTextWrapPos();

            ImGui.Dummy(new Vector2(0, 12));

            if (p.Results != null)
            {
                DrawPollResults(p);
                return;
            }

            if (pollVoted)
            {
                DrawPollThanks(p);
                return;
            }

            foreach (var option in p.Options)
                DrawPollOption(option);

            ImGui.Dummy(new Vector2(0, 12));

            bool ready = pollChoice.Length > 0 && !pollSending;

            // DECIDED BEFORE ANYTHING IS DRAWN, from widths that are known, because the obvious
            // way round does not work: after a button the cursor has already wrapped to the next
            // line, so GetCursorPosX reads the LEFT margin. The check therefore always found room
            // and always chose SameLine, which is how the date ended up clipped by the window edge
            // in the one case the check existed to catch.
            const float castWidth = 150f;
            const float shareWidth = 160f;
            const float buttonGap = 10f;
            const float dateGap = 14f;

            string closes = p.ClosesAt.HasValue
                ? $"Closes {p.ClosesAt.Value.ToLocalTime():d MMMM}"
                : string.Empty;

            float dateWidth = 0f;
            if (closes.Length > 0)
            {
                using (UiHelpFont.Push())
                    dateWidth = ImGui.CalcTextSize(closes).X;
            }

            float row = ImGui.GetContentRegionAvail().X - RowInset;
            bool dateFits = closes.Length > 0
                && castWidth + buttonGap + shareWidth + dateGap + dateWidth <= row;

            if (!ready)
                ImGui.BeginDisabled();

            if (DrawPrimaryButton("Cast vote##pollcast", new Vector2(castWidth, ButtonHeight)))
                CastPollVote(p);

            if (!ready)
                ImGui.EndDisabled();

            ImGui.SameLine(0, buttonGap);

            if (DrawAccentOutlineButton("Share this poll##pollshare",
                    new Vector2(shareWidth, ButtonHeight)))
                pollShareOpen = true;

            if (closes.Length > 0)
            {
                if (dateFits)
                {
                    ImGui.SameLine(0, dateGap);
                    ImGui.AlignTextToFramePadding();
                }
                else
                {
                    ImGui.Dummy(new Vector2(0, 8));
                }

                using (UiHelpFont.Push())
                    ImGui.TextColored(Faint, closes);
            }

            if (pollNote.Length > 0)
            {
                ImGui.Dummy(new Vector2(0, 8));
                ImGui.PushTextWrapPos(ImGui.GetContentRegionMax().X - RowInset);
                using (UiHelpFont.Push())
                    ImGui.TextColored(AccentYellow, pollNote);
                ImGui.PopTextWrapPos();
            }
        }

        /// <summary>
        /// Two-toned text laid out as ONE wrapped paragraph.
        ///
        /// This is what the mockup asks for and what ImGui will not do on its own: the label and
        /// the detail run together and wrap together, with the label brighter. TextWrapped cannot
        /// change colour mid-run, and SameLine cannot wrap, so the words are placed by hand onto
        /// the draw list - which this row is already using for its background anyway.
        ///
        /// Measures and draws through the same code, because a height computed by one routine and a
        /// layout produced by another is exactly how the detail fell out of the bottom of the box
        /// the first time.
        /// </summary>
        private float DrawTwoTonedRun(Vector2 at, float wrapWidth,
            string strong, string rest, bool draw)
        {
            var dl = ImGui.GetWindowDrawList();
            float lineHeight = ImGui.GetTextLineHeight();
            float spaceW = ImGui.CalcTextSize(" ").X;

            float x = at.X;
            float y = at.Y;
            bool atLineStart = true;

            void Run(string text, Vector4 colour)
            {
                if (string.IsNullOrWhiteSpace(text))
                    return;

                foreach (string word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    float w = ImGui.CalcTextSize(word).X;

                    if (!atLineStart && x + spaceW + w > at.X + wrapWidth)
                    {
                        x = at.X;
                        y += lineHeight;
                        atLineStart = true;
                    }

                    if (!atLineStart)
                    {
                        x += spaceW;
                    }

                    if (draw)
                        dl.AddText(new Vector2(x, y), ImGui.ColorConvertFloat4ToU32(colour), word);

                    x += w;
                    atLineStart = false;
                }
            }

            Run(strong, Ink);
            Run(rest, Dim);

            return (y - at.Y) + lineHeight;
        }

        /// <summary>
        /// One option: a bordered row with a square mark and the text beside it.
        ///
        /// The label and the detail are one wrapped paragraph, not two lines - see
        /// DrawTwoTonedRun. The mark is a square because everything in this plugin is square, and a
        /// radio dot would be the one round thing on screen.
        /// </summary>
        private void DrawPollOption(PollOption option)
        {
            bool chosen = pollChoice == option.Id;

            const float padY = 12f;
            const float markColumn = 34f;

            Vector2 start = ImGui.GetCursorScreenPos();
            float width = ImGui.GetContentRegionAvail().X - RowInset;
            float textWidth = Math.Max(80f, width - markColumn - 14f);

            var textAt = new Vector2(start.X + markColumn, start.Y + padY);

            // Measured by the routine that will draw it, at the width it will wrap to.
            float textHeight = DrawTwoTonedRun(textAt, textWidth, option.Label, option.Detail, false);
            float height = padY * 2f + textHeight;

            ImGui.InvisibleButton($"##pollopt{option.Id}", new Vector2(width, height));
            bool hovered = ImGui.IsItemHovered();

            if (ImGui.IsItemClicked())
            {
                pollChoice = option.Id;
                pollNote = string.Empty;
            }

            var dl = ImGui.GetWindowDrawList();
            var end = new Vector2(start.X + width, start.Y + height);

            dl.AddRectFilled(start, end,
                ImGui.ColorConvertFloat4ToU32(chosen || hovered ? Raised : Field));
            dl.AddRect(start, end,
                ImGui.ColorConvertFloat4ToU32(chosen ? Accent : RuleHair), 0f, 0, 1f);

            var mark = new Vector2(start.X + 13f, start.Y + padY + 1f);
            var markEnd = new Vector2(mark.X + 13f, mark.Y + 13f);

            dl.AddRect(mark, markEnd,
                ImGui.ColorConvertFloat4ToU32(chosen ? Accent : BorderControl), 0f, 0, 1f);

            if (chosen)
            {
                dl.AddRectFilled(new Vector2(mark.X + 3f, mark.Y + 3f),
                    new Vector2(markEnd.X - 3f, markEnd.Y - 3f),
                    ImGui.ColorConvertFloat4ToU32(Accent));
            }

            DrawTwoTonedRun(textAt, textWidth, option.Label, option.Detail, true);

            ImGui.SetCursorScreenPos(new Vector2(start.X, end.Y + 7f));
        }

        private void DrawPollThanks(PollResponse p)
        {
            using (UiBodyFont.Push())
                ImGui.TextColored(Positive, "Your vote is in.");

            ImGui.Dummy(new Vector2(0, 4));

            string when = p.ClosesAt.HasValue
                ? $"on {p.ClosesAt.Value.ToLocalTime():d MMMM}"
                : "when voting closes";

            ImGui.PushTextWrapPos(ImGui.GetContentRegionMax().X - RowInset);
            ImGui.TextColored(Dim,
                $"One vote per person, counted wherever you cast it. Results appear here {when}.");
            ImGui.PopTextWrapPos();

            ImGui.Dummy(new Vector2(0, 12));

            if (DrawNeutralButton("Share this poll##pollshare2", new Vector2(170, ButtonHeight)))
                pollShareOpen = true;
        }

        /// <summary>The result, once there is one to draw.</summary>
        private void DrawPollResults(PollResponse p)
        {
            var results = p.Results!;

            using (UiHelpFont.Push())
                ImGui.TextColored(Faint, $"{results.Total} votes");

            ImGui.Dummy(new Vector2(0, 10));

            float width = ImGui.GetContentRegionMax().X - 16;

            foreach (var row in results.Options)
            {
                float share = results.Total > 0 ? row.Votes / (float)results.Total : 0f;

                using (UiBodyFont.Push())
                    ImGui.TextColored(Ink, row.Label);

                ImGui.SameLine();
                using (UiHelpFont.Push())
                {
                    string pct = $"{Math.Round(share * 100)}%";
                    float used = ImGui.CalcTextSize(pct).X;
                    ImGui.SetCursorPosX(ImGui.GetCursorPosX() + width - used - ImGui.GetCursorPosX() + 12f);
                    ImGui.TextColored(Dim, pct);
                }

                Vector2 barAt = ImGui.GetCursorScreenPos();
                var dl = ImGui.GetWindowDrawList();

                dl.AddRectFilled(barAt, new Vector2(barAt.X + width, barAt.Y + 8f),
                    ImGui.ColorConvertFloat4ToU32(Field));
                if (share > 0f)
                {
                    dl.AddRectFilled(barAt, new Vector2(barAt.X + width * share, barAt.Y + 8f),
                        ImGui.ColorConvertFloat4ToU32(Accent));
                }

                ImGui.Dummy(new Vector2(width, 8f));
                ImGui.Dummy(new Vector2(0, 8));
            }
        }

        private void CastPollVote(PollResponse p)
        {
            if (pollChoice.Length == 0 || pollSending)
                return;

            pollSending = true;
            pollNote = string.Empty;

            string slug = p.Slug;
            string option = pollChoice;
            string token = p.Token;
            bool identified = PollIdentified;

            _ = Task.Run(async () =>
            {
                try
                {
                    string error = await Ratings!
                        .VotePollAsync(slug, option, token, identified).ConfigureAwait(false);

                    if (error.Length == 0)
                    {
                        pollVoted = true;
                        return;
                    }

                    // In words, when the reason is one a person can act on. The rest stay vague
                    // because the rest exist to catch scripts, and a script learns nothing from
                    // vagueness.
                    pollNote = error switch
                    {
                        "already_voted" => "A vote has already been cast from this connection.",
                        "blocked_network" => "Votes are not accepted from VPNs. Turn it off and "
                            + "try again.",
                        "closed" => "Voting has closed.",
                        "too_fast" => "Give it a moment, then try again.",
                        "stale_page" => "That took too long. Reopen the tab and try again.",
                        _ => "Could not send your vote. Try again in a moment.",
                    };

                    // The token is spent or stale either way; the next attempt needs a fresh one.
                    if (error is "stale_page" or "too_fast")
                        pollCheckedAt = DateTime.MinValue;
                }
                catch (Exception ex)
                {
                    pollNote = $"Could not send your vote: {ex.Message}";
                }
                finally
                {
                    pollSending = false;
                }
            });
        }

        /// <summary>
        /// The share window: the link, and the two things anybody wants to do with one.
        ///
        /// Drawn from the overlay rather than inside the tab so it owns its own space, the same way
        /// every other dialog in this plugin is.
        /// </summary>
        private void DrawPollShare()
        {
            if (!pollShareOpen)
                return;

            const string url = "https://pfa.marobotic.dev/voting";

            bool open = pollShareOpen;

            if (BeginDialog("Share this poll", "PfPresetsPollShare", 400f, ref open))
            {
                ImGui.PushTextWrapPos(ImGui.GetContentRegionMax().X - RowInset);
                ImGui.TextColored(Dim,
                    "Anyone can vote from this link, whether or not they have the plugin.");
                ImGui.PopTextWrapPos();

                ImGui.Dummy(new Vector2(0, 10));

                // Drawn as a framed line rather than as an input: there is nothing to type here,
                // and a read-only text box invites somebody to try.
                Vector2 boxAt = ImGui.GetCursorScreenPos();
                float boxW = ImGui.GetContentRegionMax().X - 16;
                var dl = ImGui.GetWindowDrawList();

                dl.AddRectFilled(boxAt, new Vector2(boxAt.X + boxW, boxAt.Y + 30f),
                    ImGui.ColorConvertFloat4ToU32(Field));
                dl.AddRect(boxAt, new Vector2(boxAt.X + boxW, boxAt.Y + 30f),
                    ImGui.ColorConvertFloat4ToU32(RuleHair), 0f, 0, 1f);

                ImGui.SetCursorScreenPos(new Vector2(boxAt.X + 10f, boxAt.Y + 7f));
                ImGui.TextColored(Ink, url);
                ImGui.SetCursorScreenPos(new Vector2(boxAt.X, boxAt.Y + 30f));

                ImGui.Dummy(new Vector2(0, 12));

                if (DrawPrimaryButton("Copy link##pollcopy", new Vector2(130, ButtonHeight)))
                {
                    ImGui.SetClipboardText(url);
                    pollNote = "Link copied.";
                }

                ImGui.SameLine(0, 10);

                if (DrawNeutralButton("Open in browser##pollopen", new Vector2(170, ButtonHeight)))
                    Dalamud.Utility.Util.OpenLink(url);

                EndDialog();
            }

            pollShareOpen = open;
        }
    }
}
#endif
