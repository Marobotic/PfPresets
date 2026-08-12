#if PFP_RATINGS
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace PfPresets
{
    /// <summary>Why a call didn't return data, so callers can react without parsing HTTP.</summary>
    internal enum ApiStatus
    {
        Ok,

        /// <summary>Network unreachable, DNS down, timeout, or the circuit breaker is open.
        /// Always transient from the caller's point of view.</summary>
        Offline,

        /// <summary>Not logged in, or a session could not be established.</summary>
        NoSession,

        /// <summary>The 24h per-target cooldown is still running (HTTP 409).</summary>
        Cooldown,

        /// <summary>Too many requests (HTTP 429). <see cref="ApiResult{T}.RetryAfter"/> says when.</summary>
        RateLimited,

        /// <summary>The target has opted out, or the endpoint refused the request (HTTP 403).</summary>
        Refused,

        /// <summary>Malformed request - a bug on our side, not worth retrying (HTTP 4xx).</summary>
        BadRequest,

        /// <summary>The server is broken (HTTP 5xx) after all retries.</summary>
        ServerError,
    }

    internal readonly struct ApiResult<T> where T : class
    {
        public ApiStatus Status { get; }
        public T? Value { get; }
        public TimeSpan? RetryAfter { get; }

        /// <summary>
        /// What the server said to tell the player, or empty when it didn't say.
        ///
        /// Preferred over anything written here, because wording baked into a plugin can only be
        /// corrected at the speed of everybody updating - and the refusals worth explaining are
        /// exactly the ones whose shape keeps changing. Empty is normal and expected: an
        /// unreachable server cannot send a sentence, which is precisely when the local fallback
        /// matters most.
        /// </summary>
        public string Message { get; }

        private ApiResult(ApiStatus status, T? value, TimeSpan? retryAfter, string message)
        {
            Status = status;
            Value = value;
            RetryAfter = retryAfter;
            Message = message;
        }

        public bool IsOk => Status == ApiStatus.Ok && Value != null;

        public static ApiResult<T> Ok(T value) => new(ApiStatus.Ok, value, null, string.Empty);
        public static ApiResult<T> Fail(ApiStatus status, TimeSpan? retryAfter = null,
            string message = "")
            => new(status, null, retryAfter, message);
    }

    /// <summary>
    /// HTTP client for the community rating API.
    ///
    /// It follows the same contract as <see cref="AnalyticsClient"/> - nothing in the plugin ever
    /// waits on it, no call throws outward, and a dead server degrades the rating features to
    /// "no data" rather than breaking anything - but adds what a real API needs: a bearer session,
    /// typed responses, bounded retries with backoff, Retry-After handling, and a circuit breaker
    /// so a server outage costs one failed request every few minutes instead of one per lookup.
    ///
    /// What leaves this machine on these endpoints:
    ///   - the local install id and the acting character's name + home world, once per session,
    ///     to /auth/session (the server hashes both and stores only the hashes)
    ///   - for a lookup: the name + world being searched
    ///   - for a submission: the target's name + world, a 1-5 score, optional preset feedback
    ///     tags, the duty's ContentFinderCondition id, and whether the target is a friend/FC member
    /// Nothing else - no combat data, no chat, no inventory, no hardware identifiers.
    /// </summary>
    internal sealed class PfApiClient : IDisposable
    {
        /// <summary>The shipping endpoint, used unless the config overrides it.</summary>
        private const string DefaultBaseUrl = "https://api.marobotic.dev/pfp/v2/";

        /// <summary>
        /// Ten seconds was too short and showed up as "couldn't reach the server".
        ///
        /// A progression refresh legitimately takes longer than that - it asks a third party about
        /// every party member - so the client was giving up on a request the server was still
        /// working on and reporting a healthy server as unreachable. Reads are unaffected; they
        /// answer from our own database in well under a second.
        /// </summary>
        private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(35);

        /// <summary>Refresh this long before the token actually expires, so a submission never
        /// races an expiry.</summary>
        private static readonly TimeSpan TokenRefreshMargin = TimeSpan.FromMinutes(5);

        /// <summary>Consecutive failures before the breaker opens.</summary>
        private const int CircuitFailureThreshold = 5;

        /// <summary>How long the breaker stays open. Long enough that an outage is cheap, short
        /// enough that recovery is noticed within a play session.</summary>
        private static readonly TimeSpan CircuitOpenDuration = TimeSpan.FromMinutes(5);

        private const int MaxAttempts = 3;

        /// <summary>Deserialization settings for data that came off the network: no type names
        /// (so a response can't pick which classes get instantiated) and a shallow depth cap.
        /// Same posture as <see cref="PresetShare"/> takes with share codes.</summary>
        private static readonly JsonSerializerSettings ReadSettings = new()
        {
            TypeNameHandling = TypeNameHandling.None,
            MetadataPropertyHandling = MetadataPropertyHandling.Ignore,
            MaxDepth = 16,
        };

        private static readonly JsonSerializerSettings WriteSettings = new()
        {
            ContractResolver = new CamelCasePropertyNamesContractResolver(),
            NullValueHandling = NullValueHandling.Ignore,
        };

        private readonly Configuration config;
        private readonly IPluginLog log;
        private readonly string version;
        private readonly Func<CharacterIdentity?> identityProvider;
        private readonly HttpClient http;
        private readonly CancellationTokenSource cancel = new();

        /// <summary>Serialises session creation so eight concurrent lookups can't each mint a token.</summary>
        private readonly SemaphoreSlim sessionLock = new(1, 1);

        private string? sessionToken;
        private DateTime sessionExpiresUtc = DateTime.MinValue;

        /// <summary>The identity the current token was minted for. A character switch must
        /// invalidate the session, or votes would be attributed to the previous character.</summary>
        private string? sessionIdentityKey;

        private int consecutiveFailures;
        private DateTime circuitOpenUntilUtc = DateTime.MinValue;
        private bool loggedFailure;

        public PfApiClient(
            Configuration config,
            IPluginLog log,
            string version,
            Func<CharacterIdentity?> identityProvider)
        {
            this.config = config;
            this.log = log;
            this.version = version;
            this.identityProvider = identityProvider;

            http = new HttpClient
            {
                BaseAddress = new Uri(ResolveBaseUrl(config, log)),
                Timeout = RequestTimeout,
            };
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"PfPresets/{version}");
        }

        /// <summary>Cancellation token tied to the plugin's lifetime, for callers that spawn their
        /// own work around these calls.</summary>
        public CancellationToken ShutdownToken => cancel.Token;

        /// <summary>
        /// The character this client is acting as, or null when logged out.
        ///
        /// Exposed because the vote path needs to name the local player in the evidence it sends,
        /// and this class already holds the provider - threading a second copy of the same function
        /// through the service would give two answers that could disagree at a character switch.
        /// </summary>
        public CharacterIdentity? LocalIdentity => identityProvider();

        /// <summary>
        /// The endpoint in use: the configured override when it's a usable absolute URL, otherwise
        /// the shipping one. A malformed override falls back rather than throwing - a typo in a
        /// settings box must not stop the plugin loading.
        /// </summary>
        private static string ResolveBaseUrl(Configuration config, IPluginLog log)
        {
            string configured = config.RatingApiBaseUrl?.Trim() ?? string.Empty;
            if (configured.Length == 0)
                return DefaultBaseUrl;

            // A relative BaseAddress makes every subsequent request throw, so it is checked here.
            if (!Uri.TryCreate(configured, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            {
                log.Warning($"[Ratings] Ignoring invalid server override \"{configured}\"; using the default.");
                return DefaultBaseUrl;
            }

            // HttpClient drops the last path segment of a BaseAddress that doesn't end in a slash,
            // which silently turns /pfp/v2 into /pfp - a confusing 404 rather than an obvious error.
            string normalised = configured.EndsWith('/') ? configured : configured + "/";

            log.Information($"[Ratings] Using configured rating server: {normalised}");
            return normalised;
        }

        /// <summary>The endpoint actually in use, so settings can show where votes are going.</summary>
        public string Endpoint => http.BaseAddress?.ToString() ?? DefaultBaseUrl;

        // ══════════════════════════════════════════════════════════
        //  ENDPOINTS
        // ══════════════════════════════════════════════════════════

        public async Task<ApiResult<PlayerRating>> LookupAsync(CharacterIdentity who)
        {
            if (!who.IsValid)
                return ApiResult<PlayerRating>.Fail(ApiStatus.BadRequest);

            return await SendAsync<PlayerRating>(
                HttpMethod.Post,
                "players/lookup",
                new LookupRequest { Name = who.Name, World = who.World }).ConfigureAwait(false);
        }

        /// <summary>Looks up many players in one request. The party panel and the Contacts tab
        /// each need a rating for everyone on screen at once; doing that as individual calls would
        /// put a burst of dozens of requests on the server every time either is drawn.</summary>
        public async Task<ApiResult<BatchLookupResponse>> LookupBatchAsync(IReadOnlyList<CharacterIdentity> who)
        {
            var request = new BatchLookupRequest();
            foreach (var w in who)
            {
                if (w.IsValid)
                    request.Players.Add(new LookupRequest { Name = w.Name, World = w.World });
            }

            if (request.Players.Count == 0)
                return ApiResult<BatchLookupResponse>.Fail(ApiStatus.BadRequest);

            return await SendAsync<BatchLookupResponse>(
                HttpMethod.Post, "players/batch", request).ConfigureAwait(false);
        }

        public async Task<ApiResult<SubmitRatingResponse>> SubmitAsync(SubmitRatingRequest submission)
        {
            // Score is +1 (upvote) or -1 (downvote); anything else is a bug on our side.
            if (!submission.Target.IsValid || submission.Score is not (1 or -1))
                return ApiResult<SubmitRatingResponse>.Fail(ApiStatus.BadRequest);

            return await SendAsync<SubmitRatingResponse>(
                HttpMethod.Post, "ratings", submission).ConfigureAwait(false);
        }

        /// <summary>Reports a player to the plugin author. Deliberately routed through the API
        /// rather than posting straight to a chat webhook: a webhook URL shipped inside the plugin
        /// could be extracted and used to flood the channel by anyone who unzipped the DLL.</summary>
        public async Task<ApiResult<SubmitReportResponse>> SubmitReportAsync(SubmitReportRequest report)
        {
            if (!report.Target.IsValid)
                return ApiResult<SubmitReportResponse>.Fail(ApiStatus.BadRequest);

            return await SendAsync<SubmitReportResponse>(
                HttpMethod.Post, "reports", report).ConfigureAwait(false);
        }

        /// <summary>
        /// Party progression on one duty.
        ///
        /// The only call in this client that causes character names to reach a third party: the
        /// server forwards them to FFLogs. That is why it is never issued automatically - see the
        /// progress control in the party list.
        /// </summary>
        public async Task<ApiResult<ProgressResponse>> GetProgressAsync(ProgressRequest request)
        {
            if (request.Players.Count == 0)
                return ApiResult<ProgressResponse>.Fail(ApiStatus.BadRequest);

            return await SendAsync<ProgressResponse>(
                HttpMethod.Post, "progress", request).ConfigureAwait(false);
        }

        /// <summary>
        /// One character's high-end clears, for the profile card.
        ///
        /// Reading is free - it stops at the server's own table, and every card opening does one.
        /// Only <see cref="ClearsRequest.Refresh"/> can cause anything to reach a third party, and
        /// then only if nobody has looked this character up in the last hour.
        /// </summary>
        public async Task<ApiResult<ClearsResponse>> GetClearsAsync(ClearsRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.World))
                return ApiResult<ClearsResponse>.Fail(ApiStatus.BadRequest);

            return await SendAsync<ClearsResponse>(
                HttpMethod.Post, "clears", request).ConfigureAwait(false);
        }

        // ── Achievements ──────────────────────────────────────────

        /// <summary>
        /// Offers a clear to the feed.
        ///
        /// Called for every duty that finishes, and answers "not worth a post" for almost all of
        /// them - what counts is the server's decision, so a patch that adds an Ultimate needs no
        /// plugin update. Nothing here is worth telling the user about: a clear that fails to post
        /// is a line in the log, not a message printed over somebody's victory.
        /// </summary>
        public async Task<ApiResult<AchievementPostResponse>> PostAchievementAsync(
            AchievementPostRequest request)
        {
            if (string.IsNullOrEmpty(request.Evidence))
                return ApiResult<AchievementPostResponse>.Fail(ApiStatus.BadRequest);

            return await SendAsync<AchievementPostResponse>(
                HttpMethod.Post, "achievements", request).ConfigureAwait(false);
        }

        /// <summary>The feed, newest first. Reaches our own table and nothing else.</summary>
        public async Task<ApiResult<AchievementFeedResponse>> GetFeedAsync(int page = 0)
            => await SendAsync<AchievementFeedResponse>(
                HttpMethod.Post, "achievements/feed",
                new AchievementFeedRequest { Page = Math.Max(0, page) }).ConfigureAwait(false);

        public async Task<ApiResult<AchievementReactResponse>> HeartAsync(string id)
            => await SendAsync<AchievementReactResponse>(
                HttpMethod.Post, "achievements/heart",
                new AchievementReactRequest { Id = id }).ConfigureAwait(false);

        public async Task<ApiResult<AchievementReactResponse>> ShareAsync(string id)
            => await SendAsync<AchievementReactResponse>(
                HttpMethod.Post, "achievements/reshare",
                new AchievementReactRequest { Id = id }).ConfigureAwait(false);

        /// <summary>Turns broadcasting on or off for the character this session belongs to. The
        /// server checks it against the session's own character, so this cannot be pointed at
        /// anybody else's.</summary>
        public async Task<ApiResult<BroadcastSettingResponse>> SetBroadcastAsync(
            CharacterIdentity who, bool broadcast)
        {
            if (!who.IsValid)
                return ApiResult<BroadcastSettingResponse>.Fail(ApiStatus.BadRequest);

            return await SendAsync<BroadcastSettingResponse>(
                HttpMethod.Post, "achievements/settings",
                new BroadcastSettingRequest
                {
                    Character = new CharacterRef { Name = who.Name, World = who.World },
                    Broadcast = broadcast,
                }).ConfigureAwait(false);
        }

        // ── The rating opt-out ────────────────────────────────────

        /// <summary>
        /// Files a request to be opted out of the rating system.
        ///
        /// A request, not a change - a person decides, the same as one filed from the website. What
        /// this adds over the web form is that the character comes from the session it is logged
        /// into rather than from a link anybody can paste.
        ///
        /// The sealed statement is not optional: without it the server refuses, which is correct -
        /// a request with nothing behind it is one anybody can file about anybody.
        /// </summary>
        public async Task<ApiResult<OptOutSelfResponse>> RequestOptOutAsync(
            CharacterIdentity who, bool optOut, string evidence)
        {
            if (string.IsNullOrEmpty(evidence) || !who.IsValid)
                return ApiResult<OptOutSelfResponse>.Fail(ApiStatus.BadRequest);

            return await SendAsync<OptOutSelfResponse>(
                HttpMethod.Post, "optout/self",
                new
                {
                    character = new CharacterRef { Name = who.Name, World = who.World },
                    optOut,
                    evidence,
                }).ConfigureAwait(false);
        }

        /// <summary>What the toggle should be showing, according to the server. Read at login, so
        /// the setting survives a reinstall - which is the whole promise made to somebody who opts
        /// out.</summary>
        public async Task<ApiResult<OptOutStateResponse>> GetOptedOutAsync()
            => await SendAsync<OptOutStateResponse>(
                HttpMethod.Post, "optout/state", new { }).ConfigureAwait(false);

                /// <summary>Job and level for a profile view. Answered from the server's own cache after
        /// the first lookup, so this is cheap to call and rarely reaches a third party.</summary>
        public async Task<ApiResult<CharacterInfo>> GetCharacterAsync(CharacterIdentity who)
        {
            if (!who.IsValid)
                return ApiResult<CharacterInfo>.Fail(ApiStatus.BadRequest);

            return await SendAsync<CharacterInfo>(
                HttpMethod.Post, "character", new { name = who.Name, world = who.World })
                .ConfigureAwait(false);
        }

        /// <summary>Community-wide rating totals. Unauthenticated: they are aggregate counts with
        /// nothing identifying in them.</summary>
        public async Task<ApiResult<RatingStats>> GetStatsAsync()
            => await SendAsync<RatingStats>(HttpMethod.Get, "stats", null, requireAuth: false).ConfigureAwait(false);

        public async Task<ApiResult<RatingPolicy>> GetPolicyAsync()
            => await SendAsync<RatingPolicy>(HttpMethod.Get, "policy", null, requireAuth: false).ConfigureAwait(false);

        // DeleteMyLedgerAsync is gone, with the route behind it. The cooldown ledger is the
        // anti-abuse record - erasing it on request let anybody rate the same person over and over
        // at full weight, and because the caller's identity was a claimed name it could be pointed
        // at somebody else's ledger just as easily as your own.

        internal sealed class EmptyResponse { }

        // ══════════════════════════════════════════════════════════
        //  TRANSPORT
        // ══════════════════════════════════════════════════════════

        // ── The community poll ────────────────────────────────────────────
        //
        // Unauthenticated on the read: the poll is public and the same five options go to every
        // surface. The vote carries the session only when this install is taking part - see the
        // note on the Vote tab about somebody who has opted out.

        public async Task<ApiResult<PollResponse>> GetPollAsync()
            => await SendAsync<PollResponse>(
                HttpMethod.Get, "poll", null, requireAuth: false).ConfigureAwait(false);

        public async Task<ApiResult<PollVoteResponse>> VotePollAsync(
            string slug, string option, string token, bool identified)
            => await SendAsync<PollVoteResponse>(
                HttpMethod.Post, "poll/vote",
                new PollVoteRequest
                {
                    Slug = slug, Option = option, Token = token, FromPlugin = identified,
                },
                requireAuth: identified).ConfigureAwait(false);

        private async Task<ApiResult<TRes>> SendAsync<TRes>(
            HttpMethod method,
            string path,
            object? body,
            bool requireAuth = true) where TRes : class
        {
            if (cancel.IsCancellationRequested)
                return ApiResult<TRes>.Fail(ApiStatus.Offline);

            if (IsCircuitOpen())
                return ApiResult<TRes>.Fail(ApiStatus.Offline);

            string? token = null;
            if (requireAuth)
            {
                token = await EnsureSessionAsync().ConfigureAwait(false);
                if (token == null)
                    return ApiResult<TRes>.Fail(ApiStatus.NoSession);
            }

            for (int attempt = 0; attempt < MaxAttempts; attempt++)
            {
                try
                {
                    using var request = BuildRequest(method, path, body, token);
                    using var response = await http.SendAsync(request, cancel.Token).ConfigureAwait(false);

                    // The token was rejected - mint a fresh one and give the call exactly one
                    // more go, so an expiry that slipped past the refresh margin is invisible.
                    if (response.StatusCode == HttpStatusCode.Unauthorized && requireAuth && attempt == 0)
                    {
                        InvalidateSession();
                        token = await EnsureSessionAsync().ConfigureAwait(false);
                        if (token == null)
                            return ApiResult<TRes>.Fail(ApiStatus.NoSession);
                        continue;
                    }

                    if (response.IsSuccessStatusCode)
                    {
                        OnSuccess();
                        string json = await response.Content.ReadAsStringAsync(cancel.Token).ConfigureAwait(false);
                        if (string.IsNullOrWhiteSpace(json))
                        {
                            // A 204 is a legitimate success for the endpoints that return nothing;
                            // for anything else an empty body means the server misbehaved.
                            return typeof(TRes) == typeof(EmptyResponse)
                                ? ApiResult<TRes>.Ok((TRes)(object)new EmptyResponse())
                                : ApiResult<TRes>.Fail(ApiStatus.ServerError);
                        }

                        var parsed = JsonConvert.DeserializeObject<TRes>(json, ReadSettings);
                        return parsed == null
                            ? ApiResult<TRes>.Fail(ApiStatus.ServerError)
                            : ApiResult<TRes>.Ok(parsed);
                    }

                    // Whatever the server wants the player told. Read before the switch so every
                    // refusal below carries it, and tolerant of its absence - an older server, or a
                    // proxy's own error page, simply has none.
                    string said = await ReadServerMessageAsync(response).ConfigureAwait(false);

                    // Deliberate refusals are answers, not failures: they must not trip the
                    // breaker, and retrying them would just repeat the same rejection.
                    switch (response.StatusCode)
                    {
                        case HttpStatusCode.Conflict:
                            OnSuccess();
                            return ApiResult<TRes>.Fail(ApiStatus.Cooldown, ReadRetryAfter(response), said);
                        case HttpStatusCode.TooManyRequests:
                            OnSuccess();
                            return ApiResult<TRes>.Fail(ApiStatus.RateLimited, ReadRetryAfter(response), said);
                        case HttpStatusCode.Forbidden:
                            OnSuccess();
                            return ApiResult<TRes>.Fail(ApiStatus.Refused, null, said);
                    }

                    if ((int)response.StatusCode is >= 400 and < 500)
                    {
                        OnSuccess(); // the server is healthy; the request was wrong
                        LogOnce($"[Ratings] {path} rejected: {(int)response.StatusCode}.");
                        return ApiResult<TRes>.Fail(ApiStatus.BadRequest, null, said);
                    }

                    // 5xx: retry with backoff.
                    if (attempt == MaxAttempts - 1)
                    {
                        OnFailure();
                        return ApiResult<TRes>.Fail(ApiStatus.ServerError);
                    }
                    await BackoffAsync(attempt).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancel.IsCancellationRequested)
                {
                    return ApiResult<TRes>.Fail(ApiStatus.Offline); // unloading
                }
                catch (OperationCanceledException)
                {
                    // HttpClient surfaces its own timeout as cancellation too. Retryable.
                    if (attempt == MaxAttempts - 1)
                    {
                        OnFailure();
                        return ApiResult<TRes>.Fail(ApiStatus.Offline);
                    }
                    await BackoffAsync(attempt).ConfigureAwait(false);
                }
                catch (JsonException)
                {
                    // A malformed body won't parse any better the second time.
                    OnFailure();
                    LogOnce($"[Ratings] {path} returned a body that could not be read.");
                    return ApiResult<TRes>.Fail(ApiStatus.ServerError);
                }
                catch (Exception ex)
                {
                    if (attempt == MaxAttempts - 1)
                    {
                        OnFailure();
                        LogOnce($"[Ratings] {path} failed (further failures won't be logged): {ex.Message}");
                        return ApiResult<TRes>.Fail(ApiStatus.Offline);
                    }
                    await BackoffAsync(attempt).ConfigureAwait(false);
                }
            }

            OnFailure();
            return ApiResult<TRes>.Fail(ApiStatus.Offline);
        }

        private HttpRequestMessage BuildRequest(HttpMethod method, string path, object? body, string? token)
        {
            var request = new HttpRequestMessage(method, path);

            if (body != null)
            {
                string json = JsonConvert.SerializeObject(body, WriteSettings);
                request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            }

            if (token != null)
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            return request;
        }

        /// <summary>
        /// The `message` field off an error body, or empty.
        ///
        /// Never throws and never blocks a refusal: a body that isn't JSON, isn't ours, or isn't
        /// there at all is an ordinary outcome, and the caller falls back to its own wording.
        /// </summary>
        private static async Task<string> ReadServerMessageAsync(HttpResponseMessage response)
        {
            try
            {
                string json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(json) || json.Length > 4096)
                    return string.Empty;

                var body = JsonConvert.DeserializeObject<ServerError>(json, ReadSettings);
                return body?.Message?.Trim() ?? string.Empty;
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        private sealed class ServerError
        {
            public string? Error { get; set; }
            public string? Message { get; set; }
        }

        private static TimeSpan? ReadRetryAfter(HttpResponseMessage response)
        {
            var retryAfter = response.Headers.RetryAfter;
            if (retryAfter == null)
                return null;
            if (retryAfter.Delta.HasValue)
                return retryAfter.Delta;
            if (retryAfter.Date.HasValue)
            {
                var delta = retryAfter.Date.Value - DateTimeOffset.UtcNow;
                return delta > TimeSpan.Zero ? delta : TimeSpan.Zero;
            }
            return null;
        }

        /// <summary>Exponential backoff with jitter, so a server coming back up doesn't get every
        /// client in the world retrying on the same tick.</summary>
        private async Task BackoffAsync(int attempt)
        {
            int baseMs = 500 * (1 << attempt);
            int jitter = Random.Shared.Next(0, 250);
            try
            {
                await Task.Delay(baseMs + jitter, cancel.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Unloading.
            }
        }

        // ══════════════════════════════════════════════════════════
        //  SESSION
        // ══════════════════════════════════════════════════════════

        /// <summary>
        /// Returns a usable bearer token, minting one if needed. Identity is sent here and only
        /// here: every other call carries the opaque token instead, so a character name isn't
        /// repeated on the wire for every lookup.
        /// </summary>
        private async Task<string?> EnsureSessionAsync()
        {
            var identity = SafeIdentity();
            if (identity == null || !identity.IsValid)
                return null;

            if (IsSessionUsable(identity))
                return sessionToken;

            await sessionLock.WaitAsync(cancel.Token).ConfigureAwait(false);
            try
            {
                // Another caller may have refreshed it while we waited.
                if (IsSessionUsable(identity))
                    return sessionToken;

                string installId = EnsureInstallId();
                var request = new SessionRequest
                {
                    InstallId = installId,
                    Character = identity,
                    PluginVersion = version,
                };

                using var message = BuildRequest(HttpMethod.Post, "auth/session", request, null);
                using var response = await http.SendAsync(message, cancel.Token).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    if ((int)response.StatusCode >= 500)
                        OnFailure();
                    LogOnce($"[Ratings] Session request rejected: {(int)response.StatusCode}.");
                    return null;
                }

                string json = await response.Content.ReadAsStringAsync(cancel.Token).ConfigureAwait(false);
                var session = JsonConvert.DeserializeObject<SessionResponse>(json, ReadSettings);
                if (session == null || string.IsNullOrWhiteSpace(session.Token))
                    return null;

                sessionToken = session.Token;
                sessionExpiresUtc = DateTime.UtcNow.AddSeconds(Math.Max(60, session.ExpiresIn));
                sessionIdentityKey = identity.Key;
                OnSuccess();
                return sessionToken;
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (Exception ex)
            {
                OnFailure();
                LogOnce($"[Ratings] Session request failed: {ex.Message}");
                return null;
            }
            finally
            {
                sessionLock.Release();
            }
        }

        private bool IsSessionUsable(CharacterIdentity identity)
            => sessionToken != null
            && sessionIdentityKey == identity.Key
            && DateTime.UtcNow < sessionExpiresUtc - TokenRefreshMargin;

        private void InvalidateSession()
        {
            sessionToken = null;
            sessionExpiresUtc = DateTime.MinValue;
            sessionIdentityKey = null;
        }

        /// <summary>Drops the session when the player logs out or switches character, so the next
        /// call re-establishes identity instead of voting as whoever was logged in before.</summary>
        public void OnCharacterChanged() => InvalidateSession();

        private CharacterIdentity? SafeIdentity()
        {
            try
            {
                return identityProvider();
            }
            catch (Exception)
            {
                // Reading game state can fail during a zone change or logout.
                return null;
            }
        }

        /// <summary>Reuses the analytics install id rather than minting a second one: it is already
        /// a random value that identifies nothing but "one copy of the plugin", and a second id
        /// would mean one more thing to explain and reset.</summary>
        private string EnsureInstallId()
        {
            if (!string.IsNullOrWhiteSpace(config.AnalyticsInstallId))
                return config.AnalyticsInstallId;

            config.AnalyticsInstallId = Guid.NewGuid().ToString("N");
            config.Save();
            return config.AnalyticsInstallId;
        }

        // ══════════════════════════════════════════════════════════
        //  CIRCUIT BREAKER
        // ══════════════════════════════════════════════════════════

        private bool IsCircuitOpen() => DateTime.UtcNow < circuitOpenUntilUtc;

        private void OnSuccess()
        {
            consecutiveFailures = 0;
            circuitOpenUntilUtc = DateTime.MinValue;
            loggedFailure = false;
        }

        private void OnFailure()
        {
            consecutiveFailures++;
            if (consecutiveFailures >= CircuitFailureThreshold)
            {
                circuitOpenUntilUtc = DateTime.UtcNow.Add(CircuitOpenDuration);
                consecutiveFailures = 0;
                log.Debug($"[Ratings] Server unreachable; pausing requests for {CircuitOpenDuration.TotalMinutes:0} minutes.");
            }
        }

        private void LogOnce(string message)
        {
            if (loggedFailure)
                return;
            loggedFailure = true;
            log.Debug(message);
        }

        public void Dispose()
        {
            try
            {
                cancel.Cancel();
                cancel.Dispose();
                sessionLock.Dispose();
                http.Dispose();
            }
            catch (Exception)
            {
                // Nothing useful to do while unloading.
            }
        }
    }
}
#endif
