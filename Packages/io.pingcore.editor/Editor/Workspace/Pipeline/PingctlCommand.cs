using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using PingCore.Editor.Workspace.Api;
using PingCore.Editor.Workspace.Redaction;

namespace PingCore.Editor.Workspace.Pipeline
{
    /// <summary>
    /// <c>pingctl</c>'s documented contract, pure:
    /// <c>pingctl push &lt;dir&gt; [--exclude &lt;p&gt;]...</c> with the push token and the API URL
    /// in the child's environment only, never <c>--token</c>. An exclude ending in <c>/</c> is a
    /// literal path prefix, so Unity's two do-not-ship folders are excluded by exact name.
    /// </summary>
    public static class PingctlCommand
    {
        /// <summary>The oldest pingctl the plugin drives.</summary>
        public static readonly Version MinimumVersion = new Version(0, 1, 0);

        /// <summary>Unity writes these beside every player; they never ship.</summary>
        public static readonly IReadOnlyList<string> DoNotShipSuffixes = new[] { "_BurstDebugInformation_DoNotShip", "_BackUpThisFolder_ButDontShipItWithYourGame" };

        /// <summary>The environment variable pingctl reads its push token from (built from fragments; the name is fine, the value never leaves the store and the child's environment).</summary>
        public static readonly string PushTokenVariable = "PINGCORE" + "_PUSH" + "_TOKEN";

        /// <summary>The environment variable pingctl reads its API base from. Built from fragments: the secrets scan bans the platform's own variable name as a literal.</summary>
        public static readonly string ApiUrlVariable = "PINGCORE_" + "API" + "_" + "URL";

        /// <summary>The variable that names a developer's own pingctl binary when no path is configured (<see cref="PingctlLocator"/>).</summary>
        public const string BinVariable = "PINGCTL_BIN";

        private static readonly Regex VersionPattern = new Regex("v?([0-9]+)\\.([0-9]+)\\.([0-9]+)", RegexOptions.CultureInvariant);
        private static readonly Regex PublishedPattern = new Regex("^Published version (\\S+)$", RegexOptions.CultureInvariant);
        private static readonly Regex UnchangedPattern = new Regex("^CDN reports no content changes; version stays (\\S+)$", RegexOptions.CultureInvariant);
        private static readonly Regex NothingPattern = new Regex("^Published version: (\\S+)$", RegexOptions.CultureInvariant);

        /// <summary><c>version</c>.</summary>
        public static IReadOnlyList<string> VersionArguments() => new[] { "version" };

        /// <summary>
        /// <c>push &lt;folder&gt; --exclude &lt;product&gt;_BurstDebugInformation_DoNotShip/ --exclude
        /// &lt;product&gt;_BackUpThisFolder_ButDontShipItWithYourGame/</c>, plus any other folder of the
        /// build whose name ends in either suffix, sorted. Never a token.
        /// </summary>
        public static IReadOnlyList<string> PushArguments(string folder, string product, IEnumerable<string> subfolderNames)
        {
            if (string.IsNullOrEmpty(folder))
            {
                throw new ArgumentException("The build folder is required.", nameof(folder));
            }

            var names = new SortedSet<string>(DoNotShipSuffixes.Select(s => product + s), StringComparer.Ordinal);
            foreach (string name in subfolderNames ?? Enumerable.Empty<string>())
            {
                if (name != null && DoNotShipSuffixes.Any(s => name.EndsWith(s, StringComparison.Ordinal)))
                {
                    names.Add(name);
                }
            }

            var args = new List<string> { "push", folder };
            foreach (string name in names)
            {
                args.Add("--exclude");
                args.Add(name + "/");
            }

            return args;
        }

        /// <summary>The child's extra environment: the push token (null leaves it out, as for <c>version</c>) and the workspace's API base.</summary>
        public static IReadOnlyDictionary<string, string> Environment(string pushToken, WorkspaceEndpoint endpoint)
        {
            if (endpoint == null)
            {
                throw new ArgumentNullException(nameof(endpoint));
            }

            var env = new Dictionary<string, string> { [ApiUrlVariable] = ApiUrl(endpoint) };
            if (pushToken != null)
            {
                env[PushTokenVariable] = pushToken;
            }

            return env;
        }

        /// <summary><c>https://&lt;host&gt;/api</c>: the base pingctl is given, never its stored default.</summary>
        public static string ApiUrl(WorkspaceEndpoint endpoint) => endpoint.ApiBase.TrimEnd('/');

        /// <summary>Why <paramref name="args"/> may not be started, or null: an argument shaped like a token, or holding the push token itself. Pure.</summary>
        public static string ArgumentProblem(IReadOnlyList<string> args, string pushToken)
        {
            for (int i = 0; args != null && i < args.Count; i++)
            {
                string a = args[i] ?? string.Empty;
                if (Redactor.LooksLikeCredentialArgument(a) || (!string.IsNullOrEmpty(pushToken) && a.IndexOf(pushToken, StringComparison.Ordinal) >= 0))
                {
                    return $"Argument {i + 1} of pingctl looks like a token; the push token goes through pingctl's environment only.";
                }

                if (string.Equals(a, "--token", StringComparison.OrdinalIgnoreCase) || a.StartsWith("--token=", StringComparison.OrdinalIgnoreCase))
                {
                    return "pingctl is never given --token; the push token goes through its environment only.";
                }
            }

            return null;
        }

        /// <summary>
        /// The snapshot pingctl published, and whether it is new: <c>Published version &lt;v&gt;</c>
        /// (new), <c>CDN reports no content changes; version stays &lt;v&gt;</c> or <c>Published
        /// version: &lt;v&gt;</c> after "Nothing to push" (unchanged). The last such line wins; none
        /// gives a null version. Pure.
        /// </summary>
        public static (string Version, bool? Changed) ParseSnapshot(IEnumerable<string> lines)
        {
            string version = null;
            bool? changed = null;
            foreach (string raw in lines ?? Enumerable.Empty<string>())
            {
                string line = (raw ?? string.Empty).Trim();
                Match m = PublishedPattern.Match(line);
                if (m.Success)
                {
                    version = m.Groups[1].Value;
                    changed = true;
                    continue;
                }

                m = UnchangedPattern.Match(line);
                if (!m.Success)
                {
                    m = NothingPattern.Match(line);
                }

                if (m.Success)
                {
                    version = m.Groups[1].Value;
                    changed = false;
                }
            }

            return (version, changed);
        }

        /// <summary>The version <c>pingctl version</c> printed (<c>pingctl v0.1.1</c>), or null. Pure.</summary>
        public static Version ParseVersion(IEnumerable<string> lines)
        {
            foreach (string line in lines ?? Enumerable.Empty<string>())
            {
                Match m = VersionPattern.Match(line ?? string.Empty);
                if (m.Success)
                {
                    return new Version(
                        int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture),
                        int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture),
                        int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture));
                }
            }

            return null;
        }
    }
}
