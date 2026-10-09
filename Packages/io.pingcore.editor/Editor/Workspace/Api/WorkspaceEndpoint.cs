using System;
using PingCore.Editor.Workspace.Credentials;

namespace PingCore.Editor.Workspace.Api
{
    /// <summary>
    /// The PingCore API base, <c>https://&lt;host&gt;/api/</c>. The plugin calls
    /// <see cref="DefaultApiBase"/> (<c>https://app.pingcore.io/api</c>): the API resolves the
    /// workspace from the brand member's key, so there is no workspace URL to type. A developer of
    /// the platform may point one project elsewhere with <c>apiBaseOverride</c> in
    /// <c>UserSettings/PingCoreEditorUser.json</c> (never a UI field), validated https-only: no
    /// user info, no query, no fragment and no path but <c>/</c> or <c>/api</c>
    /// (<see cref="Resolve"/>).
    /// </summary>
    public sealed class WorkspaceEndpoint
    {
        /// <summary>The API base every project uses unless its user settings override it.</summary>
        public const string DefaultApiBase = "https://app.pingcore.io/api";

        /// <summary>The host of <see cref="DefaultApiBase"/>.</summary>
        public const string DefaultHost = "app.pingcore.io";

        /// <summary>The endpoint of <see cref="DefaultApiBase"/>.</summary>
        public static WorkspaceEndpoint Default { get; } = new WorkspaceEndpoint(DefaultHost);

        /// <summary>
        /// The endpoint a project uses: <see cref="Default"/> when <paramref name="apiBaseOverride"/>
        /// is blank, else the override. An override that is not an https API base is refused with a
        /// sentence (never a silent fall back to the default), and the caller calls nothing.
        /// </summary>
        public static bool Resolve(string apiBaseOverride, out WorkspaceEndpoint endpoint, out string problem)
        {
            if (string.IsNullOrWhiteSpace(apiBaseOverride))
            {
                endpoint = Default;
                problem = null;
                return true;
            }

            if (TryParse(apiBaseOverride, out endpoint, out string why))
            {
                problem = null;
                return true;
            }

            endpoint = null;
            problem = "The apiBaseOverride in UserSettings/PingCoreEditorUser.json is refused: " + why + " Remove it to use " + DefaultApiBase + ".";
            return false;
        }

        /// <summary>True when this is not <see cref="Default"/> (a development override is in use).</summary>
        public bool IsOverride => Host != DefaultHost;

        private WorkspaceEndpoint(string host)
        {
            Host = host;
            ApiBase = "https://" + host + "/api/";
        }

        /// <summary>The lower-case host (and port, if any), also the credential target's host.</summary>
        public string Host { get; }

        /// <summary><c>https://&lt;host&gt;/api/</c>, ending in a slash.</summary>
        public string ApiBase { get; }

        /// <summary>The absolute URL of a route path (no leading slash), for example <c>fleets/42/live</c>.</summary>
        public string UrlFor(string routePath)
        {
            if (string.IsNullOrEmpty(routePath) || routePath.StartsWith("/", StringComparison.Ordinal) || routePath.Contains("..") || routePath.Contains("?") || routePath.Contains("#"))
            {
                throw new ArgumentException("A route path is relative, without a query or fragment.", nameof(routePath));
            }

            return ApiBase + routePath;
        }

        /// <summary>
        /// Parses an API base: <c>https://api.example.test</c>, <c>https://api.example.test/api</c>
        /// or the bare host. Returns false with a sentence when it is not an https API base (http
        /// is always refused).
        /// </summary>
        public static bool TryParse(string input, out WorkspaceEndpoint endpoint, out string problem)
        {
            endpoint = null;
            problem = null;
            string text = (input ?? string.Empty).Trim();
            if (text.Length == 0)
            {
                problem = "The API base is empty.";
                return false;
            }

            if (!text.Contains("://"))
            {
                text = "https://" + text;
            }

            if (!Uri.TryCreate(text, UriKind.Absolute, out Uri uri))
            {
                problem = "The API base is not a valid URL.";
                return false;
            }

            if (!string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase))
            {
                problem = "The API base must use https; the key is never sent over plain http.";
                return false;
            }

            if (!string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            {
                problem = "The API base must be the host only, without a user name, query or fragment.";
                return false;
            }

            string path = uri.AbsolutePath.TrimEnd('/');
            if (path.Length != 0 && !string.Equals(path, "/api", StringComparison.OrdinalIgnoreCase))
            {
                problem = "The API base must be the host only (optionally ending in /api).";
                return false;
            }

            string host = uri.IsDefaultPort ? uri.Host.ToLowerInvariant() : uri.Host.ToLowerInvariant() + ":" + uri.Port;
            if (!CredentialTargets.IsValidHost(host))
            {
                problem = "The API host is not a valid DNS name.";
                return false;
            }

            endpoint = new WorkspaceEndpoint(host);
            return true;
        }

        /// <summary>The endpoint for an already validated host; throws when it is not one.</summary>
        public static WorkspaceEndpoint ForHost(string host)
        {
            if (!TryParse(host, out WorkspaceEndpoint endpoint, out string problem) || endpoint.Host != host)
            {
                throw new ArgumentException(problem ?? "Not a bare host.", nameof(host));
            }

            return endpoint;
        }
    }
}
