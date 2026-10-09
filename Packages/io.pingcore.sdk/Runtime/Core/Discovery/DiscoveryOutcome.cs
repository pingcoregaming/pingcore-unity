namespace PingCore.Core.Discovery
{
    /// <summary>How one Discovery call ended. No SDK call throws for an HTTP or transport failure; it returns one of these.</summary>
    public enum DiscoveryOutcome
    {
        /// <summary>2xx with a well-formed <c>error: false</c> body.</summary>
        Ok = 0,

        /// <summary>400. With <see cref="DiscoveryCallResult.Status"/> 0 the SDK refused locally and sent nothing.</summary>
        InvalidRequest = 1,

        /// <summary>401: the credential is unknown, malformed or (player tokens) rejected with a reason.</summary>
        Unauthorized = 2,

        /// <summary>403: the credential is known but not usable here (for example <c>anonymous_tokens_disabled</c>).</summary>
        Forbidden = 3,

        /// <summary>404: unknown app, or the record is gone (expired, never existed, or not yours).</summary>
        NotFound = 4,

        /// <summary>409: a conflict the body's <c>reason</c> names (<c>no_seats</c>, <c>too_many_tickets</c>, ...).</summary>
        Conflict = 5,

        /// <summary>429: rate limited; <see cref="DiscoveryCallResult.RetryAfter"/> says when to try again.</summary>
        RateLimited = 6,

        /// <summary>503: the service is degraded. The credential is fine.</summary>
        Degraded = 7,

        /// <summary>Any other status, or a body that does not parse as the expected envelope.</summary>
        Unexpected = 8,

        /// <summary>No answer: connection failure, timeout or reset.</summary>
        Unreachable = 9,

        /// <summary>The caller's <c>CancellationToken</c> was cancelled.</summary>
        Cancelled = 10,

        /// <summary>The platform cannot do this (for example the latency probe where <c>LatencyProbe.IsSupported</c> is false).</summary>
        Unsupported = 11,
    }
}
