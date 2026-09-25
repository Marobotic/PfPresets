#if PFP_RATINGS
using System;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;

namespace PfPresets
{
    /// <summary>
    /// When to read the Party Finder for the board, so it holds what is really up.
    ///
    ///   Sharing actively ON    every 10 minutes, whether or not the player is at the keyboard.
    ///   Sharing actively OFF   (the default) only once there has been no input for an hour, then
    ///                          every 10 minutes until the player is back.
    ///
    /// Never in an instance or in combat, and never over anything else - see
    /// <see cref="PfAutomation.CanFetchBoardNow"/>; a read that is due waits until it can run.
    /// Nothing at all when Party Finder sharing is off, since nothing read would be sent.
    /// </summary>
    internal sealed class PfBoardFetch
    {
        private static readonly TimeSpan Every = TimeSpan.FromMinutes(10);
        private static readonly TimeSpan IdleAfter = TimeSpan.FromHours(1);
        private static readonly TimeSpan RetryAfter = TimeSpan.FromSeconds(30);

        /// <summary>How long without any input before a read may start. It reads every page and
        /// stops the moment the player touches anything, so it waits for a quiet spell rather than
        /// starting while they are mid-click.</summary>
        private static readonly TimeSpan QuietBeforeRead = TimeSpan.FromSeconds(15);

        private readonly Configuration config;
        private readonly PfAutomation automation;
        private readonly PfBoard board;
        private readonly InputActivity activity;
        private readonly IPluginLog log;

        private DateTime? nextRead;
        private bool running;

        public PfBoardFetch(Configuration config, PfAutomation automation, PfBoard board,
            InputActivity activity, IPluginLog log)
        {
            this.config = config;
            this.automation = automation;
            this.board = board;
            this.activity = activity;
            this.log = log;
        }

        private string status = "Idle.";

        /// <summary>For the tab and /pfa coord. Logged whenever it changes, so a read that keeps
        /// waiting says on what.</summary>
        public string Status
        {
            get => status;
            private set
            {
                if (value == status)
                    return;
                status = value;
                log.Information($"[PF Board] Reads: {value}");
            }
        }

        /// <summary>The toggle was just turned on: read at the next good moment.</summary>
        public void ReadSoon() => nextRead = DateTime.UtcNow;

        /// <summary>True while a read of the Party Finder is running.</summary>
        public bool Reading => running;

        /// <summary>A refresh press reads the game at most this often; between, it fetches.</summary>
        private static readonly TimeSpan ManualReadEvery = TimeSpan.FromMinutes(5);

        /// <summary>Another player's full read this recent is as good as our own: fetch it instead.</summary>
        private static readonly TimeSpan OthersReadCounts = TimeSpan.FromMinutes(1);

        private DateTime lastManualRead = DateTime.MinValue;

        /// <summary>
        /// Whether a refresh press on this data centre would read the game, and if not, why - the
        /// press then fetches the board instead, and the button stays enabled either way.
        /// </summary>
        public bool WouldRead(string dc, out string why)
        {
            if (running)
            {
                why = "a read is already running";
                return false;
            }
            var wait = lastManualRead + ManualReadEvery - DateTime.UtcNow;
            if (wait > TimeSpan.Zero)
            {
                why = $"read from the game {Ago(DateTime.UtcNow - lastManualRead)}; next read in {Ago(wait)}";
                return false;
            }
            if (board.SinceFullRead(dc) is { } since && since < OthersReadCounts)
            {
                why = $"another player read {dc} in full {Ago(since)} ago";
                return false;
            }
            return automation.CanFetchBoardNow(out why);
        }

        private static string Ago(TimeSpan t)
            => t.TotalSeconds < 60 ? $"{Math.Max(1, (int)t.TotalSeconds)}s" : $"{(int)t.TotalMinutes}m {t.Seconds:D2}s";

        /// <summary>
        /// The board's refresh button, on the player's own data centre: read the Party Finder now -
        /// every page of the All tab, the window kept out of sight - and make the board exactly what
        /// it shows (see PfBoard.ApplyExactRead). Refused, with why, while in a duty, in combat or
        /// otherwise busy; the button then falls back to fetching the board. Pressed on purpose, so
        /// the player's own input does not cut it short the way it does an unattended read.
        /// </summary>
        public bool ReadNow(out string why)
        {
            if (running)
            {
                why = "A read is already running.";
                return false;
            }
            if (!automation.CanFetchBoardNow(out why))
                return false;

            running = true;
            lastManualRead = DateTime.UtcNow;
            Status = "Reading the Party Finder (refresh)...";
            _ = Task.Run(async () =>
            {
                try
                {
                    board.BeginRead();
                    var (pages, note) = await automation.FetchPartyFinderAsync(() => board.ReceivedCount,
                        playerBack: () => false,
                        seenSoFar: () => board.ReadSeenCount,
                        detailWanted: board.AlliancesNeedingDetail,
                        onDetail: board.TakeDetail).ConfigureAwait(false);
                    board.EndRead(automation.LastReadTotal, automation.LastReadComplete);
                    Status = pages > 0
                        ? $"Refreshed {DateTime.Now:HH:mm}: {pages} page(s), {note}."
                        : $"Refresh attempt {DateTime.Now:HH:mm}: {note}.";
                    board.Refresh();
                }
                catch (Exception ex)
                {
                    log.Warning(ex, "[PF Board] Refresh read failed.");
                    board.CancelRead();
                }
                finally
                {
                    running = false;
                }
            });
            return true;
        }

