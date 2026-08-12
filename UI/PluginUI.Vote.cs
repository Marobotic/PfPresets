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
                        poll = got;
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

            // A CARD, NOT A LINE OF TEXT. Drawn on a split channel so the panel is exactly its
            // contents and the accent edge sits behind them - the same construction the profile
            // card uses, and the reason it does not need a height guessed in advance.
            float width = ImGui.GetContentRegionAvail().X - RowInset;
            var dl = ImGui.GetWindowDrawList();
            Vector2 cardMin = ImGui.GetCursorScreenPos();

            dl.ChannelsSplit(2);
            dl.ChannelsSetCurrent(1);

            ImGui.BeginGroup();
            try
            {
                ImGui.Dummy(new Vector2(width, 14f));
                ImGui.Indent(16f);

                ImGui.PushTextWrapPos(cardMin.X + width - 20f);
                using (UiBodyFont.Push())
                    ImGui.TextColored(Ink, "The controversy, and the future of PF Analysis");
                ImGui.PopTextWrapPos();

                ImGui.Dummy(new Vector2(0, 10f));

                if (DrawNeutralButton("Read the post##pollpost", new Vector2(150, ButtonHeight)))
                    Dalamud.Utility.Util.OpenLink(p.PostUrl);

                ImGui.Unindent(16f);
                ImGui.Dummy(new Vector2(width, 14f));
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

            // The accent down the left edge, which is what makes it read as a card rather than as
            // a box. Three pixels, matching the band on the website.
            dl.AddRectFilled(cardMin, new Vector2(cardMin.X + 3f, cardMax.Y),
                ImGui.ColorConvertFloat4ToU32(Accent));

            dl.ChannelsMerge();
        }

        private void DrawPollBody(PollResponse p)
        {
            using (UiCaptionFont.Push())
                ImGui.TextColored(Faint, "COMMUNITY VOTE");

            ImGui.Dummy(new Vector2(0, 8));

            using (UiHeadingFont.Push())
                ImGui.TextColored(Ink, p.Question);

            if (!string.IsNullOrWhiteSpace(p.Blurb))
            {
                ImGui.Dummy(new Vector2(0, 4));
                ImGui.PushTextWrapPos(ImGui.GetContentRegionMax().X - RowInset);
                ImGui.TextColored(Dim, p.Blurb);
                ImGui.PopTextWrapPos();
            }

            ImGui.Dummy(new Vector2(0, 4));

            DrawPollMeta(p);

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

            if (!ready)
                ImGui.BeginDisabled();

            if (DrawPrimaryButton("Cast vote##pollcast", new Vector2(150, ButtonHeight)))
                CastPollVote(p);

            if (!ready)
                ImGui.EndDisabled();

            ImGui.SameLine(0, 10);

            if (DrawNeutralButton("Share this poll##pollshare", new Vector2(170, ButtonHeight)))
                pollShareOpen = true;

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
        /// The three facts about the poll, as a row of pieces rather than one long sentence.
        ///
        /// A single line of middot-separated text runs straight off the edge of a narrow window,
        /// which is what the first version did. Each piece is measured and the row breaks between
        /// them when there is not room, so it degrades into two tidy lines instead of one clipped
        /// one.
        /// </summary>
        private void DrawPollMeta(PollResponse p)
        {
            string closes = p.ClosesAt.HasValue
                ? $"Closes {p.ClosesAt.Value.ToLocalTime():d MMMM}"
                : string.Empty;

            string[] parts = closes.Length > 0
                ? new[] { "One vote per person", "Results published at close", closes }
                : new[] { "One vote per person", "Results published at close" };

            float right = ImGui.GetContentRegionMax().X - RowInset;

            using (UiHelpFont.Push())
            {
                for (int i = 0; i < parts.Length; i++)
                {
                    float need = ImGui.CalcTextSize(parts[i]).X + (i > 0 ? 22f : 0f);

                    if (i > 0 && ImGui.GetCursorPosX() + need < right)
                    {
                        ImGui.SameLine(0, 10);
                        ImGui.TextColored(RuleStrong, "|");
                        ImGui.SameLine(0, 10);
                    }

                    ImGui.TextColored(Faint, parts[i]);
                }
            }
        }

        /// <summary>
        /// One option: a bordered row with a square mark, the label, and the detail under it.
        ///
        /// TWO COORDINATE SPACES MEET HERE and getting them confused is what broke the first
        /// version. `PushTextWrapPos` takes a WINDOW-LOCAL x; it was handed a screen x, so the wrap
        /// point landed hundreds of pixels off and the longest option ran past the edge of the
        /// window. And `TextColored` returns the cursor to the window's content margin, so the
        /// detail line - drawn without setting the cursor again - started at the far left, outside
        /// the box it belonged to.
        ///
        /// Both are avoided the same way: the cursor is placed in SCREEN space before each line,
        /// and the wrap point is derived from GetCursorPosX afterwards, which is already local.
        /// </summary>
        private void DrawPollOption(PollOption option)
        {
            bool chosen = pollChoice == option.Id;

            const float pad = 12f;
            const float markColumn = 34f;

            Vector2 start = ImGui.GetCursorScreenPos();
            float width = ImGui.GetContentRegionAvail().X - RowInset;
            float textWidth = Math.Max(80f, width - markColumn - 14f);

            // Measured at the width the text will actually wrap to, so the box is tall enough to
            // hold it. The first version measured one width and wrapped at another, which is why
            // the detail fell out of the bottom of the row.
            Vector2 labelSize = ImGui.CalcTextSize(option.Label, false, textWidth);
            Vector2 detailSize = option.Detail.Length > 0
                ? ImGui.CalcTextSize(option.Detail, false, textWidth)
                : Vector2.Zero;

            float height = pad * 2f + labelSize.Y
                + (detailSize.Y > 0 ? detailSize.Y : 0f);

            // The hit area first, so the whole row is the control and the input is ImGui's rather
            // than a hand-rolled mouse test.
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

            // A square, not a circle: everything in this plugin is square, and a radio dot would be
            // the one round thing on screen.
            var mark = new Vector2(start.X + 13f, start.Y + pad + 2f);
            var markEnd = new Vector2(mark.X + 13f, mark.Y + 13f);

            dl.AddRect(mark, markEnd,
                ImGui.ColorConvertFloat4ToU32(chosen ? Accent : BorderControl), 0f, 0, 1f);

            if (chosen)
            {
                dl.AddRectFilled(new Vector2(mark.X + 3f, mark.Y + 3f),
                    new Vector2(markEnd.X - 3f, markEnd.Y - 3f),
                    ImGui.ColorConvertFloat4ToU32(Accent));
            }

            // The label. Cursor set in screen space, wrap point taken from the local x it produced.
            ImGui.SetCursorScreenPos(new Vector2(start.X + markColumn, start.Y + pad));
            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + textWidth);

            using (UiBodyFont.Push())
                ImGui.TextColored(Ink, option.Label);

            ImGui.PopTextWrapPos();

            if (option.Detail.Length > 0)
            {
                // Set again, because the line above returned the cursor to the content margin.
                float detailY = ImGui.GetCursorScreenPos().Y;
                ImGui.SetCursorScreenPos(new Vector2(start.X + markColumn, detailY));
                ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + textWidth);
                ImGui.TextColored(Dim, option.Detail);
                ImGui.PopTextWrapPos();
            }

            // Below the row, whatever the text did inside it.
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
            ImGui.TextColored(Dim, $"Results appear here {when}.");
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
