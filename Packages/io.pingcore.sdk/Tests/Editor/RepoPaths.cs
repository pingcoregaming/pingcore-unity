using System;
using System.IO;
using NUnit.Framework;
using PingCore.Core;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace PingCore.Sdk.Tests.Editor
{
    /// <summary>
    /// Locates the repository from the resolved path of this package, so the tests work from
    /// <c>SampleGame</c> and from any scratch project that references the package by
    /// <c>file:</c> path. A missing directory fails the calling test; nothing is skipped.
    /// </summary>
    internal static class RepoPaths
    {
        /// <summary>Overrides the fixture root for a local run; leave it unset to replay this repository's <c>contracts/</c>.</summary>
        public const string FixturesOverrideVariable = "PINGCORE_FIXTURES_DIR";

        /// <summary>The package root, <c>Packages/io.pingcore.sdk</c>.</summary>
        public static string PackageRoot
        {
            get
            {
                PackageInfo info = PackageInfo.FindForAssembly(typeof(PingCoreSdkInfo).Assembly);
                if (info == null || string.IsNullOrEmpty(info.resolvedPath))
                {
                    Assert.Fail("io.pingcore.sdk is not resolved as a package; the tests must run from a project that lists it in Packages/manifest.json.");
                }

                return Path.GetFullPath(info.resolvedPath);
            }
        }

        /// <summary>The repository root: two levels above the package root.</summary>
        public static string RepoRoot => Path.GetFullPath(Path.Combine(PackageRoot, "..", ".."));

        /// <summary>The repository's <c>contracts/</c> directory. Fails when it is absent.</summary>
        public static string ContractsRoot
        {
            get
            {
                string contracts = Path.Combine(RepoRoot, "contracts");
                if (!Directory.Exists(contracts))
                {
                    Assert.Fail($"contracts/ not found at {contracts}. The contract tests need the repository checkout, not a copied package.");
                }

                return contracts;
            }
        }

        /// <summary>
        /// The directory searched for <c>fixtures/*.json</c>: <see cref="FixturesOverrideVariable"/>
        /// when set, otherwise <see cref="ContractsRoot"/>. Returns null with a reason when the
        /// directory does not exist, so a test-case source can report it as a failing case.
        /// </summary>
        public static string FixtureSearchRoot(out string problem)
        {
            problem = null;
            string overrideDir = Environment.GetEnvironmentVariable(FixturesOverrideVariable);
            if (!string.IsNullOrEmpty(overrideDir))
            {
                if (!Directory.Exists(overrideDir))
                {
                    problem = $"{FixturesOverrideVariable} points at {overrideDir}, which does not exist.";
                    return null;
                }

                return Path.GetFullPath(overrideDir);
            }

            PackageInfo info = PackageInfo.FindForAssembly(typeof(PingCoreSdkInfo).Assembly);
            if (info == null || string.IsNullOrEmpty(info.resolvedPath))
            {
                problem = "io.pingcore.sdk is not resolved as a package.";
                return null;
            }

            string contracts = Path.GetFullPath(Path.Combine(info.resolvedPath, "..", "..", "contracts"));
            if (!Directory.Exists(contracts))
            {
                problem = $"contracts/ not found at {contracts}.";
                return null;
            }

            return contracts;
        }

        /// <summary>
        /// True for a fixture of the SDK's contracts: a <c>.json</c> file directly in a
        /// <c>fixtures/</c> folder, except the API contract snapshots' fixtures (not published), whose DTOs
        /// live in the Editor plugin's <c>PingCore.Editor.Workspace</c> assembly and are replayed by its
        /// own tests.
        /// </summary>
        public static bool IsSdkFixture(string file)
        {
            string folder = Path.GetDirectoryName(file);
            return string.Equals(Path.GetFileName(folder), "fixtures", StringComparison.Ordinal)
                && !string.Equals(Path.GetFileName(Path.GetDirectoryName(folder)), "pingcore-api", StringComparison.Ordinal);
        }

        /// <summary>A path relative to <paramref name="root"/>, with forward slashes, for messages.</summary>
        public static string Relative(string root, string path)
        {
            string relative = path.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                ? path.Substring(root.Length).TrimStart('\\', '/')
                : path;
            return relative.Replace('\\', '/');
        }
    }
}
