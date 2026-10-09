using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using PingCore.Core;
using PingCore.Editor.Workspace.Api;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace PingCore.Editor.Workspace.Tests
{
    /// <summary>
    /// The repository paths the Workspace tests read, found from this package's resolved path, and
    /// the API contract fixtures (not published) as typed values for
    /// fakes: <c>WorkspaceFixtures.Payload&lt;FleetListResponse&gt;("fleets.list.json")</c>.
    /// </summary>
    public static class WorkspaceFixtures
    {
        /// <summary><c>Packages/io.pingcore.editor</c>.</summary>
        public static string PackageRoot
        {
            get
            {
                PackageInfo info = PackageInfo.FindForAssembly(typeof(IPingCoreApi).Assembly);
                if (info == null || string.IsNullOrEmpty(info.resolvedPath))
                {
                    Assert.Fail("io.pingcore.editor is not resolved as a package; run the tests from a project that lists it in Packages/manifest.json.");
                }

                return Path.GetFullPath(info.resolvedPath);
            }
        }

        /// <summary>The repository root, two levels above the package.</summary>
        public static string RepoRoot => Path.GetFullPath(Path.Combine(PackageRoot, "..", ".."));

        /// <summary>
        /// The API contract snapshots (not published). Where they are absent (the published repository and a
        /// copied package carry none) every test that needs them is ignored with the reason, never failed.
        /// </summary>
        public static string PingCoreApiContracts
        {
            get
            {
                string dir = Path.Combine(RepoRoot, "contracts", "pingcore-api");
                if (!Directory.Exists(dir))
                {
                    Assert.Ignore("The API contract snapshots are not published with this package, so this test has nothing to compare against here.");
                }

                return dir;
            }
        }

        /// <summary>The snapshots' fixtures folder.</summary>
        public static string FixturesDir => Path.Combine(PingCoreApiContracts, "fixtures");

        /// <summary>Every fixture file, sorted.</summary>
        public static IReadOnlyList<string> FixtureFiles()
        {
            return Directory.Exists(FixturesDir)
                ? Directory.GetFiles(FixturesDir, "*.json").OrderBy(f => f, StringComparer.Ordinal).ToList()
                : new List<string>();
        }

        /// <summary>Parses a fixture file without date or float coercion.</summary>
        public static JObject Load(string fileName)
        {
            string path = Path.Combine(FixturesDir, fileName);
            Assert.That(File.Exists(path), Is.True, $"fixture {fileName} is missing");
            return Parse(File.ReadAllText(path));
        }

        /// <summary>A fixture's payload as <typeparamref name="T"/>, with the runtime settings.</summary>
        public static T Payload<T>(string fileName)
        {
            return Load(fileName)["payload"].ToObject<T>(JsonSerializer.Create(PingCoreJson.Settings));
        }

        /// <summary>The whole API envelope the endpoint would send for a fixture's payload.</summary>
        public static string Envelope(string fileName, string message = "OK.")
        {
            return new JObject { ["error"] = false, ["message"] = message, ["data"] = Load(fileName)["payload"] }.ToString(Formatting.None);
        }

        /// <summary>Parses JSON the way the fixture tests do.</summary>
        public static JObject Parse(string json)
        {
            using (var reader = new JsonTextReader(new StringReader(json)) { DateParseHandling = DateParseHandling.None, FloatParseHandling = FloatParseHandling.Double })
            {
                return JObject.Load(reader);
            }
        }
    }
}
