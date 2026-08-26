#if PFP_RATINGS
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
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

        private const string KeyFileName = "pfa.dat";

        private const string StateFileName = "pfa.ui";

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
            http = new HttpClient
            {
                BaseAddress = new Uri(baseUrl),
                Timeout = TimeSpan.FromSeconds(20L)
            };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("PfPresets/" + version);
            Load();
        }

        public void SetEnabled(bool on)
        {
            Enabled = on;
            SaveState();
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
                    Dictionary<string, bool> st = JsonConvert.DeserializeObject<Dictionary<string, bool>>(File.ReadAllText(StatePath));
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
                    StoredKey stored = JsonConvert.DeserializeObject<StoredKey>(File.ReadAllText(KeyPath));
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
                RegisterResponse result = await PostAsync<RegisterResponse>("panels/join", new
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
            string nonce = await ChallengeAsync().ConfigureAwait(false);
            if (nonce == null)
            {
                return false;
            }
            SessionResponse res = await PostAsync<SessionResponse>("panels/open", new
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
            return await PostAsync<ScreenResponse>("panels", new
            {
                screen = id,
                controls = controls
            }, sessionToken).ConfigureAwait(false);
        }

        public async Task<string> DoAsync(string token, Dictionary<string, object>? inputs = null)
        {
            if (!(await EnsureSessionAsync().ConfigureAwait(false)))
            {
                return "Couldn't sign in.";
            }
            string nonce = await ChallengeAsync().ConfigureAwait(false);
            if (nonce == null)
            {
                return "Couldn't get a challenge.";
            }
            ScreenActionResponse res = await PostAsync<ScreenActionResponse>("panels/act", new
            {
                token = token,
                inputs = inputs,
                nonce = nonce,
                signature = Sign($"{label}|{nonce}|{token}")
            }, sessionToken).ConfigureAwait(false);
            if (res == null || !res.Ok)
            {
                return "The server refused it.";
            }
            return res.Note ?? string.Empty;
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
                string name = Between(haystack, "frame__chara__name\">", "<");
                string world = Between(haystack, "frame__chara__world\">", "<");
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
            http.Dispose();
        }
    }
}
#endif
