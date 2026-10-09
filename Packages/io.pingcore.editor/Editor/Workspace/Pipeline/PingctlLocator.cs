using System;
using System.IO;

namespace PingCore.Editor.Workspace.Pipeline
{
    /// <summary>Where pingctl was found, or why none may run.</summary>
    public sealed class PingctlLocation
    {
        public PingctlLocation(string path, string source, string problem, string expectedSha256 = null)
        {
            Path = path;
            Source = source;
            Problem = problem;
            ExpectedSha256 = expectedSha256;
        }

        /// <summary>The executable, or null.</summary>
        public string Path { get; }

        /// <summary>How it was found: <see cref="PingctlLocator.SourceOverride"/>, <c>PINGCTL_BIN</c> or <see cref="PingctlLocator.SourceBundled"/>; null when not found.</summary>
        public string Source { get; }

        /// <summary>
        /// The manifest's SHA-256 for the bundled binary (the push checks its private run copy against it,
        /// <see cref="PingctlRunCopy"/>); null for a developer's own pingctl, which runs unchecked.
        /// </summary>
        public string ExpectedSha256 { get; }

        /// <summary>Why none is usable, or null.</summary>
        public string Problem { get; }

        public bool Found => Path != null;
    }

    /// <summary>What the locator reads, passed in so the order is testable without a file system.</summary>
    public sealed class PingctlLocatorInputs
    {
        /// <summary>The developer's own pingctl from <c>UserSettings/PingCoreEditorUser.json</c>, or null.</summary>
        public string ConfiguredPath { get; set; }

        /// <summary>The <c>PINGCTL_BIN</c> variable, or null.</summary>
        public string BinVariable { get; set; }

        /// <summary>The bundle folder (<see cref="PingctlBundle.Here"/>), or null when the package carries none.</summary>
        public string BundleFolder { get; set; }

        /// <summary>This Editor's bundle platform (<see cref="PingctlBundle.PlatformHere"/>), or null.</summary>
        public string Platform { get; set; }

        public Func<string, bool> FileExists { get; set; } = File.Exists;

        public Func<string, bool> DirectoryExists { get; set; } = Directory.Exists;

        public Func<string, string> ReadText { get; set; } = File.ReadAllText;

        /// <summary>The SHA-256 of a file, lower-case hex (<see cref="PingctlBundle.Sha256Of"/>).</summary>
        public Func<string, string> Sha256Of { get; set; } = PingctlBundle.Sha256Of;
    }

    /// <summary>
    /// Finds the pingctl a push runs, in this order:
    /// <list type="number">
    /// <item>the developer's own: the path set under Ship's "Your own pingctl", then <c>PINGCTL_BIN</c>; a configured
    /// path that does not exist is reported, never skipped for another binary;</item>
    /// <item>the bundled binary for this Editor's platform, checked against the pinned manifest's SHA-256 before every
    /// use; a mismatch, a missing binary the manifest lists, or a manifest that cannot be read is refused, never
    /// skipped for another binary.</item>
    /// </list>
    /// Nothing else: a platform the package carries no binary for (Windows on Arm, Linux on Arm) gets a sentence saying
    /// to set your own pingctl, never a <c>pingctl</c> picked up from <c>PATH</c>, which nobody checked and which would be
    /// handed the push token. Pure over <see cref="PingctlLocatorInputs"/>; <see cref="LocateHere"/> fills them from this
    /// machine.
    /// </summary>
    public static class PingctlLocator
    {
        /// <summary>The source of a path set under Ship.</summary>
        public const string SourceOverride = "your own pingctl";

        /// <summary>The source of the package's binary.</summary>
        public const string SourceBundled = "bundled";

        /// <summary>The hint when no pingctl may run.</summary>
        public const string GetPingctlHint = "Reinstall the PingCore Editor package (it carries pingctl), or set your own pingctl under Ship (or PINGCTL_BIN).";

