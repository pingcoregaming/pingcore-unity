using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace PingCore.Sdk.Tests.Editor
{
    /// <summary>
    /// The runtime has one random source: <c>RandomNumberGenerator</c>, in <c>SecureIds.cs</c>
    /// (ids, the ticket ref and the jitter unit). <c>System.Random</c> is predictable, so an id or a
    /// nonce drawn from it could be guessed; this scans every <c>Runtime/**/*.cs</c>, comments included.
    /// </summary>
    public sealed class RuntimeRandomBanTests
    {
        private static readonly (string Name, Regex Pattern)[] Bans =
        {
            ("System.Random", new Regex(@"\bSystem\s*\.\s*Random\b")),
            ("new Random", new Regex(@"\bnew\s+Random\s*\(")),
            ("RandomNumberGenerator outside SecureIds.cs", new Regex(@"\bRandomNumberGenerator\b")),
        };

        private static string RuntimeRoot => Path.Combine(RepoPaths.PackageRoot, "Runtime");

        [Test]
        public void RuntimeSourceDrawsRandomnessOnlyFromRandomNumberGeneratorInSecureIds()
        {
            List<string> files = Directory.GetFiles(RuntimeRoot, "*.cs", SearchOption.AllDirectories).ToList();
            Assert.That(files.Count, Is.GreaterThan(50), "runtime source not found; the scan root is wrong");
            string secureIds = files.SingleOrDefault(f => Path.GetFileName(f) == "SecureIds.cs");
            Assert.That(secureIds, Is.Not.Null, "SecureIds.cs moved; update this scan");
            Assert.That(File.ReadAllText(secureIds), Does.Contain("RandomNumberGenerator.Create()"), "SecureIds draws from RandomNumberGenerator");

            var hits = new List<string>();
            foreach (string file in files)
            {
                string[] lines = File.ReadAllLines(file);
                for (int i = 0; i < lines.Length; i++)
                {
                    foreach (string ban in Scan(lines[i], Path.GetFileName(file)))
                    {
                        hits.Add($"{RepoPaths.Relative(RepoPaths.PackageRoot, file)}:{i + 1}: {ban}");
                    }
                }
            }

            Assert.That(hits, Is.Empty, "Randomness outside SecureIds:\n" + string.Join("\n", hits));
        }

        [Test]
        public void TheScannerFlagsEachBanAndLeavesNearMissesAlone()
        {
            // Synthetic hits: each must be flagged, or the scan above could pass vacuously.
            Assert.That(Scan("var random = new System.Random();", "Any.cs"), Does.Contain("System.Random"));
            Assert.That(Scan("private readonly Random random = new Random(42);", "Any.cs"), Does.Contain("new Random"));
            Assert.That(Scan("using (var rng = RandomNumberGenerator.Create())", "HeartbeatReporter.cs"), Does.Contain("RandomNumberGenerator outside SecureIds.cs"));
            Assert.That(Scan("/// <see cref=\"System.Random\"/>", "Any.cs"), Does.Contain("System.Random"), "comments are scanned too");

            Assert.That(Scan("using (var rng = RandomNumberGenerator.Create())", "SecureIds.cs"), Is.Empty, "SecureIds is the one sanctioned place");
            Assert.That(Scan("double unit = SecureIds.NextUnit(); var randomUnit = options.RandomUnit; NewRandomId(); renewRandom(1);", "Any.cs"), Is.Empty, "near misses are not flagged");
        }

        private static IEnumerable<string> Scan(string line, string fileName)
        {
            foreach ((string name, Regex pattern) in Bans)
            {
                if (fileName == "SecureIds.cs" && name.StartsWith("RandomNumberGenerator", System.StringComparison.Ordinal))
                {
                    continue;
                }

                if (pattern.IsMatch(line))
                {
                    yield return name;
                }
            }
        }
    }
}
