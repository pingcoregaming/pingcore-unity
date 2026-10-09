using System;
using System.Threading;
using System.Threading.Tasks;
using PingCore.Core;
using PingCore.Core.Discovery;
using PingCore.Discovery.Client.Wire;

namespace PingCore.Discovery.Client
{
    /// <summary>
    /// The player token for one Discovery app. Anonymous by default: one token per app per
    /// profile, valid 6 h, persisted in the token store and used while more than 5 minutes remain.
    /// Issuance is single-flight (concurrent callers share one request), never on a timer, at most
    /// once per 6 s per process, and never before a received <c>Retry-After</c> has passed. A studio
    /// with its own login sets a signed token (<see cref="SetSignedToken"/>) or a provider
    /// (<see cref="SetSignedTokenProvider"/>), which is then asked on expiry and after a 401.
    /// 403 <c>anonymous_tokens_disabled</c> and 503 <c>anonymous_tokens_unavailable</c> come back as
    /// typed results and are never retried.
    /// </summary>
    public sealed partial class PlayerTokenCache
    {
        /// <summary>An anonymous token is replaced when less than this remains.</summary>
        public static readonly TimeSpan AnonymousRefreshMargin = TimeSpan.FromMinutes(5);

        /// <summary>A signed token is replaced (or, with no provider, given up) when less than this remains.</summary>
        public static readonly TimeSpan SignedRefreshMargin = TimeSpan.FromSeconds(30);

        private readonly object sync = new object();
        private readonly DiscoveryCaller caller;
        private readonly string appPublicId;
        private readonly IScheduler scheduler;
        private readonly IPlayerTokenStore store;
        private readonly string storeKey;
        private readonly IssuanceGate gate;
        private readonly Action<DiscoveryLogEntry> log;
        private readonly CancellationToken lifetime;
        private PlayerToken current;
        private Task<DiscoveryResult<PlayerToken>> inflight;
        private Func<CancellationToken, Task<string>> provider;
        private bool hadAnonymous;
        private string lastPlayerId;

        internal PlayerTokenCache(DiscoveryCaller caller, string appPublicId, IScheduler scheduler, IPlayerTokenStore store, string profile, IssuanceGate gate, Action<DiscoveryLogEntry> log, CancellationToken lifetime)
        {
            this.caller = caller;
            this.appPublicId = appPublicId;
            this.scheduler = scheduler;
            this.store = store;
            storeKey = PlayerTokenStoreKeys.For(profile, appPublicId);
            this.gate = gate;
            this.log = log;
            this.lifetime = lifetime;
        }

        /// <summary>Raised on every change. Never carries the token.</summary>
        public event Action<PlayerTokenEvent> Changed;

        /// <summary>The token in hand, or null. It may be close to expiry; <see cref="GetAsync"/> is what calls use.</summary>
        public PlayerToken Current
        {
            get
            {
                lock (sync)
                {
                    return current;
                }
            }
        }

        /// <summary>The token-store key this cache uses: <c>pingcore.playerToken.&lt;profile&gt;.&lt;dscp&gt;</c>.</summary>
        public string StoreKey => storeKey;

        /// <summary>True once a signed token provider is set.</summary>
        public bool HasSignedTokenProvider
        {
            get
            {
                lock (sync)
                {
                    return provider != null;
                }
            }
        }

        /// <summary>
        /// A usable token: the one in hand while it has enough life left, else (in order) one from
        /// the provider, one from the store, or a newly issued anonymous token. Never throws for an
        /// HTTP or transport failure. A cancelled <paramref name="cancellationToken"/> stops this
        /// caller's wait only; a shared issue in flight carries on for the others.
        /// </summary>
        public Task<DiscoveryResult<PlayerToken>> GetAsync(CancellationToken cancellationToken)
        {
            TaskCompletionSource<DiscoveryResult<PlayerToken>> flight = null;
            Task<DiscoveryResult<PlayerToken>> shared;
            lock (sync)
            {
                if (current != null && IsUsable(current, scheduler.UtcNow))
                {
                    return Task.FromResult(DiscoveryResult<PlayerToken>.Success(current, 0, null));
                }

                if (inflight == null)
                {
                    flight = new TaskCompletionSource<DiscoveryResult<PlayerToken>>(TaskCreationOptions.RunContinuationsAsynchronously);
                    inflight = flight.Task;
                }

                shared = inflight;
            }

            if (flight != null)
            {
                RunFlight(flight);
            }

            return WaitAsync(shared, cancellationToken);
        }

        /// <summary>
        /// Uses a studio-signed token (ES256) from now on. Its <c>sub</c> and <c>exp</c> are read
        /// without verification; Discovery verifies. Replaces an anonymous token in hand (the stored
        /// anonymous token is kept). Throws <see cref="ArgumentException"/> (without the token) when
        /// the payload cannot be read.
        /// </summary>
        public void SetSignedToken(string jwt)
        {
            if (!JwtClaims.TryRead(jwt, out string subject, out DateTimeOffset expiresAt))
            {
                throw new ArgumentException("the signed token is not a compact JWT with a string sub and a numeric exp", nameof(jwt));
            }

            Adopt(new PlayerToken(PlayerTokenKind.Signed, jwt, subject, expiresAt), PlayerTokenChange.SignedSet);
        }

