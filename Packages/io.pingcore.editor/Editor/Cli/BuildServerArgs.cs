using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace PingCore.Editor.Cli
{
    /// <summary>
    /// The command line of <see cref="BuildServer"/>, parsed and validated without any Unity API. Two forms:
    /// <list type="table">
    /// <item><term><c>-buildProfile &lt;Assets/...asset&gt;</c></term><description>The build profile form, for
    /// CI and scripts and the Editor's Ship section: a Linux Dedicated Server Build Profile of the project, whose scenes,
    /// scripting backend and defines are the developer's own. <c>-pingcoreVersion</c> is optional (the caller's
    /// default, <c>yyyy.MM.dd-HHmmss</c> UTC, names the folder), <c>-pingcoreProduct</c> names the executable
    /// (default: the project's product name), and <c>-pingcoreScene</c>, <c>-pingcoreDefine</c> and
    /// <c>-pingcoreOutputRoot</c> are errors: the profile holds the scenes and the defines.</description></item>
    /// <item><term>the scene form</term><description>For scripts that build from a scene list:
    /// <c>-pingcoreVersion &lt;v&gt;</c> required; <c>-pingcoreScene &lt;path&gt;</c> repeatable, in order, an
    /// <c>Assets/</c> or <c>Packages/</c> path ending in <c>.unity</c>, default <see cref="DefaultScene"/>;
    /// <c>-pingcoreProduct &lt;name&gt;</c>, default <see cref="DefaultProduct"/>; <c>-pingcoreDefine &lt;SYMBOL&gt;</c>
    /// repeatable, an extra scripting define for this build only; <c>-pingcoreOutputRoot &lt;folder&gt;</c>, the
    /// project-relative folder the version folder goes in, default <see cref="ServerOutputRoot"/> (an instrumented build
    /// passes its defines and the build guard's instrumented output folder here, <c>BuildGuard.BuildGuardRules</c>). The
    /// backend is Mono.</description></item>
    /// </list>
    /// The version and the product are 1 to 64 letters, digits, <c>.</c>, <c>_</c> or <c>-</c>, starting with a
    /// letter or digit. Flags match case-insensitively. Any other <c>-pingcore*</c> flag is an error, so a typo
    /// never builds the wrong thing; Unity's own arguments are ignored.
    /// </summary>
    public sealed class BuildServerArgs
    {
        public const string VersionFlag = "-pingcoreVersion";
        public const string SceneFlag = "-pingcoreScene";
        public const string ProductFlag = "-pingcoreProduct";
        public const string DefineFlag = "-pingcoreDefine";
        public const string OutputRootFlag = "-pingcoreOutputRoot";
        public const string BuildProfileFlag = "-buildProfile";

        public const string DefaultScene = "Assets/Game/Scenes/Server.unity";
        public const string DefaultProduct = "BeaconRushServer";
        public const string ExecutableExtension = ".x86_64";
        public const string VersionFileName = "version.txt";

        /// <summary>Builds are written under this project folder, one subfolder per version, unless <c>-pingcoreOutputRoot</c> names another.</summary>
        public const string ServerOutputRoot = "Builds/Server";

        private const string FlagPrefix = "-pingcore";
        private static readonly Regex NamePattern = new Regex("^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$", RegexOptions.CultureInvariant);
        private static readonly Regex DefinePattern = new Regex("^[A-Za-z_][A-Za-z0-9_]{0,63}$", RegexOptions.CultureInvariant);
        private static readonly Regex FolderSegmentPattern = new Regex("^[A-Za-z0-9_][A-Za-z0-9._-]{0,63}$", RegexOptions.CultureInvariant);

        private BuildServerArgs(IReadOnlyList<string> scenes, string product, string version, IReadOnlyList<string> defines, string outputRoot,
            string buildProfile, IReadOnlyList<string> errors)
        {
            Scenes = scenes;
            Product = product;
            Version = version;
            Defines = defines;
            OutputRoot = outputRoot ?? ServerOutputRoot;
            BuildProfile = buildProfile;
            Errors = errors;
        }

        /// <summary>The scene form's scenes; empty in the build profile form.</summary>
        public IReadOnlyList<string> Scenes { get; }

        /// <summary>The executable's name without <c>.x86_64</c>; null in the build profile form when not given (the project's product name).</summary>
        public string Product { get; }

        public string Version { get; }

        /// <summary>The scene form's <c>-pingcoreDefine</c> symbols, in order; empty in the build profile form.</summary>
        public IReadOnlyList<string> Defines { get; }

        /// <summary>The project-relative folder the version folder goes in: <c>-pingcoreOutputRoot</c>, else <see cref="ServerOutputRoot"/>.</summary>
        public string OutputRoot { get; }

        /// <summary>The build profile's asset path, or null for the scene form.</summary>
        public string BuildProfile { get; }

        /// <summary>True for the build profile form.</summary>
        public bool UsesBuildProfile => BuildProfile != null;

        /// <summary>Every problem found; empty when the arguments are usable.</summary>
        public IReadOnlyList<string> Errors { get; }

        public bool IsValid => Errors.Count == 0;

        /// <summary>The project-relative output folder: <c>&lt;output root&gt;/&lt;v&gt;</c>, by default <c>Builds/Server/&lt;v&gt;</c>.</summary>
        public string OutputFolder => OutputRoot + "/" + Version;

        /// <summary>The extra scripting defines of the build (<c>BuildPlayerOptions.extraScriptingDefines</c>): the <c>-pingcoreDefine</c> symbols.</summary>
        public string[] ExtraDefines => new List<string>(Defines).ToArray();

        /// <summary>The scene form, with no default version: <c>-pingcoreVersion</c> is required.</summary>
        public static BuildServerArgs Parse(IReadOnlyList<string> args) => Parse(args, null);

        /// <param name="args">The command line.</param>
        /// <param name="defaultVersion">The version the build profile form takes when <c>-pingcoreVersion</c> is absent; the scene form never uses it.</param>
        public static BuildServerArgs Parse(IReadOnlyList<string> args, string defaultVersion)
        {
            var errors = new List<string>();
            var scenes = new List<string>();
            string product = null;
            string version = null;
            string profile = null;
            string outputRoot = null;
            var defines = new List<string>();

            for (int i = 0; args != null && i < args.Count; i++)
            {
                string arg = args[i] ?? string.Empty;
                bool isProfile = Is(arg, BuildProfileFlag);
                if (!isProfile && !arg.StartsWith(FlagPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                bool isScene = Is(arg, SceneFlag);
                bool isProduct = Is(arg, ProductFlag);
                bool isVersion = Is(arg, VersionFlag);
                bool isDefine = Is(arg, DefineFlag);
                bool isOutputRoot = Is(arg, OutputRootFlag);
                if (!isScene && !isProduct && !isVersion && !isProfile && !isDefine && !isOutputRoot)
                {
                    errors.Add("unknown flag " + arg + "; BuildServer takes " + BuildProfileFlag + ", " + VersionFlag + ", " + SceneFlag + ", " + ProductFlag
                        + ", " + DefineFlag + " and " + OutputRootFlag);
                    continue;
                }

                if (i + 1 >= args.Count || string.IsNullOrEmpty(args[i + 1]) || args[i + 1].StartsWith("-", StringComparison.Ordinal))
                {
                    errors.Add(arg + " needs a value");
                    continue;
                }

                string value = args[++i];
                if (isScene)
                {
                    scenes.Add(value);
                }
                else if (isDefine)
                {
                    if (!DefinePattern.IsMatch(value))
                    {
                        errors.Add(DefineFlag + " " + value + " must be a scripting define: a letter or '_', then letters, digits or '_', at most 64");
                    }
                    else if (defines.Contains(value))
                    {
                        errors.Add(DefineFlag + " " + value + " is given twice");
                    }
                    else
                    {
                        defines.Add(value);
                    }
                }
                else if (isOutputRoot)
                {
                    if (outputRoot != null)
                    {
                        errors.Add(OutputRootFlag + " is given twice");
                    }

                    outputRoot = value;
                    string rootProblem = CheckOutputRoot(value);
                    if (rootProblem != null)
                    {
                        errors.Add(rootProblem);
                    }
                }
                else if (isProfile)
                {
                    if (profile != null)
                    {
                        errors.Add(BuildProfileFlag + " is given twice");
                    }

                    profile = value;
                }
                else if (isProduct)
                {
                    if (product != null)
                    {
                        errors.Add(ProductFlag + " is given twice");
                    }

                    product = value;
                }
                else
                {
                    if (version != null)
                    {
                        errors.Add(VersionFlag + " is given twice");
                    }

                    version = value;
                }
            }

            if (profile != null)
            {
                string profileProblem = CheckBuildProfilePath(profile);
                if (profileProblem != null)
                {
                    errors.Add(profileProblem);
                }

                if (scenes.Count > 0)
                {
                    errors.Add(SceneFlag + " cannot be used with " + BuildProfileFlag + ": the build profile holds the scenes");
                }

                if (defines.Count > 0)
                {
                    errors.Add(DefineFlag + " cannot be used with " + BuildProfileFlag + ": the build profile holds the defines");
                }

                if (outputRoot != null)
                {
                    errors.Add(OutputRootFlag + " cannot be used with " + BuildProfileFlag + ": a build profile build writes under " + ServerOutputRoot);
                }

                version = version ?? defaultVersion;
            }

            if (version == null)
            {
                errors.Add(VersionFlag + " is required (for example 2026.10.02-abc1234)");
            }
            else if (!NamePattern.IsMatch(version))
            {
                errors.Add(VersionFlag + " must be 1 to 64 letters, digits, '.', '_' or '-', starting with a letter or digit");
            }

            if (product == null)
            {
                product = profile == null ? DefaultProduct : null;
            }
            else if (!NamePattern.IsMatch(product))
            {
                errors.Add(ProductFlag + " must be 1 to 64 letters, digits, '.', '_' or '-', starting with a letter or digit");
            }

            if (profile == null)
            {
                if (scenes.Count == 0)
                {
                    scenes.Add(DefaultScene);
                }

                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string scene in scenes)
                {
                    string problem = CheckScene(scene);
                    if (problem != null)
                    {
                        errors.Add(problem);
                    }
                    else if (!seen.Add(scene))
                    {
                        errors.Add(SceneFlag + " " + scene + " is given twice");
                    }
                }
            }

            return new BuildServerArgs(scenes.ToArray(), product, version, defines.ToArray(), profile == null ? outputRoot : null, profile, errors.ToArray());
        }

        /// <summary>True when <paramref name="name"/> follows the version and product rule.</summary>
        public static bool IsValidName(string name) => name != null && NamePattern.IsMatch(name);

        /// <summary>Why <paramref name="path"/> is not a build profile asset path (<c>Assets/...asset</c>, forward slashes, no <c>.</c> or <c>..</c> segment), or null. Pure.</summary>
        public static string CheckBuildProfilePath(string path)
        {
            string[] segments = (path ?? string.Empty).Split('/');
            bool climbs = path == null || path.Contains("\\") || Array.IndexOf(segments, "..") >= 0 || Array.IndexOf(segments, ".") >= 0 || Array.IndexOf(segments, string.Empty) >= 0;
            if (path == null || !path.StartsWith("Assets/", StringComparison.Ordinal) || climbs || !path.EndsWith(".asset", StringComparison.Ordinal) || path.EndsWith("/.asset", StringComparison.Ordinal))
            {
                return BuildProfileFlag + " " + path + " must be a project path under Assets/ ending in .asset, with forward slashes and no . or .. segment";
            }

            return null;
        }

        /// <summary>
        /// Why <paramref name="folder"/> cannot be an output root (a project-relative folder under <c>Builds/</c>, forward slashes,
        /// segments of letters, digits, <c>.</c>, <c>_</c> or <c>-</c>, no <c>.</c> or <c>..</c> segment), or null. Pure.
        /// </summary>
        public static string CheckOutputRoot(string folder)
        {
            string[] segments = (folder ?? string.Empty).Split('/');
            bool ok = folder != null && segments.Length >= 2 && segments[0] == "Builds";
            foreach (string segment in segments)
            {
                ok &= segment != "." && segment != ".." && FolderSegmentPattern.IsMatch(segment);
            }

            return ok ? null : OutputRootFlag + " " + folder + " must be a folder under Builds/ with forward slashes, letters, digits, '.', '_' or '-' in each part, and no . or .. part";
        }

        private static bool Is(string arg, string flag) => string.Equals(arg, flag, StringComparison.OrdinalIgnoreCase);

        private static string CheckScene(string scene)
        {
            bool rooted = scene.StartsWith("Assets/", StringComparison.Ordinal) || scene.StartsWith("Packages/", StringComparison.Ordinal);
            bool climbs = scene.Contains("\\") || Array.IndexOf(scene.Split('/'), "..") >= 0 || Array.IndexOf(scene.Split('/'), ".") >= 0;
            if (!rooted || climbs || !scene.EndsWith(".unity", StringComparison.Ordinal) || scene.EndsWith("/.unity", StringComparison.Ordinal))
            {
                return SceneFlag + " " + scene + " must be a project path under Assets/ or Packages/ ending in .unity, with forward slashes and no . or .. segment";
            }

            return null;
        }
    }
}
