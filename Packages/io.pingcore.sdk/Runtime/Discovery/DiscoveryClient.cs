using System;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using PingCore.Core;
using PingCore.Core.Discovery;
using PingCore.Core.Wire;
using PingCore.Unity;

namespace PingCore.Discovery.Client
{
    /// <summary>
    /// The player-facing Discovery client for ONE Discovery app (player tokens are per app):
    /// the server list, locations and latency, the player token, reservations, quick join and
    /// matchmaking tickets. Every call returns a result whose <see cref="DiscoveryOutcome"/> says
    /// what happened; none throws for an HTTP or transport failure. Call it from the Unity main
    /// thread (the default transport and scheduler are <c>UnityWebRequest</c> and <c>Awaitable</c>).
    /// <para>Client-embeddable only: it takes a <c>dscp_</c> public id and runtime player tokens,
    /// and <see cref="Create(DiscoveryClientOptions)"/> refuses any option shaped like another
    /// credential (<c>usr_</c>, <c>sys_</c>, <c>cdnpush_</c>, <c>dsc_</c>). Nothing here takes a
    /// <c>dsc_</c> token: backend-scope calls belong to a studio's backend.</para>
    /// </summary>
    public sealed partial class DiscoveryClient : IDisposable
    {
        private static readonly Regex CredentialShape = new Regex("(?<![A-Za-z0-9])(usr|sys|cdnpush|dsc)_", RegexOptions.CultureInvariant);
        private static readonly Regex PublicIdShape = new Regex("^dscp_[A-Za-z0-9_-]{1,95}$", RegexOptions.CultureInvariant);

        private readonly DiscoveryClientOptions options;
        private readonly DiscoveryCaller caller;
        private readonly IScheduler scheduler;
        private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        private readonly LatencyProbe latencyProbe;

        private DiscoveryClient(DiscoveryClientOptions options, IHttpTransport transport, IScheduler scheduler, IPlayerTokenStore store, IssuanceGate gate)
        {
            this.options = options;
            this.scheduler = scheduler;
            caller = new DiscoveryCaller(options.BaseUrl, transport);
            AppPublicId = options.AppPublicId;
            Tokens = new PlayerTokenCache(caller, AppPublicId, scheduler, store, options.Profile ?? string.Empty, gate, options.Log, lifetime.Token);
            latencyProbe = new LatencyProbe(scheduler);
        }

        /// <summary>The Discovery app's public id.</summary>
        public string AppPublicId { get; }

        /// <summary>The player token for this app.</summary>
        public PlayerTokenCache Tokens { get; }

        /// <summary>The Discovery base URL in use.</summary>
        public string BaseUrl => caller.BaseUrl;

        /// <summary>The scheduler this client waits on (the infrastructure check times the Editor detail with it).</summary>
        internal IScheduler Scheduler => scheduler;

        /// <summary>
        /// Creates a client. Throws <see cref="ArgumentException"/> (naming the option, never its
        /// value) when <see cref="DiscoveryClientOptions.AppPublicId"/> is not a <c>dscp_</c> id, the
        /// base URL is not http(s), the profile is malformed, or any of them is shaped like a credential.
        /// </summary>
        public static DiscoveryClient Create(DiscoveryClientOptions options)
        {
            return Create(options, IssuanceGate.Shared);
        }

        /// <summary>Creates a client from the project's settings asset for one of its app ids.</summary>
        public static DiscoveryClient Create(PingCoreClientSettings settings, string appPublicId)
        {
            if (settings == null)
            {
                throw new ArgumentNullException(nameof(settings));
            }

            return Create(new DiscoveryClientOptions { BaseUrl = settings.DiscoveryBaseUrl, AppPublicId = appPublicId });
        }

        /// <summary>Creates a client with its own issuance gate (tests: each one gets a fresh process-wide pacing).</summary>
        internal static DiscoveryClient Create(DiscoveryClientOptions options, IssuanceGate gate)
        {
            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            Validate(options);
            IHttpTransport transport = options.Transport ?? new UnityWebRequestTransport(options.CallTimeout);
            IScheduler scheduler = options.Scheduler ?? new AwaitableScheduler();
            IPlayerTokenStore store = options.TokenStore ?? new PlayerPrefsTokenStore();
            return new DiscoveryClient(options, transport, scheduler, store, gate ?? IssuanceGate.Shared);
        }

        /// <summary>True when <paramref name="value"/> contains a <c>usr_</c>, <c>sys_</c>, <c>cdnpush_</c> or <c>dsc_</c> credential prefix (a <c>dscp_</c> public id does not).</summary>
        public static bool LooksLikeCredential(string value)
        {
            return !string.IsNullOrEmpty(value) && CredentialShape.IsMatch(value);
        }

        /// <summary>True for a well-formed Discovery app public id (<c>dscp_</c> then 1 to 95 of <c>[A-Za-z0-9_-]</c>).</summary>
        public static bool IsAppPublicId(string value)
        {
            return !string.IsNullOrEmpty(value) && PublicIdShape.IsMatch(value) && !LooksLikeCredential(value);
        }

