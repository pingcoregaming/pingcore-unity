using System;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using PingCore.Core.Wire;

namespace PingCore.Core.Discovery
{
    /// <summary>
    /// Sends one <see cref="DiscoveryRequest"/> through an <see cref="IHttpTransport"/> and turns
    /// whatever happens into a <see cref="DiscoveryResult{T}"/>: it parses the envelope with
    /// <see cref="PingCoreJson.Settings"/>, classifies the status, and reads <c>Retry-After</c> and
    /// the <c>RateLimit-*</c> headers. It never throws for an HTTP or transport failure, never
    /// retries (retries belong to the caller, through <see cref="RetryGovernor"/>), and never logs
    /// a header, a body or a URL.
    /// </summary>
    public sealed class DiscoveryCaller
    {
        /// <summary>Creates a caller for one Discovery base URL.</summary>
        /// <param name="baseUrl">Absolute <c>http</c> or <c>https</c> URL, for example <c>https://discovery.pingcore.io</c>. A trailing slash is dropped.</param>
        /// <param name="transport">The HTTP seam.</param>
        public DiscoveryCaller(string baseUrl, IHttpTransport transport)
        {
            if (string.IsNullOrEmpty(baseUrl)
                || !Uri.TryCreate(baseUrl, UriKind.Absolute, out Uri parsed)
                || (parsed.Scheme != "https" && parsed.Scheme != "http"))
            {
                throw new ArgumentException("baseUrl must be an absolute http or https URL", nameof(baseUrl));
            }

            BaseUrl = baseUrl.TrimEnd('/');
            Transport = transport ?? throw new ArgumentNullException(nameof(transport));
        }

        /// <summary>The base URL, without a trailing slash.</summary>
        public string BaseUrl { get; }

        /// <summary>The transport.</summary>
        public IHttpTransport Transport { get; }

        /// <summary>Escapes one path segment (a server id can hold <c>:</c>, which must be encoded).</summary>
        public static string Segment(string value) => Uri.EscapeDataString(value ?? string.Empty);

        /// <summary>Sends <paramref name="request"/> and classifies the answer. <typeparamref name="T"/> is the success body.</summary>
        public async Task<DiscoveryResult<T>> SendAsync<T>(DiscoveryRequest request, CancellationToken cancellationToken)
            where T : WireResponse
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return DiscoveryResult<T>.Local(DiscoveryOutcome.Cancelled, "cancelled before sending");
            }

            var http = new PingCoreHttpRequest(request.Method, BaseUrl + request.PathAndQuery, request.Headers(), request.JsonBody);
            PingCoreHttpResponse response;
            try
            {
                response = await Transport.SendAsync(http, cancellationToken);
            }
            catch (Exception e)
            {
                return FromException<T>(e, cancellationToken);
            }

            if (response == null)
            {
                return DiscoveryResult<T>.Local(DiscoveryOutcome.Unexpected, "the transport returned no response");
            }

            return Classify<T>(response);
        }

        /// <summary>Classifies an exception from the transport: cancellation of <paramref name="cancellationToken"/> is Cancelled, anything else Unreachable.</summary>
        public static DiscoveryResult<T> FromException<T>(Exception e, CancellationToken cancellationToken)
        {
            if (e is OperationCanceledException && cancellationToken.IsCancellationRequested)
            {
                return DiscoveryResult<T>.Local(DiscoveryOutcome.Cancelled, "cancelled");
            }

            // A transport exception should name the failure only (its contract), but its message comes
            // from the engine (UnityWebRequest.error), which can quote the URL and with it a ticket id,
            // so URLs and paths are scrubbed out of it. Any other exception is reported by type alone.
            string message = e is PingCoreTransportException ? "transport failure (" + nameof(PingCoreTransportException) + "): " + ScrubTransportMessage(e.Message)
                : e is OperationCanceledException ? "transport failure: timed out"
                : "transport failure: " + e.GetType().Name;
            return DiscoveryResult<T>.Local(DiscoveryOutcome.Unreachable, message);
        }

        /// <summary>
        /// A transport failure message with every URL (<c>scheme://...</c>), every path or path-and-query
        /// (a token starting with <c>/</c>) and every <c>host/...</c> run replaced by <c>&lt;url&gt;</c>, cut to
        /// 200 characters. A request URL can carry a ticket id, a bearer secret. Pure.
        /// </summary>
        public static string ScrubTransportMessage(string message)
        {
            if (string.IsNullOrEmpty(message))
            {
                return "no detail";
            }

            string scrubbed = UrlLike.Replace(message, "<url>");
            return scrubbed.Length > 200 ? scrubbed.Substring(0, 200) : scrubbed;
        }

        private static readonly Regex UrlLike = new Regex(
            @"[A-Za-z][A-Za-z0-9+.\-]*://\S*|(?<![A-Za-z0-9<])/\S*|\b[A-Za-z0-9.\-]+(:\d+)?/\S*",
            RegexOptions.CultureInvariant);

        /// <summary>The outcome an HTTP status maps to. Pure.</summary>
        public static DiscoveryOutcome OutcomeForStatus(int status)
        {
            if (status >= 200 && status <= 299)
            {
                return DiscoveryOutcome.Ok;
            }

            switch (status)
            {
                case 400:
                    return DiscoveryOutcome.InvalidRequest;
                case 401:
                    return DiscoveryOutcome.Unauthorized;
                case 403:
                    return DiscoveryOutcome.Forbidden;
                case 404:
                    return DiscoveryOutcome.NotFound;
                case 409:
                    return DiscoveryOutcome.Conflict;
                case 429:
                    return DiscoveryOutcome.RateLimited;
                case 503:
                    return DiscoveryOutcome.Degraded;
                default:
                    return DiscoveryOutcome.Unexpected;
            }
        }

        /// <summary>Turns one HTTP answer into a result. Pure: no I/O, no clock.</summary>
        public static DiscoveryResult<T> Classify<T>(PingCoreHttpResponse response)
            where T : WireResponse
        {
            if (response == null)
            {
                throw new ArgumentNullException(nameof(response));
            }

            RateLimitInfo rateLimit = RateLimitInfo.FromHeaders(response.Headers);
            TimeSpan? retryAfter = RateLimitInfo.RetryAfterFromHeaders(response.Headers);
            DiscoveryOutcome outcome = OutcomeForStatus(response.Status);

            if (outcome == DiscoveryOutcome.Ok)
            {
                T value = Parse<T>(response.Body);
                if (value == null || value.Error)
                {
                    return new DiscoveryResult<T>(DiscoveryOutcome.Unexpected, response.Status, null, "the success body did not parse as the expected envelope", null, retryAfter, rateLimit, default);
                }

                return new DiscoveryResult<T>(DiscoveryOutcome.Ok, response.Status, null, null, null, retryAfter, rateLimit, value);
            }

            ErrorEnvelope error = Parse<ErrorEnvelope>(response.Body);
            string message = error?.Message ?? ("HTTP " + response.Status);
            return new DiscoveryResult<T>(outcome, response.Status, error?.Reason, message, error, retryAfter, rateLimit, default);
        }

        private static TBody Parse<TBody>(string body)
            where TBody : class
        {
            if (string.IsNullOrEmpty(body))
            {
                return null;
            }

            try
            {
                return JsonConvert.DeserializeObject<TBody>(body, PingCoreJson.Settings);
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }
}
