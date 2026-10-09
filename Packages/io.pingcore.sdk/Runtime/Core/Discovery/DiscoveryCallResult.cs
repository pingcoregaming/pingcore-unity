using System;
using PingCore.Core.Wire;

namespace PingCore.Core.Discovery
{
    /// <summary>
    /// The result of one Discovery call. Mirrors the local SDK shim's <c>FleetCallResult</c> plus
    /// the Discovery-only <see cref="Reason"/>, <see cref="RetryAfter"/> and <see cref="RateLimit"/>.
    /// It never carries a request header or a token.
    /// </summary>
    public class DiscoveryCallResult
    {
        /// <summary>Creates a result.</summary>
        /// <param name="outcome">How the call ended.</param>
        /// <param name="status">HTTP status, or 0 when nothing was sent or no answer arrived.</param>
        /// <param name="reasonWire">The body's <c>reason</c> string, or the reason a local refusal mirrors; null for none.</param>
        /// <param name="message">The body's <c>message</c>, or the SDK's own description; null on success.</param>
        /// <param name="error">The parsed error body, or null.</param>
        /// <param name="retryAfter">The <c>Retry-After</c> delay, or null.</param>
        /// <param name="rateLimit">The <c>RateLimit-*</c> budget, or null.</param>
        public DiscoveryCallResult(DiscoveryOutcome outcome, int status, string reasonWire, string message, ErrorEnvelope error, TimeSpan? retryAfter, RateLimitInfo rateLimit)
        {
            Outcome = outcome;
            Status = status;
            ReasonWire = reasonWire;
            Reason = DiscoveryReasons.Parse(reasonWire);
            Message = message;
            Error = error;
            RetryAfter = retryAfter;
            RateLimit = rateLimit;
        }

        /// <summary>Copies another result's fields (used to turn a failure of one call into the failure of another).</summary>
        protected DiscoveryCallResult(DiscoveryCallResult other)
            : this(other.Outcome, other.Status, other.ReasonWire, other.Message, other.Error, other.RetryAfter, other.RateLimit)
        {
            IdentityChanged = other.IdentityChanged;
        }

        private DiscoveryCallResult(DiscoveryCallResult other, string message)
            : this(other.Outcome, other.Status, other.ReasonWire, message, other.Error, other.RetryAfter, other.RateLimit)
        {
            IdentityChanged = true;
        }

        /// <summary>How the call ended.</summary>
        public DiscoveryOutcome Outcome { get; }

        /// <summary>HTTP status, or 0.</summary>
        public int Status { get; }

        /// <summary>The machine-readable reason, parsed; <see cref="DiscoveryReason.Unknown"/> when absent or new.</summary>
        public DiscoveryReason Reason { get; }

        /// <summary>The reason as its wire string, or null.</summary>
        public string ReasonWire { get; }

        /// <summary>Human-readable explanation; never branch on it.</summary>
        public string Message { get; }

        /// <summary>The parsed error body, or null (success, local refusal, transport failure, or an unparseable body).</summary>
        public ErrorEnvelope Error { get; }

        /// <summary>The <c>Retry-After</c> delay of a 429 (or the time left of one the SDK is still honouring), or null.</summary>
        public TimeSpan? RetryAfter { get; }

        /// <summary>The rate-limit budget the answer reported, or null.</summary>
        public RateLimitInfo RateLimit { get; }

        /// <summary>
        /// True when the call was not resent because the player id changed under it: Discovery rejected the
        /// player token (a 401, so <see cref="Outcome"/> is <see cref="DiscoveryOutcome.Unauthorized"/>), the
        /// SDK got a new token for another player id (a re-issued anonymous token always is one), and the call
        /// reads or ends something the old player id owns (a reservation read or release, a ticket poll or
        /// cancel), which the new player id would only be told is not found. The reservation or ticket is out
        /// of this player's reach now; make a new one.
        /// </summary>
        public bool IdentityChanged { get; }

        /// <summary>True for <see cref="DiscoveryOutcome.Ok"/>.</summary>
        public bool IsOk => Outcome == DiscoveryOutcome.Ok;

        /// <summary>True when the SDK refused before sending anything (status 0, <see cref="DiscoveryOutcome.InvalidRequest"/>).</summary>
        public bool IsLocalRefusal => Outcome == DiscoveryOutcome.InvalidRequest && Status == 0;

        /// <summary>A local refusal: nothing was sent. <paramref name="reason"/> is what the service would answer, or Unknown.</summary>
        public static DiscoveryCallResult Refused(DiscoveryReason reason, string message)
        {
            return new DiscoveryCallResult(DiscoveryOutcome.InvalidRequest, 0, DiscoveryReasons.ToWireValue(reason), message, null, null, null);
        }

        /// <summary>A result with no HTTP exchange behind it (transport failure, cancellation, unsupported platform).</summary>
        public static DiscoveryCallResult Local(DiscoveryOutcome outcome, string message)
        {
            return new DiscoveryCallResult(outcome, 0, null, message, null, null, null);
        }

        /// <summary>The 401 <paramref name="rejected"/>, marked <see cref="IdentityChanged"/>: the call was not resent under the new player id.</summary>
        public static DiscoveryCallResult IdentityChangedFrom(DiscoveryCallResult rejected, string message)
        {
            if (rejected == null)
            {
                throw new ArgumentNullException(nameof(rejected));
            }

            return new DiscoveryCallResult(rejected, message);
        }

        /// <inheritdoc />
        public override string ToString()
        {
            string text = Status > 0 ? Outcome + " " + Status : Outcome.ToString();
            text = ReasonWire != null ? text + " " + ReasonWire : text;
            return IdentityChanged ? text + " identityChanged" : text;
        }
    }

    /// <summary>A <see cref="DiscoveryCallResult"/> that carries a value on success.</summary>
    /// <typeparam name="T">The success value.</typeparam>
    public class DiscoveryResult<T> : DiscoveryCallResult
    {
        /// <summary>Creates a result.</summary>
        public DiscoveryResult(DiscoveryOutcome outcome, int status, string reasonWire, string message, ErrorEnvelope error, TimeSpan? retryAfter, RateLimitInfo rateLimit, T value)
            : base(outcome, status, reasonWire, message, error, retryAfter, rateLimit)
        {
            Value = value;
        }

        private DiscoveryResult(DiscoveryCallResult failure)
            : base(failure)
        {
            Value = default;
        }

        /// <summary>The value on success; default otherwise.</summary>
        public T Value { get; }

        /// <summary>A success.</summary>
        public static DiscoveryResult<T> Success(T value, int status, RateLimitInfo rateLimit)
        {
            return new DiscoveryResult<T>(DiscoveryOutcome.Ok, status, null, null, null, null, rateLimit, value);
        }

        /// <summary>The same outcome, status, reason and headers as <paramref name="failure"/>, with no value.</summary>
        public static DiscoveryResult<T> From(DiscoveryCallResult failure)
        {
            if (failure == null)
            {
                throw new ArgumentNullException(nameof(failure));
            }

            return new DiscoveryResult<T>(failure);
        }

        /// <summary>A local refusal with no value; nothing was sent.</summary>
        public static new DiscoveryResult<T> Refused(DiscoveryReason reason, string message) => From(DiscoveryCallResult.Refused(reason, message));

        /// <summary>A result with no HTTP exchange behind it.</summary>
        public static new DiscoveryResult<T> Local(DiscoveryOutcome outcome, string message) => From(DiscoveryCallResult.Local(outcome, message));
    }
}