        /// <summary>
        /// The studio backend hook: called when a signed token is needed (none in hand, expiring, or
        /// rejected with a 401). It returns a compact JWT, or null/empty when it has none. With a
        /// provider set the cache never issues or uses an anonymous token: one in hand is dropped
        /// (the stored one is kept for a later run without a provider). Pass null to remove it.
        /// </summary>
        public void SetSignedTokenProvider(Func<CancellationToken, Task<string>> signedTokenProvider)
        {
            lock (sync)
            {
                provider = signedTokenProvider;
                if (provider != null && current != null && current.Kind == PlayerTokenKind.Anonymous)
                {
                    current = null;
                }
            }
        }

        /// <summary>Drops the token in hand (and the stored anonymous token), so the next call gets a new one.</summary>
        public void Invalidate()
        {
            PlayerToken dropped;
            lock (sync)
            {
                dropped = current;
                current = null;
            }

            if (dropped == null || dropped.Kind == PlayerTokenKind.Anonymous)
            {
                SafeDelete();
            }
        }

        /// <summary>
        /// A 401 with a player-token reason for <paramref name="rejected"/>: drops it unless a newer
        /// token already replaced it, and raises <see cref="PlayerTokenChange.Rejected"/>.
        /// </summary>
        internal void Reject(PlayerToken rejected, DiscoveryReason reason)
        {
            bool dropped = false;
            lock (sync)
            {
                if (ReferenceEquals(current, rejected))
                {
                    current = null;
                    dropped = true;
                }
            }

            if (dropped && rejected.Kind == PlayerTokenKind.Anonymous)
            {
                SafeDelete();
            }

            Write(DiscoveryLogLevel.Warning, "Discovery rejected the " + (rejected.Kind == PlayerTokenKind.Signed ? "signed" : "anonymous") + " player token (" + (DiscoveryReasons.ToWireValue(reason) ?? "no reason") + ")");
            Raise(PlayerTokenChange.Rejected, rejected, reason);
        }

        /// <summary>The 401 reasons that mean "this player token is bad" (spec schema <c>PlayerTokenRejected</c>).</summary>
        internal static bool IsPlayerTokenRejection(DiscoveryReason reason)
        {
            switch (reason)
            {
                case DiscoveryReason.MalformedToken:
                case DiscoveryReason.UnsupportedAlg:
                case DiscoveryReason.NoSigningKeys:
                case DiscoveryReason.UnknownKid:
                case DiscoveryReason.BadSignature:
                case DiscoveryReason.MissingClaim:
                case DiscoveryReason.InvalidSub:
                case DiscoveryReason.InvalidClaims:
                case DiscoveryReason.AudienceMismatch:
                case DiscoveryReason.TokenExpired:
                case DiscoveryReason.TokenNotYetValid:
                case DiscoveryReason.LifetimeTooLong:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>True while <paramref name="token"/> has more than its kind's margin left at <paramref name="now"/>.</summary>
        internal static bool IsUsable(PlayerToken token, DateTimeOffset now)
        {
            TimeSpan margin = token.Kind == PlayerTokenKind.Anonymous ? AnonymousRefreshMargin : SignedRefreshMargin;
            return token.ExpiresAt - now > margin;
        }

        /// <summary>
        /// Makes <paramref name="token"/> the one in hand and raises <paramref name="change"/>, then
        /// <see cref="PlayerTokenChange.IdentityChanged"/> when its player id differs from the last token's.
        /// </summary>
        private void Adopt(PlayerToken token, PlayerTokenChange change)
        {
            string previous;
            lock (sync)
            {
                current = token;
                previous = lastPlayerId;
                lastPlayerId = token.PlayerId;
            }

            Raise(change, token, DiscoveryReason.Unknown);
            if (previous != null && !string.Equals(previous, token.PlayerId, StringComparison.Ordinal))
            {
                Write(DiscoveryLogLevel.Warning, "the player id changed (" + previous + " to " + token.PlayerId + "); what the old one held is out of reach");
                RaiseEvent(new PlayerTokenEvent(PlayerTokenChange.IdentityChanged, token.Kind, token.PlayerId, token.ExpiresAt, DiscoveryReason.Unknown, previous));
            }
        }

        private void Raise(PlayerTokenChange change, PlayerToken token, DiscoveryReason reason)
        {
            RaiseEvent(new PlayerTokenEvent(change, token.Kind, token.PlayerId, token.ExpiresAt, reason));
        }

        private void RaiseEvent(PlayerTokenEvent tokenEvent)
        {
            Action<PlayerTokenEvent> handler = Changed;
            if (handler == null)
            {
                return;
            }

            try
            {
                handler(tokenEvent);
            }
            catch (Exception e)
            {
                Write(DiscoveryLogLevel.Error, "a PlayerTokenCache.Changed handler threw " + e.GetType().Name);
            }
        }

        private void Write(DiscoveryLogLevel level, string message)
        {
            if (log == null)
            {
                return;
            }

            try
            {
                log(new DiscoveryLogEntry(level, "playerToken", message));
            }
            catch (Exception)
            {
                // A log sink must never break the token cache.
            }
        }
    }
}