        public static PingctlLocation Locate(PingctlLocatorInputs inputs)
        {
            if (inputs == null)
            {
                throw new ArgumentNullException(nameof(inputs));
            }

            if (!string.IsNullOrWhiteSpace(inputs.ConfiguredPath))
            {
                string p = inputs.ConfiguredPath.Trim();
                return inputs.FileExists(p)
                    ? new PingctlLocation(p, SourceOverride, null)
                    : new PingctlLocation(null, null, $"Your own pingctl, set under Ship, does not exist ({p}).");
            }

            if (!string.IsNullOrWhiteSpace(inputs.BinVariable))
            {
                string p = inputs.BinVariable.Trim();
                return inputs.FileExists(p)
                    ? new PingctlLocation(p, PingctlCommand.BinVariable, null)
                    : new PingctlLocation(null, null, $"{PingctlCommand.BinVariable} names a file that does not exist ({p}).");
            }

            PingctlLocation bundled = Bundled(inputs);
            if (bundled != null)
            {
                return bundled;
            }

            string platform = string.IsNullOrEmpty(inputs.Platform) ? "this platform" : inputs.Platform;
            return new PingctlLocation(null, null, $"The PingCore Editor package carries no pingctl for {platform}. Set your own pingctl under Ship (or {PingctlCommand.BinVariable}); the plugin never runs a pingctl it finds on PATH.");
        }

        /// <summary>The locator against this machine: the user setting, the process environment, the installed package and the file system.</summary>
        public static PingctlLocation LocateHere(string configuredPath)
        {
            return Locate(new PingctlLocatorInputs
            {
                ConfiguredPath = configuredPath,
                BinVariable = Environment.GetEnvironmentVariable(PingctlCommand.BinVariable),
                BundleFolder = PingctlBundle.Here(),
                Platform = PingctlBundle.PlatformHere(),
            });
        }

        // The bundle's binary, a refusal, or null when the package carries no bundle for this platform.
        private static PingctlLocation Bundled(PingctlLocatorInputs inputs)
        {
            if (string.IsNullOrEmpty(inputs.BundleFolder) || string.IsNullOrEmpty(inputs.Platform) || !inputs.DirectoryExists(inputs.BundleFolder))
            {
                return null;
            }

            string manifestPath = System.IO.Path.Combine(inputs.BundleFolder, PingctlBundle.ManifestName);
            if (!inputs.FileExists(manifestPath))
            {
                return new PingctlLocation(null, null, $"The bundled pingctl has no {PingctlBundle.ManifestName}, so it cannot be checked and is not run.");
            }

            string text;
            try
            {
                text = inputs.ReadText(manifestPath);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                return new PingctlLocation(null, null, $"The bundled pingctl's {PingctlBundle.ManifestName} could not be read ({e.GetType().Name}).");
            }

            PingctlBundleManifest manifest = PingctlBundle.Parse(text, out string problem);
            if (manifest == null)
            {
                return new PingctlLocation(null, null, Sentence(problem));
            }

            if (!manifest.Binaries.TryGetValue(inputs.Platform, out PingctlBundleEntry entry))
            {
                return null;
            }

            string binary = System.IO.Path.Combine(inputs.BundleFolder, entry.Platform, entry.File);
            if (!inputs.FileExists(binary))
            {
                return new PingctlLocation(null, null, $"The bundled pingctl for {entry.Platform} is missing from the package ({entry.Platform}/{entry.File}).");
            }

            string actual;
            try
            {
                actual = inputs.Sha256Of(binary);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                return new PingctlLocation(null, null, $"The bundled pingctl for {entry.Platform} could not be read to check it ({e.GetType().Name}).");
            }

            string mismatch = PingctlBundle.DigestProblem(entry, actual);
            return mismatch != null
                ? new PingctlLocation(null, null, Sentence(mismatch))
                : new PingctlLocation(binary, SourceBundled + " " + manifest.Version, null, entry.Sha256);
        }

        private static string Sentence(string text) => string.IsNullOrEmpty(text) ? text : char.ToUpperInvariant(text[0]) + text.Substring(1) + (text.EndsWith(".", StringComparison.Ordinal) ? string.Empty : ".");
    }
}