        /// <summary>Stops every poll loop and pending wait this client started.</summary>
        public void Dispose()
        {
            if (!lifetime.IsCancellationRequested)
            {
                lifetime.Cancel();
            }
        }

        private static void Validate(DiscoveryClientOptions options)
        {
            if (LooksLikeCredential(options.AppPublicId) || LooksLikeCredential(options.BaseUrl) || LooksLikeCredential(options.Profile))
            {
                throw new ArgumentException("an option holds a credential-shaped string (usr_, sys_, cdnpush_ or dsc_); the Discovery client takes only a dscp_ public id and runtime player tokens", nameof(options));
            }

            if (!IsAppPublicId(options.AppPublicId))
            {
                throw new ArgumentException("AppPublicId must be a Discovery app public id (dscp_...)", nameof(options));
            }

            if (string.IsNullOrEmpty(options.BaseUrl)
                || !Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out Uri uri)
                || (uri.Scheme != "https" && uri.Scheme != "http"))
            {
                throw new ArgumentException("BaseUrl must be an absolute http or https URL", nameof(options));
            }

            if (!PlayerTokenStoreKeys.IsValidProfile(options.Profile))
            {
                throw new ArgumentException("Profile is empty or 1 to 32 characters from [A-Za-z0-9_-]", nameof(options));
            }

            if (options.MaxAttempts < 1)
            {
                throw new ArgumentException("MaxAttempts must be at least 1", nameof(options));
            }
        }

