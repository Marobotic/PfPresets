#if PFP_RATINGS
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace PfPresets
{
    /// <summary>
    /// Clears as they happen, pushed by the server down one held connection.
    ///
    /// WHY. The announcer used to learn about a clear by reading the feed on a two-minute grid, so a
    /// clear reached people anywhere up to two minutes after it happened, and every client asked
    /// every two minutes whether anything had. With the stream open, the server writes each new
    /// clear to every connected client at once: everybody hears in the same second, and a quiet
    /// evening costs nothing but the server's ping every twenty-five seconds.
    ///
    /// THE POLL IS STILL THE FALLBACK. While the stream is down - an old server, a proxy that will
    /// not hold connections, a cap reached - the two-minute read carries on exactly as before, so
    /// the worst case is the old behaviour, never silence.
    ///
    /// NO STAMPEDE ON A RESTART. When the server restarts, every client loses its connection at the
    /// same instant. Coming straight back would be every client in the plugin reconnecting, and
    /// then reading the feed to catch up, in the same second. So a reconnect waits a random two to
    /// thirty seconds, longer after repeated failures, and the catch-up read rides on the reconnect,
    /// spread by that same randomness.
    /// </summary>
    internal sealed partial class RatingService
    {
        /// <summary>The server pings every twenty-five seconds; this much silence means the
        /// connection has died without anybody saying so, and it is dropped and reopened.</summary>
        private static readonly TimeSpan ClearStreamIdleLimit = TimeSpan.FromSeconds(70);

        private static readonly TimeSpan ClearStreamMaxWait = TimeSpan.FromMinutes(10);

        private static readonly JsonSerializerSettings ClearStreamJson = new()
        {
            TypeNameHandling = TypeNameHandling.None,
            MetadataPropertyHandling = MetadataPropertyHandling.Ignore,
            MaxDepth = 16,
        };

        /// <summary>True while the stream is connected and the poll can stand down.</summary>
        private volatile bool clearStreamLive;

        private CancellationTokenSource? clearStreamStop;

        /// <summary>
        /// Keeps the stream running while the announcer wants it, and stops it when not. Called from
        /// the announce tick every frame; does nothing on the frames where nothing has changed.
        /// </summary>
        private void KeepClearStream(bool wanted)
        {
            if (wanted == (clearStreamStop != null))
                return;

            if (!wanted)
            {
                clearStreamStop!.Cancel();
                clearStreamStop.Dispose();
                clearStreamStop = null;
                clearStreamLive = false;
                return;
            }

            clearStreamStop = CancellationTokenSource.CreateLinkedTokenSource(cancel.Token);
            var stop = clearStreamStop.Token;
            _ = Task.Run(() => RunClearStreamAsync(stop));
        }

        private async Task RunClearStreamAsync(CancellationToken stop)
        {
            int failures = 0;

            while (!stop.IsCancellationRequested)
            {
                TimeSpan? serverSaid = null;
                try
                {
                    var (response, retryAfter) = await api.OpenClearStreamAsync(stop).ConfigureAwait(false);
                    serverSaid = retryAfter;

                    if (response != null)
                    {
                        using (response)
                        {
                            failures = 0;
                            clearStreamLive = true;
                            log.Debug("[Ratings] Clear stream connected");

                            // Whatever landed while this client was not connected. The stream only
                            // carries clears from here on.
                            RefreshForAnnounce();

                            await ReadClearStreamAsync(response, stop).ConfigureAwait(false);
                        }
                    }
                }
                catch (Exception ex) when (!stop.IsCancellationRequested)
                {
                    log.Debug($"[Ratings] Clear stream dropped: {ex.Message}");
                }
                finally
                {
                    clearStreamLive = false;
                }

                if (stop.IsCancellationRequested)
                    return;

                failures = Math.Min(failures + 1, 6);

                // A random point in a window that doubles with each failure in a row: two to thirty
                // seconds after a drop, up to ten minutes against a server that keeps refusing.
                double window = Math.Min(30_000d * (1 << (failures - 1)), ClearStreamMaxWait.TotalMilliseconds);
                var wait = TimeSpan.FromMilliseconds(2_000 + Random.Shared.NextDouble() * (window - 2_000));
                if (serverSaid is { } said && said > wait)
                    wait = said < ClearStreamMaxWait ? said : ClearStreamMaxWait;

                try
                {
                    await Task.Delay(wait, stop).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }

        /// <summary>
        /// Reads server-sent events until the connection ends, handing each clear to the announcer.
        /// Only the two fields this server sends are understood; anything else is skipped.
        /// </summary>
        private async Task ReadClearStreamAsync(System.Net.Http.HttpResponseMessage response, CancellationToken stop)
        {
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(stop);
            await using var body = await response.Content.ReadAsStreamAsync(stop).ConfigureAwait(false);
            using var reader = new StreamReader(body);

            string evt = string.Empty;
            var data = new System.Text.StringBuilder();

            while (true)
            {
                idle.CancelAfter(ClearStreamIdleLimit);

                string? line;
                try
                {
                    line = await reader.ReadLineAsync(idle.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!stop.IsCancellationRequested)
                {
                    log.Debug("[Ratings] Clear stream went quiet; reconnecting");
                    return;
                }

                if (line == null)
                    return;

                if (line.Length == 0)
                {
                    if (evt == "clear" && data.Length > 0)
                        OnClearPushed(data.ToString());
                    evt = string.Empty;
                    data.Clear();
                    continue;
                }

                if (line.StartsWith(':'))
                    continue;   // the server's ping

                if (line.StartsWith("event:", StringComparison.Ordinal))
                    evt = line[6..].Trim();
                else if (line.StartsWith("data:", StringComparison.Ordinal))
                {
                    if (data.Length > 0)
                        data.Append('\n');
                    data.Append(line.AsSpan(5).TrimStart());

                    // A line the server would never send; not worth holding in memory.
                    if (data.Length > 16_384)
                        data.Clear();
                }
            }
        }

        private void OnClearPushed(string json)
        {
            try
            {
                var push = JsonConvert.DeserializeObject<ClearPush>(json, ClearStreamJson);
                if (push?.Post == null)
                    return;

                ObserveForAnnounce(new[] { push.Post }, push.Now, pushed: true);
            }
            catch (Exception ex)
            {
                log.Debug($"[Ratings] Clear push unreadable: {ex.Message}");
            }
        }

        private sealed class ClearPush
        {
            [JsonProperty("post")]
            public AchievementPost? Post { get; set; }

            [JsonProperty("now")]
            public long Now { get; set; }
        }
    }
}
#endif
