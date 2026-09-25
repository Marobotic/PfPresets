#if PFP_RATINGS
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Newtonsoft.Json;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.X509;

namespace PfPresets
{
    /// <summary>
    /// Client half of the moderator panel: key enrolment, session handshake and the calls the
    /// panel makes once a session is open.
    ///
    /// <para>
    /// NOTE: this type ships in every ordinary build. The partial hooks in
    /// <c>UI/PluginUI.AdminHooks.cs</c> are erased for non-moderator builds, but this class, its
    /// endpoints and the screen models it drives are compiled into the released assembly. A build
    /// without the moderator files cannot <em>reach</em> this from the UI; it still carries it.
    /// </para>
    /// </summary>
    internal sealed class PanelAccess : IDisposable
    {
        private sealed class StoredKey
        {
            public string Label { get; set; } = string.Empty;

            public string K { get; set; } = string.Empty;
        }

        private sealed class RegisterResponse
        {
            public bool Ok { get; set; }

            public string Label { get; set; } = string.Empty;
        }

        private sealed class ChallengeResponse
        {
            public string Nonce { get; set; } = string.Empty;
        }

        private sealed class SessionResponse
        {
            public string? Token { get; set; }

            public int ExpiresInSec { get; set; }
        }

        private sealed class OkResponse
        {
            public bool Ok { get; set; }
        }

        private sealed class BanResponse
        {
            public bool Ok { get; set; }

            public bool Partial { get; set; }
        }



        private readonly IDalamudPluginInterface pluginInterface;

        private readonly IPluginLog log;

        private readonly HttpClient http;

        private Ed25519PrivateKeyParameters? key;

        private string label = string.Empty;

        private string? sessionToken;

        private DateTime sessionExpires = DateTime.MinValue;

        public bool HasKey => key != null;

        public string Label => label;

        public bool Enabled { get; private set; } = true;

        /// <summary>
        /// Whether this machine is showing presets for the categories the plugin cannot post yet.
        ///
        /// Stored here rather than in Configuration because it belongs to the machine that holds a
        /// key, not to the player: the state file it lives in sits beside the key itself and means
        /// nothing without one. Applying it is not this class's job either - see
        /// DutyComposition.OfferUnsupported for why the only code that reads this across is in the
        /// moderator files.
        /// </summary>
        public bool DevPresets { get; private set; }

        private string KeyPath => Path.Combine(pluginInterface.ConfigDirectory.FullName, "pfa.dat");

        private string StatePath => Path.Combine(pluginInterface.ConfigDirectory.FullName, "pfa.ui");

        public PanelAccess(IDalamudPluginInterface pluginInterface, Configuration config, IPluginLog log, string version)
        {
            this.pluginInterface = pluginInterface;
            this.log = log;
            string baseUrl = (string.IsNullOrWhiteSpace(config.RatingApiBaseUrl) ? "https://api.marobotic.dev/pfp/v2/" : (config.RatingApiBaseUrl.TrimEnd('/') + "/"));

            // THE CONNECTION IS SET UP ONCE AND KEPT, which is most of why the console used to sit
            // on "Loading..." for several seconds. A default HttpClient opens a fresh socket per
            // burst of traffic, and opening one to this API costs a DNS lookup, a TCP handshake and
            // a TLS handshake before a single byte of the answer moves - paid again on the next
            // screen, and the one after that. A pooled connection with a long life pays it once.
            //
            // HTTP/2 matters for the same reason: the handshake and the session both cross this
            // socket, and on HTTP/1.1 they queue behind each other rather than sharing it. The
            // policy falls back on its own if the server or a proxy in the way will not speak it.
            var handler = new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.All,
                PooledConnectionLifetime = TimeSpan.FromMinutes(10L),
                PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5L),
                ConnectTimeout = TimeSpan.FromSeconds(10L),
                EnableMultipleHttp2Connections = true,
            };