        private CancellationTokenSource Link(CancellationToken cancellationToken) => CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);

        /// <summary>
        /// Sends one unauthenticated request, retrying a retryable answer through
        /// <see cref="RetryGovernor"/> up to <see cref="DiscoveryClientOptions.MaxAttempts"/>.
        /// </summary>
        private async Task<DiscoveryResult<T>> SendPublicAsync<T>(DiscoveryRequest request, string call, CancellationToken cancellationToken)
            where T : WireResponse
        {
            using (CancellationTokenSource linked = Link(cancellationToken))
            {
                return await WithRetryAsync(() => caller.SendAsync<T>(request, linked.Token), call, options.MaxAttempts, linked.Token);
            }
        }

        /// <summary>
        /// The one authorised-call wrapper. Gets a usable player token, builds the request for it,
        /// and sends. On a 401 whose reason is a player-token reason it drops the token, gets a new
        /// one once (the signed provider, else an anonymous issue), and sends the same request again
        /// (the same idempotency anchors; <paramref name="build"/> only re-reads the token's player id);
        /// a second such 401 is returned as Unauthorized. Retryable answers are retried through
        /// <see cref="RetryGovernor"/> when <paramref name="retry"/> is set; the typed player-token
        /// refusals (403 <c>anonymous_tokens_disabled</c>/<c>signed_tokens_disabled</c>, 503
        /// <c>anonymous_tokens_unavailable</c>) never are.
        /// <para>
        /// When the new token is for another player id (a re-issued anonymous token always is), a bound
        /// call is not resent: the first 401 comes back with <see cref="DiscoveryCallResult.IdentityChanged"/>
        /// set (the cache has already raised <see cref="PlayerTokenChange.IdentityChanged"/>). See
        /// <see cref="OwnerBinding"/> for which calls are bound and when.
        /// </para>
        /// </summary>
        private async Task<DiscoveryResult<T>> SendAuthorisedAsync<T>(Func<PlayerToken, DiscoveryRequest> build, string call, bool retry, OwnerBinding binding, CancellationToken cancellationToken)
            where T : WireResponse
        {
            var attempts = new AttemptHistory();
            using (CancellationTokenSource linked = Link(cancellationToken))
            {
                return await WithRetryAsync(() => SendAuthorisedOnceAsync<T>(build, call, binding, attempts, linked.Token), call, retry ? options.MaxAttempts : 1, linked.Token);
            }
        }

        private async Task<DiscoveryResult<T>> SendAuthorisedOnceAsync<T>(Func<PlayerToken, DiscoveryRequest> build, string call, OwnerBinding binding, AttemptHistory attempts, CancellationToken cancellationToken)
            where T : WireResponse
        {
            DiscoveryResult<PlayerToken> token = await Tokens.GetAsync(cancellationToken);
            if (!token.IsOk)
            {
                return DiscoveryResult<T>.From(token);
            }

            DiscoveryResult<T> answer = await caller.SendAsync<T>(build(token.Value).WithBearer(token.Value.Value), cancellationToken);
            if (answer.Status != 401 || !PlayerTokenCache.IsPlayerTokenRejection(answer.Reason))
            {
                attempts.Record(answer.Outcome);
                return answer;
            }

            Tokens.Reject(token.Value, answer.Reason);
            DiscoveryResult<PlayerToken> renewed = await Tokens.GetAsync(cancellationToken);
            if (!renewed.IsOk)
            {
                return DiscoveryResult<T>.From(renewed);
            }

            bool bound = binding == OwnerBinding.Always || (binding == OwnerBinding.OnceAnAttemptMayHaveLanded && attempts.AnyMayHaveLanded);
            if (bound && !string.Equals(token.Value.PlayerId, renewed.Value.PlayerId, StringComparison.Ordinal))
            {
                string why = binding == OwnerBinding.Always
                    ? "this call names something the old player id owns"
                    : "an earlier attempt may already have reached Discovery under the old player id";
                Write(DiscoveryLogLevel.Warning, call, "the player token was re-issued for another player id; not resent, because " + why);
                return DiscoveryResult<T>.From(DiscoveryCallResult.IdentityChangedFrom(answer,
                    "the player token was re-issued for another player id, and " + why + "; it was not resent"));
            }

            DiscoveryResult<T> second = await caller.SendAsync<T>(build(renewed.Value).WithBearer(renewed.Value.Value), cancellationToken);
            if (second.Status == 401 && PlayerTokenCache.IsPlayerTokenRejection(second.Reason))
            {
                Tokens.Reject(renewed.Value, second.Reason);
            }

            attempts.Record(second.Outcome);
            return second;
        }

        /// <summary>
        /// True when a sent request ended so that Discovery may have acted on it without the SDK seeing the
        /// answer: a 503, an unexpected answer (another status, or a body that does not parse) or no answer
        /// at all (a connection failure, timeout or reset). A 429, a 4xx refusal or a cancellation before
        /// sending is not.
        /// </summary>
        internal static bool MayHaveLanded(DiscoveryOutcome outcome)
        {
            return outcome == DiscoveryOutcome.Degraded
                || outcome == DiscoveryOutcome.Unexpected
                || outcome == DiscoveryOutcome.Unreachable;
        }

        private async Task<DiscoveryResult<T>> WithRetryAsync<T>(Func<Task<DiscoveryResult<T>>> send, string call, int maxAttempts, CancellationToken cancellationToken)
        {
            for (int attempt = 1; ; attempt++)
            {
                DiscoveryResult<T> result = await send();
                if (result.IsOk || IsFinalPlayerTokenRefusal(result)
                    || !RetryGovernor.TryGetDelay(result.Outcome, attempt, result.RetryAfter, maxAttempts, options.MaxRetryDelay, out TimeSpan delay))
                {
                    return result;
                }

                Write(DiscoveryLogLevel.Warning, call, "attempt " + attempt + " answered " + result + "; retrying in " + delay.TotalSeconds + " s");
                try
                {
                    await scheduler.DelayAsync(delay, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    return DiscoveryResult<T>.Local(DiscoveryOutcome.Cancelled, "cancelled while waiting to retry");
                }
            }
        }

        /// <summary>The player-token refusals that are typed and never retried.</summary>
        internal static bool IsFinalPlayerTokenRefusal(DiscoveryCallResult result)
        {
            return result.Reason == DiscoveryReason.AnonymousTokensDisabled
                || result.Reason == DiscoveryReason.SignedTokensDisabled
                || result.Reason == DiscoveryReason.AnonymousTokensUnavailable;
        }

        internal void LogTicket(TicketHandle handle, TicketState state)
        {
            Write(state == TicketState.Failed ? DiscoveryLogLevel.Warning : DiscoveryLogLevel.Info, "tickets", "ticket " + handle.TicketRef + " " + state);
        }

        internal void LogHandlerFailure(string handler, Exception e)
        {
            Write(DiscoveryLogLevel.Error, "events", "a " + handler + " handler threw " + e.GetType().Name);
        }

        private void Write(DiscoveryLogLevel level, string call, string message)
        {
            Action<DiscoveryLogEntry> log = options.Log;
            if (log == null)
            {
                return;
            }

            try
            {
                log(new DiscoveryLogEntry(level, call, message));
            }
            catch (Exception)
            {
                // A log sink must never break a call.
            }
        }

        /// <summary>When a 401 re-issue under another player id stops an authorised call from being resent.</summary>
        private enum OwnerBinding
        {
            /// <summary>
            /// Reserve, quick join and submit. Their reservation id, idempotency key or ticket id makes a resend
            /// safe only under the same player: Discovery scopes quick-join idempotency per player, refuses a
            /// reservation id another player holds, and answers a taken ticket id 409 <c>ticket_id_taken</c>.
            /// So a 401 on the first attempt (nothing reached Discovery) is still resent under the new player,
            /// but once any earlier attempt in the same call may have landed (<see cref="MayHaveLanded"/>), a
            /// resend under another player id could hold twice or leave the old player's ticket queued, and
            /// the call is bound.
            /// </summary>
            OnceAnAttemptMayHaveLanded,

            /// <summary>Always bound: a reservation read or release, a ticket poll or cancel name what the old player owns.</summary>
            Always,
        }

        /// <summary>What the earlier attempts of one authorised call did; one per call, never shared.</summary>
        private sealed class AttemptHistory
        {
            public bool AnyMayHaveLanded { get; private set; }

            public void Record(DiscoveryOutcome outcome)
            {
                if (DiscoveryClient.MayHaveLanded(outcome))
                {
                    AnyMayHaveLanded = true;
                }
            }
        }
    }
}
