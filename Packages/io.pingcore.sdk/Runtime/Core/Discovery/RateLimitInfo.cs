using System;
using System.Collections.Generic;
using System.Globalization;

namespace PingCore.Core.Discovery
{
    /// <summary>
    /// The budget Discovery reports on a rate-limited route, from its <c>RateLimit-Limit</c>,
    /// <c>RateLimit-Remaining</c>, <c>RateLimit-Reset</c> and <c>RateLimit-Policy</c> headers
    /// (the Discovery spec, <c>contracts/discovery/</c>).
    /// </summary>
    public sealed class RateLimitInfo
    {
        /// <summary>Creates the info from parsed values.</summary>
        public RateLimitInfo(int? limit, int? remaining, int? resetSeconds, string policy)
        {
            Limit = limit;
            Remaining = remaining;
            ResetSeconds = resetSeconds;
            Policy = policy;
        }

        /// <summary><c>RateLimit-Limit</c>: requests allowed in the window.</summary>
        public int? Limit { get; }

        /// <summary><c>RateLimit-Remaining</c>: requests left in the window.</summary>
        public int? Remaining { get; }

        /// <summary><c>RateLimit-Reset</c>: seconds until the window resets.</summary>
        public int? ResetSeconds { get; }

        /// <summary><c>RateLimit-Policy</c>, for example <c>60;w=60</c>.</summary>
        public string Policy { get; }

        /// <summary>
        /// Reads the four headers (names compared case-insensitively). Returns null when none
        /// of them is present; a value that is not a non-negative integer is left null.
        /// </summary>
        public static RateLimitInfo FromHeaders(IReadOnlyDictionary<string, string> headers)
        {
            if (headers == null)
            {
                return null;
            }

            string limit = Header(headers, "RateLimit-Limit");
            string remaining = Header(headers, "RateLimit-Remaining");
            string reset = Header(headers, "RateLimit-Reset");
            string policy = Header(headers, "RateLimit-Policy");
            if (limit == null && remaining == null && reset == null && policy == null)
            {
                return null;
            }

            return new RateLimitInfo(NonNegative(limit), NonNegative(remaining), NonNegative(reset), policy);
        }

        /// <summary>
        /// <c>Retry-After</c> as a delay. Discovery always sends whole seconds; an HTTP-date or
        /// anything else reads as null.
        /// </summary>
        public static TimeSpan? RetryAfterFromHeaders(IReadOnlyDictionary<string, string> headers)
        {
            int? seconds = NonNegative(headers == null ? null : Header(headers, "Retry-After"));
            return seconds.HasValue ? TimeSpan.FromSeconds(seconds.Value) : (TimeSpan?)null;
        }

        /// <summary>A header value by case-insensitive name, or null.</summary>
        internal static string Header(IReadOnlyDictionary<string, string> headers, string name)
        {
            if (headers.TryGetValue(name, out string exact))
            {
                return exact;
            }

            foreach (KeyValuePair<string, string> header in headers)
            {
                if (string.Equals(header.Key, name, StringComparison.OrdinalIgnoreCase))
                {
                    return header.Value;
                }
            }

            return null;
        }

        private static int? NonNegative(string raw)
        {
            if (raw == null)
            {
                return null;
            }

            return int.TryParse(raw.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int value) ? value : (int?)null;
        }

        /// <inheritdoc />
        public override string ToString() => $"RateLimit(limit={Limit}, remaining={Remaining}, reset={ResetSeconds}s)";
    }
}