            http = new HttpClient(handler)
            {
                BaseAddress = new Uri(baseUrl),
                Timeout = TimeSpan.FromSeconds(20L),
                DefaultRequestVersion = HttpVersion.Version20,
                DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
            };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("PfPresets/" + version);
            http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
            http.DefaultRequestHeaders.ExpectContinue = false;
            Load();
        }

        // ── Warm-up ───────────────────────────────────────────────

        /// <summary>
        /// A screen that has already been fetched, and when.
        ///
        /// Only ever screens asked for with no controls set - the state a tab is in when you arrive
        /// at it. A filtered screen belongs to the filter that produced it and would be wrong to
        /// hand back to somebody who has not set that filter.
        /// </summary>
        private readonly ConcurrentDictionary<string, (ScreenResponse Screen, DateTime At)> screenCache = new();

        private int warmStarted;

        /// <summary>
        /// Signs in and fetches the first screen in the background, at load, so opening the console
        /// is a cache read rather than three round trips.
        ///
        /// THIS IS THE FIX FOR THE WAIT, and the wait was never one slow request. Opening the tab
        /// ran the whole protocol from cold: a challenge, a session, and only then the screen -
        /// three sequential trips to the API, each one waiting on the last, with the connection
        /// itself being built during the first. Nothing about that is parallelisable at the moment
        /// somebody clicks, because each step needs the answer to the one before it.
        ///
        /// So it is not done at that moment. It is done at load, when nobody is waiting, and by the
        /// time the tab is opened there is a live connection, a valid session and a screen already
        /// in hand. Idempotent, and safe to call from anywhere: the interlock means only the first
        /// caller does the work.
        /// </summary>
        public void BeginWarm()
        {
            if (key == null || !Enabled)
            {
                return;
            }

            // One at a time rather than once ever: a warm-up that failed because the machine was
            // offline at load should be able to run again when the console is next reached for.
            if (Interlocked.CompareExchange(ref warmStarted, 1, 0) != 0)
            {
                return;
            }

            _ = Task.Run(async delegate
            {
                try
                {
                    if (!(await EnsureSessionAsync().ConfigureAwait(false)))
                    {
                        return;
                    }

                    // The default screen, cached by the call itself. Asked for by the same empty
                    // id the console uses on its first draw, not by null - the cache is keyed on
                    // what was asked for, and warming a key nobody reads back warms nothing.
                    await ScreenAsync(string.Empty, null).ConfigureAwait(false);

                    // And a challenge for whatever the first action turns out to be.
                    PrimeNonce();
                }
                catch (Exception ex)
                {
                    log.Debug("[Panel] Warm-up did not complete: " + ex.Message);
                }
                finally
                {
                    Interlocked.Exchange(ref warmStarted, 0);
                }
            });
        }

        /// <summary>A screen fetched earlier, if there is one for this id. The caller shows it at
        /// once and refreshes underneath, rather than blanking for a round trip.</summary>
        public bool TryCachedScreen(string? id, out ScreenResponse screen, out DateTime at)
        {
            if (screenCache.TryGetValue(id ?? string.Empty, out var hit))
            {
                screen = hit.Screen;
                at = hit.At;
                return true;
            }
            screen = null!;
            at = DateTime.MinValue;
            return false;
        }

        public void SetEnabled(bool on)
        {
            Enabled = on;
            SaveState();

            // Switched back on mid-session, there was nothing to warm at load. Warming now means
            // the console tab it just put back is as quick as it would have been.
            if (on)
            {
                BeginWarm();
            }
        }

        public void SetDevPresets(bool on)
        {
            DevPresets = on;
            SaveState();
        }

        /// <summary>
        /// Writes the whole state file, every time.
        ///
        /// Every flag, not just the one that changed: this file is read back as a flat map and
        /// written by serialising an object literal, so a setter that names only its own field
        /// silently drops the others. It went in as one flag and did not stay that way.
        /// </summary>
        private void SaveState()
        {
            try
            {
                File.WriteAllText(StatePath, JsonConvert.SerializeObject(new
                {
                    enabled = Enabled,
                    devPresets = DevPresets
                }));
            }
            catch (Exception ex)
            {
                log.Debug("[Panel] Could not save state: " + ex.Message);
            }
        }

        private void Load()
        {
            try
            {
                if (File.Exists(StatePath))
                {
                    Dictionary<string, bool>? st = JsonConvert.DeserializeObject<Dictionary<string, bool>>(File.ReadAllText(StatePath));
                    if (st != null && st.TryGetValue("enabled", out var on))
                    {
                        Enabled = on;
                    }
                    if (st != null && st.TryGetValue("devPresets", out var dev))
                    {
                        DevPresets = dev;
                    }
                }
                if (File.Exists(KeyPath))
                {
                    StoredKey? stored = JsonConvert.DeserializeObject<StoredKey>(File.ReadAllText(KeyPath));
                    if (stored != null && !string.IsNullOrEmpty(stored.K))
                    {
                        key = new Ed25519PrivateKeyParameters(Convert.FromBase64String(stored.K), 0);
                        label = stored.Label ?? string.Empty;
                        log.Information("[Panel] Key loaded (" + label + ").");
                    }
                }
            }
            catch (Exception ex)
            {
                log.Warning("[Panel] Key could not be loaded: " + ex.Message);
            }
        }

        public async Task<string> RegisterAsync(string token, string character)
        {
            try
            {
                Ed25519KeyPairGenerator ed25519KeyPairGenerator = new Ed25519KeyPairGenerator();
                ed25519KeyPairGenerator.Init(new Ed25519KeyGenerationParameters(new SecureRandom()));
                AsymmetricCipherKeyPair pair = ed25519KeyPairGenerator.GenerateKeyPair();
                Ed25519PrivateKeyParameters fresh = (Ed25519PrivateKeyParameters)pair.Private;
                string spki = Convert.ToBase64String(SubjectPublicKeyInfoFactory.CreateSubjectPublicKeyInfo((Ed25519PublicKeyParameters)pair.Public).GetDerEncoded());
                RegisterResponse? result = await PostAsync<RegisterResponse>("panels/join", new
                {
                    token = token.Trim(),
                    publicKey = spki,
                    character = character
                }).ConfigureAwait(false);
                if (result == null || !result.Ok)
                {
                    return "The server refused that code. It may already have been used.";
                }
                key = fresh;
                label = result.Label;
                File.WriteAllText(KeyPath, JsonConvert.SerializeObject(new StoredKey
                {
                    Label = label,
                    K = Convert.ToBase64String(fresh.GetEncoded())
                }));
                TryRestrictPermissions(KeyPath);
                log.Information("[Panel] Registered as " + label + ".");

                // Enrolled just now, so the load-time warm-up found no key and did nothing. The
                // console tab appears the moment this returns; give it something to draw.
                BeginWarm();
                return string.Empty;
            }
            catch (CryptographicException ex)
            {
                log.Error("[Panel] Key generation failed: " + ex.Message);
                return "Couldn't create a key on this machine.";
            }
            catch (IOException ex2)
            {
                log.Error("[Panel] Key could not be saved: " + ex2.Message);
                return "Registered, but the key could not be saved - check the config folder.";
            }
            catch (Exception ex3)
            {
                log.Warning("[Panel] Registration failed: " + ex3.GetType().Name + ": " + ex3.Message);
                return "Couldn't reach the server (" + ex3.GetType().Name + ").";
            }
        }

        private void TryRestrictPermissions(string path)
        {
            try
            {
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(path, UnixFileMode.UserWrite | UnixFileMode.UserRead);
                }
            }
            catch (Exception ex)
            {
                log.Debug("[Panel] Could not tighten permissions: " + ex.Message);
            }
        }

        private string Sign(string canonical)
        {
            byte[] data = Encoding.UTF8.GetBytes(canonical);
            Ed25519Signer ed25519Signer = new Ed25519Signer();
            ed25519Signer.Init(forSigning: true, key);
            ed25519Signer.BlockUpdate(data, 0, data.Length);
            return Convert.ToBase64String(ed25519Signer.GenerateSignature());
        }

        private async Task<string?> ChallengeAsync()
        {
            return (await PostAsync<ChallengeResponse>("panels/hello", new { label }).ConfigureAwait(false))?.Nonce;
        }

        // ── Challenges, fetched before they are needed ────────────

        /// <summary>
        /// A challenge held ready for the next action, and the moment it arrived.
        ///
        /// Every action costs two round trips - fetch a nonce, then post the signed action - and
        /// the first of them is pure latency with nothing depending on its contents. So one is
        /// fetched in the background after each use, and the next action spends only the trip that
        /// actually does something.
        ///
        /// A held challenge is not trusted past <see cref="NonceGoodFor"/>, and never trusted
        /// blindly: <see cref="DoAsync"/> retries once with a fresh one if the server refuses,
        /// because a nonce the server has forgotten and a genuinely refused action look the same
        /// from here and only one of them is worth reporting.
        /// </summary>
        private string? spareNonce;

        private DateTime spareNonceAt = DateTime.MinValue;

        private static readonly TimeSpan NonceGoodFor = TimeSpan.FromSeconds(45L);

        private void PrimeNonce()
        {
            if (key == null)
            {
                return;
            }
            _ = Task.Run(async delegate
            {
                try
                {
                    string? fresh = await ChallengeAsync().ConfigureAwait(false);
                    if (fresh != null)
                    {
                        spareNonceAt = DateTime.UtcNow;
                        spareNonce = fresh;
                    }
                }
                catch (Exception ex)
                {
                    log.Debug("[Panel] Could not pre-fetch a challenge: " + ex.Message);
                }
            });
        }

        /// <summary>The held challenge if there is a fresh one, otherwise a new one off the wire.
        /// Either way it is consumed - a nonce is used once.</summary>
        private async Task<(string? Nonce, bool WasSpare)> TakeNonceAsync()
        {
            string? held = Interlocked.Exchange(ref spareNonce, null);
            if (held != null && DateTime.UtcNow - spareNonceAt < NonceGoodFor)
            {
                return (held, true);
            }
            return (await ChallengeAsync().ConfigureAwait(false), false);
        }

        private async Task<bool> EnsureSessionAsync()
        {
            if (key == null)
            {
                return false;
            }
            if (sessionToken != null && DateTime.UtcNow < sessionExpires)
            {
                return true;
            }
            string? nonce = await ChallengeAsync().ConfigureAwait(false);
            if (nonce == null)
            {
                return false;
            }
            SessionResponse? res = await PostAsync<SessionResponse>("panels/open", new
            {
                label = label,
                nonce = nonce,
                signature = Sign(label + "|" + nonce + "|session")
            }).ConfigureAwait(false);
            if (res == null || res.Token == null)
            {
                return false;
            }
            sessionToken = res.Token;
            sessionExpires = DateTime.UtcNow.AddSeconds(Math.Max(60, res.ExpiresInSec - 60));
            return true;
        }

        public async Task<SubjectActions?> OnAsync(string name, string world)
        {
            if (!(await EnsureSessionAsync().ConfigureAwait(false)))
            {
                return null;
            }
            return await PostAsync<SubjectActions>("panels/on", new { name, world }, sessionToken).ConfigureAwait(false);
        }

        public async Task<ScreenResponse?> ScreenAsync(string? id, Dictionary<string, object>? controls)
        {
            if (!(await EnsureSessionAsync().ConfigureAwait(false)))
            {
                return null;
            }
            ScreenResponse? screen = await PostAsync<ScreenResponse>("panels", new
            {
                screen = id,
                controls = controls
            }, sessionToken).ConfigureAwait(false);

            // Kept only when nothing was filtering it - see the note on screenCache. This is what
            // the warm-up fills and what a second visit to a tab is drawn from.
            if (screen != null && (controls == null || controls.Count == 0))
            {
                screenCache[id ?? string.Empty] = (screen, DateTime.UtcNow);
            }

            return screen;
        }

        public async Task<string> DoAsync(string token, Dictionary<string, object>? inputs = null)
        {
            if (!(await EnsureSessionAsync().ConfigureAwait(false)))
            {
                return "Couldn't sign in.";
            }

            var (nonce, wasSpare) = await TakeNonceAsync().ConfigureAwait(false);
            if (nonce == null)
            {
                return "Couldn't get a challenge.";
            }

            ScreenActionResponse? res = await ActAsync(token, inputs, nonce).ConfigureAwait(false);

            // A HELD CHALLENGE GETS ONE SECOND CHANCE, AND ONLY A HELD ONE. It may have gone stale
            // between being fetched and being wanted, and a stale nonce is refused in exactly the
            // same words as an action the server genuinely will not do - so the difference has to
            // be settled by asking again rather than by reading the answer. A challenge fetched a
            // moment ago is not retried, because for that one a refusal means what it says.
            if ((res == null || !res.Ok) && wasSpare)
            {
                string? fresh = await ChallengeAsync().ConfigureAwait(false);
                if (fresh != null)
                {
                    res = await ActAsync(token, inputs, fresh).ConfigureAwait(false);
                }
            }

            // Whatever happened, the next action should not have to wait for a challenge either.
            PrimeNonce();

            if (res == null || !res.Ok)
            {
                return "The server refused it.";
            }
            return res.Note ?? string.Empty;
        }

        private Task<ScreenActionResponse?> ActAsync(string token, Dictionary<string, object>? inputs, string nonce)
        {
            return PostAsync<ScreenActionResponse>("panels/act", new
            {
                token = token,
                inputs = inputs,
                nonce = nonce,
                signature = Sign($"{label}|{nonce}|{token}")
            }, sessionToken);
        }

        public async Task<(string Name, string World)?> ReadCharacterFromLinkAsync(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return null;
            }
            Uri uri;
            try
            {
                uri = new Uri(url.Trim());
            }
            catch (UriFormatException)
            {
                return null;
            }
            string host = uri.Host.ToLowerInvariant();
            string[] parts = uri.AbsolutePath.Trim('/').Split('/');
            if (host.EndsWith("fflogs.com", StringComparison.Ordinal) && parts.Length >= 4 && parts[0] == "character")
            {
                return (TitleCaseName(Uri.UnescapeDataString(parts[3])), TitleCaseName(Uri.UnescapeDataString(parts[2])));
            }
            if (host.EndsWith("tomestone.gg", StringComparison.Ordinal) && parts.Length >= 3)
            {
                if (parts[0] == "character-name")
                {
                    return (TitleCaseName(Uri.UnescapeDataString(parts[2])), TitleCaseName(Uri.UnescapeDataString(parts[1])));
                }
                if (parts[0] == "character")
                {
                    return (TitleCaseName(Uri.UnescapeDataString(parts[2])), string.Empty);
                }
            }
            if (host.EndsWith("finalfantasyxiv.com", StringComparison.Ordinal))
            {
                return await ReadLodestoneAsync(uri).ConfigureAwait(false);
            }
            return null;
        }

        private async Task<(string Name, string World)?> ReadLodestoneAsync(Uri uri)
        {
            try
            {
                using HttpClient http = new HttpClient
                {
                    Timeout = TimeSpan.FromSeconds(12L)
                };
                http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; PfAnalysis)");
                string haystack = await http.GetStringAsync(uri).ConfigureAwait(false);
                string? name = Between(haystack, "frame__chara__name\">", "<");
                string? world = Between(haystack, "frame__chara__world\">", "<");
                if (world != null)
                {
                    int bracket = world.IndexOf('[');
                    if (bracket > 0)
                    {
                        world = world.Substring(0, bracket);
                    }
                    int gt = world.LastIndexOf('>');
                    if (gt >= 0 && gt < world.Length - 1)
                    {
                        string text = world;
                        int num = gt + 1;
                        world = text.Substring(num, text.Length - num);
                    }
                }
                if (string.IsNullOrWhiteSpace(name))
                {
                    return null;
                }
                return (name.Trim(), (world ?? string.Empty).Trim());
            }
            catch (Exception ex)
            {
                log.Debug("[Panel] Couldn't read that Lodestone page: " + ex.Message);
                return null;
            }
        }

        private static string? Between(string haystack, string open, string close)
        {
            int a = haystack.IndexOf(open, StringComparison.Ordinal);
            if (a < 0)
            {
                return null;
            }
            a += open.Length;
            int b = haystack.IndexOf(close, a, StringComparison.Ordinal);
            if (b >= 0)
            {
                int num = a;
                return haystack.Substring(num, b - num);
            }
            return null;
        }

        private static string TitleCaseName(string raw)
        {
            string trimmed = raw.Replace('+', ' ').Trim();
            if (trimmed.Length == 0)
            {
                return trimmed;
            }
            StringBuilder sb = new StringBuilder(trimmed.Length);
            bool startOfWord = true;
            string text = trimmed;
            foreach (char c in text)
            {
                sb.Append(startOfWord ? char.ToUpperInvariant(c) : c);
                startOfWord = c == ' ' || c == '-' || c == '\'';
            }
            return sb.ToString();
        }

        private async Task<T?> PostAsync<T>(string path, object body, string? bearer = null) where T : class
        {
            using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, path)
            {
                Content = new StringContent(JsonConvert.SerializeObject(body), Encoding.UTF8, "application/json")
            };
            if (bearer != null)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            }
            using HttpResponseMessage response = await http.SendAsync(request).ConfigureAwait(false);
            string text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    sessionToken = null;
                }
                log.Debug($"[Panel] {path} -> {(int)response.StatusCode} {text}");
                return null;
            }
            return JsonConvert.DeserializeObject<T>(text);
        }

        public void Dispose()
        {
            key = null;
            sessionToken = null;
            spareNonce = null;
            screenCache.Clear();
            http.Dispose();
        }
    }
}
#endif
