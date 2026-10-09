using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace PingCore.Editor.Workspace.Credentials
{
    /// <summary>
    /// The names credentials are stored under. Pure. One API host per name, so a development
    /// override never shares a key with <c>app.pingcore.io</c>:
    /// <list type="bullet">
    /// <item><c>PingCore/&lt;host&gt;/usr</c>: the brand member's API key.</item>
    /// <item><c>PingCore/&lt;host&gt;/cdnpush/&lt;sourceId&gt;</c>: a CDN source's push token.</item>
    /// </list>
    /// The <c>EditorPrefs</c> fallback stores the same names with dots for slashes
    /// (<c>PingCore.&lt;host&gt;.usr</c>).
    /// </summary>
    public static class CredentialTargets
    {
        /// <summary>The prefix of every target.</summary>
        public const string Prefix = "PingCore/";

        private static readonly Regex HostPattern = new Regex(
            "^(?=.{1,253}$)[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?(?:\\.[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?)*(?::[0-9]{1,5})?$",
            RegexOptions.CultureInvariant);

        /// <summary>The target of the brand member's <c>usr_</c> key for <paramref name="host"/>.</summary>
        public static string UserKey(string host) => Prefix + CheckedHost(host) + "/usr";

        /// <summary>The target of the push token of CDN source <paramref name="sourceId"/>.</summary>
        public static string PushToken(string host, long sourceId) => Prefix + CheckedHost(host) + "/cdnpush/" + CheckedId(sourceId, nameof(sourceId));

        /// <summary>The <c>EditorPrefs</c> key for a target: slashes become dots (<c>PingCore.&lt;host&gt;.usr</c>).</summary>
        public static string EditorPrefsKey(string target)
        {
            if (!IsTarget(target))
            {
                throw new ArgumentException("Not a PingCore credential target.", nameof(target));
            }

            return target.Replace('/', '.');
        }

        /// <summary>True for a name this class produces.</summary>
        public static bool IsTarget(string target)
        {
            if (string.IsNullOrEmpty(target) || !target.StartsWith(Prefix, StringComparison.Ordinal))
            {
                return false;
            }

            string[] parts = target.Substring(Prefix.Length).Split('/');
            if (parts.Length < 2 || !IsValidHost(parts[0]))
            {
                return false;
            }

            switch (parts[1])
            {
                case "usr":
                    return parts.Length == 2;
                case "cdnpush":
                    return parts.Length == 3 && IsPositiveId(parts[2]);
                default:
                    // Test-only targets (the Windows store round trip) live under PingCore/<host>/test/<name>.
                    return parts[1] == "test" && parts.Length == 3 && Regex.IsMatch(parts[2], "^[A-Za-z0-9-]{1,64}$");
            }
        }

        private static bool IsPositiveId(string text) => long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out long id) && id > 0;

        /// <summary>A lower-case DNS host with an optional port.</summary>
        public static bool IsValidHost(string host) => host != null && HostPattern.IsMatch(host);

        /// <summary>A test-only target under <c>PingCore/&lt;host&gt;/test/&lt;name&gt;</c>; tests delete it in teardown.</summary>
        public static string TestTarget(string host, string name) => Prefix + CheckedHost(host) + "/test/" + name;

        private static string CheckedHost(string host)
        {
            if (!IsValidHost(host))
            {
                throw new ArgumentException("The workspace host must be a lower-case DNS name.", nameof(host));
            }

            return host;
        }

        private static string CheckedId(long id, string name)
        {
            if (id <= 0)
            {
                throw new ArgumentOutOfRangeException(name, "Ids are positive.");
            }

            return id.ToString(CultureInfo.InvariantCulture);
        }
    }
}
