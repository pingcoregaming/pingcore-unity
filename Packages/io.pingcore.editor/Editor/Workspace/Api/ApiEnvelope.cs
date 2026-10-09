using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PingCore.Core;
using PingCore.Editor.Workspace.Redaction;

namespace PingCore.Editor.Workspace.Api
{
    /// <summary>
    /// The PingCore API envelope rule, pure: an answer succeeds only when its status is 2xx, its
    /// body is a JSON object and its <c>error</c> is exactly <c>false</c>; then its <c>data</c> is
    /// the value. HTTP 200 with <c>error: true</c> (an error sent without a status) is a
    /// failure carrying its message. 401, 403, 404, 409 and 429 map to their kinds whatever the
    /// body says, the 403's brand permission comes from the route table, and 429 reads
    /// <c>Retry-After</c>. A 3xx is refused (never followed). A body that is not the envelope is
    /// <see cref="PluginErrorKind.Envelope"/>. Messages pass the redactor; the body itself is
    /// never put into an error.
    /// </summary>
    internal static class ApiEnvelope
    {
        /// <summary>The longest server message carried into an error.</summary>
        public const int MaxMessageLength = 300;

        /// <summary>The outcome of one classification.</summary>
        public sealed class Outcome
        {
            public Outcome(JToken data, string message, PluginError error)
            {
                Data = data;
                Message = message;
                Error = error;
            }

            /// <summary>The envelope's <c>data</c> on success (may be JSON null).</summary>
            public JToken Data { get; }

            /// <summary>The envelope's redacted <c>message</c> on success.</summary>
            public string Message { get; }

            /// <summary>Null on success.</summary>
            public PluginError Error { get; }

            public bool Ok => Error == null;
        }

        /// <summary>Classifies one answer of <paramref name="route"/>.</summary>
        public static Outcome Classify(PingCoreHttpResponse response, WorkspaceRoute route, Redactor redactor, DateTimeOffset now)
        {
            if (response == null)
            {
                throw new ArgumentNullException(nameof(response));
            }

            redactor = redactor ?? Redactor.PatternsOnly;
            int status = response.Status;
            JObject envelope = TryParseObject(response.Body);
            string serverMessage = envelope == null ? null : Clip(redactor.Redact(envelope["message"]?.Type == JTokenType.String ? (string)envelope["message"] : null));
            string reason = envelope?["data"] is JObject data && data["reason"]?.Type == JTokenType.String ? Clip(redactor.Redact((string)data["reason"])) : null;

            if (status >= 300 && status < 400)
            {
                return Fail(route, PluginErrorKind.Transport, status, $"The workspace answered with a redirect (HTTP {status}); the plugin never follows one, so the key is never sent elsewhere.", "The plugin calls only https://app.pingcore.io/api (or the apiBaseOverride in UserSettings/PingCoreEditorUser.json).", reason);
            }

            switch (status)
            {
                case 401:
                    return Fail(route, PluginErrorKind.NotSignedIn, status, serverMessage ?? "The workspace did not accept the API key.", "Sign in again on " + PingCoreMenu.SignInText + " with a valid usr_ key.", reason);
                case 403:
                    PluginError forbidden = new PluginError(route.Step, PluginErrorKind.MissingBrandPermission,
                        serverMessage ?? $"The workspace refused {route.Method} {route.Template}.",
                        route.Permission == null ? null : $"The brand member needs the {route.Permission} brand permission.")
                    {
                        HttpStatus = status,
                        Permission = route.Permission,
                        Reason = reason,
                    };
                    return new Outcome(null, null, forbidden);
                case 404:
                    return Fail(route, PluginErrorKind.NotFound, status, serverMessage ?? "Not found.", null, reason);
                case 409:
                    return Fail(route, PluginErrorKind.Conflict, status, serverMessage ?? "The workspace reported a conflict.", null, reason);
                case 429:
                    PluginError limited = new PluginError(route.Step, PluginErrorKind.RateLimited, serverMessage ?? "The workspace is rate limiting these calls.", "Wait and try again.")
                    {
                        HttpStatus = status,
                        RetryAfter = RetryAfter(response.Headers, now),
                        Reason = reason,
                    };
                    return new Outcome(null, null, limited);
            }

            if (envelope == null)
            {
                return Fail(route, PluginErrorKind.Envelope, status, $"The workspace answered HTTP {status} without the JSON envelope.", null, null);
            }

            JToken error = envelope["error"];
            if (error == null || error.Type != JTokenType.Boolean)
            {
                return Fail(route, PluginErrorKind.Envelope, status, $"The workspace answered HTTP {status} without a boolean error field.", null, null);
            }

            if ((bool)error)
            {
                return Fail(route, PluginErrorKind.Rejected, status, serverMessage ?? $"The workspace refused {route.Method} {route.Template}.", null, reason);
            }

            if (status < 200 || status >= 300)
            {
                return Fail(route, PluginErrorKind.Envelope, status, $"The workspace answered HTTP {status} with error: false.", null, null);
            }

            return new Outcome(envelope["data"] ?? JValue.CreateNull(), serverMessage, null);
        }

        /// <summary>
        /// <c>Retry-After</c> as delta seconds or an HTTP date, clamped to 0..3600 s; null when
        /// absent or unreadable.
        /// </summary>
        public static TimeSpan? RetryAfter(IReadOnlyDictionary<string, string> headers, DateTimeOffset now)
        {
            if (headers == null)
            {
                return null;
            }

            string value = null;
            foreach (KeyValuePair<string, string> header in headers)
            {
                if (string.Equals(header.Key, "Retry-After", StringComparison.OrdinalIgnoreCase))
                {
                    value = header.Value?.Trim();
                }
            }

            if (string.IsNullOrEmpty(value))
            {
                return null;
            }

            TimeSpan wait;
            if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int seconds))
            {
                wait = TimeSpan.FromSeconds(seconds);
            }
            else if (DateTimeOffset.TryParseExact(value, "r", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset at))
            {
                wait = at - now;
            }
            else
            {
                return null;
            }

            if (wait < TimeSpan.Zero)
            {
                wait = TimeSpan.Zero;
            }

            return wait > TimeSpan.FromHours(1) ? TimeSpan.FromHours(1) : wait;
        }

        private static Outcome Fail(WorkspaceRoute route, PluginErrorKind kind, int status, string message, string hint, string reason)
        {
            return new Outcome(null, null, new PluginError(route.Step, kind, message, hint) { HttpStatus = status, Reason = reason });
        }

        private static JObject TryParseObject(string body)
        {
            if (string.IsNullOrWhiteSpace(body))
            {
                return null;
            }

            try
            {
                using (var reader = new JsonTextReader(new StringReader(body)) { DateParseHandling = DateParseHandling.None, FloatParseHandling = FloatParseHandling.Double, MaxDepth = 64 })
                {
                    JToken token = JToken.ReadFrom(reader);
                    return token as JObject;
                }
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static string Clip(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return null;
            }

            return text.Length <= MaxMessageLength ? text : text.Substring(0, MaxMessageLength) + "...";
        }
    }
}
