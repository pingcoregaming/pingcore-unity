using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace PingCore.Core.Discovery
{
    /// <summary>
    /// One Discovery request, built once and resent unchanged: a retry or the resend after a
    /// player-token re-issue carries the same body, so the same idempotency key, reservation id
    /// or ticket id. Only the bearer can be swapped (<see cref="WithBearer"/>).
    /// <see cref="ToString"/> is the label (method and path template), never the path or the token.
    /// </summary>
    public sealed class DiscoveryRequest
    {
        private DiscoveryRequest(string method, string pathAndQuery, string jsonBody, string bearerToken, string label)
        {
            Method = method;
            PathAndQuery = pathAndQuery;
            JsonBody = jsonBody;
            BearerToken = bearerToken;
            Label = label;
        }

        /// <summary>HTTP method in upper case.</summary>
        public string Method { get; }

        /// <summary>Path below the base URL, starting with <c>/</c>, with any query string. Segments are already escaped. It can hold a ticket id: never log it.</summary>
        public string PathAndQuery { get; }

        /// <summary>The serialized JSON body, or null. Never log it.</summary>
        public string JsonBody { get; }

        /// <summary>The bearer token, or null for an unauthenticated route. Never log it.</summary>
        public string BearerToken { get; }

        /// <summary>True when the request carries a bearer.</summary>
        public bool HasBearer => !string.IsNullOrEmpty(BearerToken);

        /// <summary>A loggable name for the call, for example <c>POST /v1/apps/{publicId}/tickets</c>.</summary>
        public string Label { get; }

        /// <summary>A request with no body.</summary>
        /// <param name="method">HTTP method.</param>
        /// <param name="pathAndQuery">Path and query, segments already escaped (see <see cref="DiscoveryCaller.Segment"/>).</param>
        /// <param name="label">The path template, for logs.</param>
        public static DiscoveryRequest Create(string method, string pathAndQuery, string label)
        {
            return Build(method, pathAndQuery, null, label);
        }

        /// <summary>A request whose body is <paramref name="body"/> serialized with <see cref="PingCoreJson.Settings"/>.</summary>
        public static DiscoveryRequest Json(string method, string pathAndQuery, object body, string label)
        {
            if (body == null)
            {
                throw new ArgumentNullException(nameof(body));
            }

            return Build(method, pathAndQuery, JsonConvert.SerializeObject(body, PingCoreJson.Settings), label);
        }

        /// <summary>The same request with <paramref name="bearerToken"/> as its bearer (null removes it).</summary>
        public DiscoveryRequest WithBearer(string bearerToken)
        {
            return new DiscoveryRequest(Method, PathAndQuery, JsonBody, bearerToken, Label);
        }

        /// <summary>The headers this request sends. Never log them.</summary>
        public IReadOnlyDictionary<string, string> Headers()
        {
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Accept"] = "application/json",
            };
            if (HasBearer)
            {
                headers["Authorization"] = "Bearer " + BearerToken;
            }

            return headers;
        }

        /// <inheritdoc />
        public override string ToString() => Label;

        private static DiscoveryRequest Build(string method, string pathAndQuery, string jsonBody, string label)
        {
            if (string.IsNullOrEmpty(method))
            {
                throw new ArgumentException("method is required", nameof(method));
            }

            if (string.IsNullOrEmpty(pathAndQuery) || pathAndQuery[0] != '/')
            {
                throw new ArgumentException("pathAndQuery must start with /", nameof(pathAndQuery));
            }

            string upper = method.ToUpperInvariant();
            return new DiscoveryRequest(upper, pathAndQuery, jsonBody, null, string.IsNullOrEmpty(label) ? upper : label);
        }
    }
}
