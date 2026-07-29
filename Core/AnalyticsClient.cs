using System;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using Newtonsoft.Json;

namespace PfPresets
{
    /// <summary>
    /// Sends anonymous usage counts so the plugin's reach can be measured.
    ///
    /// What leaves this machine, in full:
    ///   - a random install id generated locally (see Configuration.AnalyticsInstallId)
    ///   - the plugin version
    ///   - how many presets have been created and applied on this install, ever
    ///
    /// That is the entire payload. No character name, account id, world, duty, preset contents or
    /// anything typed into a preset is collected, and the server discards IP addresses.
    ///
    /// It is built to be irrelevant when it fails: every call is fire-and-forget on a background
    /// timer with a short timeout, all errors are swallowed after the first, and nothing in the
    /// plugin ever waits on it. If the server is down or the user is offline, the plugin behaves
    /// exactly as if analytics didn't exist.
    /// </summary>
    internal sealed class AnalyticsClient : IDisposable
    {
        private const string Endpoint = "https://analytics.marobotic.dev/api/ping";

        /// <summary>Wait before the first ping so plugin load is never held up by the network.</summary>
        private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(30);

        /// <summary>The server treats an install as online for 15 minutes, so pinging every 5 gives
        /// two missed beats of slack before it drops off the live count.</summary>
        private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

        private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

        private readonly Configuration config;
        private readonly IPluginLog log;
        private readonly string version;
        private readonly HttpClient http;
        private readonly CancellationTokenSource cancel = new();

        /// <summary>Set after the first failure so a dead endpoint doesn't fill the log every cycle.</summary>
        private bool loggedFailure;

        public AnalyticsClient(Configuration config, IPluginLog log, string version)
        {
            this.config = config;
            this.log = log;
            this.version = version;

            http = new HttpClient { Timeout = RequestTimeout };
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"PfPresets/{version}");

            _ = Task.Run(LoopAsync);
        }

        private async Task LoopAsync()
        {
            try
            {
                await Task.Delay(StartupDelay, cancel.Token).ConfigureAwait(false);

                while (!cancel.IsCancellationRequested)
                {
                    await SendAsync().ConfigureAwait(false);
                    await Task.Delay(Interval, cancel.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // Plugin unloading - expected.
            }
            catch (Exception ex)
            {
                log.Debug($"[Analytics] Loop ended: {ex.Message}");
            }
        }

        private async Task SendAsync()
        {
            // Checked every cycle rather than once, so turning it off in settings takes effect
            // immediately without a reload.
            if (!config.AnalyticsEnabled)
                return;

            try
            {
                string installId = EnsureInstallId();

                string body = JsonConvert.SerializeObject(new
                {
                    installId,
                    version,
                    presetsSaved = Math.Max(config.LifetimePresetsCreated, config.Presets.Count),
                    presetsApplied = config.LifetimePresetsApplied,
                });

                using var content = new StringContent(body, Encoding.UTF8, "application/json");
                using var response = await http.PostAsync(Endpoint, content, cancel.Token).ConfigureAwait(false);

                if (response.IsSuccessStatusCode)
                {
                    loggedFailure = false;
                    return;
                }

                if (!loggedFailure)
                {
                    loggedFailure = true;
                    log.Debug($"[Analytics] Ping rejected: {(int)response.StatusCode}.");
                }
            }
            catch (OperationCanceledException)
            {
                // Unloading, or the request timed out. Neither is worth reporting.
            }
            catch (Exception ex)
            {
                // Offline, DNS down, server gone - all fine, and all silent after the first note.
                if (!loggedFailure)
                {
                    loggedFailure = true;
                    log.Debug($"[Analytics] Ping failed (further failures won't be logged): {ex.Message}");
                }
            }
        }

        /// <summary>Returns this install's id, creating and saving one the first time.</summary>
        private string EnsureInstallId()
        {
            if (!string.IsNullOrWhiteSpace(config.AnalyticsInstallId))
                return config.AnalyticsInstallId;

            config.AnalyticsInstallId = Guid.NewGuid().ToString("N");
            config.Save();
            return config.AnalyticsInstallId;
        }

        /// <summary>Forgets this install's id, so it is counted as a brand new one from now on.
        /// Offered in settings for anyone who wants to reset what they've contributed.</summary>
        public void ResetInstallId()
        {
            config.AnalyticsInstallId = string.Empty;
            config.Save();
        }

        public void Dispose()
        {
            try
            {
                cancel.Cancel();
                cancel.Dispose();
                http.Dispose();
            }
            catch (Exception)
            {
                // Nothing useful to do while unloading.
            }
        }
    }
}
