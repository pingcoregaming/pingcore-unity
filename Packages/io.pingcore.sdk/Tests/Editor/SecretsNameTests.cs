using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;

namespace PingCore.Sdk.Tests.Editor
{
    /// <summary>
    /// The secrets rule: SDK, Editor and sample code never read the platform's own environment
    /// variables or files, named below. This scans every <c>.cs</c>,
    /// <c>.asmdef</c> and <c>.json</c> under the two packages and <c>SampleGame/Assets/</c> for
    /// those names. The needles are assembled from fragments, so this file is scanned like any
    /// other and does not flag itself.
    /// </summary>
    public sealed class SecretsNameTests
    {
        private static readonly string[] ScannedExtensions = { ".cs", ".asmdef", ".json" };

        /// <summary>Names the game process must never read, built so no needle appears whole in source.</summary>
        internal static readonly IReadOnlyList<string> Needles = new[]
        {
            "BOOTSTRAP" + "_FILE",
            "API" + "_KEY",
            "API" + "_URL",
            "SERVER" + "_ID",
            "api/" + "gameservers/",
        };

        [Test]
        public void NoScannedFileNamesABootstrapOrPlatformCredentialVariable()
        {
            string repo = RepoPaths.RepoRoot;
            var roots = new List<string> { RepoPaths.PackageRoot };
            foreach (string optional in new[] { Path.Combine(repo, "Packages", "io.pingcore.editor"), Path.Combine(repo, "SampleGame", "Assets") })
            {
                if (Directory.Exists(optional))
                {
                    roots.Add(optional);
                }
            }

            List<string> files = roots
                .SelectMany(r => Directory.GetFiles(r, "*", SearchOption.AllDirectories))
                .Where(f => ScannedExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                .ToList();

            string self = files.FirstOrDefault(f => string.Equals(Path.GetFileName(f), "SecretsNameTests.cs", StringComparison.Ordinal));
            Assert.That(self, Is.Not.Null, "the scan must include this file, or it is not scanning the package source");
            Assert.That(files.Count, Is.GreaterThan(10), "the scan found almost nothing; the roots are wrong");

            var hits = new List<string>();
            foreach (string file in files)
            {
                string text = File.ReadAllText(file);
                foreach (string hit in Scan(text))
                {
                    hits.Add($"{RepoPaths.Relative(repo, file)}: {hit}");
                }
            }

            Assert.That(hits, Is.Empty, "Forbidden names found (see the secrets rule):\n" + string.Join("\n", hits));
        }

        [Test]
        public void TheScannerFlagsASyntheticReadOfEachName()
        {
            foreach (string needle in Needles)
            {
                string synthetic = "var value = System.Environment.GetEnvironmentVariable(\"" + needle + "\");";
                Assert.That(Scan(synthetic), Is.EqualTo(new[] { needle }), needle);
            }

            Assert.That(Scan("GET /" + "api/" + "gameservers/" + "{id}/bootstrap"), Has.Some.EqualTo("api/" + "gameservers/"));
            Assert.That(Scan("API" + "_URL" + " and " + "SERVER" + "_ID"), Has.Count.EqualTo(2));
        }

        [Test]
        public void TheScannerIgnoresNamesThatOnlyShareAPrefix()
        {
            Assert.That(Scan("AGONES_SDK_HTTP_PORT serverId apiKey Api_Key"), Is.Empty);
        }

        /// <summary>The needles found in <paramref name="text"/>: env names case-sensitive, the API path case-insensitive.</summary>
        internal static List<string> Scan(string text)
        {
            var found = new List<string>();
            foreach (string needle in Needles)
            {
                StringComparison comparison = needle.Contains("/") ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                if (text.IndexOf(needle, comparison) >= 0)
                {
                    found.Add(needle);
                }
            }

            return found;
        }
    }
}
