#if PFP_RATINGS
using System;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;

namespace PfPresets
{
    /// <summary>
    /// The report dialog.
    ///
    /// Reports go to the plugin author, and the dialog says so plainly. A report button that looks
    /// official but isn't is actively harmful: someone being genuinely harassed needs Square Enix's
    /// system, and a plugin that quietly absorbs that report has made things worse.
    ///
    /// Uses the shared dialog chrome from PluginUI.Dialogs.cs. It isn't a confirmation, so it
    /// doesn't go through AskConfirm - but it shouldn't be styled by hand either.
    /// </summary>
    public partial class PluginUI
    {
        private ReportReason reportReason = ReportReason.Harassment;
        private string reportNote = string.Empty;
        private volatile string reportStatus = string.Empty;
        private bool reportSubmitting;

        /// <summary>Whether to withhold the reporter's own name from this report. Default off:
        /// a named report is easier to act on, so anonymity is the deliberate choice rather than
        /// the accidental one.</summary>
        private bool reportAnonymous;

        /// <summary>Set once a report has actually landed. The form is replaced by a
        /// acknowledgement rather than left sitting there with a live Send button - a filled-in
        /// form after a successful send invites a second, identical report.</summary>
        private bool reportSent;

        private const int MaxReportNote = 500;

        /// <summary>
        /// Opens the dialog for one character and resets every field it owns.
        ///
        /// Single entry point on purpose: the party row and the recent-players context menu both
        /// come through here, so neither can forget to clear a note or an anonymity tick left over
        /// from the last person who was reported.
        /// </summary>
        private void OpenReportDialog(CharacterIdentity who)
        {
            reportTarget = who;
            reportReason = ReportReason.Harassment;
            reportNote = string.Empty;
            reportStatus = string.Empty;
            reportSent = false;

            // Reset per report rather than remembered. Anonymity is a decision about one specific
            // report, and a sticky toggle would silently anonymise later ones that the reporter
            // would have been happy to put their name to.
            reportAnonymous = false;
        }

        private void DrawReportDialog()
        {
            var identity = reportTarget;
            if (identity == null)
                return;

            bool open = true;

            // Not a confirmation, so it isn't AskConfirm - but it is a dialog, so it uses the same
            // chrome. That style stack is shared with every other plugin; having one place that
            // pushes and pops it is what stops a mismatch corrupting everyone's UI.
            if (BeginDialog("Report", "PfPresetsReport", 360f, ref open))
            {
                try
                {
                    DrawReportBody(identity);
                }
                finally
                {
                    EndDialog();
                }
            }
            else
            {
                EndDialog();
            }

            if (!open)
                reportTarget = null;
        }

        private void DrawReportBody(CharacterIdentity identity)
        {
            if (reportSent)
            {
                DrawReportThanks();
                return;
            }

            ImGui.TextColored(TextPrimary, identity.Name);
            ImGui.SameLine(0, 5);
            ImGui.TextColored(TextMuted, $"@{identity.World}");

            // Said once, where it's read. The old version explained this three times over.
            ImGui.TextColored(AccentYellow, "Goes to the plugin author, not Square Enix.");

            ImGui.Dummy(new Vector2(0, 8));

            foreach (var (reason, label) in ReportReasons.All)
            {
                bool selected = reportReason == reason;
                if (ImGui.RadioButton($"{label}##reason{(int)reason}", selected))
                    reportReason = reason;
            }

            ImGui.Dummy(new Vector2(0, 8));

            ImGui.PushStyleColor(ImGuiCol.FrameBg, BgCardExpanded);
            ImGui.PushStyleColor(ImGuiCol.Border, BorderDefault);
            ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 1.0f);
            // The 500-char cap is enforced here and again by the database. A counter on an
            // optional field is noise until you're near the limit, so there isn't one.
            ImGui.InputTextMultiline("##ReportNote", ref reportNote, MaxReportNote,
                new Vector2(-1, 60));
            ImGui.PopStyleVar();
            ImGui.PopStyleColor(2);

            ImGui.Dummy(new Vector2(0, 6));

            DrawStyledCheckbox("Send anonymously##ReportAnon", ref reportAnonymous);
            if (ImGui.IsItemHovered())
            {
                PaddedTooltip(
                    "Leaves your name off the report.\n\n"
                    + "The report itself still arrives - who you reported, the reason,\n"
                    + "and your note. Only your own name is withheld.\n\n"
                    + "A named report is easier to act on, because it can be followed\n"
                    + "up on and weighed against the reporter's history.");
            }

            ImGui.Dummy(new Vector2(0, 6));

