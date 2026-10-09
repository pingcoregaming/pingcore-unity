using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace PingCore.Editor.Workspace.Pipeline
{
    /// <summary>One pinned binary of the bundle.</summary>
    public sealed class PingctlBundleEntry
    {
        public PingctlBundleEntry(string platform, string file, string sha256)
        {
            Platform = platform;
            File = file;
            Sha256 = sha256;
        }

        /// <summary><c>windows-amd64</c>, <c>macos-arm64</c>, <c>macos-amd64</c> or <c>linux-amd64</c>.</summary>
        public string Platform { get; }

        /// <summary>The file name inside <c>Tools~/pingctl/&lt;platform&gt;/</c> (<c>pingctl.exe</c> or <c>pingctl</c>).</summary>
        public string File { get; }

        /// <summary>The SHA-256 of the binary, lower-case hex.</summary>
        public string Sha256 { get; }
    }

    /// <summary>The pinned manifest of the bundled binaries, <c>Tools~/pingctl/manifest.json</c>.</summary>
    public sealed class PingctlBundleManifest
    {
        public PingctlBundleManifest(string version, IReadOnlyDictionary<string, PingctlBundleEntry> binaries)
        {
            Version = version;
            Binaries = binaries;
        }

        /// <summary>The one pingctl version every binary is (<c>0.1.1</c>).</summary>
        public string Version { get; }

        public IReadOnlyDictionary<string, PingctlBundleEntry> Binaries { get; }
    }

    /// <summary>
    /// The <c>pingctl</c> the Editor package carries: one pinned version for Windows, macOS (arm64 and amd64) and
    /// Linux (amd64) under <c>Tools~/pingctl/&lt;os-arch&gt;/</c>, which Unity does not import, with
    /// <c>Tools~/pingctl/manifest.json</c> naming the version and each binary's SHA-256. The binary's digest is
    /// checked against the manifest before every use, and a mismatch is refused, never run; the push then checks and
    /// runs a private, held copy (<see cref="PingctlRunCopy"/>). The manifest sits beside the binaries, so this catches
    /// a corrupt, partial or stale install, not a deliberate swap by someone who can write to the package folder (they
    /// could rewrite the manifest too). CI and scripts run the same binary from the package folder. Pure apart from
    /// <see cref="Here"/> and <see cref="Sha256Of"/>.
    /// </summary>
    public static class PingctlBundle
    {
        /// <summary>The manifest's format marker.</summary>
        public const string Format = "pingcore-pingctl-bundle/1";

        /// <summary>The bundle's folder inside the package.</summary>
        public const string RelativeFolder = "Tools~/pingctl";

        /// <summary>The manifest's file name inside <see cref="RelativeFolder"/>.</summary>
        public const string ManifestName = "manifest.json";

        private static readonly Regex HexDigest = new Regex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant);
        private static readonly Regex VersionPattern = new Regex("^[0-9]+\\.[0-9]+\\.[0-9]+$", RegexOptions.CultureInvariant);

        /// <summary>The platforms the bundle carries.</summary>
        public static IReadOnlyList<string> Platforms { get; } = new[] { "windows-amd64", "macos-arm64", "macos-amd64", "linux-amd64" };

        /// <summary>
        /// The bundle's platform for an Editor on <paramref name="os"/> (<c>windows</c>, <c>macos</c> or <c>linux</c>)
        /// and <paramref name="architecture"/>, or null when the bundle has none (the locator then asks for your own pingctl). Pure.
        /// </summary>
        public static string PlatformKey(string os, Architecture architecture)
        {
            string arch = architecture == Architecture.X64 ? "amd64" : architecture == Architecture.Arm64 ? "arm64" : null;
            string key = os == null || arch == null ? null : os + "-" + arch;
            return key != null && ((IList<string>)Platforms).Contains(key) ? key : null;
        }

        /// <summary>The platform of this Editor, or null.</summary>
        public static string PlatformHere()
        {
            string os = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "windows"
                : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "macos"
                : RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? "linux"
                : null;
            return PlatformKey(os, RuntimeInformation.OSArchitecture);
        }

        /// <summary>Reads a manifest, or null with <paramref name="problem"/>. Pure.</summary>
        public static PingctlBundleManifest Parse(string json, out string problem)
        {
            problem = null;
            JObject parsed;
            try
            {
                using (var reader = new JsonTextReader(new StringReader(json ?? string.Empty)) { DateParseHandling = DateParseHandling.None, MaxDepth = 8 })
                {
                    parsed = JObject.Load(reader);
                }
            }
            catch (JsonException e)
            {
                problem = $"the bundled pingctl's {ManifestName} is not valid JSON ({e.GetType().Name})";
                return null;
            }

            if (parsed["format"]?.Type != JTokenType.String || (string)parsed["format"] != Format)
            {
                problem = $"the bundled pingctl's {ManifestName} is not in format {Format}";
                return null;
            }

            string version = parsed["version"]?.Type == JTokenType.String ? (string)parsed["version"] : null;
            if (version == null || !VersionPattern.IsMatch(version))
            {
                problem = $"the bundled pingctl's {ManifestName} names no x.y.z version";
                return null;
            }

            var binaries = new Dictionary<string, PingctlBundleEntry>(StringComparer.Ordinal);
            if (!(parsed["binaries"] is JObject list))
            {
                problem = $"the bundled pingctl's {ManifestName} lists no binaries";
                return null;
            }

            foreach (JProperty entry in list.Properties())
            {
                JObject fields = entry.Value as JObject;
                string file = fields?["file"]?.Type == JTokenType.String ? (string)fields["file"] : null;
                string sha = fields?["sha256"]?.Type == JTokenType.String ? (string)fields["sha256"] : null;
                if (!((IList<string>)Platforms).Contains(entry.Name) || (file != "pingctl" && file != "pingctl.exe") || sha == null || !HexDigest.IsMatch(sha))
                {
                    problem = $"the bundled pingctl's {ManifestName} has a bad entry for {entry.Name}";
                    return null;
                }

                binaries[entry.Name] = new PingctlBundleEntry(entry.Name, file, sha);
            }

            return new PingctlBundleManifest(version, binaries);
        }

        /// <summary>
        /// Null when <paramref name="actualSha256"/> is the manifest's digest for the entry; else the sentence that
        /// refuses the binary. Pure.
        /// </summary>
        public static string DigestProblem(PingctlBundleEntry entry, string actualSha256)
        {
            if (entry == null)
            {
                return "the bundled pingctl has no manifest entry";
            }

            return string.Equals(entry.Sha256, actualSha256, StringComparison.Ordinal)
                ? null
                : $"the bundled pingctl for {entry.Platform} does not match its pinned SHA-256, so it is not run. Reinstall the PingCore Editor package, or set your own pingctl";
        }

        /// <summary>The SHA-256 of a file, lower-case hex.</summary>
        public static string Sha256Of(string path)
        {
            using (var sha = SHA256.Create())
            using (FileStream stream = System.IO.File.OpenRead(path))
            {
                byte[] digest = sha.ComputeHash(stream);
                var hex = new StringBuilder(digest.Length * 2);
                foreach (byte b in digest)
                {
                    hex.Append(b.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
                }

                return hex.ToString();
            }
        }

        /// <summary>The bundle folder of the installed Editor package, or null when it cannot be found.</summary>
        public static string Here()
        {
            UnityEditor.PackageManager.PackageInfo info = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(PingctlBundle).Assembly);
            return info == null || string.IsNullOrEmpty(info.resolvedPath) ? null : Path.Combine(info.resolvedPath, "Tools~", "pingctl");
        }
    }
}