        // ── The player's own browsing, as a read ──────────────────
        //
        // Having the Party Finder open is the freshest look at the data centre there is. From the
        // moment the player opens it to the moment they close it, every listing it hands over is
        // noted; on close, the board is told. Paged to the end, whatever was not seen is gone. Not,
        // the board is still trimmed to the game's own count. Only on the Data Centre tab with
        // every category showing - a filtered list's count is not the data centre's.
        private bool browsing;
        private bool browseWhole;
        private int browseEnd;
        private int browseTotal;

        private void TickBrowse()
        {
            var (open, whole, end, total) = automation.ManualBrowse();

            if (open && !browsing)
            {
                browsing = true;
                browseWhole = true;
                browseEnd = 0;
                browseTotal = 0;
                board.BeginRead();
            }

            if (!browsing)
                return;

            if (open)
            {
                browseWhole &= whole;
                browseEnd = Math.Max(browseEnd, end);
                if (total > 0)
                    browseTotal = total;
                return;
            }

            browsing = false;
            if (browseWhole && browseTotal > 0)
            {
                bool complete = browseEnd >= browseTotal;
                log.Information($"[PF Board] Your Party Finder browse: {(complete ? "every page" : $"up to {browseEnd}")} "
                    + $"of {browseTotal}; tidying the board to match.");
                board.EndRead(browseTotal, complete);
            }
            else
            {
                board.CancelRead();
            }
        }

        public void Tick()
        {
            TickBrowse();

            if (!config.CommunityEnabled || !board.Uploading)
            {
                nextRead = null;
                Status = "Off: Party Finder sharing is off.";
                return;
            }

            bool active = config.PfActiveShareEnabled;
            bool idle = activity.IdleFor >= IdleAfter;
            if (!active && !idle)
            {
                nextRead = null;
                Status = "Waiting for an hour without input.";
                return;
            }

            var now = DateTime.UtcNow;
            nextRead ??= now;
            if (running || now < nextRead)
                return;

            // SOMEBODY ALREADY DID. A data centre read in full a few minutes ago - by anybody - is on
            // the board already; reading it again only uploads the same listings and drives the
            // game's window for nothing. With many readers on one data centre this is what keeps it
            // to about one read every few minutes rather than one per reader.
            string ownDc = board.OwnDataCentre();
            if (ownDc.Length > 0 && board.SinceFullRead(ownDc) is { } since && since < Every / 2)
            {
                nextRead = now + (Every / 2 - since);
                Status = $"Skipped: {ownDc} was read in full {(int)since.TotalSeconds}s ago.";
                return;
            }

            if (!automation.CanFetchBoardNow(out string why))
            {
                nextRead = now + RetryAfter;
                Status = $"Due, waiting: {why}.";
                return;
            }

            // A quiet spell first. Checked every tick rather than retried later, so the read goes
            // the moment one comes.
            if (activity.IdleFor < QuietBeforeRead)
            {
                Status = "Due, waiting for a quiet moment.";
                return;
            }

            nextRead = now + Every;
            running = true;
            Status = "Reading the Party Finder...";

            _ = Task.Run(async () =>
            {
                board.BeginRead();
                var startedAt = DateTime.UtcNow;
                var (pages, note) = await automation.FetchPartyFinderAsync(() => board.ReceivedCount,
                    playerBack: () => activity.LastInput > startedAt,
                    seenSoFar: () => board.ReadSeenCount,
                        detailWanted: board.AlliancesNeedingDetail,
                        onDetail: board.TakeDetail).ConfigureAwait(false);
                board.EndRead(automation.LastReadTotal, automation.LastReadComplete);
                Status = pages > 0
                    ? $"Last read {DateTime.Now:HH:mm}: {pages} page(s), {note}."
                    : $"Last read attempt {DateTime.Now:HH:mm}: {note}.";
                running = false;
            });
        }
    }
}
#endif
