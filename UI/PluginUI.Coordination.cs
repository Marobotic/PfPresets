#if PFP_RATINGS
using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace PfPresets
{
    /// <summary>
    /// The PF Coordination window: what a coordinated recruitment is doing, for the two people who
    /// have a stake in it.
    ///
    /// THE HOST sees their listing's state, the applicants in the order they applied, and - once the
    /// party is full - the prompt to queue. Queueing is offered, never done for them: a full party is
    /// not necessarily a ready one.
    ///
    /// AN APPLICANT sees where they are: waiting for the party to be ready, on their way, joined,
    /// full. Until they hold a seat they see counts only - the server does not send a queued
    /// applicant the party's names, so there is nothing here to leak.
    ///
    /// It appears on its own when there is something to show and closes with the session. The close
    /// button hides it until the next thing worth knowing happens.
    /// </summary>
    public partial class PluginUI
    {
        private const float CoordinationWidth = 320f;

        private void DrawCoordinationOverlay()
        {
            var c = Coordination;
            if (c == null || !config.CommunityEnabled)
                return;

            var host = c.Host;
            var applicant = c.Applicant;

            bool showHost = host != null && !c.HostOverlayHidden;
            bool showApplicant = applicant != null && !applicant.OverlayHidden;
            if (!showHost && !showApplicant)
                return;

            var vp = ImGui.GetMainViewport();
            ImGui.SetNextWindowPos(new Vector2(vp.WorkPos.X + vp.WorkSize.X - CoordinationWidth - 40f, vp.WorkPos.Y + 120f),
                ImGuiCond.FirstUseEver);

            bool close = false;
            try
            {
                if (BeginOverlayWindow("###PfPresetsCoordination", CoordinationWidth, false))
                {
                    ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X);

                    if (showHost)
                        close |= DrawCoordinationHost(c, host!);

                    if (showHost && showApplicant)
                    {
                        ImGui.Dummy(new Vector2(0, 8));
                        Vector2 p = ImGui.GetCursorScreenPos();
                        ImGui.GetWindowDrawList().AddRectFilled(p, p + new Vector2(ImGui.GetContentRegionAvail().X, 1f),
                            ImGui.ColorConvertFloat4ToU32(FbSeparator));
                        ImGui.Dummy(new Vector2(0, 8));
                    }

                    if (showApplicant)
                        close |= DrawCoordinationApplicant(c, applicant!, closable: !showHost);

                    ImGui.PopTextWrapPos();
                }
            }
            finally
            {
                EndOverlayWindow();
            }

            if (close)
            {
                if (showHost)
                    c.HostOverlayHidden = true;
                if (showApplicant)
                {
                    applicant!.OverlayHidden = true;
                    c.DismissApplicant();
                }
            }
        }

        /// <summary>The host's side: the listing's state as a tag, the seats, the applicants as a
        /// list, and the queue buttons once the party is full. Returns true on close.</summary>
        private bool DrawCoordinationHost(PfCoordination c, PfCoordination.HostSession h)
        {
            var (close, _) = DrawOverlayHeader("coordhost", c.DutyName(h.DutyId), "You're recruiting", null);

            (string state, Vector4 tint) = h.Phase switch
            {
                PfCoordination.HostPhase.Open => !h.Registered ? ("Registering your listing", FbSlate400)
                    : h.PostedPrivate ? ("Private · applicants on their way", FbGreen)
                    : h.PostedOmitted.Length > 0 ? ($"Public · {h.PostedOmitted.Length} seat(s) held", FbBlue)
                    : ("Public · taking applications", FbBlue),
                PfCoordination.HostPhase.Switching => ("Changing your listing", AccentYellow),
                PfCoordination.HostPhase.Private => ("Private · applicants on their way", FbGreen),
                _ => ("Party full", FbGreen),
            };
            OverlayPill(state, tint);
            ImGui.Dummy(new Vector2(0, 2f));

            OverlayInset(width =>
            {
                if (h.Layout.Count > 0)
                    DrawCoordinationSeats(h.Layout, null);
                using (UiHelpFont.Push())
                    ImGui.TextColored(FbSlate400, $"{h.Filled}/{h.Seats} in party  ·  {h.Holds.Count} held");
            });

            if (h.LastError.Length > 0)
            {
                ImGui.Dummy(new Vector2(0, 2f));
                using (UiHelpFont.Push())
                    ImGui.TextColored(AccentYellow, h.LastError);
            }

            var applicants = h.Last?.Applicants
                .Where(a => a.Status is "interested" or "joining")
                .ToList();

            ImGui.Dummy(new Vector2(0, 6f));
            using (UiHelpFont.Push())
                DrawTrackedCaps(ImGui.GetWindowDrawList(), ImGui.GetCursorScreenPos(), "APPLICANTS", FbSlate400);
            using (UiHelpFont.Push())
                ImGui.Dummy(new Vector2(0, ImGui.GetTextLineHeight()));

            OverlayInset(width =>
            {
                if (applicants == null || applicants.Count == 0)
                {
                    using (UiHelpFont.Push())
                        ImGui.TextColored(FbSlate400, h.Phase == PfCoordination.HostPhase.Filled
                            ? "Everyone has arrived."
                            : "No applicants yet. Plugin users can apply from the PF Analysis board. "
                                + "They're asked to travel once every open seat has one.");
                    return;
                }

                for (int i = 0; i < applicants.Count; i++)
                {
                    var a = applicants[i];
                    string where = a.Status == "joining" ? "on the way"
                        : a.AuthorisedPrivate ? "asked to accept"
                        : a.Slot is { } slot ? $"seat {slot + 1} held"
                        : "no seat fits";
                    OverlayListRow((uint)Math.Max(0, a.Job), $"{DisplayName(a.Name)} @ {a.World}", where,
                        a.AuthorisedPrivate || a.Status == "joining" ? FbBlue : FbSlate400, width, i == applicants.Count - 1);
                }
            });

            if (h.Phase == PfCoordination.HostPhase.Filled)
            {
                ImGui.Dummy(new Vector2(0, 8f));
                if (h.QueuePromptOpen)
                {
                    using (UiBodyFont.Push())
                        ImGui.TextColored(Ink, "Your party is full. Queue when everyone is ready.");
                    ImGui.Dummy(new Vector2(0, 4f));
                    var (queue, notNow) = OverlayButtonPair("Queue for duty", "Not now", "coordqueue");
                    if (queue)
                        c.QueueHostDuty();
                    if (notNow)
                        c.DismissQueuePrompt();
                }
                else if (OverlayButton("Queue for duty", "##coordqueue2", ImGui.GetContentRegionAvail().X, true))
                {
                    c.QueueHostDuty();
                }
            }

            return close;
        }

        private const float CoordSeatIcon = 24f;

        /// <summary>
        /// The party seat by seat, the way the listing reads: the job in every filled seat, the
        /// applicant's job ringed in every held one (yours in a stronger ring), and every open
        /// seat in the colours of the roles it takes. Seats the listing never offered are left out.
        /// </summary>
        private void DrawCoordinationSeats(System.Collections.Generic.List<PfCoordinationSeatLayout> layout, int? mine)
        {
            bool first = true;
            for (int i = 0; i < layout.Count; i++)
            {
                var seat = layout[i];
                if (seat.State == "open" && (seat.Mask == "0" || seat.Mask.Length == 0))
                    continue;

                if (!first)
                    ImGui.SameLine(0, 2f);
                first = false;

                Vector2 min = ImGui.GetCursorScreenPos();
                var dl = ImGui.GetWindowDrawList();
                string tip;

                if (seat.State == "open")
                {
                    // The same icon a preset's seat taking these jobs would have.
                    ImGui.Dummy(new Vector2(CoordSeatIcon, CoordSeatIcon));
                    ulong.TryParse(seat.Mask, out ulong mask);
                    var accepted = new System.Collections.Generic.List<uint>();
                    for (int bit = 0; bit < 64; bit++)
                    {
                        if ((mask & (1UL << bit)) != 0 && JobMasks.GetJobIdFromGameBit(bit) is var id && id != 0)
                            accepted.Add(id);
                    }
                    bool omitted = mask == 0;
                    DrawSlotMiniIcon(omitted ? OmittedSlot : SeatAsSlot(accepted), min, CoordSeatIcon);
                    tip = omitted ? OmittedSeatTip : PfJobList(accepted);
                }
                else
                {
                    if (seat.Job > 0)
                        DrawJobIconInline((uint)seat.Job, CoordSeatIcon);
                    else
                        ImGui.Dummy(new Vector2(CoordSeatIcon, CoordSeatIcon));

                    string job = JobData.FindById((uint)seat.Job)?.Abbreviation ?? "Unknown job";
                    if (seat.State == "held")
                    {
                        bool yours = mine == i;
                        dl.AddCircleFilled(new Vector2(min.X + CoordSeatIcon * 0.5f, min.Y + CoordSeatIcon + 2f),
                            yours ? 3f : 2.5f, ImGui.ColorConvertFloat4ToU32(yours ? AccentGreen : Accent), 12);
                        tip = yours ? $"Your seat ({job}), held for you" : $"{job}, held for an applicant";
                    }
                    else
                    {
                        tip = $"{job}, in the party";
                    }
                }

                if (ImGui.IsItemHovered())
                    PaddedTooltip(tip);
            }
        }

        /// <summary>The applicant's side: where they stand as a tag, the party filling in, what is
        /// happening in words, the party once joined, and the buttons for this step. Returns true on
        /// close.</summary>
        private bool DrawCoordinationApplicant(PfCoordination c, PfCoordination.ApplicantSession a, bool closable)
        {
            string title = a.DutyLabel.Length > 0 ? a.DutyLabel : c.DutyName(a.DutyId);
            var (close, _) = DrawOverlayHeader("coordapplicant", title,
                $"{DisplayName(a.HostName)} @ {a.HostWorld}", null, closable);
            DrawApplicantBody(c, a);
            return close;
        }

        /// <summary>
        /// Everything about an application under its header: where it stands, the party filling
        /// in, what is happening in words, the party once joined, and this step's buttons. Shared by
        /// the PF Coordination window and the Recruit tab, so the two can never disagree.
        /// </summary>
        private void DrawApplicantBody(PfCoordination c, PfCoordination.ApplicantSession a)
        {
            (string tag, Vector4 tint) = a.Phase switch
            {
                PfCoordination.ApplicantPhase.Applying => ("Applying", FbSlate400),
                PfCoordination.ApplicantPhase.Applied => ("Waiting for the party", FbBlue),
                PfCoordination.ApplicantPhase.Offered => ("Your seat is ready", FbGreen),
                PfCoordination.ApplicantPhase.Promised => ("Accepted", FbGreen),
                PfCoordination.ApplicantPhase.Travelling => ("Travelling", FbBlue),
                PfCoordination.ApplicantPhase.Joining => ("Joining", FbBlue),
                PfCoordination.ApplicantPhase.Joined => ("Joined", FbGreen),
                PfCoordination.ApplicantPhase.Filled => ("Party full", FbGreen),
                PfCoordination.ApplicantPhase.Failed => ("Couldn't join", AccentYellow),
                _ => ("Ended", FbSlate400),
            };
            OverlayPill(tag, tint);
            ImGui.Dummy(new Vector2(0, 2f));

            var l = a.Last;
            if (l != null && l.Total > 0 && a.Phase != PfCoordination.ApplicantPhase.Ended)
            {
                OverlayInset(width =>
                {
                    if (l.Layout.Count > 0)
                    {
                        DrawCoordinationSeats(l.Layout, l.Mine?.Slot);
                    }
                    else
                    {
                        float frac = Math.Clamp(l.Filled / (float)l.Total, 0f, 1f);
                        var dl = ImGui.GetWindowDrawList();
                        Vector2 p = ImGui.GetCursorScreenPos();
                        dl.AddRectFilled(p, p + new Vector2(width, 6f), ImGui.ColorConvertFloat4ToU32(FbNeutral700), 3f);
                        dl.AddRectFilled(p, p + new Vector2(width * frac, 6f), ImGui.ColorConvertFloat4ToU32(FbBlue), 3f);
                        ImGui.Dummy(new Vector2(width, 6f));
                    }

                    int held = l.Layout.Count(x => x.State == "held");
                    using (UiHelpFont.Push())
                        ImGui.TextColored(FbSlate400, $"{l.Filled}/{l.Total} in party  ·  {held} held for applicants");
                });
            }

            ImGui.Dummy(new Vector2(0, 4f));
            using (UiBodyFont.Push())
                ImGui.TextColored(a.Phase == PfCoordination.ApplicantPhase.Failed ? AccentYellow : Ink, a.Status);

            // The party, once this client is in it or has a seat waiting in it. The server does not
            // send it before then.
            if (l != null && l.Members.Count > 0 && a.Phase is PfCoordination.ApplicantPhase.Joined
                    or PfCoordination.ApplicantPhase.Filled)
            {
                var joined = l.Members.Where(m => m.Status == "joined").ToList();
                if (joined.Count > 0)
                {
                    ImGui.Dummy(new Vector2(0, 4f));
                    OverlayInset(width =>
                    {
                        for (int i = 0; i < joined.Count; i++)
                            OverlayListRow((uint)Math.Max(0, joined[i].Job), $"{DisplayName(joined[i].Name)} @ {joined[i].World}",
                                string.Empty, FbSlate400, width, i == joined.Count - 1);
                    });
                }
            }

            float full = ImGui.GetContentRegionAvail().X;
            switch (a.Phase)
            {
                case PfCoordination.ApplicantPhase.Offered:
                {
                    ImGui.Dummy(new Vector2(0, 8f));
                    var (accept, decline) = OverlayButtonPair("Accept", "Decline", "coordoffer");
                    if (accept)
                        c.AcceptOffer();
                    if (decline)
                        c.DeclineOffer();
                    break;
                }

                case PfCoordination.ApplicantPhase.Failed:
                {
                    ImGui.Dummy(new Vector2(0, 8f));
                    var (retry, withdraw) = OverlayButtonPair("Try again", "Withdraw", "coordfailed");
                    if (retry)
                        c.RetryJoin();
                    if (withdraw)
                        c.Withdraw();
                    break;
                }

                case PfCoordination.ApplicantPhase.Ended:
                    ImGui.Dummy(new Vector2(0, 8f));
                    if (OverlayButton("Dismiss", "##coorddismiss", full, false))
                        c.DismissApplicant();
                    break;

                case PfCoordination.ApplicantPhase.Joined:
                case PfCoordination.ApplicantPhase.Filled:
                    break;

                case PfCoordination.ApplicantPhase.Applied:
                {
                    // Waiting for the party to fill - or go now.
                    ImGui.Dummy(new Vector2(0, 8f));
                    var (can, why) = c.JoinNowState();
                    float each = (full - 8f) * 0.5f;
                    if (OverlayButton(a.JoinNow ? "On the way..." : "Join party now", "##coordjoinnow", each, true, can))
                        c.JoinNow();
                    if (ImGui.IsItemHovered())
                        PaddedTooltip(why);
                    ImGui.SameLine(0, 8f);
                    if (OverlayButton("Withdraw", "##coordwithdraw", each, false))
                        c.Withdraw();
                    break;
                }

                default:
                    ImGui.Dummy(new Vector2(0, 8f));
                    if (OverlayButton("Withdraw", "##coordwithdraw", full, false))
                        c.Withdraw();
                    break;
            }
        }
    }
}
#endif