            if (!string.IsNullOrEmpty(reportStatus))
            {
                ImGui.PushTextWrapPos(0);
                ImGui.TextColored(TextSecondary, reportStatus);
                ImGui.PopTextWrapPos();
                ImGui.Dummy(new Vector2(0, 4));
            }

            // Checked every frame rather than latched when the dialog opened: the hourly allowance
            // can run out while this window is sitting open behind another report.
            string? blocked = ReportBlockedReason(identity);
            if (blocked != null)
            {
                ImGui.PushTextWrapPos(0);
                ImGui.TextColored(AccentYellow, blocked);
                ImGui.PopTextWrapPos();
                ImGui.Dummy(new Vector2(0, 4));
            }

            ImGui.BeginDisabled(reportSubmitting || blocked != null);
            if (DrawPrimaryButton(reportSubmitting ? "Sending..." : "Send", new Vector2(120, 28)))
                SendReport(identity);
            ImGui.EndDisabled();

            ImGui.SameLine(0, 8);
            if (DrawSecondaryButton("Cancel##ReportCancel", new Vector2(-1, 28)))
                reportTarget = null;
        }

        /// <summary>
        /// What's left after a report goes through.
        ///
        /// Everything else is gone deliberately - the reasons, the note, the Send button. Leaving
        /// a completed form on screen makes a second identical report one click away, and there is
        /// nothing left for the reporter to do here.
        /// </summary>
        private void DrawReportThanks()
        {
            ImGui.Dummy(new Vector2(0, 6));
            ImGui.TextColored(TextPrimary, "Thanks for your report.");
            ImGui.Dummy(new Vector2(0, 10));

            if (DrawSecondaryButton("Close##ReportDone", new Vector2(-1, 28)))
                reportTarget = null;
        }

        /// <summary>Why this report can't be sent right now, or null if it can. Says which limit
        /// was hit and when it lifts - "try again later" with no number reads as a bug.</summary>
        private string? ReportBlockedReason(CharacterIdentity identity)
        {
            if (Ratings == null)
                return null;

            var repeat = Ratings.ReportCooldownUntil(identity);
            if (repeat != null)
                return $"You just reported {identity.Name}. You can again in {Until(repeat.Value)}.";

            var quota = Ratings.ReportQuotaFreeAt();
            if (quota != null)
            {
                return $"That's {RatingService.ReportsPerHour} reports this hour, which is the limit. "
                     + $"The next one frees up in {Until(quota.Value)}.";
            }

            return null;
        }

        /// <summary>Rough time-until, in the largest unit that isn't misleading.</summary>
        private static string Until(DateTime utc)
        {
            var left = utc - DateTime.UtcNow;
            if (left <= TimeSpan.Zero)
                return "a moment";
            if (left < TimeSpan.FromMinutes(1))
                return "under a minute";
            if (left < TimeSpan.FromHours(1))
                return $"{(int)Math.Ceiling(left.TotalMinutes)} min";
            return $"{(int)Math.Ceiling(left.TotalHours)}h";
        }

        private void SendReport(CharacterIdentity identity)
        {
            if (Ratings == null || reportSubmitting)
                return;

            reportSubmitting = true;
            reportStatus = string.Empty;

            var reason = reportReason;
            string note = reportNote;

            // Read the toggle here, on the UI thread, rather than inside the task - the dialog can
            // be closed and reopened for someone else while this one is still in flight.
            var reporter = reportAnonymous ? null : LocalIdentity?.Invoke();

            _ = Task.Run(async () =>
            {
                try
                {
                    var result = await Ratings.SubmitReportAsync(identity, reason, note, 0, reporter)
                        .ConfigureAwait(false);

                    // Says which wall was hit. "Try again in a moment" against an hour-long rate
                    // limit just sends people back to the button to fail again.
                    reportStatus = result.Outcome switch
                    {
                        ReportOutcome.Sent => "Sent.",
                        ReportOutcome.LocalLimit => "You've hit your reporting limit for now.",
                        ReportOutcome.RateLimited => result.RetryAfter is { } wait
                            ? $"Too many reports from here. Try again in {Until(DateTime.UtcNow + wait)}."
                            : "Too many reports from here. Try again later.",
                        ReportOutcome.Offline => "Couldn't reach the server.",
                        _ => "Couldn't send that report.",
                    };

                    if (result.Ok)
                    {
                        // Kept open, showing the acknowledgement, rather than vanishing. A dialog
                        // that disappears the instant you click Send leaves you unsure it went.
                        reportSent = true;
                        reportStatus = string.Empty;
                    }
                }
                catch (Exception)
                {
                    reportStatus = "Couldn't send that report. Try again in a moment.";
                }
                finally
                {
                    reportSubmitting = false;
                }
            });
        }
    }
}
#endif
